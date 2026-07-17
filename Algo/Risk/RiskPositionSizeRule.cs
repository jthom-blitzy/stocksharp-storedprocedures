namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking position size.
/// </summary>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.PositionKey,
	Description = LocalizedStrings.RulePositionKey,
	GroupName = LocalizedStrings.PositionsKey)]
public class RiskPositionSizeRule : RiskRule
{
	private decimal _position;

	/// <summary>
	/// Position size.
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.PositionKey,
		Description = LocalizedStrings.PositionSizeKey,
		GroupName = LocalizedStrings.GeneralKey,
		Order = 0)]
	public decimal Position
	{
		get => _position;
		set
		{
			if (_position == value)
				return;

			_position = value;
			UpdateTitle();
		}
	}

	/// <inheritdoc />
	protected override string GetTitle() => _position.To<string>();

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		if (message.Type != MessageTypes.PositionChange)
			return false;

		var posMsg = (PositionChangeMessage)message;
		var currValue = posMsg.TryGetDecimal(PositionChangeTypes.CurrentValue);

		if (currValue == null)
			return false;

		if (Position == 0)
			return false; // No limit when Position is 0 (NULL/0 = not enforced, AAP 0.6.6)

		// P6-F8 reconciliation (AAP 0.6.1 "same threshold AND comparison"; 0.6.2 stricter-wins):
		// the resulting-position-size rule is a SHARED definition applied at two points - this
		// circuit breaker on the live value, and the PreTradeRiskService gate on the post-fill
		// projection. Both MUST use the SAME comparison. The gate already compares
		// Math.Abs(projected) >= limit (magnitude), so this rule now does the same on the live
		// value. The previous DIRECTIONAL comparison (Position > 0 ? curr >= Position :
		// curr <= Position) was LOOSER on the short side - a live short of -150 did NOT trip a
		// +100 cap - which both drifted from the gate and violated stricter-wins. Comparing
		// magnitudes symmetrically caps absolute exposure regardless of side; the ">=" boundary
		// is preserved (an exposure exactly AT the limit still trips).
		return Math.Abs(currValue.Value) >= Math.Abs(Position);
	}

	/// <inheritdoc />
	public override void Save(SettingsStorage storage)
	{
		base.Save(storage);

		storage.SetValue(nameof(Position), Position);
	}

	/// <inheritdoc />
	public override void Load(SettingsStorage storage)
	{
		base.Load(storage);

		Position = storage.GetValue<decimal>(nameof(Position));
	}
}
