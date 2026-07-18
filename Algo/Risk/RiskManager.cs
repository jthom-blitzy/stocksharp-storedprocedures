namespace StockSharp.Algo.Risk;

/// <summary>
/// The risks control manager.
/// </summary>
/// <remarks>
/// Business risk logic now has a single canonical C# home. The rolling-window order-frequency
/// evaluator and the gate's numeric-ceiling comparison convention live in
/// <see cref="CanonicalRiskRules"/> (the per-check limit values themselves are NOT stored there -
/// the gate reads them from the applicable RiskLimits row at evaluation time), and the per-order
/// accept/reject gate lives in <see cref="PreTradeRiskService"/> (which re-expresses the retired
/// usp_ValidatePreTradeRisk in C#). This class remains the portfolio-wide circuit breaker: a
/// triggered rule takes a global action (ClosePositions/StopTrading/CancelOrders via
/// <see cref="RiskMessageAdapter"/>) rather than rejecting the one order that tripped it, whereas
/// <see cref="PreTradeRiskService"/> is the classic per-order gate that rejects a single order
/// before it is accepted (and it also enforces the order-notional-value and daily-traded-volume
/// limits that used to exist only on the SQL side). Where a rule exists on both sides - order
/// frequency being the prime example - the circuit-breaker rule (<see cref="RiskOrderFreqRule"/>)
/// and the gate now consume the SAME canonical frequency evaluator in
/// <see cref="CanonicalRiskRules"/>. The shared surface is precise and narrow: the rolling-window
/// count definition and the "&gt;= Count" meets-or-exceeds threshold plus the "window must be
/// positive to be enforced" convention - i.e. GIVEN THE SAME observed events the two compute the
/// SAME in-window verdict, so they can no longer disagree on the frequency ARITHMETIC. They still
/// differ, by design, in everything AROUND that arithmetic and so can still reach different
/// outcomes on the same real order flow: the circuit breaker feeds the evaluator an in-memory
/// buffer of message-stream timestamps that it RESETS when it trips, whereas the gate queries the
/// Orders table at evaluation time and counts EVERY recent row (including rejected orders); the
/// data source, the state lifecycle, the rejected-order handling, and the action taken
/// (portfolio-wide ClosePositions/StopTrading/CancelOrders vs. rejecting the single offending
/// order) are all distinct. Rules that exist on both sides but evaluate different subjects (for
/// example commission - post-fill actual vs. pre-fill estimate - and position size - current vs.
/// hypothetical post-fill) are preserved on each side by design rather than merged; see the
/// merged-vs-preserved table in LEGACY_LAYER.md for the exact per-rule verdict.
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