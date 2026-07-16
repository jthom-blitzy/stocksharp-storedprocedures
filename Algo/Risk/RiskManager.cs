namespace StockSharp.Algo.Risk;

/// <summary>
/// The risks control manager.
/// </summary>
/// <remarks>
/// Portfolio-wide circuit breaker. It evaluates the live message stream against
/// its configured <see cref="IRiskRule"/> set and, when a rule trips, drives a
/// global action (close positions / stop trading / cancel orders) through the
/// <see cref="RiskMessageAdapter"/>. Following the risk consolidation, every rule
/// is defined exactly once as an <see cref="IRiskRule"/> subclass — the single
/// source of truth — and there are two distinct, never-merged enforcement
/// patterns that consume those same definitions: this stream-based circuit
/// breaker (which reacts to live state and takes account-level action) and the
/// per-order <see cref="PreTradeRiskService"/> pre-trade gate (which accepts or
/// rejects one prospective order). The order-frequency, notional-value and
/// daily-volume checks now all exist as first-class C# rules
/// (<see cref="RiskOrderFreqRule"/> with rolling-window semantics,
/// <see cref="RiskOrderValueRule"/> and <see cref="RiskDailyVolumeRule"/>), so
/// the two patterns can never diverge in rule definition — only in the input
/// each supplies (live position/stream vs. a prospective order).
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