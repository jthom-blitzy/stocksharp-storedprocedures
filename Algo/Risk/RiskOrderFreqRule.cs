namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking orders placing frequency.
/// </summary>
/// <remarks>
/// Canonical, single source of truth for the order-frequency limit. Both risk
/// enforcement patterns share THIS definition: the portfolio-wide
/// <see cref="RiskManager"/> circuit breaker (fed the live message stream) and
/// the per-order <see cref="PreTradeRiskService"/> pre-trade gate (fed a
/// prospective order together with the rolling order count it reads from SQL
/// Server). The shared decision lives in <see cref="IsFrequencyExceeded"/>:
/// given the number of orders already inside the rolling window, admitting one
/// more meets or exceeds <see cref="Count"/>. This class owns the streaming
/// window state; the gate reuses the same comparison against a SQL
/// <c>COUNT(*)</c>, so the two paths cannot diverge in the frequency decision.
///
/// The algorithm is a TRUE ROLLING window: an order counts while its timestamp
/// lies within <c>[now - Interval, now]</c>. The lower bound is INCLUSIVE, to
/// match the SQL predicate <c>submitted_date &gt;= now - window</c> exactly - an
/// event landing precisely on <c>now - Interval</c> is still counted. This
/// replaces the earlier fixed, non-overlapping window, which admitted a burst
/// straddling a bucket boundary; the rolling window is strictly stricter near a
/// boundary, so under the stricter-wins hard constraint (AAP 0.6.2/0.6.3) it is
/// the canonical algorithm.
///
/// Implementation notes that preserve the stricter-wins guarantee:
/// <list type="bullet">
/// <item>All window state is mutated under a lock, because the risk manager can
/// be driven concurrently from the inbound and outbound adapter paths.</item>
/// <item>Retained timestamps are bounded to <see cref="Count"/> (the fewest
/// needed to decide the "meets or exceeds" comparison), so memory cannot grow
/// with the order rate; the events dropped are always the oldest, which would
/// leave the window first, so the decision never becomes less strict.</item>
/// <item>A zero (or negative) <see cref="Interval"/> means "not enforced",
/// consistent with the NULL/0 = disabled convention the SQL gate applies to a
/// zero <c>max_order_freq_window_sec</c>.</item>
/// </list>
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.OrderFreqKey,
	Description = LocalizedStrings.RiskOrderFreqKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskOrderFreqRule : RiskRule
{
	// Rolling-window state: timestamps of the most recent orders that fall
	// within the [now - Interval, now] window (see ProcessMessage). Guarded by
	// _sync and bounded to Count entries so the state cannot grow unbounded.
	private readonly Queue<DateTime> _recent = new();
	private readonly object _sync = new();

	/// <summary>
	/// Canonical rolling-window trip decision shared by both enforcement
	/// patterns. Given the number of orders already inside the rolling window
	/// <c>[now - Interval, now]</c> (its lower bound inclusive), decides whether
	/// admitting one more order meets or exceeds <paramref name="count"/>. The
	/// <see cref="RiskManager"/> circuit breaker supplies a count derived from
	/// its streaming window state; the <see cref="PreTradeRiskService"/> gate
	/// supplies a SQL <c>COUNT(*)</c> over the same window - both route through
	/// this single comparison so the frequency rule is defined exactly once.
	/// </summary>
	/// <param name="priorCountInWindow">Number of orders already inside the rolling window, excluding the incoming order.</param>
	/// <param name="count">Configured maximum order count for the window.</param>
	/// <returns><see langword="true"/> when the incoming order must be rejected.</returns>
	public static bool IsFrequencyExceeded(int priorCountInWindow, int count)
		=> priorCountInWindow + 1 >= count;

	/// <inheritdoc />
	protected override string GetTitle() => Count + " -> " + Interval;

	private int _count = 10;

	/// <summary>
	/// Order count.
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.CountKey,
		Description = LocalizedStrings.OrdersCountKey,
		GroupName = LocalizedStrings.GeneralKey,
		Order = 0)]
	public int Count
	{
		get => _count;
		set
		{
			if (_count == value)
				return;

			if (value < 1)
				throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.InvalidValue);

			_count = value;
			UpdateTitle();
		}
	}

	private TimeSpan _interval;


	/// <summary>
	/// Interval, during which orders quantity will be monitored.
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.IntervalKey,
		Description = LocalizedStrings.RiskIntervalDescKey,
		GroupName = LocalizedStrings.GeneralKey,
		Order = 1)]
	public TimeSpan Interval
	{
		get => _interval;
		set
		{
			if (_interval == value)
				return;

			if (value < TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.InvalidValue);

			_interval = value;
			UpdateTitle();
		}
	}

	/// <inheritdoc />
	public override void Reset()
	{
		base.Reset();

		lock (_sync)
			_recent.Clear();
	}

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
			case MessageTypes.OrderReplace:
			{
				var time = message.LocalTime;

				if (time == default)
					return false;

				// A zero/negative interval is "not enforced" - the same NULL/0 =
				// disabled convention the SQL gate applies to a zero window. Without
				// this guard a Count=1 rule would trip on every timestamped order.
				if (Interval <= TimeSpan.Zero)
					return false;

				// TRUE ROLLING window (AAP 0.6.3), matching SQL usp_ValidatePreTradeRisk
				// RULE 4's COUNT of orders whose submitted_date is within "now - window".
				// The lower bound is INCLUSIVE (evict strictly-older-than, never
				// evict-at-boundary) so an event on now - Interval is still counted,
				// exactly like the SQL ">=" predicate. Rolling is strictly stricter near
				// a boundary, so under the stricter-wins hard constraint it is canonical.
				// All state is mutated under _sync (the manager may run concurrently),
				// and the queue is bounded to Count entries (the fewest needed to decide
				// the comparison) so it cannot grow with the order rate.
				lock (_sync)
				{
					var lowerBound = time - Interval;

					while (_recent.Count > 0 && _recent.Peek() < lowerBound)
						_recent.Dequeue();

					// orders already in the window, excluding the incoming one
					var priorCountInWindow = _recent.Count;

					// keep the incoming/triggering order counted (matches SQL "+ 1")
					_recent.Enqueue(time);

					// bound retained state: dropping the oldest never loosens the
					// decision (the oldest leaves the window first)
					while (_recent.Count > Count)
						_recent.Dequeue();

					// canonical, shared ">=" comparison (never ">")
					return IsFrequencyExceeded(priorCountInWindow, Count);
				}
			}

			default:
				return false;
		}
	}

	/// <inheritdoc />
	public override void Save(SettingsStorage storage)
	{
		base.Save(storage);

		storage.SetValue(nameof(Count), Count);
		storage.SetValue(nameof(Interval), Interval);
	}

	/// <inheritdoc />
	public override void Load(SettingsStorage storage)
	{
		base.Load(storage);

		Count = storage.GetValue<int>(nameof(Count));
		Interval = storage.GetValue<TimeSpan>(nameof(Interval));
	}
}
