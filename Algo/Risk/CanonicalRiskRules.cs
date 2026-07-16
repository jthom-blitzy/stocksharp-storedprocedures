namespace StockSharp.Algo.Risk;

/// <summary>
/// Canonical, single-source-of-truth risk-rule definitions shared by the per-order pre-trade
/// gate (<see cref="PreTradeRiskService"/>) and the portfolio-wide circuit-breaker rules
/// (for example <see cref="RiskOrderFreqRule"/>). Centralising the shared logic here removes the
/// former C# &lt;-&gt; SQL divergence: both sides now compute order frequency, and apply the shared
/// "meets or exceeds" comparison, from ONE definition, so they can no longer disagree.
/// </summary>
/// <remarks>
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
	/// <param name="now">The trailing window's end instant (exclusive upper reference point).</param>
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
	/// Rolling-window order-frequency breach test for the CIRCUIT-BREAKER RULE convention, where
	/// the current event has ALREADY been appended to <paramref name="times"/>. Returns
	/// <see langword="true"/> when CountWithinWindow(...) &gt;= <paramref name="maxCount"/>. This is
	/// IDENTICAL in outcome to <see cref="IsOrderFrequencyBreached"/> for the equivalent situation,
	/// because counting the past events and adding one equals counting the past events plus the
	/// appended current one. The "&gt;=" semantics are preserved exactly.
	/// </summary>
	/// <param name="times">Timestamps of events including the current one already appended.</param>
	/// <param name="now">The trailing window's end instant, from the same timeline as <paramref name="times"/>.</param>
	/// <param name="window">The trailing window length.</param>
	/// <param name="maxCount">The order-frequency ceiling; a breach is a "meets or exceeds" of this count.</param>
	/// <returns><see langword="true"/> if the events in the window meet or exceed the frequency ceiling.</returns>
	public static bool IsOrderFrequencyBreachedIncludingCurrent(IEnumerable<DateTime> times, DateTime now, TimeSpan window, int maxCount)
		=> CountWithinWindow(times, now, window) >= maxCount;

	/// <summary>
	/// Shared "meets or exceeds an optional ceiling" comparison used by the pre-trade gate for its
	/// numeric ceilings. Returns <see langword="true"/> (a breach) only when
	/// <paramref name="ceiling"/> is set (HasValue) AND <paramref name="value"/> &gt;= ceiling.
	/// A null ceiling means "not enforced", matching the SQL "IS NOT NULL" guard.
	/// </summary>
	/// <remarks>
	/// This is the GATE/SQL convention: NULL = not enforced. The circuit-breaker rules
	/// (RiskCommissionRule, RiskPositionSizeRule, ...) intentionally use a DIFFERENT convention
	/// (a value of 0 = no limit) on a different subject and are preserved as-is (different by
	/// design, AAP 0.6.2). Uses <see cref="decimal"/> so the comparison can never silently loosen.
	/// </remarks>
	/// <param name="value">The observed value (money / quantity / notional) to test.</param>
	/// <param name="ceiling">The optional ceiling; <see langword="null"/> means the limit is not enforced.</param>
	/// <returns><see langword="true"/> if the ceiling is enforced and <paramref name="value"/> meets or exceeds it.</returns>
	public static bool MeetsOrExceeds(decimal value, decimal? ceiling)
		=> ceiling.HasValue && value >= ceiling.Value;
}
