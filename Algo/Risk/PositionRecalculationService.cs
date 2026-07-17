namespace StockSharp.Algo.Risk;

using System.Data;

using Microsoft.Data.SqlClient;

/// <summary>
/// Recomputes a portfolio/security position (quantity, average price and
/// realized P&amp;L) from a single executed trade using standard average-cost
/// accounting.
/// </summary>
/// <remarks>
/// <para>
/// C# replacement for the SQL proc dbo.usp_RecalculatePositionOnTrade and the
/// trg_Trades_PositionRecalc trigger. The trigger auto-recomputed on every Trades
/// insert while the proc was ALSO exposed standalone, so invoking both double-counted
/// a trade (see LEGACY_LAYER.md). This service is the single, unambiguous source of
/// recompute: the gateway calls the transaction-aware
/// <see cref="RecalculateAsync(SqlConnection, SqlTransaction, long, decimal, decimal, CancellationToken)"/>
/// exactly once per trade, inside the very transaction that inserts the trade, so the
/// trade row and its position effect commit or roll back together.
/// </para>
/// <para>
/// The gateway calls this service exactly once per trade insert, in the same
/// transaction as the insert, so a trade and its position effect are one atomic unit.
/// There is no auto-recompute trigger and no standalone mutator overload, so there is
/// no second recompute path to double-count a single fill against (AAP 0.6.5). This
/// service does not itself provide replay/idempotency protection: each call applies
/// the trade it is given, and it is the caller's responsibility not to record the same
/// logical fill twice.
/// </para>
/// <para>
/// The position row is read under WITH (UPDLOCK, HOLDLOCK) so concurrent fills for
/// the same portfolio/security serialize and simultaneous first fills cannot race on
/// the unique key. Trade inputs and the computed quantity/average-price/realized-P&amp;L
/// outputs are validated against the schema's DECIMAL(18,4) range, so an out-of-range
/// value fails closed with a clear error instead of overflowing the arithmetic or the
/// column on write (MJ-7). Writes use explicit DECIMAL(18,4) parameters and assert the
/// affected-row count, so a missing/duplicate row surfaces as an error rather than a
/// silent no-op. unrealized_pnl is deliberately left untouched (it needs a live
/// market price - see dbo.Positions in 001_Schema.sql); a freshly inserted row
/// starts at 0.
/// </para>
/// </remarks>
public class PositionRecalculationService
{
	// Every RiskLimits/Positions/Trades money/qty column and every variable in the
	// original proc was DECIMAL(18,4), rounded half away from zero on assignment. The
	// pure method normalizes inputs and rounds outputs to the same scale so, e.g., a
	// weighted average of 5/3 persists as 1.6667 exactly as the proc stored it.
	private static decimal Round4(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

	// Largest magnitude representable by DECIMAL(18,4) (18 digits, 4 after the point).
	// A trade input or a computed position value outside this range could never persist
	// and could overflow intermediate arithmetic, so the recompute fails closed with a
	// clear error rather than letting SQL Server throw on write or a decimal operation
	// throw mid-computation (MJ-7).
	private const decimal _maxDecimal18_4 = 99999999999999.9999m;

	// True when a value fits the schema's DECIMAL(18,4) magnitude.
	private static bool IsWithinDecimal18_4(decimal value)
		=> value >= -_maxDecimal18_4 && value <= _maxDecimal18_4;

	// Explicit DECIMAL(18,4) parameter so ADO.NET sends the value at the schema's
	// precision/scale rather than inferring a type that could shift the stored value.
	private static SqlParameter Decimal4(string name, decimal value)
		=> new(name, SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = value };

	/// <summary>
	/// Pure average-cost + realized-P&amp;L recompute for a single trade. Ported from
	/// dbo.usp_RecalculatePositionOnTrade so it can be unit-tested without a database.
	/// Inputs are validated and normalized to DECIMAL(18,4) and outputs are rounded to
	/// DECIMAL(18,4) (round-half-away-from-zero) to match the proc's stored values.
	/// </summary>
	/// <param name="existingQty">Current signed position quantity (0 if flat).</param>
	/// <param name="existingAvgPrice">Current average price (0 if flat).</param>
	/// <param name="existingRealizedPnl">Current realized P&amp;L (0 if flat).</param>
	/// <param name="side">Side of the order the trade belongs to; must be <see cref="Sides.Buy"/> or <see cref="Sides.Sell"/>.</param>
	/// <param name="tradeQty">Trade quantity; must be positive (matches the Trades.qty CHECK).</param>
	/// <param name="tradePrice">Trade price; must be positive (matches the Trades.price CHECK).</param>
	/// <returns>The new signed quantity, average price and realized P&amp;L, each at DECIMAL(18,4) scale.</returns>
	/// <exception cref="ArgumentOutOfRangeException">
	/// <paramref name="tradeQty"/> or <paramref name="tradePrice"/> is not positive, or
	/// <paramref name="side"/> is neither <see cref="Sides.Buy"/> nor <see cref="Sides.Sell"/>.
	/// </exception>
	public static (decimal Quantity, decimal AveragePrice, decimal RealizedPnl) Recalculate(
		decimal existingQty, decimal existingAvgPrice, decimal existingRealizedPnl,
		Sides side, decimal tradeQty, decimal tradePrice)
	{
		// --- input validation (fail closed before any arithmetic) ----------
		// Trades.qty and Trades.price carry CHECK (> 0) constraints; a non-positive
		// value is invalid and (for a flat position) would divide by zero.
		tradeQty = Round4(tradeQty);
		tradePrice = Round4(tradePrice);

		if (tradeQty <= 0)
			throw new ArgumentOutOfRangeException(nameof(tradeQty), tradeQty, "Trade quantity must be positive.");

		if (tradePrice <= 0)
			throw new ArgumentOutOfRangeException(nameof(tradePrice), tradePrice, "Trade price must be positive.");

		// Range-check inputs against DECIMAL(18,4) so an out-of-range fill fails closed
		// here rather than overflowing the average-cost / P&L arithmetic below or the
		// Positions columns on write (MJ-7).
		if (!IsWithinDecimal18_4(tradeQty))
			throw new ArgumentOutOfRangeException(nameof(tradeQty), tradeQty, "Trade quantity is outside the supported DECIMAL(18,4) range.");

		if (!IsWithinDecimal18_4(tradePrice))
			throw new ArgumentOutOfRangeException(nameof(tradePrice), tradePrice, "Trade price is outside the supported DECIMAL(18,4) range.");

		// Explicit Buy/Sell mapping - never silently treat an unexpected side as a sell.
		var tradeSignedQty = side switch
		{
			Sides.Buy => tradeQty,
			Sides.Sell => -tradeQty,
			_ => throw new ArgumentOutOfRangeException(nameof(side), side, "Order side must be Buy or Sell."),
		};

		// Existing values already originate from DECIMAL(18,4) columns; normalize
		// defensively so the computation runs on the same values SQL Server stored, and
		// range-check them for the same overflow safety as the trade inputs (MJ-7).
		existingQty = Round4(existingQty);
		existingAvgPrice = Round4(existingAvgPrice);
		existingRealizedPnl = Round4(existingRealizedPnl);

		if (!IsWithinDecimal18_4(existingQty))
			throw new ArgumentOutOfRangeException(nameof(existingQty), existingQty, "Existing quantity is outside the supported DECIMAL(18,4) range.");

		if (!IsWithinDecimal18_4(existingAvgPrice))
			throw new ArgumentOutOfRangeException(nameof(existingAvgPrice), existingAvgPrice, "Existing average price is outside the supported DECIMAL(18,4) range.");

		if (!IsWithinDecimal18_4(existingRealizedPnl))
			throw new ArgumentOutOfRangeException(nameof(existingRealizedPnl), existingRealizedPnl, "Existing realized P&L is outside the supported DECIMAL(18,4) range.");

		decimal newQty, newAvgPrice;
		var newRealizedPnl = existingRealizedPnl;

		if (existingQty == 0 || Math.Sign(existingQty) == Math.Sign(tradeSignedQty))
		{
			// adding to (or opening) a position: weighted-average the price in
			newQty = existingQty + tradeSignedQty;
			newAvgPrice = (Math.Abs(existingQty) * existingAvgPrice + tradeQty * tradePrice) / Math.Abs(newQty);
		}
		else
		{
			// trade works against the existing position: realize P&L on the closed portion
			var closingQty = Math.Abs(existingQty) < tradeQty ? Math.Abs(existingQty) : tradeQty;
			var remainingQty = tradeQty - closingQty;

			newRealizedPnl = existingRealizedPnl + closingQty * (tradePrice - existingAvgPrice) * Math.Sign(existingQty);

			if (remainingQty == 0)
			{
				// partial or exact close; average price is only meaningful while a position stays open
				newQty = existingQty + tradeSignedQty;
				newAvgPrice = newQty == 0 ? 0 : existingAvgPrice;
			}
			else
			{
				// fully closed and flipped: what's left of the trade opens a new position on the other side
				newQty = Math.Sign(tradeSignedQty) * remainingQty;
				newAvgPrice = tradePrice;
			}
		}

		// Round to the stored DECIMAL(18,4) scale exactly as each proc variable was.
		var resultQty = Round4(newQty);
		var resultAvgPrice = Round4(newAvgPrice);
		var resultRealizedPnl = Round4(newRealizedPnl);

		// A computed value that cannot be represented at DECIMAL(18,4) (e.g. a realized
		// P&L that overflows the column after an extreme fill) must fail closed with a
		// clear error rather than throw obscurely when the Positions row is written (MJ-7).
		if (!IsWithinDecimal18_4(resultQty))
			throw new OverflowException(FormattableString.Invariant($"Recomputed position quantity {resultQty} exceeds the supported DECIMAL(18,4) range."));

		if (!IsWithinDecimal18_4(resultAvgPrice))
			throw new OverflowException(FormattableString.Invariant($"Recomputed average price {resultAvgPrice} exceeds the supported DECIMAL(18,4) range."));

		if (!IsWithinDecimal18_4(resultRealizedPnl))
			throw new OverflowException(FormattableString.Invariant($"Recomputed realized P&L {resultRealizedPnl} exceeds the supported DECIMAL(18,4) range."));

		return (resultQty, resultAvgPrice, resultRealizedPnl);
	}

	// NOTE: there is intentionally no standalone RecalculateAsync(orderId, qty, price)
	// overload that opens its own transaction and mutates the stored position from raw
	// (qty, price) arguments. Such an overload would apply a position delta without
	// inserting - or even identifying - a Trade row, reviving exactly the double-count
	// hazard this refactor removes: the old trg_Trades_PositionRecalc trigger recomputed
	// on every Trades insert while the proc was ALSO callable standalone, so running both
	// applied one fill twice (see LEGACY_LAYER.md / AAP 0.6.5). The only supported way to
	// drive a recompute is the transaction-aware overload below, which the gateway
	// invokes exactly once inside the same transaction that inserts the trade. The pure,
	// side-effect-free Recalculate(...) above remains available for computation and unit
	// tests.

	/// <summary>
	/// Reads the order and its current position, recomputes from the given trade and
	/// persists the result using a caller-supplied open connection and transaction -
	/// the same transaction that inserts the trade, so the trade and its position
	/// effect are one atomic unit. This is the single recompute entry point per trade
	/// (the old auto-recompute trigger no longer exists); the gateway calls it exactly
	/// once per trade insert (AAP 0.6.5), so there is no second recompute path to
	/// double-count a single fill against.
	/// </summary>
	/// <param name="connection">Open SQL Server connection.</param>
	/// <param name="transaction">Transaction that also performs the trade insert.</param>
	/// <param name="orderId">Identifier of the order the trade belongs to; its portfolio, security and side drive the recompute.</param>
	/// <param name="tradeQty">Executed trade quantity (must be positive).</param>
	/// <param name="tradePrice">Executed trade price (must be positive).</param>
	/// <param name="cancellationToken">Token used to cancel the asynchronous operation.</param>
	public async Task RecalculateAsync(
		SqlConnection connection, SqlTransaction transaction,
		long orderId, decimal tradeQty, decimal tradePrice, CancellationToken cancellationToken = default)
	{
		if (connection is null)
			throw new ArgumentNullException(nameof(connection));

		if (transaction is null)
			throw new ArgumentNullException(nameof(transaction));

		int portfolioId, securityId;
		Sides side;

		await using (var orderCmd = new SqlCommand(
			"SELECT portfolio_id, security_id, side FROM dbo.Orders WHERE order_id = @order_id", connection)
		{
			Transaction = transaction,
		})
		{
			orderCmd.Parameters.AddWithValue("@order_id", orderId);

			await using var reader = await orderCmd.ExecuteReaderAsync(cancellationToken);

			if (!await reader.ReadAsync(cancellationToken))
				throw new InvalidOperationException(FormattableString.Invariant($"Order '{orderId}' not found."));

			portfolioId = reader.GetInt32(0);
			securityId = reader.GetInt32(1);

			// Explicit B/S mapping - fail closed on any unexpected persisted side.
			var sideCode = reader.GetString(2);
			side = sideCode switch
			{
				"B" => Sides.Buy,
				"S" => Sides.Sell,
				_ => throw new InvalidOperationException(FormattableString.Invariant($"Order '{orderId}' has unexpected side '{sideCode}'.")),
			};
		}

		decimal existingQty = 0, existingAvgPrice = 0, existingRealizedPnl = 0;
		var positionExists = false;

		// UPDLOCK+HOLDLOCK: take the update lock on the existing row (or a key-range
		// lock when absent) for the life of the transaction, so concurrent recomputes
		// for the same position serialize and two first fills cannot both insert.
		await using (var posCmd = new SqlCommand(
			"SELECT qty, avg_price, realized_pnl FROM dbo.Positions WITH (UPDLOCK, HOLDLOCK) WHERE portfolio_id = @portfolio_id AND security_id = @security_id", connection)
		{
			Transaction = transaction,
		})
		{
			posCmd.Parameters.AddWithValue("@portfolio_id", portfolioId);
			posCmd.Parameters.AddWithValue("@security_id", securityId);

			await using var reader = await posCmd.ExecuteReaderAsync(cancellationToken);

			if (await reader.ReadAsync(cancellationToken))
			{
				existingQty = reader.GetDecimal(0);
				existingAvgPrice = reader.GetDecimal(1);
				existingRealizedPnl = reader.GetDecimal(2);
				positionExists = true;
			}
		}

		var (newQty, newAvgPrice, newRealizedPnl) = Recalculate(
			existingQty, existingAvgPrice, existingRealizedPnl, side, tradeQty, tradePrice);

		// persist: UPDATE the row when it exists, otherwise INSERT. unrealized_pnl
		// is left untouched (needs a live market price); a new row starts at 0. The
		// affected-row count is asserted so a vanished/duplicated row is never a
		// silent no-op.
		if (positionExists)
		{
			await using var update = new SqlCommand(
				"""
				UPDATE dbo.Positions
					SET qty = @qty, avg_price = @avg_price, realized_pnl = @realized_pnl, updated_date = SYSUTCDATETIME()
					WHERE portfolio_id = @portfolio_id AND security_id = @security_id
				""", connection)
			{
				Transaction = transaction,
			};
			update.Parameters.Add(Decimal4("@qty", newQty));
			update.Parameters.Add(Decimal4("@avg_price", newAvgPrice));
			update.Parameters.Add(Decimal4("@realized_pnl", newRealizedPnl));
			update.Parameters.AddWithValue("@portfolio_id", portfolioId);
			update.Parameters.AddWithValue("@security_id", securityId);

			var affected = await update.ExecuteNonQueryAsync(cancellationToken);

			if (affected != 1)
				throw new InvalidOperationException(FormattableString.Invariant(
					$"Position update for portfolio {portfolioId}/security {securityId} affected {affected} rows (expected 1)."));
		}
		else
		{
			await using var insert = new SqlCommand(
				"""
				INSERT INTO dbo.Positions (portfolio_id, security_id, qty, avg_price, realized_pnl, unrealized_pnl, updated_date)
					VALUES (@portfolio_id, @security_id, @qty, @avg_price, @realized_pnl, 0, SYSUTCDATETIME())
				""", connection)
			{
				Transaction = transaction,
			};
			insert.Parameters.AddWithValue("@portfolio_id", portfolioId);
			insert.Parameters.AddWithValue("@security_id", securityId);
			insert.Parameters.Add(Decimal4("@qty", newQty));
			insert.Parameters.Add(Decimal4("@avg_price", newAvgPrice));
			insert.Parameters.Add(Decimal4("@realized_pnl", newRealizedPnl));

			var affected = await insert.ExecuteNonQueryAsync(cancellationToken);

			if (affected != 1)
				throw new InvalidOperationException(FormattableString.Invariant(
					$"Position insert for portfolio {portfolioId}/security {securityId} affected {affected} rows (expected 1)."));
		}
	}
}
