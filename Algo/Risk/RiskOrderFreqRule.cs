namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking orders placing frequency.
/// </summary>
/// <remarks>
/// This rule now uses the same rolling <c>COUNT(*)</c>-over-"now minus <see cref="Interval"/>"
/// semantics as the SQL pre-trade gate (<c>dbo.usp_ValidatePreTradeRisk</c> in
/// Database/002_StoredProcedures.sql, ported to <see cref="PreTradeRiskService"/>): each incoming
/// order counts the prior orders that still fall inside the trailing <see cref="Interval"/> window
/// and rejects once that count plus the current order reaches <see cref="Count"/>. The per-order
/// pre-trade gate and this circuit-breaker rule therefore no longer diverge for the same
/// configuration and burst pattern.
/// This replaced the previous fixed-window bucketing, which split time into non-overlapping
/// windows and could let a burst straddling a window boundary dodge the limit. Adopting the
/// rolling count is a deliberate, at-least-as-strict tightening: near a window boundary it is
/// never looser than the old behaviour and rejects strictly more borderline bursts.
/// <para>
/// Event-time / late-event policy: the SQL gate assumes monotonically non-decreasing
/// <c>submitted_date</c> values (rows are stamped with <c>GETUTCDATE()</c> as they are inserted).
/// A live message stream can, however, deliver out-of-order (late) events whose correct trailing
/// window would need state that a newer event has already evicted. To honour the "never less
/// strict than a correct rolling count" reconciliation requirement, this rule tracks a high
/// watermark (the newest <see cref="Message.LocalTime"/> observed) and treats any strictly-earlier
/// event as a breach, rather than silently under-counting it. In-order events (timestamp greater
/// than or equal to the watermark) follow the exact rolling-count logic above.
/// </para>
/// <para>
/// Robustness: a non-positive <see cref="Interval"/> deactivates the rule (no trailing window is
/// configured); event timestamps are held in a bounded FIFO queue evicted from the front (amortised
/// O(1) rather than the previous O(n) list scan); the trailing-window subtraction is guarded
/// against <see cref="DateTime"/> underflow; and all state access is synchronized so the rule is
/// safe under concurrent message delivery.
/// </para>
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.OrderFreqKey,
	Description = LocalizedStrings.RiskOrderFreqKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskOrderFreqRule : RiskRule
{
	// Bounded FIFO of the timestamps of orders still inside the trailing window. On the in-order
	// path the queue stays monotonically non-decreasing, so eviction from the front is amortised
	// O(1). Guarded by <see cref="_sync"/> for safe concurrent message delivery.
	private readonly Queue<DateTime> _times = new();
	private readonly object _sync = new();

	// Highest LocalTime observed so far; used to detect out-of-order (late) events.
	private DateTime _watermark;

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
		{
			_times.Clear();
			_watermark = default;
		}
	}

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
			case MessageTypes.OrderReplace:
			{
				var interval = Interval;

				// A non-positive interval means no trailing window is configured, so the rule is
				// inactive and never trips. This also yields deterministic same-timestamp behaviour
				// (a zero window can no longer accidentally reject).
				if (interval <= TimeSpan.Zero)
					return false;

				var time = message.LocalTime;

				// Ignore messages without a usable timestamp.
				if (time == default)
					return false;

				lock (_sync)
				{
					// Late-event / watermark policy: if this event is older than the newest event
					// already processed, the bounded state needed to reconstruct its true trailing
					// window may already have been evicted by a newer event. Rather than silently
					// under-counting (which would be less strict than a correct rolling count), treat
					// any out-of-order event conservatively as a breach.
					if (time < _watermark)
						return true;

					_watermark = time;

					// Guard against DateTime underflow when the window extends before DateTime.MinValue.
					var cutoff = interval < time - DateTime.MinValue
						? time - interval
						: DateTime.MinValue;

					// Evict timestamps strictly older than the rolling window. The SQL gate keeps rows
					// where submitted_date >= now - window, so a timestamp exactly at the boundary
					// (== cutoff) is retained. The queue is monotonic on this in-order path, so this
					// dequeues from the front in amortised O(1).
					while (_times.Count > 0 && _times.Peek() < cutoff)
						_times.Dequeue();

					// Prior orders still inside the trailing window (the current order is not yet counted).
					var recentCount = _times.Count;

					// Record the current order so subsequent messages observe it within their windows.
					_times.Enqueue(time);

					// Canonical rolling-count reject: the "+ 1" is the current order, mirroring the SQL
					// gate's (recentOrderCount + 1) >= max_order_freq_count predicate.
					return recentCount + 1 >= Count;
				}
			}
		}

		return false;
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
