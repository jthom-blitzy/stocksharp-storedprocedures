namespace StockSharp.Algo.Storages.Sql;

using System.Data;

using Microsoft.Data.SqlClient;

using StockSharp.Algo.Risk;

/// <summary>
/// ADO.NET gateway onto the StockSharpLegacy database (schema lives under /Database at the repo
/// root). Deliberately raw <see cref="SqlCommand"/> calls, not an ORM. Following the SQL -&gt; C#
/// risk-logic consolidation this is a <i>pure data-access adapter</i>: it performs only plain
/// parameterized INSERT/SELECT statements and delegates every risk decision and position
/// calculation to the C# services in the <see cref="StockSharp.Algo.Risk"/> namespace -
/// <see cref="PreTradeRiskService"/> for the seven-check pre-trade gate and
/// <see cref="PositionRecalculationService"/> for average-cost / realized-P&amp;L. The database no
/// longer holds any stored-procedure business logic, so there are no CommandType.StoredProcedure
/// decisioning calls here any more (the old EXEC dbo.usp_SubmitOrder path is gone).
///
/// This is an adapter that sits <i>alongside</i> <see cref="IEntityRegistry"/>,
/// not a replacement for it: Securities/Exchanges/subscriptions are still
/// served by <see cref="Csv.CsvEntityRegistry"/>. Only orders, trades and
/// positions have been moved to SQL Server so far. That split - some entities
/// in SQL, some still in CSV, nothing keeping their identifiers in sync - is
/// the same half-migrated state described in LEGACY_LAYER.md.
/// </summary>
public class SqlLegacyOrderGateway
{
	private readonly string _connectionString;
	private readonly PreTradeRiskService _preTradeRiskService;
	private readonly PositionRecalculationService _positionRecalculationService;

	/// <summary>
	/// Initializes a new instance of the <see cref="SqlLegacyOrderGateway"/>.
	/// </summary>
	/// <param name="connectionString">SQL Server connection string for the StockSharpLegacy database.</param>
	public SqlLegacyOrderGateway(string connectionString)
	{
		if (connectionString.IsEmpty())
			throw new ArgumentNullException(nameof(connectionString));

		_connectionString = connectionString;

		// The gateway owns the two risk services that replaced the SQL stored procedures. They are
		// constructed from the same connection string; the gateway passes them its own open connection
		// and transaction (see SubmitOrderAsync / RecordTradeAsync) so validation, calculation and the
		// INSERT it guards all run inside a single atomic unit.
		_preTradeRiskService = new PreTradeRiskService(connectionString);
		_positionRecalculationService = new PositionRecalculationService(connectionString);
	}

	private SqlConnection CreateConnection() => new(_connectionString);

	/// <summary>
	/// Finds the SQL-side portfolio_id for a <see cref="Portfolio"/>, creating the row
	/// if it doesn't exist yet. Matched by name - there is no shared surrogate key
	/// between <see cref="Portfolio"/> and dbo.Portfolios.
	/// </summary>
	public async Task<int> EnsurePortfolioAsync(Portfolio portfolio, CancellationToken cancellationToken = default)
	{
		if (portfolio is null)
			throw new ArgumentNullException(nameof(portfolio));

		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using (var select = new SqlCommand("SELECT portfolio_id FROM dbo.Portfolios WHERE name = @name", connection))
		{
			select.Parameters.AddWithValue("@name", portfolio.Name);

			if (await select.ExecuteScalarAsync(cancellationToken) is int existingId)
				return existingId;
		}

		// currency isn't modeled on BusinessEntities.Portfolio the way it is on
		// dbo.Portfolios, so newly auto-created rows always land on the column
		// default ('USD') regardless of what the security/portfolio actually trades in
		await using var insert = new SqlCommand(
			"INSERT INTO dbo.Portfolios (name) OUTPUT INSERTED.portfolio_id VALUES (@name)", connection);
		insert.Parameters.AddWithValue("@name", portfolio.Name);

		return (int)await insert.ExecuteScalarAsync(cancellationToken);
	}

