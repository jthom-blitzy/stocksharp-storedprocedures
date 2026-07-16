namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking daily traded volume.
/// </summary>
/// <remarks>
/// Canonical, single source of truth for the daily-traded-volume limit. This
/// check previously existed ONLY in SQL (usp_ValidatePreTradeRisk RULE 7,
/// "today's accepted/filled qty + new qty >= max_daily_volume") with no C#
/// counterpart; the risk consolidation promotes it to a first-class C# rule.
/// The rule owns only the threshold and the ">=" comparison; the running
/// "today's volume" aggregate is supplied by the caller - the
/// <see cref="PreTradeRiskService"/> gate reads it from SQL Server and adds the
/// prospective order's quantity before evaluating. A zero threshold means
/// "not enforced".
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.IntradayVolumeKey,
	Description = LocalizedStrings.RiskOrderVolumeKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskDailyVolumeRule : RiskRule
{
	private decimal _dailyVolume;

	/// <summary>
	/// Daily traded volume.
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.DailyKey,
		Description = LocalizedStrings.IntradayVolumeKey,
		GroupName = LocalizedStrings.GeneralKey,
		Order = 0)]
	public decimal DailyVolume
	{
		get => _dailyVolume;
		set
		{
			if (_dailyVolume == value)
				return;

			if (value < 0)
				throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.InvalidValue);

			_dailyVolume = value;
			UpdateTitle();
		}
	}

	/// <inheritdoc />
	protected override string GetTitle() => _dailyVolume.To<string>();

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		// Canonical daily-volume check ported from SQL usp_ValidatePreTradeRisk
		// RULE 7. The rule holds only the threshold + ">=" comparison against the
		// incoming order volume; the pre-trade gate combines it with today's
		// aggregated traded volume (read from SQL) to reproduce the SQL semantics
		// "today's qty + new qty >= max_daily_volume". ">=" preserved; 0 = not enforced.
		if (DailyVolume == 0)
			return false;

		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
			{
				var orderReg = (OrderRegisterMessage)message;
				return orderReg.Volume >= DailyVolume;
			}

			case MessageTypes.OrderReplace:
			{
				var orderReplace = (OrderReplaceMessage)message;
				return orderReplace.Volume > 0 && orderReplace.Volume >= DailyVolume;
			}

			default:
				return false;
		}
	}

	/// <inheritdoc />
	public override void Save(SettingsStorage storage)
	{
		base.Save(storage);

		storage.SetValue(nameof(DailyVolume), DailyVolume);
	}

	/// <inheritdoc />
	public override void Load(SettingsStorage storage)
	{
		base.Load(storage);

		DailyVolume = storage.GetValue<decimal>(nameof(DailyVolume));
	}
}
