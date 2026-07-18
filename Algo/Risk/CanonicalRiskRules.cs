namespace StockSharp.Algo.Risk;

/// <summary>
/// Canonical, single-source-of-truth definitions for the risk semantics that are genuinely
/// SHARED between the per-order pre-trade gate (<see cref="PreTradeRiskService"/>) and the
/// portfolio-wide circuit-breaker rules. Two things live here, and only here:
/// (1) the rolling-window ORDER-FREQUENCY evaluator, consumed by BOTH the gate and
/// <see cref="RiskOrderFreqRule"/> — this is what removes the former C# &lt;-&gt; SQL frequency
/// divergence, because the two sides can no longer disagree when they compute frequency from one
/// definition; and (2) the gate's numeric-ceiling semantics — the "enabled limit" predicate
/// (<see cref="IsCeilingEnabled"/>, where a NULL or zero limit means "not enforced") and the
/// "meets or exceeds" comparison (<see cref="MeetsOrExceeds"/>), consumed by the gate.
/// </summary>
/// <remarks>
/// This is deliberately NOT a registry of every threshold. The circuit-breaker price/quantity/
/// position/commission rules (for example <see cref="RiskOrderPriceRule"/>,
/// <see cref="RiskPositionSizeRule"/>, <see cref="RiskCommissionRule"/>) keep their OWN limit
/// values and evaluate their OWN subjects (e.g. the current streamed position, or realised
/// commission after the fill) and are preserved unchanged — different by design (AAP 0.6.2).
/// Likewise the gate reads its per-check thresholds from the applicable RiskLimits row at
/// evaluation time (in <see cref="PreTradeRiskService"/>); those values are NOT stored here.
/// Only the frequency evaluator and the ceiling-comparison convention are canonicalised in this
/// class.
///
/// Pure and stateless (no persistence, no I/O, no Npgsql), so it is trivially unit-testable.
/// The order-frequency evaluator implements a TRUE ROLLING WINDOW. It replaces the retired
/// fixed, non-overlapping bucket algorithm that <see cref="RiskOrderFreqRule"/> used to run: a
/// burst straddling a bucket boundary could dodge a fixed-bucket limit, which was strictly
/// LOOSER than the SQL rolling COUNT(*) and would have violated the threshold-strictness
/// invariant. The rolling window here is never less strict than that SQL original.
/// </remarks>
public static class CanonicalRiskRules
{
	/// <summary>
	/// Counts the events in <paramref name="times"/> whose timestamp falls within the trailing
	/// <paramref name="window"/> ending at <paramref name="now"/>, i.e. t &gt;= (now - window).
	/// The lower bound is INCLUSIVE and there is no upper bound, exactly matching the retired SQL
	/// predicate "submitted_date &gt;= DATEADD(SECOND, -window, SYSUTCDATETIME())". The inclusive
	/// lower bound guarantees the rolling window is never LESS strict than that SQL original.
	/// </summary>
	/// <remarks>
	/// The caller must pass <paramref name="times"/> and <paramref name="now"/> from the SAME
	/// timeline: the gate uses UTC database timestamps plus an injected UTC "now"; the circuit
	/// breaker uses message LocalTime values. The comparison is relative, so it is zone-agnostic
	/// as long as both arguments come from one clock.
	/// </remarks>
	/// <param name="times">The event timestamps to test. Must not be <see langword="null"/>.</param>
	/// <param name="now">The reference instant from which the trailing window is measured; the
	/// inclusive lower bound is <paramref name="now"/> - <paramref name="window"/>. This method
	/// applies NO upper bound, so it is not an exclusive upper reference — a timestamp at or after
	/// the lower bound is counted regardless of how it compares to <paramref name="now"/> (in
	/// practice callers pass event times that do not exceed <paramref name="now"/>).</param>
	/// <param name="window">The trailing window length; the inclusive lower bound is <paramref name="now"/> - <paramref name="window"/>.</param>
	/// <returns>The number of timestamps at or after the inclusive lower bound.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="times"/> is <see langword="null"/>.</exception>
	public static int CountWithinWindow(IEnumerable<DateTime> times, DateTime now, TimeSpan window)
	{
		if (times is null)
			throw new ArgumentNullException(nameof(times));

		// Inclusive trailing lower bound: t >= now - window (SQL parity, and inclusive => not looser).
		// This rolling comparison deliberately supersedes RiskOrderFreqRule's retired fixed,
		// non-overlapping bucket algorithm, which was strictly looser at a bucket boundary and
		// therefore violated the threshold-strictness invariant (AAP 0.6.1).
		var cutoff = now - window;
		var count = 0;

		foreach (var t in times)
		{
			if (t >= cutoff)
				count++;
		}

		return count;
	}

