namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking daily traded volume.
/// </summary>
/// <remarks>
/// Canonical, single source of truth for the daily-traded-volume limit. This
/// check previously existed ONLY in SQL (usp_ValidatePreTradeRisk RULE 7,
/// "today's accepted/filled qty + new qty >= max_daily_volume") with no C#
/// counterpart; the risk consolidation promotes it to a first-class C# rule.
///
/// The canonical decision - "does the cumulative traded volume for the day,
/// including the incoming order, meet or exceed the limit?" - lives in the
/// shared <see cref="IsDailyVolumeExceeded"/> comparison, which BOTH enforcement
/// patterns call so the rule is defined exactly once. The two patterns differ
/// only in how the cumulative total is sourced (a by-design, two-application-point
/// split, like resulting-position-size):
/// <list type="bullet">
/// <item>The <see cref="RiskManager"/> circuit breaker (stream) keeps its own
/// running daily total: it accumulates each order's volume it observes and
/// resets when the day rolls over, so a run of individually-small orders still
/// trips the daily limit (the earlier per-order comparator never did).</item>
/// <item>The <see cref="PreTradeRiskService"/> gate does not use the streaming
/// total; it reads the authoritative accepted/filled volume for the current UTC
/// day from SQL Server and adds the prospective order's quantity before calling
/// the same comparison.</item>
/// </list>
/// A zero threshold means "not enforced" and the reject boundary is ">="
/// ("meets or exceeds"), matching the SQL semantics exactly.
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.IntradayVolumeKey,
	Description = LocalizedStrings.RiskOrderVolumeKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskDailyVolumeRule : RiskRule
{
	// Streaming daily accumulator (RiskManager path only): the running traded
	// volume for _accumulatedDate. Guarded by _sync; reset when the day rolls
	// over or on Reset(). The gate does NOT use this state - it supplies its own
	// SQL-sourced daily total to IsDailyVolumeExceeded.
	private readonly object _sync = new();
	private decimal _accumulatedVolume;
	private DateTime _accumulatedDate;

	private decimal _dailyVolume;

	/// <summary>
	/// Canonical daily-volume trip decision shared by both enforcement patterns.
	/// Trips when the cumulative traded volume for the day (including the
	/// incoming order) meets or exceeds the limit; a zero limit means "not
	/// enforced". The <see cref="RiskManager"/> circuit breaker passes its
	/// streaming daily accumulator; the <see cref="PreTradeRiskService"/> gate
	/// passes today's SQL-sourced accepted/filled volume plus the prospective
	/// order quantity - both route through this single comparison.
	/// </summary>
	/// <param name="cumulativeVolume">Cumulative traded volume for the day, including the incoming order.</param>
	/// <param name="limit">Configured daily-volume limit; <c>0</c> disables the check.</param>
	/// <returns><see langword="true"/> when the incoming order must be rejected.</returns>
	public static bool IsDailyVolumeExceeded(decimal cumulativeVolume, decimal limit)
		=> limit != 0 && cumulativeVolume >= limit;

	/// <summary>
	/// Daily traded volume.
	/// </summary>
	[Display(
		ResourceType = typeof(LocalizedStrings),
		Name = LocalizedStrings.DailyKey,
		Description = LocalizedStrings.IntradayVolumeKey,
		GroupName = LocalizedStrings.GeneralKey,
		Order = 0)]
	public decimal DailyVolume
	{
		get => _dailyVolume;
		set
		{
			if (_dailyVolume == value)
				return;

			if (value < 0)
				throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.InvalidValue);

			_dailyVolume = value;
			UpdateTitle();
		}
	}

	/// <inheritdoc />
	protected override string GetTitle() => _dailyVolume.To<string>();

	/// <inheritdoc />
	public override void Reset()
	{
		base.Reset();

		lock (_sync)
		{
			_accumulatedVolume = 0;
			_accumulatedDate = default;
		}
	}

	/// <inheritdoc />
	public override bool ProcessMessage(Message message)
	{
		// Canonical daily-volume check ported from SQL usp_ValidatePreTradeRisk
		// RULE 7 ("today's qty + new qty >= max_daily_volume"). In the stream
		// path this rule keeps its own running daily total so a series of
		// individually-small orders still trips the limit; the decision itself is
		// the shared IsDailyVolumeExceeded comparison the gate also uses. ">="
		// preserved; 0 = not enforced.
		if (DailyVolume == 0)
			return false;

		decimal volume;

		switch (message.Type)
		{
			case MessageTypes.OrderRegister:
				volume = ((OrderRegisterMessage)message).Volume;
				break;

			case MessageTypes.OrderReplace:
			{
				volume = ((OrderReplaceMessage)message).Volume;

				if (volume <= 0)
					return false;

				break;
			}

			default:
				return false;
		}

		lock (_sync)
		{
			// Bucket by the message timestamp's date and reset when the day rolls
			// over. The gate supplies the authoritative SQL UTC-day total instead
			// of this streaming accumulator.
			var day = message.LocalTime.Date;

			if (day != _accumulatedDate)
			{
				_accumulatedDate = day;
				_accumulatedVolume = 0;
			}

			_accumulatedVolume += volume;

			return IsDailyVolumeExceeded(_accumulatedVolume, DailyVolume);
		}
	}

	/// <inheritdoc />
	public override void Save(SettingsStorage storage)
	{
		base.Save(storage);

		storage.SetValue(nameof(DailyVolume), DailyVolume);
	}

	/// <inheritdoc />
	public override void Load(SettingsStorage storage)
	{
		base.Load(storage);

		DailyVolume = storage.GetValue<decimal>(nameof(DailyVolume));
	}
}
