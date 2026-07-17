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

	/// <summary>
	/// Evaluate the notional (<paramref name="volume"/> * <paramref name="price"/>) against the
	/// configured <see cref="Value"/> ceiling. Register and replace messages are handled identically:
	/// a non-positive <see cref="Value"/> disables the rule, non-positive volume or price contribute
	/// no notional, and the multiplication is overflow-safe (a saturating overflow is treated as a
	/// breach, since an overflowing notional is necessarily above any finite ceiling).
	/// </summary>
	/// <param name="volume">Order volume.</param>
	/// <param name="price">Order price.</param>
	/// <returns><see langword="true" /> when the notional meets or exceeds <see cref="Value"/>.</returns>
	private bool IsBreached(decimal volume, decimal price)
	{
		// A non-positive ceiling means "no limit" (mirrors the RiskLimits NULL/0 convention).
		if (Value <= 0)
			return false;

		// Malformed / non-positive inputs carry no notional exposure.
		if (volume <= 0 || price <= 0)
			return false;

		decimal notional;

		try
		{
			notional = checked(volume * price);
		}
		catch (OverflowException)
		{
			// An overflowing notional is unbounded and therefore necessarily above the ceiling;
			// treat it as a breach (conservative, never less strict).
			return true;
		}

		return notional >= Value;
	}

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
			{
				var orderReg = (OrderRegisterMessage)message;
				return IsBreached(orderReg.Volume, orderReg.Price);
			}

			case MessageTypes.OrderReplace:
			{
				var orderReplace = (OrderReplaceMessage)message;
				return IsBreached(orderReplace.Volume, orderReplace.Price);
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
