namespace StockSharp.Samples.Misc.LegacySqlDemo;

using System;
using System.Threading.Tasks;

using StockSharp.Algo.Storages.Sql;
using StockSharp.BusinessEntities;
using StockSharp.Messages;

/// <summary>
/// End-to-end walkthrough of the StockSharpLegacy SQL layer under /Database and
/// Algo/Storages/Sql: submit a compliant order, submit a non-compliant one,
/// record a fill, and show the position being recomputed in C#. Pre-trade
/// validation now runs in the C# PreTradeRiskService and position recompute in
/// the C# PositionRecalculationService (both in StockSharp.Algo.Risk); the SQL
/// side holds data only (tables, the RiskLimits threshold values, and a pure
/// audit trigger) with no risk-decision or P&amp;L logic. See LEGACY_LAYER.md at the
/// repo root for the full writeup of what this layer is and why it exists.
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

		// --- order #2: price breaches RiskLimits.max_order_price (seeded at 500.00) -> REJECTED ---
		Console.WriteLine("Submitting BUY 10 @ 999.00 (price exceeds the max_order_price limit)...");
		var order2 = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 10m, 999.00m, OrderTypes.Limit);
		Console.WriteLine($"  -> order_id={order2.OrderId} is_valid={order2.IsValid} reject_reason={order2.RejectReason ?? "(none)"}");
		Console.WriteLine("     Note: this rejection comes from the C# PreTradeRiskService (Algo/Risk), the");
		Console.WriteLine("     per-order pre-trade gate that replaced SQL usp_ValidatePreTradeRisk. Each risk");
		Console.WriteLine("     rule is defined as a canonical IRiskRule class; the RiskManager circuit breaker");
		Console.WriteLine("     and this pre-trade gate are two distinct patterns that apply those definitions");
		Console.WriteLine("     where a rule exists in both, though at different points, so their per-rule");
		Console.WriteLine("     results can legitimately differ by design (see LEGACY_LAYER.md).");
		Console.WriteLine();

		if (!order1.IsValid)
			return;

		// --- record a fill against the accepted order; RecordTradeAsync inserts the
		//     trade and then calls PositionRecalculationService exactly once, inside
		//     one transaction, to recompute dbo.Positions. There is no auto-recompute
		//     trigger, so the position is recomputed once end-to-end in C# (AAP 0.6.5). ---
		Console.WriteLine("Recording a trade: 100 @ 150.00 against order #1...");
		await gateway.RecordTradeAsync(order1.OrderId, 100m, 150.00m);

		var position = await gateway.GetPositionAsync(portfolioId, securityId);
		Console.WriteLine($"  -> position after recompute: qty={position.Quantity} avg_price={position.AveragePrice} realized_pnl={position.RealizedPnL}");
	}
}
