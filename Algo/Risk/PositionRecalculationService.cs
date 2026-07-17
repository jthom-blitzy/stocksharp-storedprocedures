namespace StockSharp.Algo.Risk;

using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Canonical average-cost position-recomputation service: the single C# source of truth for the
/// quantity / average-price / realized-P&amp;L math that the retired SQL procedure
/// <c>dbo.usp_RecalculatePositionOnTrade</c> used to own (see <c>Database/002_StoredProcedures.sql</c>,
/// which this refactor deletes).
/// </summary>
/// <remarks>
/// <para>
/// The pre-refactor design ran this recompute inside SQL Server: the <c>trg_Trades_PositionRecalc</c>
/// trigger fired for every inserted trade AND the procedure was also exposed standalone, so invoking
/// both double-counted a trade's effect on a position. In the consolidated design the trigger is removed
/// and the data-access gateway calls <see cref="ApplyAsync"/> exactly once per inserted trade, establishing
/// a single-apply invariant. <see cref="ApplyAsync"/> enforces that invariant DURABLY and across processes:
/// it claims each <c>trade_id</c> in the <c>processedtrades</c> ledger and serializes writes per
/// (portfolio, security) inside one transaction, so a repeat from a second service instance, a process
/// restart, or a concurrent caller is an idempotent no-op at the database level rather than relying on
/// process-local memory (AAP 0.6.5).
/// </para>
/// <para>
/// The average-cost arithmetic lives in the pure, static, database-free <see cref="Recalculate"/> method so
/// it can be unit-tested for parity against the original SQL implementation without a database;
/// <see cref="ApplyAsync"/> is only the persistence seam around it. All money/quantity/price arithmetic uses
/// <see cref="decimal"/> (never <c>double</c>/<c>float</c>) to preserve the schema's <c>NUMERIC(18,4)</c>
/// scale so a downstream comparison can never silently loosen.
/// </para>
/// </remarks>
public sealed class PositionRecalculationService
{
	// SINGLE-APPLY invariant (AAP 0.6.5): the position-recalc DB trigger was removed, so this service is the
	// sole applier of a trade's effect on a position and must run exactly once per trade. The guard is DURABLE
	// and cross-process: ApplyAsync claims each trade_id in the processedtrades ledger table inside the SAME
	// transaction as the position write (see ApplyAsync). An in-memory per-instance HashSet was insufficient -
	// a second service instance, a process restart, or a concurrent caller would not see it and would re-apply
	// the trade, double-counting the position - so there is deliberately NO process-local dedup state here and
	// no lock field; per-(portfolio,security) serialization is a transaction-scoped advisory lock in the DB.

	// Injectable UTC clock: mirrors the SQL SYSUTCDATETIME() / Postgres now() at time zone 'utc' time source
	// used to stamp positions.updated_date and processedtrades.applied_date, and makes those timestamps
	// deterministic under test.
	private readonly Func<DateTime> _utcNow;

	/// <summary>
	/// Initializes a new instance of the <see cref="PositionRecalculationService"/> class.
	/// </summary>
	/// <param name="utcNow">
	/// Optional UTC clock used to stamp <c>positions.updated_date</c>. When <see langword="null"/> the service
	/// uses <see cref="DateTime.UtcNow"/>. Supplying a fixed clock makes tests deterministic.
	/// </param>
	public PositionRecalculationService(Func<DateTime> utcNow = null)
	{
		_utcNow = utcNow ?? (() => DateTime.UtcNow);
	}

