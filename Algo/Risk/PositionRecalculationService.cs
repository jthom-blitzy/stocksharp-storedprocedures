namespace StockSharp.Algo.Risk;

using System.Data;

using Microsoft.Data.SqlClient;

/// <summary>
/// The recomputed position after applying a single trade: new signed quantity, new average price,
/// and cumulative realized profit-and-loss.
/// </summary>
public class PositionRecalcResult
{
	/// <summary>The new signed position quantity (positive long, negative short).</summary>
	public decimal Quantity { get; init; }

	/// <summary>The new average entry price of the open position (0 when flat).</summary>
	public decimal AveragePrice { get; init; }

	/// <summary>The cumulative realized profit-and-loss after this trade.</summary>
	public decimal RealizedPnl { get; init; }
}

/// <summary>
/// Recomputes portfolio positions on each recorded trade using average-cost accounting and
/// realized-P&amp;L tracking, ported from dbo.usp_RecalculatePositionOnTrade. This is the SINGLE
/// entry point for position recalculation, invoked exactly once per trade by the gateway; there is
/// no competing database trigger, which eliminates the historical double-count hazard. The pure math
/// (<see cref="Recalculate"/>) is separated from the database read/write (<see cref="ApplyTradeAsync"/>)
/// so it is unit-testable without a database.
/// </summary>
public class PositionRecalculationService
{
	private readonly string _connectionString;

	/// <summary>
	/// Initializes a new instance of <see cref="PositionRecalculationService"/>.
	/// </summary>
	/// <param name="connectionString">The StockSharpLegacy SQL Server connection string.</param>
	public PositionRecalculationService(string connectionString)
	{
		if (connectionString.IsEmpty())
			throw new ArgumentNullException(nameof(connectionString));

		_connectionString = connectionString;
	}

	private SqlConnection CreateConnection() => new(_connectionString);

	/// <summary>
	/// Computes the new position state produced by applying one trade to an existing position, using the
	/// same average-cost and realized-P&amp;L rules as dbo.usp_RecalculatePositionOnTrade. Pure and
	/// side-effect-free; results are rounded to 4 decimal places (away-from-zero) to mirror the DECIMAL(18,4)
	/// storage columns.
	/// </summary>
	/// <param name="existingQty">The current signed position quantity (0 when flat / no row).</param>
	/// <param name="existingAvgPrice">The current average price (0 when flat / no row).</param>
	/// <param name="existingRealizedPnl">The current cumulative realized P&amp;L (0 when no row).</param>
	/// <param name="positionExists">Whether a position row already exists (kept for caller clarity; the math does not branch on it).</param>
	/// <param name="side">The trade side.</param>
	/// <param name="tradeQty">The trade quantity (&gt; 0).</param>
	/// <param name="tradePrice">The trade price (&gt; 0).</param>
	/// <returns>The recomputed position.</returns>
	public static PositionRecalcResult Recalculate(decimal existingQty, decimal existingAvgPrice, decimal existingRealizedPnl, bool positionExists, Sides side, decimal tradeQty, decimal tradePrice)
	{
		// Signed trade quantity: buys add, sells subtract (mirrors the SQL CASE on @side).
		var tradeSignedQty = side == Sides.Buy ? tradeQty : -tradeQty;

		var newRealizedPnl = existingRealizedPnl;
		decimal newQty;
		decimal newAvgPrice;

		if (existingQty == 0 || Math.Sign(existingQty) == Math.Sign(tradeSignedQty))
		{
			// Adding to (or opening) a position on the same side: weighted-average the price in.
			// newQty can never be 0 on this branch (same sign, non-zero addend), so the divide is safe.
			newQty = existingQty + tradeSignedQty;
			newAvgPrice = (Math.Abs(existingQty) * existingAvgPrice + tradeQty * tradePrice) / Math.Abs(newQty);
		}
		else
		{
			// Trade works against the existing position: realize P&L on the closed portion.
			var closingQty = Math.Min(Math.Abs(existingQty), tradeQty);
			var remainingQty = tradeQty - closingQty;

			newRealizedPnl = existingRealizedPnl + closingQty * (tradePrice - existingAvgPrice) * Math.Sign(existingQty);

			if (remainingQty == 0)
			{
				// Partial or exact close (no flip); avg price is only meaningful while the position stays open.
				newQty = existingQty + tradeSignedQty;
				newAvgPrice = newQty == 0 ? 0 : existingAvgPrice;
			}
			else
			{
				// Fully closed and flipped: what is left of the trade opens a new position on the other side.
				newQty = Math.Sign(tradeSignedQty) * remainingQty;
				newAvgPrice = tradePrice;
			}
		}

		// Round to 4 dp (DECIMAL(18,4)) away-from-zero so sequential recomputations stay bit-identical to
		// the SQL, which reads each subsequent trade off the 4dp-stored avg_price.
		return new PositionRecalcResult
		{
			Quantity = Math.Round(newQty, 4, MidpointRounding.AwayFromZero),
			AveragePrice = Math.Round(newAvgPrice, 4, MidpointRounding.AwayFromZero),
			RealizedPnl = Math.Round(newRealizedPnl, 4, MidpointRounding.AwayFromZero),
		};
	}

