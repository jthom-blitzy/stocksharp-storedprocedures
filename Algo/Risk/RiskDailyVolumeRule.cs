namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking cumulative traded/ordered volume within a single day.
/// </summary>
/// <remarks>
/// This is the canonical C# home of the former SQL-only max_daily_volume limit
/// (dbo.usp_ValidatePreTradeRisk in Database/002_StoredProcedures.sql). In this
/// circuit-breaker context the rule keeps a per-message-stream running total that
/// resets when the message date (<c>message.LocalTime.Date</c>) rolls over and on
/// <see cref="Reset"/>; it trips when the running total plus the incoming order volume
/// meets or exceeds <see cref="Volume"/>. The per-order pre-trade gate
/// <see cref="PreTradeRiskService"/> enforces the SAME threshold
/// (<see cref="RiskLimitSet.MaxDailyVolume"/>) but computes today's volume from persisted
/// orders. A <see cref="Volume"/> of 0 means "no limit".
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.IntradayVolumeKey,
	Description = LocalizedStrings.IntradayVolumeKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskDailyVolumeRule : RiskRule
{
	private decimal _volume;
	private DateTime? _currentDay;
	private decimal _dailyTotal;

	/// <summary>
	/// Daily volume.
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.VolumeKey,
		Description = LocalizedStrings.VolumeDescKey,
		GroupName = LocalizedStrings.GeneralKey,
		Order = 0)]
	public decimal Volume
	{
		get => _volume;
		set
		{
			if (_volume == value)
				return;

			if (value < 0)
				throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.InvalidValue);

			_volume = value;
			UpdateTitle();
		}
	}

	/// <inheritdoc />
	protected override string GetTitle() => _volume.To<string>();

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		decimal orderVolume;

		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
				orderVolume = ((OrderRegisterMessage)message).Volume;
				break;

			case MessageTypes.OrderReplace:
				orderVolume = ((OrderReplaceMessage)message).Volume;
				break;

			default:
				return false;
		}

		if (Volume == 0)
			return false;

		var day = message.LocalTime.Date;

		if (_currentDay != day)
		{
			_currentDay = day;
			_dailyTotal = 0;
		}

		_dailyTotal += orderVolume;

		return _dailyTotal >= Volume;
	}

	/// <inheritdoc />
	public override void Reset()
	{
		base.Reset();

		_currentDay = null;
		_dailyTotal = 0;
	}

	/// <inheritdoc />
	public override void Save(SettingsStorage storage)
	{
		base.Save(storage);

		storage.SetValue(nameof(Volume), Volume);
	}

	/// <inheritdoc />
	public override void Load(SettingsStorage storage)
	{
		base.Load(storage);

		Volume = storage.GetValue<decimal>(nameof(Volume));
	}
}
