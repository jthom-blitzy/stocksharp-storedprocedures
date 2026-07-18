namespace StockSharp.Tests;

using System.Data;
using System.Text;

using StockSharp.Algo.Risk;
using StockSharp.Algo.Storages.Sql;

using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Parity, characterization and integration coverage for the average-cost position / realized-P&amp;L
/// recomputation the refactor consolidated out of the retired SQL procedure
/// <c>usp_RecalculatePositionOnTrade</c> (<c>Database/002_StoredProcedures.sql</c>) into the canonical C#
/// service <see cref="PositionRecalculationService"/> under <c>Algo/Risk/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Mandated by the test-coverage rule (AAP 0.7.3) and the staged testing sequence (AAP 0.6.3). The suite is
/// an executable staged matrix over ONE shared case table (<see cref="_cases"/>) so a regression can be
/// attributed to the correct risk axis (logic consolidation vs engine migration):
/// </para>
/// <list type="number">
///   <item><description><b>Stage 1 - Original SQL Server procedure (golden baseline, live SQL Server).</b>
///     For every shared case <see cref="Stage1_OriginalSqlServerProcedure_GoldenBaseline"/> executes the
///     ORIGINAL, retired <c>dbo.usp_RecalculatePositionOnTrade</c> on a live SQL Server (materialized from
///     the embedded <c>OriginalCharacterizationSetup.sql</c> resource), seeding the existing position in a
///     transaction, running the procedure and reading the persisted <c>dbo.Positions</c> row back, then
///     rolling back. Those live procedure outputs ARE the golden <c>(qty, avg_price, realized_pnl)</c>, so
///     the stage authentically validates the shared <see cref="_cases"/> constants against the behaviour the
///     refactor consolidated out of SQL (AAP G1), rather than trusting hand-written numbers.</description></item>
///   <item><description><b>Stage 2 - Consolidated C# logic on SQL Server state (engine held constant).</b>
///     <see cref="Stage2_ConsolidatedLogic_OnSqlServerState_MatchesGolden"/> seeds the SAME existing
///     position on SQL Server, reads that state back FROM SQL Server, and feeds it to the REAL, pure
///     <see cref="PositionRecalculationService.Recalculate"/>. The engine is held at SQL Server, so a
///     divergence here isolates a logic-consolidation bug (SQL to C#) from an engine-migration bug. This is
///     the authentic C#-parity step of AAP 0.6.3, made possible because the recalculation logic was
///     extracted as a pure <see cref="decimal"/> function with no provider dependency, so it can run against
///     state read from EITHER engine.</description></item>
///   <item><description><b>Stage 3 - PostgreSQL engine parity (live).</b>
///     <see cref="Stage3_PostgreSqlEngineParity_AllCasesViaApplyAsync"/> drives the same cases end to end
///     through <see cref="PositionRecalculationService.ApplyAsync"/> against a live PostgreSQL database,
///     reading the persisted <c>positions</c> row back and asserting the golden values. Passing Stage 2 but
///     failing Stage 3 points to a dialect / engine issue, not logic.</description></item>
///   <item><description><b>Stage 4 - the ordering is the diagnostic instrument.</b>
///     <see cref="Stage4_StagedOrdering_AttributionMatrix"/> runs all three engines per shared case in one
///     pass and asserts the axes pairwise: Stage 1 (SQL procedure) == Stage 2 (C# on SQL Server state) is
///     the LOGIC axis; Stage 2 == Stage 3 (C# on PostgreSQL) is the ENGINE axis. The first axis that
///     diverges names the regression's cause, which is precisely the risk-axis disambiguation the staged
///     sequence exists to provide.</description></item>
/// </list>
/// <para>
/// Both live engines are gated by MANDATORY guards: when the relevant connection environment variable is SET
/// but the database is unreachable the staged test FAILS (never Inconclusive), so a required milestone run
/// cannot appear green without actually exercising the engine; it reports Inconclusive ONLY when the variable
/// is unset (an ordinary local run that did not opt in). SQL Server (Stages 1/2/4) is gated by
/// <see cref="OpenSqlServerAsync"/> on <c>STOCKSHARP_LEGACY_MSSQL_CONNECTION</c>; PostgreSQL (Stages 3/4 and
/// the gateway integration tests) by <see cref="OpenMandatoryAsync"/> on <c>STOCKSHARP_LEGACY_SQL_CONNECTION</c>.
/// </para>
/// <para>
/// Alongside the staged matrix, three always-run pure backbone tests keep the logic covered without any
/// database: <see cref="PureLogic_RecalculateMatchesGoldenConstants"/> (the pure function reproduces the
/// shared constants), <see cref="PureLogic_AverageCostInvariants"/> (the average-cost algebraic invariants),
/// and <see cref="CaseTable_CoversAllBranches"/> (the shared case table still covers both signs across every
/// branch, so no stage can silently degrade into a weaker check).
/// </para>
/// <para>
/// Every money / quantity / price fixture is a <see cref="decimal"/> literal (the <c>m</c> suffix) so the
/// schema's <c>NUMERIC(18,4)</c> scale is preserved and a comparison can never silently loosen (AAP 0.6.4).
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize] // Staged Stages 1/2/4 seed and roll back transactions against the shared SQL Server (dbo.*) and PostgreSQL engines; parallel execution deadlocks on those shared tables, so this class runs serially (repo convention, cf. StorageNotParallelizeTests / ExportTests / PreTradeRiskParityTests).
public class PositionRecalculationTests : BaseTestClass
{
	// ========================================================================================================
	// Shared case table (AAP 0.6.3 "the same cases"): the single source of truth consumed identically by
	// Stage 1 (characterization), Stage 2 (logic invariants) and Stage 3 (PostgreSQL parity). Each row is
	// pre-verified against the SQL golden baseline in usp_RecalculatePositionOnTrade
	// (Database/002_StoredProcedures.sql ~L281-L320) with the sign convention 'B' = positive delta,
	// 'S' = negative delta. Covers BOTH signs across every average-cost branch: open-from-flat, same-sign
	// accumulation, partial close, exact close and flip - long and short - so no stage degrades to a no-op.
	// ========================================================================================================

	/// <summary>One average-cost recomputation fixture: an existing position, a trade, and the golden result.</summary>
	private sealed record RecalcCase(
		string Name,
		decimal ExistingQty, decimal ExistingAvgPrice, decimal ExistingRealizedPnl,
		string Side, decimal TradeQty, decimal TradePrice,
		decimal ExpectedQty, decimal ExpectedAvgPrice, decimal ExpectedRealizedPnl);

	private static readonly RecalcCase[] _cases =
	[
		// name                                exQty     exAvg   exRPnl  side   tQty    tPrice    expQty    expAvg  expRPnl
		new("long_open_from_flat",               0m,      0m,      0m,   "B",   100m,  150.00m,   100m,  150.00m,    0m),
		new("long_same_sign_weighted_avg",     100m,  150.00m,     0m,   "B",   100m,  160.00m,   200m,  155.00m,    0m),
		new("long_partial_close",              100m,  150.00m,     0m,   "S",    40m,  170.00m,    60m,  150.00m,  800m),
		new("long_exact_close",                100m,  150.00m,     0m,   "S",   100m,  170.00m,     0m,      0m, 2000m),
		new("long_flip_to_short",              100m,  150.00m,     0m,   "S",   150m,  170.00m,   -50m,  170.00m, 2000m),
		new("short_open_from_flat",              0m,      0m,      0m,   "S",   100m,  150.00m,  -100m,  150.00m,    0m),
		new("short_same_sign_weighted_avg",   -100m,  150.00m,     0m,   "S",    50m,  180.00m,  -150m,  160.00m,    0m),
		new("short_partial_close",             -100m,  150.00m,     0m,   "B",    40m,  130.00m,   -60m,  150.00m,  800m),
		new("short_exact_close",               -100m,  150.00m,     0m,   "B",   100m,  130.00m,     0m,      0m, 2000m),
		new("short_flip_to_long",              -100m,  150.00m,     0m,   "B",   150m,  130.00m,    50m,  130.00m, 2000m),
		new("realized_carried_on_add",         100m,  150.00m,    55m,   "B",   100m,  160.00m,   200m,  155.00m,   55m),
		new("realized_accumulated_on_close",   100m,  150.00m,    55m,   "S",    40m,  170.00m,    60m,  150.00m,  855m),
	];

	// ========================================================================================================
	// Pure backbone (always runs, no database): the pure Recalculate reproduces the shared constants. The
	// AUTHENTIC golden baseline that validates those constants against the live retired SQL Server procedure
	// is Stage1_OriginalSqlServerProcedure_GoldenBaseline below.
	// ========================================================================================================

	/// <summary>
	/// Pure backbone (always runs). For every shared case the pure, database-free
	/// <see cref="PositionRecalculationService.Recalculate"/> must reproduce the golden
	/// <c>(qty, avg_price, realized_pnl)</c> constants. Those constants are themselves validated against the
	/// live, retired SQL Server procedure by <see cref="Stage1_OriginalSqlServerProcedure_GoldenBaseline"/>;
	/// this fast, engine-independent test keeps the consolidated arithmetic covered on an ordinary local run
	/// that has not opted into a database (analogous to the pure phases of the pre-trade parity suite).
	/// </summary>
	[TestMethod]
	public void PureLogic_RecalculateMatchesGoldenConstants()
	{
		foreach (var c in _cases)
		{
			var (qty, avgPrice, realizedPnl) = PositionRecalculationService.Recalculate(
				c.ExistingQty, c.ExistingAvgPrice, c.ExistingRealizedPnl, c.Side, c.TradeQty, c.TradePrice);

			qty.AssertEqual(c.ExpectedQty);
			avgPrice.AssertEqual(c.ExpectedAvgPrice);
			realizedPnl.AssertEqual(c.ExpectedRealizedPnl);
		}
	}

