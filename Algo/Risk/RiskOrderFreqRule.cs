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
/// Trusted-clock time source (review finding CR-28, CWE-20/CWE-367): each order is time-stamped with
/// the trusted processing clock (<see cref="UtcNow"/>, defaulting to <see cref="DateTime.UtcNow"/>),
/// mirroring the SQL gate stamping <c>submitted_date</c> with the server clock
/// (<c>SYSUTCDATETIME()</c>). The caller-supplied <see cref="Message.LocalTime"/> is deliberately
/// ignored, so a skewed, stale, or default timestamp from an untrusted producer can no longer advance
/// or dodge the trailing window. The trusted clock is monotonically non-decreasing in practice; should
/// it ever step backward, this rule tracks a high watermark (the newest observed instant) and treats
/// the backward step conservatively as a breach (fail closed) rather than silently under-counting -
/// honouring the "never less strict than a correct rolling count" reconciliation requirement.
/// </para>
/// <para>
/// Robustness: a non-positive <see cref="Interval"/> deactivates the rule (no trailing window is
/// configured); event timestamps are held in a bounded FIFO queue evicted from the front (amortised
/// O(1) rather than the previous O(n) list scan) and additionally capped to at most
/// (<see cref="Count"/> - 1) retained entries so a sustained burst cannot grow the queue without
/// bound once the rule is saturated (review finding CR-6); the trailing-window subtraction is guarded
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
	// Bounded FIFO of the trusted-clock timestamps of orders still inside the trailing window. The
	// trusted clock is monotonically non-decreasing, so the queue stays ordered and eviction from the
	// front is amortised O(1); it is additionally capped to at most (Count - 1) entries (see CR-6).
	// Guarded by <see cref="_sync"/> for safe concurrent message delivery.
	private readonly Queue<DateTime> _times = new();
	private readonly object _sync = new();

	// Highest processing-clock time observed so far; used to detect a backward clock step.
	private DateTime _watermark;

	private Func<DateTime> _utcNow = static () => DateTime.UtcNow;

	/// <summary>
	/// The trusted UTC processing clock used to time-stamp each incoming order. Defaults to
	/// <see cref="DateTime.UtcNow"/> - the server/processing clock, mirroring the SQL gate's use of
	/// <c>SYSUTCDATETIME()</c> - so a caller-supplied, stale, skewed, or default
	/// <see cref="Message.LocalTime"/> can no longer advance or dodge the trailing frequency window
	/// (review finding CR-28). Exposed only as an injectable test seam for deterministic timing; it is
	/// never persisted (see <see cref="Save"/>/<see cref="Load"/>) and is hidden from the UI. Never null.
	/// </summary>
	[Browsable(false)]
	public Func<DateTime> UtcNow
	{
		get => _utcNow;
		set => _utcNow = value ?? throw new ArgumentNullException(nameof(value));
	}

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

				// Time-stamp the event with the TRUSTED processing clock instead of the caller-supplied
				// message.LocalTime (review finding CR-28, CWE-20/CWE-367): the SQL gate stamps
				// submitted_date with the server clock (SYSUTCDATETIME), so a skewed, stale, or default
				// (DateTime.MinValue) LocalTime supplied by an untrusted producer can no longer advance
				// or dodge the trailing window here. The clock is injectable purely for deterministic
				// tests and defaults to DateTime.UtcNow.
				var time = _utcNow();

				lock (_sync)
				{
					// Bounded-skew fail-closed policy: if the trusted clock steps backward relative to
					// the newest event already processed, the bounded state needed to reconstruct the
					// true trailing window for this earlier instant may already have been evicted. Rather
					// than silently under-counting (which would be less strict than a correct rolling
					// count), treat any backward step conservatively as a breach.
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

					// Capacity bound (review finding CR-6, DoS/saturation safety): the rolling-count
					// decision only ever needs to know whether at least (Count - 1) prior orders fall
					// inside the window. Retaining more than the latest (Count - 1) timestamps can never
					// change any future decision - on the monotonic in-order path older timestamps always
					// leave the window no later than newer ones - so evict from the front until at most
					// (Count - 1) remain. This caps memory at O(Count) regardless of traffic once the rule
					// is saturated, and is never less strict: whenever the window truly holds >= (Count - 1)
					// orders the newest (Count - 1) are all still in-window, so recentCount still reaches
					// (Count - 1) and the "+ 1" current order trips the limit.
					while (_times.Count > Count - 1)
						_times.Dequeue();

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
