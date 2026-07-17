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
				return IsNotionalExceeded(orderReg.Volume, orderReg.Price);
			}

			case MessageTypes.OrderReplace:
			{
				var orderReplace = (OrderReplaceMessage)message;

				// Notional needs BOTH dimensions, so guard price AND volume symmetrically
				// (MJ-4): an incomplete replace that leaves either unset (<= 0) carries no
				// well-defined notional and is not evaluated HERE. Such an order is still
				// bounded by the per-dimension RiskOrderPriceRule / RiskOrderVolumeRule
				// ceilings, and order SUBMISSION is gated authoritatively by
				// PreTradeRiskService, which always supplies a concrete
				// OrderRegisterMessage (never a replace). The earlier code guarded only
				// price, letting a volume-less replace slip the notional ceiling - the
				// symmetric guard closes that (stricter-wins, AAP 0.6.2).
				return orderReplace.Price > 0 && orderReplace.Volume > 0
					&& IsNotionalExceeded(orderReplace.Volume, orderReplace.Price);
			}

			default:
				return false;
		}
	}

	// Notional (Volume * Price) can overflow DECIMAL for extreme values on the stream
	// path (unlike the gate, streamed messages are not range-checked). An overflow means
	// the notional is astronomically large and so exceeds any configured OrderValue, so
	// the rule trips - failing closed under stricter-wins (AAP 0.6.2) rather than letting
	// the multiplication throw and disrupt the circuit breaker or silently admit the
	// order (MJ-7). ">=" ("meets or exceeds") is preserved.
	private bool IsNotionalExceeded(decimal volume, decimal price)
	{
		try
		{
			return volume * price >= OrderValue;
		}
		catch (OverflowException)
		{
			return true;
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