	/// <summary>
	/// Rolling-window order-frequency breach test for the PRE-TRADE GATE convention, where the
	/// prospective order is NOT part of <paramref name="recentEventTimes"/> (the gate counts the
	/// existing rows in the orders table and then accounts for the order being validated). Returns
	/// <see langword="true"/> when CountWithinWindow(...) + 1 &gt;= <paramref name="maxCount"/> —
	/// the exact SQL "@recentOrderCount + 1 &gt;= @max_order_freq_count". The "&gt;=" meets-or-exceeds
	/// semantics are preserved exactly.
	/// </summary>
	/// <param name="recentEventTimes">Timestamps of orders already recorded, excluding the prospective one.</param>
	/// <param name="now">The trailing window's end instant, from the same timeline as <paramref name="recentEventTimes"/>.</param>
	/// <param name="window">The trailing window length.</param>
	/// <param name="maxCount">The order-frequency ceiling; a breach is a "meets or exceeds" of this count.</param>
	/// <returns><see langword="true"/> if the prospective order would meet or exceed the frequency ceiling.</returns>
	public static bool IsOrderFrequencyBreached(IEnumerable<DateTime> recentEventTimes, DateTime now, TimeSpan window, int maxCount)
		=> CountWithinWindow(recentEventTimes, now, window) + 1 >= maxCount;

	/// <summary>
	/// Rolling-window order-frequency breach test for the PRE-TRADE GATE convention, operating on a
	/// PRE-COMPUTED count of orders already inside the trailing window. This is the overload the gate
	/// uses so the count can stay BOUNDED in the database — a single "SELECT COUNT(*) ... WHERE
	/// submitted_date &gt;= (now - window)" — instead of materialising every in-window timestamp into
	/// memory. Returns <see langword="true"/> when <paramref name="recentOrderCount"/> + 1 &gt;=
	/// <paramref name="maxCount"/>, the exact SQL "@recentOrderCount + 1 &gt;= @max_order_freq_count".
	/// The decision is identical to the <see cref="IEnumerable{T}"/> overload for the same window
	/// (the SQL "&gt;= now - window" filter is the inclusive lower bound implemented by
	/// <see cref="CountWithinWindow"/>), so the two forms can never disagree; this one just avoids
	/// the unbounded fetch. The "+1" accounts for the prospective (not-yet-persisted) order and the
	/// "&gt;=" meets-or-exceeds semantics are preserved exactly.
	/// </summary>
	/// <param name="recentOrderCount">Count of orders already recorded within the trailing window (excludes the prospective order).</param>
	/// <param name="maxCount">The order-frequency ceiling; a breach is a "meets or exceeds" of this count.</param>
	/// <returns><see langword="true"/> if the prospective order would meet or exceed the frequency ceiling.</returns>
	public static bool IsOrderFrequencyBreached(long recentOrderCount, int maxCount)
		=> recentOrderCount + 1 >= maxCount;

	/// <summary>
	/// Rolling-window order-frequency breach test for the CIRCUIT-BREAKER RULE convention, where
	/// the current event has ALREADY been appended to <paramref name="times"/>. Returns
	/// <see langword="true"/> when CountWithinWindow(...) &gt;= <paramref name="maxCount"/>. This is
	/// IDENTICAL in outcome to <see cref="IsOrderFrequencyBreached(IEnumerable{DateTime}, DateTime, TimeSpan, int)"/>
	/// for the equivalent situation, because counting the past events and adding one equals counting
	/// the past events plus the appended current one. The "&gt;=" semantics are preserved exactly.
	/// </summary>
	/// <param name="times">Timestamps of events including the current one already appended.</param>
	/// <param name="now">The trailing window's end instant, from the same timeline as <paramref name="times"/>.</param>
	/// <param name="window">The trailing window length.</param>
	/// <param name="maxCount">The order-frequency ceiling; a breach is a "meets or exceeds" of this count.</param>
	/// <returns><see langword="true"/> if the events in the window meet or exceed the frequency ceiling.</returns>
	public static bool IsOrderFrequencyBreachedIncludingCurrent(IEnumerable<DateTime> times, DateTime now, TimeSpan window, int maxCount)
		=> CountWithinWindow(times, now, window) >= maxCount;

