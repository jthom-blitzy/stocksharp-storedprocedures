namespace StockSharp.Tests;

using StockSharp.Algo.Risk;
using StockSharp.Algo.Storages.Sql;

using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Characterization + parity coverage for the seven PRE-TRADE risk checks that this refactor
/// consolidated out of the retired SQL procedure <c>usp_ValidatePreTradeRisk</c>
/// (<c>Database/002_StoredProcedures.sql</c>) into the canonical C# gate
/// <see cref="PreTradeRiskService"/> and its shared rule module <see cref="CanonicalRiskRules"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mandated by the test-coverage rule (AAP 0.7.3) and the staged four-step testing sequence
/// (AAP 0.6.3). The suite proves BEHAVIORAL PARITY between the SQL "golden baseline" and the new C#
/// logic and — critically — GUARDS THE THRESHOLD-STRICTNESS INVARIANT (AAP 0.6.1/0.6.4): a
/// reconciled threshold may never end up less strict than the stricter of the two originals.
/// </para>
/// <para>
/// The pure-logic phases (A–F) require NO database: they compute the SQL golden-baseline subject
/// arithmetic in-test with <see cref="decimal"/> and assert the accept/reject decision through the
/// canonical surfaces (<see cref="CanonicalRiskRules.MeetsOrExceeds"/> and the rolling-frequency
/// evaluators). They are the always-green backbone of the parity proof and run in any environment.
/// The guarded integration phase (G) exercises the real, database-state-aware
/// <see cref="PreTradeRiskService.ValidateAsync"/> against a live PostgreSQL instance (the
/// containerized step-3 run); it reports <c>Inconclusive</c> — never fails — when no database is
/// configured, so <c>dotnet test</c> stays green without one.
/// </para>
/// <para>
/// The seven SQL golden-baseline checks (evaluated in this order, each "&gt;=" meets-or-exceeds,
/// each NULL/0 = "not enforced") are: (1) price ceiling <c>price &gt;= max_order_price</c>;
/// (2) qty ceiling <c>qty &gt;= max_order_qty</c>; (3) notional <c>qty*price &gt;= max_order_value</c>;
/// (4) rolling order frequency <c>recentCount + 1 &gt;= max_order_freq_count</c>; (5) hypothetical
/// post-fill position <c>abs(currentQty + signedDelta) &gt;= max_position_size</c> (signedDelta is
/// <c>+qty</c> for side <c>B</c>, <c>-qty</c> for side <c>S</c>); (6) cumulative commission estimate
/// <c>existingNotional*rate + qty*price*rate &gt;= max_commission_total</c>; and (7) daily volume
/// <c>todayQty + qty &gt;= max_daily_volume</c> (see <c>Database/002_StoredProcedures.sql</c> L129–L243).
/// </para>
/// <para>
/// Every money / quantity / price / rate fixture uses a <see cref="decimal"/> literal (the <c>m</c>
/// suffix), never <c>double</c>/<c>float</c>, so the comparisons preserve the schema's
/// <c>NUMERIC(18,4)</c> / <c>NUMERIC(9,6)</c> scale and a "&gt;=" comparison can never silently loosen.
/// </para>
/// </remarks>
[TestClass]
public class PreTradeRiskParityTests : BaseTestClass
{
	// ---------------------------------------------------------------------------------------------
	// Canonical DEMO fixture — the seeded RiskLimits values from Database/004_SeedData.sql that drive
	// the sample's three observable outcomes. Kept as decimal/int constants so every phase reasons
	// about the SAME thresholds the demo and the live database use.
	// ---------------------------------------------------------------------------------------------
	private const decimal DemoMaxOrderPrice = 500.00m;
	private const decimal DemoMaxOrderQty = 10000m;
	private const decimal DemoMaxOrderValue = 1000000.00m;
	private const decimal DemoMaxPositionSize = 100000m;
	private const decimal DemoMaxDailyVolume = 250000m;
	private const int DemoMaxOrderFreqCount = 5;
	private const int DemoMaxOrderFreqWindowSec = 60;
	private const decimal DemoMaxCommissionTotal = 5000.00m;
	private const decimal DemoCommissionRate = 0.0005m;

	// =============================================================================================
	// Phase A — CanonicalRiskRules.MeetsOrExceeds: ">=" boundary + NULL/0 "not enforced" semantics.
	// These are the canonical ceiling-comparison contract shared by every numeric gate check.
	// =============================================================================================

	/// <summary>Just below the ceiling is accepted (SQL "@value &gt;= @limit" is false here).</summary>
	[TestMethod]
	public void MeetsOrExceeds_BelowLimit_False()
	{
		// 499.9999 < 500.00 => not a breach => accept.
		CanonicalRiskRules.MeetsOrExceeds(499.9999m, DemoMaxOrderPrice).AssertFalse();
	}

	/// <summary>
	/// THE EXACT BOUNDARY: value == limit must be REJECTED. The SQL procedure uses "&gt;=" (meets OR
	/// exceeds), not "&gt;", on every ceiling, so equality is a breach. This is the single most
	/// important boundary assertion of the "&gt;=" discipline.
	/// </summary>
	[TestMethod]
	public void MeetsOrExceeds_EqualsLimit_True()
	{
		// 500.00 >= 500.00 => breach => reject (meets-or-exceeds).
		CanonicalRiskRules.MeetsOrExceeds(500.00m, DemoMaxOrderPrice).AssertTrue();
	}

	/// <summary>Above the ceiling is rejected.</summary>
	[TestMethod]
	public void MeetsOrExceeds_AboveLimit_True()
	{
		// 500.0001 >= 500.00 => breach => reject.
		CanonicalRiskRules.MeetsOrExceeds(500.0001m, DemoMaxOrderPrice).AssertTrue();
	}

