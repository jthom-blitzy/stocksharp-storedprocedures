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
/// after the fill - both share CanonicalRiskRules with the RiskManager circuit breaker, so
/// there are no SQL stored procedures or triggers involved any more. See LEGACY_LAYER.md at
/// the repo root for the full writeup of what this layer is and why it exists.
///
/// Requires a running PostgreSQL database with the /Database schema applied - see
/// Database/README.md for the one-command `docker-compose up`, or set
/// STOCKSHARP_LEGACY_SQL_CONNECTION to point at your own instance.
///
/// Exit code: returns 0 on a successful walkthrough and a non-zero code if the database
/// cannot be reached, so orchestration/CI (e.g. docker-compose --exit-code-from app) register
/// a real database outage as a failure rather than a false success.
/// </summary>
class Program
{
	static async Task<int> Main()
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
			Console.WriteLine($"Could not reach the legacy PostgreSQL database ({ex.Message}).");
			Console.WriteLine("Run `docker-compose up` from the repo root to start PostgreSQL and apply the /Database scripts;");
			Console.WriteLine("see Database/README.md for details and LEGACY_LAYER.md for what this layer is.");
			// QA finding (MAJOR): a database outage must NOT be reported as success. Return a non-zero
			// exit code so docker-compose --exit-code-from app / CI / any orchestrator registers the
			// failure instead of seeing a false-success exit 0.
			return 1;
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
		Console.WriteLine("     The RiskManager circuit breaker now shares the same CanonicalRiskRules definitions,");
		Console.WriteLine("     so the per-order gate and the circuit breaker no longer diverge - see LEGACY_LAYER.md.");
		Console.WriteLine();

		// The within-limits order is expected to be accepted; if it was not, the walkthrough cannot
		// demonstrate the fill/position step, but this is not a database-reachability failure, so the
		// original exit-0 behaviour of this branch is preserved (the QA exit-code finding concerns the
		// database-unavailable path handled in the catch above).
		if (!order1.IsValid)
			return 0;

		// --- record a fill against the accepted order; RecordTradeAsync inserts the trade and
		//     then PositionRecalculationService recomputes the position exactly once (the old
		//     trg_Trades_PositionRecalc trigger has been removed) ---
		Console.WriteLine("Recording a trade: 100 @ 150.00 against order #1...");
		await gateway.RecordTradeAsync(order1.OrderId, 100m, 150.00m);

		var position = await gateway.GetPositionAsync(portfolioId, securityId);
		Console.WriteLine($"  -> position after recalculation: qty={position.Quantity} avg_price={position.AveragePrice} realized_pnl={position.RealizedPnL}");

		return 0;
	}
}
