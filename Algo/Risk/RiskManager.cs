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
/// once. Call <see cref="ApplyCanonicalLimits(RiskLimitSet, RiskActions)"/> (or the
/// <see cref="ApplyCanonicalLimits(RiskLimitSet)"/> convenience overload) to seed this manager's
/// circuit-breaker rules from that shared definition; both overloads are exposed through
/// <see cref="IRiskManager"/> so production code can drive the canonical configuration through the same
/// abstraction the platform already resolves (there is no test-only back door). The two limits that were
/// historically SQL-only - order notional value and daily traded volume - now have first-class rule
/// classes here (<see cref="RiskOrderValueRule"/> and <see cref="RiskDailyVolumeRule"/>). The
/// circuit-breaker action behaviour itself is unchanged.
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

	// Serializes canonical (re)configuration against rule processing so a message is never evaluated
	// against a half-built rule set. ApplyCanonicalLimits mutates _rules under this lock and ProcessRules
	// takes a consistent snapshot under it, so a concurrent processor observes either the complete old
	// set or the complete new set - never an intermediate state.
	private readonly object _rulesLock = new();

	// The exact rule instances produced by the most recent ApplyCanonicalLimits call, tracked by
	// reference so a subsequent apply can replace ONLY canonical-generated rules and preserve every
	// other rule (P&L, slippage, position-time, error, or any user-added rule).
	private IRiskRule[] _canonicalRules = [];

	/// <inheritdoc />
	public INotifyList<IRiskRule> Rules => _rules;

	/// <inheritdoc />
	public virtual void Reset()
	{
		IRiskRule[] rules;

		lock (_rulesLock)
			rules = _rules.Cache;

		rules.ForEach(r => r.Reset());
	}

	/// <inheritdoc />
	public IEnumerable<IRiskRule> ProcessRules(Message message)
	{
		if (message.Type == MessageTypes.Reset)
		{
			Reset();
			return [];
		}

		// Grab a consistent snapshot under the same lock that guards canonical reconfiguration, so we
		// never evaluate a message against a partially-swapped rule set (see _rulesLock).
		IRiskRule[] rules;

		lock (_rulesLock)
			rules = _rules.Cache;

		if (rules.Length == 0)
			return [];

		return [.. rules.Where(r => r.ProcessMessage(message))];
	}

	/// <summary>
	/// Builds the deterministic set of circuit-breaker rules for a canonical, single-source-of-truth
	/// <see cref="RiskLimitSet"/>, assigning the supplied <paramref name="action"/> to every generated
	/// rule. This is a pure factory: it creates rules but does not attach them to any manager, so it can
	/// be unit-tested in isolation and reused by both <see cref="ApplyCanonicalLimits(RiskLimitSet, RiskActions)"/>
	/// and any production wiring that needs the rules without mutating a live manager.
	/// </summary>
	/// <remarks>
	/// One rule is emitted per <b>enforced</b> canonical ceiling, honouring the same NULL/0 "not
	/// enforced" convention as <see cref="RiskLimitSet"/>: a ceiling that is not enforced (null or
	/// non-positive) contributes no rule and therefore can never trip. Building the set from the enforced
	/// ceilings - rather than assigning a zero threshold to a disabled rule - deliberately avoids the
	/// circuit-breaker rules' "a zero <c>&gt;=</c> ceiling always trips" hazard (for example
	/// <see cref="RiskOrderPriceRule"/>, whose <see cref="RiskOrderPriceRule.Price"/> of 0 would otherwise
	/// match every order). The mapping is one canonical ceiling to one rule, produced in a fixed,
	/// deterministic order:
	/// <see cref="RiskLimitSet.EffectiveMaxOrderPrice"/> -&gt; <see cref="RiskOrderPriceRule"/>,
	/// <see cref="RiskLimitSet.EffectiveMaxOrderQty"/> -&gt; <see cref="RiskOrderVolumeRule"/>,
	/// <see cref="RiskLimitSet.EffectiveMaxOrderValue"/> -&gt; <see cref="RiskOrderValueRule"/>,
	/// <see cref="RiskLimitSet.EffectiveMaxPositionSize"/> -&gt; a <b>pair</b> of
	/// <see cref="RiskPositionSizeRule"/>s (see below),
	/// <see cref="RiskLimitSet.EffectiveMaxDailyVolume"/> -&gt; <see cref="RiskDailyVolumeRule"/>, the
	/// frequency pair (<see cref="RiskLimitSet.IsFrequencyEnforced"/>) -&gt;
	/// <see cref="RiskOrderFreqRule"/>, and <see cref="RiskLimitSet.EffectiveMaxCommissionTotal"/> -&gt;
	/// <see cref="RiskOrderCommissionRule"/>.
	/// <para>
	/// <b>Position size (both sides).</b> The canonical <see cref="RiskLimitSet.MaxPositionSize"/> is an
	/// absolute magnitude, exactly as the pre-trade gate treats it (<c>Math.Abs(resultingPosition) &gt;=
	/// limit</c>). A single positive <see cref="RiskPositionSizeRule"/> would only catch a growing
	/// <em>long</em> position and would silently miss an equally large <em>short</em>. To match the
	/// gate's absolute semantics, this factory emits two rules for one ceiling: one with
	/// <c>Position = +limit</c> (trips when the current value is at/above the long ceiling) and one with
	/// <c>Position = -limit</c> (trips when the current value is at/below the short floor).
	/// </para>
	/// <para>
	/// <b>Commission (execution context).</b> The commission ceiling maps to
	/// <see cref="RiskOrderCommissionRule"/>, which accumulates the actual commission carried on order
	/// <see cref="ExecutionMessage"/>s streaming through the circuit breaker. The alternative,
	/// <see cref="RiskCommissionRule"/>, only reacts to a money <see cref="PositionChangeMessage"/>
	/// carrying <see cref="PositionChangeTypes.Commission"/>, which the standard execution pipeline does
	/// not emit for order fills - so seeding it would produce a ceiling that can never trip.
	/// </para>
	/// <para>
	/// <b>Frequency validation.</b> A malformed frequency pair (see
	/// <see cref="RiskLimitSet.IsFrequencyMalformed"/> - exactly one of count/window specified) is a
	/// configuration error and throws, rather than being silently dropped.
	/// </para>
	/// </remarks>
	/// <param name="limits">
	/// The canonical limit set to build rules from (for example, the row chosen by
	/// <see cref="RiskLimitSet.SelectMostSpecific(IEnumerable{RiskLimitSet}, int, int)"/>). Cannot be null.
	/// </param>
	/// <param name="action">The circuit-breaker action assigned to every generated rule.</param>
	/// <returns>The ordered list of freshly-created rules; never null (possibly empty when nothing is enforced).</returns>
	/// <exception cref="ArgumentNullException"><paramref name="limits"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException">The frequency pair is malformed (<see cref="RiskLimitSet.IsFrequencyMalformed"/>).</exception>
	public static IList<IRiskRule> CreateRules(RiskLimitSet limits, RiskActions action)
	{
		if (limits is null)
			throw new ArgumentNullException(nameof(limits));

		if (limits.IsFrequencyMalformed)
			throw new ArgumentException(
				$"Malformed order-frequency limit: exactly one of count ({limits.MaxOrderFreqCount?.ToString() ?? "null"}) " +
				$"and window-seconds ({limits.MaxOrderFreqWindowSeconds?.ToString() ?? "null"}) is set. " +
				"Specify both (to enforce) or neither (to disable).", nameof(limits));

		var rules = new List<IRiskRule>();

		void Add(RiskRule rule)
		{
			// Propagate the requested action to every generated rule instead of leaving each rule at the
			// enum default (which silently mapped everything to ClosePositions).
			rule.Action = action;
			rules.Add(rule);
		}

		if (limits.EffectiveMaxOrderPrice is decimal maxOrderPrice)
			Add(new RiskOrderPriceRule { Price = maxOrderPrice });

		if (limits.EffectiveMaxOrderQty is decimal maxOrderQty)
			Add(new RiskOrderVolumeRule { Volume = maxOrderQty });

		if (limits.EffectiveMaxOrderValue is decimal maxOrderValue)
			Add(new RiskOrderValueRule { Value = maxOrderValue });

		if (limits.EffectiveMaxPositionSize is decimal maxPositionSize)
		{
			// One ceiling -> a symmetric pair so both a long build-up and a short build-up are caught,
			// mirroring the gate's absolute-magnitude check.
			Add(new RiskPositionSizeRule { Position = maxPositionSize });
			Add(new RiskPositionSizeRule { Position = -maxPositionSize });
		}

		if (limits.EffectiveMaxDailyVolume is decimal maxDailyVolume)
			Add(new RiskDailyVolumeRule { Volume = maxDailyVolume });

		if (limits.IsFrequencyEnforced)
			Add(new RiskOrderFreqRule
			{
				Count = limits.MaxOrderFreqCount.Value,
				Interval = limits.MaxOrderFreqWindow.Value,
			});

		if (limits.EffectiveMaxCommissionTotal is decimal maxCommissionTotal)
			Add(new RiskOrderCommissionRule { Commission = maxCommissionTotal });

		return rules;
	}

	/// <summary>
	/// Seeds this circuit breaker's <see cref="Rules"/> from the canonical, single-source-of-truth
	/// <see cref="RiskLimitSet"/> using the default <see cref="RiskActions.ClosePositions"/> action, so
	/// every threshold enforced here is defined exactly once and is identical to the one enforced by the
	/// per-order pre-trade gate <see cref="PreTradeRiskService"/>. This is a convenience overload of
	/// <see cref="ApplyCanonicalLimits(RiskLimitSet, RiskActions)"/>; prefer the two-argument overload
	/// when a specific circuit-breaker action is required.
	/// </summary>
	/// <param name="limits">The canonical limit set to seed from. Cannot be null.</param>
	/// <exception cref="ArgumentNullException"><paramref name="limits"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException">The frequency pair is malformed (<see cref="RiskLimitSet.IsFrequencyMalformed"/>).</exception>
	public void ApplyCanonicalLimits(RiskLimitSet limits)
		=> ApplyCanonicalLimits(limits, RiskActions.ClosePositions);

	/// <summary>
	/// Seeds this circuit breaker's <see cref="Rules"/> from the canonical, single-source-of-truth
	/// <see cref="RiskLimitSet"/>, assigning <paramref name="action"/> to every generated rule, so every
	/// threshold enforced here is defined exactly once and is identical to the one enforced by the
	/// per-order pre-trade gate <see cref="PreTradeRiskService"/>.
	/// </summary>
	/// <remarks>
	/// The rule set for <paramref name="limits"/> is built <b>in full first</b> (via
	/// <see cref="CreateRules(RiskLimitSet, RiskActions)"/>) and then swapped in atomically under the same
	/// lock that <see cref="ProcessRules(Message)"/> uses to snapshot the rules, so concurrent message
	/// processing never observes a partially-configured set. Only the rules produced by a previous call to
	/// this method are replaced; every other rule already on the manager (P&amp;L, slippage, position-time,
	/// error, or any rule added directly to <see cref="Rules"/>) is preserved. This method only configures
	/// which rules are present and their thresholds; the circuit-breaker action behaviour and the role of
	/// this manager are unchanged.
	/// </remarks>
	/// <param name="limits">
	/// The canonical limit set to seed from (for example, the row chosen by
	/// <see cref="RiskLimitSet.SelectMostSpecific(IEnumerable{RiskLimitSet}, int, int)"/>). Cannot be null.
	/// </param>
	/// <param name="action">The circuit-breaker action assigned to every generated rule.</param>
	/// <exception cref="ArgumentNullException"><paramref name="limits"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException">The frequency pair is malformed (<see cref="RiskLimitSet.IsFrequencyMalformed"/>).</exception>
	public void ApplyCanonicalLimits(RiskLimitSet limits, RiskActions action)
	{
		// Build the complete replacement set BEFORE touching the live manager. If CreateRules throws
		// (null limits or a malformed frequency pair) the existing configuration is left untouched.
		var newCanonical = CreateRules(limits, action);

		lock (_rulesLock)
		{
			// Preserve every rule that this method did not previously generate (reference identity),
			// so unrelated rules survive a canonical (re)configuration.
			var canonical = _canonicalRules;
			var preserved = _rules.Where(r => !canonical.Contains(r)).ToList();

			_rules.Clear();
			_rules.AddRange(preserved);
			_rules.AddRange(newCanonical);

			_canonicalRules = [.. newCanonical];
		}
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