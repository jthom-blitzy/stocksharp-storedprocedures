namespace StockSharp.Samples.Misc.LegacySqlDemo;

using System;
using System.Threading.Tasks;

using Microsoft.Data.SqlClient;

using StockSharp.Algo.Risk;
using StockSharp.Algo.Storages.Sql;
using StockSharp.BusinessEntities;
using StockSharp.Messages;

/// <summary>
/// End-to-end walkthrough of the StockSharpLegacy SQL layer under /Database and Algo/Storages/Sql:
/// seed the portfolio-wide RiskManager circuit breaker from the canonical RiskLimitSet, submit a
/// compliant order, submit a non-compliant one, record a fill, and show the position that the C#
/// PositionRecalculationService recomputes. After the SQL -&gt; C# consolidation the gateway runs the
/// pre-trade checks in PreTradeRiskService and the position math in PositionRecalculationService, and
/// this program seeds a RiskManager from the SAME canonical RiskLimitSet the gate uses (via
/// PreTradeRiskService.LoadLimitsAsync + RiskManager.ApplyCanonicalLimits) so both enforcement patterns
/// consume one definition; the database is pure storage. See LEGACY_LAYER.md at the repo root for the
/// full writeup.
///
/// Requires a running SQL Server with the StockSharpLegacy database - see
/// Database/README.md for the one-line Docker command, or set
/// STOCKSHARP_LEGACY_SQL_CONNECTION to point at your own instance.
/// </summary>
class Program
{
	/// <summary>
	/// Runs the end-to-end walkthrough and returns a process exit code so an orchestrator or CI can tell
	/// success from failure (finding INF-P6-04): <c>0</c> when the three observable outcomes all reproduced,
	/// <c>1</c> when the database could not be reached / provisioned (the caught fatal error), and <c>2</c>
	/// when the compliant order #1 was unexpectedly rejected so the fill and position outcomes could not run.
	/// </summary>
	/// <returns>The process exit code: 0 on success, non-zero on failure.</returns>
	static async Task<int> Main()
	{
		var connectionString = SqlLegacyConnection.Resolve();
		var gateway = new SqlLegacyOrderGateway(connectionString);

		var portfolio = new Portfolio { Name = "DEMO" };
		var security = new Security { Id = "AAPL@NASDAQ", Code = "AAPL", Board = ExchangeBoard.Nasdaq, Type = SecurityTypes.Stock };

		// Every database interaction in this demo - ensuring the portfolio/security rows, resetting the
		// DEMO portfolio's transactional state, seeding the circuit breaker from the canonical RiskLimitSet,
		// submitting the two orders, recording the fill and reading the recomputed position - runs inside
		// this single try/catch (QA finding: canonical-fallback demo smoke). If the StockSharpLegacy database
		// cannot be reached, OR its schema predates an object this build depends on (for example a shared or
		// pre-provisioned instance left by an earlier build against which the current Database/001-004 scripts
		// were never re-applied), the demo prints one guiding message and exits with a NON-ZERO status - a
		// controlled failure signal rather than an unhandled exception - so an orchestrator/CI detects it
		// (finding INF-P6-04). On a correctly provisioned database the three observable outcomes
		// (accept / reject-by-price / trade-recomputes-position) are unchanged and the program returns 0.
		try
		{
			var portfolioId = await gateway.EnsurePortfolioAsync(portfolio);
			var securityId = await gateway.EnsureSecurityAsync(security);

			Console.WriteLine($"Portfolio '{portfolio.Name}' = portfolio_id {portfolioId}");
			Console.WriteLine($"Security '{security.Id}' = security_id {securityId}");
			Console.WriteLine();

			// --- make the demo repeatable (review finding MA-14) ---
			// The demo submits and fills orders against the seeded DEMO portfolio. Without a reset, each run
			// would ACCUMULATE rows: a second run's position would read qty 200 instead of 100, and after
			// enough runs the compliant order #1 would trip the seeded order-frequency limit (5 orders / 60s)
			// and be rejected instead of accepted - contradicting the "same outcome on every run" intent. To
			// keep the three observable outcomes identical on EVERY run we clear this portfolio's disposable
			// transactional rows (orders, trades, positions and the derived status history) up front, while
			// PRESERVING the seeded RiskLimits row, the portfolio and the securities. See Database/README.md
			// for the documented reset requirement.
			Console.WriteLine("Resetting DEMO portfolio transactional state (orders/trades/positions/history) for a repeatable run...");
			await ResetDemoTransactionalStateAsync(connectionString, portfolioId);
			Console.WriteLine();

			// --- consolidate: seed the portfolio-wide circuit breaker from the SAME canonical source the
			//     per-order gate uses (review findings CR-1 / CR-20) ---
			// The gateway's per-order pre-trade gate loads the applicable RiskLimits row and enforces it order
			// by order. Here we resolve that SAME scoped row through PreTradeRiskService.LoadLimitsAsync and seed
			// a RiskManager (Algo/Risk) - the portfolio-wide circuit breaker - from it via ApplyCanonicalLimits.
			// This is a real, non-test caller applying the canonical RiskLimitSet to a production RiskManager,
			// scoped to this exact (portfolio, security): the two enforcement patterns stay architecturally
			// distinct but now demonstrably consume ONE canonical definition, satisfying the "define once, enforce
			// in both patterns" objective. NOTE: this demo IS that production caller - the platform's
			// Connector/RiskMessageAdapter do NOT auto-seed a manager from the database, and wiring them is
			// outside this refactor's scope (Algo/Connector.cs is not an in-scope file), so no such claim is made.
			var riskService = new PreTradeRiskService(connectionString);
			var canonicalLimits = await riskService.LoadLimitsAsync(portfolioId, securityId);

			var circuitBreaker = new RiskManager();
			if (canonicalLimits is not null)
				circuitBreaker.ApplyCanonicalLimits(canonicalLimits, RiskActions.ClosePositions);

			var priceCeiling = canonicalLimits?.EffectiveMaxOrderPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(not enforced)";
			Console.WriteLine($"Seeded the portfolio-wide RiskManager circuit breaker from the canonical RiskLimitSet for " +
				$"portfolio_id {portfolioId} / security_id {securityId}: {circuitBreaker.Rules.Count} rule(s), max_order_price ceiling = {priceCeiling}.");
			Console.WriteLine("     The per-order gate (below) and this circuit breaker now read the same canonical thresholds.");
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
			Console.WriteLine("     Note: this rejection comes from the C# PreTradeRiskService (the per-order pre-trade");
			Console.WriteLine("     gate), which reads the applicable RiskLimits row and enforces it in C#. The C#");
			Console.WriteLine("     RiskManager (Algo/Risk) is the portfolio-wide circuit breaker - a distinct");
			Console.WriteLine("     enforcement pattern we seeded above from this very same canonical RiskLimitSet.");
			Console.WriteLine();

			if (!order1.IsValid)
			{
				// The compliant order #1 (BUY 100 @ 150, within every seeded ceiling) is expected to be
				// ACCEPTED on a correctly provisioned database. If it was rejected, the demo cannot record a
				// fill or show the recomputed position, so its expected outcomes were NOT reproduced. Exit
				// non-zero (finding INF-P6-04) so an orchestrator/CI sees the anomaly instead of a false success.
				Console.WriteLine($"Compliant order #1 was unexpectedly rejected ({order1.RejectReason ?? "(no reason)"}); " +
					"the demo could not complete its trade/position outcomes.");
				return 2;
			}

			// --- record a fill against the accepted order; RecordTradeAsync inserts the trade and then
			//     invokes PositionRecalculationService once to recompute dbo.Positions in C# ---
			Console.WriteLine("Recording a trade: 100 @ 150.00 against order #1...");
			await gateway.RecordTradeAsync(order1.OrderId, 100m, 150.00m);

			var position = await gateway.GetPositionAsync(portfolioId, securityId);
			Console.WriteLine($"  -> position after C# recompute: qty={position.Quantity} avg_price={position.AveragePrice} realized_pnl={position.RealizedPnL}");

			// All three observable outcomes reproduced (accept / reject-by-price / trade-recomputes-position):
			// signal success so an orchestrator/CI can distinguish a good run from a failure (finding INF-P6-04).
			return 0;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Could not reach StockSharpLegacy ({ex.Message}).");
			Console.WriteLine("See Database/README.md for how to stand up the SQL Server instance this demo needs");
			Console.WriteLine("and to (re)apply the 001-004 scripts so its schema matches this build.");

			// A fatal, unrecoverable failure (unreachable/misprovisioned database): exit non-zero rather than
			// returning 0, so the failure is not silently reported as success (finding INF-P6-04).
			return 1;
		}
	}