	/// <summary>
	/// Finds the SQL-side security_id for a <see cref="Security"/>, creating the row
	/// if it doesn't exist yet. Matched by Code + Board.Code.
	/// </summary>
	public async Task<int> EnsureSecurityAsync(Security security, CancellationToken cancellationToken = default)
	{
		if (security is null)
			throw new ArgumentNullException(nameof(security));

		var boardCode = security.Board?.Code;

		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using (var select = new SqlCommand(
			"""
			SELECT security_id FROM dbo.Securities
			WHERE security_code = @code AND (board_code = @board OR (@board IS NULL AND board_code IS NULL))
			""", connection))
		{
			select.Parameters.AddWithValue("@code", security.Code);
			select.Parameters.AddWithValue("@board", (object)boardCode ?? DBNull.Value);

			if (await select.ExecuteScalarAsync(cancellationToken) is int existingId)
				return existingId;
		}

		await using var insert = new SqlCommand(
			"INSERT INTO dbo.Securities (security_code, board_code, security_type) OUTPUT INSERTED.security_id VALUES (@code, @board, @type)", connection);
		insert.Parameters.AddWithValue("@code", security.Code);
		insert.Parameters.AddWithValue("@board", (object)boardCode ?? DBNull.Value);
		insert.Parameters.AddWithValue("@type", (object)security.Type?.ToString() ?? DBNull.Value);

		return (int)await insert.ExecuteScalarAsync(cancellationToken);
	}

	/// <summary>
	/// Submits an order: validates it against the seven pre-trade checks in
	/// <see cref="PreTradeRiskService"/> and then records the row in dbo.Orders (ACCEPTED, or REJECTED
	/// with a reject_reason - rejected orders are still recorded, not dropped, so there is an audit trail
	/// of what was blocked and why). Validation and the INSERT execute inside ONE transaction guarded by a
	/// per-portfolio application lock, so two concurrent submissions cannot both read stale state and both
	/// slip past a shared limit (closes the check-then-act race, review finding C03). This replaces the old
	/// EXEC dbo.usp_SubmitOrder / usp_ValidatePreTradeRisk path.
	/// </summary>
	/// <param name="portfolioId">The SQL-side portfolio identifier.</param>
	/// <param name="securityId">The SQL-side security identifier.</param>
	/// <param name="side">The order side.</param>
	/// <param name="volume">The order quantity.</param>
	/// <param name="price">The order price, or null for a market order.</param>
	/// <param name="orderType">The order type (LIMIT or MARKET only).</param>
	/// <param name="externalTransactionId">Optional external transaction identifier stored on the row.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The submission outcome: the new order_id, whether it was accepted, and any reject reason.</returns>
	public async Task<SqlOrderSubmitResult> SubmitOrderAsync(
		int portfolioId, int securityId, Sides side, decimal volume, decimal? price, OrderTypes orderType,
		long? externalTransactionId = null, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);
		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

		// Serialize concurrent submissions for this portfolio so the validate-then-insert sequence is a
		// single atomic check-then-act. Portfolio scope (not portfolio+security) is deliberate: the
		// frequency and commission checks are portfolio-wide, so a narrower lock would leave those races
		// open. The lock is held for the life of the transaction and released on commit/rollback.
		await AcquireAppLockAsync(connection, transaction, $"StockSharpLegacy:Order:Portfolio:{portfolioId}", cancellationToken);

		var evaluation = await _preTradeRiskService.ValidateAsync(
			connection, transaction, portfolioId, securityId, side, volume, price, orderType, cancellationToken);

		var status = evaluation.IsValid ? "ACCEPTED" : "REJECTED";

		long orderId;