	// ========================================================================================================
	// Pure backbone (always runs, no database): the average-cost algebraic invariants. The AUTHENTIC
	// consolidated-logic parity step (C# on state read from a live SQL Server) is
	// Stage2_ConsolidatedLogic_OnSqlServerState_MatchesGolden below.
	// ========================================================================================================

	/// <summary>
	/// Pure backbone (always runs). For every shared case the consolidated C# result must satisfy the
	/// average-cost ALGEBRAIC INVARIANTS independently of the golden numbers: signed-quantity conservation,
	/// realize-P&amp;L-only-on-a-close (with the closed-portion formula), and the per-branch average-price
	/// rules. This engine-independent check documents WHY each golden number is correct; the authentic
	/// engine-held-constant parity run is <see cref="Stage2_ConsolidatedLogic_OnSqlServerState_MatchesGolden"/>.
	/// </summary>
	[TestMethod]
	public void PureLogic_AverageCostInvariants()
	{
		foreach (var c in _cases)
		{
			var signedTradeQty = c.Side == "B" ? c.TradeQty : -c.TradeQty;

			var (qty, avgPrice, realizedPnl) = PositionRecalculationService.Recalculate(
				c.ExistingQty, c.ExistingAvgPrice, c.ExistingRealizedPnl, c.Side, c.TradeQty, c.TradePrice);

			// (i) Signed-quantity conservation: the new signed qty is always existing + signed trade qty.
			qty.AssertEqual(c.ExistingQty + signedTradeQty);

			var sameSignOrFlat = c.ExistingQty == 0m || Math.Sign(c.ExistingQty) == Math.Sign(signedTradeQty);

			if (sameSignOrFlat)
			{
				// (ii) A same-sign add (or an open from flat) realizes NOTHING: realized carries through.
				realizedPnl.AssertEqual(c.ExistingRealizedPnl);

				if (c.ExistingQty == 0m)
				{
					// opening from flat: the average price is just the incoming trade price.
					avgPrice.AssertEqual(c.TradePrice);
				}
				else
				{
					// (iii) a same-sign weighted average must lie between the existing and the trade price.
					var lo = Math.Min(c.ExistingAvgPrice, c.TradePrice);
					var hi = Math.Max(c.ExistingAvgPrice, c.TradePrice);
					(avgPrice >= lo && avgPrice <= hi).AssertTrue();
				}
			}
			else
			{
				// Opposite sign -> a close occurs. Realized changes by closingQty*(price-avg)*sign(existing).
				var closingQty = Math.Min(Math.Abs(c.ExistingQty), c.TradeQty);
				var expectedRealizedDelta = closingQty * (c.TradePrice - c.ExistingAvgPrice) * Math.Sign(c.ExistingQty);
				(realizedPnl - c.ExistingRealizedPnl).AssertEqual(expectedRealizedDelta);

				var remaining = c.TradeQty - Math.Abs(c.ExistingQty);

				if (remaining > 0m)
				{
					// (iv) flip: the residual is a fresh position at the incoming trade price and the new
					// signed qty takes the trade's sign.
					avgPrice.AssertEqual(c.TradePrice);
					Math.Sign(qty).AssertEqual(Math.Sign(signedTradeQty));
				}
				else if (qty == 0m)
				{
					// (v) exact close: no open position remains, so the average price resets to 0.
					avgPrice.AssertEqual(0m);
				}
				else
				{
					// (vi) partial close: the position stays open at the UNCHANGED average price.
					avgPrice.AssertEqual(c.ExistingAvgPrice);
				}
			}
		}
	}

	// ========================================================================================================
	// Named narrative characterizations (human-readable examples that pin behaviour the case table cannot
	// express as numbers alone: the demo's observable outcome, the side sign convention, and the structural
	// absence of an unrealized_pnl output).
	// ========================================================================================================

	/// <summary>
	/// The demo's observable outcome: recording a trade against an accepted order updates the position
	/// automatically. Flat + BUY 100 @ 150 opens a long of 100 @ 150 - the same case the sample demonstrates
	/// (<c>Samples/08_Misc/03_LegacySqlDemo/Program.cs</c>), kept explicit here as a named characterization.
	/// </summary>
	[TestMethod]
	public void Recalculate_DemoScenario()
	{
		var (qty, avgPrice, realizedPnl) = PositionRecalculationService.Recalculate(
			existingQty: 0m, existingAvgPrice: 0m, existingRealizedPnl: 0m,
			side: "B", tradeQty: 100m, tradePrice: 150.00m);

		qty.AssertEqual(100m);          // recording the trade updates the position from flat to 100
		avgPrice.AssertEqual(150.00m);  // (|0|*0 + 100*150.00) / |100| = 150.00
		realizedPnl.AssertEqual(0m);    // opening a position realizes nothing
	}

	/// <summary>
	/// Focused proof of the sign convention: <c>"B"</c> (buy) contributes a POSITIVE delta and <c>"S"</c>
	/// (sell) a NEGATIVE delta. SQL golden baseline:
	/// <c>tradeSignedQty = CASE WHEN side = 'B' THEN trade_qty ELSE -trade_qty END</c>.
	/// </summary>
	[TestMethod]
	public void Recalculate_SignConvention_BvsS()
	{
		var buy = PositionRecalculationService.Recalculate(
			existingQty: 0m, existingAvgPrice: 0m, existingRealizedPnl: 0m,
			side: "B", tradeQty: 10m, tradePrice: 100.00m);
		buy.Qty.AssertEqual(10m);   // 'B' -> +tradeQty -> +10

		var sell = PositionRecalculationService.Recalculate(
			existingQty: 0m, existingAvgPrice: 0m, existingRealizedPnl: 0m,
			side: "S", tradeQty: 10m, tradePrice: 100.00m);
		sell.Qty.AssertEqual(-10m); // 'S' -> -tradeQty -> -10
	}

	/// <summary>
	/// Documents and proves that recalculation never computes an <c>unrealized_pnl</c>: the pure
	/// <see cref="PositionRecalculationService.Recalculate"/> contract returns only
	/// <c>(Qty, AvgPrice, RealizedPnl)</c>, so by construction it cannot produce an unrealized value.
	/// Realized P&amp;L is the only P&amp;L the math touches - unchanged on an add, changed only on a close.
	/// The DB-level proof that the STORED <c>unrealized_pnl</c> column is left untouched lives in the
	/// mandatory <see cref="ApplyAsync_LeavesUnrealizedPnlUntouched"/> test.
	/// </summary>
	[TestMethod]
	public void Recalculate_DoesNotProduceUnrealizedPnl()
	{
		// A same-sign add must leave realized P&L exactly as it came in (only a close realizes P&L),
		// and there is no unrealized output in the tuple to change.
		var add = PositionRecalculationService.Recalculate(
			existingQty: 100m, existingAvgPrice: 150.00m, existingRealizedPnl: 55m,
			side: "B", tradeQty: 100m, tradePrice: 160.00m);
		add.RealizedPnl.AssertEqual(55m);    // realized carried through unchanged on an add
		add.Qty.AssertEqual(200m);           // 100 + 100
		add.AvgPrice.AssertEqual(155.00m);   // (100*150 + 100*160)/200 = 155.00

		// A close changes ONLY realized P&L; there is still no unrealized output.
		var close = PositionRecalculationService.Recalculate(
			existingQty: 100m, existingAvgPrice: 150.00m, existingRealizedPnl: 55m,
			side: "S", tradeQty: 40m, tradePrice: 170.00m);
		close.RealizedPnl.AssertEqual(855m); // 55 + 40*(170-150)*Sign(+100) = 55 + 800 = 855
		close.Qty.AssertEqual(60m);          // 100 + (-40)
		close.AvgPrice.AssertEqual(150.00m); // partial close: average price unchanged
	}

	// ========================================================================================================
	// F16 / F21 - input validation. REPLACES the former zero-quantity "successful no-op" oracle, which
	// codified invalid behaviour (a zero-qty trade silently returning the position unchanged). The
	// consolidated service now REJECTS a malformed trade BEFORE any arithmetic, matching the Trades CHECK
	// constraints (side IN ('B','S'), qty > 0, price > 0); these tests assert exactly those rejections and
	// each carries accurate XML documentation (F21).
	// ========================================================================================================

	/// <summary>
	/// An unrecognised side must be rejected with <see cref="ArgumentException"/> rather than silently
	/// treated as a sell (the retired ternary mapped anything that was not <c>"B"</c> to a sell). Covers a
	/// junk value, the empty string, <see langword="null"/>, and the wrong case (the contract is exactly the
	/// upper-case <c>"B"</c> / <c>"S"</c> the schema's <c>CHAR(1)</c> stores).
	/// </summary>
	[TestMethod]
	public void Recalculate_InvalidSide_ThrowsArgumentException()
	{
		foreach (var badSide in new[] { "X", "", "b", "s", "buy", null })
		{
			ThrowsExactly<ArgumentException>(
				() => PositionRecalculationService.Recalculate(0m, 0m, 0m, badSide, 100m, 150.00m),
				$"side \"{badSide ?? "<null>"}\" must be rejected, not treated as a sell");
		}
	}

	/// <summary>
	/// A non-positive trade quantity must be rejected with <see cref="ArgumentOutOfRangeException"/> before
	/// any arithmetic (parity with the Trades <c>CHECK (qty &gt; 0)</c>): zero would divide by
	/// <c>ABS(newQty)</c> = 0 in the weighted-average branch, and a negative quantity is nonsensical.
	/// </summary>
	[TestMethod]
	public void Recalculate_NonPositiveQuantity_ThrowsArgumentOutOfRange()
	{
		foreach (var badQty in new[] { 0m, -1m, -100m })
		{
			ThrowsExactly<ArgumentOutOfRangeException>(
				() => PositionRecalculationService.Recalculate(0m, 0m, 0m, "B", badQty, 150.00m),
				$"trade quantity {badQty} must be rejected (qty > 0)");
		}
	}

