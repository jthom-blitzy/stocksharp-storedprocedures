namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking position size.
/// </summary>
/// <remarks>
/// Canonical, single source of truth for the resulting-position-size limit,
/// shared by both enforcement patterns as a SHARED DEFINITION WITH TWO
/// APPLICATION POINTS (AAP 0.6.4). The comparison is a SYMMETRIC ABSOLUTE
/// ceiling - it trips when <c>|position| &gt;= |limit|</c> - matching the SQL
/// gate's <c>ABS(current + signed order qty) &gt;= max_position_size</c>. This
/// reconciles a prior divergence where the stream rule compared directionally
/// (so a large short could evade a positive limit); the absolute comparison is
/// at least as strict in every direction, honouring the stricter-wins hard
/// constraint (AAP 0.6.2). The two patterns differ only in the value supplied:
/// the <see cref="RiskManager"/> circuit breaker feeds the LIVE position from a
/// <see cref="PositionChangeMessage"/>, while the <see cref="PreTradeRiskService"/>
/// gate feeds the HYPOTHETICAL POST-FILL projection (current + signed order
/// quantity) before the order is accepted. A zero limit means "not enforced".
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.PositionKey,
	Description = LocalizedStrings.RulePositionKey,
	GroupName = LocalizedStrings.PositionsKey)]
public class RiskPositionSizeRule : RiskRule
{
	/// <summary>
	/// Canonical resulting-position-size trip decision shared by both enforcement
	/// patterns. Trips when the absolute position meets or exceeds the absolute
	/// limit; a zero limit means "not enforced". The <see cref="RiskManager"/>
	/// circuit breaker passes the live position value; the
	/// <see cref="PreTradeRiskService"/> gate passes the hypothetical post-fill
	/// projection - both route through this single symmetric comparison.
	/// </summary>
	/// <param name="value">Position value under test (live or projected).</param>
	/// <param name="limit">Configured position-size ceiling; <c>0</c> disables the check.</param>
	/// <returns><see langword="true"/> when the position meets or exceeds the ceiling.</returns>
	public static bool IsPositionSizeExceeded(decimal value, decimal limit)
		=> limit != 0 && Math.Abs(value) >= Math.Abs(limit);

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

		// Canonical symmetric-absolute comparison (see IsPositionSizeExceeded and
		// the class remarks): the live position is checked against |limit| in both
		// directions, so an oversized short can no longer evade a positive ceiling.
		return IsPositionSizeExceeded(currValue.Value, Position);
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
