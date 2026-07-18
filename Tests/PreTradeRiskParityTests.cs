namespace StockSharp.Tests;

using System.Data;
using System.Diagnostics;
using System.Text;

using StockSharp.Algo.Risk;
using StockSharp.Algo.Storages.Sql;

using Microsoft.Data.SqlClient;
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
[DoNotParallelize] // Staged Steps 1/2/4 seed and roll back transactions against the shared SQL Server (dbo.*) and PostgreSQL engines; parallel execution deadlocks on those shared tables, so this class runs serially (repo convention, cf. StorageNotParallelizeTests / ExportTests).
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
	// Phase G - PostgreSQL demo-scenario integration (staged STEP 3 via the real, database-state-aware
	// PreTradeRiskService.ValidateAsync against a LIVE PostgreSQL instance). These reproduce two of the
	// three observable demo outcomes (AAP 0.7.1) on the migrated engine. They use OpenPostgresAsync,
	// which is MANDATORY-WHEN-CONFIGURED: it reports Inconclusive ONLY when STOCKSHARP_LEGACY_SQL_CONNECTION
	// is unset, and FAILS when a database IS configured but unreachable (a configured engine MUST actually
	// be exercised - the former skip-on-unreachable behaviour could mask a broken step-3 run).
	//
	// Each test opens a transaction, seeds inside it, validates on the SAME transaction (the service reads
	// risklimits/positions/orders/trades ON that transaction so validation shares the caller's snapshot),
	// and rolls back, leaving the database pristine for re-runs.
	// =============================================================================================

	/// <summary>
	/// Demo scenario (a): an order within EVERY configured limit is accepted. Seeds a fresh
	/// portfolio + security + the DEMO risklimits row, then validates a small buy (qty 100 @ 150). With
	/// no seeded orders/positions/trades all seven checks pass, so the gate accepts.
	/// </summary>
	[TestMethod]
	public async Task ValidateAsync_WithinLimits_Accepted()
	{
		await using var connection = await OpenPostgresAsync();
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
	/// meets/exceeds the seeded 500 ceiling, so the gate rejects at check 1. The reject reason is asserted
	/// LOOSELY (Contains "price"), never by full-string equality, because the DB-read NUMERIC(18,4) renders
	/// with trailing zeros (e.g. "500.0000") via Ecng <c>.To&lt;string&gt;()</c>.
	/// </summary>
	[TestMethod]
	public async Task ValidateAsync_PriceCeilingBreached_Rejected()
	{
		await using var connection = await OpenPostgresAsync();
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

	// =============================================================================================
	// Phase M9 - REVIEW-FINDING REGRESSIONS for the pre-trade GATE (findings M1, M2, M3).
	//
	// These four tests lock in the Phase 4 gate-logic fixes so a future edit cannot silently reopen them:
	//   * M1  - EvaluateGate rejects a STRUCTURALLY malformed order (bad side / non-positive qty / bad price)
	//           even when every ceiling is disabled (pure, no database).
	//   * M2  - negative and unpaired-frequency limits are rejected at BOTH layers: the schema CHECK
	//           constraints (Phase 2 DDL) AND the ValidateAsync runtime fail-closed guard (Phase 4).
	//   * M3  - the rolling frequency window is anchored on the DATABASE transaction clock, not a (skewable)
	//           application clock, so a skewed app clock cannot loosen the control.
	// =============================================================================================

	/// <summary>
	/// M1: <see cref="PreTradeRiskService.EvaluateGate"/> is a PURE static decision core with no database
	/// dependency. A direct caller (or a future reuse of the gate) must never be able to coax a wrong answer
	/// out of a malformed order. Before the fix an unknown side fell through to the step-5 "'B' adds, else
	/// subtracts" position math (silently treated as a SELL) and a non-positive qty could return Valid whenever
	/// the qty ceiling happened to be disabled. Every vector here uses ALL-NULL ("unlimited") ceilings on
	/// purpose, so ONLY the new structural guards can produce a rejection.
	/// </summary>
	[TestMethod]
	public void EvaluateGate_MalformedInput_StructurallyRejected()
	{
		// Unknown side "X" -> rejected with the EXACT reason ValidateAsync uses (full-string equality).
		var badSide = PreTradeRiskService.EvaluateGate(new GateInputs { Side = "X", Qty = 100m, Price = 150m });
		badSide.IsValid.AssertFalse();
		badSide.RejectReason.AssertEqual("Invalid side: X");

		// Lowercase and word forms are NOT the canonical "B"/"S" domain either.
		PreTradeRiskService.EvaluateGate(new GateInputs { Side = "b", Qty = 100m, Price = 150m }).IsValid.AssertFalse();
		PreTradeRiskService.EvaluateGate(new GateInputs { Side = "SELL", Qty = 100m, Price = 150m }).IsValid.AssertFalse();
		PreTradeRiskService.EvaluateGate(new GateInputs { Side = "", Qty = 100m, Price = 150m }).IsValid.AssertFalse();

		// Zero and negative quantity -> rejected even though the qty ceiling is disabled (NULL).
		var zeroQty = PreTradeRiskService.EvaluateGate(new GateInputs { Side = "B", Qty = 0m, Price = 150m });
		zeroQty.IsValid.AssertFalse();
		zeroQty.RejectReason.AssertEqual("Invalid qty");
		PreTradeRiskService.EvaluateGate(new GateInputs { Side = "B", Qty = -5m, Price = 150m }).IsValid.AssertFalse();

		// A SUPPLIED non-positive price -> rejected before any ceiling check (a market order, Price=null, is
		// unaffected and is covered by the control case below).
		var badPrice = PreTradeRiskService.EvaluateGate(new GateInputs { Side = "B", Qty = 100m, Price = -1m });
		badPrice.IsValid.AssertFalse();
		badPrice.RejectReason.Contains("Invalid price").AssertTrue();

		// CONTROL: well-formed buy (limit) and sell (market) with ALL ceilings disabled -> Valid, reason null.
		// Proves the structural guards do not false-positive on a legitimate order.
		var okBuy = PreTradeRiskService.EvaluateGate(new GateInputs { Side = "B", Qty = 100m, Price = 150m });
		okBuy.IsValid.AssertTrue();
		okBuy.RejectReason.AssertNull();
		PreTradeRiskService.EvaluateGate(new GateInputs { Side = "S", Qty = 100m, Price = null }).IsValid.AssertTrue();
	}

	/// <summary>
	/// M2 (schema layer): the Phase 2 CHECK constraints reject a negative ceiling, an unpaired frequency
	/// (count set without a window), and a negative frequency window at the DDL boundary - so a corrupt limit
	/// can never be persisted in the first place. Each attempt runs in its own transaction that is rolled back,
	/// because a failed statement aborts the surrounding PostgreSQL transaction.
	/// </summary>
	[TestMethod]
	public async Task RiskLimits_NegativeAndUnpairedFrequency_RejectedByCheckConstraints()
	{
		await using var connection = await OpenPostgresAsync();

		// (a) negative max_order_price -> ck_risklimits_max_order_price.
		await AssertRiskLimitsInsertViolatesCheckAsync(connection,
			"INSERT INTO risklimits (portfolio_id, max_order_price, commission_rate) VALUES (@pid, -1, 0.0005)");

		// (b) frequency count set but window NULL (unpaired) -> ck_risklimits_freq_paired.
		await AssertRiskLimitsInsertViolatesCheckAsync(connection,
			"INSERT INTO risklimits (portfolio_id, max_order_freq_count, commission_rate) VALUES (@pid, 5, 0.0005)");

		// (c) negative frequency window (paired, so freq_paired is satisfied) -> ck_risklimits_freq_window.
		await AssertRiskLimitsInsertViolatesCheckAsync(connection,
			"INSERT INTO risklimits (portfolio_id, max_order_freq_count, max_order_freq_window_sec, commission_rate) " +
			"VALUES (@pid, 5, -1, 0.0005)");
	}

	/// <summary>
	/// M2 (runtime layer / defence in depth): even if a negative ceiling somehow bypasses the DDL (an older
	/// database, a manual edit, a dropped constraint), <see cref="PreTradeRiskService.ValidateAsync"/> must
	/// FAIL CLOSED rather than treat the negative value as "unlimited". We drop the CHECK constraint INSIDE the
	/// transaction, plant a negative row, prove ValidateAsync rejects, then ROLLBACK - which restores both the
	/// constraint and removes the row (PostgreSQL transactional DDL), leaving the shared test database pristine.
	/// </summary>
	[TestMethod]
	public async Task ValidateAsync_NegativeLimit_FailsClosed()
	{
		await using var connection = await OpenPostgresAsync();
		await using var transaction = await connection.BeginTransactionAsync(CancellationToken);

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = await InsertPortfolioAsync(connection, transaction, "NEGRT_" + suffix);
		var securityId = await InsertSecurityAsync(connection, transaction, "SEC_" + suffix);

		await using (var drop = new NpgsqlCommand(
			"ALTER TABLE risklimits DROP CONSTRAINT IF EXISTS ck_risklimits_max_order_price", connection, transaction))
			await drop.ExecuteNonQueryAsync(CancellationToken);

		await using (var insert = new NpgsqlCommand(
			"INSERT INTO risklimits (portfolio_id, max_order_price, commission_rate) VALUES (@pid, -1, 0.0005)",
			connection, transaction))
		{
			insert.Parameters.Add(PgP("pid", NpgsqlDbType.Integer, portfolioId));
			await insert.ExecuteNonQueryAsync(CancellationToken);
		}

		var result = await new PreTradeRiskService()
			.ValidateAsync(connection, transaction, portfolioId, securityId, "B", 10m, 100.00m, CancellationToken);

		result.IsValid.AssertFalse();
		result.RejectReason.AssertNotNull();
		result.RejectReason.ToLowerInvariant().Contains("negative limit").AssertTrue();

		await transaction.RollbackAsync(CancellationToken);
	}

	/// <summary>
	/// M3: the rolling frequency window must be anchored on the DATABASE transaction clock (the same clock that
	/// stamps <c>orders.submitted_date</c>), NOT on a skewable application clock. PostgreSQL <c>now()</c> is the
	/// TRANSACTION-START timestamp (stable for the whole transaction), so seeded orders and an in-transaction
	/// <c>SELECT now()</c> share one clock. We seed four recent orders (limit = 5 per 60s) and show the two
	/// verdicts DIVERGE: the DEFAULT service (DB clock) counts them and REJECTS the fifth on frequency (the safe
	/// verdict), whereas a service given an app clock skewed +1h into the future would push the cutoff past the
	/// seeded rows and wrongly ACCEPT. Proving the default path yields the strict verdict proves it does not
	/// trust an app clock.
	/// </summary>
	[TestMethod]
	public async Task ValidateAsync_FrequencyWindow_UsesDatabaseClock_NotAppClock()
	{
		await using var connection = await OpenPostgresAsync();
		await using var transaction = await connection.BeginTransactionAsync(CancellationToken);

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = await InsertPortfolioAsync(connection, transaction, "SKEW_" + suffix);
		var securityId = await InsertSecurityAsync(connection, transaction, "SEC_" + suffix);
		await InsertDemoRiskLimitsAsync(connection, transaction, portfolioId); // frequency = 5 orders / 60s

		// Seed FOUR recent orders; submitted_date DEFAULTS to (now() at time zone 'utc') = txn-start time.
		for (var i = 0; i < 4; i++)
			await PgSeedOrderAsync(connection, transaction, portfolioId, securityId, "B", 1m, 150.00m, "ACCEPTED");

		// Read the SAME transaction clock so the injected-clock service can be skewed relative to it.
		DateTime dbNow;
		await using (var nowCmd = new NpgsqlCommand("SELECT now() at time zone 'utc'", connection, transaction))
			dbNow = DateTime.SpecifyKind((DateTime)await nowCmd.ExecuteScalarAsync(CancellationToken), DateTimeKind.Unspecified);

		// (a) DEFAULT service (no injected clock) -> reads DB transaction time; cutoff = dbNow - 60s, so all
		// four seeded orders fall INSIDE the window and the prospective fifth trips 4 + 1 = 5 >= 5 => REJECT.
		var dbClockResult = await new PreTradeRiskService()
			.ValidateAsync(connection, transaction, portfolioId, securityId, "B", 1m, 150.00m, CancellationToken);
		dbClockResult.IsValid.AssertFalse();
		dbClockResult.RejectReason.AssertNotNull();
		dbClockResult.RejectReason.ToLowerInvariant().Contains("frequency").AssertTrue();

		// (b) A service whose APP clock is skewed +1h into the future. If the gate trusted this app clock (the
		// M3 bug), cutoff = (dbNow + 1h) - 60s would sit AFTER the seeded rows, they would NOT be counted, and
		// the order would be wrongly ACCEPTED. Observing that ACCEPT here demonstrates the loosening an app
		// clock would cause - and therefore that the DEFAULT (DB-clock) path in (a) is the safe one.
		var skewedResult = await new PreTradeRiskService(() => dbNow.AddHours(1))
			.ValidateAsync(connection, transaction, portfolioId, securityId, "B", 1m, 150.00m, CancellationToken);
		skewedResult.IsValid.AssertTrue();

		await transaction.RollbackAsync(CancellationToken);
	}

	/// <summary>
	/// Helper for <see cref="RiskLimits_NegativeAndUnpairedFrequency_RejectedByCheckConstraints"/>: inserts a
	/// fresh portfolio and attempts <paramref name="insertSql"/> (parameter <c>@pid</c> = that portfolio id) on
	/// its own transaction, asserting the statement fails with SQLSTATE 23514 (check_violation), then rolls the
	/// transaction back. A per-attempt transaction is required because a failed statement aborts the
	/// surrounding PostgreSQL transaction.
	/// </summary>
	private async Task AssertRiskLimitsInsertViolatesCheckAsync(NpgsqlConnection connection, string insertSql)
	{
		await using var transaction = await connection.BeginTransactionAsync(CancellationToken);

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = await InsertPortfolioAsync(connection, transaction, "NEGCHK_" + suffix);

		var caught = false;
		try
		{
			await using var command = new NpgsqlCommand(insertSql, connection, transaction);
			command.Parameters.Add(PgP("pid", NpgsqlDbType.Integer, portfolioId));
			await command.ExecuteNonQueryAsync(CancellationToken);
		}
		catch (PostgresException ex) when (ex.SqlState == "23514") // 23514 = check_violation
		{
			caught = true;
		}

		caught.AssertTrue();
		await transaction.RollbackAsync(CancellationToken);
	}

	// =============================================================================================
	// Phase M6/M9 - REVIEW-FINDING REGRESSIONS for TEST SAFETY, the AUDIT TRIGGER, SECURITY CASE
	// parity, and DEMO failure behavior (findings M6, M9, and the M5 "add failure-path tests" item).
	// =============================================================================================

	/// <summary>
	/// M6: the SQL Server characterization harness must REFUSE to run its destructive DDL against a database
	/// that is not clearly disposable. This exercises the pure guard predicate directly (no SQL Server needed),
	/// so the safety rule is verified even in a run where STOCKSHARP_LEGACY_MSSQL_CONNECTION is unset.
	/// </summary>
	[TestMethod]
	public void MssqlHarness_RefusesNonDisposableDatabase()
	{
		// Production-looking names with no explicit opt-in are REFUSED.
		MssqlDatabaseIsDisposable("StockSharpLegacy", explicitlyAllowed: false).AssertFalse();
		MssqlDatabaseIsDisposable("master", explicitlyAllowed: false).AssertFalse();
		MssqlDatabaseIsDisposable("", explicitlyAllowed: false).AssertFalse();
		MssqlDatabaseIsDisposable(null, explicitlyAllowed: false).AssertFalse();

		// A name that marks itself disposable (contains "test", any case) is ACCEPTED.
		MssqlDatabaseIsDisposable("StockSharpLegacyTest", explicitlyAllowed: false).AssertTrue();
		MssqlDatabaseIsDisposable("ci_TEST_db", explicitlyAllowed: false).AssertTrue();

		// The explicit STOCKSHARP_LEGACY_MSSQL_ALLOW_DDL opt-in overrides the name check for any target.
		MssqlDatabaseIsDisposable("StockSharpLegacy", explicitlyAllowed: true).AssertTrue();
	}

	/// <summary>
	/// M9 (audit trigger): the kept, pure status-audit trigger <c>trg_Orders_StatusAudit</c> must append EXACTLY
	/// one <c>OrderStatusHistory</c> row when an order's status genuinely changes, and must NOT fire for an
	/// INSERT, a non-status column amendment, or a "change" to the same value (the AFTER UPDATE OF status +
	/// IS DISTINCT FROM guards). This is the regression the docs previously CLAIMED existed (M9) but did not.
	/// </summary>
	[TestMethod]
	public async Task Audit_StatusChange_WritesHistoryRow_PureAuditOnly()
	{
		await using var connection = await OpenPostgresAsync();
		await using var transaction = await connection.BeginTransactionAsync(CancellationToken);

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = await InsertPortfolioAsync(connection, transaction, "AUDIT_" + suffix);
		var securityId = await InsertSecurityAsync(connection, transaction, "SEC_" + suffix);
		var orderId = await PgInsertOrderReturningIdAsync(connection, transaction, portfolioId, securityId, "B", 100m, 150.00m, "PENDING");

		// Baseline: the INSERT itself does NOT create a history row (trigger is AFTER UPDATE OF status).
		(await CountHistoryAsync(connection, transaction, orderId)).AssertEqual(0L);

		// (a) a genuine status change PENDING -> FILLED writes exactly one history row with the old/new pair.
		await SetOrderStatusAsync(connection, transaction, orderId, "FILLED");
		(await CountHistoryAsync(connection, transaction, orderId)).AssertEqual(1L);
		await using (var hist = new NpgsqlCommand(
			"SELECT old_status, new_status FROM orderstatushistory WHERE order_id = @id ORDER BY history_id DESC LIMIT 1",
			connection, transaction))
		{
			hist.Parameters.Add(PgP("id", NpgsqlDbType.Bigint, orderId));
			await using var reader = await hist.ExecuteReaderAsync(CancellationToken);
			(await reader.ReadAsync(CancellationToken)).AssertTrue();
			reader.GetString(0).AssertEqual("PENDING"); // old_status
			reader.GetString(1).AssertEqual("FILLED");  // new_status
		}

		// (b) amending a NON-status column (price) does NOT create a history row (narrow, pure-audit).
		await using (var amend = new NpgsqlCommand("UPDATE orders SET price = 151.00 WHERE order_id = @id", connection, transaction))
		{
			amend.Parameters.Add(PgP("id", NpgsqlDbType.Bigint, orderId));
			await amend.ExecuteNonQueryAsync(CancellationToken);
		}
		(await CountHistoryAsync(connection, transaction, orderId)).AssertEqual(1L);

		// (c) an UPDATE that sets status to its CURRENT value (no real change) does NOT create a row
		//     (the IS DISTINCT FROM guard in the trigger function).
		await SetOrderStatusAsync(connection, transaction, orderId, "FILLED");
		(await CountHistoryAsync(connection, transaction, orderId)).AssertEqual(1L);

		await transaction.RollbackAsync(CancellationToken);
	}

	/// <summary>
	/// M9 (security case parity): the M4 case-insensitive functional unique index
	/// <c>UQ_Securities_code_board</c> must fold case so that <c>('AAPL','NASDAQ')</c> and
	/// <c>('aapl','nasdaq')</c> collide (the SQL Server default-CI-collation identity), and NULLS NOT DISTINCT
	/// must make two <c>(code, NULL board)</c> rows collide too. Both attempts run in their own transaction
	/// because the first duplicate aborts the surrounding transaction.
	/// </summary>
	[TestMethod]
	public async Task Securities_CaseVariantIdentity_CollidesInUniqueIndex()
	{
		await using var connection = await OpenPostgresAsync();

		var code = "CV" + Guid.NewGuid().ToString("N"); // unique base, mixed hex letters -> distinct upper/lower

		// (a) case-variant code AND board collide.
		await AssertSecondSecurityInsertCollidesAsync(connection,
			code.ToLowerInvariant(), "nasdaq",
			code.ToUpperInvariant(), "NASDAQ");

		// (b) NULLS NOT DISTINCT: two (code, NULL board) rows collide despite the NULL board.
		await AssertSecondSecurityInsertCollidesAsync(connection,
			code.ToLowerInvariant(), null,
			code.ToUpperInvariant(), null);
	}

	/// <summary>
	/// M5 / M9 (demo failure behavior): runs the BUILT demo executable against an UNREACHABLE database and
	/// asserts its documented failure contract - a non-zero exit code and a SANITIZED message that leaks
	/// neither the connection password nor a provider stack trace / SQLSTATE. Reports Inconclusive (skips)
	/// when the demo assembly has not been built, mirroring the harness's opt-in philosophy.
	/// </summary>
	[TestMethod]
	public async Task Demo_FailurePath_ExitsNonZeroAndSanitized()
	{
		var demoDll = FindDemoAssembly();
		if (demoDll is null)
		{
			Inconclusive("Demo assembly (03_Misc.LegacySqlDemo.dll) not found - build the demo to run this characterization.");
			return;
		}

		var startInfo = new ProcessStartInfo
		{
			FileName = "dotnet",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		startInfo.ArgumentList.Add(demoDll);
		// Point the demo at an unreachable database with a SECRET password we then prove is never printed.
		startInfo.Environment["STOCKSHARP_LEGACY_SQL_CONNECTION"] =
			"Host=localhost;Port=1;Database=nope;Username=demo;Password=THIS_MUST_NOT_LEAK;Timeout=3";

		using var process = Process.Start(startInfo);
		var stdout = await process.StandardOutput.ReadToEndAsync(CancellationToken);
		var stderr = await process.StandardError.ReadToEndAsync(CancellationToken);
		await process.WaitForExitAsync(CancellationToken);

		// (1) a setup failure exits NON-ZERO (the demo contract: 1 = setup failure).
		process.ExitCode.AssertEqual(1);
		// (2) the failure is reported, sanitized, with actionable guidance.
		stderr.Contains("Could not reach the legacy PostgreSQL database").AssertTrue();
		// (3) NOTHING sensitive/internal leaks: no password, no provider stack frames, no SQLSTATE.
		stderr.Contains("THIS_MUST_NOT_LEAK").AssertFalse();
		stdout.Contains("THIS_MUST_NOT_LEAK").AssertFalse();
		stderr.Contains("at Npgsql").AssertFalse();
		stderr.Contains("at System.").AssertFalse();
	}

	// --- Phase M6/M9 helpers ------------------------------------------------------------------------------

	/// <summary>Counts OrderStatusHistory rows for an order on the caller's transaction (audit-trigger test).</summary>
	private async Task<long> CountHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long orderId)
	{
		await using var command = new NpgsqlCommand(
			"SELECT COUNT(*) FROM orderstatushistory WHERE order_id = @id", connection, transaction);
		command.Parameters.Add(PgP("id", NpgsqlDbType.Bigint, orderId));
		return (long)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Sets an order's status on the caller's transaction (audit-trigger test).</summary>
	private async Task SetOrderStatusAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long orderId, string status)
	{
		await using var command = new NpgsqlCommand(
			"UPDATE orders SET status = @status WHERE order_id = @id", connection, transaction);
		command.Parameters.Add(PgP("status", NpgsqlDbType.Varchar, status));
		command.Parameters.Add(PgP("id", NpgsqlDbType.Bigint, orderId));
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Inserts a securities row with an explicit (code, nullable board) on the caller's transaction.</summary>
	private async Task InsertSecurityRowAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, string board)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO securities (security_code, board_code, security_type) VALUES (@code, @board, 'Stock')",
			connection, transaction);
		command.Parameters.Add(PgP("code", NpgsqlDbType.Varchar, code));
		command.Parameters.Add(PgP("board", NpgsqlDbType.Varchar, board));
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Inserts the first (code1, board1) securities row, then asserts the second (code2, board2) row
	/// violates the case-insensitive unique index with SQLSTATE 23505, on its own rolled-back transaction.</summary>
	private async Task AssertSecondSecurityInsertCollidesAsync(NpgsqlConnection connection, string code1, string board1, string code2, string board2)
	{
		await using var transaction = await connection.BeginTransactionAsync(CancellationToken);

		await InsertSecurityRowAsync(connection, transaction, code1, board1);

		var caught = false;
		try
		{
			await InsertSecurityRowAsync(connection, transaction, code2, board2);
		}
		catch (PostgresException ex) when (ex.SqlState == "23505") // 23505 = unique_violation
		{
			caught = true;
		}

		caught.AssertTrue();
		await transaction.RollbackAsync(CancellationToken);
	}

	/// <summary>Locates the built demo assembly by walking up from the test output directory to the repo root
	/// and searching the demo project's output; returns null when it has not been built.</summary>
	private static string FindDemoAssembly()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null)
		{
			var demoProjectDir = Path.Combine(dir.FullName, "Samples", "08_Misc", "03_LegacySqlDemo");
			if (Directory.Exists(demoProjectDir))
				// The demo's entry assembly is namespace-derived (StockSharp.Samples.Misc.LegacySqlDemo.dll),
				// not the .csproj file name. Prefer a Release build if both configurations are present.
				return Directory.EnumerateFiles(demoProjectDir, "StockSharp.Samples.Misc.LegacySqlDemo.dll", SearchOption.AllDirectories)
					.OrderByDescending(path => path.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
					.FirstOrDefault();

			dir = dir.Parent;
		}

		return null;
	}

	// =============================================================================================
	// Phase M1 - MANDATED PRODUCTION-PATH COVERAGE (review finding M1).
	//
	// Phases A-H prove pure-logic parity and run ValidateAsync for two demo scenarios, but the
	// checkpoint-required matrix demanded EXACT (not loose) reject reasons plus direct production-path
	// coverage of: concurrent SubmitOrderAsync serialization, REJECTED orders counting toward the rolling
	// frequency window, the UTC day-rollover boundary, market-order commission using a seeded latest trade,
	// exact first-breach reason ORDERING across all seven checks, NULL and ZERO ("unlimited") for EVERY
	// check read from the database, both order-side signs against a seeded position, and the non-positive
	// price/qty structural rejection (M11) on the real gateway. Every reject reason is asserted by
	// FULL-STRING equality against the exact text PreTradeRiskService emits, so a change to the wording,
	// the number rendering, or the check ORDERING is caught (the loose Contains checks of Phase G are NOT
	// sufficient for these). Reject-reason numbers follow the proven rendering rules: the order VALUE is the
	// canonical quantizer output (which preserves the input literal's scale, e.g. "500", "999"), while a
	// DB-read NUMERIC(18,4) limit always renders at scale 4 ("500.0000") and an INT limit as a plain integer
	// ("5", "60"). All DB tests use the MANDATORY-WHEN-CONFIGURED OpenPostgresAsync gate (Inconclusive only
	// when unset; FAIL when configured-but-unreachable), so a configured step-3 engine is always exercised.
	// =============================================================================================

	/// <summary>
	/// Concurrent <see cref="SqlLegacyOrderGateway.SubmitOrderAsync"/> serialization (M1). Fires a burst of
	/// identical, within-all-other-limits orders on ONE portfolio whose only enforced ceiling is the rolling
	/// frequency limit (5 per 60s). The transaction-scoped per-portfolio advisory lock (C4) serializes them,
	/// so the k-th committer sees exactly k-1 prior orders and the gate accepts PRECISELY
	/// <c>maxFreqCount - 1</c> (=4) and rejects the rest for FREQUENCY - a deterministic outcome that would
	/// NOT hold under the former read-then-write TOCTOU (a burst could all read count 0 and all be accepted).
	/// </summary>
	[TestMethod]
	public async Task Gateway_ConcurrentSubmit_SerializedByAdvisoryLock_AcceptsExactlyFreqCountMinusOne()
	{
		await using var manager = await OpenPostgresAsync();

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await PgInsertPortfolioCommittedAsync(manager, "M1_CONC_" + suffix);
			securityId = await PgInsertSecurityCommittedAsync(manager, "M1_CONC_" + suffix);

			// Only the rolling frequency ceiling is enforced (5 per 60s); every other limit stays NULL so the
			// advisory lock and the frequency window are the sole gate under concurrency.
			await PgInsertRiskLimitsCommittedAsync(manager, portfolioId, new GateVector
			{
				MaxOrderFreqCount = DemoMaxOrderFreqCount,          // 5
				MaxOrderFreqWindowSec = DemoMaxOrderFreqWindowSec,  // 60
				CommissionRate = DemoCommissionRate,
			});

			const int burst = 8;
			var gateway = new SqlLegacyOrderGateway(SqlLegacyConnection.Resolve());
			var tasks = new List<Task<SqlOrderSubmitResult>>(burst);
			for (var i = 0; i < burst; i++)
				tasks.Add(gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 150.00m, OrderTypes.Limit, cancellationToken: CancellationToken));

			var results = await Task.WhenAll(tasks);

			var accepted = results.Count(r => r.IsValid);
			var rejected = results.Where(r => !r.IsValid).ToList();

			// Serialized rolling window => exactly maxFreqCount-1 (=4) accepted, the remainder rejected.
			accepted.AssertEqual(DemoMaxOrderFreqCount - 1);
			rejected.Count.AssertEqual(burst - (DemoMaxOrderFreqCount - 1));

			foreach (var r in rejected)
			{
				// Every rejection is a FREQUENCY rejection naming the 60s window and the limit 5. The count
				// number varies with commit order ("5".."8"), which is legitimate, so it is not pinned.
				r.RejectReason.StartsWith("Order frequency ").AssertTrue();
				r.RejectReason.EndsWith("s meets/exceeds limit 5").AssertTrue();
			}

			// Both ACCEPTED and REJECTED orders are persisted (parity with usp_SubmitOrder), so all 8 rows exist.
			(await PgCountOrdersAsync(manager, portfolioId)).AssertEqual(burst);
		}
		finally
		{
			await PgCleanupAsync(manager, portfolioId, securityId);
		}
	}

	/// <summary>
	/// REJECTED orders count toward the rolling frequency window through the real gateway (M1). The first
	/// four submissions breach the PRICE ceiling and are REJECTED, yet each is still PERSISTED (parity with
	/// usp_SubmitOrder), so they occupy the window; the fifth submission is WITHIN the price ceiling but must
	/// be rejected for FREQUENCY - proving a rejected order still consumes a frequency slot. Reject reasons
	/// are asserted EXACTLY.
	/// </summary>
	[TestMethod]
	public async Task Gateway_RejectedOrders_CountTowardRollingFrequencyWindow()
	{
		await using var manager = await OpenPostgresAsync();

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await PgInsertPortfolioCommittedAsync(manager, "M1_RJF_" + suffix);
			securityId = await PgInsertSecurityCommittedAsync(manager, "M1_RJF_" + suffix);

			await PgInsertRiskLimitsCommittedAsync(manager, portfolioId, new GateVector
			{
				MaxOrderPrice = DemoMaxOrderPrice,                  // 500
				MaxOrderFreqCount = DemoMaxOrderFreqCount,          // 5
				MaxOrderFreqWindowSec = DemoMaxOrderFreqWindowSec,  // 60
				CommissionRate = DemoCommissionRate,
			});

			var gateway = new SqlLegacyOrderGateway(SqlLegacyConnection.Resolve());

			// Four price-ceiling rejections, each PERSISTED (real order_id > 0, not a structural short-circuit).
			for (var i = 0; i < 4; i++)
			{
				var rejPrice = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 1m, 999m, OrderTypes.Limit, cancellationToken: CancellationToken);
				rejPrice.IsValid.AssertFalse();
				rejPrice.RejectReason.AssertEqual("Order price 999 meets/exceeds limit 500.0000");
				(rejPrice.OrderId > 0L).AssertTrue();
			}

			// Fifth: price 150 clears the ceiling, but the four prior (rejected) orders fill the window.
			var rejFreq = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 1m, 150m, OrderTypes.Limit, cancellationToken: CancellationToken);
			rejFreq.IsValid.AssertFalse();
			rejFreq.RejectReason.AssertEqual("Order frequency 5 in 60s meets/exceeds limit 5");

			(await PgCountOrdersAsync(manager, portfolioId)).AssertEqual(5); // all five persisted
		}
		finally
		{
			await PgCleanupAsync(manager, portfolioId, securityId);
		}
	}

	/// <summary>
	/// Daily-volume UTC day rollover (M1). With an injected fixed clock, an ACCEPTED order dated the PRIOR
	/// UTC day must be EXCLUDED from today's volume (the half-open [dayStart, dayEnd) boundary), while an
	/// order dated this UTC day counts. Sub-scenario 1 accepts an order that only fits if yesterday's volume
	/// is excluded; sub-scenario 2 crosses the ceiling on THIS day's volume alone and asserts the EXACT
	/// daily-volume reason (whose number would differ if the prior day leaked in).
	/// </summary>
	[TestMethod]
	public async Task ValidateAsync_DailyVolume_ExcludesPriorUtcDay_AtDayRollover()
	{
		await using var cn = await OpenPostgresAsync();

		// Kind=Unspecified; the service normalizes its clock to Unspecified UTC, matching the `timestamp`
		// (without time zone) columns Npgsql reads back, so the day boundary is computed on one timeline.
		var fixedNow = new DateTime(2025, 6, 15, 12, 0, 0);
		var dayStart = fixedNow.Date;                 // 2025-06-15 00:00:00
		var priorDay = dayStart.AddSeconds(-1);       // 2025-06-14 23:59:59 (prior UTC day)
		var thisDay = dayStart.AddSeconds(1);         // 2025-06-15 00:00:01 (this UTC day)
		var service = new PreTradeRiskService(() => fixedNow);

		// Sub-scenario 1: ACCEPT only if the prior day is excluded (100000 today + 149999 = 249999 < 250000).
		await using (var txn = await cn.BeginTransactionAsync(CancellationToken))
		{
			var s = Guid.NewGuid().ToString("N");
			var pf = await InsertPortfolioAsync(cn, txn, "M1_ROLL_" + s);
			var sec = await InsertSecurityAsync(cn, txn, "M1_ROLL_" + s);
			await PgInsertRiskLimitsAsync(cn, txn, pf, null, new GateVector { MaxDailyVolume = DemoMaxDailyVolume }); // 250000, rest NULL
			await PgSeedOrderWithDateAsync(cn, txn, pf, sec, "B", 200000m, null, "ACCEPTED", priorDay); // prior day - must NOT count
			await PgSeedOrderWithDateAsync(cn, txn, pf, sec, "B", 100000m, null, "ACCEPTED", thisDay);  // this day - counts

			var accept = await service.ValidateAsync(cn, txn, pf, sec, "B", 149999m, null, CancellationToken);
			if (!accept.IsValid)
				Fail($"day-rollover: expected ACCEPT (prior-day volume must be excluded) but got REJECT: {accept.RejectReason}");

			await txn.RollbackAsync(CancellationToken);
		}

		// Sub-scenario 2: cross the ceiling on THIS day's volume alone (100000 + 150000 = 250000 >= 250000).
		await using (var txn = await cn.BeginTransactionAsync(CancellationToken))
		{
			var s = Guid.NewGuid().ToString("N");
			var pf = await InsertPortfolioAsync(cn, txn, "M1_ROLL2_" + s);
			var sec = await InsertSecurityAsync(cn, txn, "M1_ROLL2_" + s);
			await PgInsertRiskLimitsAsync(cn, txn, pf, null, new GateVector { MaxDailyVolume = DemoMaxDailyVolume });
			await PgSeedOrderWithDateAsync(cn, txn, pf, sec, "B", 200000m, null, "ACCEPTED", priorDay); // excluded
			await PgSeedOrderWithDateAsync(cn, txn, pf, sec, "B", 100000m, null, "ACCEPTED", thisDay);  // counts

			var reject = await service.ValidateAsync(cn, txn, pf, sec, "B", 150000m, null, CancellationToken);
			reject.IsValid.AssertFalse();
			// todayVolume (100000.0000, NUMERIC SUM) + qty (150000) = 250000.0000; had the prior day leaked in it would be 450000.0000.
			reject.RejectReason.AssertEqual("Daily volume 250000.0000 meets/exceeds limit 250000.0000");

			await txn.RollbackAsync(CancellationToken);
		}
	}

	/// <summary>
	/// Market-order commission uses the seeded LATEST trade price (M1). A market order (price null) has no
	/// ex-ante price, so its commission estimate uses the security's most-recent trade price. Sub-scenario A
	/// seeds one trade (price 1000) so the estimate INCLUDES <c>qty * 1000 * rate</c> and tips to the ceiling
	/// (EXACT reason asserted); sub-scenario B seeds NO trade, so the estimate omits that term and the order
	/// is ACCEPTED - isolating the seeded-trade contribution.
	/// </summary>
	[TestMethod]
	public async Task ValidateAsync_MarketOrderCommission_UsesSeededLatestTradePrice()
	{
		await using var cn = await OpenPostgresAsync();

		// A: WITH a seeded trade at price 1000 => est = Q( Q(10000)*0.0005 + 100*1000*0.0005 ) = 55.0000 >= 55.
		await using (var txn = await cn.BeginTransactionAsync(CancellationToken))
		{
			var s = Guid.NewGuid().ToString("N");
			var pf = await InsertPortfolioAsync(cn, txn, "M1_MKT_" + s);
			var sec = await InsertSecurityAsync(cn, txn, "M1_MKT_" + s);
			await PgInsertRiskLimitsAsync(cn, txn, pf, null, new GateVector { MaxCommissionTotal = 55m, CommissionRate = 0.0005m });
			var orderId = await PgInsertOrderReturningIdAsync(cn, txn, pf, sec, "B", 10m, 1000m, "FILLED");
			await PgInsertTradeTxnAsync(cn, txn, orderId, 10m, 1000m); // last trade price 1000; existing gross notional 10*1000 = 10000

			var reject = await new PreTradeRiskService().ValidateAsync(cn, txn, pf, sec, "B", 100m, null, CancellationToken);
			reject.IsValid.AssertFalse();
			reject.RejectReason.AssertEqual("Estimated cumulative commission 55.0000 meets/exceeds limit 55.0000");

			await txn.RollbackAsync(CancellationToken);
		}

		// B: WITHOUT any trade the market order has no last price => estimate is only the (zero) existing-notional term => ACCEPT.
		await using (var txn = await cn.BeginTransactionAsync(CancellationToken))
		{
			var s = Guid.NewGuid().ToString("N");
			var pf = await InsertPortfolioAsync(cn, txn, "M1_MKT2_" + s);
			var sec = await InsertSecurityAsync(cn, txn, "M1_MKT2_" + s);
			await PgInsertRiskLimitsAsync(cn, txn, pf, null, new GateVector { MaxCommissionTotal = 55m, CommissionRate = 0.0005m });

			var accept = await new PreTradeRiskService().ValidateAsync(cn, txn, pf, sec, "B", 100m, null, CancellationToken);
			if (!accept.IsValid)
				Fail($"market commission (no trade): expected ACCEPT but got REJECT: {accept.RejectReason}");

			await txn.RollbackAsync(CancellationToken);
		}
	}

	/// <summary>
	/// Exact first-breach reason ORDERING across all seven checks (M1). Each cascade vector makes the checks
	/// BEFORE the target pass (their limits NULL) and the target check breach, so the returned reason must be
	/// the target's - proving the seven checks run in the SAME order as the retired proc and the FIRST breach
	/// wins. Vectors 1-3 additionally set MULTIPLE low ceilings so more than one check would breach, yet the
	/// earliest still wins. Every reason is asserted by FULL-STRING equality.
	/// </summary>
	[TestMethod]
	public async Task ValidateAsync_FirstBreachReason_ExactOrderingCascade()
	{
		await using var cn = await OpenPostgresAsync();

		async Task AssertFirstBreachAsync(GateVector limits, string side, decimal qty, decimal? price, int recentOrders, string expected)
		{
			await using var txn = await cn.BeginTransactionAsync(CancellationToken);
			var s = Guid.NewGuid().ToString("N");
			var pf = await InsertPortfolioAsync(cn, txn, "M1_ORD_" + s);
			var sec = await InsertSecurityAsync(cn, txn, "M1_ORD_" + s);
			await PgInsertRiskLimitsAsync(cn, txn, pf, null, limits);
			for (var i = 0; i < recentOrders; i++)
				await PgSeedOrderAsync(cn, txn, pf, sec, "B", 1m, 1.00m, "PENDING");

			var r = await new PreTradeRiskService().ValidateAsync(cn, txn, pf, sec, side, qty, price, CancellationToken);
			r.IsValid.AssertFalse();
			r.RejectReason.AssertEqual(expected);

			await txn.RollbackAsync(CancellationToken);
		}

		// 1. price WINS over qty and notional (all three would breach).
		await AssertFirstBreachAsync(new GateVector { MaxOrderPrice = 100m, MaxOrderQty = 1m, MaxOrderValue = 100m },
			"B", 10m, 500m, 0, "Order price 500 meets/exceeds limit 100.0000");
		// 2. qty WINS over notional (price disabled).
		await AssertFirstBreachAsync(new GateVector { MaxOrderQty = 1m, MaxOrderValue = 100m },
			"B", 10m, 50m, 0, "Order qty 10 meets/exceeds limit 1.0000");
		// 3. notional (price + qty disabled).
		await AssertFirstBreachAsync(new GateVector { MaxOrderValue = 100m },
			"B", 10m, 50m, 0, "Order value 500 meets/exceeds limit 100.0000");
		// 4. frequency (2 recent + this = 3 >= 3).
		await AssertFirstBreachAsync(new GateVector { MaxOrderFreqCount = 3, MaxOrderFreqWindowSec = 60 },
			"B", 1m, null, 2, "Order frequency 3 in 60s meets/exceeds limit 3");
		// 5. resulting position (no seeded position => resulting = qty).
		await AssertFirstBreachAsync(new GateVector { MaxPositionSize = 100m },
			"B", 100m, null, 0, "Resulting position 100 meets/exceeds limit 100.0000");
		// 6. cumulative commission estimate (no trades => est = qty*price*rate = 10000*100*0.0005 = 500).
		await AssertFirstBreachAsync(new GateVector { MaxCommissionTotal = 500m, CommissionRate = 0.0005m },
			"B", 10000m, 100m, 0, "Estimated cumulative commission 500.0000 meets/exceeds limit 500.0000");
		// 7. daily volume (no seeded today volume => 0 + 100 = 100 >= 100).
		await AssertFirstBreachAsync(new GateVector { MaxDailyVolume = 100m },
			"B", 100m, null, 0, "Daily volume 100 meets/exceeds limit 100.0000");
	}

	/// <summary>
	/// A NULL or ZERO limit means "not enforced" for EVERY check, read from the database (M1 / AAP 0.6.4).
	/// For each of the seven checks, the target limit is set NULL and then 0 while a benign, always-passing
	/// companion ceiling stays enabled (so the "no configured limits" early-out does NOT mask the result -
	/// the target check is genuinely evaluated and skipped), and a would-breach order is submitted. Every
	/// case must ACCEPT.
	/// </summary>
	[TestMethod]
	public async Task ValidateAsync_NullAndZeroLimit_TreatedAsUnlimited_PerCheck()
	{
		await using var cn = await OpenPostgresAsync();

		async Task AssertUnlimitedAsync(string label, GateVector limits, string side, decimal qty, decimal? price, int recentOrders)
		{
			await using var txn = await cn.BeginTransactionAsync(CancellationToken);
			var s = Guid.NewGuid().ToString("N");
			var pf = await InsertPortfolioAsync(cn, txn, "M1_NZ_" + s);
			var sec = await InsertSecurityAsync(cn, txn, "M1_NZ_" + s);
			await PgInsertRiskLimitsAsync(cn, txn, pf, null, limits);
			for (var i = 0; i < recentOrders; i++)
				await PgSeedOrderAsync(cn, txn, pf, sec, "B", 1m, 1.00m, "PENDING");

			var r = await new PreTradeRiskService().ValidateAsync(cn, txn, pf, sec, side, qty, price, CancellationToken);
			if (!r.IsValid)
				Fail($"{label}: expected ACCEPT (NULL/0 limit = unlimited) but got REJECT: {r.RejectReason}");
			r.RejectReason.AssertNull();

			await txn.RollbackAsync(CancellationToken);
		}

		// Benign, always-passing companions keep a ceiling ENABLED so the early-out cannot mask the target:
		//  - a huge frequency limit (never tripped by 0 recent orders) companions the six non-frequency checks;
		//  - a huge position ceiling companions the frequency check itself.
		const int hugeFreq = 1_000_000;
		const decimal hugePos = 1_000_000_000_000m;

		// price
		await AssertUnlimitedAsync("price NULL", new GateVector { MaxOrderPrice = null, MaxOrderFreqCount = hugeFreq, MaxOrderFreqWindowSec = 60 }, "B", 1m, 999999m, 0);
		await AssertUnlimitedAsync("price 0", new GateVector { MaxOrderPrice = 0m, MaxOrderFreqCount = hugeFreq, MaxOrderFreqWindowSec = 60 }, "B", 1m, 999999m, 0);
		// qty
		await AssertUnlimitedAsync("qty NULL", new GateVector { MaxOrderQty = null, MaxOrderFreqCount = hugeFreq, MaxOrderFreqWindowSec = 60 }, "B", 999999m, null, 0);
		await AssertUnlimitedAsync("qty 0", new GateVector { MaxOrderQty = 0m, MaxOrderFreqCount = hugeFreq, MaxOrderFreqWindowSec = 60 }, "B", 999999m, null, 0);
		// notional (order value)
		await AssertUnlimitedAsync("value NULL", new GateVector { MaxOrderValue = null, MaxOrderFreqCount = hugeFreq, MaxOrderFreqWindowSec = 60 }, "B", 1000m, 1000m, 0);
		await AssertUnlimitedAsync("value 0", new GateVector { MaxOrderValue = 0m, MaxOrderFreqCount = hugeFreq, MaxOrderFreqWindowSec = 60 }, "B", 1000m, 1000m, 0);
		// frequency (10 recent orders would breach a small count; the check is disabled/unlimited when the
		// frequency pair is fully UNSET - both count and window NULL - or when the count is 0. The M2
		// paired-frequency CHECK forbids a HALF-set pair (count NULL with a window, or vice versa), so the
		// "disabled" case is represented as BOTH-NULL rather than count-NULL-with-a-window.
		await AssertUnlimitedAsync("freq NULL (both unset)", new GateVector { MaxOrderFreqCount = null, MaxOrderFreqWindowSec = null, MaxPositionSize = hugePos }, "B", 1m, null, 10);
		await AssertUnlimitedAsync("freq 0", new GateVector { MaxOrderFreqCount = 0, MaxOrderFreqWindowSec = 60, MaxPositionSize = hugePos }, "B", 1m, null, 10);
		// resulting position
		await AssertUnlimitedAsync("position NULL", new GateVector { MaxPositionSize = null, MaxOrderFreqCount = hugeFreq, MaxOrderFreqWindowSec = 60 }, "B", 999999m, null, 0);
		await AssertUnlimitedAsync("position 0", new GateVector { MaxPositionSize = 0m, MaxOrderFreqCount = hugeFreq, MaxOrderFreqWindowSec = 60 }, "B", 999999m, null, 0);
		// commission (rate present so the estimate is non-trivial; NULL/0 total disables the check)
		await AssertUnlimitedAsync("commission NULL", new GateVector { MaxCommissionTotal = null, CommissionRate = 0.0005m, MaxOrderFreqCount = hugeFreq, MaxOrderFreqWindowSec = 60 }, "B", 10000m, 1000m, 0);
		await AssertUnlimitedAsync("commission 0", new GateVector { MaxCommissionTotal = 0m, CommissionRate = 0.0005m, MaxOrderFreqCount = hugeFreq, MaxOrderFreqWindowSec = 60 }, "B", 10000m, 1000m, 0);
		// daily volume
		await AssertUnlimitedAsync("daily NULL", new GateVector { MaxDailyVolume = null, MaxOrderFreqCount = hugeFreq, MaxOrderFreqWindowSec = 60 }, "B", 999999m, null, 0);
		await AssertUnlimitedAsync("daily 0", new GateVector { MaxDailyVolume = 0m, MaxOrderFreqCount = hugeFreq, MaxOrderFreqWindowSec = 60 }, "B", 999999m, null, 0);
	}

	/// <summary>
	/// Both order-side signs against seeded database position state (M1). The position-size subject is the
	/// HYPOTHETICAL post-fill position <c>currentQty + signedDelta</c>, where <c>B</c> adds and <c>S</c>
	/// subtracts. Against a LONG (+50000) seed a buy tips the magnitude over the ceiling (REJECT) while a
	/// sell reduces it (ACCEPT); against a SHORT (-50000) seed a sell grows the magnitude (REJECT) while a
	/// buy reduces it (ACCEPT). Reject reasons - including the sign of the resulting quantity - are EXACT.
	/// </summary>
	[TestMethod]
	public async Task ValidateAsync_PositionSize_BothSideSigns_ThroughDatabaseState()
	{
		await using var cn = await OpenPostgresAsync();

		async Task RunSignAsync(decimal seededQty, string side, decimal orderQty, bool expectReject, string expectedReason)
		{
			await using var txn = await cn.BeginTransactionAsync(CancellationToken);
			var s = Guid.NewGuid().ToString("N");
			var pf = await InsertPortfolioAsync(cn, txn, "M1_SIGN_" + s);
			var sec = await InsertSecurityAsync(cn, txn, "M1_SIGN_" + s);
			await PgInsertRiskLimitsAsync(cn, txn, pf, null, new GateVector { MaxPositionSize = DemoMaxPositionSize }); // 100000
			await PgInsertPositionAsync(cn, txn, pf, sec, seededQty);

			var r = await new PreTradeRiskService().ValidateAsync(cn, txn, pf, sec, side, orderQty, null, CancellationToken);
			if (expectReject)
			{
				r.IsValid.AssertFalse();
				r.RejectReason.AssertEqual(expectedReason);
			}
			else if (!r.IsValid)
			{
				Fail($"sign test (seed {seededQty}, {side} {orderQty}): expected ACCEPT but got REJECT: {r.RejectReason}");
			}

			await txn.RollbackAsync(CancellationToken);
		}

		// long +50000: buy adds (50000 + 60000 = 110000 => reject), sell reduces (50000 - 60000 = -10000 => accept).
		await RunSignAsync(50000m, "B", 60000m, true, "Resulting position 110000.0000 meets/exceeds limit 100000.0000");
		await RunSignAsync(50000m, "S", 60000m, false, null);
		// short -50000: sell grows magnitude (-50000 - 60000 = -110000 => reject), buy reduces (-50000 + 60000 = 10000 => accept).
		await RunSignAsync(-50000m, "S", 60000m, true, "Resulting position -110000.0000 meets/exceeds limit 100000.0000");
		await RunSignAsync(-50000m, "B", 60000m, false, null);
	}

	/// <summary>
	/// Non-positive price/qty is rejected STRUCTURALLY on the real gateway with NO row persisted (M11/M12).
	/// A zero/negative quantity or a non-null non-positive price is a malformed request that violates the
	/// schema CHECKs, so the gateway short-circuits BEFORE any database read: it returns the rejection DTO
	/// (OrderId 0) and writes no orders row, so it also never consumes a frequency slot. Reasons are EXACT.
	/// </summary>
	[TestMethod]
	public async Task Gateway_NonPositivePriceOrQty_RejectedStructurally_NoRowPersisted()
	{
		await using var manager = await OpenPostgresAsync();

		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = 0;
		var securityId = 0;

		try
		{
			portfolioId = await PgInsertPortfolioCommittedAsync(manager, "M1_NEG_" + suffix);
			securityId = await PgInsertSecurityCommittedAsync(manager, "M1_NEG_" + suffix);

			var gateway = new SqlLegacyOrderGateway(SqlLegacyConnection.Resolve());

			var negPrice = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, -0.01m, OrderTypes.Limit, cancellationToken: CancellationToken);
			negPrice.IsValid.AssertFalse();
			negPrice.OrderId.AssertEqual(0L);
			negPrice.RejectReason.AssertEqual("Invalid price -0.01");

			var zeroQty = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 0m, 150.00m, OrderTypes.Limit, cancellationToken: CancellationToken);
			zeroQty.IsValid.AssertFalse();
			zeroQty.OrderId.AssertEqual(0L);
			zeroQty.RejectReason.AssertEqual("Invalid qty");

			// Neither malformed request may leak an orders row (not persisted => not counted for frequency).
			(await PgCountOrdersAsync(manager, portfolioId)).AssertEqual(0);
		}
		finally
		{
			await PgCleanupAsync(manager, portfolioId, securityId);
		}
	}

	/// <summary>
	/// Non-positive price is rejected by the service BEFORE any ceiling (M11). Even with the full DEMO
	/// ceilings seeded, a zero or negative price must be rejected up front by the structural guard (never
	/// bypassing the price / notional / commission checks). Reasons are EXACT.
	/// </summary>
	[TestMethod]
	public async Task ValidateAsync_NonPositivePrice_RejectedBeforeAnyCeiling()
	{
		await using var cn = await OpenPostgresAsync();
		await using var txn = await cn.BeginTransactionAsync(CancellationToken);

		var s = Guid.NewGuid().ToString("N");
		var pf = await InsertPortfolioAsync(cn, txn, "M1_NEGV_" + s);
		var sec = await InsertSecurityAsync(cn, txn, "M1_NEGV_" + s);
		await InsertDemoRiskLimitsAsync(cn, txn, pf);

		var zero = await new PreTradeRiskService().ValidateAsync(cn, txn, pf, sec, "B", 100m, 0m, CancellationToken);
		zero.IsValid.AssertFalse();
		zero.RejectReason.AssertEqual("Invalid price 0");

		var negative = await new PreTradeRiskService().ValidateAsync(cn, txn, pf, sec, "B", 100m, -5m, CancellationToken);
		negative.IsValid.AssertFalse();
		negative.RejectReason.AssertEqual("Invalid price -5");

		await txn.RollbackAsync(CancellationToken);
	}

	// =============================================================================================
	// Phase H - DUAL-ENGINE STAGED FOUR-STEP VALIDATION (AAP 0.6.3).
	//
	// Two independent change axes landed in one refactor: (A) LOGIC consolidation - the accept/reject
	// decision moved out of the retired T-SQL procedure dbo.usp_ValidatePreTradeRisk into the canonical
	// C# PreTradeRiskService.EvaluateGate; and (B) ENGINE migration - SQL Server -> PostgreSQL. A single
	// test run cannot attribute a regression to the right axis, so the four steps hold one axis constant
	// at a time (the ORDERING is the diagnostic instrument):
	//
	//   Step 1 - the GOLDEN baseline: execute the ORIGINAL dbo.usp_ValidatePreTradeRisk on a LIVE SQL
	//            Server instance (materialized from the embedded characterization script) per vector.
	//   Step 2 - the consolidated C# logic with the ENGINE held at SQL Server: read the SAME state from
	//            SQL Server and run the REAL PreTradeRiskService.EvaluateGate; a mismatch vs Step 1
	//            isolates a LOGIC regression (engine constant).
	//   Step 3 - the consolidated C# logic on the MIGRATED engine: run the REAL, database-state-aware
	//            PreTradeRiskService.ValidateAsync on PostgreSQL; passing Step 2 but failing Step 3
	//            isolates an ENGINE/dialect regression (logic constant).
	//   Step 4 - cross-compare all three per vector so any divergence is attributable to a named axis.
	//
	// This is authentic because EvaluateGate is the SAME pure decision core that ValidateAsync delegates
	// to in production - Step 2 and Step 3 exercise identical LOGIC, differing ONLY by engine. Both
	// engines must be reachable for Steps 1-2 (SQL Server) and Step 3 (PostgreSQL); when a required engine
	// is not configured the step reports Inconclusive, and when it is configured but unreachable it FAILS.
	// =============================================================================================

	/// <summary>
	/// One parity vector: the effective limits to seed, the prospective order to submit, and the expected
	/// decision. Every vector is engine-agnostic - the SAME instance drives the SQL Server proc (Step 1),
	/// EvaluateGate on SQL Server state (Step 2), and ValidateAsync on PostgreSQL (Step 3). Only the
	/// targeted check sets its limit(s); the rest stay NULL (unlimited) so each vector isolates one check.
	/// Reject reasons are compared LOOSELY by <see cref="ExpectRejectCategory"/> (a stable leading phrase
	/// emitted identically by both engines), never by exact string, because SQL Server renders NUMERIC with
	/// trailing zeros while C# <c>decimal</c> keeps its own scale (documented Info-6 cosmetic difference).
	/// </summary>
	private sealed class GateVector
	{
		/// <summary>Stable identifier used in assertion failure messages.</summary>
		public string Name { get; init; }
		/// <summary>Human-readable name of the single check this vector isolates.</summary>
		public string TargetCheck { get; init; }
		public decimal? MaxOrderPrice { get; init; }
		public decimal? MaxOrderQty { get; init; }
		public decimal? MaxOrderValue { get; init; }
		public decimal? MaxPositionSize { get; init; }
		public decimal? MaxDailyVolume { get; init; }
		public int? MaxOrderFreqCount { get; init; }
		public int? MaxOrderFreqWindowSec { get; init; }
		public decimal? MaxCommissionTotal { get; init; }
		public decimal? CommissionRate { get; init; }
		/// <summary>Number of PENDING orders to pre-seed for the portfolio (drives the frequency window).</summary>
		public int SeedRecentOrderCount { get; init; }
		/// <summary>Total qty of a single pre-seeded ACCEPTED order dated today (drives the daily-volume rollup); 0 = none.</summary>
		public decimal SeedTodayAcceptedQty { get; init; }
		/// <summary>Prospective order side, "B" or "S".</summary>
		public string Side { get; init; } = "B";
		/// <summary>Prospective order quantity.</summary>
		public decimal Qty { get; init; }
		/// <summary>Prospective order price, or <see langword="null"/> for a market order.</summary>
		public decimal? Price { get; init; }
		/// <summary>Expected accept (true) / reject (false) decision, identical on all three engines.</summary>
		public bool ExpectValid { get; init; }
		/// <summary>Stable leading phrase the reject reason must contain when <see cref="ExpectValid"/> is false.</summary>
		public string ExpectRejectCategory { get; init; }
	}

	/// <summary>
	/// The shared parity vector table: all seven checks at below / at / above their ceiling, plus one
	/// within-ALL-limits accept. "At" and "above" reject (">=" meets-or-exceeds); "below" accepts. Positive
	/// limits are used throughout so Step 1 == Step 2 == Step 3 (the 0-limit divergence is a SEPARATE test).
	/// </summary>
	private static IReadOnlyList<GateVector> ParityVectors { get; } = new[]
	{
		// 1. Order price ceiling (limit 500): 499 accepts, 500/501 reject.
		new GateVector { Name = "price_below", TargetCheck = "order price", MaxOrderPrice = DemoMaxOrderPrice, Qty = 1m, Price = 499.00m, ExpectValid = true },
		new GateVector { Name = "price_at",    TargetCheck = "order price", MaxOrderPrice = DemoMaxOrderPrice, Qty = 1m, Price = 500.00m, ExpectValid = false, ExpectRejectCategory = "Order price" },
		new GateVector { Name = "price_above", TargetCheck = "order price", MaxOrderPrice = DemoMaxOrderPrice, Qty = 1m, Price = 501.00m, ExpectValid = false, ExpectRejectCategory = "Order price" },
		// 2. Order qty ceiling (limit 10000): market orders (price null) at 9999 / 10000 / 10001.
		new GateVector { Name = "qty_below", TargetCheck = "order qty", MaxOrderQty = DemoMaxOrderQty, Qty = 9999m,  Price = null, ExpectValid = true },
		new GateVector { Name = "qty_at",    TargetCheck = "order qty", MaxOrderQty = DemoMaxOrderQty, Qty = 10000m, Price = null, ExpectValid = false, ExpectRejectCategory = "Order qty" },
		new GateVector { Name = "qty_above", TargetCheck = "order qty", MaxOrderQty = DemoMaxOrderQty, Qty = 10001m, Price = null, ExpectValid = false, ExpectRejectCategory = "Order qty" },
		// 3. Notional value ceiling (limit 1,000,000) at price 100: qty 9999/10000/10001 -> 999900/1000000/1000100.
		new GateVector { Name = "value_below", TargetCheck = "order value", MaxOrderValue = DemoMaxOrderValue, Qty = 9999m,  Price = 100.00m, ExpectValid = true },
		new GateVector { Name = "value_at",    TargetCheck = "order value", MaxOrderValue = DemoMaxOrderValue, Qty = 10000m, Price = 100.00m, ExpectValid = false, ExpectRejectCategory = "Order value" },
		new GateVector { Name = "value_above", TargetCheck = "order value", MaxOrderValue = DemoMaxOrderValue, Qty = 10001m, Price = 100.00m, ExpectValid = false, ExpectRejectCategory = "Order value" },
		// 4. Order frequency (limit 5 per 60s): seed 3/4/5 recent orders; gate adds the prospective (+1) -> 4/5/6.
		new GateVector { Name = "freq_below", TargetCheck = "order frequency", MaxOrderFreqCount = DemoMaxOrderFreqCount, MaxOrderFreqWindowSec = DemoMaxOrderFreqWindowSec, SeedRecentOrderCount = 3, Qty = 1m, Price = null, ExpectValid = true },
		new GateVector { Name = "freq_at",    TargetCheck = "order frequency", MaxOrderFreqCount = DemoMaxOrderFreqCount, MaxOrderFreqWindowSec = DemoMaxOrderFreqWindowSec, SeedRecentOrderCount = 4, Qty = 1m, Price = null, ExpectValid = false, ExpectRejectCategory = "Order frequency" },
		new GateVector { Name = "freq_above", TargetCheck = "order frequency", MaxOrderFreqCount = DemoMaxOrderFreqCount, MaxOrderFreqWindowSec = DemoMaxOrderFreqWindowSec, SeedRecentOrderCount = 5, Qty = 1m, Price = null, ExpectValid = false, ExpectRejectCategory = "Order frequency" },
		// 5. Resulting position size (limit 100000) from flat: market orders at 99999 / 100000 / 100001.
		new GateVector { Name = "pos_below", TargetCheck = "resulting position", MaxPositionSize = DemoMaxPositionSize, Qty = 99999m,  Price = null, ExpectValid = true },
		new GateVector { Name = "pos_at",    TargetCheck = "resulting position", MaxPositionSize = DemoMaxPositionSize, Qty = 100000m, Price = null, ExpectValid = false, ExpectRejectCategory = "Resulting position" },
		new GateVector { Name = "pos_above", TargetCheck = "resulting position", MaxPositionSize = DemoMaxPositionSize, Qty = 100001m, Price = null, ExpectValid = false, ExpectRejectCategory = "Resulting position" },
		// 6. Cumulative commission ESTIMATE (limit 5000) at rate 0.0005, price 1000: qty*price*rate = 4999.5/5000/5000.5.
		new GateVector { Name = "comm_below", TargetCheck = "commission", MaxCommissionTotal = DemoMaxCommissionTotal, CommissionRate = DemoCommissionRate, Qty = 9999m,  Price = 1000.00m, ExpectValid = true },
		new GateVector { Name = "comm_at",    TargetCheck = "commission", MaxCommissionTotal = DemoMaxCommissionTotal, CommissionRate = DemoCommissionRate, Qty = 10000m, Price = 1000.00m, ExpectValid = false, ExpectRejectCategory = "Estimated cumulative commission" },
		new GateVector { Name = "comm_above", TargetCheck = "commission", MaxCommissionTotal = DemoMaxCommissionTotal, CommissionRate = DemoCommissionRate, Qty = 10001m, Price = 1000.00m, ExpectValid = false, ExpectRejectCategory = "Estimated cumulative commission" },
		// 7. Daily traded volume (limit 250000): seed one ACCEPTED order qty 100000 today; prospective 149999/150000/150001 -> 249999/250000/250001.
		new GateVector { Name = "daily_below", TargetCheck = "daily volume", MaxDailyVolume = DemoMaxDailyVolume, SeedTodayAcceptedQty = 100000m, Qty = 149999m, Price = null, ExpectValid = true },
		new GateVector { Name = "daily_at",    TargetCheck = "daily volume", MaxDailyVolume = DemoMaxDailyVolume, SeedTodayAcceptedQty = 100000m, Qty = 150000m, Price = null, ExpectValid = false, ExpectRejectCategory = "Daily volume" },
		new GateVector { Name = "daily_above", TargetCheck = "daily volume", MaxDailyVolume = DemoMaxDailyVolume, SeedTodayAcceptedQty = 100000m, Qty = 150001m, Price = null, ExpectValid = false, ExpectRejectCategory = "Daily volume" },
		// 8. Within EVERY limit -> accept (all ceilings enabled at their demo values).
		new GateVector { Name = "all_within", TargetCheck = "all limits", MaxOrderPrice = DemoMaxOrderPrice, MaxOrderQty = DemoMaxOrderQty, MaxOrderValue = DemoMaxOrderValue, MaxPositionSize = DemoMaxPositionSize, MaxDailyVolume = DemoMaxDailyVolume, MaxOrderFreqCount = DemoMaxOrderFreqCount, MaxOrderFreqWindowSec = DemoMaxOrderFreqWindowSec, MaxCommissionTotal = DemoMaxCommissionTotal, CommissionRate = DemoCommissionRate, Qty = 100m, Price = 150.00m, ExpectValid = true },
	};

	/// <summary>
	/// STEP 1 (golden baseline): run the ORIGINAL <c>dbo.usp_ValidatePreTradeRisk</c> on a LIVE SQL Server
	/// instance for every parity vector and assert it produces the documented decision. This captures the
	/// behaviour of the retired procedure before any consolidation, establishing the reference every later
	/// step is measured against.
	/// </summary>
	[TestMethod]
	public async Task Step1_OriginalSqlServerProcedure_GoldenBaseline()
	{
		await using var connection = await OpenSqlServerAsync();
		foreach (var vector in ParityVectors)
		{
			var (isValid, reason) = await SqlProcDecisionAsync(connection, vector);
			AssertGateDecision(isValid, reason, vector, "Step 1 (original dbo.usp_ValidatePreTradeRisk on SQL Server)");
		}
	}

	/// <summary>
	/// STEP 2 (logic axis, engine held at SQL Server): read the SAME state from SQL Server and run the REAL
	/// <see cref="PreTradeRiskService.EvaluateGate"/> - the pure decision core that production
	/// <see cref="PreTradeRiskService.ValidateAsync"/> delegates to. Because the engine is unchanged from
	/// Step 1, any divergence here is a LOGIC regression in the consolidated C# gate, not an engine effect.
	/// </summary>
	[TestMethod]
	public async Task Step2_ConsolidatedGate_OnSqlServerState_MatchesGolden()
	{
		await using var connection = await OpenSqlServerAsync();
		foreach (var vector in ParityVectors)
		{
			var result = await EvaluateGateOnSqlServerAsync(connection, vector);
			AssertGateDecision(result.IsValid, result.RejectReason, vector, "Step 2 (PreTradeRiskService.EvaluateGate on SQL Server state)");
		}
	}

	/// <summary>
	/// STEP 3 (engine axis, logic held constant): run the REAL, database-state-aware
	/// <see cref="PreTradeRiskService.ValidateAsync"/> against a LIVE PostgreSQL instance for every vector.
	/// The logic is identical to Step 2 (ValidateAsync delegates to EvaluateGate), so passing Step 2 but
	/// failing here isolates an ENGINE / dialect regression introduced by the SQL Server -> PostgreSQL
	/// migration. This is the containerized step-3 run covering ALL seven checks at below/at/above.
	/// </summary>
	[TestMethod]
	public async Task Step3_ConsolidatedGate_OnPostgreSql_MatchesGolden()
	{
		await using var connection = await OpenPostgresAsync();
		foreach (var vector in ParityVectors)
		{
			var result = await ValidateAsyncOnPostgresAsync(connection, vector);
			AssertGateDecision(result.IsValid, result.RejectReason, vector, "Step 3 (PreTradeRiskService.ValidateAsync on PostgreSQL)");
		}
	}

	/// <summary>
	/// STEP 3 (most-specific RiskLimits selection) via the REAL <see cref="PreTradeRiskService.ValidateAsync"/>
	/// on PostgreSQL. Two sub-cases prove the production selection ports the T-SQL
	/// <c>ORDER BY CASE ... effective_date DESC</c> exactly: (1) SPECIFICITY - portfolio+security beats
	/// portfolio-only beats security-only; (2) effective_date TIE-BREAK - within the same specificity the
	/// newest row wins. Each sub-case is arranged so the decision differs depending on which row is chosen,
	/// so the accept/reject outcome alone proves the correct row was selected.
	/// </summary>
	[TestMethod]
	public async Task Step3_MostSpecificRiskLimitsSelection_ViaValidateAsync()
	{
		await using var connection = await OpenPostgresAsync();
		var service = new PreTradeRiskService();

		// Sub-case 1 - SPECIFICITY: Row A (portfolio+security) caps price at 100; Row B (portfolio-only) at
		// 1000; Row C (security-only) at 2000. An order at 150 rejects ONLY if Row A (the most specific) won.
		{
			await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
			var suffix = Guid.NewGuid().ToString("N");
			var portfolioId = await InsertPortfolioAsync(connection, transaction, "MSPEC_" + suffix);
			var securityId = await InsertSecurityAsync(connection, transaction, "MSPEC_" + suffix);
			await PgInsertPriceLimitAsync(connection, transaction, portfolioId, securityId, 100.00m); // Row A (rank 0)
			await PgInsertPriceLimitAsync(connection, transaction, portfolioId, null, 1000.00m);      // Row B (rank 1)
			await PgInsertPriceLimitAsync(connection, transaction, null, securityId, 2000.00m);       // Row C (rank 2)

			var rejected = await service.ValidateAsync(connection, transaction, portfolioId, securityId, "B", 1m, 150.00m, CancellationToken);
			rejected.IsValid.AssertFalse();
			rejected.RejectReason.AssertNotNull();
			rejected.RejectReason.Contains("Order price").AssertTrue();

			// Sanity: 99 < 100 accepts under Row A, confirming Row A's 100 ceiling (not a looser row) is active.
			var accepted = await service.ValidateAsync(connection, transaction, portfolioId, securityId, "B", 1m, 99.00m, CancellationToken);
			accepted.IsValid.AssertTrue();

			await transaction.RollbackAsync(CancellationToken);
		}

		// Sub-case 2 - effective_date TIE-BREAK: two portfolio-only rows, the OLDER capping at 1000 and the
		// NEWER at 100. An order at 150 rejects ONLY if the NEWER row won (ORDER BY effective_date DESC).
		{
			await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
			var suffix = Guid.NewGuid().ToString("N");
			var portfolioId = await InsertPortfolioAsync(connection, transaction, "MSPEC2_" + suffix);
			var securityId = await InsertSecurityAsync(connection, transaction, "MSPEC2_" + suffix);
			await PgInsertPriceLimitWithAgeAsync(connection, transaction, portfolioId, 1000.00m, 1); // OLDER, looser
			await PgInsertPriceLimitWithAgeAsync(connection, transaction, portfolioId, 100.00m, 0);  // NEWER, stricter

			var rejected = await service.ValidateAsync(connection, transaction, portfolioId, securityId, "B", 1m, 150.00m, CancellationToken);
			rejected.IsValid.AssertFalse();
			rejected.RejectReason.Contains("Order price").AssertTrue();

			await transaction.RollbackAsync(CancellationToken);
		}
	}

	/// <summary>
	/// STEP 4 (attribution matrix): the staged ORDERING is itself the diagnostic instrument. For every
	/// vector this runs all three engines and cross-compares pairwise so a regression is attributable to a
	/// specific axis - Step 1 vs Step 2 holds the ENGINE constant (SQL Server), so a mismatch is a LOGIC
	/// regression; Step 2 vs Step 3 holds the LOGIC constant (canonical C#), so a mismatch is an
	/// ENGINE/dialect regression. Requires BOTH engines to be configured.
	/// </summary>
	[TestMethod]
	public async Task Step4_StagedOrdering_AttributionMatrix()
	{
		await using var sqlServer = await OpenSqlServerAsync();
		await using var postgres = await OpenPostgresAsync();

		foreach (var vector in ParityVectors)
		{
			var step1 = await SqlProcDecisionAsync(sqlServer, vector);
			var step2 = await EvaluateGateOnSqlServerAsync(sqlServer, vector);
			var step3 = await ValidateAsyncOnPostgresAsync(postgres, vector);

			// Every engine must match the vector's documented expectation ...
			AssertGateDecision(step1.IsValid, step1.Reason, vector, "Step 1 (SQL Server procedure)");
			AssertGateDecision(step2.IsValid, step2.RejectReason, vector, "Step 2 (EvaluateGate on SQL Server state)");
			AssertGateDecision(step3.IsValid, step3.RejectReason, vector, "Step 3 (ValidateAsync on PostgreSQL)");

			// ... and the axes must agree pairwise so any regression is attributable to a named axis.
			if (step1.IsValid != step2.IsValid)
				Fail($"LOGIC-axis regression on vector '{vector.Name}': Step 1 (proc) IsValid={step1.IsValid} but Step 2 (EvaluateGate) IsValid={step2.IsValid} - the ENGINE was held at SQL Server, so the consolidated C# LOGIC diverged from the retired procedure.");
			if (step2.IsValid != step3.IsValid)
				Fail($"ENGINE-axis regression on vector '{vector.Name}': Step 2 (SQL Server state) IsValid={step2.IsValid} but Step 3 (PostgreSQL) IsValid={step3.IsValid} - the LOGIC was held constant, so the SQL Server -> PostgreSQL migration changed the decision.");
		}
	}

	/// <summary>
	/// STEP 4 (documented divergence): the ONE deliberate, AAP-0.6.4-mandated difference between the retired
	/// SQL logic and the canonical C# logic. The retired proc guards each ceiling with <c>IS NOT NULL</c>, so
	/// a literal 0 ceiling REJECTS; the canonical convention treats 0 as "unlimited" and ACCEPTS. This is a
	/// LOGIC-axis difference only: the two canonical C# evaluations (Step 2 on SQL Server state, Step 3 on
	/// PostgreSQL) must AGREE with each other (both accept) - only the retired proc diverges (rejects). It is
	/// asserted as EXPECTED so a future regression that silently changes the 0-limit meaning is caught.
	/// </summary>
	[TestMethod]
	public async Task Step4_ZeroLimitDivergence_IsDocumentedAndExpected()
	{
		var vector = new GateVector { Name = "zero_price_limit", TargetCheck = "order price (0)", MaxOrderPrice = 0m, CommissionRate = DemoCommissionRate, Qty = 1m, Price = 123.00m, ExpectValid = true };

		await using var sqlServer = await OpenSqlServerAsync();
		await using var postgres = await OpenPostgresAsync();

		var procDecision = await SqlProcDecisionAsync(sqlServer, vector);        // retired SQL logic
		var gateOnSqlServer = await EvaluateGateOnSqlServerAsync(sqlServer, vector); // canonical C# on SQL Server state
		var validateOnPostgres = await ValidateAsyncOnPostgresAsync(postgres, vector); // canonical C# on PostgreSQL

		// Retired SQL rejects on the 0 ceiling (IS NOT NULL, and 123 >= 0).
		procDecision.IsValid.AssertFalse();
		procDecision.Reason.AssertNotNull();
		// Canonical C# treats 0 as unlimited and ACCEPTS - on BOTH engines, proving the divergence is LOGIC-only.
		gateOnSqlServer.IsValid.AssertTrue();
		validateOnPostgres.IsValid.AssertTrue();
		// The two canonical evaluations agree (engine axis is clean); only the retired SQL logic differs.
		gateOnSqlServer.IsValid.AssertEqual(validateOnPostgres.IsValid);
	}

	/// <summary>
	/// Shared decision assertion: verifies the engine's accept/reject matches the vector's expectation and,
	/// on a reject, that the reason CONTAINS the expected category phrase (loose match - see
	/// <see cref="GateVector.ExpectRejectCategory"/>). Failure messages name the engine and vector so a
	/// staged run pinpoints exactly which step and vector diverged.
	/// </summary>
	private void AssertGateDecision(bool actualValid, string actualReason, GateVector vector, string engine)
	{
		if (vector.ExpectValid)
		{
			if (!actualValid)
				Fail($"{engine}: vector '{vector.Name}' expected ACCEPT but got REJECT (reason: {actualReason}).");
		}
		else
		{
			if (actualValid)
				Fail($"{engine}: vector '{vector.Name}' expected REJECT (category '{vector.ExpectRejectCategory}') but got ACCEPT.");
			if (actualReason.IsEmpty())
				Fail($"{engine}: vector '{vector.Name}' expected a reject reason containing '{vector.ExpectRejectCategory}' but the reason was empty.");
			if (!actualReason.Contains(vector.ExpectRejectCategory))
				Fail($"{engine}: vector '{vector.Name}' expected reject category '{vector.ExpectRejectCategory}' but the reason was '{actualReason}'.");
		}
	}

	// --- Mandatory connection guards (Inconclusive ONLY when unset; Fail when configured-but-unreachable) ---

	/// <summary>
	/// Opens a PostgreSQL connection for the step-3 tests. Reports <c>Inconclusive</c> ONLY when
	/// STOCKSHARP_LEGACY_SQL_CONNECTION is unset (no Postgres engine opted in for this run); FAILS when the
	/// variable IS set but the database cannot be opened, so a configured engine is always actually exercised.
	/// </summary>
	private async Task<NpgsqlConnection> OpenPostgresAsync()
	{
		// Gate on the opt-in variable: Resolve() always yields a localhost fallback, so "is a Postgres engine
		// configured for THIS run" is decided by the variable, not by Resolve().
		if (Environment.GetEnvironmentVariable("STOCKSHARP_LEGACY_SQL_CONNECTION").IsEmpty())
		{
			Inconclusive("No PostgreSQL connection configured (STOCKSHARP_LEGACY_SQL_CONNECTION unset) - skipping staged PostgreSQL parity.");
			return null; // unreachable (Inconclusive throws); required for definite assignment.
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

			// Configured but unreachable is a genuine FAILURE, not a skip - a configured step-3 engine must run.
			Fail($"STOCKSHARP_LEGACY_SQL_CONNECTION is configured but PostgreSQL is not reachable: {ex.Message}");
			return null; // unreachable (Fail throws); required for definite assignment.
		}
	}

	/// <summary>
	/// Opens a SQL Server connection for the step-1/step-2 golden baseline and ensures the embedded
	/// characterization schema + retired procedures exist on it. Reports <c>Inconclusive</c> ONLY when
	/// STOCKSHARP_LEGACY_MSSQL_CONNECTION is unset; FAILS when it is set but SQL Server is unreachable.
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
	/// M6 test-safety predicate: is <paramref name="databaseName"/> an acceptable target for the destructive
	/// characterization DDL (CREATE TABLE / CREATE OR ALTER PROCEDURE)? True ONLY when the name marks it as a
	/// disposable test database (contains "test", case-insensitive) OR the operator has explicitly opted in
	/// via <paramref name="explicitlyAllowed"/>. Pure and side-effect-free so it is unit-testable WITHOUT a
	/// live SQL Server (see <see cref="MssqlHarness_RefusesNonDisposableDatabase"/>). The twin copy in
	/// PositionRecalculationTests is intentionally identical (that harness is duplicated per file).
	/// </summary>
	private static bool MssqlDatabaseIsDisposable(string databaseName, bool explicitlyAllowed)
		=> explicitlyAllowed
			|| (!databaseName.IsEmpty() && databaseName.Contains("test", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Materializes the ORIGINAL SQL Server characterization schema and the two retired procedures on the
	/// given connection by executing the embedded <c>OriginalCharacterizationSetup.sql</c> resource. The
	/// script is idempotent (guarded CREATEs / CREATE OR ALTER), so re-runs are safe; a static flag avoids
	/// redundant executions within a single process. Runs each GO-delimited batch on the SAME connection so
	/// the leading SET ANSI_NULLS/QUOTED_IDENTIFIER ON persist for the filtered-index creation.
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

			// M6 (test safety): this harness runs DDL that MUTATES the target database. Refuse to touch
			// anything that is not clearly a DISPOSABLE test database, so a STOCKSHARP_LEGACY_MSSQL_CONNECTION
			// accidentally aimed at a shared/production instance cannot be (re)initialized. Accept only when the
			// database NAME marks it disposable (contains "test") OR the operator explicitly opts in via
			// STOCKSHARP_LEGACY_MSSQL_ALLOW_DDL. See QA/README.md for disposable SQL Server provisioning.
			if (!MssqlDatabaseIsDisposable(connection.Database,
					!Environment.GetEnvironmentVariable("STOCKSHARP_LEGACY_MSSQL_ALLOW_DDL").IsEmpty()))
				Fail($"Refusing to run characterization DDL against database '{connection.Database}': it is not a " +
					"recognized disposable test database. Point STOCKSHARP_LEGACY_MSSQL_CONNECTION at a DISPOSABLE " +
					"database whose name contains 'test', or set STOCKSHARP_LEGACY_MSSQL_ALLOW_DDL=1 to confirm the " +
					"target is safe to (re)initialize (see QA/README.md).");

			var assembly = typeof(PreTradeRiskParityTests).Assembly;
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

	// --- SQL Server engine helpers (Step 1 proc + Step 2 EvaluateGate-on-SQL-Server-state) ---

	/// <summary>
	/// STEP 1 per-vector: seeds the scenario in a transaction, executes the ORIGINAL
	/// <c>dbo.usp_ValidatePreTradeRisk</c> with the prospective order (reading its BIT/NVARCHAR OUTPUT
	/// parameters), then rolls back. The procedure reads the seeded state itself, so the prospective order
	/// is NOT inserted (it is a pre-trade gate).
	/// </summary>
	private async Task<(bool IsValid, string Reason)> SqlProcDecisionAsync(SqlConnection connection, GateVector vector)
	{
		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken);
		var (portfolioId, securityId) = await SeedSqlServerScenarioAsync(connection, transaction, vector);

		await using var command = new SqlCommand(
			"EXEC dbo.usp_ValidatePreTradeRisk @portfolio_id = @portfolio_id, @security_id = @security_id, " +
			"@side = @side, @qty = @qty, @price = @price, @is_valid = @is_valid OUTPUT, @reject_reason = @reject_reason OUTPUT",
			connection, transaction);
		command.Parameters.Add(SqlP("@portfolio_id", SqlDbType.Int, portfolioId));
		command.Parameters.Add(SqlP("@security_id", SqlDbType.Int, securityId));
		command.Parameters.Add(SqlP("@side", SqlDbType.VarChar, vector.Side));
		command.Parameters.Add(SqlDec("@qty", vector.Qty));
		command.Parameters.Add(SqlDec("@price", vector.Price));
		var isValidParam = new SqlParameter("@is_valid", SqlDbType.Bit) { Direction = ParameterDirection.Output };
		var reasonParam = new SqlParameter("@reject_reason", SqlDbType.NVarChar, 200) { Direction = ParameterDirection.Output };
		command.Parameters.Add(isValidParam);
		command.Parameters.Add(reasonParam);
		await command.ExecuteNonQueryAsync(CancellationToken);

		var isValid = (bool)isValidParam.Value;
		var reason = reasonParam.Value as string; // DBNull.Value -> null
		await transaction.RollbackAsync(CancellationToken);
		return (isValid, reason);
	}

	/// <summary>
	/// STEP 2 per-vector: seeds the scenario in a transaction, reads the resolved limits and the five
	/// database-derived aggregates FROM SQL SERVER, runs the REAL <see cref="PreTradeRiskService.EvaluateGate"/>
	/// on that state, then rolls back. The engine is SQL Server exactly as in Step 1, so only the LOGIC differs.
	/// </summary>
	private async Task<PreTradeRiskResult> EvaluateGateOnSqlServerAsync(SqlConnection connection, GateVector vector)
	{
		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken);
		var (portfolioId, securityId) = await SeedSqlServerScenarioAsync(connection, transaction, vector);
		var inputs = await ReadSqlServerGateStateAsync(connection, transaction, portfolioId, securityId, vector);
		var result = PreTradeRiskService.EvaluateGate(in inputs);
		await transaction.RollbackAsync(CancellationToken);
		return result;
	}

	/// <summary>
	/// Reads, FROM SQL SERVER, exactly the state <see cref="PreTradeRiskService.EvaluateGate"/> consumes:
	/// the most-specific risklimits row (replicating the proc's <c>ORDER BY CASE ... effective_date DESC</c>),
	/// the trailing-window order count, the current position qty, the existing gross notional / last trade
	/// price (commission), and today's accepted volume. Each aggregate is read only when its ceiling is
	/// enabled (present AND &gt; 0), mirroring ValidateAsync, and returns the assembled <see cref="GateInputs"/>.
	/// </summary>
	private async Task<GateInputs> ReadSqlServerGateStateAsync(SqlConnection connection, SqlTransaction transaction, int portfolioId, int securityId, GateVector vector)
	{
		decimal? maxOrderPrice = null, maxOrderQty = null, maxOrderValue = null, maxPositionSize = null, maxDailyVolume = null, maxCommissionTotal = null, commissionRate = null;
		int? maxOrderFreqCount = null, maxOrderFreqWindowSec = null;

		await using (var command = new SqlCommand(
			"SELECT TOP (1) max_order_price, max_order_qty, max_order_value, max_position_size, max_daily_volume, " +
			"max_order_freq_count, max_order_freq_window_sec, max_commission_total, commission_rate " +
			"FROM dbo.RiskLimits WHERE is_active = 1 " +
			"AND (portfolio_id = @portfolio_id OR portfolio_id IS NULL) " +
			"AND (security_id = @security_id OR security_id IS NULL) " +
			"ORDER BY CASE WHEN portfolio_id IS NOT NULL AND security_id IS NOT NULL THEN 0 WHEN portfolio_id IS NOT NULL THEN 1 ELSE 2 END, effective_date DESC",
			connection, transaction))
		{
			command.Parameters.Add(SqlP("@portfolio_id", SqlDbType.Int, portfolioId));
			command.Parameters.Add(SqlP("@security_id", SqlDbType.Int, securityId));
			await using var reader = await command.ExecuteReaderAsync(CancellationToken);
			if (await reader.ReadAsync(CancellationToken))
			{
				maxOrderPrice = reader.IsDBNull(0) ? null : reader.GetDecimal(0);
				maxOrderQty = reader.IsDBNull(1) ? null : reader.GetDecimal(1);
				maxOrderValue = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
				maxPositionSize = reader.IsDBNull(3) ? null : reader.GetDecimal(3);
				maxDailyVolume = reader.IsDBNull(4) ? null : reader.GetDecimal(4);
				maxOrderFreqCount = reader.IsDBNull(5) ? null : reader.GetInt32(5);
				maxOrderFreqWindowSec = reader.IsDBNull(6) ? null : reader.GetInt32(6);
				maxCommissionTotal = reader.IsDBNull(7) ? null : reader.GetDecimal(7);
				commissionRate = reader.IsDBNull(8) ? null : reader.GetDecimal(8);
			}
		}

		// Enabled = present AND > 0 (canonical NULL-or-0 = unlimited); reads are skipped for disabled ceilings.
		var frequencyEnabled = maxOrderFreqCount is > 0 && maxOrderFreqWindowSec is > 0;
		var positionEnabled = maxPositionSize is > 0m;
		var commissionEnabled = maxCommissionTotal is > 0m;
		var dailyEnabled = maxDailyVolume is > 0m;

		long recentOrderCount = 0;
		if (frequencyEnabled)
		{
			await using var command = new SqlCommand(
				"SELECT COUNT(*) FROM dbo.Orders WHERE portfolio_id = @portfolio_id " +
				"AND submitted_date >= DATEADD(SECOND, -@window, SYSUTCDATETIME())", connection, transaction);
			command.Parameters.Add(SqlP("@portfolio_id", SqlDbType.Int, portfolioId));
			command.Parameters.Add(SqlP("@window", SqlDbType.Int, maxOrderFreqWindowSec.Value));
			recentOrderCount = Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken));
		}

		decimal currentPositionQty = 0m;
		if (positionEnabled)
		{
			await using var command = new SqlCommand(
				"SELECT qty FROM dbo.Positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id", connection, transaction);
			command.Parameters.Add(SqlP("@portfolio_id", SqlDbType.Int, portfolioId));
			command.Parameters.Add(SqlP("@security_id", SqlDbType.Int, securityId));
			var scalar = await command.ExecuteScalarAsync(CancellationToken);
			if (scalar is not null and not DBNull)
				currentPositionQty = (decimal)scalar;
		}

		decimal existingGrossNotional = 0m;
		decimal? lastTradePrice = null;
		if (commissionEnabled)
		{
			// Market order (no price) falls back to the security's most recent trade price, as the proc did.
			if (vector.Price is null)
			{
				await using var command = new SqlCommand(
					"SELECT TOP (1) t.price FROM dbo.Trades t JOIN dbo.Orders o ON o.order_id = t.order_id " +
					"WHERE o.security_id = @security_id ORDER BY t.executed_date DESC", connection, transaction);
				command.Parameters.Add(SqlP("@security_id", SqlDbType.Int, securityId));
				var scalar = await command.ExecuteScalarAsync(CancellationToken);
				if (scalar is not null and not DBNull)
					lastTradePrice = (decimal)scalar;
			}

			// Existing gross notional the retired proc's own way (SUM of trade qty*price); 0 for a fresh portfolio.
			await using (var command = new SqlCommand(
				"SELECT SUM(t.qty * t.price) FROM dbo.Trades t JOIN dbo.Orders o ON o.order_id = t.order_id " +
				"WHERE o.portfolio_id = @portfolio_id", connection, transaction))
			{
				command.Parameters.Add(SqlP("@portfolio_id", SqlDbType.Int, portfolioId));
				var scalar = await command.ExecuteScalarAsync(CancellationToken);
				if (scalar is not null and not DBNull)
					existingGrossNotional = (decimal)scalar;
			}
		}

		decimal todayVolume = 0m;
		if (dailyEnabled)
		{
			await using var command = new SqlCommand(
				"SELECT SUM(qty) FROM dbo.Orders WHERE portfolio_id = @portfolio_id AND security_id = @security_id " +
				"AND status IN ('ACCEPTED','FILLED','PARTFILLED') AND CAST(submitted_date AS DATE) = CAST(SYSUTCDATETIME() AS DATE)", connection, transaction);
			command.Parameters.Add(SqlP("@portfolio_id", SqlDbType.Int, portfolioId));
			command.Parameters.Add(SqlP("@security_id", SqlDbType.Int, securityId));
			var scalar = await command.ExecuteScalarAsync(CancellationToken);
			if (scalar is not null and not DBNull)
				todayVolume = (decimal)scalar;
		}

		return new GateInputs
		{
			Side = vector.Side,
			Qty = vector.Qty,
			Price = vector.Price,
			MaxOrderPrice = maxOrderPrice,
			MaxOrderQty = maxOrderQty,
			MaxOrderValue = maxOrderValue,
			MaxPositionSize = maxPositionSize,
			MaxDailyVolume = maxDailyVolume,
			MaxOrderFreqCount = maxOrderFreqCount,
			MaxOrderFreqWindowSec = maxOrderFreqWindowSec,
			MaxCommissionTotal = maxCommissionTotal,
			CommissionRate = commissionRate,
			RecentOrderCount = recentOrderCount,
			CurrentPositionQty = currentPositionQty,
			ExistingGrossNotional = existingGrossNotional,
			LastTradePrice = lastTradePrice,
			TodayVolume = todayVolume,
		};
	}

	/// <summary>Seeds a fresh portfolio + security, the vector's risklimits row (portfolio-scoped), and any
	/// recent PENDING orders (frequency) / today ACCEPTED order (daily volume) the vector requires. The
	/// prospective order is NOT seeded - both engines read state and decide on the prospective separately.</summary>
	private async Task<(int PortfolioId, int SecurityId)> SeedSqlServerScenarioAsync(SqlConnection connection, SqlTransaction transaction, GateVector vector)
	{
		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = await SqlInsertPortfolioAsync(connection, transaction, "PARITY_" + suffix);
		var securityId = await SqlInsertSecurityAsync(connection, transaction, "SEC_" + suffix);
		await SqlInsertRiskLimitsAsync(connection, transaction, portfolioId, null, vector);
		for (var i = 0; i < vector.SeedRecentOrderCount; i++)
			await SqlSeedOrderAsync(connection, transaction, portfolioId, securityId, "B", 1m, 1.00m, "PENDING");
		if (vector.SeedTodayAcceptedQty > 0m)
			await SqlSeedOrderAsync(connection, transaction, portfolioId, securityId, "B", vector.SeedTodayAcceptedQty, 1.00m, "ACCEPTED");
		return (portfolioId, securityId);
	}

	private async Task<int> SqlInsertPortfolioAsync(SqlConnection connection, SqlTransaction transaction, string name)
	{
		await using var command = new SqlCommand(
			"INSERT INTO dbo.Portfolios (name) OUTPUT INSERTED.portfolio_id VALUES (@name)", connection, transaction);
		command.Parameters.Add(SqlP("@name", SqlDbType.NVarChar, name));
		return (int)await command.ExecuteScalarAsync(CancellationToken);
	}

	private async Task<int> SqlInsertSecurityAsync(SqlConnection connection, SqlTransaction transaction, string code)
	{
		await using var command = new SqlCommand(
			"INSERT INTO dbo.Securities (security_code) OUTPUT INSERTED.security_id VALUES (@code)", connection, transaction);
		command.Parameters.Add(SqlP("@code", SqlDbType.NVarChar, code));
		return (int)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Inserts one risklimits row from the vector's limit fields (commission_rate always supplied,
	/// since the column is NOT NULL). Decimal parameters carry explicit NUMERIC precision/scale so SQL Server
	/// does not silently truncate to scale 0.</summary>
	private async Task SqlInsertRiskLimitsAsync(SqlConnection connection, SqlTransaction transaction, int portfolioId, int? securityId, GateVector vector)
	{
		await using var command = new SqlCommand(
			"INSERT INTO dbo.RiskLimits (portfolio_id, security_id, max_order_price, max_order_qty, max_order_value, " +
			"max_position_size, max_daily_volume, max_order_freq_count, max_order_freq_window_sec, max_commission_total, commission_rate) " +
			"VALUES (@portfolio_id, @security_id, @max_order_price, @max_order_qty, @max_order_value, " +
			"@max_position_size, @max_daily_volume, @max_order_freq_count, @max_order_freq_window_sec, @max_commission_total, @commission_rate)",
			connection, transaction);
		command.Parameters.Add(SqlP("@portfolio_id", SqlDbType.Int, portfolioId));
		command.Parameters.Add(SqlP("@security_id", SqlDbType.Int, securityId));
		command.Parameters.Add(SqlDec("@max_order_price", vector.MaxOrderPrice));
		command.Parameters.Add(SqlDec("@max_order_qty", vector.MaxOrderQty));
		command.Parameters.Add(SqlDec("@max_order_value", vector.MaxOrderValue));
		command.Parameters.Add(SqlDec("@max_position_size", vector.MaxPositionSize));
		command.Parameters.Add(SqlDec("@max_daily_volume", vector.MaxDailyVolume));
		command.Parameters.Add(SqlP("@max_order_freq_count", SqlDbType.Int, vector.MaxOrderFreqCount));
		command.Parameters.Add(SqlP("@max_order_freq_window_sec", SqlDbType.Int, vector.MaxOrderFreqWindowSec));
		command.Parameters.Add(SqlDec("@max_commission_total", vector.MaxCommissionTotal));
		command.Parameters.Add(SqlDec("@commission_rate", vector.CommissionRate ?? 0.0005m, 9, 6));
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	private async Task SqlSeedOrderAsync(SqlConnection connection, SqlTransaction transaction, int portfolioId, int securityId, string side, decimal qty, decimal? price, string status)
	{
		await using var command = new SqlCommand(
			"INSERT INTO dbo.Orders (portfolio_id, security_id, side, qty, price, order_type, status) " +
			"VALUES (@portfolio_id, @security_id, @side, @qty, @price, @order_type, @status)", connection, transaction);
		command.Parameters.Add(SqlP("@portfolio_id", SqlDbType.Int, portfolioId));
		command.Parameters.Add(SqlP("@security_id", SqlDbType.Int, securityId));
		command.Parameters.Add(SqlP("@side", SqlDbType.VarChar, side));
		command.Parameters.Add(SqlDec("@qty", qty));
		command.Parameters.Add(SqlDec("@price", price));
		command.Parameters.Add(SqlP("@order_type", SqlDbType.VarChar, price is null ? "MARKET" : "LIMIT"));
		command.Parameters.Add(SqlP("@status", SqlDbType.VarChar, status));
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Builds a SQL Server parameter, mapping a null value (including a null nullable) to DBNull.</summary>
	private static SqlParameter SqlP(string name, SqlDbType type, object value)
		=> new(name, type) { Value = value ?? DBNull.Value };

	/// <summary>Builds a decimal SQL Server parameter with explicit NUMERIC precision/scale (default 18,4;
	/// 9,6 for the commission rate) so the value is never truncated to scale 0.</summary>
	private static SqlParameter SqlDec(string name, object value, byte precision = 18, byte scale = 4)
		=> new(name, SqlDbType.Decimal) { Precision = precision, Scale = scale, Value = value ?? DBNull.Value };

	// --- PostgreSQL engine helpers (Step 3 real ValidateAsync + self-contained seeding) ---

	/// <summary>STEP 3 per-vector: seeds the scenario in a transaction and runs the REAL, database-state-aware
	/// <see cref="PreTradeRiskService.ValidateAsync"/> on PostgreSQL for the prospective order, then rolls back.</summary>
	private async Task<PreTradeRiskResult> ValidateAsyncOnPostgresAsync(NpgsqlConnection connection, GateVector vector)
	{
		await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
		var (portfolioId, securityId) = await SeedPostgresScenarioAsync(connection, transaction, vector);
		var result = await new PreTradeRiskService()
			.ValidateAsync(connection, transaction, portfolioId, securityId, vector.Side, vector.Qty, vector.Price, CancellationToken);
		await transaction.RollbackAsync(CancellationToken);
		return result;
	}

	/// <summary>PostgreSQL counterpart of <see cref="SeedSqlServerScenarioAsync"/>: identical seed shape,
	/// Postgres dialect.</summary>
	private async Task<(int PortfolioId, int SecurityId)> SeedPostgresScenarioAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, GateVector vector)
	{
		var suffix = Guid.NewGuid().ToString("N");
		var portfolioId = await InsertPortfolioAsync(connection, transaction, "PARITY_" + suffix);
		var securityId = await InsertSecurityAsync(connection, transaction, "SEC_" + suffix);
		await PgInsertRiskLimitsAsync(connection, transaction, portfolioId, null, vector);
		for (var i = 0; i < vector.SeedRecentOrderCount; i++)
			await PgSeedOrderAsync(connection, transaction, portfolioId, securityId, "B", 1m, 1.00m, "PENDING");
		if (vector.SeedTodayAcceptedQty > 0m)
			await PgSeedOrderAsync(connection, transaction, portfolioId, securityId, "B", vector.SeedTodayAcceptedQty, 1.00m, "ACCEPTED");
		return (portfolioId, securityId);
	}

	private async Task PgInsertRiskLimitsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int portfolioId, int? securityId, GateVector vector)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO risklimits (portfolio_id, security_id, max_order_price, max_order_qty, max_order_value, " +
			"max_position_size, max_daily_volume, max_order_freq_count, max_order_freq_window_sec, max_commission_total, commission_rate) " +
			"VALUES (@portfolio_id, @security_id, @max_order_price, @max_order_qty, @max_order_value, " +
			"@max_position_size, @max_daily_volume, @max_order_freq_count, @max_order_freq_window_sec, @max_commission_total, @commission_rate)",
			connection, transaction);
		command.Parameters.Add(PgP("portfolio_id", NpgsqlDbType.Integer, portfolioId));
		command.Parameters.Add(PgP("security_id", NpgsqlDbType.Integer, securityId));
		command.Parameters.Add(PgP("max_order_price", NpgsqlDbType.Numeric, vector.MaxOrderPrice));
		command.Parameters.Add(PgP("max_order_qty", NpgsqlDbType.Numeric, vector.MaxOrderQty));
		command.Parameters.Add(PgP("max_order_value", NpgsqlDbType.Numeric, vector.MaxOrderValue));
		command.Parameters.Add(PgP("max_position_size", NpgsqlDbType.Numeric, vector.MaxPositionSize));
		command.Parameters.Add(PgP("max_daily_volume", NpgsqlDbType.Numeric, vector.MaxDailyVolume));
		command.Parameters.Add(PgP("max_order_freq_count", NpgsqlDbType.Integer, vector.MaxOrderFreqCount));
		command.Parameters.Add(PgP("max_order_freq_window_sec", NpgsqlDbType.Integer, vector.MaxOrderFreqWindowSec));
		command.Parameters.Add(PgP("max_commission_total", NpgsqlDbType.Numeric, vector.MaxCommissionTotal));
		command.Parameters.Add(PgP("commission_rate", NpgsqlDbType.Numeric, vector.CommissionRate ?? 0.0005m));
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Inserts a price-only risklimits row for the given (nullable) portfolio/security scope, used
	/// by the most-specific-selection test.</summary>
	private async Task PgInsertPriceLimitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int? portfolioId, int? securityId, decimal maxOrderPrice)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO risklimits (portfolio_id, security_id, max_order_price, commission_rate) " +
			"VALUES (@portfolio_id, @security_id, @max_order_price, 0.0005)", connection, transaction);
		command.Parameters.Add(PgP("portfolio_id", NpgsqlDbType.Integer, portfolioId));
		command.Parameters.Add(PgP("security_id", NpgsqlDbType.Integer, securityId));
		command.Parameters.Add(PgP("max_order_price", NpgsqlDbType.Numeric, maxOrderPrice));
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Inserts a portfolio-only price-limit row whose effective_date is <paramref name="ageDays"/>
	/// days in the past (via a SQL-side interval so no client DateTime-kind conversion is needed), used by
	/// the effective_date tie-break test.</summary>
	private async Task PgInsertPriceLimitWithAgeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int portfolioId, decimal maxOrderPrice, int ageDays)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO risklimits (portfolio_id, max_order_price, commission_rate, effective_date) " +
			"VALUES (@portfolio_id, @max_order_price, 0.0005, (now() at time zone 'utc') - make_interval(days => @age))", connection, transaction);
		command.Parameters.Add(PgP("portfolio_id", NpgsqlDbType.Integer, portfolioId));
		command.Parameters.Add(PgP("max_order_price", NpgsqlDbType.Numeric, maxOrderPrice));
		command.Parameters.Add(PgP("age", NpgsqlDbType.Integer, ageDays));
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	private async Task PgSeedOrderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int portfolioId, int securityId, string side, decimal qty, decimal? price, string status)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO orders (portfolio_id, security_id, side, qty, price, order_type, status) " +
			"VALUES (@portfolio_id, @security_id, @side, @qty, @price, @order_type, @status)", connection, transaction);
		command.Parameters.Add(PgP("portfolio_id", NpgsqlDbType.Integer, portfolioId));
		command.Parameters.Add(PgP("security_id", NpgsqlDbType.Integer, securityId));
		command.Parameters.Add(PgP("side", NpgsqlDbType.Varchar, side));
		command.Parameters.Add(PgP("qty", NpgsqlDbType.Numeric, qty));
		command.Parameters.Add(PgP("price", NpgsqlDbType.Numeric, price));
		command.Parameters.Add(PgP("order_type", NpgsqlDbType.Varchar, price is null ? "MARKET" : "LIMIT"));
		command.Parameters.Add(PgP("status", NpgsqlDbType.Varchar, status));
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Builds a Npgsql parameter, mapping a null value (including a null nullable) to DBNull.</summary>
	private static NpgsqlParameter PgP(string name, NpgsqlDbType type, object value)
		=> new(name, type) { Value = value ?? DBNull.Value };

	// --- Phase M1 helpers: COMMITTED (auto-commit) seed/teardown for the real-gateway tests -----------------
	// The production gateway opens its OWN connection via SqlLegacyConnection.Resolve(), so its risk reads see
	// only COMMITTED rows. These helpers therefore insert on the manager connection with no explicit
	// transaction (each statement auto-commits), mirroring PositionRecalculationTests' committed gateway path.

	/// <summary>Inserts a portfolio (auto-commit) and returns its generated id, for the real-gateway tests.</summary>
	private async Task<int> PgInsertPortfolioCommittedAsync(NpgsqlConnection connection, string name)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO portfolios (name) VALUES (@name) RETURNING portfolio_id", connection);
		command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Varchar) { Value = name });
		return (int)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Inserts a security (auto-commit) and returns its generated id, for the real-gateway tests.</summary>
	private async Task<int> PgInsertSecurityCommittedAsync(NpgsqlConnection connection, string code)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO securities (security_code) VALUES (@code) RETURNING security_id", connection);
		command.Parameters.Add(new NpgsqlParameter("code", NpgsqlDbType.Varchar) { Value = code });
		return (int)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Inserts a portfolio-scoped risklimits row (auto-commit) from a <see cref="GateVector"/>; the
	/// committed counterpart of <see cref="PgInsertRiskLimitsAsync"/> used by the real-gateway tests.</summary>
	private async Task PgInsertRiskLimitsCommittedAsync(NpgsqlConnection connection, int portfolioId, GateVector vector)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO risklimits (portfolio_id, security_id, max_order_price, max_order_qty, max_order_value, " +
			"max_position_size, max_daily_volume, max_order_freq_count, max_order_freq_window_sec, max_commission_total, commission_rate) " +
			"VALUES (@portfolio_id, NULL, @max_order_price, @max_order_qty, @max_order_value, " +
			"@max_position_size, @max_daily_volume, @max_order_freq_count, @max_order_freq_window_sec, @max_commission_total, @commission_rate)",
			connection);
		command.Parameters.Add(PgP("portfolio_id", NpgsqlDbType.Integer, portfolioId));
		command.Parameters.Add(PgP("max_order_price", NpgsqlDbType.Numeric, vector.MaxOrderPrice));
		command.Parameters.Add(PgP("max_order_qty", NpgsqlDbType.Numeric, vector.MaxOrderQty));
		command.Parameters.Add(PgP("max_order_value", NpgsqlDbType.Numeric, vector.MaxOrderValue));
		command.Parameters.Add(PgP("max_position_size", NpgsqlDbType.Numeric, vector.MaxPositionSize));
		command.Parameters.Add(PgP("max_daily_volume", NpgsqlDbType.Numeric, vector.MaxDailyVolume));
		command.Parameters.Add(PgP("max_order_freq_count", NpgsqlDbType.Integer, vector.MaxOrderFreqCount));
		command.Parameters.Add(PgP("max_order_freq_window_sec", NpgsqlDbType.Integer, vector.MaxOrderFreqWindowSec));
		command.Parameters.Add(PgP("max_commission_total", NpgsqlDbType.Numeric, vector.MaxCommissionTotal));
		command.Parameters.Add(PgP("commission_rate", NpgsqlDbType.Numeric, vector.CommissionRate ?? 0.0005m));
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Counts the orders persisted for a portfolio (auto-commit), used to assert what the gateway wrote.</summary>
	private async Task<int> PgCountOrdersAsync(NpgsqlConnection connection, int portfolioId)
	{
		await using var command = new NpgsqlCommand(
			"SELECT COUNT(*) FROM orders WHERE portfolio_id = @p", connection);
		command.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Integer) { Value = portfolioId });
		return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken));
	}

	/// <summary>FK-respecting teardown of everything seeded for a (portfolio, security), mirroring
	/// PositionRecalculationTests.CleanupAsync so the real-gateway tests leave the shared database pristine.</summary>
	private async Task PgCleanupAsync(NpgsqlConnection connection, int portfolioId, int securityId)
	{
		if (portfolioId != 0)
		{
			await PgExecCommittedAsync(connection,
				"DELETE FROM orderstatushistory WHERE order_id IN (SELECT order_id FROM orders WHERE portfolio_id = @p)", portfolioId);
			await PgExecCommittedAsync(connection,
				"DELETE FROM trades WHERE order_id IN (SELECT order_id FROM orders WHERE portfolio_id = @p)", portfolioId);
			await PgExecCommittedAsync(connection, "DELETE FROM positions WHERE portfolio_id = @p", portfolioId);
			await PgExecCommittedAsync(connection, "DELETE FROM risklimits WHERE portfolio_id = @p", portfolioId);
			await PgExecCommittedAsync(connection, "DELETE FROM orders WHERE portfolio_id = @p", portfolioId);
		}

		if (securityId != 0)
			await PgExecCommittedAsync(connection, "DELETE FROM securities WHERE security_id = @p", securityId);

		if (portfolioId != 0)
			await PgExecCommittedAsync(connection, "DELETE FROM portfolios WHERE portfolio_id = @p", portfolioId);
	}

	/// <summary>Runs a single-parameter (<c>@p</c>) teardown statement (auto-commit).</summary>
	private async Task PgExecCommittedAsync(NpgsqlConnection connection, string sql, int id)
	{
		await using var command = new NpgsqlCommand(sql, connection);
		command.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Integer) { Value = id });
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	// --- Phase M1 helpers: transactional seeders for the direct ValidateAsync tests ------------------------

	/// <summary>Inserts a position row (qty may be negative for a short) on the caller's transaction;
	/// avg_price / realized_pnl / unrealized_pnl / updated_date take their schema defaults (0 / now()).</summary>
	private async Task PgInsertPositionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int portfolioId, int securityId, decimal qty)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO positions (portfolio_id, security_id, qty) VALUES (@portfolio_id, @security_id, @qty)", connection, transaction);
		command.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
		command.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });
		command.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = qty });
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Seeds an order with an EXPLICIT <c>submitted_date</c> (Kind=Unspecified UTC) so the daily-volume
	/// day-boundary tests can place rows in a specific UTC day; a null price yields a MARKET order.</summary>
	private async Task PgSeedOrderWithDateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int portfolioId, int securityId, string side, decimal qty, decimal? price, string status, DateTime submittedDate)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO orders (portfolio_id, security_id, side, qty, price, order_type, status, submitted_date) " +
			"VALUES (@portfolio_id, @security_id, @side, @qty, @price, @order_type, @status, @submitted_date)", connection, transaction);
		command.Parameters.Add(PgP("portfolio_id", NpgsqlDbType.Integer, portfolioId));
		command.Parameters.Add(PgP("security_id", NpgsqlDbType.Integer, securityId));
		command.Parameters.Add(PgP("side", NpgsqlDbType.Varchar, side));
		command.Parameters.Add(PgP("qty", NpgsqlDbType.Numeric, qty));
		command.Parameters.Add(PgP("price", NpgsqlDbType.Numeric, price));
		command.Parameters.Add(PgP("order_type", NpgsqlDbType.Varchar, price is null ? "MARKET" : "LIMIT"));
		command.Parameters.Add(PgP("status", NpgsqlDbType.Varchar, status));
		command.Parameters.Add(new NpgsqlParameter("submitted_date", NpgsqlDbType.Timestamp) { Value = DateTime.SpecifyKind(submittedDate, DateTimeKind.Unspecified) });
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	/// <summary>Inserts an order on the caller's transaction and RETURNs its generated order_id (so a trade
	/// row with a valid FK can be seeded for the market-order commission test); null price => MARKET.</summary>
	private async Task<long> PgInsertOrderReturningIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int portfolioId, int securityId, string side, decimal qty, decimal? price, string status)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO orders (portfolio_id, security_id, side, qty, price, order_type, status) " +
			"VALUES (@portfolio_id, @security_id, @side, @qty, @price, @order_type, @status) RETURNING order_id", connection, transaction);
		command.Parameters.Add(PgP("portfolio_id", NpgsqlDbType.Integer, portfolioId));
		command.Parameters.Add(PgP("security_id", NpgsqlDbType.Integer, securityId));
		command.Parameters.Add(PgP("side", NpgsqlDbType.Varchar, side));
		command.Parameters.Add(PgP("qty", NpgsqlDbType.Numeric, qty));
		command.Parameters.Add(PgP("price", NpgsqlDbType.Numeric, price));
		command.Parameters.Add(PgP("order_type", NpgsqlDbType.Varchar, price is null ? "MARKET" : "LIMIT"));
		command.Parameters.Add(PgP("status", NpgsqlDbType.Varchar, status));
		return (long)await command.ExecuteScalarAsync(CancellationToken);
	}

	/// <summary>Inserts a trades row on the caller's transaction (for the market-order last-trade-price and
	/// existing-gross-notional reads); price stays decimal to preserve NUMERIC(18,4) scale.</summary>
	private async Task PgInsertTradeTxnAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long orderId, decimal qty, decimal price)
	{
		await using var command = new NpgsqlCommand(
			"INSERT INTO trades (order_id, qty, price, executed_date) " +
			"VALUES (@order_id, @qty, @price, now() at time zone 'utc')", connection, transaction);
		command.Parameters.Add(new NpgsqlParameter("order_id", NpgsqlDbType.Bigint) { Value = orderId });
		command.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = qty });
		command.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = price });
		await command.ExecuteNonQueryAsync(CancellationToken);
	}

	// --- Preserved PostgreSQL seed helpers (unchanged from the original Phase G) ---

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
	/// in <c>Database/004_SeedData.sql</c> - especially <c>max_order_price = 500.00</c> - so the guarded
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
