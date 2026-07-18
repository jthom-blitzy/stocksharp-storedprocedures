namespace StockSharp.Samples.Misc.LegacySqlDemo;

using System;
using System.Threading.Tasks;

using StockSharp.Algo.Storages.Sql;
using StockSharp.BusinessEntities;
using StockSharp.Messages;

/// <summary>
/// End-to-end walkthrough of the legacy order/position layer under /Database and
/// Algo/Storages/Sql, now running on PostgreSQL with the business logic consolidated
/// into C#: submit a compliant order, submit a non-compliant one, record a fill, and
/// show the automatic position update. Pre-trade validation runs in the canonical
/// PreTradeRiskService gate, and the position is recomputed by PositionRecalculationService
/// after the fill. The gate and the RiskManager circuit breaker share ONE canonical
/// rolling-frequency evaluator plus the "&gt;=" / "NULL-or-0 = unlimited" comparison convention
/// (CanonicalRiskRules); the circuit breaker's other rules (price, qty, position, commission)
/// stay separately configured by design, so this is NOT a claim that every rule is merged.
/// There are no SQL stored procedures and no position-recalculation trigger any more, but the
/// pure status-audit trigger on Orders intentionally remains. See LEGACY_LAYER.md at the repo
/// root for the full writeup of what this layer is and why it exists.
///
/// Requires a running PostgreSQL database with the /Database schema applied - see
/// Database/README.md for the one-command `docker compose up`, or set
/// STOCKSHARP_LEGACY_SQL_CONNECTION to point at your own instance.
///
/// Repeatability: the fixed output below assumes a CLEAN database. Re-running against a
/// persisted volume accumulates state - the position grows (qty 100 -&gt; 200 -&gt; ...) and the
/// repeated submissions can trip the seeded 5-orders / 60-seconds frequency limit, which changes
/// the order_id values and the accept/reject outcomes. Run `docker compose down -v` (which drops
/// the database volume) immediately before any run whose output you want to reproduce exactly.
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
			// N3: surface only the exception TYPE name, never the raw exception message, so a connection
			// failure cannot leak host/database internals (server address, credentials, schema names).
			Console.WriteLine($"Could not reach the legacy PostgreSQL database ({ex.GetType().Name}).");
			Console.WriteLine("Run `docker compose up` from the repo root to start PostgreSQL and apply the /Database scripts;");
			Console.WriteLine("see Database/README.md for details and LEGACY_LAYER.md for what this layer is.");
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
		Console.WriteLine("Submitting BUY 10 @ 999.00 (price exceeds the seeded max_order_price limit)...");
		var order2 = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 10m, 999.00m, OrderTypes.Limit);
		Console.WriteLine($"  -> order_id={order2.OrderId} is_valid={order2.IsValid} reject_reason={order2.RejectReason ?? "(none)"}");
		Console.WriteLine("     Note: this rejection comes from the canonical PreTradeRiskService gate (Algo/Risk).");
		Console.WriteLine("     The RiskManager circuit breaker shares the same canonical rolling-frequency evaluator");
		Console.WriteLine("     and comparison convention (CanonicalRiskRules), so the gate and the breaker can no");
		Console.WriteLine("     longer disagree on a frequency decision - see LEGACY_LAYER.md for the full rule map.");
		Console.WriteLine();

		// The within-limits order is expected to be accepted on a CLEAN database; if it was not (e.g. a
		// persisted-volume re-run tripped the frequency limit - see the repeatability note above), the
		// walkthrough simply cannot demonstrate the fill/position step, so it returns early.
		if (!order1.IsValid)
			return;

		// --- record a fill against the accepted order; RecordTradeAsync inserts the trade and
		//     then PositionRecalculationService recomputes the position exactly once (the old
		//     trg_Trades_PositionRecalc trigger has been removed) ---
		Console.WriteLine("Recording a trade: 100 @ 150.00 against order #1...");
		await gateway.RecordTradeAsync(order1.OrderId, 100m, 150.00m);

		var position = await gateway.GetPositionAsync(portfolioId, securityId);
		Console.WriteLine($"  -> position after recalculation: qty={position.Quantity} avg_price={position.AveragePrice} realized_pnl={position.RealizedPnL}");
	}
}
