namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking order price.
/// </summary>
/// <remarks>
/// The price threshold is the canonical order-price ceiling - the single source of truth shared by
/// both risk-enforcement patterns: this portfolio-wide circuit-breaker rule and the per-order
/// pre-trade gate (<see cref="PreTradeRiskService"/>). The shared value originates from
/// <see cref="RiskLimitSet.MaxOrderPrice"/> and is wired onto this rule by the <see cref="RiskManager"/>
/// canonical seed helper, so the ceiling is defined exactly once. The reject-when-<c>&gt;=</c>
/// semantics are identical on both sides, which is why the two implementations collapse to one
/// canonical definition (a duplicative-by-accident merge per AAP 0.6.2).
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.OrderPrice2Key,
	Description = LocalizedStrings.RiskOrderPriceKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskOrderPriceRule : RiskRule
{
	private decimal _price;

	/// <summary>
	/// Order price.
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.PriceKey,
		Description = LocalizedStrings.OrderPriceKey,
		GroupName = LocalizedStrings.GeneralKey,
		Order = 0)]
	public decimal Price
	{
		get => _price;
		set
		{
			if (_price == value)
				return;

			if (value < 0)
				throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.InvalidValue);

			_price = value;
			UpdateTitle();
		}
	}

	/// <inheritdoc />
	protected override string GetTitle() => _price.To<string>();

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
			{
				var orderReg = (OrderRegisterMessage)message;
				return orderReg.Price >= Price;
			}

			case MessageTypes.OrderReplace:
			{
				var orderReplace = (OrderReplaceMessage)message;
				return orderReplace.Price > 0 && orderReplace.Price >= Price;
			}

			default:
				return false;
		}
	}

	/// <inheritdoc />
	public override void Save(SettingsStorage storage)
	{
		base.Save(storage);

		storage.SetValue(nameof(Price), Price);
	}

	/// <inheritdoc />
	public override void Load(SettingsStorage storage)
	{
		base.Load(storage);

		Price = storage.GetValue<decimal>(nameof(Price));
	}
}
