namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking order price.
/// </summary>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.OrderPrice2Key,
	Description = LocalizedStrings.RiskOrderPriceKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskOrderPriceRule : RiskRule
{
	/// <summary>
	/// Canonical order-price-ceiling trip decision shared by both enforcement
	/// patterns. Trips when the order price meets or exceeds the limit; a zero
	/// limit means "not enforced". The <see cref="RiskManager"/> circuit breaker
	/// (via <see cref="ProcessMessage"/>) and the <see cref="PreTradeRiskService"/>
	/// gate both route through this single comparison, so the NULL/0 = "not
	/// enforced" convention can never diverge between the two paths.
	/// </summary>
	/// <param name="price">Order price under test.</param>
	/// <param name="limit">Configured price ceiling; <c>0</c> disables the check.</param>
	/// <returns><see langword="true"/> when the price meets or exceeds the ceiling.</returns>
	public static bool IsOrderPriceExceeded(decimal price, decimal limit)
		=> limit != 0 && price >= limit;

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
		// Route through the shared IsOrderPriceExceeded comparison so the 0 = "not
		// enforced" convention is honoured identically here and in the pre-trade
		// gate - a 0 ceiling never trips.
		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
			{
				var orderReg = (OrderRegisterMessage)message;
				return IsOrderPriceExceeded(orderReg.Price, Price);
			}

			case MessageTypes.OrderReplace:
			{
				var orderReplace = (OrderReplaceMessage)message;
				return orderReplace.Price > 0 && IsOrderPriceExceeded(orderReplace.Price, Price);
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
