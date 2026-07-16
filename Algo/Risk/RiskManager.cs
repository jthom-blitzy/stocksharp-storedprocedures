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
/// The consolidation makes each risk rule's threshold-and-comparison the single
/// source of truth, owned by one <see cref="IRiskRule"/> subclass and reused by
/// both enforcement patterns rather than re-encoded in each. The two patterns
/// are distinct and never merged: this stream-based circuit breaker (which
/// reacts to live state and takes account-level action) and the per-order
/// <see cref="PreTradeRiskService"/> pre-trade gate (which accepts or rejects a
/// single prospective order). They consume the SAME definition but supply
/// different inputs by design - the circuit breaker feeds live stream/position
/// state, the gate feeds a prospective order and SQL-sourced aggregates:
/// <list type="bullet">
/// <item>Order price, quantity and notional value: identical pure-threshold rule
/// classes (<see cref="RiskOrderPriceRule"/>, <see cref="RiskOrderVolumeRule"/>,
/// <see cref="RiskOrderValueRule"/>) used by both.</item>
/// <item>Order frequency (<see cref="RiskOrderFreqRule"/>), resulting position
/// size (<see cref="RiskPositionSizeRule"/>) and daily traded volume
/// (<see cref="RiskDailyVolumeRule"/>): the decision is one shared comparison
/// (<c>IsFrequencyExceeded</c> / <c>IsPositionSizeExceeded</c> /
/// <c>IsDailyVolumeExceeded</c>); the circuit breaker feeds live streaming state
/// while the gate feeds a projection/aggregate read from SQL Server.</item>
/// <item>Cumulative commission is INTENTIONALLY two implementations - the actual
/// post-fill figure here (off <see cref="Messages.ExecutionMessage"/>) versus the
/// gate's pre-fill estimate - because the data is available at different times.
/// This is a documented, by-design difference, not a divergence (see
/// LEGACY_LAYER.md).</item>
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