namespace StockSharp.Algo.Storages.Sql;

using System.Data;

using Microsoft.Data.SqlClient;

using StockSharp.Algo.Risk;

/// <summary>
/// ADO.NET gateway onto the StockSharpLegacy database (schema lives under /Database at the repo
/// root). Deliberately raw <see cref="SqlCommand"/> calls, not an ORM. Following the SQL -&gt; C#
/// risk-logic consolidation this is a <i>data-access adapter with no business logic</i>: it performs
/// plain parameterized INSERT/SELECT statements for orders, trades and positions, and it additionally
/// issues SQL Server application locks (<c>sp_getapplock</c>/<c>sp_releaseapplock</c>) purely as a
/// concurrency-control mechanism to serialize concurrent submits and fills for a portfolio - those
/// locks carry no risk decisioning. Every risk decision and position calculation is delegated to the
/// C# services in the <see cref="StockSharp.Algo.Risk"/> namespace -
/// <see cref="PreTradeRiskService"/> for the seven-check pre-trade gate and
/// <see cref="PositionRecalculationService"/> for average-cost / realized-P&amp;L. The database no
/// longer holds any stored-procedure business logic, so there are no CommandType.StoredProcedure
/// decisioning calls here any more (the old EXEC dbo.usp_SubmitOrder path is gone). Transactions run at
/// READ COMMITTED isolation; it is the application lock (not a serializable range lock) that makes the
/// validate-then-insert and fill-then-recompute sequences atomic against concurrent callers.
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
	/// <remarks>
	/// <para>
	/// Structural validation (review finding M07 / CWE-20) runs BEFORE any database work: the side must be
	/// Buy or Sell, the quantity must be positive, and the price must match the order type (a Limit order
	/// carries a positive price, a Market order carries none). A structurally invalid order throws rather
	/// than being persisted; this is distinct from a <i>risk</i> rejection (a well-formed order that
	/// breaches a limit), which is still recorded as REJECTED for the audit trail.
	/// </para>
	/// <para>
	/// Idempotency (review finding M09): when <paramref name="externalTransactionId"/> is supplied,
	/// re-submitting the same key returns the ORIGINAL outcome instead of inserting a second order, so a
	/// client retry cannot create a duplicate. If the same key is replayed with a materially different
	/// payload (different security, side, quantity, price or order type) a
	/// <see cref="SqlLegacyIdempotencyConflictException"/> is thrown rather than silently ignoring the
	/// mismatch. The lookup and the INSERT run under the shared per-portfolio application lock, so the
	/// check-then-insert is atomic against concurrent same-portfolio submissions.
	/// </para>
	/// </remarks>
	/// <param name="portfolioId">The SQL-side portfolio identifier.</param>
	/// <param name="securityId">The SQL-side security identifier.</param>
	/// <param name="side">The order side (Buy or Sell).</param>
	/// <param name="volume">The order quantity (must be positive).</param>
	/// <param name="price">The order price (positive for Limit); must be null for a Market order.</param>
	/// <param name="orderType">The order type (Limit or Market only).</param>
	/// <param name="externalTransactionId">Optional business idempotency key stored on the row; a duplicate key returns the original outcome.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The submission outcome: the new order_id, whether it was accepted, and any reject reason.</returns>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="side"/> is not Buy/Sell, or <paramref name="volume"/> is not positive.</exception>
	/// <exception cref="ArgumentException">The <paramref name="price"/> does not match the <paramref name="orderType"/> (Limit needs a positive price; Market needs none).</exception>
	/// <exception cref="NotSupportedException"><paramref name="orderType"/> is neither Limit nor Market.</exception>
	/// <exception cref="SqlLegacyIdempotencyConflictException"><paramref name="externalTransactionId"/> was reused with a different payload.</exception>
	public async Task<SqlOrderSubmitResult> SubmitOrderAsync(
		int portfolioId, int securityId, Sides side, decimal volume, decimal? price, OrderTypes orderType,
		long? externalTransactionId = null, CancellationToken cancellationToken = default)
	{
		// M07 - validate the whole structural contract up front, before opening any database resources, so
		// a malformed order fails fast and deterministically and never reaches persistence. A risk breach
		// (a well-formed order over a limit) is a different thing and is still recorded as REJECTED below.
		ValidateOrderStructure(side, volume, price, orderType);

		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);
		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

		// Serialize concurrent submissions for this portfolio so the validate-then-insert sequence is a
		// single atomic check-then-act. Portfolio scope (not portfolio+security) is deliberate: the
		// frequency and commission checks are portfolio-wide, so a narrower lock would leave those races
		// open. This is the SAME shared lock domain the fill path takes as its OUTER lock (review finding
		// C04), so a submit and a fill for the same portfolio serialize against each other. The lock is
		// held for the life of the transaction and released on commit/rollback.
		await AcquireAppLockAsync(connection, transaction, PortfolioLockResource(portfolioId), cancellationToken);

		// M09 - order-submit idempotency. Under the portfolio lock, look for an order already recorded
		// under this key; a duplicate submission (client retry) returns the ORIGINAL outcome, and a key
		// reused for a different payload is a conflict (thrown), never a silent second order.
		if (externalTransactionId is long submitKey)
		{
			var existing = await TryGetOrderByKeyAsync(connection, transaction, portfolioId, submitKey, cancellationToken);

			if (existing is not null)
			{
				var incomingSide = MapSide(side);
				var incomingType = MapOrderType(orderType);
				var normalizedVolume = decimal.Round(volume, 4, MidpointRounding.AwayFromZero);
				var normalizedPrice = price is decimal rawPrice ? decimal.Round(rawPrice, 4, MidpointRounding.AwayFromZero) : (decimal?)null;

				var samePayload =
					existing.SecurityId == securityId &&
					string.Equals(existing.Side, incomingSide, StringComparison.Ordinal) &&
					existing.Qty == normalizedVolume &&
					existing.Price == normalizedPrice &&
					string.Equals(existing.OrderType, incomingType, StringComparison.Ordinal);

				if (!samePayload)
				{
					throw new SqlLegacyIdempotencyConflictException(
						"order", submitKey.ToString(),
						$"external_transaction_id {submitKey} was already used for a different order " +
						$"(existing: security {existing.SecurityId}, {existing.Side} {existing.Qty} @ {(existing.Price?.ToString() ?? "MKT")} {existing.OrderType}; " +
						$"replay: security {securityId}, {incomingSide} {normalizedVolume} @ {(normalizedPrice?.ToString() ?? "MKT")} {incomingType}).");
				}

				await transaction.CommitAsync(cancellationToken);

				return new()
				{
					OrderId = existing.OrderId,
					IsValid = string.Equals(existing.Status, "ACCEPTED", StringComparison.Ordinal),
					RejectReason = existing.RejectReason,
				};
			}
		}

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
			insert.Parameters.Add(new SqlParameter("@side", SqlDbType.Char, 1) { Value = MapSide(side) });
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

	// The single shared application-lock resource for a portfolio. Both SubmitOrderAsync (as its only
	// lock) and RecordTradeCoreAsync (as its OUTER lock) take this, so a submit and a fill for the same
	// portfolio serialize against each other (review finding C04): a fill can no longer read order/position
	// state that an in-flight submit is about to change, and vice versa.
	private static string PortfolioLockResource(int portfolioId) => $"StockSharpLegacy:Portfolio:{portfolioId}";

	// M07 - exhaustive, fail-fast structural validation of the public order contract (CWE-20). Runs before
	// any database work. A malformed order throws here; a well-formed order that later breaches a risk
	// limit is a different case and is recorded as REJECTED by SubmitOrderAsync.
	private static void ValidateOrderStructure(Sides side, decimal volume, decimal? price, OrderTypes orderType)
	{
		if (side is not (Sides.Buy or Sides.Sell))
			throw new ArgumentOutOfRangeException(nameof(side), side, "Order side must be Buy or Sell.");

		if (volume <= 0m)
			throw new ArgumentOutOfRangeException(nameof(volume), volume, "Order quantity must be positive.");

		if (orderType is not (OrderTypes.Limit or OrderTypes.Market))
			throw new NotSupportedException($"Order type '{orderType}' is not supported by the legacy gateway; only Limit and Market are.");

		if (orderType == OrderTypes.Limit)
		{
			if (price is not decimal limitPrice || limitPrice <= 0m)
				throw new ArgumentException("A Limit order requires a positive price.", nameof(price));
		}
		else if (price is not null)
		{
			throw new ArgumentException("A Market order must not carry a price.", nameof(price));
		}
	}

	// Exhaustive side -> dbo.Orders.side (CHAR(1)) mapping. Buy/Sell are the only valid enum values; an
	// out-of-range cast throws rather than being silently coerced to 'S' (review finding M07). This is the
	// C# analogue of the CK_Orders_side CHECK the column carries.
	private static string MapSide(Sides side) => side switch
	{
		Sides.Buy => "B",
		Sides.Sell => "S",
		_ => throw new ArgumentOutOfRangeException(nameof(side), side, "Order side must be Buy or Sell."),
	};

	// M09 helper - reads the order already recorded under an external_transaction_id for this portfolio
	// (or null if none), scoped by the portfolio lock the caller holds. Returns the payload columns the
	// idempotency comparison needs plus the persisted outcome (status/reject_reason).
	private static async Task<ExistingOrder> TryGetOrderByKeyAsync(
		SqlConnection connection, SqlTransaction transaction, int portfolioId, long externalTransactionId, CancellationToken cancellationToken)
	{
		await using var command = new SqlCommand(
			"""
			SELECT order_id, security_id, side, qty, price, order_type, status, reject_reason
			FROM dbo.Orders
			WHERE portfolio_id = @portfolio_id AND external_transaction_id = @external_transaction_id
			""", connection, transaction);
		command.Parameters.Add(new SqlParameter("@portfolio_id", SqlDbType.Int) { Value = portfolioId });
		command.Parameters.Add(new SqlParameter("@external_transaction_id", SqlDbType.BigInt) { Value = externalTransactionId });

		await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

		if (!await reader.ReadAsync(cancellationToken))
			return null;

		return new ExistingOrder(
			reader.GetInt64(0),
			reader.GetInt32(1),
			reader.GetString(2).Trim(),
			reader.GetDecimal(3),
			await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetDecimal(4),
			reader.GetString(5).Trim(),
			reader.GetString(6).Trim(),
			await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7));
	}

	// The payload + outcome of an order already persisted under an external_transaction_id (M09).
	private sealed record ExistingOrder(
		long OrderId, int SecurityId, string Side, decimal Qty, decimal? Price, string OrderType, string Status, string RejectReason);

	// Serializes a unit of work on a named resource using sys.sp_getapplock (a SQL-Server-specific
	// application-lock primitive) in Transaction-owner mode. Combined with the enclosing transaction this
	// turns a read-then-write sequence into a single atomic critical section (C03/CWE-367): a second caller
	// contending for the same resource waits here until the first commits, so it can no longer act on
	// pre-write state. The lock is released automatically when the transaction ends. The submission path
	// and the fill path share ONE portfolio-scoped lock domain (StockSharpLegacy:Portfolio:{id}) as the
	// OUTER lock, so a submit and a fill for the same portfolio serialize against each other (review
	// finding C04 / the submit-vs-fill TOCTOU); the fill path then takes a narrower per-position lock as an
	// INNER lock, and, for a keyed fill, a per-key lock as the innermost lock. Acquired in that strict
	// order (portfolio -> position -> trade-key) the locks form a total order, so the gateway can never
	// deadlock on its own application locks.
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

		// Explicitly typed (SqlDbType.Int) and named ReturnValue parameter. sp_getapplock returns an int,
		// and reading it back as a typed value is what lets the fail-closed guard below reason about it.
		var returnValue = new SqlParameter("@ReturnValue", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
		command.Parameters.Add(returnValue);

		await command.ExecuteNonQueryAsync(cancellationToken);

		// FAIL CLOSED (review finding I01). sp_getapplock returns >= 0 on success (0 granted immediately,
		// 1 granted after waiting) and < 0 on failure (-1 timeout, -2 cancelled, -3 deadlock victim,
		// -999 validation error). Only a value we can positively prove is a non-negative int counts as
		// "granted"; a null, a non-integer, or any negative/unrecognized value is treated as a FAILURE and
		// throws. Never assume the lock was granted just because no recognizable failure code came back -
		// letting a critical section run without a lock we can prove we hold would silently reopen the very
		// check-then-act race the lock exists to close.
		if (returnValue.Value is int returnCode)
		{
			if (returnCode < 0)
				throw new InvalidOperationException($"Could not acquire the application lock '{resource}' (sp_getapplock returned {returnCode}).");
		}
		else
		{
			var reported = returnValue.Value is null or DBNull ? "null" : returnValue.Value.ToString();
			throw new InvalidOperationException($"Could not acquire the application lock '{resource}' (sp_getapplock returned an unrecognized value '{reported}'; treated as failure).");
		}
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
	/// This overload records an <b>un-keyed</b> fill (no business idempotency key), so every call inserts a
	/// new dbo.Trades row. Use the <see cref="RecordTradeAsync(long, decimal, decimal, long, CancellationToken)"/>
	/// overload to make recording exactly-once under retries. This 4-argument signature is the original,
	/// backward-compatible one (review finding M06); the idempotency key is a <i>separate</i> overload
	/// rather than an inserted middle parameter, so existing positional callers keep compiling.
	/// </para>
	/// <para>
	/// Validation (review finding M08): before a new fill is inserted the gateway verifies, under the
	/// application locks, that the order is fillable (status ACCEPTED or PARTFILLED - a REJECTED or
	/// otherwise terminal order cannot take a fill) and that the cumulative fills would not exceed the
	/// ordered quantity (no over-fill). A violation throws <see cref="InvalidOperationException"/>.
	/// </para>
	/// <para>
	/// Concurrency (review findings C03/C04, QA finding F1): the fill takes the shared per-portfolio
	/// application lock as its OUTER lock - the SAME lock <see cref="SubmitOrderAsync"/> holds - so a fill
	/// and a submission for the same portfolio serialize against each other, and a narrower per-position
	/// lock as its INNER lock, both acquired BEFORE the trade is inserted so concurrent fills cannot form
	/// the insert-then-lock deadlock cycle. If a transient deadlock (SQL error 1205) still occurs, the whole
	/// unit of work is retried on a fresh transaction a bounded number of times.
	/// </para>
	/// </summary>
	/// <param name="orderId">The order the fill is recorded against.</param>
	/// <param name="qty">The fill quantity (must be positive).</param>
	/// <param name="price">The fill price (must be positive).</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <exception cref="InvalidOperationException">The order is not fillable, or the fill would over-fill it.</exception>
	public Task RecordTradeAsync(long orderId, decimal qty, decimal price, CancellationToken cancellationToken = default)
		=> RecordTradeWithRetryAsync(orderId, qty, price, null, cancellationToken);

	/// <summary>
	/// Records a fill against a SQL-side order <b>idempotently</b>, keyed by
	/// <paramref name="externalTradeId"/> (the external execution/trade id of the logical fill), and
	/// recomputes the affected position exactly once. Re-recording the same key - a client retry, or the
	/// internal deadlock retry - applies the fill at most once instead of inserting a second trade row, so
	/// the position is never double-counted; uniqueness is backed by the filtered index
	/// UQ_Trades_external_trade_id.
	/// <para>
	/// The key is <b>required</b> on this overload (review finding M06/M09): a caller that wants
	/// exactly-once semantics must supply a real key rather than passing <see langword="null"/>, which
	/// would silently degrade to at-least-once. Replaying the same key with a materially different payload
	/// (different order, quantity or price) throws <see cref="SqlLegacyIdempotencyConflictException"/>
	/// rather than silently ignoring the mismatch. The same fillable/over-fill validation (M08) as the
	/// un-keyed overload applies to the first, non-duplicate recording.
	/// </para>
	/// </summary>
	/// <param name="orderId">The order the fill is recorded against.</param>
	/// <param name="qty">The fill quantity (must be positive).</param>
	/// <param name="price">The fill price (must be positive).</param>
	/// <param name="externalTradeId">The required business idempotency key (external execution/trade id).</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <exception cref="InvalidOperationException">The order is not fillable, or the fill would over-fill it.</exception>
	/// <exception cref="SqlLegacyIdempotencyConflictException"><paramref name="externalTradeId"/> was reused with a different payload.</exception>
	public Task RecordTradeAsync(long orderId, decimal qty, decimal price, long externalTradeId, CancellationToken cancellationToken = default)
		=> RecordTradeWithRetryAsync(orderId, qty, price, externalTradeId, cancellationToken);

	// Shared implementation for both public overloads: a bounded deadlock-retry around a single atomic
	// attempt. A deadlock victim's transaction is rolled back atomically (its dbo.Trades INSERT is undone),
	// so re-running the whole unit of work on a fresh transaction is safe; combined with the idempotency
	// key even a post-commit client retry cannot double-record a logical fill (QA findings F1 + F2). With
	// the lock-before-insert ordering below a same-position deadlock should no longer occur - this loop is
	// defense-in-depth for any residual cross-resource contention and rethrows once the attempts are
	// exhausted (never hangs).
	private async Task RecordTradeWithRetryAsync(long orderId, decimal qty, decimal price, long? externalTradeId, CancellationToken cancellationToken)
	{
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

	// Single attempt of RecordTradeAsync: acquire the shared portfolio (outer) + per-position (inner) locks
	// (and, for a keyed fill, the per-key innermost lock) BEFORE touching any row, enforce the M08 fillable
	// / over-fill guard, insert the fill (exactly-once when keyed), recompute the position exactly once, and
	// commit - all in one transaction.
	private async Task RecordTradeCoreAsync(long orderId, decimal qty, decimal price, long? externalTradeId, CancellationToken cancellationToken)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);
		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

		// Resolve the position this fill belongs to from its (immutable, already-committed) order row before
		// taking any lock, so the lock resources can be keyed by portfolio and security.
		var (portfolioId, securityId) = await ResolveOrderScopeAsync(connection, transaction, orderId, cancellationToken);

		// OUTER lock - the shared per-portfolio domain (review finding C04). Taking the SAME lock the
		// submission path holds is what serializes a fill against an in-flight submit for the same portfolio,
		// closing the submit-vs-fill check-then-act window (a fill can no longer read order/position state a
		// concurrent submit is about to change). Note: this intentionally does NOT reserve capacity for
		// still-open orders - the pre-trade gate mirrors the original SQL check, which counted only booked
		// positions (AAP 0.7.1 preserve-observable-behavior); it closes the race, it does not change the
		// booking model.
		await AcquireAppLockAsync(connection, transaction, PortfolioLockResource(portfolioId), cancellationToken);

		// INNER lock - the narrower per-position lock, preserving the fine-grained lock-before-insert design
		// (QA finding F1): concurrent fills of the same position serialize here before either inserts its
		// dbo.Trades row, so two transactions can never each hold the other's uncommitted trade row while
		// contending for the position. Acquired AFTER the portfolio lock, so the lock order is a strict
		// total order (portfolio -> position -> trade-key) and the gateway cannot self-deadlock.
		await AcquireAppLockAsync(connection, transaction, $"StockSharpLegacy:Position:{portfolioId}:{securityId}", cancellationToken);

		// Keyed idempotency (review finding M09). Take the innermost per-key lock so that even two fills for
		// DIFFERENT positions replaying the SAME key serialize (their portfolio/position locks differ, so
		// only this lock orders them), then look the key up: an exact-payload match is applied exactly once
		// (no second insert, no second recompute); a same-key/different-payload replay is a conflict.
		if (externalTradeId is long tradeKey)
		{
			await AcquireAppLockAsync(connection, transaction, $"StockSharpLegacy:TradeKey:{tradeKey}", cancellationToken);

			var existingTrade = await TryGetTradeByKeyAsync(connection, transaction, tradeKey, cancellationToken);

			if (existingTrade is not null)
			{
				var replayQty = decimal.Round(qty, 4, MidpointRounding.AwayFromZero);
				var replayPrice = decimal.Round(price, 4, MidpointRounding.AwayFromZero);

				var samePayload = existingTrade.OrderId == orderId && existingTrade.Qty == replayQty && existingTrade.Price == replayPrice;

				if (!samePayload)
				{
					throw new SqlLegacyIdempotencyConflictException(
						"trade", tradeKey.ToString(),
						$"external_trade_id {tradeKey} was already used for a different fill " +
						$"(existing: order {existingTrade.OrderId}, {existingTrade.Qty} @ {existingTrade.Price}; " +
						$"replay: order {orderId}, {replayQty} @ {replayPrice}).");
				}

				// Exact duplicate -> apply exactly once: no insert, no recompute, the position already
				// reflects this fill. Commit so the (read-only) transaction and its locks release cleanly.
				await transaction.CommitAsync(cancellationToken);
				return;
			}
		}

		// M08 - under the locks, verify this NEW fill is legal against the current order state: the order
		// must be fillable (ACCEPTED/PARTFILLED) and the cumulative fills must not exceed the ordered qty.
		await GuardFillableAsync(connection, transaction, orderId, qty, cancellationToken);

		// Plain parameterized INSERT of the fill. For a keyed fill the per-key lock + the not-exists check
		// above already guarantee this is the first insert of the key, so no WHERE NOT EXISTS guard is
		// needed; for an un-keyed fill this is the original always-insert behavior.
		await using (var insert = new SqlCommand(
			externalTradeId is null
				? "INSERT INTO dbo.Trades (order_id, qty, price) VALUES (@order_id, @qty, @price)"
				: "INSERT INTO dbo.Trades (order_id, qty, price, external_trade_id) VALUES (@order_id, @qty, @price, @external_trade_id)",
			connection, transaction))
		{
			insert.Parameters.Add(new SqlParameter("@order_id", SqlDbType.BigInt) { Value = orderId });
			insert.Parameters.Add(new SqlParameter("@qty", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = qty });
			insert.Parameters.Add(new SqlParameter("@price", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = price });

			if (externalTradeId is long key)
				insert.Parameters.Add(new SqlParameter("@external_trade_id", SqlDbType.BigInt) { Value = key });

			await insert.ExecuteNonQueryAsync(cancellationToken);
		}

		// Recompute the position from the persisted trade set exactly once, inside the same transaction. The
		// service also takes UPDLOCK/HOLDLOCK on the position row (defense for standalone callers); here it
		// runs uncontended because we already hold the portfolio and position application locks.
		await _positionRecalculationService.ApplyTradeAsync(connection, transaction, orderId, cancellationToken);

		await transaction.CommitAsync(cancellationToken);
	}

	// M08 helper - reads the order's status, ordered quantity, and cumulative recorded fills (under the
	// caller's locks and transaction) and rejects a fill that the order cannot legally take: a non-fillable
	// (REJECTED / terminal) order, or a fill that would push the total filled quantity past the ordered
	// quantity. Throws InvalidOperationException on a violation; returns quietly when the fill is legal.
	private static async Task GuardFillableAsync(
		SqlConnection connection, SqlTransaction transaction, long orderId, decimal qty, CancellationToken cancellationToken)
	{
		await using var command = new SqlCommand(
			"""
			SELECT o.status, o.qty, ISNULL((SELECT SUM(t.qty) FROM dbo.Trades t WHERE t.order_id = o.order_id), 0)
			FROM dbo.Orders o
			WHERE o.order_id = @order_id
			""", connection, transaction);
		command.Parameters.Add(new SqlParameter("@order_id", SqlDbType.BigInt) { Value = orderId });

		await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

		if (!await reader.ReadAsync(cancellationToken))
			throw new InvalidOperationException($"SqlLegacyOrderGateway: order_id {orderId} not found.");

		var status = reader.GetString(0).Trim();
		var orderedQty = reader.GetDecimal(1);
		var priorFills = reader.GetDecimal(2);

		// Only a live, accepted order can take a fill. A REJECTED order (recorded for audit) or any other
		// non-fillable status must never be filled.
		if (status is not ("ACCEPTED" or "PARTFILLED"))
			throw new InvalidOperationException($"SqlLegacyOrderGateway: order_id {orderId} is not fillable (status '{status}'; only ACCEPTED or PARTFILLED orders can be filled).");

		var normalizedQty = decimal.Round(qty, 4, MidpointRounding.AwayFromZero);

		if (priorFills + normalizedQty > orderedQty)
			throw new InvalidOperationException($"SqlLegacyOrderGateway: fill of {normalizedQty} would over-fill order_id {orderId} (already filled {priorFills} of {orderedQty}).");
	}

	// M09 helper - reads the trade already recorded under an external_trade_id (or null if none), scoped by
	// the per-key lock the caller holds. Returns the payload the idempotency comparison needs.
	private static async Task<ExistingTrade> TryGetTradeByKeyAsync(
		SqlConnection connection, SqlTransaction transaction, long externalTradeId, CancellationToken cancellationToken)
	{
		await using var command = new SqlCommand(
			"SELECT order_id, qty, price FROM dbo.Trades WHERE external_trade_id = @external_trade_id", connection, transaction);
		command.Parameters.Add(new SqlParameter("@external_trade_id", SqlDbType.BigInt) { Value = externalTradeId });

		await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

		if (!await reader.ReadAsync(cancellationToken))
			return null;

		return new ExistingTrade(reader.GetInt64(0), reader.GetDecimal(1), reader.GetDecimal(2));
	}

	// The payload of a trade already persisted under an external_trade_id (M09).
	private sealed record ExistingTrade(long OrderId, decimal Qty, decimal Price);

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
