namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking orders placing frequency.
/// </summary>
/// <remarks>
/// Order-frequency is now evaluated by the single canonical rolling-window evaluator in
/// <see cref="CanonicalRiskRules"/>, which is also used by the per-order gate
/// <see cref="PreTradeRiskService"/>. The SHARED surface is precisely: the rolling-window count
/// definition and the "&gt;= <see cref="Count"/>" meets-or-exceeds threshold, plus the
/// zero-window-is-disabled convention (a non-positive <see cref="Interval"/> is not enforced,
/// mirroring <see cref="CanonicalRiskRules.IsFrequencyEnabled"/>). Because both consume that one
/// definition, the circuit breaker and the gate compute the SAME in-window verdict for the SAME
/// observed events - they cannot disagree on the frequency ARITHMETIC. They still differ, by
/// design, both in the STATE they feed that arithmetic and in what they DO with the verdict: this
/// rule counts an in-memory buffer of message-stream timestamps that it RESETS when it trips and
/// then takes a portfolio-wide circuit-breaker action, whereas the gate counts recent Orders rows
/// read from the database at evaluation time (every recent row, including rejected orders) and
/// rejects the single offending order. Same arithmetic, different data source, state lifecycle, and
/// reaction - so on the same real order flow the two can still reach different outcomes (see the
/// divergence note on <see cref="RiskManager"/>).
/// The previous fixed, non-overlapping bucket algorithm was retired: a burst straddling a bucket
/// boundary could dodge the limit (looser than the SQL rolling COUNT(*)), which would violate the
/// threshold-strictness invariant. This rule keeps its circuit-breaker lifecycle - when the rolling
/// count meets or exceeds <see cref="Count"/> it trips (takes its configured action) and resets its
/// in-window buffer.
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.OrderFreqKey,
	Description = LocalizedStrings.RiskOrderFreqKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskOrderFreqRule : RiskRule
{
	// Rolling-window state: timestamps (message.LocalTime) of recent OrderRegister/OrderReplace
	// messages. Replaces the old fixed-bucket _endTime/_current pair. The canonical evaluator in
	// CanonicalRiskRules counts the entries falling in the trailing Interval (AAP 0.6.1).
	private readonly List<DateTime> _times = [];

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

		_times.Clear();
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

				// M12: a ZERO (or non-positive) Interval means the frequency window is DISABLED - the SAME
				// "window must be > 0 to be enforced" convention the gate applies (CanonicalRiskRules
				// .IsFrequencyEnabled requires a positive window). With a zero window the trailing range
				// [time, time] would contain only the just-appended current order and trip immediately at
				// Count = 1, which the retired fixed-bucket baseline never did and the gate does not do.
				// Short-circuit so a disabled window neither trips nor accumulates state.
				if (Interval <= TimeSpan.Zero)
					return false;

				// Canonical rolling window (AAP 0.6.1): append this order's time, then ask the
				// SINGLE shared evaluator - also used by PreTradeRiskService - whether the number
				// of orders within the trailing Interval meets or exceeds Count. "IncludingCurrent"
				// because we have already appended the current order (the gate instead counts DB
				// rows and adds +1; both conventions yield identical answers).
				_times.Add(time);

				if (CanonicalRiskRules.IsOrderFrequencyBreachedIncludingCurrent(_times, time, Interval, Count))
				{
					// Circuit-breaker trip-then-reset lifecycle: mirrors the old `_endTime = null`
					// reset. Clearing after a trip stops the action from re-firing on every
					// subsequent order and keeps parity with the existing OrderFreq test. This is
					// still strictly stricter than the retired fixed-bucket algorithm, which could
					// let a boundary-straddling burst slip through.
					_times.Clear();
					return true;
				}

				// Not breached: drop entries that have aged out of the trailing window so the
				// buffer stays bounded (they cannot affect any future window). The evaluator above
				// already ignores them, so this is pure memory hygiene and does not change results.
				_times.RemoveAll(t => t < time - Interval);

				return false;
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