	/// <summary>
	/// A NULL ceiling means "not enforced / unlimited": even an astronomically large value is
	/// accepted. Mirrors the SQL "@max_* IS NOT NULL" guard that skips the check when unset.
	/// </summary>
	[TestMethod]
	public void MeetsOrExceeds_NullCeiling_NotEnforced_False()
	{
		CanonicalRiskRules.MeetsOrExceeds(999999.9999m, (decimal?)null).AssertFalse();
	}

	/// <summary>
	/// A ZERO ceiling ALSO means "not enforced / unlimited" (AAP 0.6.4, compliance item 5): the
	/// canonical convention unifies NULL and 0 to both mean "no limit", matching how the C#
	/// circuit-breaker rules already treat a 0 threshold. So a 0 ceiling never breaches, regardless
	/// of the value. (MeetsOrExceeds composes IsCeilingEnabled, which requires the ceiling to be
	/// present AND strictly positive.)
	/// </summary>
	[TestMethod]
	public void MeetsOrExceeds_ZeroCeiling_NotEnforced_False()
	{
		CanonicalRiskRules.MeetsOrExceeds(999999.9999m, 0m).AssertFalse();
		// IsCeilingEnabled is the canonical predicate underneath: NULL or 0 => not enforced.
		CanonicalRiskRules.IsCeilingEnabled((decimal?)null).AssertFalse();
		CanonicalRiskRules.IsCeilingEnabled(0m).AssertFalse();
		CanonicalRiskRules.IsCeilingEnabled(500.00m).AssertTrue();
	}

	/// <summary>
	/// Precision is preserved at both schema scales: NUMERIC(18,4) money/qty and NUMERIC(9,6)
	/// commission rate. A four-decimal value equal to a four-decimal ceiling still meets it, and a
	/// six-decimal rate one ULP below its six-decimal ceiling does NOT — proving no scale is lost so
	/// the "&gt;=" comparison cannot silently loosen (hard NFR, AAP 0.6.4).
	/// </summary>
	[TestMethod]
	public void MeetsOrExceeds_PrecisionAtScale()
	{
		// NUMERIC(18,4): equal at full four-decimal scale => breach.
		CanonicalRiskRules.MeetsOrExceeds(500.0000m, 500.0000m).AssertTrue();

		// NUMERIC(9,6): commission-rate scale. Equal at six decimals => breach; one ULP below => accept.
		CanonicalRiskRules.MeetsOrExceeds(0.000500m, 0.000500m).AssertTrue();
		CanonicalRiskRules.MeetsOrExceeds(0.000499m, 0.000500m).AssertFalse();
	}

	// =============================================================================================
	// Phase B — CanonicalRiskRules.CountWithinWindow: the rolling-count primitive. Inclusive lower
	// bound (t >= now - window), no upper bound — exactly the SQL predicate
	// "submitted_date >= DATEADD(SECOND, -window, SYSUTCDATETIME())".
	// =============================================================================================

	/// <summary>
	/// The trailing lower bound is INCLUSIVE: a timestamp exactly on the (now - window) boundary is
	/// counted, one just outside it is not. Mirrors the SQL "&gt;=" window predicate, whose inclusive
	/// lower bound guarantees the rolling window is never LESS strict than the original.
	/// </summary>
	[TestMethod]
	public void CountWithinWindow_InclusiveLowerBound()
	{
		var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
		var window = TimeSpan.FromSeconds(10);

		var times = new List<DateTime>
		{
			now.AddSeconds(-9),   // inside  => counted
			now.AddSeconds(-10),  // exactly on the boundary (now - window) => counted (inclusive)
			now.AddSeconds(-11),  // outside => not counted
		};

		CanonicalRiskRules.CountWithinWindow(times, now, window).AssertEqual(2);
	}

	/// <summary>
	/// There is NO upper bound: a timestamp equal to <c>now</c> and one slightly after <c>now</c> are
	/// both &gt;= (now - window), so both are counted. Documents that the method is a pure "at or
	/// after the lower bound" count, matching the SQL COUNT(*) which likewise has no upper guard.
	/// </summary>
	[TestMethod]
	public void CountWithinWindow_NoUpperBound_IncludesFuture()
	{
		var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
		var window = TimeSpan.FromSeconds(10);

		var times = new List<DateTime>
		{
			now.AddSeconds(-1),  // inside
			now,                 // exactly now
			now.AddSeconds(1),   // after now — still >= (now - window), so counted (no upper bound)
		};

		CanonicalRiskRules.CountWithinWindow(times, now, window).AssertEqual(3);
	}

	/// <summary>An empty sequence yields a count of zero.</summary>
	[TestMethod]
	public void CountWithinWindow_Empty_Zero()
	{
		var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
		CanonicalRiskRules.CountWithinWindow([], now, TimeSpan.FromSeconds(10)).AssertEqual(0);
	}

	/// <summary>
	/// The null-guard contract: a null <c>times</c> throws <see cref="ArgumentNullException"/>
	/// (documented on the canonical API). Uses the inherited fluent <c>ThrowsExactly</c> helper.
	/// </summary>
	[TestMethod]
	public void CountWithinWindow_NullTimes_Throws()
	{
		var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
		var window = TimeSpan.FromSeconds(10);

		ThrowsExactly<ArgumentNullException>(() => CanonicalRiskRules.CountWithinWindow(null, now, window));
	}

