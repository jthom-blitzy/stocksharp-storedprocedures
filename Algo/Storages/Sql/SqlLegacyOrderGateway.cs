namespace StockSharp.Algo.Storages.Sql;

using System.Data;

using Microsoft.Data.SqlClient;

using StockSharp.Algo.Risk;

/// <summary>
/// Pure ADO.NET data-access gateway onto the StockSharpLegacy database (schema
/// lives under /Database at the repo root). Deliberately raw
/// <see cref="SqlCommand"/> calls, not an ORM. The gateway holds no risk
/// thresholds or accept/reject/P&amp;L logic: every decision is delegated to the
/// canonical C# services - <see cref="PreTradeRiskService"/> for pre-trade
/// validation and <see cref="PositionRecalculationService"/> for average-cost /
/// realized-P&amp;L recompute - while the gateway performs only the create/read
/// operations against dbo.Orders, dbo.Trades and dbo.Positions (plus the
/// idempotency-key dedup reads that make retries replay-safe). The SQL side holds
/// data only - tables, constraints, indexes, the threshold VALUES in dbo.RiskLimits,
/// and one pure audit trigger (trg_Orders_StatusAudit that cascades Orders status
/// changes to dbo.OrderStatusHistory) - but no risk-decision or P&amp;L arithmetic
/// logic. The retired business-logic procedures (usp_SubmitOrder,
/// usp_ValidatePreTradeRisk, usp_RecalculatePositionOnTrade) and the
/// trg_Trades_PositionRecalc recompute trigger are dropped by the Database scripts
/// and are neither used nor installed.
///
/// This is an adapter that sits <i>alongside</i> <see cref="IEntityRegistry"/>,
/// not a replacement for it: Securities/Exchanges/subscriptions are still
/// served by <see cref="Csv.CsvEntityRegistry"/>. Only orders, trades and
/// positions live in SQL Server - that entity-storage split (nothing keeping the
/// SQL and CSV identifiers in sync) is described in LEGACY_LAYER.md and is
/// unrelated to the now-consolidated risk logic.
/// </summary>
public class SqlLegacyOrderGateway
{
	private readonly string _connectionString;
	private readonly PreTradeRiskService _preTradeRisk;
	private readonly PositionRecalculationService _positionRecalc;

	// Max attempts for a whole read-decide-write unit when SQL Server aborts it with a
	// transient concurrency error (deadlock victim / lock timeout). The retry is only
	// safe because submission and trade recording are idempotent (external_transaction_id
	// and execution_id keys): a re-run either finds the committed row and returns it or
	// re-runs cleanly after a full rollback (MJ-4).
	private const int _maxTransientRetries = 3;