		// Plain parameterized INSERT - no decisioning here. Rejected orders are recorded too (with the
		// reason). Note dbo.Orders.CK_Orders_qty enforces qty > 0, so an "Invalid qty" rejection is not
		// storable and surfaces as a constraint error, exactly as the old usp_SubmitOrder INSERT did.
		await using (var insert = new SqlCommand(
			"""
			INSERT INTO dbo.Orders (portfolio_id, security_id, side, qty, price, order_type, status, reject_reason, external_transaction_id)
			OUTPUT INSERTED.order_id
			VALUES (@portfolio_id, @security_id, @side, @qty, @price, @order_type, @status, @reject_reason, @external_transaction_id)
			""", connection, transaction))
		{
			insert.Parameters.Add(new SqlParameter("@portfolio_id", SqlDbType.Int) { Value = portfolioId });
			insert.Parameters.Add(new SqlParameter("@security_id", SqlDbType.Int) { Value = securityId });
			insert.Parameters.Add(new SqlParameter("@side", SqlDbType.Char, 1) { Value = side == Sides.Buy ? "B" : "S" });
			insert.Parameters.Add(new SqlParameter("@qty", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = volume });
			insert.Parameters.Add(new SqlParameter("@price", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = (object)price ?? DBNull.Value });
			insert.Parameters.Add(new SqlParameter("@order_type", SqlDbType.VarChar, 10) { Value = MapOrderType(orderType) });
			insert.Parameters.Add(new SqlParameter("@status", SqlDbType.VarChar, 10) { Value = status });
			insert.Parameters.Add(new SqlParameter("@reject_reason", SqlDbType.NVarChar, 200) { Value = (object)evaluation.RejectReason ?? DBNull.Value });
			insert.Parameters.Add(new SqlParameter("@external_transaction_id", SqlDbType.BigInt) { Value = (object)externalTransactionId ?? DBNull.Value });

			orderId = (long)await insert.ExecuteScalarAsync(cancellationToken);
		}

		await transaction.CommitAsync(cancellationToken);