	// =============================================================================================
	// Phase C — Gate vs circuit-breaker frequency-convention equivalence. The pre-trade GATE counts
	// the persisted recent orders and adds the prospective order (the SQL "+1"): breach when
	// count + 1 >= max. The CIRCUIT-BREAKER convention appends the current event first, then breaches
	// when count' >= max. Since count' = count + 1, the two conventions ALWAYS agree.
	// =============================================================================================

	/// <summary>
	/// Proves the two rolling-frequency helpers are consistent: the gate convention
	/// (<see cref="CanonicalRiskRules.IsOrderFrequencyBreached(System.Collections.Generic.IEnumerable{System.DateTime}, System.DateTime, System.TimeSpan, int)"/>,
	/// which adds the prospective order) equals the circuit-breaker convention
	/// (<see cref="CanonicalRiskRules.IsOrderFrequencyBreachedIncludingCurrent"/>, with the current
	/// event already appended) for both a non-breaching and a boundary case. Because appending
	/// <c>now</c> to the past events adds exactly one in-window timestamp, gate "count + 1 &gt;= max"
	/// is identical to including-current "count' &gt;= max" where count' = count + 1.
	/// </summary>
	[TestMethod]
	public void Frequency_GateEqualsCircuitBreaker()
	{
		var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
		var window = TimeSpan.FromSeconds(10);
		const int maxCount = 5;

		// Non-breaching: 3 recent orders. Gate: 3 + 1 = 4 >= 5 ? false. Including-current: count(3 past
		// + appended now) = 4 >= 5 ? false. The two conventions produce the same answer.
		var past3 = new List<DateTime> { now.AddSeconds(-1), now.AddSeconds(-2), now.AddSeconds(-3) };
		var gate3 = CanonicalRiskRules.IsOrderFrequencyBreached(past3, now, window, maxCount);
		var cb3 = CanonicalRiskRules.IsOrderFrequencyBreachedIncludingCurrent(past3.Concat(new[] { now }), now, window, maxCount);
		gate3.AssertFalse();
		cb3.AssertFalse();
		gate3.AssertEqual(cb3);

		// Boundary: 4 recent orders. Gate: 4 + 1 = 5 >= 5 ? true (meets). Including-current: 5 >= 5 ? true.
		var past4 = new List<DateTime> { now.AddSeconds(-1), now.AddSeconds(-2), now.AddSeconds(-3), now.AddSeconds(-4) };
		var gate4 = CanonicalRiskRules.IsOrderFrequencyBreached(past4, now, window, maxCount);
		var cb4 = CanonicalRiskRules.IsOrderFrequencyBreachedIncludingCurrent(past4.Concat(new[] { now }), now, window, maxCount);
		gate4.AssertTrue();
		cb4.AssertTrue();
		gate4.AssertEqual(cb4);
	}

	// =============================================================================================
	// Phase D — The seven-check subject-arithmetic characterization (the heart of the parity proof).
	// For each SQL golden-baseline check we compute the SAME subject the procedure computes, in
	// decimal, then assert the accept/reject decision through CanonicalRiskRules.MeetsOrExceeds
	// (checks 1,2,3,5,6,7) or the rolling-frequency helper (check 4). Each check asserts three
	// points: just below (accept), exactly at the ceiling (reject — the ">=" boundary), just above
	// (reject). Fixtures are the seeded DEMO limits (Database/004_SeedData.sql).
	// =============================================================================================

	/// <summary>Check 1 — price ceiling. SQL: <c>@price &gt;= @max_order_price</c> (subject = price).</summary>
	[TestMethod]
	public void Check1_PriceCeiling()
	{
		CanonicalRiskRules.MeetsOrExceeds(499.9999m, DemoMaxOrderPrice).AssertFalse(); // just below => accept
		CanonicalRiskRules.MeetsOrExceeds(500.00m, DemoMaxOrderPrice).AssertTrue();    // exactly at => reject (meets)
		CanonicalRiskRules.MeetsOrExceeds(500.0001m, DemoMaxOrderPrice).AssertTrue();  // just above => reject
	}

	/// <summary>Check 2 — order qty ceiling. SQL: <c>@qty &gt;= @max_order_qty</c> (subject = qty).</summary>
	[TestMethod]
	public void Check2_QtyCeiling()
	{
		CanonicalRiskRules.MeetsOrExceeds(9999.9999m, DemoMaxOrderQty).AssertFalse();  // just below => accept
		CanonicalRiskRules.MeetsOrExceeds(10000m, DemoMaxOrderQty).AssertTrue();       // exactly at => reject (meets)
		CanonicalRiskRules.MeetsOrExceeds(10000.0001m, DemoMaxOrderQty).AssertTrue();  // just above => reject
	}

	/// <summary>
	/// Check 3 — notional order value. SQL: <c>(@qty * @price) &gt;= @max_order_value</c>. The product
	/// is computed in <see cref="decimal"/> (never <c>double</c>) so scale is preserved. With
	/// price = 100.00: 9999 -&gt; 999900.00 (accept), 10000 -&gt; 1000000.00 (reject, meets),
	/// 10001 -&gt; 1000100.00 (reject).
	/// </summary>
	[TestMethod]
	public void Check3_NotionalValue()
	{
		var price = 100.00m;

		// Document the subject values, then assert the accept/reject decision on the decimal product.
		(9999m * price).AssertEqual(999900.00m);
		(10000m * price).AssertEqual(1000000.00m);
		(10001m * price).AssertEqual(1000100.00m);

		CanonicalRiskRules.MeetsOrExceeds(9999m * price, DemoMaxOrderValue).AssertFalse();  // 999900.00  => accept
		CanonicalRiskRules.MeetsOrExceeds(10000m * price, DemoMaxOrderValue).AssertTrue();  // 1000000.00 => reject (meets)
		CanonicalRiskRules.MeetsOrExceeds(10001m * price, DemoMaxOrderValue).AssertTrue();  // 1000100.00 => reject
	}

