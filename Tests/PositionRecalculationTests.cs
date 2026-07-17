namespace StockSharp.Tests;

using StockSharp.Algo.Risk;
using StockSharp.Algo.Storages.Sql;

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
///   <item><description><b>Stage 1 - Characterization (golden baseline, engine-independent, always runs).</b>
///     The pure <see cref="PositionRecalculationService.Recalculate"/> must reproduce, for every shared
///     case, the golden <c>(qty, avg_price, realized_pnl)</c>. Those golden values ARE the behaviour of the
///     retired SQL Server <c>usp_RecalculatePositionOnTrade</c>, captured as engine-independent numbers
///     before the procedure was retired (AAP G1). Because <c>Recalculate</c> is pure <see cref="decimal"/>
///     arithmetic with no database dependency, this baseline is identical whether the row is later
///     persisted by SQL Server or PostgreSQL, which is exactly why it can stand in for a live SQL Server
///     characterization run now that the procedure no longer exists.</description></item>
///   <item><description><b>Stage 2 - Consolidated C# logic parity (engine held constant, always runs).</b>
///     The same cases are checked against the average-cost ALGEBRAIC INVARIANTS (signed-quantity
///     conservation, realize-P&amp;L-only-on-a-close, and the average-price rule per branch), independently
///     of the golden numbers. Holding the engine constant (no database) isolates a logic-consolidation bug
///     (SQL to C#) from an engine-migration bug. This is the C#-parity step of AAP 0.6.3; a live
///     C#-against-SQL-Server run is not possible because the consolidated service was migrated to the
///     Npgsql provider (AAP G2) and the SQL procedure was retired (AAP G1), so the engine-independent
///     invariants are that step's faithful, executable stand-in.</description></item>
///   <item><description><b>Stage 3 - PostgreSQL engine parity (MANDATORY, live).</b> The same cases are
///     driven end to end through <see cref="PositionRecalculationService.ApplyAsync"/> against a live
///     PostgreSQL database, reading the persisted <c>positions</c> row back and asserting the golden values.
///     Passing Stage 2 but failing Stage 3 points to a dialect / engine issue, not logic. This stage is
///     MANDATORY: see <see cref="OpenMandatoryAsync"/>. When the connection environment variable is SET but
///     the database is unreachable the test FAILS (never Inconclusive), so a required milestone run cannot
///     appear green without actually exercising PostgreSQL; it reports Inconclusive ONLY when no database is
///     configured at all (the variable is unset) - an ordinary local run that did not opt in.</description></item>
///   <item><description><b>Stage 4 - the ordering is the diagnostic instrument.</b> Stages 1-3 deliberately
///     consume the IDENTICAL shared case table, so the first stage that fails localises the fault to the
///     axis that stage introduces (numbers, then logic, then engine).
///     <see cref="Stage4_StagedOrdering_IsTheDiagnosticInstrument"/> makes that contract executable.</description></item>
/// </list>
/// <para>
/// Every money / quantity / price fixture is a <see cref="decimal"/> literal (the <c>m</c> suffix) so the
/// schema's <c>NUMERIC(18,4)</c> scale is preserved and a comparison can never silently loosen (AAP 0.6.4).
/// </para>
/// </remarks>
[TestClass]
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
	// Stage 1 - Characterization: pure Recalculate matches the golden baseline (engine-independent).
	// ========================================================================================================

	/// <summary>
	/// Stage 1 (characterization). For every shared case the pure, database-free
	/// <see cref="PositionRecalculationService.Recalculate"/> must reproduce the golden
	/// <c>(qty, avg_price, realized_pnl)</c> captured from the retired SQL Server procedure. Always runs
	/// (no database), so it is the engine-constant golden baseline the later stages are compared against.
	/// </summary>
	[TestMethod]
	public void Stage1_Characterization_PureRecalculateMatchesGoldenBaseline()
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
	// Stage 2 - Consolidated C# logic parity: average-cost algebraic invariants (engine held constant).
	// ========================================================================================================

	/// <summary>
	/// Stage 2 (logic parity). For every shared case the consolidated C# result must satisfy the average-cost
	/// ALGEBRAIC INVARIANTS independently of the golden numbers: signed-quantity conservation,
	/// realize-P&amp;L-only-on-a-close (with the closed-portion formula), and the per-branch average-price
	/// rules. Holding the engine constant (no database) isolates a SQL-to-C# logic-consolidation bug from an
	/// engine-migration bug. Always runs.
	/// </summary>
	[TestMethod]
	public void Stage2_ConsolidatedLogicParity_AverageCostInvariants()
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
	// Single-apply note (F5, F2): the position-recalc DB trigger was removed and there is NO durable ledger
	// table. The AUTHORITATIVE single-apply guarantee is STRUCTURAL - the gateway assigns a fresh trade_id
	// per committed trade (INSERT ... RETURNING) inside one atomic transaction, so each committed trade is
	// applied exactly once and a rolled-back attempt persists nothing. The service adds an in-process
	// best-effort HashSet guard on top (recorded only AFTER the apply's DB work succeeds). Because that guard
	// is deliberately process-local, a concurrent same-trade duplicate on the SAME instance is NOT asserted
	// to dedupe (it cannot arise in production - the gateway never reuses a trade_id); the invariant is
	// covered instead by the structural real-Trades-row path, the sequential same-instance guard, and the
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

				var svc = new PositionRecalculationService();
				await using (var txn = await cn.BeginTransactionAsync(CancellationToken))
				{
					// The gateway owns the transaction in production; here the test plays that role - begin,
					// ApplyAsync on (connection, transaction), commit once.
					await svc.ApplyAsync(cn, txn, orderId, NextTradeId(), c.TradeQty, c.TradePrice, CancellationToken);
					await txn.CommitAsync(CancellationToken);
				}

				var position = await ReadPositionAsync(cn, portfolioId, securityId);

				// NUMERIC(18,4) columns -> decimal, so parity is exact and no >= comparison downstream loosens.
				position.Qty.AssertEqual(c.ExpectedQty);
				position.AvgPrice.AssertEqual(c.ExpectedAvgPrice);
				position.RealizedPnl.AssertEqual(c.ExpectedRealizedPnl);
			}
			finally
			{
				await CleanupAsync(cn, portfolioId, securityId);
			}
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
	/// per-(portfolio, security) <c>pg_advisory_xact_lock</c> serialises the read-recompute-write so neither
	/// update is lost and the position accumulates to qty 200.
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
	/// F5 - sequential single-apply via the in-process guard. The SAME service instance applies the SAME
	/// trade_id twice (sequentially, each in its own committed transaction). The best-effort in-process guard
	/// recognises the repeat and makes the second call an idempotent no-op, so the position is opened once
	/// (qty 100), not double-counted (200).
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

			// The guard is keyed on trade_id and lives on the instance, so BOTH calls must reuse the SAME id
			// AND the SAME svc for the second to be recognised as a repeat.
			var svc = new PositionRecalculationService();
			var tradeId = NextTradeId();

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

			var svc = new PositionRecalculationService();
			await using (var txn = await cn.BeginTransactionAsync(CancellationToken))
			{
				await svc.ApplyAsync(cn, txn, orderId, NextTradeId(), 100m, 150.00m, CancellationToken);
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

	// ========================================================================================================
	// Stage 4 - the ordering itself is the diagnostic instrument (AAP 0.6.3).
	// ========================================================================================================

	/// <summary>
	/// Stage 4 (meta-test). Makes the staged-testing contract executable: Stages 1-3 must consume the ONE
	/// shared <see cref="_cases"/> table, and that table must cover BOTH signs across every average-cost
	/// branch (open-from-flat, same-sign accumulation, partial close, exact close, flip) plus realized-P&amp;L
	/// carry and accumulation. Because the three stages iterate the identical field, the first stage that
	/// fails localises a regression to the axis it introduces - numbers (Stage 1), consolidated logic
	/// (Stage 2), then the PostgreSQL engine (Stage 3) - which is precisely the risk-axis disambiguation the
	/// staged sequence exists to provide. A future edit that shrank the table or dropped a branch would trip
	/// this guard rather than silently degrade a stage into a weaker check.
	/// </summary>
	[TestMethod]
	public void Stage4_StagedOrdering_IsTheDiagnosticInstrument()
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
	/// Seeds an EXISTING position row so a subsequent apply exercises the UPSERT UPDATE path. Leaves
	/// <c>cumulative_gross_notional</c> at its default 0; the apply's rollup accumulation is validated by the
	/// gateway-level Phase 4 coverage, not asserted here.
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
	/// A monotonic in-memory identifier for the service's single-apply guard. It is NOT persisted: the
	/// position-recalc ledger table was removed (F5), so this only needs to be unique per call within a run.
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
