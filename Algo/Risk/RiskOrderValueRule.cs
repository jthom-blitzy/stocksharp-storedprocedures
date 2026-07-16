namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking order notional value (volume multiplied by price).
/// </summary>
/// <remarks>
/// This is the canonical C# home of the former SQL-only max_order_value limit
/// (dbo.usp_ValidatePreTradeRisk in Database/002_StoredProcedures.sql). The same
/// threshold is enforced pre-fill by the per-order gate <see cref="PreTradeRiskService"/>
/// (via <see cref="RiskLimitSet.MaxOrderValue"/>); this rule is its portfolio-wide
/// circuit-breaker counterpart. A <see cref="Value"/> of 0 means "no limit".
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.TurnoverKey,
	Description = LocalizedStrings.TurnoverKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskOrderValueRule : RiskRule
{
	private decimal _value;

	/// <summary>
	/// Order value.
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.ValueKey,
		Description = LocalizedStrings.ValueKey,
		GroupName = LocalizedStrings.GeneralKey,
		Order = 0)]
	public decimal Value
	{
		get => _value;
		set
		{
			if (_value == value)
				return;

			if (value < 0)
				throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.InvalidValue);

			_value = value;
			UpdateTitle();
		}
	}

	/// <inheritdoc />
	protected override string GetTitle() => _value.To<string>();

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
			{
				var orderReg = (OrderRegisterMessage)message;
				return Value != 0 && orderReg.Price > 0 && (orderReg.Volume * orderReg.Price) >= Value;
			}

			case MessageTypes.OrderReplace:
			{
				var orderReplace = (OrderReplaceMessage)message;
				return Value != 0 && orderReplace.Price > 0 && orderReplace.Volume > 0 && (orderReplace.Volume * orderReplace.Price) >= Value;
			}

			default:
				return false;
		}
	}

	/// <inheritdoc />
	public override void Save(SettingsStorage storage)
	{
		base.Save(storage);

		storage.SetValue(nameof(Value), Value);
	}

	/// <inheritdoc />
	public override void Load(SettingsStorage storage)
	{
		base.Load(storage);

		Value = storage.GetValue<decimal>(nameof(Value));
	}
}
