namespace StockSharp.Tests;

using StockSharp.Algo.Risk;
using StockSharp.Algo.Storages.Sql;

using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Parity / characterization coverage for the average-cost position and realized-P&amp;L recomputation that the
/// refactor consolidated out of the retired SQL procedure <c>usp_RecalculatePositionOnTrade</c>
/// (<c>Database/002_StoredProcedures.sql</c>) into the canonical C# service
/// <see cref="PositionRecalculationService"/> under <c>Algo/Risk/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Mandated by the test-coverage rule (AAP 0.7.3) and the staged four-step testing sequence (AAP 0.6.3):
/// these tests prove the C# average-cost arithmetic matches the SQL golden baseline and lock two invariants —
/// <c>unrealized_pnl</c> is left untouched by recalculation (it is end-of-day / market-price driven), and the
/// single-apply invariant (applying the same <c>trade_id</c> twice must not double-count, since the position-
/// recalc DB trigger was removed).
/// </para>
/// <para>
/// Phase A/B tests exercise the pure, deterministic, database-free <see cref="PositionRecalculationService.Recalculate"/>
/// and are always green. Phase C exercises the real <see cref="PositionRecalculationService.ApplyAsync"/> against a
/// live PostgreSQL database (the step-3 engine-migration run) and reports <c>Inconclusive</c> — never a failure —
/// when no database is configured or reachable. Every money/quantity/price fixture is a <see cref="decimal"/>
/// literal (the <c>m</c> suffix) so the schema's <c>NUMERIC(18,4)</c> scale is preserved and a comparison can
/// never silently loosen.
/// </para>
/// </remarks>
[TestClass]
public class PositionRecalculationTests : BaseTestClass
{
	// ========================================================================================================
	// Phase A - pure Recalculate parity (the backbone; no database, always green).
	//
	// Each fixture is pre-verified against the SQL golden baseline in usp_RecalculatePositionOnTrade
	// (Database/002_StoredProcedures.sql ~L281-L320) with the sign convention 'B' = positive delta,
	// 'S' = negative delta. Inline arithmetic comments document the reconciliation at the point of use.
	// ========================================================================================================

	/// <summary>
	/// Opening a fresh long from a flat position. SQL golden baseline: the same-sign/flat branch
	/// (<c>@existingQty = 0</c>) where the weighted average of an empty position is just the trade price.
	/// </summary>
	[TestMethod]
	public void Recalculate_OpenFromFlat_Buy()
	{
		var (qty, avgPrice, realizedPnl) = PositionRecalculationService.Recalculate(
			existingQty: 0m, existingAvgPrice: 0m, existingRealizedPnl: 0m,
			side: "B", tradeQty: 100m, tradePrice: 150.00m);

		qty.AssertEqual(100m);          // 0 + 100 = 100
		avgPrice.AssertEqual(150.00m);  // (|0|*0 + 100*150.00) / |100| = 150.00
		realizedPnl.AssertEqual(0m);    // opening a position realizes nothing
	}

	/// <summary>
	/// Adding to an existing long at a higher price. SQL golden baseline: the same-sign branch takes the
	/// volume-weighted average price in and leaves realized P&amp;L unchanged.
	/// </summary>
	[TestMethod]
	public void Recalculate_SameSignAccumulation_WeightedAvg()
	{
		var (qty, avgPrice, realizedPnl) = PositionRecalculationService.Recalculate(
			existingQty: 100m, existingAvgPrice: 150.00m, existingRealizedPnl: 0m,
			side: "B", tradeQty: 100m, tradePrice: 160.00m);

		qty.AssertEqual(200m);          // 100 + 100 = 200
		avgPrice.AssertEqual(155.00m);  // (100*150 + 100*160) / 200 = 31000/200 = 155.00 (weighted average)
		realizedPnl.AssertEqual(0m);    // a same-sign add never realizes P&L
	}

