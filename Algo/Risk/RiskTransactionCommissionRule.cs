namespace StockSharp.Algo.Risk;

/// <summary>
/// The base class for risk-rules, tracking commission for own transactions.
/// </summary>
/// <remarks>
/// Subclasses accumulate the actual commission realized post-fill from
/// <see cref="ExecutionMessage.Commission"/> as executions stream in, enforcing the ceiling within the
/// portfolio-wide circuit-breaker context driven by <see cref="RiskManager"/>. This is deliberately
/// different from the per-order pre-trade gate <see cref="PreTradeRiskService"/>, which runs before any
/// fill exists and can therefore only estimate the commission as
/// <c>existingNotional * commission_rate + qty * estPrice * commission_rate</c>. Both enforcement
/// patterns consume the single canonical threshold <see cref="RiskLimitSet.MaxCommissionTotal"/>, yet
/// because one measures a realized running total while the other projects a pre-fill estimate, the two
/// are different-by-design and will not agree numerically. This intentional divergence is preserved
/// rather than force-merged under DRY, per AAP §0.6.2.
/// <para>
/// Robustness (review finding CR-15): the running total and the duplicate-suppression set are guarded
/// by a single lock so accumulation and <see cref="Reset"/> are correct under concurrent message
/// delivery; each execution is applied at most once, keyed by a stable identity
/// (<see cref="ExecutionMessage.SeqNum"/>, else the trade identity, else a transaction/order composite),
/// so a re-delivered message cannot inflate the total; and accumulation saturates instead of throwing
/// on <see cref="decimal"/> overflow. When no stable identity can be formed the message is counted
/// (never dropped), keeping the rule at-least-as-strict.
/// </para>
/// </remarks>
public abstract class RiskTransactionCommissionRule : RiskRule
{
	private decimal _commission;

	// Running realized-commission total and its guard. Every read-modify-write of the total (and the
	// dedup set below) happens under _sync so the rule is correct under concurrent message delivery
	// (review finding CR-15).
	private readonly object _sync = new();
	private decimal _totalCommission;

	// Stable identities of executions already applied to _totalCommission, so a re-delivered (duplicate)
	// execution message - e.g. after a transport reconnect or replay - is counted at most once
	// (review finding CR-15). Cleared on Reset alongside the running total.
	private readonly HashSet<string> _seen = [];

	/// <summary>
	/// Commission limit.
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.CommissionKey,
		Description = LocalizedStrings.CommissionDescKey,
		GroupName = LocalizedStrings.GeneralKey,
		Order = 0)]
	public decimal Commission
	{
		get => _commission;
		set
		{
			if (_commission == value)
				return;

			_commission = value;
			UpdateTitle();
		}
	}

	/// <inheritdoc />
	protected override string GetTitle() => _commission.To<string>();

	/// <inheritdoc />
	public override void Reset()
	{
		base.Reset();

		lock (_sync)
		{
			_totalCommission = 0m;
			_seen.Clear();
		}
	}

	/// <summary>
	/// Determine whether the commission is applicable to this rule.
	/// </summary>
	/// <param name="execMsg"><see cref="ExecutionMessage"/></param>
	/// <returns>Check result.</returns>
	protected abstract bool IsMatch(ExecutionMessage execMsg);

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		if (message.Type != MessageTypes.Execution)
			return false;

		if (message is not ExecutionMessage execMsg ||
			!IsMatch(execMsg) ||
			execMsg.Commission is not decimal commission)
			return false;

		var limit = Commission;

		lock (_sync)
		{
			// Deduplicate by a stable execution identity (review finding CR-15, concurrency/replay): a
			// transport reconnect or replay can re-deliver the identical execution message, which the
			// previous unsynchronized "_totalCommission += commission" would count repeatedly and could
			// trip the breaker on phantom commission. Each execution is applied to the running total at
			// most once. When a stable identity cannot be formed we deliberately fall through and COUNT
			// the message rather than drop it: over-counting toward a ceiling is the conservative,
			// at-least-as-strict choice, whereas dropping could silently under-count and weaken the limit.
			var identity = TryGetExecutionIdentity(execMsg, commission);

			if (identity is not null && !_seen.Add(identity))
			{
				// Already applied: ignore the duplicate but still report the current breach state, so a
				// re-delivered message can never flip an already-tripped breaker back to "not breached".
				return IsBreached(_totalCommission, limit);
			}

			// Overflow-safe (saturating) accumulation (review finding CR-15): a pathological commission
			// stream must not throw mid-pipeline. Saturate toward the sign of the incoming commission -
			// a positive overflow necessarily exceeds any finite positive ceiling, and a negative
			// overflow necessarily undershoots any finite negative ceiling.
			try
			{
				_totalCommission = checked(_totalCommission + commission);
			}
			catch (OverflowException)
			{
				_totalCommission = commission >= 0m ? decimal.MaxValue : decimal.MinValue;
			}

			return IsBreached(_totalCommission, limit);
		}
	}

	/// <summary>
	/// Evaluate the breach predicate against the running total. A <see cref="Commission"/> of 0 means
	/// "no limit"; a positive ceiling trips when the total meets or exceeds it, and a negative ceiling
	/// (e.g. a credit/rebate floor) trips when the total falls to or below it.
	/// </summary>
	private static bool IsBreached(decimal total, decimal limit)
	{
		if (limit == 0m)
			return false; // No limit when Commission is 0.

		return limit > 0m ? total >= limit : total <= limit;
	}

	/// <summary>
	/// Build a stable identity that survives re-delivery yet distinguishes genuinely distinct commission
	/// events. <see cref="ExecutionMessage.SeqNum"/> (when set) is the strongest per-message key;
	/// otherwise a fill is keyed by its trade identity (<see cref="ExecutionMessage.TradeId"/> /
	/// <see cref="ExecutionMessage.TradeStringId"/>), and an order-info execution by the composite of its
	/// transaction/order identity plus the fields that differ between legitimate distinct updates
	/// (<see cref="ExecutionMessage.Balance"/>, <see cref="ExecutionMessage.ServerTime"/>) and the
	/// commission itself. Returns <see langword="null"/> when no meaningful identity is available.
	/// </summary>
	private static string TryGetExecutionIdentity(ExecutionMessage execMsg, decimal commission)
	{
		if (execMsg.SeqNum != default)
			return "S:" + execMsg.SeqNum.To<string>();

		if (execMsg.HasTradeInfo)
		{
			if (execMsg.TradeId is long tradeId)
				return "T:" + tradeId.To<string>();

			if (!execMsg.TradeStringId.IsEmptyOrWhiteSpace())
				return "TS:" + execMsg.TradeStringId;
		}

		var hasOrderId = execMsg.OrderId is not null || !execMsg.OrderStringId.IsEmptyOrWhiteSpace();

		if (execMsg.TransactionId != default || hasOrderId)
		{
			return string.Join("|",
				"O",
				execMsg.TransactionId.To<string>(),
				execMsg.OrderId?.To<string>() ?? string.Empty,
				execMsg.OrderStringId ?? string.Empty,
				execMsg.Balance?.To<string>() ?? string.Empty,
				execMsg.ServerTime.Ticks.To<string>(),
				commission.To<string>());
		}

		return null;
	}

	/// <inheritdoc />
	public override void Save(SettingsStorage storage)
	{
		base.Save(storage);

		storage.Set(nameof(Commission), Commission);
	}

	/// <inheritdoc />
	public override void Load(SettingsStorage storage)
	{
		base.Load(storage);

		Commission = storage.GetValue<decimal>(nameof(Commission));
	}
}
