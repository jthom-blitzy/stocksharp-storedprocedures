namespace StockSharp.Algo.Risk;

using System.Data;

using Microsoft.Data.SqlClient;

/// <summary>
/// Recomputes a portfolio/security position (quantity, average price and
/// realized P&amp;L) from a single executed trade using standard average-cost
/// accounting.
/// </summary>
/// <remarks>
/// C# replacement for the SQL proc dbo.usp_RecalculatePositionOnTrade and the
/// trg_Trades_PositionRecalc trigger, both of which are removed by the risk
/// consolidation. The trigger previously auto-recomputed on every Trades insert
/// while the proc was ALSO exposed standalone, so invoking both double-counted a
/// trade (see LEGACY_LAYER.md). This service is now the single, unambiguous
/// source of recompute: the gateway calls <see cref="RecalculateAsync"/> EXACTLY
/// ONCE after inserting a trade. unrealized_pnl is deliberately left untouched
/// here because it requires a live market price (see dbo.Positions in
/// 001_Schema.sql); a freshly inserted position row starts at 0.
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

	/// <summary>
	/// Pure average-cost + realized-P&amp;L recompute for a single trade. Ported
	/// verbatim from dbo.usp_RecalculatePositionOnTrade so it can be unit-tested
	/// without a database.
	/// </summary>
	/// <param name="existingQty">Current signed position quantity (0 if flat).</param>
	/// <param name="existingAvgPrice">Current average price (0 if flat).</param>
	/// <param name="existingRealizedPnl">Current realized P&amp;L (0 if flat).</param>
	/// <param name="side">Side of the order the trade belongs to.</param>
	/// <param name="tradeQty">Trade quantity (always positive).</param>
	/// <param name="tradePrice">Trade price.</param>
	/// <returns>The new signed quantity, average price and realized P&amp;L.</returns>
	public static (decimal Quantity, decimal AveragePrice, decimal RealizedPnl) Recalculate(
		decimal existingQty, decimal existingAvgPrice, decimal existingRealizedPnl,
		Sides side, decimal tradeQty, decimal tradePrice)
	{
		var tradeSignedQty = side == Sides.Buy ? tradeQty : -tradeQty;

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

		return (newQty, newAvgPrice, newRealizedPnl);
	}

	/// <summary>
	/// Reads the order and its current position from SQL Server, recomputes the
	/// position from the given trade, and persists the result. This is the single
	/// recompute entry point per trade (the old trigger no longer exists).
	/// </summary>
	/// <param name="orderId">Identifier of the order the trade belongs to; its portfolio, security and side drive the recompute.</param>
	/// <param name="tradeQty">Executed trade quantity (always positive).</param>
	/// <param name="tradePrice">Executed trade price.</param>
	/// <param name="cancellationToken">Token used to cancel the asynchronous operation.</param>
	public async Task RecalculateAsync(long orderId, decimal tradeQty, decimal tradePrice, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		int portfolioId, securityId;
		Sides side;

		await using (var orderCmd = new SqlCommand(
			"SELECT portfolio_id, security_id, side FROM dbo.Orders WHERE order_id = @order_id", connection))
		{
			orderCmd.Parameters.AddWithValue("@order_id", orderId);

			await using var reader = await orderCmd.ExecuteReaderAsync(cancellationToken);

			if (!await reader.ReadAsync(cancellationToken))
				throw new InvalidOperationException($"Order '{orderId}' not found.");

			portfolioId = reader.GetInt32(0);
			securityId = reader.GetInt32(1);
			side = reader.GetString(2) == "B" ? Sides.Buy : Sides.Sell;
		}

		decimal existingQty = 0, existingAvgPrice = 0, existingRealizedPnl = 0;
		var positionExists = false;

		await using (var posCmd = new SqlCommand(
			"SELECT qty, avg_price, realized_pnl FROM dbo.Positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id", connection))
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
		// is left untouched (needs a live market price); a new row starts at 0.
		if (positionExists)
		{
			await using var update = new SqlCommand(
				"""
				UPDATE dbo.Positions
					SET qty = @qty, avg_price = @avg_price, realized_pnl = @realized_pnl, updated_date = SYSUTCDATETIME()
					WHERE portfolio_id = @portfolio_id AND security_id = @security_id
				""", connection);
			update.Parameters.AddWithValue("@qty", newQty);
			update.Parameters.AddWithValue("@avg_price", newAvgPrice);
			update.Parameters.AddWithValue("@realized_pnl", newRealizedPnl);
			update.Parameters.AddWithValue("@portfolio_id", portfolioId);
			update.Parameters.AddWithValue("@security_id", securityId);

			await update.ExecuteNonQueryAsync(cancellationToken);
		}
		else
		{
			await using var insert = new SqlCommand(
				"""
				INSERT INTO dbo.Positions (portfolio_id, security_id, qty, avg_price, realized_pnl, unrealized_pnl, updated_date)
					VALUES (@portfolio_id, @security_id, @qty, @avg_price, @realized_pnl, 0, SYSUTCDATETIME())
				""", connection);
			insert.Parameters.AddWithValue("@portfolio_id", portfolioId);
			insert.Parameters.AddWithValue("@security_id", securityId);
			insert.Parameters.AddWithValue("@qty", newQty);
			insert.Parameters.AddWithValue("@avg_price", newAvgPrice);
			insert.Parameters.AddWithValue("@realized_pnl", newRealizedPnl);

			await insert.ExecuteNonQueryAsync(cancellationToken);
		}
	}
}