		return new()
		{
			OrderId = orderId,
			IsValid = evaluation.IsValid,
			RejectReason = evaluation.RejectReason,
		};
	}

	// Serializes a unit of work on a named resource using sp_getapplock in Transaction-owner mode.
	// Combined with the enclosing transaction this turns a read-then-write sequence into a single atomic
	// critical section (C03/CWE-367): a second caller contending for the same resource waits here until
	// the first commits, so it can no longer act on pre-write state. The lock is released automatically
	// when the transaction ends. Two callers use this: SubmitOrderAsync serializes per portfolio (its
	// frequency/commission checks are portfolio-wide), and RecordTradeAsync serializes per position
	// (portfolio+security) and acquires it BEFORE inserting the trade so concurrent fills cannot form the
	// insert-then-lock deadlock cycle (QA finding F1). Distinct resource names mean the two paths do not
	// block each other.
	private static async Task AcquireAppLockAsync(SqlConnection connection, SqlTransaction transaction, string resource, CancellationToken cancellationToken)
	{
		await using var command = new SqlCommand("sys.sp_getapplock", connection, transaction)
		{
			CommandType = CommandType.StoredProcedure,
		};
		command.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = resource });
		command.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = "Exclusive" });
		command.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Transaction" });
		command.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = 15000 });

		var returnValue = new SqlParameter { Direction = ParameterDirection.ReturnValue };
		command.Parameters.Add(returnValue);

		await command.ExecuteNonQueryAsync(cancellationToken);

		// sp_getapplock returns >= 0 on success (0 granted immediately, 1 granted after waiting) and < 0
		// on failure (-1 timeout, -2 cancelled, -3 deadlock victim, -999 validation error).
		if (returnValue.Value is int returnCode && returnCode < 0)
			throw new InvalidOperationException($"Could not acquire the application lock '{resource}' (sp_getapplock returned {returnCode}).");
	}

	// Resolves the portfolio/security scope of the position a fill belongs to, from its order. The order
	// row was written by a previously-committed SubmitOrderAsync, so this is a plain committed read; it is
	// used to key the per-position application lock BEFORE any row is modified (QA finding F1).
	private static async Task<(int PortfolioId, int SecurityId)> ResolveOrderScopeAsync(
		SqlConnection connection, SqlTransaction transaction, long orderId, CancellationToken cancellationToken)
	{
		await using var command = new SqlCommand(
			"SELECT portfolio_id, security_id FROM dbo.Orders WHERE order_id = @order_id", connection, transaction);
		command.Parameters.Add(new SqlParameter("@order_id", SqlDbType.BigInt) { Value = orderId });

		await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

		if (!await reader.ReadAsync(cancellationToken))
			throw new InvalidOperationException($"SqlLegacyOrderGateway: order_id {orderId} not found");

		return (reader.GetInt32(0), reader.GetInt32(1));
	}

	// A SQL Server deadlock victim surfaces as SqlException with error number 1205.
	private static bool IsDeadlockVictim(SqlException exception) => exception.Number == 1205;

	/// <summary>
	/// Records a fill against a SQL-side order and recomputes the affected position. The dbo.Trades
	/// INSERT and the position recompute run inside ONE transaction, and the recompute is performed
	/// exactly once by <see cref="PositionRecalculationService"/>. The old trg_Trades_PositionRecalc
	/// trigger - and the double-count hazard of the trigger and a standalone recalc both firing for the
	/// same trade - is gone; the recompute is no longer "automatic" inside SQL.
	/// <para>
	/// Concurrency: a per-position (portfolio+security) application lock is acquired BEFORE the trade is
	/// inserted, so concurrent fills of the same instrument serialize cleanly instead of forming the
	/// insert-then-lock deadlock cycle that would otherwise arise from inserting the trade first and only
	/// then locking the position row. If a transient deadlock (SQL error 1205) still occurs, the whole
	/// unit of work is retried on a fresh transaction a bounded number of times.
	/// </para>
	/// <para>
	/// Idempotency: pass <paramref name="externalTradeId"/> - the external execution/trade id of the
	/// logical fill - to make recording exactly-once. When supplied, a duplicate call (a client retry, or
	/// the deadlock retry above) no-ops instead of inserting a second trade row, so the position is not
	/// double-counted (uniqueness is enforced by the filtered index UQ_Trades_external_trade_id). When it
	/// is <see langword="null"/> (the default) the fill is always inserted, preserving the original
	/// behavior for callers that do not supply a key.
	/// </para>
	/// </summary>
	/// <param name="orderId">The order the fill is recorded against.</param>
	/// <param name="qty">The fill quantity.</param>
	/// <param name="price">The fill price.</param>
	/// <param name="externalTradeId">
	/// Optional business idempotency key (external execution/trade id). When supplied, re-recording the
	/// same key no-ops so the fill is applied at most once; when <see langword="null"/> the fill is always
	/// recorded.
	/// </param>
	/// <param name="cancellationToken">The cancellation token.</param>
	public async Task RecordTradeAsync(long orderId, decimal qty, decimal price, long? externalTradeId = null, CancellationToken cancellationToken = default)
	{
		// Bounded deadlock-retry. A deadlock victim's transaction is rolled back atomically (its dbo.Trades
		// INSERT is undone), so re-running the whole unit of work on a fresh transaction is safe; combined
		// with the optional externalTradeId idempotency key even a post-commit client retry cannot
		// double-record a logical fill (QA findings F1 + F2). With the lock-before-insert ordering below a
		// same-position deadlock should no longer occur - this loop is defense-in-depth for any residual
		// cross-resource contention and rethrows once the attempts are exhausted (never hangs).
		const int maxAttempts = 3;

		for (var attempt = 1; ; attempt++)
		{
			try
			{
				await RecordTradeCoreAsync(orderId, qty, price, externalTradeId, cancellationToken);
				return;
			}
			catch (SqlException ex) when (IsDeadlockVictim(ex) && attempt < maxAttempts)
			{
				// Brief, increasing backoff before retrying on a fresh transaction.
				await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
			}
		}
	}

	// Single attempt of RecordTradeAsync: lock the position, insert the fill (idempotently when a key is
	// supplied), recompute the position exactly once, and commit - all in one transaction.
	private async Task RecordTradeCoreAsync(long orderId, decimal qty, decimal price, long? externalTradeId, CancellationToken cancellationToken)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);
		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

		// Resolve the position this fill belongs to, then serialize concurrent fills of that position
		// BEFORE inserting the trade. Acquiring the lock first is what breaks the insert-then-lock deadlock
		// cycle (QA finding F1): a second concurrent fill for the same position blocks here, before it
		// inserts its own dbo.Trades row, so two transactions can never each hold the other's uncommitted
		// trade row while contending for the position lock. Position scope (not the submission path's
		// portfolio scope) is deliberate - fills for different securities do not contend.
		var (portfolioId, securityId) = await ResolveOrderScopeAsync(connection, transaction, orderId, cancellationToken);
		await AcquireAppLockAsync(connection, transaction, $"StockSharpLegacy:Position:{portfolioId}:{securityId}", cancellationToken);

		// Plain parameterized INSERT of the fill. With an idempotency key the INSERT no-ops when a trade
		// with that key already exists, so a retry records the logical fill at most once (QA finding F2);
		// rowsInserted tells us whether a new trade actually entered the set.
		int rowsInserted;

		if (externalTradeId is null)
		{
			await using var insert = new SqlCommand(
				"INSERT INTO dbo.Trades (order_id, qty, price) VALUES (@order_id, @qty, @price)", connection, transaction);
			insert.Parameters.Add(new SqlParameter("@order_id", SqlDbType.BigInt) { Value = orderId });
			insert.Parameters.Add(new SqlParameter("@qty", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = qty });
			insert.Parameters.Add(new SqlParameter("@price", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = price });

			rowsInserted = await insert.ExecuteNonQueryAsync(cancellationToken);
		}
		else
		{
			await using var insert = new SqlCommand(
				"""
				INSERT INTO dbo.Trades (order_id, qty, price, external_trade_id)
				SELECT @order_id, @qty, @price, @external_trade_id
				WHERE NOT EXISTS (SELECT 1 FROM dbo.Trades WHERE external_trade_id = @external_trade_id)
				""", connection, transaction);
			insert.Parameters.Add(new SqlParameter("@order_id", SqlDbType.BigInt) { Value = orderId });
			insert.Parameters.Add(new SqlParameter("@qty", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = qty });
			insert.Parameters.Add(new SqlParameter("@price", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = price });
			insert.Parameters.Add(new SqlParameter("@external_trade_id", SqlDbType.BigInt) { Value = externalTradeId.Value });

			rowsInserted = await insert.ExecuteNonQueryAsync(cancellationToken);
		}

		// Recompute the position from the persisted trade set exactly once, inside the same transaction.
		// The service also takes UPDLOCK/HOLDLOCK on the position row (defense for standalone callers); here
		// it runs uncontended because we already hold the per-position application lock. Skip the recompute
		// when the INSERT was a duplicate no-op: the trade set is unchanged, so the position already
		// reflects this fill and must not be applied again.
		if (rowsInserted > 0)
			await _positionRecalculationService.ApplyTradeAsync(connection, transaction, orderId, cancellationToken);

		await transaction.CommitAsync(cancellationToken);
	}

	/// <summary>
	/// Reads the current SQL-side position for a portfolio/security pair, or
	/// <see langword="null"/> if no trades have been recorded against it yet.
	/// </summary>
	public async Task<SqlPosition> GetPositionAsync(int portfolioId, int securityId, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using var command = new SqlCommand(
			"SELECT qty, avg_price, realized_pnl, unrealized_pnl, updated_date FROM dbo.Positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id",
			connection);
		command.Parameters.AddWithValue("@portfolio_id", portfolioId);
		command.Parameters.AddWithValue("@security_id", securityId);

		await using var reader = await command.ExecuteReaderAsync(cancellationToken);

		if (!await reader.ReadAsync(cancellationToken))
			return null;

		return new()
		{
			PortfolioId = portfolioId,
			SecurityId = securityId,
			Quantity = reader.GetDecimal(0),
			AveragePrice = reader.GetDecimal(1),
			RealizedPnL = reader.GetDecimal(2),
			UnrealizedPnL = reader.GetDecimal(3),
			UpdatedDate = reader.GetDateTime(4),
		};
	}

	private static string MapOrderType(OrderTypes type) => type switch
	{
		OrderTypes.Limit => "LIMIT",
		OrderTypes.Market => "MARKET",
		_ => throw new NotSupportedException($"Order type '{type}' has no dbo.Orders.order_type mapping (LIMIT/MARKET only)."),
	};
}
