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
/// <c>INSERT ... RETURNING trade_id</c>) and, as a second integrity check, verifies the trade actually
/// BELONGS to the order it is being applied against, deriving the applied qty/price from the stored trade row
/// rather than from caller-supplied values (C1). The per-trade_id guard dedups a replay of the SAME persisted
/// trade; to additionally dedup the SAME real-world fill reported twice as two separate gateway calls, the
/// gateway exposes a 4-argument <c>RecordTradeAsync</c> overload that carries a stable external fill key
/// (<c>trades.external_trade_id</c>, a partial-unique column) and collapses a retry to a single persisted
/// trade via <c>INSERT ... ON CONFLICT DO NOTHING</c> (C2). The original 3-argument gateway contract is
/// preserved unchanged (AAP 0.7.1); the external key is an additive opt-in overload.
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

	// Injectable UTC clock OVERRIDE for positions.updated_date. NULL (the default) means "use the DATABASE
	// transaction time": the position timestamp is then written by the same now() at time zone 'utc' server
	// clock that stamps trades.executed_date and orders.submitted_date, so one order/trade/position timeline
	// cannot be skewed by a divergent application-host clock (M3). A non-null clock OVERRIDES that with an
	// app-supplied value purely to make tests deterministic; production leaves it null and trusts the DB clock.
	private readonly Func<DateTime> _utcNow;

	/// <summary>
	/// Initializes a new instance of the <see cref="PositionRecalculationService"/> class.
	/// </summary>
	/// <param name="utcNow">
	/// Optional UTC clock OVERRIDE used to stamp <c>positions.updated_date</c>. When <see langword="null"/>
	/// (the default) the service stamps the position from the DATABASE transaction clock
	/// (<c>now() at time zone 'utc'</c>), so the timestamp shares the server clock with the trade and cannot be
	/// skewed by the application host's clock (M3). Supplying a fixed clock overrides this to make tests
	/// deterministic.
	/// </param>
	public PositionRecalculationService(Func<DateTime> utcNow = null)
	{
		// Deliberately NOT defaulted to DateTime.UtcNow: a null clock selects the DB-time SQL path in ApplyAsync.
		_utcNow = utcNow;
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
	/// Applies a single PERSISTED trade's effect to the position for the trade's order, on the gateway's
	/// already-open connection and WITHIN the gateway's transaction. The trade's quantity, price and owning
	/// order are read back FROM THE DATABASE (never trusted from the caller): the method atomically claims the
	/// trade by BOTH <paramref name="tradeId"/> AND <paramref name="orderId"/> - so a trade can only ever be
	/// applied to the order it actually belongs to - then reads the current position, recomputes via
	/// <see cref="Recalculate"/>, and upserts the position. Enrolls in the caller's transaction so the trade
	/// row and this position write commit together or not at all (atomicity, AAP 0.6.5).
	/// </summary>
	/// <param name="connection">
	/// An already-open <see cref="NpgsqlConnection"/> owned by the caller. This service never opens, closes,
	/// or disposes it.
	/// </param>
	/// <param name="transaction">
	/// The caller's in-flight <see cref="NpgsqlTransaction"/> on <paramref name="connection"/>. This service
	/// runs every command on it but NEVER begins, commits, or rolls it back - the gateway owns that lifecycle,
	/// so the trade INSERT and this position write form ONE atomic unit and the per-portfolio advisory lock
	/// taken here is held until the gateway commits/rolls back.
	/// </param>
	/// <param name="orderId">
	/// Identifier of the order the trade MUST belong to (<c>orders.order_id</c>). The atomic claim verifies
	/// <c>trades.order_id = orderId</c>, so a mismatched pair is rejected rather than silently posting the
	/// trade to another order's position (C1).
	/// </param>
	/// <param name="tradeId">
	/// Identifier of the PERSISTED trade to apply. Drives the DURABLE single-apply guard
	/// (<c>trades.position_applied</c>, a database column - NOT an in-process flag): a replay of an
	/// already-applied trade is an idempotent no-op that survives process restarts and holds across instances.
	/// </param>
	/// <param name="cancellationToken">Token used to cancel the database operations.</param>
	/// <returns>
	/// A task that completes when the position has been upserted, or immediately when this
	/// <paramref name="tradeId"/> has already been applied (durable idempotent no-op).
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="transaction"/> is <see langword="null"/>.</exception>
	/// <exception cref="InvalidOperationException">
	/// No order exists for <paramref name="orderId"/> (parity with the SQL RAISERROR); OR the trade
	/// <paramref name="tradeId"/> does not exist; OR the trade exists but belongs to a DIFFERENT order than
	/// <paramref name="orderId"/> (the C1 mismatch this method rejects).
	/// </exception>
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
		CancellationToken cancellationToken = default)
	{
		if (connection is null)
			throw new ArgumentNullException(nameof(connection));

		if (transaction is null)
			throw new ArgumentNullException(nameof(transaction));

		// Every command below runs on the caller's connection + transaction; this method NEVER begins, commits,
		// or rolls back (the gateway owns that), so the trade INSERT and this position write are one atomic unit.
		// C1: the applied qty/price and the trade->order linkage are read back FROM THE DATABASE below (the
		// atomic claim), never trusted from the caller, so a caller can neither post arbitrary values to a
		// position nor apply a trade to an order it does not belong to.

		// (1) Resolve the ORDER the trade must belong to. portfolio_id / security_id / side are properties of
		// the ORDER (the trades table has no side/portfolio/security of its own), so they are read here from
		// the order the caller names. A missing order throws (parity with the retired proc's RAISERROR); the
		// caller's transaction then rolls back so nothing is persisted for a bad order id.
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

		// (3) ATOMIC CLAIM + DERIVE (C1 + durable single-apply). One UPDATE both claims the trade for position
		// application (flips trades.position_applied FALSE -> TRUE) AND reads the PERSISTED qty/price straight
		// back via RETURNING. The WHERE requires trade_id = @trade_id AND order_id = @order_id, so the trade is
		// proven to belong to the caller's order before anything is posted; binding the applied qty/price to the
		// stored NUMERIC(18,4) row (not to caller-supplied values) means a caller can neither post arbitrary
		// values to a position nor cross-post a trade onto another order's position. The UPDATE is atomic under
		// the row lock and, together with the per-portfolio advisory lock above, collapses concurrent/duplicate
		// applies of the SAME trade to a single winner - the loser sees position_applied already TRUE, matches 0
		// rows, and no-ops. This is the DURABLE guard (a database column), which survives process restarts and
		// coordinates across instances - NOT the former in-process HashSet.
		decimal persistedQty;
		decimal persistedPrice;
		bool claimed;

		using (var claimCommand = new NpgsqlCommand(
			"UPDATE trades SET position_applied = TRUE " +
			"WHERE trade_id = @trade_id AND order_id = @order_id AND NOT position_applied " +
			"RETURNING qty, price", connection, transaction))
		{
			claimCommand.Parameters.Add(new NpgsqlParameter("trade_id", NpgsqlDbType.Bigint) { Value = tradeId });
			claimCommand.Parameters.Add(new NpgsqlParameter("order_id", NpgsqlDbType.Bigint) { Value = orderId });

			// No MARS on a single Npgsql connection: fully read and dispose this reader before the next command.
			await using var claimReader = await claimCommand.ExecuteReaderAsync(cancellationToken);

			claimed = await claimReader.ReadAsync(cancellationToken);

			// NUMERIC(18,4) -> decimal (never double) to preserve scale and the >= semantics downstream.
			persistedQty = claimed ? claimReader.GetDecimal(0) : 0m;
			persistedPrice = claimed ? claimReader.GetDecimal(1) : 0m;
		}

		// (4) DISAMBIGUATE a zero-row claim (C1). The claim matched nothing for exactly one of three reasons,
		// and each must be told apart rather than lumped into a silent no-op:
		//   (a) the trade does not exist at all               -> data/programming error, THROW;
		//   (b) the trade exists but belongs to another order -> the adversarial mismatch this fix targets,
		//                                                         THROW so nothing posts to the wrong position;
		//   (c) the trade exists, belongs to THIS order, and was ALREADY applied -> the legitimate durable
		//                                                         idempotent replay, RETURN a no-op.
		// This probe SELECT runs ONLY on the (rare) zero-row path, so the happy path stays a single round trip.
		if (!claimed)
		{
			using var probeCommand = new NpgsqlCommand(
				"SELECT order_id, position_applied FROM trades WHERE trade_id = @trade_id", connection, transaction);
			probeCommand.Parameters.Add(new NpgsqlParameter("trade_id", NpgsqlDbType.Bigint) { Value = tradeId });

			await using var probeReader = await probeCommand.ExecuteReaderAsync(cancellationToken);

			if (!await probeReader.ReadAsync(cancellationToken))
				throw new InvalidOperationException(
					$"PositionRecalculationService: trade_id {tradeId} not found");

			var actualOrderId = probeReader.GetInt64(0);

			if (actualOrderId != orderId)
				throw new InvalidOperationException(
					$"PositionRecalculationService: trade_id {tradeId} belongs to order_id {actualOrderId}, not {orderId}");

			// Belongs to this order and was already applied (or was concurrently claimed under the same advisory
			// lock): the durable guard has already done its job - idempotent no-op.
			return;
		}

		// (5) Load the existing position for (portfolio, security); default to a flat 0/0/0 when none exists yet
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

		// (6) Average-cost recompute via the shared pure method (the single source of truth for the math), on
		// the PERSISTED trade values (C1). Recalculate re-validates side / qty > 0 / price > 0, so even a
		// corrupt persisted trade row cannot mis-post a position - it throws instead.
		var (newQty, newAvgPrice, newRealizedPnl) = Recalculate(
			existingQty, existingAvgPrice, existingRealizedPnl, side, persistedQty, persistedPrice);

		// (7) UPSERT the position on the positions (portfolio_id, security_id) unique constraint.
		// unrealized_pnl requires a live market price and is end-of-day only; the proc never maintained it, so
		// the INSERT path seeds it to 0 and the DO UPDATE path deliberately OMITS it (existing value left
		// UNTOUCHED, AAP 0.6.5). qty / avg_price / realized_pnl use an absolute SET (the advisory lock serializes
		// read-recompute-write per portfolio, so newQty already reflects any concurrent apply's committed
		// result). C5 fix: there is deliberately NO cumulative_gross_notional rollup column here - accumulating
		// per-trade gross into a NUMERIC(18,4) store rounds each addend to 4dp and can drift from the true total,
		// silently loosening the pre-trade commission gate. The gate instead computes exact SUM(t.qty * t.price)
		// straight from the trades rows (PreTradeRiskService), so no lossy rollup is maintained.
		// M3 (time semantics): by DEFAULT updated_date is stamped from the DATABASE transaction clock
		// (now() at time zone 'utc') - the SAME server clock as trades.executed_date / orders.submitted_date -
		// so a divergent application-host clock cannot skew the position's timeline. Only when a clock was
		// injected (deterministic tests) is an app value bound instead. One UPSERT serves both paths.
		var updatedDateSql = _utcNow is null ? "now() at time zone 'utc'" : "@updated_date";
		var upsertSql =
			"INSERT INTO positions (portfolio_id, security_id, qty, avg_price, realized_pnl, unrealized_pnl, updated_date) " +
			$"VALUES (@portfolio_id, @security_id, @qty, @avg_price, @realized_pnl, 0, {updatedDateSql}) " +
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
			// On the injected-clock path only, bind the app timestamp. positions.updated_date is timestamp
			// WITHOUT time zone holding UTC; Npgsql rejects a DateTime with Kind=Utc bound to that type, so
			// normalise to Unspecified and bind explicitly as NpgsqlDbType.Timestamp. The default (null-clock)
			// path binds no parameter and lets the DB clock write the column.
			if (_utcNow is not null)
			{
				upsertCommand.Parameters.Add(new NpgsqlParameter("updated_date", NpgsqlDbType.Timestamp)
				{
					Value = DateTime.SpecifyKind(_utcNow(), DateTimeKind.Unspecified)
				});
			}

			await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
		}
	}
}
