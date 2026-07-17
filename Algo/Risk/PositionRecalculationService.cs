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
/// A single call within one atomic transaction guarantees each trade is applied once
/// <em>for that insert</em>, but it cannot by itself make a RETRY safe: re-running the
/// same logical fill would insert a second trade and apply the delta again. Replay
/// safety is therefore provided by the Trades.execution_id idempotency key
/// (001_Schema.sql): the gateway records a trade under that key, and a duplicate key
/// skips both the insert and this recompute, so a retried fill is applied exactly
/// once end-to-end. The former standalone mutator overload was removed because it
/// mutated the position with no trade row and no key to detect such a replay (CR-4).
/// </para>
/// <para>
/// The position row is read under WITH (UPDLOCK, HOLDLOCK) so concurrent fills for
/// the same portfolio/security serialize and simultaneous first fills cannot race on
/// the unique key. Writes use explicit DECIMAL(18,4) parameters and assert the
/// affected-row count, so a missing/duplicate row surfaces as an error rather than a
/// silent no-op. unrealized_pnl is deliberately left untouched (it needs a live
/// market price - see dbo.Positions in 001_Schema.sql); a freshly inserted row
/// starts at 0.
/// </para>
/// </remarks>
public class PositionRecalculationService
{
	private readonly string _connectionString;

	/// <summary>
	/// Initializes a new instance of the <see cref="PositionRecalculationService"/>.
	/// </summary>
	/// <param name="connectionString">SQL Server connection string for the StockSharpLegacy database.</param>
	public PositionRecalculationService(string connectionString)
	{
		if (connectionString.IsEmpty())
			throw new ArgumentNullException(nameof(connectionString));

		_connectionString = connectionString;
	}

	private SqlConnection CreateConnection() => new(_connectionString);

	// Every RiskLimits/Positions/Trades money/qty column and every variable in the
	// original proc was DECIMAL(18,4), rounded half away from zero on assignment. The
	// pure method normalizes inputs and rounds outputs to the same scale so, e.g., a
	// weighted average of 5/3 persists as 1.6667 exactly as the proc stored it.
	private static decimal Round4(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

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

		// Explicit Buy/Sell mapping - never silently treat an unexpected side as a sell.
		var tradeSignedQty = side switch
		{
			Sides.Buy => tradeQty,
			Sides.Sell => -tradeQty,
			_ => throw new ArgumentOutOfRangeException(nameof(side), side, "Order side must be Buy or Sell."),
		};

		// Existing values already originate from DECIMAL(18,4) columns; normalize
		// defensively so the computation runs on the same values SQL Server stored.
		existingQty = Round4(existingQty);
		existingAvgPrice = Round4(existingAvgPrice);
		existingRealizedPnl = Round4(existingRealizedPnl);

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
		return (Round4(newQty), Round4(newAvgPrice), Round4(newRealizedPnl));
	}

	// NOTE (CR-4): the standalone RecalculateAsync(orderId, qty, price) convenience
	// overload was intentionally removed. It opened its own transaction and mutated
	// the stored position from raw (qty, price) arguments WITHOUT inserting - or even
	// identifying - a Trade row, so nothing tied the mutation to a specific execution.
	// Calling it twice (a retry, or a manual re-run) applied the same position delta
	// twice with no way to detect the replay - a silent double-count. The only
	// supported way to drive a recompute is now the transaction-aware overload below,
	// which the gateway invokes exactly once inside the same transaction that inserts
	// the trade, under the Trades.execution_id idempotency key. The pure, side-effect-
	// free Recalculate(...) above remains available for computation and unit tests.

	/// <summary>
	/// Reads the order and its current position, recomputes from the given trade and
	/// persists the result using a caller-supplied open connection and transaction -
	/// the same transaction that inserts the trade, so the trade and its position
	/// effect are one atomic unit. This is the single recompute entry point per trade
	/// (the old auto-recompute trigger no longer exists); the gateway calls it exactly
	/// once per trade insert, and the Trades.execution_id idempotency key ensures a
	/// retried trade neither re-inserts nor re-applies its position effect.
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
