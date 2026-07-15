namespace StockSharp.Algo.Risk;

/// <summary>
/// The risks control manager.
/// </summary>
/// <remarks>
/// Since the StockSharpLegacy SQL layer was added (see /Database,
/// Algo/Storages/Sql, and LEGACY_LAYER.md at the repo root), this is no
/// longer the only place pre-trade risk is enforced, and the two don't cover
/// the same rules. This engine is a portfolio-wide circuit breaker: a
/// triggered rule takes a global action (ClosePositions/StopTrading/
/// CancelOrders via RiskMessageAdapter) rather than rejecting the one order
/// that tripped it. dbo.usp_ValidatePreTradeRisk is the opposite model - a
/// classic per-order gate that rejects a single order before it's accepted,
/// and it also enforces two limits (order notional value, daily traded
/// volume) that have no rule class here at all. Nothing in this codebase
/// reconciles the two, which is the point - a caller going through
/// SqlLegacyOrderGateway gets SQL-side coverage only; a caller going through
/// this RiskManager gets C#-side coverage only.
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