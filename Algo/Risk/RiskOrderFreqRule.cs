namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking orders placing frequency.
/// </summary>
/// <remarks>
/// Canonical, single source of truth for the order-frequency limit. Both risk
/// enforcement patterns consume THIS definition: the portfolio-wide
/// <see cref="RiskManager"/> circuit breaker (fed the live message stream) and
/// the per-order <see cref="PreTradeRiskService"/> pre-trade gate (fed a
/// prospective order together with the rolling order count it reads from SQL
/// Server). The algorithm is a true rolling window: an order counts while its
/// timestamp lies within [now - Interval, now], and the incoming order is
/// rejected when the rolling count (including itself) meets or exceeds Count.
/// This replaces the earlier fixed, non-overlapping window, which admitted a
/// burst straddling a bucket boundary; the rolling window is strictly stricter
/// near a boundary, so under the stricter-wins reconciliation rule it is the
/// canonical algorithm.
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.OrderFreqKey,
	Description = LocalizedStrings.RiskOrderFreqKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskOrderFreqRule : RiskRule
{
	// Rolling-window state: timestamps of the orders that currently fall within
	// the [now - Interval, now] window (see ProcessMessage for the algorithm).
	private readonly List<DateTime> _recent = [];

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

				// RECONCILIATION (AAP 0.6.3): the previous algorithm bucketed time into
				// fixed, non-overlapping windows, so a burst straddling a bucket boundary
				// could dodge the limit. This is now a TRUE ROLLING window that matches the
				// SQL usp_ValidatePreTradeRisk RULE 4 (COUNT of orders within "now - window").
				// Rolling is strictly stricter near a boundary, so under the stricter-wins
				// hard constraint it is the canonical algorithm. This class is the single
				// source of truth consumed by both the RiskManager circuit breaker and the
				// PreTradeRiskService gate. The ">=" reject boundary is preserved (never ">").
				_recent.RemoveAll(t => t <= time - Interval);
				_recent.Add(time);

				return _recent.Count >= Count;
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