	/// <summary>
	/// Recomputes a position's signed quantity, average price and realized P&amp;L after a single trade using
	/// average-cost accounting. Pure and database-free: a faithful port of the arithmetic in the retired
	/// <c>dbo.usp_RecalculatePositionOnTrade</c> procedure, reused by both <see cref="ApplyAsync"/> and the
	/// parity tests.
	/// </summary>
	/// <param name="existingQty">Current signed position quantity (positive long, negative short, zero flat).</param>
	/// <param name="existingAvgPrice">Current volume-weighted average price of the open position.</param>
	/// <param name="existingRealizedPnl">Realized P&amp;L accumulated so far.</param>
	/// <param name="side">Trade side: <c>"B"</c> for buy (positive); any other value is treated as a sell (negative), matching the SQL side sign.</param>
	/// <param name="tradeQty">Trade quantity (always positive, as stored in the schema).</param>
	/// <param name="tradePrice">Trade execution price.</param>
	/// <returns>The recomputed <c>(Qty, AvgPrice, RealizedPnl)</c> triple.</returns>
	public static (decimal Qty, decimal AvgPrice, decimal RealizedPnl) Recalculate(
		decimal existingQty, decimal existingAvgPrice, decimal existingRealizedPnl,
		string side, decimal tradeQty, decimal tradePrice)
	{
		// A zero-quantity trade has no effect on the position: return the inputs unchanged. This guard makes
		// the pure method TOTAL for every input - in particular a zero-qty trade FROM A FLAT position
		// (existingQty == 0) would otherwise reach the weighted-average branch below and divide by
		// ABS(newQty) == 0, throwing DivideByZeroException. It is defensive: the trades table's CHECK (qty > 0)
		// makes a zero-qty trade unreachable through ApplyAsync, but this hardens the method for direct callers
		// and tests and does not change any tradeQty > 0 result (QA INFO 1).
		if (tradeQty == 0m)
			return (existingQty, existingAvgPrice, existingRealizedPnl);

		// Signed trade quantity: 'B' (Buy) is positive, 'S' (Sell) is negative (SQL side sign).
		var tradeSignedQty = side == "B" ? tradeQty : -tradeQty;

		decimal newQty;
		decimal newAvgPrice;
		var newRealizedPnl = existingRealizedPnl;

		if (existingQty == 0m || Math.Sign(existingQty) == Math.Sign(tradeSignedQty))
		{
			// Same-sign or flat: adding to (or opening) the position => weighted-average the price in.
			newQty = existingQty + tradeSignedQty;
			// newQty cannot be 0 here (a same-sign sum or an open from flat), so ABS(newQty) != 0.
			newAvgPrice = (Math.Abs(existingQty) * existingAvgPrice + tradeQty * tradePrice) / Math.Abs(newQty);
		}
		else
		{
			// Opposite side: the trade works against the existing position, so realize P&L on the closed portion.
			var closingQty = Math.Min(Math.Abs(existingQty), tradeQty); // SQL CASE = MIN(ABS(existingQty), tradeQty)
			var remainingQty = tradeQty - closingQty;
			// Realized P&L on the closed portion; SIGN(existingQty) gives the direction of the closed lot.
			newRealizedPnl = existingRealizedPnl + closingQty * (tradePrice - existingAvgPrice) * Math.Sign(existingQty);

			if (remainingQty == 0m)
			{
				// Partial or exact close (did NOT flip): keep the average price while a position stays open,
				// unless the position is now fully flat, in which case the average price is meaningless (0).
				newQty = existingQty + tradeSignedQty;
				newAvgPrice = newQty == 0m ? 0m : existingAvgPrice;
			}
			else
			{
				// Fully closed AND flipped to the other side: the residual opens a new position on the other
				// side, so it takes the incoming trade price as its average.
				newQty = Math.Sign(tradeSignedQty) * remainingQty;
				newAvgPrice = tradePrice;
			}
		}

		return (newQty, newAvgPrice, newRealizedPnl);
	}