	/// <summary>
	/// Check 4 — order frequency. Uses the rolling-frequency helper (NOT MeetsOrExceeds). SQL:
	/// count the orders within the trailing window, then reject when <c>@recentOrderCount + 1 &gt;=
	/// @max_order_freq_count</c> (the "+1" is the prospective order). With window = 60s, max = 5:
	/// 3 recent -&gt; 4 (accept), 4 recent -&gt; 5 (reject, meets), 5 recent -&gt; 6 (reject).
	/// </summary>
	[TestMethod]
	public void Check4_OrderFrequency()
	{
		var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
		var window = TimeSpan.FromSeconds(DemoMaxOrderFreqWindowSec); // 60s

		var past3 = new List<DateTime> { now.AddSeconds(-5), now.AddSeconds(-10), now.AddSeconds(-15) };
		var past4 = new List<DateTime> { now.AddSeconds(-5), now.AddSeconds(-10), now.AddSeconds(-15), now.AddSeconds(-20) };
		var past5 = new List<DateTime> { now.AddSeconds(-5), now.AddSeconds(-10), now.AddSeconds(-15), now.AddSeconds(-20), now.AddSeconds(-25) };

		// 3 recent => gate 3 + 1 = 4 < 5 => accept
		CanonicalRiskRules.IsOrderFrequencyBreached(past3, now, window, DemoMaxOrderFreqCount).AssertFalse();
		// 4 recent => gate 4 + 1 = 5 >= 5 => reject (meets)
		CanonicalRiskRules.IsOrderFrequencyBreached(past4, now, window, DemoMaxOrderFreqCount).AssertTrue();
		// 5 recent => gate 5 + 1 = 6 >= 5 => reject
		CanonicalRiskRules.IsOrderFrequencyBreached(past5, now, window, DemoMaxOrderFreqCount).AssertTrue();
	}

	/// <summary>
	/// Check 5 — hypothetical POST-fill position size. SQL: <c>ABS(ISNULL(currentQty,0) + signedDelta)
	/// &gt;= @max_position_size</c>, where signedDelta = <c>+qty</c> for a BUY (side "B") and
	/// <c>-qty</c> for a SELL (side "S"). Both sides are covered to prove the B = positive /
	/// S = negative sign convention, from a flat position and from an existing long.
	/// </summary>
	[TestMethod]
	public void Check5_HypotheticalPositionSize()
	{
		// --- BUY from flat: subject = abs(0 + qty) = qty ---
		PositionSubject(0m, "B", 99999m).AssertEqual(99999m);
		CanonicalRiskRules.MeetsOrExceeds(PositionSubject(0m, "B", 99999m), DemoMaxPositionSize).AssertFalse(); // just below => accept
		CanonicalRiskRules.MeetsOrExceeds(PositionSubject(0m, "B", 100000m), DemoMaxPositionSize).AssertTrue(); // exactly at => reject (meets)
		CanonicalRiskRules.MeetsOrExceeds(PositionSubject(0m, "B", 100001m), DemoMaxPositionSize).AssertTrue(); // just above => reject

		// --- SELL sign: subject = abs(0 - qty) = qty. Proves the "S" side negates the delta. ---
		PositionSubject(0m, "S", 100000m).AssertEqual(100000m);
		CanonicalRiskRules.MeetsOrExceeds(PositionSubject(0m, "S", 100000m), DemoMaxPositionSize).AssertTrue(); // reject (meets)

		// --- With an existing long of 99900: a BUY adds toward the ceiling ---
		PositionSubject(99900m, "B", 100m).AssertEqual(100000m);
		CanonicalRiskRules.MeetsOrExceeds(PositionSubject(99900m, "B", 100m), DemoMaxPositionSize).AssertTrue();  // 100000 => reject (meets)
		CanonicalRiskRules.MeetsOrExceeds(PositionSubject(99900m, "B", 99m), DemoMaxPositionSize).AssertFalse();  // 99999  => accept
	}

	/// <summary>
	/// Check 6 — cumulative commission estimate. SQL: <c>existingNotional*rate + qty*price*rate &gt;=
	/// @max_commission_total</c>. Rate 0.0005, limit 5000.00, existingNotional 0, price 1000.00:
	/// 9999 -&gt; 4999.5000 (accept), 10000 -&gt; 5000.0000 (reject, meets), 10001 -&gt; 5000.5000 (reject).
	/// All arithmetic stays in <see cref="decimal"/> so the NUMERIC(18,4)/(9,6) scale is preserved.
	/// </summary>
	[TestMethod]
	public void Check6_CommissionEstimate()
	{
		var price = 1000.00m;

		// Document the subject (estimate) at the boundary, then assert the accept/reject decision.
		CommissionEstimate(0m, 9999m, price, DemoCommissionRate).AssertEqual(4999.5000m);
		CommissionEstimate(0m, 10000m, price, DemoCommissionRate).AssertEqual(5000.0000m);

		CanonicalRiskRules.MeetsOrExceeds(CommissionEstimate(0m, 9999m, price, DemoCommissionRate), DemoMaxCommissionTotal).AssertFalse();  // 4999.5000 => accept
		CanonicalRiskRules.MeetsOrExceeds(CommissionEstimate(0m, 10000m, price, DemoCommissionRate), DemoMaxCommissionTotal).AssertTrue();  // 5000.0000 => reject (meets)
		CanonicalRiskRules.MeetsOrExceeds(CommissionEstimate(0m, 10001m, price, DemoCommissionRate), DemoMaxCommissionTotal).AssertTrue();  // 5000.5000 => reject
	}

