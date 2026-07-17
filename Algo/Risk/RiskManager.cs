namespace StockSharp.Algo.Risk;

/// <summary>
/// The risks control manager.
/// </summary>
/// <remarks>
/// Portfolio-wide circuit breaker. It evaluates the live message stream against
/// its configured <see cref="IRiskRule"/> set and, when a rule trips, drives a
/// global action (close positions / stop trading / cancel orders) through the
/// <see cref="RiskMessageAdapter"/>. Its mechanics are unchanged by the risk
/// consolidation; only the SOURCE of each rule's threshold and comparison moved.
///
/// The consolidation gives each risk rule a single owning <see cref="IRiskRule"/>
/// subclass that defines its threshold-and-comparison semantics (the
/// <c>">="</c> "meets or exceeds" convention). The two enforcement patterns are
/// distinct and never merged: this stream-based circuit breaker (which reacts to
/// live state and takes account-level action) and the per-order
/// <see cref="PreTradeRiskService"/> pre-trade gate (which accepts or rejects a
/// single prospective order). They apply the SAME rule definitions but at
/// different points and with different inputs, so their per-rule results can
/// legitimately differ:
/// <list type="bullet">
/// <item>Order price, quantity and notional value: the circuit breaker evaluates
/// each incoming order message through <see cref="RiskOrderPriceRule"/>,
/// <see cref="RiskOrderVolumeRule"/> and <see cref="RiskOrderValueRule"/> via
/// their <c>ProcessMessage</c>; the gate applies the same <c>">="</c> comparison
/// to a prospective order. All of these canonical rules honour the SQL
/// <c>NULL/0 = "not enforced"</c> convention (AAP 0.6.6): a zero limit disables the
/// check IDENTICALLY on BOTH the circuit-breaker and the gate paths - the rule
/// classes short-circuit a zero threshold before any comparison (P6-F7).</item>
/// <item>Resulting position size (<see cref="RiskPositionSizeRule"/>) is a shared
/// definition applied at two points with the SAME comparison: both this circuit
/// breaker (on the live value) and the gate (on the hypothetical post-fill
/// projection) compare the ABSOLUTE magnitude against the limit (P6-F8). The only
/// difference is the application point - the live value vs. the post-fill
/// projection - a by-design timing/framing difference, not a difference in
/// strictness or comparison (see LEGACY_LAYER.md).</item>
/// <item>Order frequency (<see cref="RiskOrderFreqRule"/>) and daily traded
/// volume (<see cref="RiskDailyVolumeRule"/>): the circuit breaker feeds live
/// streaming state while the gate feeds an aggregate read from SQL Server.</item>
/// <item>Cumulative commission is INTENTIONALLY two implementations, each with
/// its OWN independently configured limit: the actual post-fill figure here (off
/// <see cref="Messages.ExecutionMessage"/>) versus the gate's pre-fill estimate.
/// They are NOT wired to a single shared configuration value - the limits are
/// owned separately because the underlying data is available at different times.
/// This is a documented, by-design difference (see LEGACY_LAYER.md).</item>
/// </list>
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