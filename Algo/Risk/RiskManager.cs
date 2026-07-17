namespace StockSharp.Algo.Risk;

/// <summary>
/// The risks control manager.
/// </summary>
/// <remarks>
/// This engine is a portfolio-wide circuit breaker: when a rule trips it takes a global action
/// (ClosePositions/StopTrading/CancelOrders via <see cref="RiskMessageAdapter"/>) rather than
/// rejecting the single order that tripped it. That keeps it architecturally distinct from the
/// per-order pre-trade gate <see cref="PreTradeRiskService"/> (the C# port of
/// <c>dbo.usp_ValidatePreTradeRisk</c>), which rejects one order before it is accepted. The two
/// enforcement patterns are deliberately kept separate and are intentionally NOT merged (AAP §0.6).
/// They are, however, no longer defined independently: the risk decisioning that used to live in
/// T-SQL stored procedures has been consolidated into C#, and both patterns now resolve their
/// thresholds from the same canonical <see cref="RiskLimitSet"/>, so every limit is defined exactly
/// once. Call <see cref="ApplyCanonicalLimits(RiskLimitSet)"/> to seed this manager's circuit-breaker
/// rules from that shared definition; the two limits that were historically SQL-only - order notional
/// value and daily traded volume - now have first-class rule classes here
/// (<see cref="RiskOrderValueRule"/> and <see cref="RiskDailyVolumeRule"/>). The circuit-breaker
/// action behaviour itself is unchanged.
/// </remarks>
public class RiskManager : BaseLogReceiver, IRiskManager
{
	/// <summary>
	/// Initializes a new instance of the <see cref="RiskManager"/>.
	/// </summary>
	public RiskManager()
	{
	}

	private readonly CachedSynchronizedList<IRiskRule> _rules = [];

	/// <inheritdoc />
	public INotifyList<IRiskRule> Rules => _rules;

	/// <inheritdoc />
	public virtual void Reset()
	{
		_rules.Cache.ForEach(r => r.Reset());
	}

	/// <inheritdoc />
	public IEnumerable<IRiskRule> ProcessRules(Message message)
	{
		if (message.Type == MessageTypes.Reset)
		{
			Reset();
			return [];
		}

		var rules = _rules.Cache;

		if (rules.Length == 0)
			return [];

		return [.. rules.Where(r => r.ProcessMessage(message))];
	}

	/// <summary>
	/// Seeds this circuit breaker's <see cref="Rules"/> from the canonical, single-source-of-truth
	/// <see cref="RiskLimitSet"/> so every threshold enforced here is defined exactly once and is
	/// identical to the one enforced by the per-order pre-trade gate <see cref="PreTradeRiskService"/>.
	/// This is the "canonical seed helper" referenced by the individual rule classes.
	/// </summary>
	/// <remarks>
	/// The existing rule set is replaced with one rule per <b>enforced</b> canonical ceiling, honouring
	/// the same NULL/0 "not enforced" convention as <see cref="RiskLimitSet"/>: a ceiling that is not
	/// enforced (null or non-positive) contributes no rule and therefore can never trip. Building the
	/// set from the enforced ceilings - rather than assigning a zero threshold to a disabled rule -
	/// deliberately avoids the circuit-breaker rules' "a zero <c>&gt;=</c> ceiling always trips" hazard
	/// (for example <see cref="RiskOrderPriceRule"/>, whose <see cref="RiskOrderPriceRule.Price"/> of 0
	/// would otherwise match every order). The mapping is one canonical ceiling to one rule:
	/// <see cref="RiskLimitSet.EffectiveMaxOrderPrice"/> -&gt; <see cref="RiskOrderPriceRule"/>,
	/// <see cref="RiskLimitSet.EffectiveMaxOrderQty"/> -&gt; <see cref="RiskOrderVolumeRule"/>,
	/// <see cref="RiskLimitSet.EffectiveMaxOrderValue"/> -&gt; <see cref="RiskOrderValueRule"/>,
	/// <see cref="RiskLimitSet.EffectiveMaxPositionSize"/> -&gt; <see cref="RiskPositionSizeRule"/>,
	/// <see cref="RiskLimitSet.EffectiveMaxDailyVolume"/> -&gt; <see cref="RiskDailyVolumeRule"/>, the
	/// frequency pair (<see cref="RiskLimitSet.IsFrequencyEnforced"/>) -&gt;
	/// <see cref="RiskOrderFreqRule"/>, and <see cref="RiskLimitSet.EffectiveMaxCommissionTotal"/> -&gt;
	/// <see cref="RiskCommissionRule"/>. This method only configures which rules are present and their
	/// thresholds; the circuit-breaker action behaviour and the role of this manager are unchanged.
	/// </remarks>
	/// <param name="limits">
	/// The canonical limit set to seed from (for example, the row chosen by
	/// <see cref="RiskLimitSet.SelectMostSpecific(IEnumerable{RiskLimitSet}, int, int)"/>). Cannot be null.
	/// </param>
	/// <exception cref="ArgumentNullException"><paramref name="limits"/> is <see langword="null"/>.</exception>
	public void ApplyCanonicalLimits(RiskLimitSet limits)
	{
		if (limits is null)
			throw new ArgumentNullException(nameof(limits));

		// Rebuild deterministically so repeated calls never accumulate duplicate rules.
		Rules.Clear();

		if (limits.EffectiveMaxOrderPrice is decimal maxOrderPrice)
			Rules.Add(new RiskOrderPriceRule { Price = maxOrderPrice });

		if (limits.EffectiveMaxOrderQty is decimal maxOrderQty)
			Rules.Add(new RiskOrderVolumeRule { Volume = maxOrderQty });

		if (limits.EffectiveMaxOrderValue is decimal maxOrderValue)
			Rules.Add(new RiskOrderValueRule { Value = maxOrderValue });

		if (limits.EffectiveMaxPositionSize is decimal maxPositionSize)
			Rules.Add(new RiskPositionSizeRule { Position = maxPositionSize });

		if (limits.EffectiveMaxDailyVolume is decimal maxDailyVolume)
			Rules.Add(new RiskDailyVolumeRule { Volume = maxDailyVolume });

		if (limits.IsFrequencyEnforced)
			Rules.Add(new RiskOrderFreqRule
			{
				Count = limits.MaxOrderFreqCount.Value,
				Interval = limits.MaxOrderFreqWindow.Value,
			});

		if (limits.EffectiveMaxCommissionTotal is decimal maxCommissionTotal)
			Rules.Add(new RiskCommissionRule { Commission = maxCommissionTotal });
	}

	/// <inheritdoc />
	public override void Load(SettingsStorage storage)
	{
		Rules.Clear();
		Rules.AddRange(storage.GetValue<SettingsStorage[]>(nameof(Rules)).Select(s => s.LoadEntire<IRiskRule>()));

		base.Load(storage);
	}

	/// <inheritdoc />
	public override void Save(SettingsStorage storage)
	{
		storage.SetValue(nameof(Rules), Rules.Select(r => r.SaveEntire(false)).ToArray());

		base.Save(storage);
	}

	/// <inheritdoc />
	public IRiskManager Clone()
	{
		var clone = new RiskManager();
		clone.Load(this.Save());
		return clone;
	}

	object ICloneable.Clone() => Clone();
}