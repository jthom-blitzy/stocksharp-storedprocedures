namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking order volume.
/// </summary>
/// <remarks>
/// The quantity ceiling enforced here is the canonical order-quantity limit - the single source
/// of truth shared between this portfolio-wide circuit-breaker rule and the per-order pre-trade
/// gate (<see cref="PreTradeRiskService"/>). The shared value originates from
/// <see cref="RiskLimitSet.MaxOrderQty"/> and is wired into this rule via the
/// <see cref="RiskManager"/> canonical seed helper, so the threshold is defined exactly once.
/// Both enforcement patterns apply identical reject-when-<c>qty &gt;= limit</c> semantics, which is
/// why this rule and its former SQL counterpart (<c>max_order_qty</c>) collapse into one canonical
/// definition - a duplicative-by-accident merge (AAP §0.6.2).
/// </remarks>
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