	/// <summary>
	/// A non-positive trade price must be rejected with <see cref="ArgumentOutOfRangeException"/> before any
	/// arithmetic (parity with the Trades <c>CHECK (price &gt; 0)</c>).
	/// </summary>
	[TestMethod]
	public void Recalculate_NonPositivePrice_ThrowsArgumentOutOfRange()
	{
		foreach (var badPrice in new[] { 0m, -1m, -150.00m })
		{
			ThrowsExactly<ArgumentOutOfRangeException>(
				() => PositionRecalculationService.Recalculate(0m, 0m, 0m, "B", 100m, badPrice),
				$"trade price {badPrice} must be rejected (price > 0)");
		}
	}

	// ========================================================================================================
	// Stage 3 - PostgreSQL engine parity (MANDATORY) + gateway / service integration coverage (F15).
	//
	// These exercise the REAL PositionRecalculationService.ApplyAsync and SqlLegacyOrderGateway against a
	// live PostgreSQL database. Per F14 they are MANDATORY milestone tests: OpenMandatoryAsync FAILS the run
	// when a database is declared (env var set) but unreachable, and skips (Inconclusive) only when none is
	// configured. Each scenario seeds only what it needs with unique natural keys (including unique trade
	// ids), and tears everything down in a finally so the shared dev/CI database stays clean across re-runs.
	//
	// Single-apply note (C3, F5/F2): the position-recalc DB trigger was removed, and exactly-once application
	// is now enforced DURABLY. ApplyAsync claims the persisted trade by flipping trades.position_applied
	// FALSE -> TRUE with a conditional UPDATE in the SAME transaction as the position write, so a repeat,
	// retry, restart, or second-instance replay of an already-applied trade_id updates 0 rows and is an
	// idempotent no-op. This is a persisted, cross-instance guarantee (not the former process-local HashSet),
	// so a DIRECT ApplyAsync must target a REAL persisted trade_id - the tests below INSERT a trades row first
	// (mirroring the gateway's INSERT ... RETURNING trade_id). The invariant is covered by the real-Trades-row
	// gateway path, the sequential same-trade_id no-op, the cross-instance/gateway duplicate no-op, and the
	// concurrent DIFFERENT-trades accumulation below.
	// ========================================================================================================

	/// <summary>
	/// Stage 3 (PostgreSQL engine parity, MANDATORY). Drives every shared case end to end through
	/// <see cref="PositionRecalculationService.ApplyAsync"/> against a live PostgreSQL database and asserts
	/// the persisted <c>positions</c> row equals the golden baseline. A non-flat existing position is seeded
	/// first (exercising the UPSERT UPDATE path); an all-flat case exercises the UPSERT INSERT path. Passing
	/// Stage 2 but failing here isolates a dialect / engine-migration issue rather than a logic bug.
	/// </summary>
	[TestMethod]
	public async Task Stage3_PostgreSqlEngineParity_AllCasesViaApplyAsync()
	{
		await using var cn = await OpenMandatoryAsync();

		if (cn is null)
			return;

		foreach (var c in _cases)
		{
			// The real ApplyAsync runs end to end on PostgreSQL and returns the persisted row.
			var (qty, avgPrice, realizedPnl) = await RecalcViaApplyOnPostgresAsync(cn, c);

			// NUMERIC(18,4) columns -> decimal, so parity is exact and no >= comparison downstream loosens.
			qty.AssertEqual(c.ExpectedQty);
			avgPrice.AssertEqual(c.ExpectedAvgPrice);
			realizedPnl.AssertEqual(c.ExpectedRealizedPnl);
		}
	}