	/// <summary>
	/// A sell that partially closes a long: realizes P&amp;L on the closed portion and leaves the average price
	/// unchanged. SQL golden baseline: opposite-sign branch, <c>@remainingQty = 0</c>, <c>@newQty != 0</c>.
	/// </summary>
	[TestMethod]
	public void Recalculate_PartialClose_RealizesPnl_AvgUnchanged()
	{
		var (qty, avgPrice, realizedPnl) = PositionRecalculationService.Recalculate(
			existingQty: 100m, existingAvgPrice: 150.00m, existingRealizedPnl: 0m,
			side: "S", tradeQty: 40m, tradePrice: 170.00m);

		qty.AssertEqual(60m);           // 100 + (-40) = 60
		avgPrice.AssertEqual(150.00m);  // partial close: average price is UNCHANGED while the position stays open
		realizedPnl.AssertEqual(800m);  // closingQty 40 * (170 - 150) * Sign(+100) = 40*20*(+1) = +800
	}

	/// <summary>
	/// A sell that exactly closes a long to flat: realizes P&amp;L and resets the average price to 0.
	/// SQL golden baseline: opposite-sign branch, <c>@remainingQty = 0</c>, <c>@newQty = 0</c>.
	/// </summary>
	[TestMethod]
	public void Recalculate_ExactClose_ResetsAvg()
	{
		var (qty, avgPrice, realizedPnl) = PositionRecalculationService.Recalculate(
			existingQty: 100m, existingAvgPrice: 150.00m, existingRealizedPnl: 0m,
			side: "S", tradeQty: 100m, tradePrice: 170.00m);

		qty.AssertEqual(0m);             // 100 + (-100) = 0 (flat)
		avgPrice.AssertEqual(0m);        // exact close: average price is meaningless with no open position -> 0
		realizedPnl.AssertEqual(2000m);  // closingQty 100 * (170 - 150) * Sign(+100) = 100*20*(+1) = +2000
	}

	/// <summary>
	/// A sell larger than the open long: closes the long, realizes its P&amp;L, and flips to a short whose
	/// residual takes the incoming trade price. SQL golden baseline: opposite-sign branch, <c>@remainingQty &gt; 0</c>.
	/// </summary>
	[TestMethod]
	public void Recalculate_Flip_ResidualTakesTradePrice()
	{
		var (qty, avgPrice, realizedPnl) = PositionRecalculationService.Recalculate(
			existingQty: 100m, existingAvgPrice: 150.00m, existingRealizedPnl: 0m,
			side: "S", tradeQty: 150m, tradePrice: 170.00m);

		qty.AssertEqual(-50m);           // closingQty 100, remaining 50; Sign(-150)*50 = -50 (flipped to short)
		avgPrice.AssertEqual(170.00m);   // on a flip the residual is a FRESH position at the incoming trade price
		realizedPnl.AssertEqual(2000m);  // closingQty 100 * (170 - 150) * Sign(+100) = 100*20*(+1) = +2000
	}

	/// <summary>
	/// Growing an existing short with another sell. SQL golden baseline: same-sign branch on the short side,
	/// proving the <c>ABS(...)</c> weighting works with a negative existing quantity.
	/// </summary>
	[TestMethod]
	public void Recalculate_ShortSameSignAccumulation_WeightedAvg()
	{
		var (qty, avgPrice, realizedPnl) = PositionRecalculationService.Recalculate(
			existingQty: -100m, existingAvgPrice: 150.00m, existingRealizedPnl: 0m,
			side: "S", tradeQty: 50m, tradePrice: 180.00m);

		qty.AssertEqual(-150m);         // -100 + (-50) = -150 (growing short)
		avgPrice.AssertEqual(160.00m);  // (|−100|*150 + 50*180) / |−150| = (15000+9000)/150 = 160.00
		realizedPnl.AssertEqual(0m);    // a same-sign add never realizes P&L
	}