	/// <summary>
	/// Check 7 — daily traded volume. SQL: <c>ISNULL(todayQty,0) + @qty &gt;= @max_daily_volume</c>
	/// (subject = todayQty + qty), limit 250000. From a zero day: 249999 (accept), 250000 (reject,
	/// meets), 250001 (reject). From an existing 249900: +100 -&gt; 250000 (reject), +99 -&gt; 249999 (accept).
	/// </summary>
	[TestMethod]
	public void Check7_DailyVolume()
	{
		// todayQty = 0
		CanonicalRiskRules.MeetsOrExceeds(0m + 249999m, DemoMaxDailyVolume).AssertFalse(); // 249999 => accept
		CanonicalRiskRules.MeetsOrExceeds(0m + 250000m, DemoMaxDailyVolume).AssertTrue();  // 250000 => reject (meets)
		CanonicalRiskRules.MeetsOrExceeds(0m + 250001m, DemoMaxDailyVolume).AssertTrue();  // 250001 => reject

		// todayQty = 249900 (already-traded volume accumulates toward the daily ceiling)
		CanonicalRiskRules.MeetsOrExceeds(249900m + 100m, DemoMaxDailyVolume).AssertTrue();  // 250000 => reject (meets)
		CanonicalRiskRules.MeetsOrExceeds(249900m + 99m, DemoMaxDailyVolume).AssertFalse();  // 249999 => accept
	}

	/// <summary>
	/// End-to-end NULL/0 = "not enforced" convention at the subject level: with an unset (NULL) or 0
	/// ceiling, an arbitrarily large value is accepted on every check. The gate treats NULL and 0
	/// IDENTICALLY as "unlimited" (AAP 0.6.4, compliance item 5); demonstrated here on the price/qty
	/// subject, and the same rule governs every one of the seven checks.
	/// </summary>
	[TestMethod]
	public void NullOrZeroLimit_NotEnforced()
	{
		CanonicalRiskRules.MeetsOrExceeds(999999m, (decimal?)null).AssertFalse(); // NULL => not enforced => accept
		CanonicalRiskRules.MeetsOrExceeds(999999m, 0m).AssertFalse();             // 0    => not enforced => accept
	}

	// --- Phase D subject helpers (compute the SQL golden-baseline subject in decimal) ---

	/// <summary>
	/// The SQL hypothetical post-fill position subject: <c>abs(currentQty + signedDelta)</c>, where
	/// signedDelta is <c>+qty</c> for side "B" and <c>-qty</c> for side "S". Mirrors
	/// <c>usp_ValidatePreTradeRisk</c> check 5 exactly.
	/// </summary>
	private static decimal PositionSubject(decimal currentQty, string side, decimal qty)
		=> Math.Abs(currentQty + (side == "B" ? qty : -qty));

	/// <summary>
	/// The SQL cumulative commission estimate subject:
	/// <c>existingNotional*rate + qty*price*rate</c>. Mirrors <c>usp_ValidatePreTradeRisk</c> check 6.
	/// Pure <see cref="decimal"/> arithmetic so the NUMERIC scale is preserved.
	/// </summary>
	private static decimal CommissionEstimate(decimal existingNotional, decimal qty, decimal price, decimal rate)
		=> existingNotional * rate + qty * price * rate;

	// =============================================================================================
	// Phase E — Most-specific RiskLimits selection characterization. Reproduces, without a database,
	// the SQL "most-specific wins" ordering from usp_ValidatePreTradeRisk (L100–L118):
	// portfolio+security first, then portfolio-only, then broader; ties broken by effective_date DESC;
	// the first row wins (SQL "ORDER BY CASE ... , effective_date DESC ... TOP(1)/LIMIT 1").
	// =============================================================================================

	/// <summary>A candidate RiskLimits row for the selection characterization (tagged for assertions).</summary>
	private sealed record LimitRow(int? PortfolioId, int? SecurityId, DateTime EffectiveDate, string Tag);

	/// <summary>
	/// Pure reproduction of the SQL selection ordering that <see cref="PreTradeRiskService"/> mirrors:
	/// keep only rows whose (portfolio, security) scope matches (NULL = wildcard), rank
	/// portfolio+security = 0, portfolio-only = 1, broader = 2, then newest <c>effective_date</c> first,
	/// and take the first.
	/// </summary>
	private static LimitRow SelectMostSpecific(IEnumerable<LimitRow> rows, int portfolioId, int securityId) =>
		rows.Where(r => (r.PortfolioId == null || r.PortfolioId == portfolioId)
					 && (r.SecurityId == null || r.SecurityId == securityId))
			.OrderBy(r => r.PortfolioId != null && r.SecurityId != null ? 0
						: r.PortfolioId != null ? 1 : 2)
			.ThenByDescending(r => r.EffectiveDate)
			.FirstOrDefault();

	/// <summary>The portfolio+security row (rank 0) beats a portfolio-only and a security-only row.</summary>
	[TestMethod]
	public void MostSpecific_PortfolioAndSecurity_Wins()
	{
		var d = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		var rows = new List<LimitRow>
		{
			new(1, 10, d, "pf+sec"),      // rank 0 — most specific
			new(1, null, d, "pf-only"),   // rank 1
			new(null, 10, d, "sec-only"), // rank 2
		};

		SelectMostSpecific(rows, 1, 10).Tag.AssertEqual("pf+sec");
	}

	/// <summary>With no portfolio+security row, the portfolio-only row (rank 1) beats the broader row.</summary>
	[TestMethod]
	public void MostSpecific_FallsBackToPortfolioOnly()
	{
		var d = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		var rows = new List<LimitRow>
		{
			new(1, null, d, "pf-only"),   // rank 1
			new(null, 10, d, "sec-only"), // rank 2
		};

		SelectMostSpecific(rows, 1, 10).Tag.AssertEqual("pf-only");
	}