	/// <summary>
	/// F15 - the real Trades-row path. <see cref="SqlLegacyOrderGateway.RecordTradeAsync"/> INSERTs a trades
	/// row AND applies the position in one atomic transaction (not a direct <c>ApplyAsync</c> call). Exactly
	/// ONE trade row must exist afterwards and the position must reflect exactly ONE apply - the structural
	/// single-apply guarantee (a fresh trade_id per committed trade).
	/// </summary>
	[TestMethod]
	public async Task Gateway_RecordTrade_PersistsTradeRowAndUpdatesPositionOnce()
	{
		await using var cn = await OpenMandatoryAsync();

		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_gw_real_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");

			var gateway = new SqlLegacyOrderGateway(SqlLegacyConnection.Resolve());
			await gateway.RecordTradeAsync(orderId, 100m, 150.00m, CancellationToken);

			(await CountTradesForOrderAsync(cn, orderId)).AssertEqual(1); // exactly one persisted trade row

			var position = await gateway.GetPositionAsync(portfolioId, securityId, CancellationToken);
			IsNotNull(position, "a position must exist after a recorded trade");
			position.Quantity.AssertEqual(100m);       // opened once - not double-applied
			position.AveragePrice.AssertEqual(150.00m);
			position.RealizedPnL.AssertEqual(0m);
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId);
		}
	}

	/// <summary>
	/// F15 - gateway atomicity: recording a trade against a NON-existent order violates
	/// <c>FK_Trades_Orders</c>, so the gateway's single atomic transaction rolls back and NO partial state
	/// (no trade row) can leak. Proves the INSERT+recalculation unit is all-or-nothing at the gateway boundary.
	/// </summary>
	[TestMethod]
	public async Task Gateway_RecordTrade_NonexistentOrder_LeavesNoPartialState()
	{
		await using var cn = await OpenMandatoryAsync();

		if (cn is null)
			return;

		// An order id that cannot exist (identity starts at 1 and climbs; this sits far above any seeded id).
		var nonExistentOrderId = long.MaxValue - Random.Shared.NextInt64(1, 1_000_000L);

		var gateway = new SqlLegacyOrderGateway(SqlLegacyConnection.Resolve());

		await ThrowsAsync<PostgresException>(
			async () => await gateway.RecordTradeAsync(nonExistentOrderId, 100m, 150.00m, CancellationToken),
			"recording a trade against a non-existent order must fail on the foreign key");

		(await CountTradesForOrderAsync(cn, nonExistentOrderId)).AssertEqual(0); // rolled back: no trade row
	}

	/// <summary>
	/// F15 / F2 - the strong atomicity property: a trade row already INSERTed inside the transaction is
	/// UNDONE when the subsequent position apply throws. Models the gateway's atomic unit: in one
	/// transaction insert a REAL trade row (valid FK to the order), then drive the apply against a DIFFERENT,
	/// non-existent order so <see cref="PositionRecalculationService.ApplyAsync"/> throws
	/// <see cref="InvalidOperationException"/>; rolling back must leave neither the trade row nor a position.
	/// </summary>
	[TestMethod]
	public async Task ServiceTransaction_TradeInsertRolledBackWhenApplyThrows()
	{
		await using var cn = await OpenMandatoryAsync();

		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_atomic_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");
			var nonExistentOrderId = long.MaxValue - Random.Shared.NextInt64(1, 1_000_000L);
			var svc = new PositionRecalculationService();

			await using (var txn = await cn.BeginTransactionAsync(CancellationToken))
			{
				// Insert a REAL trade row against the valid order, inside the transaction.
				await using (var insert = new NpgsqlCommand(
					"INSERT INTO trades (order_id, qty, price, executed_date) " +
					"VALUES (@order_id, @qty, @price, now() at time zone 'utc') RETURNING trade_id", cn, txn))
				{
					insert.Parameters.Add(new NpgsqlParameter("order_id", NpgsqlDbType.Bigint) { Value = orderId });
					insert.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = 100m });
					insert.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = 150.00m });
					_ = (long)await insert.ExecuteScalarAsync(CancellationToken);
				}

				// The apply targets a non-existent order -> InvalidOperationException (parity with the SQL
				// RAISERROR). The service reads no order row (no SQL error), so the transaction stays healthy
				// and can be rolled back cleanly.
				await ThrowsAsync<InvalidOperationException>(
					async () => await svc.ApplyAsync(cn, txn, nonExistentOrderId, NextTradeId(), 100m, 150.00m, CancellationToken),
					"applying a trade for a non-existent order must throw");

				await txn.RollbackAsync(CancellationToken);
			}

			// The trade row inserted inside the rolled-back transaction is GONE and no position was created.
			(await CountTradesForOrderAsync(cn, orderId)).AssertEqual(0);
			(await PositionExistsAsync(cn, portfolioId, securityId)).AssertEqual(false);
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId);
		}
	}

	/// <summary>
	/// F15 - concurrency / lost-update invariant. Two DIFFERENT trades on the SAME (portfolio, security),
	/// recorded CONCURRENTLY through the gateway (each its own connection + transaction), must both land: the
	/// per-portfolio <c>pg_advisory_xact_lock</c> (C4, the single-key lock space shared by order submission
	/// and recalculation) serialises the read-recompute-write so neither update is lost and the position
	/// accumulates to qty 200.
	/// </summary>
	[TestMethod]
	public async Task Gateway_ConcurrentDifferentTrades_Accumulate()
	{
		await using var cn = await OpenMandatoryAsync();

		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_gw_conc_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");

			// Each RecordTradeAsync opens its own connection + transaction and commits, releasing the advisory
			// lock for the other - so genuine concurrency serialises rather than dead-locking.
			var gateway = new SqlLegacyOrderGateway(SqlLegacyConnection.Resolve());
			var applyA = gateway.RecordTradeAsync(orderId, 100m, 150.00m, CancellationToken);
			var applyB = gateway.RecordTradeAsync(orderId, 100m, 150.00m, CancellationToken);
			await Task.WhenAll(applyA, applyB);

			(await CountTradesForOrderAsync(cn, orderId)).AssertEqual(2); // two DISTINCT trade rows

			var position = await gateway.GetPositionAsync(portfolioId, securityId, CancellationToken);
			IsNotNull(position, "a position must exist after two recorded trades");
			position.Quantity.AssertEqual(200m);       // 100 + 100 - NEITHER update lost
			position.AveragePrice.AssertEqual(150.00m); // both at 150 -> weighted average still 150
			position.RealizedPnL.AssertEqual(0m);       // two same-side opens realize nothing
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId);
		}
	}

	/// <summary>
	/// C3 - sequential single-apply via the DURABLE guard. A REAL trades row is inserted, then the SAME
	/// persisted trade_id is applied twice (sequentially, each in its own committed transaction). The durable
	/// trades.position_applied guard flips FALSE -> TRUE on the first apply and commits it, so the second
	/// apply updates 0 rows and is an idempotent no-op - the position is opened once (qty 100), not
	/// double-counted (200). Because the flag is persisted, the guarantee holds across transactions,
	/// instances, and process restarts, not just within one in-memory service object.
	/// </summary>
	[TestMethod]
	public async Task ApplyAsync_SequentialSameTrade_SingleApply_NoDoubleCount()
	{
		await using var cn = await OpenMandatoryAsync();

		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_single_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");

			// The durable guard is keyed on the PERSISTED trade_id (trades.position_applied), so insert a REAL
			// trade row and reuse its trade_id for BOTH applies; the second sees the committed flag and no-ops.
			// A fresh service instance suffices - the guarantee lives in the database, not the object.
			var svc = new PositionRecalculationService();
			var tradeId = await InsertTradeAsync(cn, orderId, 100m, 150.00m);

			await using (var t1 = await cn.BeginTransactionAsync(CancellationToken))
			{
				await svc.ApplyAsync(cn, t1, orderId, tradeId, 100m, 150.00m, CancellationToken);
				await t1.CommitAsync(CancellationToken);
			}

			await using (var t2 = await cn.BeginTransactionAsync(CancellationToken))
			{
				await svc.ApplyAsync(cn, t2, orderId, tradeId, 100m, 150.00m, CancellationToken);
				await t2.CommitAsync(CancellationToken);
			}

			var position = await ReadPositionAsync(cn, portfolioId, securityId);
			position.Qty.AssertEqual(100m);           // NOT 200 - the second apply was a guarded no-op
			position.AvgPrice.AssertEqual(150.00m);   // opened at 150; the repeat did not re-average it
			position.RealizedPnl.AssertEqual(0m);     // opening a position realizes nothing
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId);
		}
	}

	/// <summary>
	/// The stored <c>unrealized_pnl</c> column is left untouched by recalculation: it is end-of-day /
	/// market-price driven and the average-cost recompute must never overwrite it. Seeds a position carrying
	/// a sentinel <c>unrealized_pnl</c>, applies a trade, and asserts qty / avg / realized update while the
	/// sentinel survives (the UPSERT's DO UPDATE deliberately omits <c>unrealized_pnl</c> from its SET list).
	/// </summary>
	[TestMethod]
	public async Task ApplyAsync_LeavesUnrealizedPnlUntouched()
	{
		await using var cn = await OpenMandatoryAsync();

		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_unreal_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");

			const decimal sentinelUnrealized = 123.4500m;
			await InsertPositionAsync(cn, portfolioId, securityId,
				qty: 0m, avgPrice: 0m, realizedPnl: 0m, unrealizedPnl: sentinelUnrealized);

			// Insert a REAL trades row so the durable single-apply guard (C3) can claim it; ApplyAsync flips
			// its position_applied flag inside the transaction below.
			var tradeId = await InsertTradeAsync(cn, orderId, 100m, 150.00m);

			var svc = new PositionRecalculationService();
			await using (var txn = await cn.BeginTransactionAsync(CancellationToken))
			{
				await svc.ApplyAsync(cn, txn, orderId, tradeId, 100m, 150.00m, CancellationToken);
				await txn.CommitAsync(CancellationToken);
			}

			var position = await ReadPositionAsync(cn, portfolioId, securityId);
			position.Qty.AssertEqual(100m);                    // 0 + 100
			position.AvgPrice.AssertEqual(150.00m);            // (|0|*0 + 100*150)/100 = 150
			position.RealizedPnl.AssertEqual(0m);              // opening a position realizes nothing
			position.UnrealizedPnl.AssertEqual(sentinelUnrealized); // untouched by recalculation
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId);
		}
	}

	/// <summary>
	/// C3 (multi-instance / restart / retry). The gateway records a trade (INSERT ... RETURNING trade_id, then
	/// apply, committed with trades.position_applied = TRUE). A SEPARATE, freshly constructed service instance
	/// - standing in for a different process instance or a post-restart retry - then re-applies the SAME
	/// persisted trade_id. Because the guard is a persisted flag rather than in-process state, the retry
	/// updates 0 rows and is an idempotent no-op: the position stays applied exactly once (qty 100, not 200).
	/// </summary>
	[TestMethod]
	public async Task ApplyAsync_CrossInstanceDuplicateSamePersistedTrade_IsDurableNoOp()
	{
		await using var cn = await OpenMandatoryAsync();

		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_xinst_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");

			// First application through the gateway: inserts the trade and applies it in one committed txn.
			var gateway = new SqlLegacyOrderGateway(SqlLegacyConnection.Resolve());
			await gateway.RecordTradeAsync(orderId, 100m, 150.00m, CancellationToken);
			var tradeId = await ReadOnlyTradeIdForOrderAsync(cn, orderId);
			(await ReadTradeAppliedAsync(cn, tradeId)).AssertEqual(true); // durably marked applied

			// A DIFFERENT service instance replays the SAME persisted trade_id (multi-instance / restart / retry).
			var otherInstance = new PositionRecalculationService();
			await using (var txn = await cn.BeginTransactionAsync(CancellationToken))
			{
				await otherInstance.ApplyAsync(cn, txn, orderId, tradeId, 100m, 150.00m, CancellationToken);
				await txn.CommitAsync(CancellationToken);
			}

			(await CountTradesForOrderAsync(cn, orderId)).AssertEqual(1); // still exactly one trade row
			var position = await gateway.GetPositionAsync(portfolioId, securityId, CancellationToken);
			IsNotNull(position, "a position must exist after the recorded trade");
			position.Quantity.AssertEqual(100m);        // applied exactly once across instances - NOT 200
			position.AveragePrice.AssertEqual(150.00m);
			position.RealizedPnL.AssertEqual(0m);
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId);
		}
	}

	/// <summary>
	/// C3 (rollback-then-retry). The durable guard flips trades.position_applied FALSE -> TRUE in the SAME
	/// transaction as the position write, so a ROLLED-BACK apply undoes BOTH: the flag returns to FALSE and no
	/// position is persisted. A legitimate retry of the SAME trade_id then SUCCEEDS - it is NOT wrongly
	/// suppressed by a stale flag. Proves the mark and the effect are atomic, so the guard never blocks a
	/// reapply of an attempt that never actually committed.
	/// </summary>
	[TestMethod]
	public async Task ApplyAsync_RollbackThenRetrySameTrade_RetryApplies()
	{
		await using var cn = await OpenMandatoryAsync();

		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_rbretry_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");
			var tradeId = await InsertTradeAsync(cn, orderId, 100m, 150.00m);
			var svc = new PositionRecalculationService();

			// First attempt applies then ROLLS BACK: the position write and the position_applied flip are undone.
			await using (var t1 = await cn.BeginTransactionAsync(CancellationToken))
			{
				await svc.ApplyAsync(cn, t1, orderId, tradeId, 100m, 150.00m, CancellationToken);
				await t1.RollbackAsync(CancellationToken);
			}

			(await ReadTradeAppliedAsync(cn, tradeId)).AssertEqual(false); // flag rolled back with the position
			(await PositionExistsAsync(cn, portfolioId, securityId)).AssertEqual(false); // nothing persisted

			// Retry the SAME trade_id: NOT suppressed - it applies and commits this time.
			await using (var t2 = await cn.BeginTransactionAsync(CancellationToken))
			{
				await svc.ApplyAsync(cn, t2, orderId, tradeId, 100m, 150.00m, CancellationToken);
				await t2.CommitAsync(CancellationToken);
			}

			(await ReadTradeAppliedAsync(cn, tradeId)).AssertEqual(true);
			var position = await ReadPositionAsync(cn, portfolioId, securityId);
			position.Qty.AssertEqual(100m);        // the retry applied exactly once
			position.AvgPrice.AssertEqual(150.00m);
			position.RealizedPnl.AssertEqual(0m);
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId);
		}
	}

	/// <summary>
	/// C3 / C4 (non-atomic race). Two CONCURRENT applies of the SAME persisted trade_id, each on its own
	/// connection + transaction, race for the position. The per-portfolio advisory lock serialises them and
	/// the durable position_applied guard lets exactly ONE win: the other sees the committed flag and no-ops.
	/// The position is applied exactly once (qty 100, not 200) - no lost update and no double count.
	/// </summary>
	[TestMethod]
	public async Task ApplyAsync_ConcurrentSameTrade_AppliedExactlyOnce()
	{
		await using var cn = await OpenMandatoryAsync();

		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_racesame_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");
			var tradeId = await InsertTradeAsync(cn, orderId, 100m, 150.00m);

			// Apply the SAME trade_id twice concurrently, each on a fresh connection/transaction so they truly
			// race; the advisory lock + durable guard collapse them to a single apply.
			async Task ApplyOnFreshConnectionAsync()
			{
				await using var c = new NpgsqlConnection(SqlLegacyConnection.Resolve());
				await c.OpenAsync(CancellationToken);
				await using var txn = await c.BeginTransactionAsync(CancellationToken);
				await new PositionRecalculationService().ApplyAsync(c, txn, orderId, tradeId, 100m, 150.00m, CancellationToken);
				await txn.CommitAsync(CancellationToken);
			}

			await Task.WhenAll(ApplyOnFreshConnectionAsync(), ApplyOnFreshConnectionAsync());

			(await CountTradesForOrderAsync(cn, orderId)).AssertEqual(1); // still exactly one trade row
			var position = await ReadPositionAsync(cn, portfolioId, securityId);
			position.Qty.AssertEqual(100m);        // exactly one apply won the race - NOT 200
			position.AvgPrice.AssertEqual(150.00m);
			position.RealizedPnl.AssertEqual(0m);
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId);
		}
	}

	/// <summary>
	/// C5 (sub-4dp persisted-value parity). A trade recorded with more than 4 decimal places is stored in the
	/// NUMERIC(18,4) columns ROUNDED to 4dp (round half away from zero), and RecordTradeAsync reads those
	/// PERSISTED values back (RETURNING qty, price) to drive the recompute - so the position reflects the
	/// stored 4dp values, never the raw >4dp input. Guards against a position computed from a value that
	/// disagrees with what the trades row actually holds.
	/// </summary>
	[TestMethod]
	public async Task RecordTrade_SubFourDecimalQtyPrice_PositionMatchesPersistedValues()
	{
		await using var cn = await OpenMandatoryAsync();

		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_subdp_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");

			// 100.00006 -> 100.0001 (rounds up), 150.00004 -> 150.0000 (rounds down): unambiguous at 4dp.
			var gateway = new SqlLegacyOrderGateway(SqlLegacyConnection.Resolve());
			await gateway.RecordTradeAsync(orderId, 100.00006m, 150.00004m, CancellationToken);

			// The stored trade row holds the 4dp-rounded values.
			var tradeId = await ReadOnlyTradeIdForOrderAsync(cn, orderId);
			var (persistedQty, persistedPrice) = await ReadTradeQtyPriceAsync(cn, tradeId);
			persistedQty.AssertEqual(100.0001m);
			persistedPrice.AssertEqual(150.0000m);

			// The position was computed from those PERSISTED values, not the raw >4dp input.
			var position = await gateway.GetPositionAsync(portfolioId, securityId, CancellationToken);
			IsNotNull(position, "a position must exist after the recorded trade");
			position.Quantity.AssertEqual(100.0001m);
			position.AveragePrice.AssertEqual(150.0000m);
			position.RealizedPnL.AssertEqual(0m);
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId);
		}
	}

	// ========================================================================================================
	// Stage 4 - the ordering itself is the diagnostic instrument (AAP 0.6.3).
	// ========================================================================================================

	/// <summary>
	/// Pure backbone (always runs). Guards the shared-case-table coverage contract that the staged matrix
	/// depends on: the ONE shared <see cref="_cases"/> table must cover BOTH signs across every average-cost
	/// branch (open-from-flat, same-sign accumulation, partial close, exact close, flip) plus realized-P&amp;L
	/// carry and accumulation. Because Stages 1-4 all iterate this identical table, a future edit that shrank
	/// it or dropped a branch would trip this guard rather than silently degrade a stage into a weaker check.
	/// The executable Stage 1 -> 2 -> 3 attribution itself lives in
	/// <see cref="Stage4_StagedOrdering_AttributionMatrix"/>.
	/// </summary>
	[TestMethod]
	public void CaseTable_CoversAllBranches()
	{
		// The shared table is the single source of truth the three executable stages all iterate.
		_cases.Length.AssertEqual(12);

		var names = new HashSet<string>(_cases.Select(c => c.Name));

		foreach (var expected in new[]
		{
			"long_open_from_flat", "long_same_sign_weighted_avg", "long_partial_close",
			"long_exact_close", "long_flip_to_short",
			"short_open_from_flat", "short_same_sign_weighted_avg", "short_partial_close",
			"short_exact_close", "short_flip_to_long",
			"realized_carried_on_add", "realized_accumulated_on_close",
		})
		{
			names.Contains(expected).AssertTrue();
		}

		// BOTH signs open from flat (neither sign's opening branch is missing).
		_cases.Any(c => c.Name == "long_open_from_flat").AssertTrue();
		_cases.Any(c => c.Name == "short_open_from_flat").AssertTrue();

		// BOTH flips are present: a long that crosses to short and a short that crosses to long.
		_cases.Any(c => c.ExistingQty > 0m && c.ExpectedQty < 0m).AssertTrue();
		_cases.Any(c => c.ExistingQty < 0m && c.ExpectedQty > 0m).AssertTrue();

		// BOTH exact closes are present (qty and avg both reset to 0).
		_cases.Count(c => c.ExpectedQty == 0m && c.ExpectedAvgPrice == 0m).AssertEqual(2);

		// Realized P&L is exercised both as a carried-forward balance (unchanged on a same-sign add) and as
		// an accumulation onto an existing balance (on a close).
		_cases.Any(c => c.Name == "realized_carried_on_add" && c.ExistingRealizedPnl == c.ExpectedRealizedPnl).AssertTrue();
		_cases.Any(c => c.Name == "realized_accumulated_on_close" && c.ExpectedRealizedPnl > c.ExistingRealizedPnl).AssertTrue();
	}

	// ========================================================================================================
	// Staged four-step validation (AAP 0.6.3) - authentic, dual-engine. Stages 1/2/4 exercise a live SQL
	// Server (the original retired procedure and state reads); Stage 3 the live PostgreSQL engine. The
	// shared _cases table is consumed identically so the first stage that diverges names the failing axis.
	// ========================================================================================================

	/// <summary>
	/// Stage 1 (golden baseline, live SQL Server). For every shared case, executes the ORIGINAL retired
	/// <c>dbo.usp_RecalculatePositionOnTrade</c> on a live SQL Server: seeds the existing position in a
	/// transaction, runs the procedure with the case's trade, reads the persisted <c>dbo.Positions</c> row
	/// back, then rolls back. The procedure's live output IS the golden <c>(qty, avg_price, realized_pnl)</c>,
	/// so this stage authentically validates the shared <see cref="_cases"/> constants against the behaviour
	/// the refactor consolidated out of SQL (AAP G1) rather than trusting hand-written numbers. MANDATORY when
	/// <c>STOCKSHARP_LEGACY_MSSQL_CONNECTION</c> is set (see <see cref="OpenSqlServerAsync"/>).
	/// </summary>
	[TestMethod]
	public async Task Stage1_OriginalSqlServerProcedure_GoldenBaseline()
	{
		await using var sqlServer = await OpenSqlServerAsync();

		foreach (var c in _cases)
		{
			var golden = await SqlProcRecalcAsync(sqlServer, c);
			AssertRecalcEqual(golden, c, "Stage 1 (original SQL Server procedure)");
		}
	}

	/// <summary>
	/// Stage 2 (consolidated C# logic on SQL Server state, engine held constant). For every shared case,
	/// seeds the SAME existing position on SQL Server, reads that state back FROM SQL Server, and feeds it to
	/// the REAL, pure <see cref="PositionRecalculationService.Recalculate"/>. Because the engine is held at
	/// SQL Server, a divergence here isolates a SQL-to-C# LOGIC-consolidation bug from an engine-migration
	/// bug. The result must equal the golden constants (themselves validated live by Stage 1). MANDATORY when
	/// <c>STOCKSHARP_LEGACY_MSSQL_CONNECTION</c> is set.
	/// </summary>
	[TestMethod]
	public async Task Stage2_ConsolidatedLogic_OnSqlServerState_MatchesGolden()
	{
		await using var sqlServer = await OpenSqlServerAsync();

		foreach (var c in _cases)
		{
			var result = await RecalcOnSqlServerStateAsync(sqlServer, c);
			AssertRecalcEqual(result, c, "Stage 2 (consolidated C# on SQL Server state)");
		}
	}

	/// <summary>
	/// Stage 4 (the ordering is the diagnostic instrument). Runs all three engines for every shared case in
	/// one pass and asserts the axes pairwise: Stage 1 (SQL Server procedure) == Stage 2 (consolidated C# on
	/// SQL Server state) is the LOGIC axis (engine held at SQL Server); Stage 2 == Stage 3 (consolidated C# on
	/// PostgreSQL) is the ENGINE axis (logic held constant). The first axis that diverges names the
	/// regression's cause, which is exactly the risk-axis disambiguation the staged sequence provides. Requires
	/// BOTH engines configured (SQL Server via <c>STOCKSHARP_LEGACY_MSSQL_CONNECTION</c>, PostgreSQL via
	/// <c>STOCKSHARP_LEGACY_SQL_CONNECTION</c>).
	/// </summary>
	[TestMethod]
	public async Task Stage4_StagedOrdering_AttributionMatrix()
	{
		await using var sqlServer = await OpenSqlServerAsync();
		await using var postgres = await OpenMandatoryAsync();

		if (postgres is null)
			return;

		foreach (var c in _cases)
		{
			var step1 = await SqlProcRecalcAsync(sqlServer, c);           // original SQL logic on SQL Server
			var step2 = await RecalcOnSqlServerStateAsync(sqlServer, c);  // consolidated C# on SQL Server state
			var step3 = await RecalcViaApplyOnPostgresAsync(postgres, c); // consolidated C# on PostgreSQL

			// Every engine must match the case's documented golden expectation ...
			AssertRecalcEqual(step1, c, "Step 1 (SQL Server procedure)");
			AssertRecalcEqual(step2, c, "Step 2 (Recalculate on SQL Server state)");
			AssertRecalcEqual(step3, c, "Step 3 (ApplyAsync on PostgreSQL)");

			// ... and the axes must agree pairwise so any regression is attributable to a named axis.
			if (step1 != step2)
				Fail($"LOGIC-axis regression on case '{c.Name}': Step 1 (proc)={FormatTriple(step1)} but Step 2 (Recalculate)={FormatTriple(step2)} - the ENGINE was held at SQL Server, so the consolidated C# LOGIC diverged from the retired procedure.");
			if (step2 != step3)
				Fail($"ENGINE-axis regression on case '{c.Name}': Step 2 (SQL Server state)={FormatTriple(step2)} but Step 3 (PostgreSQL)={FormatTriple(step3)} - the LOGIC was held constant, so the SQL Server -> PostgreSQL migration changed the result.");
		}
	}

	// ========================================================================================================
	// Staged-engine helpers: SQL Server golden baseline / state read (Stages 1/2/4), PostgreSQL apply
	// (Stages 3/4), the mandatory SQL Server guard, and the idempotent embedded-schema materializer.
	// ========================================================================================================

	/// <summary>Fails with a case- and engine-named message when the recomputed triple diverges from the
	/// shared case's golden <c>(qty, avg_price, realized_pnl)</c>; NUMERIC(18,4) -> decimal so parity is exact.</summary>
	private void AssertRecalcEqual((decimal Qty, decimal AvgPrice, decimal RealizedPnl) actual, RecalcCase c, string engine)
	{
		if (actual.Qty != c.ExpectedQty || actual.AvgPrice != c.ExpectedAvgPrice || actual.RealizedPnl != c.ExpectedRealizedPnl)
			Fail($"{engine}: case '{c.Name}' expected (qty={c.ExpectedQty}, avg={c.ExpectedAvgPrice}, rpnl={c.ExpectedRealizedPnl}) but got {FormatTriple(actual)}.");
	}

	/// <summary>Renders a recalculation triple for diagnostic failure messages.</summary>
	private static string FormatTriple((decimal Qty, decimal AvgPrice, decimal RealizedPnl) t)
		=> $"(qty={t.Qty}, avg={t.AvgPrice}, rpnl={t.RealizedPnl})";

	/// <summary>
	/// STAGE 1 per-case: seeds the scenario (portfolio, security, ACCEPTED order and, when non-flat, the
	/// existing position) in a transaction, executes the ORIGINAL <c>dbo.usp_RecalculatePositionOnTrade</c>,
	/// reads the persisted <c>dbo.Positions</c> row back, then rolls back. Returns the live golden triple.
	/// </summary>
	private async Task<(decimal Qty, decimal AvgPrice, decimal RealizedPnl)> SqlProcRecalcAsync(SqlConnection connection, RecalcCase c)
	{
		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken);
		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = await SqlSeedPortfolioAsync(connection, transaction, "RECALC_" + c.Name + "_" + suffix);
		var securityId = await SqlSeedSecurityAsync(connection, transaction, "SEC_" + suffix);
		var orderId = await SqlSeedOrderReturningIdAsync(connection, transaction, portfolioId, securityId, c.Side, c.TradeQty, c.TradePrice, "ACCEPTED");

		// Seed the EXISTING position only when non-flat (UPSERT UPDATE path); a flat case exercises the proc's INSERT path.
		if (c.ExistingQty != 0m || c.ExistingAvgPrice != 0m || c.ExistingRealizedPnl != 0m)
			await SqlSeedPositionAsync(connection, transaction, portfolioId, securityId, c.ExistingQty, c.ExistingAvgPrice, c.ExistingRealizedPnl);

		await using (var command = new SqlCommand(
			"EXEC dbo.usp_RecalculatePositionOnTrade @order_id = @order_id, @trade_qty = @trade_qty, @trade_price = @trade_price",
			connection, transaction))
		{
			command.Parameters.Add(SqlP("@order_id", SqlDbType.BigInt, orderId));
			command.Parameters.Add(SqlDec("@trade_qty", c.TradeQty));
			command.Parameters.Add(SqlDec("@trade_price", c.TradePrice));
			await command.ExecuteNonQueryAsync(CancellationToken);
		}

		var (qty, avgPrice, realizedPnl, _) = await ReadSqlServerPositionAsync(connection, transaction, portfolioId, securityId);
		await transaction.RollbackAsync(CancellationToken);
		return (qty, avgPrice, realizedPnl);
	}

	/// <summary>
	/// STAGE 2 per-case: seeds the SAME existing position on SQL Server, reads that state back FROM SQL Server
	/// (defaulting to flat when absent, exactly as the procedure does), then runs the REAL, pure
	/// <see cref="PositionRecalculationService.Recalculate"/> on that SQL-Server-read state. Rolls back.
	/// Holding the engine at SQL Server isolates the consolidated-logic axis.
	/// </summary>
	private async Task<(decimal Qty, decimal AvgPrice, decimal RealizedPnl)> RecalcOnSqlServerStateAsync(SqlConnection connection, RecalcCase c)
	{
		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken);
		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = await SqlSeedPortfolioAsync(connection, transaction, "RECALC2_" + c.Name + "_" + suffix);
		var securityId = await SqlSeedSecurityAsync(connection, transaction, "SEC_" + suffix);

		if (c.ExistingQty != 0m || c.ExistingAvgPrice != 0m || c.ExistingRealizedPnl != 0m)
			await SqlSeedPositionAsync(connection, transaction, portfolioId, securityId, c.ExistingQty, c.ExistingAvgPrice, c.ExistingRealizedPnl);

		// Read existing state back FROM SQL SERVER (engine held constant), then apply the consolidated C# logic.
		var (existingQty, existingAvgPrice, existingRealizedPnl, _) = await ReadSqlServerPositionAsync(connection, transaction, portfolioId, securityId);
		var result = PositionRecalculationService.Recalculate(
			existingQty, existingAvgPrice, existingRealizedPnl, c.Side, c.TradeQty, c.TradePrice);

		await transaction.RollbackAsync(CancellationToken);
		return result;
	}

	/// <summary>
	/// STAGE 3/4 per-case (PostgreSQL): seeds the scenario, drives the REAL
	/// <see cref="PositionRecalculationService.ApplyAsync"/> end to end inside one committed transaction, reads
	/// the persisted <c>positions</c> row back, and tears the scenario down. A non-flat case exercises the
	/// UPSERT UPDATE path, an all-flat case the UPSERT INSERT path.
	/// </summary>
	private async Task<(decimal Qty, decimal AvgPrice, decimal RealizedPnl)> RecalcViaApplyOnPostgresAsync(NpgsqlConnection cn, RecalcCase c)
	{
		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_stage3_" + c.Name + "_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, c.Side, c.TradeQty, c.TradePrice, "ACCEPTED");

			// Seed the EXISTING position only when it is non-flat: an all-zero case exercises the UPSERT
			// INSERT path, a non-flat case the UPSERT UPDATE path, so both DB paths are covered.
			if (c.ExistingQty != 0m || c.ExistingAvgPrice != 0m || c.ExistingRealizedPnl != 0m)
			{
				await InsertPositionAsync(cn, portfolioId, securityId,
					qty: c.ExistingQty, avgPrice: c.ExistingAvgPrice, realizedPnl: c.ExistingRealizedPnl, unrealizedPnl: 0m);
			}

			// Insert a REAL trades row first (durable single-apply guard, C3): ApplyAsync flips this row's
			// position_applied flag, so a persisted trade_id must exist. Mirrors the gateway inserting the
			// trade via INSERT ... RETURNING trade_id immediately before the apply.
			var tradeId = await InsertTradeAsync(cn, orderId, c.TradeQty, c.TradePrice);

			var svc = new PositionRecalculationService();
			await using (var txn = await cn.BeginTransactionAsync(CancellationToken))
			{
				// The gateway owns the transaction in production; here the test plays that role - begin,
				// ApplyAsync on (connection, transaction), commit once.
				await svc.ApplyAsync(cn, txn, orderId, tradeId, c.TradeQty, c.TradePrice, CancellationToken);
				await txn.CommitAsync(CancellationToken);
			}

			var position = await ReadPositionAsync(cn, portfolioId, securityId);
			return (position.Qty, position.AvgPrice, position.RealizedPnl);
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId);
		}
	}

	/// <summary>Reads the persisted <c>dbo.Positions</c> row (qty, avg_price, realized_pnl) for the
	/// (portfolio, security), returning <c>(0,0,0,false)</c> when no row exists - matching the procedure's
	/// own default-to-flat behaviour.</summary>
	private async Task<(decimal Qty, decimal AvgPrice, decimal RealizedPnl, bool Exists)> ReadSqlServerPositionAsync(SqlConnection connection, SqlTransaction transaction, int portfolioId, int securityId)
	{
		await using var command = new SqlCommand(
			"SELECT qty, avg_price, realized_pnl FROM dbo.Positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id",
			connection, transaction);
		command.Parameters.Add(SqlP("@portfolio_id", SqlDbType.Int, portfolioId));
		command.Parameters.Add(SqlP("@security_id", SqlDbType.Int, securityId));

		await using var reader = await command.ExecuteReaderAsync(CancellationToken);
		if (!await reader.ReadAsync(CancellationToken))
			return (0m, 0m, 0m, false);

		return (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2), true);
	}

	/// <summary>Inserts a SQL Server portfolio and returns its generated id.</summary>
	private async Task<int> SqlSeedPortfolioAsync(SqlConnection connection, SqlTransaction transaction, string name)
	{
		await using var command = new SqlCommand(
			"INSERT INTO dbo.Portfolios (name) OUTPUT INSERTED.portfolio_id VALUES (@name)", connection, transaction);
		command.Parameters.Add(SqlP("@name", SqlDbType.NVarChar, name));
		return (int)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Inserts a SQL Server security (board left NULL) and returns its generated id.</summary>
	private async Task<int> SqlSeedSecurityAsync(SqlConnection connection, SqlTransaction transaction, string code)
	{
		await using var command = new SqlCommand(
			"INSERT INTO dbo.Securities (security_code) OUTPUT INSERTED.security_id VALUES (@code)", connection, transaction);
		command.Parameters.Add(SqlP("@code", SqlDbType.NVarChar, code));
		return (int)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Inserts a SQL Server order and returns its generated <c>order_id</c> (the procedure looks up
	/// side/portfolio/security from it).</summary>
	private async Task<long> SqlSeedOrderReturningIdAsync(SqlConnection connection, SqlTransaction transaction, int portfolioId, int securityId, string side, decimal qty, decimal price, string status)
	{
		await using var command = new SqlCommand(
			"INSERT INTO dbo.Orders (portfolio_id, security_id, side, qty, price, order_type, status) " +
			"OUTPUT INSERTED.order_id VALUES (@portfolio_id, @security_id, @side, @qty, @price, 'LIMIT', @status)", connection, transaction);
		command.Parameters.Add(SqlP("@portfolio_id", SqlDbType.Int, portfolioId));
		command.Parameters.Add(SqlP("@security_id", SqlDbType.Int, securityId));
		command.Parameters.Add(SqlP("@side", SqlDbType.VarChar, side));
		command.Parameters.Add(SqlDec("@qty", qty));
		command.Parameters.Add(SqlDec("@price", price));
		command.Parameters.Add(SqlP("@status", SqlDbType.VarChar, status));
		return (long)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Seeds an existing SQL Server position row (unrealized_pnl defaulted to 0).</summary>
	private async Task SqlSeedPositionAsync(SqlConnection connection, SqlTransaction transaction, int portfolioId, int securityId, decimal qty, decimal avgPrice, decimal realizedPnl)
	{
		await using var command = new SqlCommand(
			"INSERT INTO dbo.Positions (portfolio_id, security_id, qty, avg_price, realized_pnl, unrealized_pnl) " +
			"VALUES (@portfolio_id, @security_id, @qty, @avg_price, @realized_pnl, 0)", connection, transaction);
		command.Parameters.Add(SqlP("@portfolio_id", SqlDbType.Int, portfolioId));
		command.Parameters.Add(SqlP("@security_id", SqlDbType.Int, securityId));
		command.Parameters.Add(SqlDec("@qty", qty));
		command.Parameters.Add(SqlDec("@avg_price", avgPrice));
		command.Parameters.Add(SqlDec("@realized_pnl", realizedPnl));
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Builds a SQL Server parameter, mapping a null value to <see cref="DBNull"/>.</summary>
	private static SqlParameter SqlP(string name, SqlDbType type, object value)
		=> new(name, type) { Value = value ?? DBNull.Value };

	/// <summary>Builds a decimal SQL Server parameter with explicit NUMERIC precision/scale (default 18,4) so
	/// the value is never truncated to scale 0 (a Microsoft.Data.SqlClient default that would loosen parity).</summary>
	private static SqlParameter SqlDec(string name, object value, byte precision = 18, byte scale = 4)
		=> new(name, SqlDbType.Decimal) { Precision = precision, Scale = scale, Value = value ?? DBNull.Value };

	/// <summary>
	/// Opens the live SQL Server connection the Stage 1/2/4 golden baseline requires and ensures the embedded
	/// characterization schema + retired procedures exist on it. Reports <c>Inconclusive</c> ONLY when
	/// <c>STOCKSHARP_LEGACY_MSSQL_CONNECTION</c> is unset (no SQL Server opted in for this run); FAILS when the
	/// variable IS set but SQL Server cannot be opened, so a configured golden-baseline milestone is always
	/// actually exercised (never silently skipped).
	/// </summary>
	private async Task<SqlConnection> OpenSqlServerAsync()
	{
		var connectionString = Environment.GetEnvironmentVariable("STOCKSHARP_LEGACY_MSSQL_CONNECTION");
		if (connectionString.IsEmpty())
		{
			Inconclusive("No SQL Server connection configured (STOCKSHARP_LEGACY_MSSQL_CONNECTION unset) - skipping staged SQL Server golden baseline.");
			return null; // unreachable (Inconclusive throws); required for definite assignment.
		}

		SqlConnection connection = null;
		try
		{
			connection = new SqlConnection(connectionString);
			await connection.OpenAsync(CancellationToken);
		}
		catch (Exception ex)
		{
			if (connection is not null)
				await connection.DisposeAsync();

			Fail($"STOCKSHARP_LEGACY_MSSQL_CONNECTION is configured but SQL Server is not reachable: {ex.Message}");
			return null; // unreachable (Fail throws); required for definite assignment.
		}

		await EnsureSqlServerSchemaAsync(connection);
		return connection;
	}

	// Guards a one-time, idempotent materialization of the embedded golden schema per test-run process.
	private static readonly SemaphoreSlim _sqlServerSchemaGate = new(1, 1);
	private static bool _sqlServerSchemaEnsured;

	/// <summary>
	/// Materializes the ORIGINAL SQL Server characterization schema and the two retired procedures on the
	/// given connection by executing the embedded <c>OriginalCharacterizationSetup.sql</c> resource. The
	/// script is idempotent (guarded CREATEs / CREATE OR ALTER), so re-runs are safe; a static flag avoids
	/// redundant executions within a single process. Runs each GO-delimited batch on the SAME connection.
	/// </summary>
	private async Task EnsureSqlServerSchemaAsync(SqlConnection connection)
	{
		if (_sqlServerSchemaEnsured)
			return;

		await _sqlServerSchemaGate.WaitAsync(CancellationToken);
		try
		{
			if (_sqlServerSchemaEnsured)
				return;

			var assembly = typeof(PositionRecalculationTests).Assembly;
			var resourceName = assembly.GetManifestResourceNames()
				.Single(name => name.EndsWith("OriginalCharacterizationSetup.sql", StringComparison.Ordinal));

			string script;
			await using (var stream = assembly.GetManifestResourceStream(resourceName))
			using (var reader = new StreamReader(stream))
				script = await reader.ReadToEndAsync(CancellationToken);

			foreach (var batch in SplitSqlBatches(script))
			{
				await using var command = new SqlCommand(batch, connection);
				await command.ExecuteNonQueryAsync(CancellationToken);
			}

			_sqlServerSchemaEnsured = true;
		}
		finally
		{
			_sqlServerSchemaGate.Release();
		}
	}

	/// <summary>Splits a T-SQL script on standalone <c>GO</c> batch separators (Microsoft.Data.SqlClient
	/// cannot execute <c>GO</c>), yielding each non-empty batch in order.</summary>
	private static IEnumerable<string> SplitSqlBatches(string script)
	{
		var batch = new StringBuilder();
		foreach (var line in script.Split('\n'))
		{
			if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
			{
				var text = batch.ToString().Trim();
				if (!text.IsEmpty())
					yield return text;
				batch.Clear();
			}
			else
			{
				batch.AppendLine(line);
			}
		}

		var tail = batch.ToString().Trim();
		if (!tail.IsEmpty())
			yield return tail;
	}

	// ========================================================================================================
	// Test infrastructure: mandatory-vs-optional database gate (F14) and seed / read / teardown helpers.
	// ========================================================================================================

	/// <summary>
	/// Opens the live PostgreSQL connection the MANDATORY Stage 3 / gateway integration tests require, using
	/// the same <see cref="SqlLegacyConnection.Resolve"/> string the production gateway uses (F14).
	/// </summary>
	/// <remarks>
	/// The database is a declared MANDATORY milestone when the connection environment variable
	/// <c>STOCKSHARP_LEGACY_SQL_CONNECTION</c> is set: if it is set but the database cannot be opened the run
	/// <see cref="BaseTestClass.Fail(string)">FAILS</see> - a required PostgreSQL parity milestone must never
	/// silently pass without exercising the engine. When the variable is NOT set the database was not opted
	/// into for this run, so the test is reported <see cref="BaseTestClass.Inconclusive(string)">Inconclusive</see>
	/// (an ordinary local build) rather than failed. Callers use the returned connection when non-null and
	/// otherwise return immediately; because <c>Inconclusive</c> throws, the non-null path is the only one a
	/// caller ever actually continues on.
	/// </remarks>
	/// <returns>An open <see cref="NpgsqlConnection"/>; or <see langword="null"/> only on the (throwing) skip path.</returns>
	private async Task<NpgsqlConnection> OpenMandatoryAsync()
	{
		// Gate on the ENVIRONMENT VARIABLE, not on whether Resolve() yields a string: Resolve() always has a
		// localhost fallback, so "is a database configured for THIS run" is decided by the opt-in variable.
		var configured = Environment.GetEnvironmentVariable("STOCKSHARP_LEGACY_SQL_CONNECTION");

		if (configured.IsEmpty())
		{
			Inconclusive(
				"PostgreSQL not configured (STOCKSHARP_LEGACY_SQL_CONNECTION unset); skipping the optional " +
				"local database stage. Set the variable to run the MANDATORY PostgreSQL parity/integration tests.");
			return null; // unreachable in practice (Inconclusive throws); required for definite assignment.
		}

		var connection = new NpgsqlConnection(SqlLegacyConnection.Resolve());

		try
		{
			await connection.OpenAsync(CancellationToken);
		}
		catch (Exception error)
		{
			await connection.DisposeAsync();

			// Declared but unreachable -> a MANDATORY milestone must FAIL, not skip.
			Fail(
				"STOCKSHARP_LEGACY_SQL_CONNECTION is set but the PostgreSQL database could not be opened, so " +
				"the mandatory parity/integration tests cannot run: " + error.Message);
			return null; // unreachable in practice (Fail throws); required for definite assignment.
		}

		return connection;
	}

	/// <summary>Inserts a portfolio with the given unique name and returns its generated id.</summary>
	private async Task<int> InsertPortfolioAsync(NpgsqlConnection connection, string name)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO portfolios (name) VALUES (@name) RETURNING portfolio_id", connection);
		command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Varchar) { Value = name });
		return (int)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Inserts a security with the given unique code (board left NULL) and returns its generated id.</summary>
	private async Task<int> InsertSecurityAsync(NpgsqlConnection connection, string code)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO securities (security_code) VALUES (@code) RETURNING security_id", connection);
		command.Parameters.Add(new NpgsqlParameter("code", NpgsqlDbType.Varchar) { Value = code });
		return (int)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Inserts an order with an explicit status and returns its generated id.</summary>
	private async Task<long> InsertOrderAsync(
		NpgsqlConnection connection, int portfolioId, int securityId, string side, decimal qty, decimal price, string status)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO orders (portfolio_id, security_id, side, qty, price, status) " +
			"VALUES (@portfolio_id, @security_id, @side, @qty, @price, @status) RETURNING order_id", connection);
		command.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
		command.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });
		command.Parameters.Add(new NpgsqlParameter("side", NpgsqlDbType.Varchar) { Value = side });
		command.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = qty });
		command.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = price });
		command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Varchar) { Value = status });
		return (long)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>
	/// Inserts a REAL trades row (RETURNING the generated trade_id) so a direct
	/// <see cref="PositionRecalculationService.ApplyAsync"/> call can exercise the DURABLE single-apply guard
	/// (C3): ApplyAsync flips this row's <c>trades.position_applied</c> flag FALSE -&gt; TRUE, so a persisted
	/// trade_id must exist for the apply to take effect. Auto-commits on <paramref name="connection"/> (no
	/// explicit transaction) so a subsequent transaction on the same connection sees the committed row -
	/// mirroring the gateway, which INSERTs the trade immediately before applying it.
	/// </summary>
	private async Task<long> InsertTradeAsync(NpgsqlConnection connection, long orderId, decimal qty, decimal price)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO trades (order_id, qty, price, executed_date) " +
			"VALUES (@order_id, @qty, @price, now() at time zone 'utc') RETURNING trade_id", connection);
		command.Parameters.Add(new NpgsqlParameter("order_id", NpgsqlDbType.Bigint) { Value = orderId });
		command.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = qty });
		command.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = price });
		return (long)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Reads the single trades.trade_id recorded for an order (used to re-target a persisted trade in C3 tests).</summary>
	private async Task<long> ReadOnlyTradeIdForOrderAsync(NpgsqlConnection connection, long orderId)
	{
		await using var command = new NpgsqlCommand(
			"SELECT trade_id FROM trades WHERE order_id = @order_id ORDER BY trade_id LIMIT 1", connection);
		command.Parameters.Add(new NpgsqlParameter("order_id", NpgsqlDbType.Bigint) { Value = orderId });
		return (long)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Reads a trade's durable single-apply flag (trades.position_applied), used by C3 idempotency assertions.</summary>
	private async Task<bool> ReadTradeAppliedAsync(NpgsqlConnection connection, long tradeId)
	{
		await using var command = new NpgsqlCommand(
			"SELECT position_applied FROM trades WHERE trade_id = @trade_id", connection);
		command.Parameters.Add(new NpgsqlParameter("trade_id", NpgsqlDbType.Bigint) { Value = tradeId });
		return (bool)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Reads a trade's PERSISTED NUMERIC(18,4) qty/price - used by the C5 sub-4dp persisted-value parity assertion.</summary>
	private async Task<(decimal Qty, decimal Price)> ReadTradeQtyPriceAsync(NpgsqlConnection connection, long tradeId)
	{
		await using var command = new NpgsqlCommand(
			"SELECT qty, price FROM trades WHERE trade_id = @trade_id", connection);
		command.Parameters.Add(new NpgsqlParameter("trade_id", NpgsqlDbType.Bigint) { Value = tradeId });
		await using var reader = await command.ExecuteReaderAsync(CancellationToken);
		(await reader.ReadAsync(CancellationToken)).AssertTrue();
		return (reader.GetDecimal(0), reader.GetDecimal(1));
	}

	/// <summary>
	/// Seeds an EXISTING position row so a subsequent apply exercises the UPSERT UPDATE path. There is no
	/// <c>cumulative_gross_notional</c> rollup column (C5): the pre-trade commission gate reads exact
	/// SUM(t.qty * t.price) straight from the trades rows, so no rollup is seeded or asserted here.
	/// </summary>
	private async Task InsertPositionAsync(
		NpgsqlConnection connection, int portfolioId, int securityId,
		decimal qty, decimal avgPrice, decimal realizedPnl, decimal unrealizedPnl)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO positions (portfolio_id, security_id, qty, avg_price, realized_pnl, unrealized_pnl) " +
			"VALUES (@portfolio_id, @security_id, @qty, @avg_price, @realized_pnl, @unrealized_pnl)", connection);
		command.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
		command.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });
		command.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = qty });
		command.Parameters.Add(new NpgsqlParameter("avg_price", NpgsqlDbType.Numeric) { Value = avgPrice });
		command.Parameters.Add(new NpgsqlParameter("realized_pnl", NpgsqlDbType.Numeric) { Value = realizedPnl });
		command.Parameters.Add(new NpgsqlParameter("unrealized_pnl", NpgsqlDbType.Numeric) { Value = unrealizedPnl });
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Reads back the persisted position row for parity assertions.</summary>
	private async Task<PositionRow> ReadPositionAsync(NpgsqlConnection connection, int portfolioId, int securityId)
	{
		await using var command = new NpgsqlCommand(
			"SELECT qty, avg_price, realized_pnl, unrealized_pnl FROM positions " +
			"WHERE portfolio_id = @portfolio_id AND security_id = @security_id", connection);
		command.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
		command.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });

		await using var reader = await command.ExecuteReaderAsync(CancellationToken);
		(await reader.ReadAsync(CancellationToken)).AssertTrue();

		return new PositionRow(
			reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3));
	}

	/// <summary>Counts the persisted trade rows for an order (single-apply / rollback structural checks).</summary>
	private async Task<int> CountTradesForOrderAsync(NpgsqlConnection connection, long orderId)
	{
		await using var command = new NpgsqlCommand(
			"SELECT COUNT(*) FROM trades WHERE order_id = @order_id", connection);
		command.Parameters.Add(new NpgsqlParameter("order_id", NpgsqlDbType.Bigint) { Value = orderId });
		return (int)(long)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>True when a position row exists for the (portfolio, security) - used by rollback assertions.</summary>
	private async Task<bool> PositionExistsAsync(NpgsqlConnection connection, int portfolioId, int securityId)
	{
		await using var command = new NpgsqlCommand(
			"SELECT EXISTS (SELECT 1 FROM positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id)",
			connection);
		command.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
		command.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });
		return (bool)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>
	/// A monotonic in-memory trade_id used ONLY where ApplyAsync is expected to THROW at order-resolution
	/// BEFORE it reaches the durable trades.position_applied guard (e.g. the non-existent-order rollback
	/// test), so no persisted trade row is required. Tests that expect the apply to SUCCEED must instead
	/// insert a real trades row via <see cref="InsertTradeAsync"/> and use the returned trade_id (C3).
	/// </summary>
	private static long NextTradeId() => Interlocked.Increment(ref _tradeIdSeq);

	private static long _tradeIdSeq = DateTime.UtcNow.Ticks;

	/// <summary>
	/// Removes everything a scenario seeded, in FK-safe order, so the shared database stays clean across
	/// re-runs. Scoped by the unique portfolio / security ids, guarded so an un-seeded id is a no-op. There
	/// is deliberately NO <c>processedtrades</c> delete - that ledger table no longer exists (F5).
	/// </summary>
	private async Task CleanupAsync(NpgsqlConnection connection, int portfolioId, int securityId)
	{
		if (portfolioId != 0)
		{
			await ExecuteAsync(connection,
				"DELETE FROM orderstatushistory WHERE order_id IN (SELECT order_id FROM orders WHERE portfolio_id = @p)", portfolioId);
			await ExecuteAsync(connection,
				"DELETE FROM trades WHERE order_id IN (SELECT order_id FROM orders WHERE portfolio_id = @p)", portfolioId);
			await ExecuteAsync(connection, "DELETE FROM positions WHERE portfolio_id = @p", portfolioId);
			await ExecuteAsync(connection, "DELETE FROM orders WHERE portfolio_id = @p", portfolioId);
		}

		if (securityId != 0)
			await ExecuteAsync(connection, "DELETE FROM securities WHERE security_id = @p", securityId);

		if (portfolioId != 0)
			await ExecuteAsync(connection, "DELETE FROM portfolios WHERE portfolio_id = @p", portfolioId);
	}

	/// <summary>Runs a single-parameter (<c>@p</c>) teardown statement.</summary>
	private async Task ExecuteAsync(NpgsqlConnection connection, string sql, int id)
	{
		await using var command = new NpgsqlCommand(sql, connection);
		command.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Integer) { Value = id });
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Persisted position projection used by the read-back parity assertions.</summary>
	private readonly record struct PositionRow(decimal Qty, decimal AvgPrice, decimal RealizedPnl, decimal UnrealizedPnl);
}