	/// <summary>
	/// A buy that partially covers a short at a LOWER price: this is a PROFIT. SQL golden baseline: opposite-sign
	/// branch on the short side — the <c>Sign(existingQty)</c> term flips the sign so covering low earns money.
	/// </summary>
	[TestMethod]
	public void Recalculate_ShortPartialClose_RealizesPnl()
	{
		var (qty, avgPrice, realizedPnl) = PositionRecalculationService.Recalculate(
			existingQty: -100m, existingAvgPrice: 150.00m, existingRealizedPnl: 0m,
			side: "B", tradeQty: 40m, tradePrice: 130.00m);

		qty.AssertEqual(-60m);          // -100 + 40 = -60 (still short)
		avgPrice.AssertEqual(150.00m);  // partial close: average price is UNCHANGED while the short stays open
		realizedPnl.AssertEqual(800m);  // 40 * (130 - 150) * Sign(-100) = 40*(-20)*(-1) = +800 (covering low = profit)
	}

	/// <summary>
	/// A buy larger than the open short: covers the short (a profit here), then flips to a long whose residual
	/// takes the incoming trade price. SQL golden baseline: opposite-sign branch on the short side, <c>@remainingQty &gt; 0</c>.
	/// </summary>
	[TestMethod]
	public void Recalculate_ShortFlip_ResidualTakesTradePrice()
	{
		var (qty, avgPrice, realizedPnl) = PositionRecalculationService.Recalculate(
			existingQty: -100m, existingAvgPrice: 150.00m, existingRealizedPnl: 0m,
			side: "B", tradeQty: 150m, tradePrice: 130.00m);

		qty.AssertEqual(50m);            // closingQty 100, remaining 50; Sign(+150)*50 = +50 (flipped to long)
		avgPrice.AssertEqual(130.00m);   // on a flip the residual is a FRESH position at the incoming trade price
		realizedPnl.AssertEqual(2000m);  // closingQty 100 * (130 - 150) * Sign(-100) = 100*(-20)*(-1) = +2000
	}

	/// <summary>
	/// The demo's observable outcome: recording a trade against an accepted order updates the position
	/// automatically. Flat + BUY 100 @ 150 opens a long of 100 @ 150 — the same case the sample demonstrates
	/// (Samples/08_Misc/03_LegacySqlDemo/Program.cs), kept explicit here as a named characterization test.
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
	/// Focused proof of the sign convention: <c>'B'</c> (buy) contributes a POSITIVE delta and <c>'S'</c> (sell)
	/// a NEGATIVE delta. SQL golden baseline: <c>@tradeSignedQty = CASE WHEN @side = 'B' THEN @trade_qty ELSE -@trade_qty END</c>.
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

	// ========================================================================================================
	// Phase B - the unrealized_pnl-untouched invariant (pure, always green).
	// ========================================================================================================

