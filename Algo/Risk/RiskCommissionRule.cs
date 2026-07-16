namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking commission size.
/// </summary>
/// <remarks>
/// In the portfolio-wide circuit-breaker context this rule tracks the actual commission reported on
/// money <c>PositionChangeMessage</c>s (the <c>PositionChangeTypes.Commission</c> value), i.e. the real
/// cost accrued once fills have executed. It is deliberately distinct from the per-order pre-trade gate
/// (<see cref="PreTradeRiskService"/>), which runs before acceptance and can therefore only estimate
/// commission pre-fill from the configured rate. Both enforcement patterns read the single canonical
/// threshold <see cref="RiskLimitSet.MaxCommissionTotal"/>, so the ceiling is defined exactly once. Yet
/// because one side measures a realized amount while the other measures a pre-fill projection, the two are
/// different-by-design and will not agree numerically - an intentional non-consolidation preserved rather
/// than merged (AAP §0.6.2).
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.CommissionKey,
	Description = LocalizedStrings.RiskCommissionKey,
	GroupName = LocalizedStrings.PnLKey)]
public class RiskCommissionRule : RiskRule
{
	private decimal _commission;

	/// <summary>
	/// Commission size.
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.CommissionKey,
		Description = LocalizedStrings.CommissionDescKey,
		GroupName = LocalizedStrings.GeneralKey,
		Order = 0)]
	public decimal Commission
	{
		get => _commission;
		set
		{
			if (_commission == value)
				return;

			_commission = value;
			UpdateTitle();
		}
	}

	/// <inheritdoc />
	protected override string GetTitle() => _commission.To<string>();

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		if (Commission == 0)
			return false; // No limit when Commission is 0

		if (message.Type != MessageTypes.PositionChange)
			return false;

		var pfMsg = (PositionChangeMessage)message;

		if (!pfMsg.IsMoney())
			return false;

		var currValue = pfMsg.TryGetDecimal(PositionChangeTypes.Commission);

		if (currValue == null)
			return false;

		// Handle both positive (upper bound) and negative (lower bound) commission limits
		if (Commission > 0)
			return currValue >= Commission;
		else
			return currValue <= Commission;
	}

	/// <inheritdoc />
	public override void Save(SettingsStorage storage)
	{
		base.Save(storage);

		storage.SetValue(nameof(Commission), Commission);
	}

	/// <inheritdoc />
	public override void Load(SettingsStorage storage)
	{
		base.Load(storage);

		Commission = storage.GetValue<decimal>(nameof(Commission));
	}
}