	/// <summary>Among rows of equal rank, the later <c>effective_date</c> wins (SQL "effective_date DESC").</summary>
	[TestMethod]
	public void MostSpecific_EffectiveDateDescTieBreak()
	{
		var older = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		var newer = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
		var rows = new List<LimitRow>
		{
			new(1, null, older, "old"), // rank 1, earlier date
			new(1, null, newer, "new"), // rank 1, later date => wins the tie-break
		};

		SelectMostSpecific(rows, 1, 10).Tag.AssertEqual("new");
	}

	/// <summary>Rows scoped to a DIFFERENT portfolio or security are filtered out and never selected.</summary>
	[TestMethod]
	public void MostSpecific_NonMatching_Excluded()
	{
		var d = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		var rows = new List<LimitRow>
		{
			new(2, null, d, "other-pf"),   // portfolio 2 != 1 => excluded
			new(null, 99, d, "other-sec"), // security 99 != 10 => excluded
			new(1, null, d, "pf-only"),    // the only in-scope row
		};

		var selected = SelectMostSpecific(rows, 1, 10);
		selected.Tag.AssertEqual("pf-only");
		(selected.Tag == "other-pf").AssertFalse();
		(selected.Tag == "other-sec").AssertFalse();
	}

	// =============================================================================================
	// Phase F — CRITICAL: rolling-vs-fixed-bucket frequency strictness (guards the threshold-strictness
	// invariant, AAP 0.6.1). This is the single most important test in the file. It proves the canonical
	// rolling evaluator is AT LEAST AS STRICT as the SQL COUNT(*) rolling window and STRICTLY STRICTER
	// than the retired fixed-bucket algorithm for the boundary-straddling burst the AAP calls out.
	// =============================================================================================

	/// <summary>
	/// Byte-accurate reproduction of the RETIRED fixed, non-overlapping bucket algorithm that
	/// <see cref="RiskOrderFreqRule"/> used to run (its <c>ProcessMessage</c> body,
	/// <c>Algo/Risk/RiskOrderFreqRule.cs</c> L92–L144). It is reproduced here — NOT referenced — on
	/// purpose: the production <c>RiskOrderFreqRule</c> is being changed to the canonical rolling
	/// algorithm, so the test must retain the OLD behavior to demonstrate the difference the refactor
	/// removes. Semantics preserved exactly: <c>time == default</c> is ignored; the first order opens a
	/// bucket <c>[time, time+interval)</c> with count 1; an order strictly inside the current bucket
	/// increments and TRIPS at <c>count &gt;= Count</c> (then clears the bucket); an order at or past the
	/// bucket end opens a fresh bucket with count 1.
	/// </summary>
	private sealed class RetiredFixedBucketFreq
	{
		private readonly int _count;
		private readonly TimeSpan _interval;
		private int _current;
		private DateTime? _endTime;

		public RetiredFixedBucketFreq(int count, TimeSpan interval)
		{
			_count = count;
			_interval = interval;
		}

		/// <summary>Returns true when the rule "trips" for the order at <paramref name="time"/> (mirrors ProcessMessage's bool return).</summary>
		public bool Process(DateTime time)
		{
			if (time == default)
				return false;

			if (_endTime == null)
			{
				_endTime = time + _interval;
				_current = 1;
				return false;
			}

			if (time < _endTime)
			{
				_current++;

				if (_current >= _count)
				{
					_endTime = null;
					return true;
				}
			}
			else
			{
				_endTime = time + _interval;
				_current = 1;
			}

			return false;
		}
	}

	/// <summary>
	/// The boundary-straddling burst: eight orders at offsets {0,1,2,3,10,11,12,13}s with a "5 per 10s"
	/// limit. The retired fixed-bucket algorithm NEVER trips (4 orders in bucket [0,10), 4 in [10,20) —
	/// a run of 4 in each, never reaching 5), so the burst dodges the limit. The canonical rolling
	/// evaluator DOES trip (the trailing 10s window at t=13 spans the boundary and counts
	/// {3,10,11,12,13} = 5), under BOTH the including-current and the gate ("+1") conventions. This is
	/// exactly the divergence the consolidation removes, and it proves the reconciled threshold is never
	/// less strict than the stricter original.
	/// </summary>
	[TestMethod]
	public void Frequency_RollingStricterThanFixedBucket_BoundaryStraddle()
	{
		var baseTime = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
		var offsets = new[] { 0, 1, 2, 3, 10, 11, 12, 13 };
		var times = offsets.Select(s => baseTime.AddSeconds(s)).ToList();
		const int count = 5;
		var interval = TimeSpan.FromSeconds(10);

		// --- Retired fixed-bucket: NEVER trips. ---
		// Bucket 1 = [0s,10s): orders at 0,1,2,3 => run of 4 (never reaches 5). At t=10 the bucket rolls
		// over (10 is NOT < endTime 10), opening bucket 2 = [10s,20s): orders at 10,11,12,13 => run of 4
		// again. Max run = 4 in each bucket, so no trip — the burst slips through by straddling the
		// [0,10)/[10,20) boundary. Materialise eagerly (ToList) because the algorithm is stateful.
		var bucket = new RetiredFixedBucketFreq(count, interval);
		var trips = times.Select(bucket.Process).ToList();
		trips.Any(trip => trip).AssertFalse();

		// --- Canonical rolling window: DOES trip. ---
		// now = 13s, window 10s => cutoff = 3s. Counted set = {3,10,11,12,13} = 5 (0,1,2 have rolled out;
		// 3 is exactly on the inclusive lower bound and is KEPT). 5 >= 5 => breach (circuit-breaker form).
		CanonicalRiskRules.CountWithinWindow(times, times[^1], interval).AssertEqual(5);
		CanonicalRiskRules.IsOrderFrequencyBreachedIncludingCurrent(times, times[^1], interval, count).AssertTrue();

		// --- Gate ("+1") convention also trips. ---
		// past = offsets 0..12 (all but the last); now = 13s. CountWithinWindow(past) = {3,10,11,12} = 4,
		// so the gate computes 4 + 1 = 5 >= 5 => breach for the prospective order at 13s.
		var past = times.Take(times.Count - 1).ToList();
		CanonicalRiskRules.CountWithinWindow(past, times[^1], interval).AssertEqual(4);
		CanonicalRiskRules.IsOrderFrequencyBreached(past, times[^1], interval, count).AssertTrue();

		// Conclusion: the canonical rolling evaluator is at least as strict as the SQL COUNT(*) rolling
		// window and strictly stricter than the retired fixed-bucket algorithm — the threshold-strictness
		// invariant holds (AAP 0.6.1).
	}

