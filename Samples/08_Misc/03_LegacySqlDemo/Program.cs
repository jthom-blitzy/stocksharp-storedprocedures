namespace StockSharp.Samples.Misc.LegacySqlDemo;

using System;
using System.Threading.Tasks;

using StockSharp.Algo.Storages.Sql;
using StockSharp.BusinessEntities;
using StockSharp.Messages;

/// <summary>
/// End-to-end walkthrough of the StockSharpLegacy SQL layer under /Database and
/// Algo/Storages/Sql: submit a compliant order, submit a non-compliant one,
/// record a fill, and show the position update. Pre-trade validation and the
/// position/realized-P&amp;L recompute are performed by the canonical C# services
/// (StockSharp.Algo.Risk) - SQL Server holds only the data. See LEGACY_LAYER.md
/// at the repo root for the full writeup of what this layer is and why it exists.
///
/// Requires a running SQL Server with the StockSharpLegacy database - see
/// Database/README.md for the one-line Docker command, or set
/// STOCKSHARP_LEGACY_SQL_CONNECTION to point at your own instance.
/// </summary>
class Program
{
	static async Task Main()
	{
		var gateway = new SqlLegacyOrderGateway(SqlLegacyConnection.Resolve());

		var portfolio = new Portfolio { Name = "DEMO" };
		var security = new Security { Id = "AAPL@NASDAQ", Code = "AAPL", Board = ExchangeBoard.Nasdaq, Type = SecurityTypes.Stock };

		int portfolioId, securityId;

		try
		{
			portfolioId = await gateway.EnsurePortfolioAsync(portfolio);
			securityId = await gateway.EnsureSecurityAsync(security);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Could not reach StockSharpLegacy ({ex.Message}).");
			Console.WriteLine("See Database/README.md for how to stand up the SQL Server instance this demo needs.");
			return;
		}

		Console.WriteLine($"Portfolio '{portfolio.Name}' = portfolio_id {portfolioId}");
		Console.WriteLine($"Security '{security.Id}' = security_id {securityId}");
		Console.WriteLine();

		// --- order #1: within every configured RiskLimits ceiling -> ACCEPTED ---
		Console.WriteLine("Submitting BUY 100 @ 150.00 (within limits)...");
		var order1 = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 150.00m, OrderTypes.Limit);
		Console.WriteLine($"  -> order_id={order1.OrderId} is_valid={order1.IsValid} reject_reason={order1.RejectReason ?? "(none)"}");
		Console.WriteLine();

		// --- order #2: price breaches the max_order_price limit (RiskLimits row seeded at 500.00) -> REJECTED ---
		Console.WriteLine("Submitting BUY 10 @ 999.00 (price exceeds the configured max_order_price limit)...");
		var order2 = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 10m, 999.00m, OrderTypes.Limit);
		Console.WriteLine($"  -> order_id={order2.OrderId} is_valid={order2.IsValid} reject_reason={order2.RejectReason ?? "(none)"}");
		Console.WriteLine("     Note: this rejection comes from the C# PreTradeRiskService gate (Algo/Risk),");
		Console.WriteLine("     which reads the limit from dbo.RiskLimits. The RiskManager circuit breaker");
		Console.WriteLine("     consumes the SAME canonical rule definitions - the two enforcement patterns");
		Console.WriteLine("     differ only in the input they evaluate, never in the rule itself.");
		Console.WriteLine();

		if (!order1.IsValid)
			return;

		// --- record a fill against the accepted order; the gateway inserts the trade and then
		//     invokes PositionRecalculationService exactly once to recompute dbo.Positions
		//     (quantity / weighted-average price / realized P&L) inside the same transaction ---
		Console.WriteLine("Recording a trade: 100 @ 150.00 against order #1...");
		await gateway.RecordTradeAsync(order1.OrderId, 100m, 150.00m);

		var position = await gateway.GetPositionAsync(portfolioId, securityId);
		Console.WriteLine($"  -> position after recalculation: qty={position.Quantity} avg_price={position.AveragePrice} realized_pnl={position.RealizedPnL}");
	}
}
