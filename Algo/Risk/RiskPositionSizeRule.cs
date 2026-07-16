namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking position size.
/// </summary>
/// <remarks>
/// This is the portfolio-wide circuit-breaker view of the position limit: it inspects the
/// current position carried on a <see cref="PositionChangeMessage"/> via
/// <see cref="PositionChangeTypes.CurrentValue"/> and answers "are we already over?".
/// By contrast, the per-order pre-trade gate <see cref="PreTradeRiskService"/> projects the
/// hypothetical post-fill position ("would this order put us over?"), because it runs before the
/// order is accepted rather than after a fill has moved the position.
/// Both enforcement patterns consume the single canonical threshold
/// <see cref="RiskLimitSet.MaxPositionSize"/>, yet they are different-by-design: two distinct
/// evaluation contexts that share one threshold, and are therefore deliberately NOT merged (AAP 0.6.2).
/// Concretely, a positive <see cref="Position"/> acts as an upper bound (trip when
/// <c>currValue &gt;= Position</c>), a negative <see cref="Position"/> acts as a short-side lower
/// bound (trip when <c>currValue &lt;= Position</c>), and a zero <see cref="Position"/> disables the check.
/// </remarks>
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
			return false; // No limit when Position is 0

		if (Position > 0)
			return currValue >= Position;
		else
			return currValue <= Position;
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