	/// <summary>
	/// Applies a single recorded trade to its portfolio position: looks up the trade's order, reads the
	/// existing position, computes the new state via <see cref="Recalculate"/>, and persists it. Must be
	/// called exactly once per recorded trade (the gateway's single entry point) — there is no trigger or
	/// standalone job that also recalculates, so a trade is never double-applied.
	/// </summary>
	/// <param name="orderId">The order the trade belongs to (source of portfolio/security/side).</param>
	/// <param name="tradeQty">The executed trade quantity.</param>
	/// <param name="tradePrice">The executed trade price.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	public async Task ApplyTradeAsync(long orderId, decimal tradeQty, decimal tradePrice, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		// The read-modify-write of the single dbo.Positions row is wrapped in one transaction so the
		// position read (step 3) and the UPSERT (step 5) are atomic - the same atomicity the stored
		// procedure relied on when it ran as a single batch.
		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

		// Step 2 - resolve the order this trade belongs to (portfolio / security / side).
		int portfolioId;
		int securityId;
		Sides side;

		await using (var orderCommand = new SqlCommand(
			"SELECT portfolio_id, security_id, side FROM dbo.Orders WHERE order_id = @order_id", connection, transaction))
		{
			orderCommand.Parameters.AddWithValue("@order_id", orderId);

			await using var orderReader = await orderCommand.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

			if (!await orderReader.ReadAsync(cancellationToken))
				throw new InvalidOperationException($"PositionRecalculationService: order_id {orderId} not found");

			portfolioId = orderReader.GetInt32(0);
			securityId = orderReader.GetInt32(1);

			// dbo.Orders.side is CHAR(1) constrained to 'B'/'S'; mirror the SQL CASE (anything not 'B' is Sell).
			side = orderReader.GetString(2) == "B" ? Sides.Buy : Sides.Sell;
		}

		// Step 3 - read the existing position; absent means flat (0 qty / 0 avg / 0 realized).
		var existingQty = 0m;
		var existingAvgPrice = 0m;
		var existingRealizedPnl = 0m;
		var positionExists = false;

		await using (var positionCommand = new SqlCommand(
			"SELECT qty, avg_price, realized_pnl FROM dbo.Positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id", connection, transaction))
		{
			positionCommand.Parameters.AddWithValue("@portfolio_id", portfolioId);
			positionCommand.Parameters.AddWithValue("@security_id", securityId);

			await using var positionReader = await positionCommand.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

			if (await positionReader.ReadAsync(cancellationToken))
			{
				existingQty = positionReader.GetDecimal(0);
				existingAvgPrice = positionReader.GetDecimal(1);
				existingRealizedPnl = positionReader.GetDecimal(2);
				positionExists = true;
			}
		}

		// Step 4 - pure recompute (identical math to dbo.usp_RecalculatePositionOnTrade).
		var result = Recalculate(existingQty, existingAvgPrice, existingRealizedPnl, positionExists, side, tradeQty, tradePrice);

		// Step 5 - persist. unrealized_pnl is set to 0 on insert and is never maintained here; it stays an
		// end-of-day mark-to-market concern (see dbo.Positions in Database/001_Schema.sql).
		var persistSql = positionExists
			? "UPDATE dbo.Positions SET qty = @q, avg_price = @ap, realized_pnl = @rp, updated_date = SYSUTCDATETIME() WHERE portfolio_id = @portfolio_id AND security_id = @security_id"
			: "INSERT INTO dbo.Positions (portfolio_id, security_id, qty, avg_price, realized_pnl, unrealized_pnl, updated_date) VALUES (@portfolio_id, @security_id, @q, @ap, @rp, 0, SYSUTCDATETIME())";

		await using (var persistCommand = new SqlCommand(persistSql, connection, transaction))
		{
			persistCommand.Parameters.AddWithValue("@portfolio_id", portfolioId);
			persistCommand.Parameters.AddWithValue("@security_id", securityId);
			persistCommand.Parameters.AddWithValue("@q", result.Quantity);
			persistCommand.Parameters.AddWithValue("@ap", result.AveragePrice);
			persistCommand.Parameters.AddWithValue("@rp", result.RealizedPnl);

			await persistCommand.ExecuteNonQueryAsync(cancellationToken);
		}

		await transaction.CommitAsync(cancellationToken);
	}
}
