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
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.OrderFreqKey,
	Description = LocalizedStrings.RiskOrderFreqKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskOrderFreqRule : RiskRule
{
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

				var cutoff = time - Interval;

				// Evict timestamps strictly older than the rolling window. The SQL gate keeps rows
				// where submitted_date >= now - window, so a timestamp exactly at the boundary is kept.
				_times.RemoveAll(t => t < cutoff);

				// Prior orders still inside the trailing window (the current order is not yet counted).
				var recentCount = _times.Count;

				// Record the current order so subsequent messages observe it within their windows.
				_times.Add(time);

				// Canonical rolling-count reject: the "+ 1" is the current order, mirroring the SQL
				// gate's (recentOrderCount + 1) >= max_order_freq_count predicate.
				return recentCount + 1 >= Count;
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