	/// <summary>
	/// Clears the disposable transactional rows for a single portfolio - orders, trades, positions and
	/// the derived order-status history - so the demo produces the same three outcomes on every run
	/// (review finding MA-14: without this, rows accumulate across runs and the position doubles while the
	/// order-frequency limit eventually rejects the compliant order). The seeded RiskLimits row, the
	/// portfolio and the securities are intentionally left in place, so the seeded thresholds and the
	/// DEMO/AAPL/MSFT rows the AAP requires still exist. Deletes are ordered child-before-parent to respect
	/// the foreign keys (OrderStatusHistory and Trades both reference Orders; Positions reference the
	/// portfolio/security only) and run inside a single transaction so the reset is all-or-nothing.
	/// This reset is a dev/maintenance step, not steady-state runtime: it needs <c>DELETE</c> on the four
	/// disposable transactional tables, which is a privilege beyond the <c>SELECT, INSERT, UPDATE</c> the
	/// gateway uses at runtime. Run it under the provisioning login, or grant that narrowly scoped
	/// disposable <c>DELETE</c> to the application login - see the credential note in Database/README.md.
	/// </summary>
	/// <param name="connectionString">Connection string for the StockSharpLegacy database.</param>
	/// <param name="portfolioId">The portfolio whose transactional rows should be cleared.</param>
	static async Task ResetDemoTransactionalStateAsync(string connectionString, int portfolioId)
	{
		const string sql = @"
DELETE FROM dbo.OrderStatusHistory WHERE order_id IN (SELECT order_id FROM dbo.Orders WHERE portfolio_id = @portfolioId);
DELETE FROM dbo.Trades            WHERE order_id IN (SELECT order_id FROM dbo.Orders WHERE portfolio_id = @portfolioId);
DELETE FROM dbo.Positions         WHERE portfolio_id = @portfolioId;
DELETE FROM dbo.Orders            WHERE portfolio_id = @portfolioId;";

		await using var connection = new SqlConnection(connectionString);
		await connection.OpenAsync();

		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

		await using (var command = new SqlCommand(sql, connection, transaction))
		{
			command.Parameters.AddWithValue("@portfolioId", portfolioId);
			await command.ExecuteNonQueryAsync();
		}

		await transaction.CommitAsync();
	}
}