	/// <summary>
	/// Documents and proves that recalculation never computes an <c>unrealized_pnl</c>: the pure
	/// <see cref="PositionRecalculationService.Recalculate"/> contract returns only
	/// <c>(Qty, AvgPrice, RealizedPnl)</c>, so by construction it cannot produce an unrealized value. Realized
	/// P&amp;L is the only P&amp;L the math touches — unchanged on an add, changed only on a close.
	/// </summary>
	[TestMethod]
	public void Recalculate_DoesNotProduceUnrealizedPnl()
	{
		// unrealized_pnl is end-of-day / market-price driven and is deliberately NOT maintained by
		// recalculation (SQL parity: usp_RecalculatePositionOnTrade leaves unrealized_pnl alone). The
		// DB-level proof that the STORED unrealized_pnl column is untouched lives in the guarded Phase C
		// test ApplyAsync_LeavesUnrealizedPnlUntouched.

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

	[TestMethod]
	public void Recalculate_ZeroQtyFromFlat_NoThrow_IsNoOp()
	{
		// INFO guard (defensive totality): a zero-quantity trade against a FLAT position would compute 0/0 in
		// the weighted-average branch and throw DivideByZeroException. ApplyAsync can never feed this (the DB
		// CHECK qty > 0 rejects a zero-qty trade before it reaches the service), but the PURE method must be
		// TOTAL - so a zero-qty trade is a no-op that returns the position unchanged instead of throwing.
		var fromFlat = PositionRecalculationService.Recalculate(
			existingQty: 0m, existingAvgPrice: 0m, existingRealizedPnl: 0m,
			side: "B", tradeQty: 0m, tradePrice: 0m);

		fromFlat.Qty.AssertEqual(0m);          // still flat
		fromFlat.AvgPrice.AssertEqual(0m);     // no average to form
		fromFlat.RealizedPnl.AssertEqual(0m);  // nothing realized

		// A zero-qty trade against a NON-flat position is likewise a no-op that preserves existing state.
		var nonFlat = PositionRecalculationService.Recalculate(
			existingQty: 100m, existingAvgPrice: 150.00m, existingRealizedPnl: 55m,
			side: "S", tradeQty: 0m, tradePrice: 170.00m);

		nonFlat.Qty.AssertEqual(100m);          // unchanged
		nonFlat.AvgPrice.AssertEqual(150.00m);  // unchanged
		nonFlat.RealizedPnl.AssertEqual(55m);   // unchanged
	}

	// ========================================================================================================
	// Phase C - guarded PostgreSQL integration (step-3 engine-migration run).
	//
	// These exercise the REAL PositionRecalculationService.ApplyAsync against a live PostgreSQL database and
	// MUST report Inconclusive (never fail) when no database is available. They seed only what each scenario
	// needs with unique natural keys - including UNIQUE trade ids, because the single-apply guard is now a
	// DURABLE processedtrades ledger row (not process-local), so a fixed id would survive across runs - for
	// re-run safety, and tear everything down (positions, orders, securities, portfolios AND the
	// processedtrades ledger rows) in a finally.
	// ========================================================================================================

	/// <summary>
	/// Single-apply invariant (same instance): the position-recalc DB trigger was removed, so the service
	/// claims each applied <c>trade_id</c> in the durable <c>processedtrades</c> ledger inside its own
	/// transaction; re-applying the SAME <c>trade_id</c> is an idempotent no-op, so the position is not
	/// double-counted. The durable CROSS-instance/restart proof is
	/// <see cref="ApplyAsync_CrossInstanceSameTrade_NoDoubleCount"/> and the concurrency proof is
	/// <see cref="ApplyAsync_ConcurrentDifferentTrades_Accumulate"/>.
	/// </summary>
	[TestMethod]
	public async Task ApplyAsync_SingleApply_NoDoubleCount()
	{
		await using var cn = await TryOpenAsync();

		// TryOpenAsync reports Inconclusive (which throws) when no DB is available; the null guard is a
		// belt-and-braces fallback so this test can never NullReference or fail on a missing database.
		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		// The single-apply guard is keyed on trade_id, so BOTH calls MUST use the SAME value for the second to
		// be recognised as a repeat. The guard is now DURABLE (a processedtrades ledger row), so the id must
		// be UNIQUE PER RUN and purged in cleanup - otherwise a re-run would find it already applied.
		var tradeId = NextTradeId();

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_recalc_single_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");

			var svc = new PositionRecalculationService();

			// First apply: opens the position from flat -> qty 100 @ 150.
			await svc.ApplyAsync(cn, orderId, tradeId, 100m, 150.00m, CancellationToken);
			// Second apply with the SAME trade_id: must be an idempotent no-op (single-apply guard).
			await svc.ApplyAsync(cn, orderId, tradeId, 100m, 150.00m, CancellationToken);

			var position = await ReadPositionAsync(cn, portfolioId, securityId);

			position.Qty.AssertEqual(100m);           // NOT 200 - the second apply was ignored (no double-count)
			position.AvgPrice.AssertEqual(150.0000m); // opened at 150; the repeat did not re-average it
			position.RealizedPnl.AssertEqual(0m);     // opening a position realizes nothing
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId, tradeId);
		}
	}

	/// <summary>
	/// The stored <c>unrealized_pnl</c> column is left untouched by recalculation: it is EOD / market-price
	/// driven and the average-cost recompute must never overwrite it. Seeds a position carrying a sentinel
	/// <c>unrealized_pnl</c>, applies a trade, and asserts qty/avg/realized update while the sentinel survives.
	/// </summary>
	[TestMethod]
	public async Task ApplyAsync_LeavesUnrealizedPnlUntouched()
	{
		await using var cn = await TryOpenAsync();

		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		// Unique per run: the single-apply guard is a durable processedtrades ledger row, so a fixed id would
		// survive across runs and skip the apply. Purged in cleanup.
		var tradeId = NextTradeId();

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_recalc_unreal_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");

			// Seed an EXISTING flat position carrying a sentinel unrealized_pnl the recalc must never overwrite.
			// The service's UPSERT hits ON CONFLICT DO UPDATE (the row already exists) and deliberately OMITS
			// unrealized_pnl from the SET list, so the sentinel must survive.
			const decimal sentinelUnrealized = 123.4500m;
			await InsertPositionAsync(cn, portfolioId, securityId,
				qty: 0m, avgPrice: 0m, realizedPnl: 0m, unrealizedPnl: sentinelUnrealized);

			var svc = new PositionRecalculationService();
			await svc.ApplyAsync(cn, orderId, tradeId, 100m, 150.00m, CancellationToken);

			var position = await ReadPositionAsync(cn, portfolioId, securityId);

			// qty / avg_price / realized_pnl are updated by the recalc ...
			position.Qty.AssertEqual(100m);           // 0 + 100
			position.AvgPrice.AssertEqual(150.0000m); // (|0|*0 + 100*150)/100 = 150
			position.RealizedPnl.AssertEqual(0m);     // opening a position realizes nothing
			// ... but the stored unrealized_pnl is STILL the sentinel (recalculation left it untouched).
			position.UnrealizedPnl.AssertEqual(sentinelUnrealized);
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId, tradeId);
		}
	}

	/// <summary>
	/// Single-apply invariant ACROSS service instances / process restarts - the first original CRITICAL. The
	/// guard used to be a process-local <c>HashSet</c>, so a SECOND instance (modelling a restarted process
	/// with fresh in-memory state) re-applied the SAME trade and DOUBLE-COUNTED the position (observed qty
	/// 200). With the durable <c>processedtrades</c> ledger the repeat is recognised regardless of which
	/// instance applies it, so the position stays at qty 100.
	/// </summary>
	[TestMethod]
	public async Task ApplyAsync_CrossInstanceSameTrade_NoDoubleCount()
	{
		await using var cn = await TryOpenAsync();

		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;
		var tradeId = NextTradeId();

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_recalc_xinst_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");

			// Instance A opens the position from flat -> qty 100 @ 150.
			var svcA = new PositionRecalculationService();
			await svcA.ApplyAsync(cn, orderId, tradeId, 100m, 150.00m, CancellationToken);

			var afterA = await ReadPositionAsync(cn, portfolioId, securityId);
			afterA.Qty.AssertEqual(100m); // first apply took effect

			// A DISTINCT instance (fresh, empty in-memory state - as after a process restart) applies the SAME
			// trade_id. The old process-local guard MISSED this and double-counted to 200; the durable
			// processedtrades ledger recognises the repeat and makes it an idempotent no-op.
			var svcB = new PositionRecalculationService();
			await svcB.ApplyAsync(cn, orderId, tradeId, 100m, 150.00m, CancellationToken);

			var afterB = await ReadPositionAsync(cn, portfolioId, securityId);
			afterB.Qty.AssertEqual(100m);           // NOT 200 - durable single-apply held across instances
			afterB.AvgPrice.AssertEqual(150.0000m); // unchanged by the ignored repeat
			afterB.RealizedPnl.AssertEqual(0m);     // opening a position realizes nothing
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId, tradeId);
		}
	}

	/// <summary>
	/// Concurrency / lost-update invariant - the second original CRITICAL. Two DIFFERENT trades on the SAME
	/// (portfolio, security) applied CONCURRENTLY used to race on an unsynchronised read-modify-write, and the
	/// absolute UPSERT overwrote one update so the observed qty was 100 instead of 200 (a lost update). Each
	/// apply now runs in its own transaction and serialises on a per-(portfolio, security)
	/// <c>pg_advisory_xact_lock</c>, so both updates land and the position accumulates to qty 200.
	/// </summary>
	[TestMethod]
	public async Task ApplyAsync_ConcurrentDifferentTrades_Accumulate()
	{
		await using var cn = await TryOpenAsync();

		if (cn is null)
			return;

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;
		var tradeIdA = NextTradeId();
		var tradeIdB = NextTradeId();

		try
		{
			portfolioId = await InsertPortfolioAsync(cn, "pf_recalc_conc_" + suffix);
			securityId = await InsertSecurityAsync(cn, "sec_" + suffix);
			// Both trades reference one accepted BUY order; ApplyAsync reads only (portfolio, security, side)
			// from it, so a single order is enough and both applies target the SAME position row.
			var orderId = await InsertOrderAsync(cn, portfolioId, securityId, "B", 100m, 150.00m, "ACCEPTED");

			// Two DISTINCT connections: ApplyAsync opens its own transaction, and a single NpgsqlConnection
			// cannot run two overlapping transactions, so genuine concurrency needs one connection each.
			await using var cnA = new NpgsqlConnection(SqlLegacyConnection.Resolve());
			await using var cnB = new NpgsqlConnection(SqlLegacyConnection.Resolve());
			await cnA.OpenAsync(CancellationToken);
			await cnB.OpenAsync(CancellationToken);

			var svcA = new PositionRecalculationService();
			var svcB = new PositionRecalculationService();

			// Fire both applies concurrently against the SAME (portfolio, security). Without per-key
			// serialisation these race and one update is lost (observed qty 100); with the advisory lock they
			// serialise and both land.
			var applyA = svcA.ApplyAsync(cnA, orderId, tradeIdA, 100m, 150.00m, CancellationToken);
			var applyB = svcB.ApplyAsync(cnB, orderId, tradeIdB, 100m, 150.00m, CancellationToken);
			await Task.WhenAll(applyA, applyB);

			var position = await ReadPositionAsync(cn, portfolioId, securityId);

			position.Qty.AssertEqual(200m);           // 100 + 100 - NEITHER update lost
			position.AvgPrice.AssertEqual(150.0000m); // both at 150 -> weighted average still 150
			position.RealizedPnl.AssertEqual(0m);     // two same-side opens realize nothing
		}
		finally
		{
			await CleanupAsync(cn, portfolioId, securityId, tradeIdA, tradeIdB);
		}
	}

	// --------------------------------------------------------------------------------------------------------
	// Phase C helpers (private -> not part of the CLS-compliant public API surface, which lets them safely
	// take/return the non-CLS-compliant Npgsql provider types). All DB access flows through the inherited
	// CancellationToken so the run can be cancelled cleanly.
	// --------------------------------------------------------------------------------------------------------

	/// <summary>
	/// Opens a PostgreSQL connection for the guarded parity tests, or reports <c>Inconclusive</c> (never a
	/// failure) when no database is configured or reachable. Checks the environment variable FIRST so a missing
	/// configuration skips fast instead of paying the multi-second Npgsql connect timeout.
	/// </summary>
	private async Task<NpgsqlConnection> TryOpenAsync()
	{
		// Mirrors the env-var name owned by SqlLegacyConnection (STOCKSHARP_LEGACY_SQL_CONNECTION): if it is
		// unset the parity DB is not provisioned, so skip rather than fail.
		if (Environment.GetEnvironmentVariable("STOCKSHARP_LEGACY_SQL_CONNECTION").IsEmpty())
		{
			Inconclusive("No PostgreSQL connection configured (STOCKSHARP_LEGACY_SQL_CONNECTION unset) - skipping DB parity test.");
			return null;
		}

		try
		{
			var cn = new NpgsqlConnection(SqlLegacyConnection.Resolve());
			await cn.OpenAsync(CancellationToken);
			return cn;
		}
		catch (Exception ex)
		{
			Inconclusive($"PostgreSQL not reachable - skipping DB parity test: {ex.Message}");
			return null;
		}
	}

	/// <summary>Inserts a portfolio with a unique name and returns its generated identifier.</summary>
	private async Task<int> InsertPortfolioAsync(NpgsqlConnection cn, string name)
	{
		using var cmd = new NpgsqlCommand(
			"INSERT INTO portfolios (name) VALUES (@name) RETURNING portfolio_id", cn);
		cmd.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Varchar) { Value = name });
		return Convert.ToInt32(await cmd.ExecuteScalarAsync(CancellationToken));
	}

	/// <summary>Inserts a security with a unique code and returns its generated identifier.</summary>
	private async Task<int> InsertSecurityAsync(NpgsqlConnection cn, string code)
	{
		using var cmd = new NpgsqlCommand(
			"INSERT INTO securities (security_code) VALUES (@code) RETURNING security_id", cn);
		cmd.Parameters.Add(new NpgsqlParameter("code", NpgsqlDbType.Varchar) { Value = code });
		return Convert.ToInt32(await cmd.ExecuteScalarAsync(CancellationToken));
	}

	/// <summary>Inserts an order (order_type LIMIT) and returns its generated identifier.</summary>
	private async Task<long> InsertOrderAsync(
		NpgsqlConnection cn, int portfolioId, int securityId, string side, decimal qty, decimal price, string status)
	{
		using var cmd = new NpgsqlCommand(
			"INSERT INTO orders (portfolio_id, security_id, side, qty, price, order_type, status) " +
			"VALUES (@pid, @sid, @side, @qty, @price, 'LIMIT', @status) RETURNING order_id", cn);
		cmd.Parameters.Add(new NpgsqlParameter("pid", NpgsqlDbType.Integer) { Value = portfolioId });
		cmd.Parameters.Add(new NpgsqlParameter("sid", NpgsqlDbType.Integer) { Value = securityId });
		cmd.Parameters.Add(new NpgsqlParameter("side", NpgsqlDbType.Varchar) { Value = side });
		cmd.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = qty });
		cmd.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = price });
		cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Varchar) { Value = status });
		return Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken));
	}

	/// <summary>Inserts a position row (used to seed a sentinel <c>unrealized_pnl</c> for the untouched test).</summary>
	private async Task InsertPositionAsync(
		NpgsqlConnection cn, int portfolioId, int securityId, decimal qty, decimal avgPrice, decimal realizedPnl, decimal unrealizedPnl)
	{
		using var cmd = new NpgsqlCommand(
			"INSERT INTO positions (portfolio_id, security_id, qty, avg_price, realized_pnl, unrealized_pnl) " +
			"VALUES (@pid, @sid, @qty, @avg, @realized, @unrealized)", cn);
		cmd.Parameters.Add(new NpgsqlParameter("pid", NpgsqlDbType.Integer) { Value = portfolioId });
		cmd.Parameters.Add(new NpgsqlParameter("sid", NpgsqlDbType.Integer) { Value = securityId });
		cmd.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = qty });
		cmd.Parameters.Add(new NpgsqlParameter("avg", NpgsqlDbType.Numeric) { Value = avgPrice });
		cmd.Parameters.Add(new NpgsqlParameter("realized", NpgsqlDbType.Numeric) { Value = realizedPnl });
		cmd.Parameters.Add(new NpgsqlParameter("unrealized", NpgsqlDbType.Numeric) { Value = unrealizedPnl });
		await cmd.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Reads back the (single) position row for a portfolio/security, asserting it exists.</summary>
	private async Task<(decimal Qty, decimal AvgPrice, decimal RealizedPnl, decimal UnrealizedPnl)> ReadPositionAsync(
		NpgsqlConnection cn, int portfolioId, int securityId)
	{
		using var cmd = new NpgsqlCommand(
			"SELECT qty, avg_price, realized_pnl, unrealized_pnl FROM positions " +
			"WHERE portfolio_id = @pid AND security_id = @sid", cn);
		cmd.Parameters.Add(new NpgsqlParameter("pid", NpgsqlDbType.Integer) { Value = portfolioId });
		cmd.Parameters.Add(new NpgsqlParameter("sid", NpgsqlDbType.Integer) { Value = securityId });

		await using var reader = await cmd.ExecuteReaderAsync(CancellationToken);
		(await reader.ReadAsync(CancellationToken)).AssertTrue(); // the position row must exist after an apply

		// NUMERIC(18,4) columns -> decimal (never double) so the >= semantics downstream cannot loosen.
		return (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3));
	}

	/// <summary>
	/// Generates a process-unique, positive <c>trade_id</c> for the durable single-apply ledger. Because the
	/// guard is now a persisted <c>processedtrades</c> row (not process-local state), a hard-coded id would
	/// survive across runs and make a re-run's first apply look like an already-applied repeat; a fresh random
	/// id per test keeps every run independent. Bounded well inside <c>BIGINT</c> and far from any seeded ids.
	/// </summary>
	private static long NextTradeId() => Random.Shared.NextInt64(1_000_000_000L, long.MaxValue);

	/// <summary>
	/// Removes every row seeded for a portfolio/security in FK-safe order (children first) so the shared
	/// dev/CI database stays clean across re-runs, and purges the durable <c>processedtrades</c> ledger rows
	/// for the supplied trade ids so a re-run does not see them already applied. Safe to call with zero ids
	/// (deletes nothing).
	/// </summary>
	private async Task CleanupAsync(NpgsqlConnection cn, int portfolioId, int securityId, params long[] tradeIds)
	{
		using var cmd = new NpgsqlCommand(
			"DELETE FROM trades WHERE order_id IN (SELECT order_id FROM orders WHERE portfolio_id = @pid); " +
			"DELETE FROM orderstatushistory WHERE order_id IN (SELECT order_id FROM orders WHERE portfolio_id = @pid); " +
			"DELETE FROM positions WHERE portfolio_id = @pid; " +
			"DELETE FROM orders WHERE portfolio_id = @pid; " +
			"DELETE FROM securities WHERE security_id = @sid; " +
			"DELETE FROM portfolios WHERE portfolio_id = @pid; " +
			"DELETE FROM processedtrades WHERE trade_id = ANY(@ids);", cn);
		cmd.Parameters.Add(new NpgsqlParameter("pid", NpgsqlDbType.Integer) { Value = portfolioId });
		cmd.Parameters.Add(new NpgsqlParameter("sid", NpgsqlDbType.Integer) { Value = securityId });
		// The durable single-apply ledger (processedtrades) has NO FK to trades, so it must be purged
		// explicitly; ANY over a (possibly empty) bigint[] deletes exactly the ids this test claimed.
		cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = tradeIds ?? [] });
		await cmd.ExecuteNonQueryAsync(CancellationToken);
	}
}