	/// <summary>
	/// Initializes a new instance of the <see cref="SqlLegacyOrderGateway"/>.
	/// </summary>
	/// <param name="connectionString">SQL Server connection string for the StockSharpLegacy database.</param>
	public SqlLegacyOrderGateway(string connectionString)
	{
		if (connectionString.IsEmpty())
			throw new ArgumentNullException(nameof(connectionString));

		_connectionString = connectionString;
		_preTradeRisk = new PreTradeRiskService(connectionString);
		_positionRecalc = new PositionRecalculationService(connectionString);
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
	/// Submits an order: runs the C# <see cref="PreTradeRiskService"/> pre-trade gate
	/// and, for a well-formed order, inserts the resulting dbo.Orders row (ACCEPTED, or
	/// REJECTED with the reason preserved for the audit trail - risk-rejected orders are
	/// recorded, not dropped). A MALFORMED request (invalid side, non-positive quantity,
	/// or a bad/absent price - <see cref="PreTradeRejectionKind.InvalidRequest"/>) is an
	/// input error: the method returns the rejection WITHOUT persisting a row (with a
	/// <see langword="null"/> <see cref="SqlOrderSubmitResult.OrderId"/>), because such a
	/// row would violate the Orders CHECK constraints or require mapping an invalid enum
	/// (MJ-1).
	/// </summary>
	/// <remarks>
	/// Validation and the insert run in one serializable transaction so a concurrent
	/// submission cannot change the frequency/volume/position/commission state between
	/// the check and the insert. Submission is idempotent on
	/// <paramref name="externalTransactionId"/>: a retry with the same id returns the
	/// original order's recorded outcome rather than creating a second order, enforced by
	/// the filtered unique index UX_Orders_external_transaction_id (CR-4). The whole
	/// read-decide-insert unit is retried on a transient deadlock/lock-timeout, which is
	/// safe precisely because of that idempotency key (MJ-4).
	/// </remarks>
	public async Task<SqlOrderSubmitResult> SubmitOrderAsync(
		int portfolioId, int securityId, Sides side, decimal volume, decimal? price, OrderTypes orderType,
		long? externalTransactionId = null, string requestedBy = null, CancellationToken cancellationToken = default)
	{
		return await ExecuteWithRetryAsync(async ct =>
		{
			await using var connection = CreateConnection();
			await connection.OpenAsync(ct);

			await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

			// (CR-4) Replay dedup: if this transaction id already produced an order, return
			// that order's recorded outcome rather than creating a second one. Under
			// SERIALIZABLE the key-range lock taken by this read also blocks a concurrent
			// insert of the same key until this transaction ends.
			if (externalTransactionId is not null)
			{
				var existing = await TryReadOrderResultByTransactionAsync(connection, transaction, externalTransactionId.Value, ct);

				if (existing is not null)
				{
					// Read-only path; commit to release the range lock promptly.
					await transaction.CommitAsync(ct);
					return existing;
				}
			}

			// Canonical C# pre-trade gate; runs on the same connection/transaction as the
			// insert below so the read-decide-insert sequence is one atomic unit.
			var validation = await _preTradeRisk.ValidateAsync(
				connection, transaction, portfolioId, securityId, side, volume, price,
				orderType, requestedBy, ct);

			// (MJ-1) A malformed request is an INPUT error, not a risk decision. Do not
			// persist an Orders row: a non-positive qty violates CK_Orders_qty, an invalid
			// side/order-type has no CHAR mapping (MapSide/MapOrderType would throw), and a
			// bad price is not a real order. Return the rejection with a null OrderId.
			if (validation.RejectionKind == PreTradeRejectionKind.InvalidRequest)
			{
				await transaction.RollbackAsync(ct);

				return new SqlOrderSubmitResult
				{
					OrderId = null,
					IsValid = false,
					RejectReason = validation.RejectReason,
				};
			}

			// Well-formed order: ACCEPTED, or REJECTED (risk-limit breach) recorded for the
			// audit trail. Both satisfy every Orders CHECK constraint (qty>0, valid side and
			// order_type), so the only insert failure that can occur is the idempotency-key
			// race handled below.
			var status = validation.IsValid ? "ACCEPTED" : "REJECTED";

			try
			{
				long orderId;

				await using (var insert = new SqlCommand(
					"""
					INSERT INTO dbo.Orders (portfolio_id, security_id, side, qty, price, order_type, status, reject_reason, external_transaction_id)
						OUTPUT INSERTED.order_id
						VALUES (@portfolio_id, @security_id, @side, @qty, @price, @order_type, @status, @reject_reason, @external_transaction_id)
					""", connection)
				{
					Transaction = transaction,
				})
				{
					insert.Parameters.AddWithValue("@portfolio_id", portfolioId);
					insert.Parameters.AddWithValue("@security_id", securityId);
					insert.Parameters.AddWithValue("@side", MapSide(side));
					insert.Parameters.Add(new SqlParameter("@qty", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = volume });
					insert.Parameters.Add(new SqlParameter("@price", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = (object)price ?? DBNull.Value });
					insert.Parameters.AddWithValue("@order_type", MapOrderType(orderType));
					insert.Parameters.AddWithValue("@status", status);
					insert.Parameters.AddWithValue("@reject_reason", (object)validation.RejectReason ?? DBNull.Value);
					insert.Parameters.AddWithValue("@external_transaction_id", (object)externalTransactionId ?? DBNull.Value);

					orderId = (long)await insert.ExecuteScalarAsync(ct);
				}

				await transaction.CommitAsync(ct);

				return new SqlOrderSubmitResult
				{
					OrderId = orderId,
					IsValid = validation.IsValid,
					RejectReason = validation.RejectReason,
				};
			}
			catch (SqlException ex) when (externalTransactionId is not null && IsUniqueViolation(ex))
			{
				// (CR-4) A concurrent submit with the same transaction id won the race
				// between our dedup read and our insert. Roll back and return the committed
				// winner's outcome, read on a fresh connection.
				await transaction.RollbackAsync(ct);

				await using var readConnection = CreateConnection();
				await readConnection.OpenAsync(ct);

				var winner = await TryReadOrderResultByTransactionAsync(readConnection, null, externalTransactionId.Value, ct);

				return winner ?? throw new InvalidOperationException(FormattableString.Invariant(
					$"Unique violation on external_transaction_id {externalTransactionId} but no winning order row was found."));
			}
		}, cancellationToken);
	}

	/// <summary>
	/// Records a fill against a SQL-side order: inserts the dbo.Trades row and then
	/// recomputes the position through the C# <see cref="PositionRecalculationService"/>
	/// once, both inside one serializable transaction so the trade and its position
	/// effect commit or roll back together (the old trg_Trades_PositionRecalc trigger is
	/// gone, so this is the single, unambiguous recompute).
	/// </summary>
	/// <remarks>
	/// When <paramref name="executionId"/> is supplied the recording is idempotent: a
	/// retry with the same execution id neither re-inserts the trade nor re-applies its
	/// position effect (enforced by the filtered unique index UX_Trades_execution_id), so
	/// a duplicated fill is applied exactly once end-to-end (CR-4). With no execution id
	/// the call behaves as before (insert + single recompute) and carries no replay
	/// protection. The whole insert-and-recompute unit is retried on a transient
	/// deadlock/lock-timeout, which is safe because of that idempotency key (MJ-4).
	/// </remarks>
	/// <param name="orderId">Identifier of the order the fill belongs to.</param>
	/// <param name="qty">Executed quantity (must be positive).</param>
	/// <param name="price">Executed price (must be positive).</param>
	/// <param name="executionId">Optional idempotency key (e.g. venue fill id) making the recording replay-safe.</param>
	/// <param name="cancellationToken">Token used to cancel the asynchronous operation.</param>
	public async Task RecordTradeAsync(
		long orderId, decimal qty, decimal price, long? executionId = null, CancellationToken cancellationToken = default)
	{
		await ExecuteWithRetryAsync(async ct =>
		{
			await using var connection = CreateConnection();
			await connection.OpenAsync(ct);

			await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

			// (CR-4) Replay dedup: if this execution id was already recorded, the fill and
			// its position effect were already applied - do nothing. Under SERIALIZABLE the
			// key-range lock taken by this read also blocks a concurrent insert of the same
			// key until this transaction ends.
			if (executionId is not null)
			{
				await using var exists = new SqlCommand(
					"SELECT TOP (1) 1 FROM dbo.Trades WHERE execution_id = @execution_id", connection)
				{
					Transaction = transaction,
				};
				exists.Parameters.AddWithValue("@execution_id", executionId.Value);

				if (await exists.ExecuteScalarAsync(ct) is not null)
				{
					await transaction.CommitAsync(ct);
					return;
				}
			}

			try
			{
				await using var insert = new SqlCommand(
					"INSERT INTO dbo.Trades (order_id, qty, price, execution_id) VALUES (@order_id, @qty, @price, @execution_id)", connection)
				{
					Transaction = transaction,
				};
				insert.Parameters.AddWithValue("@order_id", orderId);
				insert.Parameters.Add(new SqlParameter("@qty", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = qty });
				insert.Parameters.Add(new SqlParameter("@price", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = price });
				insert.Parameters.AddWithValue("@execution_id", (object)executionId ?? DBNull.Value);

				var affected = await insert.ExecuteNonQueryAsync(ct);

				if (affected != 1)
					throw new InvalidOperationException(FormattableString.Invariant(
						$"Trade insert for order {orderId} affected {affected} rows (expected 1)."));
			}
			catch (SqlException ex) when (executionId is not null && IsUniqueViolation(ex))
			{
				// (CR-4) A concurrent RecordTrade with the same execution id won the race
				// between our dedup read and our insert. The winner already applied the
				// position effect, so skip the recompute and treat this as a benign no-op.
				await transaction.RollbackAsync(ct);
				return;
			}

			// Single recompute per trade, in the same transaction as the insert above.
			await _positionRecalc.RecalculateAsync(connection, transaction, orderId, qty, price, ct);

			await transaction.CommitAsync(ct);
		}, cancellationToken);
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

	// SQL Server transient-concurrency error numbers: 1205 = chosen as the deadlock
	// victim; 1222 = lock request timeout period exceeded. Both abort the current
	// transaction with a full rollback, so re-running the unit is safe.
	private static bool IsTransientConcurrencyError(SqlException ex)
	{
		foreach (SqlError error in ex.Errors)
		{
			if (error.Number is 1205 or 1222)
				return true;
		}

		return false;
	}

	// SQL Server unique-constraint / unique-index violation numbers: 2627 (constraint)
	// and 2601 (index). A retried submit/trade that races another writer on the same
	// idempotency key surfaces as one of these, which the caller treats as "the other
	// writer won" rather than an error.
	private static bool IsUniqueViolation(SqlException ex)
	{
		foreach (SqlError error in ex.Errors)
		{
			if (error.Number is 2627 or 2601)
				return true;
		}

		return false;
	}

	// Runs a whole open-connection -> begin-transaction -> work -> commit unit, retrying
	// the ENTIRE unit on a fresh connection/transaction when SQL Server aborts it with a
	// transient concurrency error. Bounded by _maxTransientRetries so a persistent
	// deadlock still surfaces rather than looping forever (MJ-4).
	private static async Task<T> ExecuteWithRetryAsync<T>(
		Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
	{
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				return await operation(cancellationToken);
			}
			catch (SqlException ex) when (attempt < _maxTransientRetries && IsTransientConcurrencyError(ex))
			{
				// Small, attempt-scaled backoff to let the winning transaction finish
				// before the losing one re-acquires its locks.
				await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
			}
		}
	}

	// Void-returning convenience overload of the transient-retry runner for operations
	// that produce no result (e.g. RecordTradeAsync).
	private static Task ExecuteWithRetryAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
		=> ExecuteWithRetryAsync(async ct =>
		{
			await operation(ct);
			return true;
		}, cancellationToken);

	// Reads the recorded outcome of a previously submitted order by its idempotency key
	// (external_transaction_id). Returns null when no order carries that key yet. Used
	// for replay-safe submission: a retried SubmitOrder returns the original outcome
	// instead of creating a second order (CR-4).
	private static async Task<SqlOrderSubmitResult> TryReadOrderResultByTransactionAsync(
		SqlConnection connection, SqlTransaction transaction, long externalTransactionId, CancellationToken cancellationToken)
	{
		await using var command = new SqlCommand(
			"SELECT TOP (1) order_id, status, reject_reason FROM dbo.Orders WHERE external_transaction_id = @external_transaction_id ORDER BY order_id",
			connection)
		{
			Transaction = transaction,
		};
		command.Parameters.AddWithValue("@external_transaction_id", externalTransactionId);

		await using var reader = await command.ExecuteReaderAsync(cancellationToken);

		if (!await reader.ReadAsync(cancellationToken))
			return null;

		var status = reader.GetString(1);

		return new()
		{
			OrderId = reader.GetInt64(0),
			IsValid = status == "ACCEPTED",
			RejectReason = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
		};
	}

	private static string MapOrderType(OrderTypes type) => type switch
	{
		OrderTypes.Limit => "LIMIT",
		OrderTypes.Market => "MARKET",
		_ => throw new NotSupportedException($"Order type '{type}' has no dbo.Orders.order_type mapping (LIMIT/MARKET only)."),
	};

	private static string MapSide(Sides side) => side switch
	{
		Sides.Buy => "B",
		Sides.Sell => "S",
		_ => throw new NotSupportedException($"Order side '{side}' has no dbo.Orders.side mapping (Buy/Sell only)."),
	};
}
