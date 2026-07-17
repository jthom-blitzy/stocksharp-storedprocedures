namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking cumulative traded/ordered volume within a single day.
/// </summary>
/// <remarks>
/// This rule shares the single canonical <c>max_daily_volume</c> threshold
/// (<see cref="RiskLimitSet.MaxDailyVolume"/>) with the per-order pre-trade gate
/// <see cref="PreTradeRiskService"/>, but the two are intentionally <b>different-by-design</b>
/// evaluation contexts (AAP §0.6.2 / §0.3.3), not a single shared computation:
/// <list type="bullet">
/// <item>The gate is the <b>authoritative</b> daily-volume check. It computes today's traded
/// volume from <b>persisted</b> orders (accepted/filled/part-filled) scoped by portfolio and
/// security over the current <b>UTC</b> day.</item>
/// <item>This rule is the portfolio-wide <b>circuit-breaker counterpart</b>: an in-stream
/// approximation that accumulates the volume of submitted register/replace messages so the
/// message pipeline can trip without a database round-trip.</item>
/// </list>
/// To reconcile its semantics with the gate, the state is:
/// <list type="bullet">
/// <item><b>Partitioned</b> by (portfolio, security) rather than a single global counter, so one
/// instrument's flow cannot mask or inflate another's.</item>
/// <item><b>Keyed to the UTC calendar day</b> (matching the gate); crossing to a newer UTC day
/// clears the prior day's totals, and a stale message from an already-closed earlier day is not
/// accumulated (the gate remains authoritative for past days). The active day is <b>bounded by the
/// wall-clock UTC day</b>: a future-dated message (whether adversarial or from clock skew) is clamped
/// to today and can never advance the active day into the future, so it cannot erase the current
/// day's accumulated totals and silently disable enforcement (review finding CR-3).</item>
/// <item><b>Deterministic and synchronized</b>: accumulation is guarded by a lock, non-positive
/// (malformed) volumes contribute nothing, and the running total is accumulated with overflow-safe
/// (saturating) arithmetic.</item>
/// </list>
/// Because a replace message is counted as additional submitted volume, this in-stream counter is
/// conservative (it can trip earlier, never later, than the persisted gate) — consistent with the
/// at-least-as-strict reconciliation principle. It trips when a partition's running total meets or
/// exceeds <see cref="Volume"/>. A <see cref="Volume"/> of 0 (or non-positive) means "no limit".
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.IntradayVolumeKey,
	Description = LocalizedStrings.IntradayVolumeKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskDailyVolumeRule : RiskRule
{
	private decimal _volume;

	// Running submitted-volume totals partitioned by (portfolio, security) for the current UTC day.
	private readonly Dictionary<(string Portfolio, string Security), decimal> _dailyTotals = [];
	private readonly object _sync = new();
	private DateTime? _currentUtcDay;

	/// <summary>
	/// Daily volume.
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.VolumeKey,
		Description = LocalizedStrings.VolumeDescKey,
		GroupName = LocalizedStrings.GeneralKey,
		Order = 0)]
	public decimal Volume
	{
		get => _volume;
		set
		{
			if (_volume == value)
				return;

			if (value < 0)
				throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.InvalidValue);

			_volume = value;
			UpdateTitle();
		}
	}

	/// <inheritdoc />
	protected override string GetTitle() => _volume.To<string>();

	/// <summary>
	/// Normalize a message timestamp to its UTC calendar day, matching the gate's UTC-day scope.
	/// A <see cref="DateTimeKind.Local"/> value is converted to UTC; <see cref="DateTimeKind.Utc"/>
	/// and <see cref="DateTimeKind.Unspecified"/> values (the platform stamps these timestamps in
	/// UTC) are used as-is.
	/// </summary>
	private static DateTime ToUtcDay(DateTime time)
		=> (time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time).Date;

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		// OrderReplaceMessage derives from OrderRegisterMessage, so a single cast covers both.
		OrderRegisterMessage order;

		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
			case MessageTypes.OrderReplace:
				order = (OrderRegisterMessage)message;
				break;

			default:
				return false;
		}

		var limit = Volume;

		// Non-positive threshold means "no limit".
		if (limit <= 0)
			return false;

		var orderVolume = order.Volume;

		// Non-positive (malformed) volume carries no exposure and is not accumulated.
		if (orderVolume <= 0)
			return false;

		var utcDay = ToUtcDay(message.LocalTime);
		var key = (order.PortfolioName ?? string.Empty, order.SecurityId.ToString());

		// Bound the message's day by the wall-clock UTC day (review finding CR-3). A future-dated
		// message - whether adversarial or the result of clock skew - must never be able to advance
		// the active day into the future, because doing so would clear the current day's totals and
		// then make every real current-day message look "stale" (day < _currentUtcDay), silently
		// disabling enforcement until the wall clock caught up. Clamping the effective day to today
		// keeps such a message accumulating against the current day (fail-safe/conservative) instead.
		// Past-dated messages are left untouched, so historical replay/backtests are unaffected.
		var nowUtcDay = DateTime.UtcNow.Date;
		var effectiveDay = utcDay > nowUtcDay ? nowUtcDay : utcDay;

		lock (_sync)
		{
			if (_currentUtcDay is null || effectiveDay > _currentUtcDay)
			{
				// Advance to a new UTC day: the prior day's partitioned totals are cleared. Because
				// effectiveDay is bounded by today, this can only ever advance up to the real current
				// UTC day, never to an arbitrary future day supplied by an incoming message.
				_currentUtcDay = effectiveDay;
				_dailyTotals.Clear();
			}
			else if (effectiveDay < _currentUtcDay)
			{
				// Stale/out-of-order message from an already-closed earlier UTC day. The gate's
				// persisted query is authoritative for past days, so this is not accumulated here.
				return false;
			}

			_dailyTotals.TryGetValue(key, out var total);

			try
			{
				total = checked(total + orderVolume);
			}
			catch (OverflowException)
			{
				// Saturate: an overflowing running total necessarily exceeds any finite ceiling.
				total = decimal.MaxValue;
			}

			_dailyTotals[key] = total;

			return total >= limit;
		}
	}

	/// <inheritdoc />
	public override void Reset()
	{
		base.Reset();

		lock (_sync)
		{
			_currentUtcDay = null;
			_dailyTotals.Clear();
		}
	}

	/// <inheritdoc />
	public override void Save(SettingsStorage storage)
	{
		base.Save(storage);

		storage.SetValue(nameof(Volume), Volume);
	}

	/// <inheritdoc />
	public override void Load(SettingsStorage storage)
	{
		base.Load(storage);

		Volume = storage.GetValue<decimal>(nameof(Volume));
	}
}