	/// <summary>
	/// Applies a single trade's effect to the position for the trade's order, on the gateway's already-open
	/// connection: reads the order and the current position, recomputes via <see cref="Recalculate"/>, and
	/// upserts the position. Runs its effect exactly once per <paramref name="tradeId"/>.
	/// </summary>
	/// <param name="connection">
	/// An already-open <see cref="NpgsqlConnection"/> owned by the caller. The caller owns the connection's
	/// lifetime — this service never opens, closes, or disposes it — but this method DOES begin and commit its
	/// own short transaction on that connection so the durable single-apply claim, the per-(portfolio,security)
	/// serialization lock, the position read, and the position upsert form one atomic, serialized unit. The
	/// connection must therefore not already have an ambient transaction open when this is called.
	/// </param>
	/// <param name="orderId">Identifier of the order the trade executed against (<c>orders.order_id</c>).</param>
	/// <param name="tradeId">Identifier of the trade being applied; drives the single-apply guard.</param>
	/// <param name="tradeQty">Executed trade quantity (positive).</param>
	/// <param name="tradePrice">Executed trade price.</param>
	/// <param name="cancellationToken">Token used to cancel the database operations.</param>
	/// <returns>
	/// A task that completes when the position has been upserted, or immediately when the trade has already
	/// been applied.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
	/// <exception cref="InvalidOperationException">No order exists for <paramref name="orderId"/> (parity with the SQL RAISERROR).</exception>
	// The assembly is [CLSCompliant(true)], but the Npgsql provider types are not CLS-compliant. Exposing the
	// gateway's open NpgsqlConnection here is intentional (this service is the persistence seam that runs on the
	// gateway's connection/transaction), so opt this member out of CLS checking, matching the repository
	// convention for public members that surface non-CLS-compliant provider types.
	[CLSCompliant(false)]
	public async Task ApplyAsync(
		NpgsqlConnection connection,
		long orderId,
		long tradeId,
		decimal tradeQty,
		decimal tradePrice,
		CancellationToken cancellationToken = default)
	{
		if (connection is null)
			throw new ArgumentNullException(nameof(connection));

		// Own a short transaction so the durable single-apply claim, the per-(portfolio,security) advisory
		// lock, the position read, and the position UPSERT are ONE atomic, serialized unit. This is what makes
		// the single-apply invariant hold across process restarts / multiple service instances / concurrency
		// (AAP 0.6.5); a process-local guard cannot. On any exception before the commit the transaction rolls
		// back (via await using), so a failed attempt leaves NO durable claim and the trade can be retried.
		await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

		// Look up the order to resolve the (portfolio, security, side) the trade affects. Throw when the order
		// is missing rather than silently no-op, matching the SQL RAISERROR in the retired proc; the
		// transaction rolls back so nothing is persisted for a bad order id.
		int portfolioId;
		int securityId;
		string side;

		using (var orderCommand = new NpgsqlCommand(
			"SELECT portfolio_id, security_id, side FROM orders WHERE order_id = @order_id", connection, transaction))
		{
			orderCommand.Parameters.Add(new NpgsqlParameter("order_id", NpgsqlDbType.Bigint) { Value = orderId });

			// No MARS on a single Npgsql connection: fully read and dispose this reader before the next command.
			await using var orderReader = await orderCommand.ExecuteReaderAsync(cancellationToken);

			if (!await orderReader.ReadAsync(cancellationToken))
				throw new InvalidOperationException($"PositionRecalculationService: order_id {orderId} not found");

			portfolioId = orderReader.GetInt32(0);
			securityId = orderReader.GetInt32(1);
			// side is CHAR(1): read as string, trim any padding, and normalise to "B"/"S".
			side = orderReader.GetString(2).Trim();
		}

		// Serialize every apply for this (portfolio, security) with a TRANSACTION-SCOPED advisory lock, held
		// until this transaction commits/rolls back. Unlike SELECT ... FOR UPDATE it locks even when no
		// positions row exists yet, so two concurrent trades on the same instrument cannot both read the same
		// base position and lose one update (fixes the concurrent lost-update): the second apply blocks here,
		// then reads the first apply's committed result. portfolio_id/security_id are INT -> the (int,int)
		// overload of pg_advisory_xact_lock, whose two 32-bit keys uniquely identify the pair.
		using (var lockCommand = new NpgsqlCommand(
			"SELECT pg_advisory_xact_lock(@portfolio_id, @security_id)", connection, transaction))
		{
			lockCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
			lockCommand.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });
			await lockCommand.ExecuteNonQueryAsync(cancellationToken);
		}

		// DURABLE single-apply guard (AAP 0.6.5): claim this trade_id in the processedtrades ledger. ON
		// CONFLICT DO NOTHING makes a repeat a no-op regardless of which process/instance runs it - fixing the
		// cross-instance/restart double-apply that the old in-memory HashSet could not. ExecuteNonQuery returns
		// the affected-row count: 0 means the trade_id was already claimed (already applied), so commit the
		// (still-empty) transaction and return without touching the position. The claim shares this transaction
		// with the position write below, so the two commit or roll back together (retry-safe).
		using (var claimCommand = new NpgsqlCommand(
			"INSERT INTO processedtrades (trade_id, applied_date) VALUES (@trade_id, @applied_date) ON CONFLICT (trade_id) DO NOTHING",
			connection, transaction))
		{
			claimCommand.Parameters.Add(new NpgsqlParameter("trade_id", NpgsqlDbType.Bigint) { Value = tradeId });
			// applied_date is timestamp WITHOUT time zone holding UTC; normalise Kind to Unspecified (same
			// dialect rule as positions.updated_date below) and bind explicitly as NpgsqlDbType.Timestamp.
			claimCommand.Parameters.Add(new NpgsqlParameter("applied_date", NpgsqlDbType.Timestamp)
			{
				Value = DateTime.SpecifyKind(_utcNow(), DateTimeKind.Unspecified)
			});

			if (await claimCommand.ExecuteNonQueryAsync(cancellationToken) == 0)
			{
				// Already applied by an earlier (possibly crashed-then-restarted or concurrent) call: no-op.
				await transaction.CommitAsync(cancellationToken);
				return;
			}
		}

		// Load the existing position for (portfolio, security); default to a flat 0/0/0 when none exists yet
		// (the UPSERT below inserts a fresh row in that case, so a separate "exists" flag is unnecessary). The
		// read is serialized by the advisory lock above, so it observes any prior apply's committed result.
		var existingQty = 0m;
		var existingAvgPrice = 0m;
		var existingRealizedPnl = 0m;

		using (var positionCommand = new NpgsqlCommand(
			"SELECT qty, avg_price, realized_pnl FROM positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id", connection, transaction))
		{
			positionCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
			positionCommand.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });

			await using var positionReader = await positionCommand.ExecuteReaderAsync(cancellationToken);

			if (await positionReader.ReadAsync(cancellationToken))
			{
				// NUMERIC(18,4) columns -> decimal (never double) to preserve scale and the >= semantics downstream.
				existingQty = positionReader.GetDecimal(0);
				existingAvgPrice = positionReader.GetDecimal(1);
				existingRealizedPnl = positionReader.GetDecimal(2);
			}
		}

		// Average-cost recompute via the shared pure method (the single source of truth for the math).
		var (newQty, newAvgPrice, newRealizedPnl) = Recalculate(
			existingQty, existingAvgPrice, existingRealizedPnl, side, tradeQty, tradePrice);

		// UPSERT the position on the positions (portfolio_id, security_id) unique constraint.
		// unrealized_pnl requires a live market price and is end-of-day only; the proc never maintained it.
		// The INSERT path seeds it to 0; the DO UPDATE path deliberately OMITS it so an existing value is left
		// UNTOUCHED (AAP 0.6.5, matching the Positions comment in 001_Schema.sql). The absolute SET (rather than
		// an additive delta) is correct here because the advisory lock serializes the whole read-recompute-write
		// per (portfolio, security), so newQty already reflects any concurrent apply's committed result.
		const string upsertSql =
			"INSERT INTO positions (portfolio_id, security_id, qty, avg_price, realized_pnl, unrealized_pnl, updated_date) " +
			"VALUES (@portfolio_id, @security_id, @qty, @avg_price, @realized_pnl, 0, @updated_date) " +
			"ON CONFLICT (portfolio_id, security_id) DO UPDATE " +
			"SET qty = EXCLUDED.qty, " +
			"avg_price = EXCLUDED.avg_price, " +
			"realized_pnl = EXCLUDED.realized_pnl, " +
			"updated_date = EXCLUDED.updated_date";

		using (var upsertCommand = new NpgsqlCommand(upsertSql, connection, transaction))
		{
			upsertCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
			upsertCommand.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });
			// qty / avg_price / realized_pnl are NUMERIC(18,4): bind as decimal (NpgsqlDbType.Numeric).
			upsertCommand.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = newQty });
			upsertCommand.Parameters.Add(new NpgsqlParameter("avg_price", NpgsqlDbType.Numeric) { Value = newAvgPrice });
			upsertCommand.Parameters.Add(new NpgsqlParameter("realized_pnl", NpgsqlDbType.Numeric) { Value = newRealizedPnl });
			// positions.updated_date is timestamp WITHOUT time zone holding UTC; Npgsql rejects a DateTime with
			// Kind=Utc bound to that type, so normalise to Unspecified and bind explicitly as NpgsqlDbType.Timestamp.
			upsertCommand.Parameters.Add(new NpgsqlParameter("updated_date", NpgsqlDbType.Timestamp)
			{
				Value = DateTime.SpecifyKind(_utcNow(), DateTimeKind.Unspecified)
			});

			await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
		}

		// Commit: the durable trade claim and the position write become visible together, and the advisory
		// lock is released, letting a serialized concurrent apply for the same (portfolio, security) proceed.
		await transaction.CommitAsync(cancellationToken);
	}
}