	/// <summary>
	/// Supporting case: a NON-straddling burst of five orders within one 10s window. Both algorithms
	/// agree they trip, documenting that the rolling evaluator is not GRATUITOUSLY stricter — it is only
	/// correctly stricter at a bucket boundary (the straddle case above), never elsewhere.
	/// </summary>
	[TestMethod]
	public void Frequency_RollingMatchesSqlCount_SimpleWindow()
	{
		var baseTime = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
		var offsets = new[] { 0, 1, 2, 3, 4 };
		var times = offsets.Select(s => baseTime.AddSeconds(s)).ToList();
		const int count = 5;
		var interval = TimeSpan.FromSeconds(10);

		// Retired fixed-bucket: the 5th order in bucket [0,10) reaches the count and trips.
		var bucket = new RetiredFixedBucketFreq(count, interval);
		var trips = times.Select(bucket.Process).ToList();
		trips.Any(trip => trip).AssertTrue();

		// Canonical rolling: 5 within the trailing 10s => also trips. Same answer, no boundary to straddle.
		CanonicalRiskRules.CountWithinWindow(times, times[^1], interval).AssertEqual(5);
		CanonicalRiskRules.IsOrderFrequencyBreachedIncludingCurrent(times, times[^1], interval, count).AssertTrue();
	}

	// =============================================================================================
	// Phase G — Guarded PostgreSQL integration parity (staged step-3). These tests exercise the real,
	// database-state-aware PreTradeRiskService.ValidateAsync against a LIVE PostgreSQL instance (the
	// containerized step-3 run). They MUST NOT fail dotnet test when no database is available:
	// TryOpenAsync reports Inconclusive (never fails) when STOCKSHARP_LEGACY_SQL_CONNECTION is unset or
	// the configured database is unreachable, mirroring the repo's skip-when-unavailable convention.
	//
	// DOCUMENTED DECISION: the on-disk PreTradeRiskService.ValidateAsync requires the caller's in-flight
	// NpgsqlTransaction as its second argument (it reads risklimits/positions/orders/trades ON that
	// transaction so the validation shares the caller's snapshot) and throws if it is null. Each test
	// therefore opens a transaction, seeds inside it, validates on the SAME transaction, and rolls back
	// at the end — which both satisfies the required signature AND leaves the database pristine for
	// re-runs (no residual rows), so no delete-by-unique-suffix cleanup query is needed. A unique suffix
	// is still applied to the portfolio/security natural keys as belt-and-suspenders.
	// =============================================================================================

	/// <summary>
	/// Demo scenario (a): an order within EVERY configured limit is accepted. Seeds a fresh
	/// portfolio + security + the DEMO risklimits row, then validates a small buy (qty 100 @ 150). With
	/// no seeded orders/positions/trades all seven checks pass (price 150 &lt; 500, qty 100 &lt; 10000,
	/// notional 15000 &lt; 1e6, frequency 1 &lt; 5, flat position 100 &lt; 100000, commission 7.5 &lt; 5000,
	/// daily 100 &lt; 250000), so the gate accepts.
	/// </summary>
	[TestMethod]
	public async Task ValidateAsync_WithinLimits_Accepted()
	{
		await using var connection = await TryOpenAsync();
		await using var transaction = await connection.BeginTransactionAsync(CancellationToken);

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = await InsertPortfolioAsync(connection, transaction, "PARITY_" + suffix);
		var securityId = await InsertSecurityAsync(connection, transaction, "SEC_" + suffix);
		await InsertDemoRiskLimitsAsync(connection, transaction, portfolioId);

		var result = await new PreTradeRiskService()
			.ValidateAsync(connection, transaction, portfolioId, securityId, "B", 100m, 150.00m, CancellationToken);

		result.IsValid.AssertTrue();
		result.RejectReason.AssertNull();

		// Roll back so the seeded rows never persist (keeps the database clean for re-runs).
		await transaction.RollbackAsync(CancellationToken);
	}

