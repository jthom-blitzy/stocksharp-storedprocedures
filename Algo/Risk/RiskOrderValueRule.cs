namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking order notional value (quantity * price).
/// </summary>
/// <remarks>
/// Canonical, single source of truth for the order notional-value limit. This
/// check previously existed ONLY in SQL (usp_ValidatePreTradeRisk RULE 3,
/// "(qty * price) >= max_order_value") with no C# counterpart; the risk
/// consolidation promotes it to a first-class C# rule so both enforcement
/// patterns - the <see cref="RiskManager"/> circuit breaker and the
/// <see cref="PreTradeRiskService"/> pre-trade gate - share one definition.
/// A zero threshold means "not enforced" and the reject boundary is ">="
/// ("meets or exceeds"), matching the SQL semantics exactly.
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.TurnoverKey,
	Description = LocalizedStrings.TurnoverKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskOrderValueRule : RiskRule
{
	private decimal _orderValue;

	/// <summary>
	/// Order notional value (quantity * price).
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.ValueKey,
		Description = LocalizedStrings.TurnoverKey,
		GroupName = LocalizedStrings.GeneralKey,
		Order = 0)]
	public decimal OrderValue
	{
		get => _orderValue;
		set
		{
			if (_orderValue == value)
				return;

			if (value < 0)
				throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.InvalidValue);

			_orderValue = value;
			UpdateTitle();
		}
	}

	/// <inheritdoc />
	protected override string GetTitle() => _orderValue.To<string>();

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		// Canonical notional-value check ported from SQL usp_ValidatePreTradeRisk
		// RULE 3 ("(qty * price) >= max_order_value"). ">=" is preserved (never ">")
		// and a 0 threshold means "not enforced" - the shared NULL/0 convention.
		if (OrderValue == 0)
			return false;

		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
			{
				var orderReg = (OrderRegisterMessage)message;
				return orderReg.Volume * orderReg.Price >= OrderValue;
			}

			case MessageTypes.OrderReplace:
			{
				var orderReplace = (OrderReplaceMessage)message;
				return orderReplace.Price > 0 && orderReplace.Volume * orderReplace.Price >= OrderValue;
			}

			default:
				return false;
		}
	}

	/// <inheritdoc />
	public override void Save(SettingsStorage storage)
	{
		base.Save(storage);

		storage.SetValue(nameof(OrderValue), OrderValue);
	}

	/// <inheritdoc />
	public override void Load(SettingsStorage storage)
	{
		base.Load(storage);

		OrderValue = storage.GetValue<decimal>(nameof(OrderValue));
	}
}
