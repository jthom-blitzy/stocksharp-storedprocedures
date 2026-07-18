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
/// and the data-access gateway inserts each trade with a fresh unique <c>trade_id</c> and calls
/// <see cref="ApplyAsync"/> exactly once for it, WITHIN the same gateway-owned transaction that carries the
/// trade INSERT, so a trade and its position effect commit together or not at all.
/// </para>
/// <para>
/// Single-apply is enforced DURABLY, not by an in-process guard: each trades row carries a
/// <c>position_applied</c> flag, and <see cref="ApplyAsync"/> flips it FALSE-&gt;TRUE with a conditional
/// <c>UPDATE ... WHERE trade_id = @id AND NOT position_applied</c> in the SAME transaction as the position
/// write. If that UPDATE affects zero rows the trade was already applied, so the call is an idempotent
/// no-op. Because the flag is persisted and marked in the same commit, the guarantee is exactly-once PER
/// PERSISTED trade_id and survives process restarts and holds across instances (a replay of a committed
/// trade_id sees TRUE), and a rolled-back attempt undoes both the mark and the position write together.
/// <see cref="ApplyAsync"/> therefore requires a PERSISTED trade_id (the gateway guarantees this via
/// <c>INSERT ... RETURNING trade_id</c>). NOTE (boundary): the guarantee is keyed on the DB trade_id, not
/// on an external fill id; if the SAME real-world fill were reported twice as two separate gateway calls it
/// would receive two distinct trade_ids and apply twice. De-duplicating that would need a stable external
/// idempotency key in the gateway signature, which the frozen public contract (AAP 0.7.1) does not permit.
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

	// Injectable UTC clock: mirrors the SQL SYSUTCDATETIME() / Postgres now() at time zone 'utc' time source
	// used to stamp positions.updated_date, and makes those timestamps deterministic under test.
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
	/// <param name="side">Trade side: <c>"B"</c> for buy (positive) or <c>"S"</c> for sell (negative), matching the SQL side sign.</param>
	/// <param name="tradeQty">Trade quantity; must be strictly positive (parity with the Trades <c>CHECK (qty &gt; 0)</c> constraint).</param>
	/// <param name="tradePrice">Trade execution price; must be strictly positive (parity with the Trades <c>CHECK (price &gt; 0)</c> constraint).</param>
	/// <returns>The recomputed <c>(Qty, AvgPrice, RealizedPnl)</c> triple.</returns>
	/// <exception cref="ArgumentException"><paramref name="side"/> is neither <c>"B"</c> nor <c>"S"</c>.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="tradeQty"/> or <paramref name="tradePrice"/> is not strictly positive.</exception>
	public static (decimal Qty, decimal AvgPrice, decimal RealizedPnl) Recalculate(
		decimal existingQty, decimal existingAvgPrice, decimal existingRealizedPnl,
		string side, decimal tradeQty, decimal tradePrice)
	{
		// Validate the trade BEFORE any arithmetic (F4 / CWE-20). The retired SQL proc relied on the Trades
		// CHECK constraints (side IN ('B','S'), qty > 0, price > 0); porting the logic to C# without porting
		// those guards would let a malformed trade silently mis-compute a position - e.g. an unrecognised side
		// being treated as a sell, or a zero/negative qty reaching the weighted-average branch below and
		// dividing by ABS(newQty) == 0. Reject such input explicitly rather than returning a misleading no-op.
		if (side != "B" && side != "S")
			throw new ArgumentException($"Trade side must be \"B\" (buy) or \"S\" (sell); got \"{side ?? "<null>"}\".", nameof(side));

		if (tradeQty <= 0m)
			throw new ArgumentOutOfRangeException(nameof(tradeQty), tradeQty, "Trade quantity must be strictly positive.");

		if (tradePrice <= 0m)
			throw new ArgumentOutOfRangeException(nameof(tradePrice), tradePrice, "Trade price must be strictly positive.");

		// Signed trade quantity: 'B' (Buy) is positive, 'S' (Sell) is negative (SQL side sign).
		var tradeSignedQty = side == "B" ? tradeQty : -tradeQty;

		decimal newQty;
		decimal newAvgPrice;
		var newRealizedPnl = existingRealizedPnl;

		if (existingQty == 0m || Math.Sign(existingQty) == Math.Sign(tradeSignedQty))
		{
			// Same-sign or flat: adding to (or opening) the position => weighted-average the price in.
			newQty = existingQty + tradeSignedQty;
			// newQty cannot be 0 here (a same-sign sum, or an open from flat with tradeQty > 0), so ABS(newQty) != 0.
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
	/// connection and WITHIN the gateway's transaction: reads the order and the current position, recomputes
	/// via <see cref="Recalculate"/>, and upserts the position. Enrolls in the caller's transaction so the
	/// trade row and this position write commit together or not at all (atomicity, AAP 0.6.5).
	/// </summary>
	/// <param name="connection">
	/// An already-open <see cref="NpgsqlConnection"/> owned by the caller. This service never opens, closes,
	/// or disposes it.
	/// </param>
	/// <param name="transaction">
	/// The caller's in-flight <see cref="NpgsqlTransaction"/> on <paramref name="connection"/>. This service
	/// runs every command on it but NEVER begins, commits, or rolls it back - the gateway owns that lifecycle,
	/// so the trade INSERT and this position write form ONE atomic unit and the per-(portfolio,security)
	/// advisory lock taken here is held until the gateway commits/rolls back.
	/// </param>
	/// <param name="orderId">Identifier of the order the trade executed against (<c>orders.order_id</c>).</param>
	/// <param name="tradeId">Identifier of the trade being applied; drives the in-process single-apply guard.</param>
	/// <param name="tradeQty">Executed trade quantity; must be strictly positive.</param>
	/// <param name="tradePrice">Executed trade price; must be strictly positive.</param>
	/// <param name="cancellationToken">Token used to cancel the database operations.</param>
	/// <returns>
	/// A task that completes when the position has been upserted, or immediately when this <paramref name="tradeId"/>
	/// has already been applied within this process.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="transaction"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="tradeQty"/> or <paramref name="tradePrice"/> is not strictly positive.</exception>
	/// <exception cref="InvalidOperationException">No order exists for <paramref name="orderId"/> (parity with the SQL RAISERROR).</exception>
	// The assembly is [CLSCompliant(true)], but the Npgsql provider types are not CLS-compliant. Exposing the
	// gateway's open NpgsqlConnection/NpgsqlTransaction here is intentional (this service is the persistence seam
	// that runs on the gateway's connection/transaction), so opt this member out of CLS checking, matching the
	// repository convention for public members that surface non-CLS-compliant provider types.
	[CLSCompliant(false)]
	public async Task ApplyAsync(
		NpgsqlConnection connection,
		NpgsqlTransaction transaction,
		long orderId,
		long tradeId,
		decimal tradeQty,
		decimal tradePrice,
		CancellationToken cancellationToken = default)
	{
		if (connection is null)
			throw new ArgumentNullException(nameof(connection));

		if (transaction is null)
			throw new ArgumentNullException(nameof(transaction));

		// Reject a malformed fill before any DB work (F4, parity with the Trades CHECK qty > 0 / price > 0).
		if (tradeQty <= 0m)
			throw new ArgumentOutOfRangeException(nameof(tradeQty), tradeQty, "Trade quantity must be strictly positive.");

		if (tradePrice <= 0m)
			throw new ArgumentOutOfRangeException(nameof(tradePrice), tradePrice, "Trade price must be strictly positive.");

		// Every command below runs on the caller's connection + transaction; this method NEVER begins, commits,
		// or rolls back (the gateway owns that), so the trade INSERT and this position write are one atomic unit.
		// The DURABLE single-apply guard (the conditional UPDATE of trades.position_applied) lives AFTER
		// order-resolution and the advisory lock below - see that block for the full C3 idempotency contract.

		// Look up the order to resolve the (portfolio, security, side) the trade affects. Throw when the order
		// is missing rather than silently no-op, matching the SQL RAISERROR in the retired proc; the caller's
		// transaction then rolls back so nothing is persisted for a bad order id.
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

		// Serialize every apply for this portfolio with a TRANSACTION-SCOPED advisory lock on the caller's
		// transaction, held until the gateway commits/rolls back. Unlike SELECT ... FOR UPDATE it locks even when
		// no positions row exists yet, so two concurrent trades cannot both read the same base position and lose
		// one update (fixes the concurrent lost-update): the second apply blocks here, then reads the first
		// apply's committed result. C4 fix: this uses the SAME single-key pg_advisory_xact_lock(bigint) space,
		// keyed on portfolio_id, as the pre-trade gate in SqlLegacyOrderGateway.SubmitOrderAsync. The previous
		// (int,int) overload keyed on (portfolio, security) occupied a DIFFERENT PostgreSQL advisory-lock space
		// from the gate's single-key lock, so a submit and a recalculate on the same portfolio did NOT mutually
		// exclude; unifying on the one-arg bigint overload (portfolio_id widened to bigint) restores a single
		// coherent lock hierarchy per portfolio across both services.
		using (var lockCommand = new NpgsqlCommand(
			"SELECT pg_advisory_xact_lock(@portfolio_id)", connection, transaction))
		{
			lockCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Bigint) { Value = (long)portfolioId });
			await lockCommand.ExecuteNonQueryAsync(cancellationToken);
		}

		// DURABLE single-apply guard (C3): atomically claim this persisted trade for position application by
		// flipping trades.position_applied FALSE -> TRUE in the caller's transaction. Because the row is locked
		// by this UPDATE until the gateway commits, and because the portfolio advisory lock above serializes
		// same-portfolio applies, a concurrent or repeated apply of the SAME trade_id (another instance, a
		// restart, or a retry) sees the row already TRUE and the UPDATE affects 0 rows -> idempotent no-op
		// return, so the position is applied EXACTLY ONCE per persisted trade_id and the effect commits in the
		// same transaction as the flag. This replaces the former in-process HashSet, which could not survive a
		// process restart nor coordinate across instances. The trade row must already be persisted (the gateway
		// INSERTs it immediately before calling this), so a 0-row result here means "already applied", never
		// "unknown trade". Residual boundary: the SAME external fill reported twice becomes two DISTINCT
		// trade_ids and both apply - de-duplicating that needs an external-fill id the frozen public contract
		// (AAP 0.7.1) does not carry, so it is intentionally out of scope.
		using (var claimCommand = new NpgsqlCommand(
			"UPDATE trades SET position_applied = TRUE WHERE trade_id = @trade_id AND NOT position_applied", connection, transaction))
		{
			claimCommand.Parameters.Add(new NpgsqlParameter("trade_id", NpgsqlDbType.Bigint) { Value = tradeId });

			var claimed = await claimCommand.ExecuteNonQueryAsync(cancellationToken);

			if (claimed == 0)
				return;
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
		// unrealized_pnl requires a live market price and is end-of-day only; the proc never maintained it, so
		// the INSERT path seeds it to 0 and the DO UPDATE path deliberately OMITS it (existing value left
		// UNTOUCHED, AAP 0.6.5). qty / avg_price / realized_pnl use an absolute SET (the advisory lock serializes
		// read-recompute-write per portfolio, so newQty already reflects any concurrent apply's committed
		// result). C5 fix: there is deliberately NO cumulative_gross_notional rollup column here - accumulating
		// per-trade gross into a NUMERIC(18,4) store rounds each addend to 4dp and can drift from the true total,
		// silently loosening the pre-trade commission gate. The gate instead computes exact SUM(t.qty * t.price)
		// straight from the trades rows (PreTradeRiskService), so no lossy rollup is maintained.
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
	}
}
