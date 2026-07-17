namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking order volume.
/// </summary>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.OrderVolume2Key,
	Description = LocalizedStrings.RiskOrderVolumeKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskOrderVolumeRule : RiskRule
{
	private decimal _volume;

	/// <summary>
	/// Order volume.
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.VolumeKey,
		Description = LocalizedStrings.OrderVolumeKey,
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
		// P6-F7 reconciliation (AAP 0.6.6): a zero (or NULL-sourced) threshold means the
		// control is NOT ENFORCED - the shared "NULL/0 = not enforced" convention the SQL
		// pre-trade gate and the sibling canonical rules (RiskOrderValueRule,
		// RiskDailyVolumeRule, RiskPositionSizeRule) already honour. Without this guard a
		// Volume of 0 tripped on every positive order (orderReg.Volume >= 0 is always true),
		// silently activating the circuit-breaker action. The ">=" ("meets or exceeds")
		// boundary is preserved unchanged for every genuinely configured (non-zero) limit.
		if (Volume == 0)
			return false;

		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
			{
				var orderReg = (OrderRegisterMessage)message;
				return orderReg.Volume >= Volume;
			}

			case MessageTypes.OrderReplace:
			{
				var orderReplace = (OrderReplaceMessage)message;
				return orderReplace.Volume > 0 && orderReplace.Volume >= Volume;
			}

			default:
				return false;
		}
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
