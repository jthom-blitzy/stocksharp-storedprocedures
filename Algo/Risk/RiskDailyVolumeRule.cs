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
/// <item><b>Keyed to the trusted-clock UTC calendar day</b> (matching the gate, which scopes its
/// query by the server's current UTC day): the accumulation day is taken from the trusted processing
/// clock (<see cref="UtcNow"/>), <b>not</b> from the caller-supplied <see cref="Message.LocalTime"/>.
/// Every message processed "now" counts against "now"'s bucket; crossing to a newer UTC day clears the
/// prior day's totals. Because a skewed, stale, or default timestamp can no longer choose the bucket,
/// it can neither be back-dated to an already-closed day to dodge accumulation nor forward-dated to
/// erase the current day's totals and silently disable enforcement (review findings CR-28 / CWE-20 /
/// CWE-367). This trusted-clock bucketing subsumes the earlier future-day clamp (CR-3). The clock is
/// injectable purely as a deterministic test seam (review finding CR-24).</item>
/// <item><b>Normalized to the canonical DECIMAL(18,4) scale</b>: each submitted volume is normalized
/// via <see cref="RiskLimitSet.NormalizeMoneySaturating"/> before accumulation, so the in-stream
/// counter is never less strict than the gate at sub-scale boundaries (review finding CR-27).</item>
/// <item><b>Deterministic and synchronized</b>: accumulation is guarded by a lock, non-positive
/// (malformed or sub-scale) volumes contribute nothing, and the running total is accumulated with
/// overflow-safe (saturating) arithmetic.</item>
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

	// The trusted-clock UTC day the partitioned totals currently belong to.
	private DateTime? _currentUtcDay;

	private Func<DateTime> _utcNow = static () => DateTime.UtcNow;

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

	/// <summary>
	/// The trusted UTC processing clock used to determine the current calendar day. Defaults to
	/// <see cref="DateTime.UtcNow"/> - the server/processing clock, mirroring the SQL gate scoping its
	/// daily-volume query by the server's current UTC day - so a caller-supplied, stale, skewed, or
	/// default <see cref="Message.LocalTime"/> can no longer choose the accumulation bucket and thereby
	/// dodge the daily cap by back-dating (review findings CR-28 / CWE-20 / CWE-367, and CR-24 for the
	/// deterministic test seam). Exposed only as an injectable test seam; it is never persisted (see
	/// <see cref="Save"/>/<see cref="Load"/>) and is hidden from the UI. Never null.
	/// </summary>
	[Browsable(false)]
	public Func<DateTime> UtcNow
	{
		get => _utcNow;
		set => _utcNow = value ?? throw new ArgumentNullException(nameof(value));
	}

	/// <inheritdoc />
	protected override string GetTitle() => _volume.To<string>();

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

		// Normalize the submitted volume to the canonical DECIMAL(18,4) persistence scale (review
		// finding CR-27) before accumulating, so the in-stream counter cannot be made less strict than
		// the gate at sub-scale boundaries: a value such as 0.00004 that rounds up to 0.0001 at the gate
		// accumulates as 0.0001 here too. Saturating (rather than throwing) keeps a single malformed
		// message from tearing down the whole message pipeline mid-stream.
		var orderVolume = RiskLimitSet.NormalizeMoneySaturating(order.Volume);

		// Non-positive (malformed, or sub-scale rounding to zero) volume carries no exposure.
		if (orderVolume <= 0)
			return false;

		var key = (order.PortfolioName ?? string.Empty, order.SecurityId.ToString());

		// Determine the accumulation day from the TRUSTED processing clock rather than the
		// caller-supplied message.LocalTime (review findings CR-28 / CWE-20 / CWE-367): the SQL gate
		// scopes its daily-volume query by the server's current UTC day, so every message processed
		// "now" must count against "now"'s bucket. A skewed, stale, or default timestamp from an
		// untrusted producer can therefore no longer land in a different day bucket - in particular it
		// can no longer be back-dated to an "already-closed" day to dodge accumulation, nor forward-dated
		// to clear the current day's totals. This trusted-clock bucketing subsumes the earlier
		// future-day clamp (CR-3): the message timestamp no longer influences the active day at all.
		var effectiveDay = _utcNow().Date;

		lock (_sync)
		{
			if (_currentUtcDay is null || effectiveDay > _currentUtcDay)
			{
				// Advance to a new UTC day (per the trusted clock): the prior day's partitioned totals
				// are cleared. The trusted clock is monotonically non-decreasing, so this only ever
				// advances forward to the real current UTC day.
				_currentUtcDay = effectiveDay;
				_dailyTotals.Clear();
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
