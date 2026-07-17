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
	/// <summary>
	/// Canonical order-quantity-ceiling trip decision shared by both enforcement
	/// patterns. Trips when the order volume meets or exceeds the limit; a zero
	/// limit means "not enforced". The <see cref="RiskManager"/> circuit breaker
	/// (via <see cref="ProcessMessage"/>) and the <see cref="PreTradeRiskService"/>
	/// gate both route through this single comparison, so the NULL/0 = "not
	/// enforced" convention can never diverge between the two paths.
	/// </summary>
	/// <param name="volume">Order volume under test.</param>
	/// <param name="limit">Configured volume ceiling; <c>0</c> disables the check.</param>
	/// <returns><see langword="true"/> when the volume meets or exceeds the ceiling.</returns>
	public static bool IsOrderVolumeExceeded(decimal volume, decimal limit)
		=> limit != 0 && volume >= limit;

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
		// Route through the shared IsOrderVolumeExceeded comparison so the 0 = "not
		// enforced" convention is honoured identically here and in the pre-trade
		// gate - a 0 ceiling never trips.
		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
			{
				var orderReg = (OrderRegisterMessage)message;
				return IsOrderVolumeExceeded(orderReg.Volume, Volume);
			}

			case MessageTypes.OrderReplace:
			{
				var orderReplace = (OrderReplaceMessage)message;
				return orderReplace.Volume > 0 && IsOrderVolumeExceeded(orderReplace.Volume, Volume);
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
