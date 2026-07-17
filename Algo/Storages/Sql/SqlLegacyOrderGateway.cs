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
/// operations against dbo.Orders, dbo.Trades and dbo.Positions. The SQL side holds
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
		_positionRecalc = new PositionRecalculationService();
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
		try
		{
			await using var insert = new SqlCommand(
				"INSERT INTO dbo.Portfolios (name) OUTPUT INSERTED.portfolio_id VALUES (@name)", connection);
			insert.Parameters.AddWithValue("@name", portfolio.Name);

			return (int)await insert.ExecuteScalarAsync(cancellationToken);
		}
		catch (SqlException ex) when (IsUniqueViolation(ex))
		{
			// P10-F11: the SELECT-then-INSERT above is check-then-act, so two concurrent
			// callers can both miss the row and race to INSERT the same portfolio name.
			// UQ_Portfolios_name makes the loser fail with a duplicate-key violation
			// (2627/2601) instead of creating a second row - recover by re-reading the row the
			// winner just committed and returning its id, so the method stays idempotent under
			// concurrency rather than surfacing a raw provider error to the caller.
			await using var reselect = new SqlCommand(
				"SELECT portfolio_id FROM dbo.Portfolios WHERE name = @name", connection);
			reselect.Parameters.AddWithValue("@name", portfolio.Name);

			if (await reselect.ExecuteScalarAsync(cancellationToken) is int winnerId)
				return winnerId;

			// The winning row is only absent here if it was deleted again between the failed
			// INSERT and this read; there is nothing to recover, so surface a sanitized domain
			// error rather than the raw provider exception.
			throw SanitizedConstraintError(ex, "portfolio creation");
		}
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

		try
		{
			await using var insert = new SqlCommand(
				"INSERT INTO dbo.Securities (security_code, board_code, security_type) OUTPUT INSERTED.security_id VALUES (@code, @board, @type)", connection);
			insert.Parameters.AddWithValue("@code", security.Code);
			insert.Parameters.AddWithValue("@board", (object)boardCode ?? DBNull.Value);
			insert.Parameters.AddWithValue("@type", (object)security.Type?.ToString() ?? DBNull.Value);

			return (int)await insert.ExecuteScalarAsync(cancellationToken);
		}
		catch (SqlException ex) when (IsUniqueViolation(ex))
		{
			// P10-F12: same check-then-act race as EnsurePortfolioAsync. UQ_Securities_code_board
			// turns the losing concurrent INSERT into a duplicate-key violation; recover by
			// re-selecting the winner's row (by code + board, NULL-safe) and returning its id so
			// the method is idempotent under concurrency.
			await using var reselect = new SqlCommand(
				"""
				SELECT security_id FROM dbo.Securities
				WHERE security_code = @code AND (board_code = @board OR (@board IS NULL AND board_code IS NULL))
				""", connection);
			reselect.Parameters.AddWithValue("@code", security.Code);
			reselect.Parameters.AddWithValue("@board", (object)boardCode ?? DBNull.Value);

			if (await reselect.ExecuteScalarAsync(cancellationToken) is int winnerId)
				return winnerId;

			throw SanitizedConstraintError(ex, "security creation");
		}
	}

	/// <summary>
	/// Submits an order: runs the C# <see cref="PreTradeRiskService"/> pre-trade gate
	/// and, for a well-formed order, inserts the resulting dbo.Orders row (ACCEPTED, or
	/// REJECTED with the reason preserved for the audit trail - risk-rejected orders are
	/// recorded, not dropped). A MALFORMED request (invalid side, non-positive quantity,
	/// or a bad/absent price - <see cref="PreTradeRejectionKind.InvalidRequest"/>) is an
	/// input error and is thrown back to the caller as an <see cref="ArgumentException"/>
	/// WITHOUT persisting any row, because such a row would violate the dbo.Orders CHECK
	/// constraints or require mapping an invalid enum (MJ-1). Every returned result
	/// therefore describes a persisted order and carries a real
	/// <see cref="SqlOrderSubmitResult.OrderId"/>.
	/// </summary>
	/// <remarks>
	/// The pre-trade gate and the insert run on one connection inside a single
	/// SERIALIZABLE transaction, so the read-decide-insert sequence is one atomic unit:
	/// a concurrent submission cannot commit a change to the frequency/volume/position/
	/// commission state between this gate's reads and its insert, and the frequency
	/// window's key-range locks serialize competing inserts into the same window. This
	/// does NOT reserve position or commission exposure across separate in-flight orders:
	/// the gate reads the committed state at validation time and neither Submit nor the
	/// gate mutates dbo.Positions/dbo.Trades, so two orders that are each validated before
	/// either commits are both checked against the same pre-existing state (CR-5). The
	/// <paramref name="externalTransactionId"/> is persisted as a caller correlation id
	/// only - it is NOT a uniqueness or idempotency key, and a retry submits a new order.
	/// </remarks>
	public async Task<SqlOrderSubmitResult> SubmitOrderAsync(
		int portfolioId, int securityId, Sides side, decimal volume, decimal? price, OrderTypes orderType,
		long? externalTransactionId = null, string requestedBy = null, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

		// Serialize the whole read-decide-insert critical section per PORTFOLIO before
		// taking any data locks. Under SERIALIZABLE the pre-trade reads that scan this
		// portfolio's Orders range (RiskOrderFreqRule COUNT(*), RiskDailyVolumeRule SUM,
		// the commission notional SUM) take RangeS-S key-range locks, while the INSERT
		// below needs a RangeI-N lock on that same range. Two concurrent submits to the
		// SAME portfolio therefore each hold RangeS-S and then request RangeI-N, which
		// deadlocks (SQL error 1205) - a robustness gap, though never a limit bypass, since
		// the loser rolls back atomically. Taking an EXCLUSIVE, transaction-scoped
		// application lock keyed on portfolio_id up front means only one submit per
		// portfolio is ever inside the critical section, so that lock cycle cannot form;
		// submits to DIFFERENT portfolios use different lock keys (and disjoint Orders key
		// ranges) and still run fully in parallel. The lock is released automatically when
		// this transaction commits or rolls back, so it also preserves the SERIALIZABLE
		// check-to-insert (TOCTOU) guarantee the gate relies on. sp_getapplock is a
		// built-in SQL Server concurrency primitive (like the isolation level itself), not
		// business logic - no risk threshold or decision lives in SQL.
		await AcquirePortfolioSubmitLockAsync(connection, transaction, portfolioId, cancellationToken);

		// Canonical C# pre-trade gate; runs on the same connection/transaction as the
		// insert below so the read-decide-insert sequence is one atomic unit.
		var validation = await _preTradeRisk.ValidateAsync(
			connection, transaction, portfolioId, securityId, side, volume, price,
			orderType, requestedBy, cancellationToken);

		// (MJ-1) A malformed request is an INPUT error, not a risk decision. Do not
		// persist an Orders row: a non-positive qty violates CK_Orders_qty, an invalid
		// side/order-type has no CHAR mapping (MapSide/MapOrderType would throw), and a
		// bad price is not a real order. Surface it to the caller as an argument error,
		// mirroring how the retired usp_SubmitOrder failed such input on a CHECK
		// violation rather than recording a row.
		if (validation.RejectionKind == PreTradeRejectionKind.InvalidRequest)
		{
			await transaction.RollbackAsync(cancellationToken);
			throw new ArgumentException(validation.RejectReason);
		}

		// Well-formed order: ACCEPTED, or REJECTED (risk-limit breach) recorded for the
		// audit trail. Both satisfy every Orders CHECK constraint (qty>0, valid side and
		// order_type).
		var status = validation.IsValid ? "ACCEPTED" : "REJECTED";

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

			try
			{
				orderId = (long)await insert.ExecuteScalarAsync(cancellationToken);
			}
			catch (SqlException ex) when (IsConstraintViolation(ex))
			{
				// P4-F6: a constraint violation on the Orders insert (a bad @portfolio_id /
				// @security_id foreign key, or a CHECK) is translated to a sanitized domain
				// error so the raw SqlException text - which embeds the constraint / table /
				// database names - is never surfaced to the caller (it is retained on the
				// InnerException for logs). The SERIALIZABLE transaction disposes and rolls back
				// on the way out, so no partial row is persisted.
				throw SanitizedConstraintError(ex, "order submission");
			}
		}

		await transaction.CommitAsync(cancellationToken);

		return new SqlOrderSubmitResult
		{
			OrderId = orderId,
			IsValid = validation.IsValid,
			RejectReason = validation.RejectReason,
		};
	}

	/// <summary>
	/// Records a fill against a SQL-side order: inserts the dbo.Trades row and then
	/// recomputes the position through the C# <see cref="PositionRecalculationService"/>
	/// once, both inside one SERIALIZABLE transaction so the trade and its position
	/// effect commit or roll back together. The old trg_Trades_PositionRecalc trigger is
	/// gone, so this is the single, unambiguous recompute for the trade - there is no
	/// second automatic recompute to double-count against (AAP 0.6.5).
	/// </summary>
	/// <param name="orderId">Identifier of the order the fill belongs to.</param>
	/// <param name="qty">Executed quantity (must be positive).</param>
	/// <param name="price">Executed price (must be positive).</param>
	/// <param name="cancellationToken">Token used to cancel the asynchronous operation.</param>
	public async Task RecordTradeAsync(long orderId, decimal qty, decimal price, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

		// P4-F3: a fill may only be recorded against an order that is in a FILLABLE state.
		// Read the order's status inside this SERIALIZABLE transaction FIRST - the read takes a
		// key lock on the order row that is held to commit, so the status cannot change between
		// this check and the Trades insert below. That gives check-to-insert atomicity WITHOUT
		// an application lock: unlike SubmitOrderAsync this method does only point reads by
		// primary key (no per-portfolio range scans), so the RangeS-S/RangeI-N cycle that made
		// SubmitOrderAsync need sp_getapplock cannot form here. A REJECTED, CANCELLED or
		// still-PENDING order must NOT drive a trade or a position change: the request is
		// rejected atomically (no trade row, no recompute) and the transaction rolls back on the
		// way out. This is a data-integrity guard, not a risk decision - it holds no thresholds.
		string status;

		await using (var statusCmd = new SqlCommand(
			"SELECT status FROM dbo.Orders WHERE order_id = @order_id", connection)
		{
			Transaction = transaction,
		})
		{
			statusCmd.Parameters.AddWithValue("@order_id", orderId);

			if (await statusCmd.ExecuteScalarAsync(cancellationToken) is string existingStatus)
				status = existingStatus;
			else
				throw new InvalidOperationException(FormattableString.Invariant(
					$"Cannot record a trade: order {orderId} does not exist."));
		}

		// Only an order that was accepted (or is already partially/fully filled) can receive a
		// fill; anything else is an invalid state transition. The surfaced Message names only
		// the order id and its business status (a domain value the caller owns) - never a
		// schema object - so there is no information-disclosure concern here.
		if (status is not ("ACCEPTED" or "PARTFILLED" or "FILLED"))
			throw new InvalidOperationException(FormattableString.Invariant(
				$"Cannot record a trade: order {orderId} is not in a fillable state (status '{status}')."));

		await using (var insert = new SqlCommand(
			"INSERT INTO dbo.Trades (order_id, qty, price) VALUES (@order_id, @qty, @price)", connection)
		{
			Transaction = transaction,
		})
		{
			insert.Parameters.AddWithValue("@order_id", orderId);
			insert.Parameters.Add(new SqlParameter("@qty", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = qty });
			insert.Parameters.Add(new SqlParameter("@price", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = price });

			int affected;

			try
			{
				affected = await insert.ExecuteNonQueryAsync(cancellationToken);
			}
			catch (SqlException ex) when (IsConstraintViolation(ex))
			{
				// P4-F6: sanitize a constraint violation on the Trades insert (e.g.
				// FK_Trades_Orders, or a CK on qty/price) - the transaction rolls back on the
				// way out, so no partial trade or recompute is persisted.
				throw SanitizedConstraintError(ex, "trade recording");
			}

			if (affected != 1)
				throw new InvalidOperationException(FormattableString.Invariant(
					$"Trade insert for order {orderId} affected {affected} rows (expected 1)."));
		}

		// Single recompute per trade, in the same transaction as the insert above.
		await _positionRecalc.RecalculateAsync(connection, transaction, orderId, qty, price, cancellationToken);

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

	private static string MapSide(Sides side) => side switch
	{
		Sides.Buy => "B",
		Sides.Sell => "S",
		_ => throw new NotSupportedException($"Order side '{side}' has no dbo.Orders.side mapping (Buy/Sell only)."),
	};

	// --- Provider-error sanitization (P4-F6) --------------------------------------------
	// SQL Server surfaces constraint / data-integrity failures as a SqlException whose
	// Message embeds internal identifiers - constraint names (FK_/CK_/UQ_), table and column
	// names, and the database name. Leaking that text to a caller is an information-disclosure
	// gap, so the helpers below recognise the EXPECTED provider error numbers and translate
	// them to a stable, domain-level InvalidOperationException. The raw SqlException is kept as
	// the InnerException, so full detail is still available to server-side diagnostics/logs but
	// is NOT part of the surfaced Message. Transient / connectivity errors are deliberately NOT
	// matched here: they propagate unchanged so the callers' reachability semantics (and the
	// test harness's OpenLegacyOrInconclusiveAsync fallback) are unaffected.

	// Unique-constraint / unique-index violation (duplicate key): 2627 (constraint), 2601 (index).
	private static bool IsUniqueViolation(SqlException ex)
		=> ex.Number is 2627 or 2601;

	// Data-integrity violations that embed schema object names: FK / CHECK (547), NOT NULL
	// (515), string-or-binary truncation (8152 legacy, 2628 newer), and numeric overflow on
	// convert (8115). Unique violations are included so any insert that hits one is sanitized
	// too (the Ensure* paths handle their OWN unique races before this can be reached).
	private static bool IsConstraintViolation(SqlException ex)
		=> ex.Number is 547 or 515 or 8152 or 2628 or 8115 || IsUniqueViolation(ex);

	// Wraps an expected constraint SqlException in a sanitized domain exception. The public
	// Message names only the logical operation - never a constraint, table, column or the
	// database - while the original SqlException is retained on InnerException for logs.
	private static InvalidOperationException SanitizedConstraintError(SqlException ex, string operation)
		=> new(FormattableString.Invariant(
			$"The {operation} was rejected because it violates a data-integrity constraint."), ex);

	// Acquires an EXCLUSIVE, transaction-scoped application lock keyed on the portfolio so
	// the read-decide-insert critical section in SubmitOrderAsync runs one-at-a-time per
	// portfolio. This removes the SERIALIZABLE RangeS-S (pre-trade range reads) vs RangeI-N
	// (Orders insert) deadlock cycle on a hot portfolio while leaving submits to other
	// portfolios fully concurrent (distinct lock keys). @LockOwner = 'Transaction' ties the
	// lock's lifetime to the surrounding transaction, so it is released on commit OR
	// rollback with no explicit unlock and no leak on the exception paths. @LockTimeout = -1
	// waits for the lock (bounded only by the caller's CancellationToken, which cancels the
	// wait); the critical section is short, so that wait stays tiny in practice. This uses
	// the built-in sp_getapplock concurrency primitive - it holds no risk threshold or
	// accept/reject logic, so the "no business logic in SQL" contract is preserved.
	private static async Task AcquirePortfolioSubmitLockAsync(
		SqlConnection connection, SqlTransaction transaction, int portfolioId, CancellationToken cancellationToken)
	{
		await using var command = new SqlCommand("sys.sp_getapplock", connection)
		{
			Transaction = transaction,
			CommandType = CommandType.StoredProcedure,
			// No client-side command deadline: the wait is governed by @LockTimeout (below)
			// and the CancellationToken, not by an arbitrary command timeout that could
			// surface mid-serialization as a raw error under a large burst.
			CommandTimeout = 0,
		};
		command.Parameters.AddWithValue("@Resource", FormattableString.Invariant($"SqlLegacyOrderGateway/SubmitOrder/portfolio/{portfolioId}"));
		command.Parameters.AddWithValue("@LockMode", "Exclusive");
		command.Parameters.AddWithValue("@LockOwner", "Transaction");
		command.Parameters.AddWithValue("@LockTimeout", -1);

		var resultCode = new SqlParameter
		{
			ParameterName = "@Result",
			SqlDbType = SqlDbType.Int,
			Direction = ParameterDirection.ReturnValue,
		};
		command.Parameters.Add(resultCode);

		try
		{
			await command.ExecuteNonQueryAsync(cancellationToken);
		}
		catch (SqlException ex) when (cancellationToken.IsCancellationRequested)
		{
			// P4-F5: when the CancellationToken fires while ExecuteNonQueryAsync is waiting on
			// the (@LockTimeout = -1) lock, Microsoft.Data.SqlClient asks the server to cancel
			// the running batch, which can surface as a SqlException ("Operation cancelled by
			// user.") rather than a clean OperationCanceledException. Normalize it so a
			// caller-requested cancellation is ALWAYS observed as an OperationCanceledException,
			// regardless of whether the wait was interrupted before the batch started running
			// (clean OCE, already correct) or after (raw SqlException, translated here). The raw
			// SqlException is preserved as the inner exception for diagnostics.
			throw new OperationCanceledException(
				"The per-portfolio submit lock wait was canceled.", ex, cancellationToken);
		}

		// sp_getapplock return codes: 0 = granted, 1 = granted after waiting (both success);
		// negative = failure (-1 timeout, -2 canceled, -3 deadlock victim, -999 bad
		// parameter / other). With an infinite @LockTimeout a wait-timeout (-1) cannot occur,
		// and a caller cancellation is normalized to an OperationCanceledException (either
		// raised directly by ExecuteNonQueryAsync or translated from a SqlException by the catch
		// above) before we reach here, so any negative code is an unexpected acquisition failure
		// worth surfacing.
		var code = resultCode.Value is int value ? value : -999;

		if (code < 0)
			throw new InvalidOperationException(FormattableString.Invariant(
				$"Could not acquire the per-portfolio submit lock for portfolio {portfolioId} (sp_getapplock returned {code})."));
	}
}
