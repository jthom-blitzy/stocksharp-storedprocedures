namespace StockSharp.Algo.Risk;

/// <summary>
/// The risks control manager.
/// </summary>
/// <remarks>
/// Business risk logic now has a single canonical C# home. The shared thresholds and
/// the rolling-window order-frequency evaluator live in <see cref="CanonicalRiskRules"/>,
/// and the per-order accept/reject gate lives in <see cref="PreTradeRiskService"/> (which
/// re-expresses the retired usp_ValidatePreTradeRisk in C#). This class remains the
/// portfolio-wide circuit breaker: a triggered rule takes a global action
/// (ClosePositions/StopTrading/CancelOrders via <see cref="RiskMessageAdapter"/>) rather
/// than rejecting the one order that tripped it, whereas <see cref="PreTradeRiskService"/>
/// is the classic per-order gate that rejects a single order before it is accepted (and it
/// also enforces the order-notional-value and daily-traded-volume limits that used to exist
/// only on the SQL side). Where a rule exists on both sides - order frequency being the
/// prime example - the circuit-breaker rule (<see cref="RiskOrderFreqRule"/>) and the gate
/// now consume the SAME canonical definitions in <see cref="CanonicalRiskRules"/>, so the
/// two can no longer silently diverge. See /LEGACY_LAYER.md at the repo root for the full
/// merged-versus-preserved rule table.
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