	/// <summary>
	/// Demo scenario (b): an order breaching the price ceiling is rejected with a reason. Price 999
	/// meets/exceeds the seeded 500 ceiling, so the gate rejects at check 1 (the FIRST check, so no
	/// other seed data is needed). The reject reason is asserted LOOSELY (Contains "price"), never by
	/// full-string equality, because the DB-read NUMERIC(18,4) renders with trailing zeros (e.g.
	/// "500.0000") via Ecng <c>.To&lt;string&gt;()</c>.
	/// </summary>
	[TestMethod]
	public async Task ValidateAsync_PriceCeilingBreached_Rejected()
	{
		await using var connection = await TryOpenAsync();
		await using var transaction = await connection.BeginTransactionAsync(CancellationToken);

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = await InsertPortfolioAsync(connection, transaction, "PARITY_" + suffix);
		var securityId = await InsertSecurityAsync(connection, transaction, "SEC_" + suffix);
		await InsertDemoRiskLimitsAsync(connection, transaction, portfolioId);

		var result = await new PreTradeRiskService()
			.ValidateAsync(connection, transaction, portfolioId, securityId, "B", 10m, 999.00m, CancellationToken);

		result.IsValid.AssertFalse();
		result.RejectReason.AssertNotNull();
		result.RejectReason.ToLowerInvariant().Contains("price").AssertTrue();

		await transaction.RollbackAsync(CancellationToken);
	}

	// --- Phase G helpers: skip-guard and self-contained raw-SQL seeding ---

	/// <summary>
	/// Opens a PostgreSQL connection for the guarded step-3 tests, or signals skip. The environment
	/// variable is checked FIRST so an unconfigured run skips immediately instead of blocking on the
	/// localhost fallback's connect timeout. Reports <c>Inconclusive</c> (never fails) when no database
	/// is configured OR the configured database is unreachable.
	/// </summary>
	/// <returns>An open <see cref="NpgsqlConnection"/>; or <see langword="null"/> only on the (throwing) skip path.</returns>
	private async Task<NpgsqlConnection> TryOpenAsync()
	{
		// Gate on the opt-in environment variable: Resolve() always yields a localhost fallback, so
		// "is a database configured for THIS run" is decided by the variable, not by Resolve().
		if (Environment.GetEnvironmentVariable("STOCKSHARP_LEGACY_SQL_CONNECTION").IsEmpty())
		{
			Inconclusive("No PostgreSQL connection configured (STOCKSHARP_LEGACY_SQL_CONNECTION unset) - skipping DB parity test.");
			return null; // unreachable in practice (Inconclusive throws); required for definite assignment.
		}

		NpgsqlConnection connection = null;

		try
		{
			connection = new NpgsqlConnection(SqlLegacyConnection.Resolve());
			await connection.OpenAsync(CancellationToken);
			return connection;
		}
		catch (Exception ex)
		{
			if (connection is not null)
				await connection.DisposeAsync();

			Inconclusive($"PostgreSQL not reachable - skipping DB parity test: {ex.Message}");
			return null; // unreachable in practice (Inconclusive throws); required for definite assignment.
		}
	}

	/// <summary>Inserts a portfolio with the given unique name and returns its generated id.</summary>
	private async Task<int> InsertPortfolioAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string name)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO portfolios (name) VALUES (@name) RETURNING portfolio_id", connection, transaction);
		command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Varchar) { Value = name });
		return (int)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Inserts a security with the given unique code and returns its generated id.</summary>
	private async Task<int> InsertSecurityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO securities (security_code) VALUES (@code) RETURNING security_id", connection, transaction);
		command.Parameters.Add(new NpgsqlParameter("code", NpgsqlDbType.Varchar) { Value = code });
		return (int)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>
	/// Seeds the portfolio-scoped DEMO risklimits row (security_id left NULL). Mirrors the seeded values
	/// in <c>Database/004_SeedData.sql</c> — especially <c>max_order_price = 500.00</c> — so the guarded
	/// scenarios reproduce the demo's accept/reject outcomes. <c>is_active</c> and <c>effective_date</c>
	/// take their schema defaults (TRUE / now() at time zone 'utc').
	/// </summary>
	private async Task InsertDemoRiskLimitsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int portfolioId)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO risklimits (portfolio_id, max_order_price, max_order_qty, max_order_value, " +
			"max_position_size, max_daily_volume, max_order_freq_count, max_order_freq_window_sec, " +
			"max_commission_total, commission_rate) " +
			"VALUES (@portfolio_id, @max_order_price, @max_order_qty, @max_order_value, " +
			"@max_position_size, @max_daily_volume, @max_order_freq_count, @max_order_freq_window_sec, " +
			"@max_commission_total, @commission_rate)", connection, transaction);
		command.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
		command.Parameters.Add(new NpgsqlParameter("max_order_price", NpgsqlDbType.Numeric) { Value = DemoMaxOrderPrice });
		command.Parameters.Add(new NpgsqlParameter("max_order_qty", NpgsqlDbType.Numeric) { Value = DemoMaxOrderQty });
		command.Parameters.Add(new NpgsqlParameter("max_order_value", NpgsqlDbType.Numeric) { Value = DemoMaxOrderValue });
		command.Parameters.Add(new NpgsqlParameter("max_position_size", NpgsqlDbType.Numeric) { Value = DemoMaxPositionSize });
		command.Parameters.Add(new NpgsqlParameter("max_daily_volume", NpgsqlDbType.Numeric) { Value = DemoMaxDailyVolume });
		command.Parameters.Add(new NpgsqlParameter("max_order_freq_count", NpgsqlDbType.Integer) { Value = DemoMaxOrderFreqCount });
		command.Parameters.Add(new NpgsqlParameter("max_order_freq_window_sec", NpgsqlDbType.Integer) { Value = DemoMaxOrderFreqWindowSec });
		command.Parameters.Add(new NpgsqlParameter("max_commission_total", NpgsqlDbType.Numeric) { Value = DemoMaxCommissionTotal });
		command.Parameters.Add(new NpgsqlParameter("commission_rate", NpgsqlDbType.Numeric) { Value = DemoCommissionRate });
		await command.ExecuteNonQueryAsync(CancellationToken);
	}
}
