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
/// a single-apply invariant. This service still guards against an accidental double invocation for the same
/// trade id (AAP 0.6.5).
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
	// sole applier of a trade's effect on a position. This set records trade ids that have already been
	// applied, so an accidental second call for the same trade becomes an idempotent no-op. It is in-memory
	// and per service instance, which is sufficient because the gateway constructs one service and calls
	// ApplyAsync exactly once per inserted trade. It is guarded by _sync so a shared instance stays correct.
	private readonly HashSet<long> _appliedTradeIds = [];
	private readonly object _sync = new();

	// Injectable UTC clock: mirrors the SQL SYSUTCDATETIME() / Postgres now() at time zone 'utc' time source
	// used to stamp positions.updated_date, and makes that timestamp deterministic under test.
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
	/// An already-open <see cref="NpgsqlConnection"/> owned by the caller. This service never opens, closes,
	/// disposes, or begins/commits a transaction on it — the gateway owns the connection and transaction so
	/// the read and the upsert stay consistent, mirroring the procedure's transactional context.
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

		// SINGLE-APPLY invariant: the position-recalc DB trigger was removed, so this service is the
		// sole applier and must run exactly once per trade. Guard against an accidental double
		// invocation for the same trade_id (idempotent no-op on repeat). The set is recorded only
		// AFTER a successful apply, so a failed attempt can still be retried. (AAP 0.6.5)
		lock (_sync)
		{
			if (_appliedTradeIds.Contains(tradeId))
				return;
		}

		// Look up the order to resolve the (portfolio, security, side) the trade affects. Throw when the
		// order is missing rather than silently no-op, matching the SQL RAISERROR in the retired proc.
		int portfolioId;
		int securityId;
		string side;

		using (var orderCommand = new NpgsqlCommand(
			"SELECT portfolio_id, security_id, side FROM orders WHERE order_id = @order_id", connection))
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

		// Load the existing position for (portfolio, security); default to a flat 0/0/0 when none exists yet
		// (the UPSERT below inserts a fresh row in that case, so a separate "exists" flag is unnecessary).
		var existingQty = 0m;
		var existingAvgPrice = 0m;
		var existingRealizedPnl = 0m;

		using (var positionCommand = new NpgsqlCommand(
			"SELECT qty, avg_price, realized_pnl FROM positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id", connection))
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
		// UNTOUCHED (AAP 0.6.5, matching the Positions comment in 001_Schema.sql).
		const string upsertSql =
			"INSERT INTO positions (portfolio_id, security_id, qty, avg_price, realized_pnl, unrealized_pnl, updated_date) " +
			"VALUES (@portfolio_id, @security_id, @qty, @avg_price, @realized_pnl, 0, @updated_date) " +
			"ON CONFLICT (portfolio_id, security_id) DO UPDATE " +
			"SET qty = EXCLUDED.qty, " +
			"avg_price = EXCLUDED.avg_price, " +
			"realized_pnl = EXCLUDED.realized_pnl, " +
			"updated_date = EXCLUDED.updated_date";

		using (var upsertCommand = new NpgsqlCommand(upsertSql, connection))
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

		// Record the trade as applied only AFTER the successful UPSERT, so a failed attempt can be retried.
		lock (_sync)
		{
			_appliedTradeIds.Add(tradeId);
		}
	}
}