	/// <summary>
	/// Canonical "is this optional numeric ceiling ENFORCED?" predicate for the pre-trade gate.
	/// A ceiling is enforced only when it is present (HasValue) AND strictly positive; a NULL or
	/// ZERO limit means "not enforced / unlimited". This is the AAP 0.6.4 convention — the same one
	/// the schema documents for its max_* columns ("NULL/0 = not enforced") and the same one the
	/// C# circuit-breaker rules already use (0 = no limit), so treating 0 as unlimited here unifies
	/// the two sides rather than diverging from them.
	/// </summary>
	/// <remarks>
	/// NOTE the deliberate change from the original SQL proc, which guarded only on IS NOT NULL and
	/// so treated a limit of 0 as an always-reject ceiling. The frozen AAP (0.6.4 and compliance
	/// item 5) mandates that BOTH null and 0 mean unlimited; this predicate implements that
	/// canonical convention. The seeded RiskLimits values are all strictly positive, so this changes
	/// no observable demo behaviour — it only defines the meaning of an unset/zero limit.
	/// </remarks>
	/// <param name="ceiling">The optional ceiling to test.</param>
	/// <returns><see langword="true"/> if the ceiling is present and strictly positive (enforced).</returns>
	public static bool IsCeilingEnabled(decimal? ceiling)
		=> ceiling.HasValue && ceiling.Value > 0m;

	/// <summary>
	/// Canonical "is the order-frequency limit ENFORCED?" predicate. Both the count and the window
	/// must be present and strictly positive; a NULL or ZERO count/window means the frequency check
	/// is not enforced (AAP 0.6.4, the same null-or-zero-is-unlimited convention as
	/// <see cref="IsCeilingEnabled"/>).
	/// </summary>
	/// <param name="maxCount">The optional order-frequency ceiling (orders per window).</param>
	/// <param name="windowSeconds">The optional trailing-window length in seconds.</param>
	/// <returns><see langword="true"/> if both the count and the window are present and strictly positive.</returns>
	public static bool IsFrequencyEnabled(int? maxCount, int? windowSeconds)
		=> maxCount.HasValue && maxCount.Value > 0 && windowSeconds.HasValue && windowSeconds.Value > 0;

	/// <summary>
	/// Shared "meets or exceeds an optional ceiling" comparison used by the pre-trade gate for its
	/// numeric ceilings. Returns <see langword="true"/> (a breach) only when the ceiling is ENFORCED
	/// (<see cref="IsCeilingEnabled"/>: present and strictly positive) AND <paramref name="value"/>
	/// &gt;= ceiling. A NULL or ZERO ceiling means "not enforced" and never breaches (AAP 0.6.4).
	/// </summary>
	/// <remarks>
	/// This is the canonical GATE convention: NULL or 0 = not enforced, positive = "&gt;=" rejects.
	/// It composes <see cref="IsCeilingEnabled"/> so the null-or-zero-is-unlimited rule is defined in
	/// exactly one place. The circuit-breaker rules (RiskCommissionRule, RiskPositionSizeRule, ...)
	/// evaluate a DIFFERENT subject with their own limit values (different by design, AAP 0.6.2);
	/// they are preserved as-is and are not routed through this method. Uses <see cref="decimal"/>
	/// so the comparison can never silently loosen.
	/// </remarks>
	/// <param name="value">The observed value (money / quantity / notional) to test.</param>
	/// <param name="ceiling">The optional ceiling; <see langword="null"/> or 0 means the limit is not enforced.</param>
	/// <returns><see langword="true"/> if the ceiling is enforced and <paramref name="value"/> meets or exceeds it.</returns>
	public static bool MeetsOrExceeds(decimal value, decimal? ceiling)
		=> IsCeilingEnabled(ceiling) && value >= ceiling.Value;

	/// <summary>
	/// The scale (number of decimal places) of the schema's NUMERIC(18,4) money/quantity/price columns.
	/// </summary>
	public const int NumericScale = 4;

	/// <summary>
	/// Normalises a money/quantity/price value to the schema's NUMERIC(18,4) <see cref="NumericScale"/>,
	/// the SINGLE canonical rounding used by both the pre-trade gate (<see cref="PreTradeRiskService"/>) and
	/// the data-access gateway. Quantising once, up front, guarantees the value the gate DECIDES on is the
	/// exact value PostgreSQL PERSISTS into the NUMERIC(18,4) column, so a &gt;4-dp input can never be
	/// accepted on its full-precision value yet stored at (or above) a ceiling it should have met, and the
	/// gateway never binds a raw value that diverges from the validated one (decision == persistence).
	/// Away-from-zero rounding is never LESS strict than truncation (hard strictness NFR, AAP 0.6.4), and it
	/// is idempotent - re-quantising an already-4-dp value is a no-op - so applying it in several places is
	/// safe. Uses <see cref="decimal"/> throughout; never <c>double</c>/<c>float</c>.
	/// </summary>
	/// <param name="value">The raw value to normalise.</param>
	/// <returns><paramref name="value"/> rounded to <see cref="NumericScale"/> decimals, away from zero.</returns>
	public static decimal QuantizeToScale(decimal value)
		=> Math.Round(value, NumericScale, MidpointRounding.AwayFromZero);
}
