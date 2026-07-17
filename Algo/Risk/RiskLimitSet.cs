namespace StockSharp.Algo.Risk;

/// <summary>
/// The canonical, single-source-of-truth set of risk thresholds, mirroring the columns of the
/// <c>dbo.RiskLimits</c> table (see <c>Database/001_Schema.sql</c>). One instance represents the
/// effective ceilings for a given portfolio/security scope and is shared, without modification, by
/// both risk-enforcement patterns so that every threshold is defined exactly once.
/// </summary>
/// <remarks>
/// A <see langword="null"/> ceiling means "not enforced" - the same NULL/0 convention used by the
/// <c>dbo.RiskLimits</c> table and by the existing C# <c>RiskRule</c> classes (a 0/unset threshold is
/// treated as "no limit"). The very same instance is consumed by the per-order pre-trade gate
/// (<see cref="PreTradeRiskService"/>) and by the portfolio-wide circuit breaker
/// (<see cref="RiskManager"/>), satisfying the "define once, enforce in multiple patterns" mandate.
/// This is a plain, dependency-free value object: it does not derive from <c>RiskRule</c>, carries no
/// UI metadata, and is safe to construct by hand in tests as well as to hydrate from ADO.NET.
/// </remarks>
public class RiskLimitSet
{
	/// <summary>
	/// Ceiling on a single order's price (<c>dbo.RiskLimits.max_order_price</c>); the threshold consumed
	/// by the order-price rule. <see langword="null"/> means the price ceiling is not enforced.
	/// </summary>
	public decimal? MaxOrderPrice { get; init; }

	/// <summary>
	/// Ceiling on a single order's quantity (<c>dbo.RiskLimits.max_order_qty</c>); the threshold consumed
	/// by the order-volume rule. <see langword="null"/> means the quantity ceiling is not enforced.
	/// </summary>
	public decimal? MaxOrderQty { get; init; }

	/// <summary>
	/// Ceiling on a single order's notional value, i.e. <c>qty * price</c>
	/// (<c>dbo.RiskLimits.max_order_value</c>); the threshold consumed by the order-value rule. This limit
	/// was historically SQL-only, with no C# equivalent. <see langword="null"/> means the notional ceiling
	/// is not enforced.
	/// </summary>
	public decimal? MaxOrderValue { get; init; }

	/// <summary>
	/// Ceiling on the absolute post-fill position size (<c>dbo.RiskLimits.max_position_size</c>); the
	/// threshold consumed by the position-size rule. <see langword="null"/> means the position ceiling is
	/// not enforced.
	/// </summary>
	public decimal? MaxPositionSize { get; init; }

	/// <summary>
	/// Ceiling on the cumulative traded quantity per day (<c>dbo.RiskLimits.max_daily_volume</c>); the
	/// threshold consumed by the daily-volume rule. This limit was historically SQL-only, with no C#
	/// equivalent. <see langword="null"/> means the daily-volume ceiling is not enforced.
	/// </summary>
	public decimal? MaxDailyVolume { get; init; }

	/// <summary>
	/// Maximum number of orders permitted within <see cref="MaxOrderFreqWindowSeconds"/>
	/// (<c>dbo.RiskLimits.max_order_freq_count</c>); the count threshold consumed by the order-frequency
	/// rule. <see langword="null"/> means the order-frequency limit is not enforced.
	/// </summary>
	public int? MaxOrderFreqCount { get; init; }

	/// <summary>
	/// Length, in seconds, of the rolling order-frequency window
	/// (<c>dbo.RiskLimits.max_order_freq_window_sec</c>); the interval paired with
	/// <see cref="MaxOrderFreqCount"/> and consumed by the order-frequency rule. <see langword="null"/>
	/// means no window is configured.
	/// </summary>
	public int? MaxOrderFreqWindowSeconds { get; init; }

	/// <summary>
	/// Ceiling on the cumulative commission (<c>dbo.RiskLimits.max_commission_total</c>); the threshold
	/// consumed by the commission rule. <see langword="null"/> means the commission ceiling is not enforced.
	/// </summary>
	public decimal? MaxCommissionTotal { get; init; }

	/// <summary>
	/// Commission rate applied when estimating an order's commission
	/// (<c>dbo.RiskLimits.commission_rate</c>).
	/// </summary>
	/// <remarks>
	/// This property intentionally has <b>no C# default</b>. The single authoritative source for the
	/// rate is the database column <c>dbo.RiskLimits.commission_rate</c> and its
	/// <c>DF_RiskLimits_commission_rate</c> default (<c>0.0005</c>); duplicating that literal here would
	/// create two independently-drifting sources of truth (MA-15). The pre-trade gate
	/// <see cref="PreTradeRiskService"/> always hydrates this value from the loaded <c>dbo.RiskLimits</c>
	/// row, so production never relies on the CLR default. When a <see cref="RiskLimitSet"/> is
	/// constructed by hand (for example in tests) and the commission estimate matters, the rate must be
	/// set explicitly; an unset rate is <c>0</c>, which yields a zero commission estimate rather than a
	/// silent, hard-coded assumption. The rate is not a ceiling and never affects
	/// <see cref="IsUnlimited"/>.
	/// </remarks>
	public decimal CommissionRate { get; init; }

	/// <summary>
	/// Scope key identifying the portfolio this limit set applies to (<c>dbo.RiskLimits.portfolio_id</c>).
	/// A <see langword="null"/> value is a wildcard that applies to all portfolios.
	/// </summary>
	public int? PortfolioId { get; init; }

	/// <summary>
	/// Scope key identifying the security this limit set applies to (<c>dbo.RiskLimits.security_id</c>).
	/// A <see langword="null"/> value is a wildcard that applies to all securities.
	/// </summary>
	public int? SecurityId { get; init; }

	/// <summary>
	/// Whether this limit set is active and therefore eligible for selection
	/// (<c>dbo.RiskLimits.is_active</c>). Defaults to <see langword="true"/>; inactive sets are ignored by
	/// <see cref="SelectMostSpecific"/>.
	/// </summary>
	public bool IsActive { get; init; } = true;

	/// <summary>
	/// Timestamp from which this limit set takes effect (<c>dbo.RiskLimits.effective_date</c>). Used as the
	/// most-recent-wins tie-break by <see cref="SelectMostSpecific"/>.
	/// </summary>
	public DateTime EffectiveDate { get; init; }

	/// <summary>
	/// The rolling order-frequency window expressed as a <see cref="TimeSpan"/>, projected from
	/// <see cref="MaxOrderFreqWindowSeconds"/>. Returns <see langword="null"/> when no window is configured.
	/// </summary>
	public TimeSpan? MaxOrderFreqWindow => MaxOrderFreqWindowSeconds is int s ? TimeSpan.FromSeconds(s) : null;

	/// <summary>
	/// The single, canonical "is this ceiling enforced?" rule, shared by every consumer of a
	/// <see cref="RiskLimitSet"/> (the pre-trade gate <see cref="PreTradeRiskService"/> and the
	/// circuit-breaker seed on <see cref="RiskManager"/>). A ceiling counts as enforced only when it
	/// is non-null <b>and</b> strictly positive; a <see langword="null"/> value OR a non-positive
	/// value (<c>0</c> or below) means "not enforced". This is the NULL/0 convention documented for
	/// the <c>dbo.RiskLimits</c> table (see <c>Database/001_Schema.sql</c>) and used by the existing
	/// <c>RiskRule</c> classes, expressed in one place so no consumer can drift from it.
	/// </summary>
	/// <remarks>
	/// <b>Intentional, AAP-sanctioned behaviour change (MA-16).</b> Treating a non-null <c>0</c> (or
	/// negative) ceiling as "not enforced" is a deliberate reconciliation, and it is the <em>second</em>
	/// documented behavioural change of this refactor (the first being the order-frequency tightening,
	/// AAP §0.6.1). The literal legacy proc <c>dbo.usp_ValidatePreTradeRisk</c> guarded each check only
	/// with <c>IF @max_x IS NOT NULL</c>, so a stored <c>0</c> ceiling made a comparison such as
	/// <c>price &gt;= 0</c> reject <em>every</em> order (an effectively unusable "block-all" state). The
	/// canonical model instead adopts the NULL/0 = "not enforced" convention that AAP §0.3.1 mandates
	/// carry over into <see cref="RiskLimitSet"/>, so a <c>0</c> ceiling disables that single check
	/// rather than blocking all orders. This is per-check: other populated ceilings still apply. The
	/// divergence is proven intentional (not a regression) by an explicit characterization test.
	/// </remarks>
	/// <param name="ceiling">The raw ceiling value read from the limit set.</param>
	/// <returns><see langword="true"/> when the ceiling is enforced (non-null and <c>&gt; 0</c>).</returns>
	public static bool IsCeilingEnforced(decimal? ceiling) => ceiling is decimal c && c > 0m;

	/// <summary>The enforced order-price ceiling, or <see langword="null"/> when not enforced (null/non-positive).</summary>
	public decimal? EffectiveMaxOrderPrice => IsCeilingEnforced(MaxOrderPrice) ? MaxOrderPrice : null;

	/// <summary>The enforced order-quantity ceiling, or <see langword="null"/> when not enforced (null/non-positive).</summary>
	public decimal? EffectiveMaxOrderQty => IsCeilingEnforced(MaxOrderQty) ? MaxOrderQty : null;

	/// <summary>The enforced order-notional-value ceiling, or <see langword="null"/> when not enforced (null/non-positive).</summary>
	public decimal? EffectiveMaxOrderValue => IsCeilingEnforced(MaxOrderValue) ? MaxOrderValue : null;

	/// <summary>The enforced post-fill position-size ceiling, or <see langword="null"/> when not enforced (null/non-positive).</summary>
	public decimal? EffectiveMaxPositionSize => IsCeilingEnforced(MaxPositionSize) ? MaxPositionSize : null;

	/// <summary>The enforced daily-volume ceiling, or <see langword="null"/> when not enforced (null/non-positive).</summary>
	public decimal? EffectiveMaxDailyVolume => IsCeilingEnforced(MaxDailyVolume) ? MaxDailyVolume : null;

	/// <summary>The enforced cumulative-commission ceiling, or <see langword="null"/> when not enforced (null/non-positive).</summary>
	public decimal? EffectiveMaxCommissionTotal => IsCeilingEnforced(MaxCommissionTotal) ? MaxCommissionTotal : null;

	/// <summary>
	/// Gets a value indicating whether the order-frequency limit is enforced. The count and window are
	/// validated <b>as a pair</b>: the limit is enforced only when <see cref="MaxOrderFreqCount"/> and
	/// <see cref="MaxOrderFreqWindowSeconds"/> are <b>both</b> present and strictly positive. A partial
	/// configuration (only one of the two set), a zero, or a negative value disables the check rather
	/// than producing a nonsensical always-reject or divide-by-window state.
	/// </summary>
	public bool IsFrequencyEnforced =>
		MaxOrderFreqCount is int c && c > 0 &&
		MaxOrderFreqWindowSeconds is int w && w > 0;

	/// <summary>
	/// Gets a value indicating whether the order-frequency configuration is <b>malformed</b>: exactly
	/// one of <see cref="MaxOrderFreqCount"/> and <see cref="MaxOrderFreqWindowSeconds"/> is a positive
	/// value while the other is null or non-positive. Such a half-specified pair expresses an intent to
	/// enforce a frequency limit that cannot be evaluated (a count with no window, or a window with no
	/// count), so it must be surfaced as a configuration error rather than silently disabled. When both
	/// are positive the limit is <see cref="IsFrequencyEnforced">enforced</see>; when both are
	/// null/non-positive the limit is coherently "not enforced" (the NULL/0 convention). Consumers that
	/// build rules from this set (for example <see cref="RiskManager.CreateRules(RiskLimitSet, RiskActions)"/>)
	/// throw when this is <see langword="true"/>.
	/// </summary>
	public bool IsFrequencyMalformed
	{
		get
		{
			var countActive = MaxOrderFreqCount is int c && c > 0;
			var windowActive = MaxOrderFreqWindowSeconds is int w && w > 0;
			return countActive != windowActive;
		}
	}

	/// <summary>
	/// Gets a value indicating whether none of the order/position/commission/frequency limits is enforced.
	/// </summary>
	/// <remarks>
	/// This is <see langword="true"/> only when every ceiling is "not enforced" under the single
	/// canonical <see cref="IsCeilingEnforced(decimal?)"/> rule (null OR non-positive) and the
	/// frequency pair is not enforced (<see cref="IsFrequencyEnforced"/> is <see langword="false"/>).
	/// It deliberately excludes <see cref="MaxOrderFreqWindowSeconds"/> and <see cref="CommissionRate"/>,
	/// which are not ceilings. This mirrors the branch of <c>dbo.usp_ValidatePreTradeRisk</c> where every
	/// threshold is NULL/0, so there is nothing to enforce and the order is accepted immediately;
	/// <c>PreTradeRiskService</c> uses it to short-circuit straight to ACCEPT.
	/// </remarks>
	public bool IsUnlimited =>
		!IsCeilingEnforced(MaxOrderPrice) &&
		!IsCeilingEnforced(MaxOrderQty) &&
		!IsCeilingEnforced(MaxOrderValue) &&
		!IsCeilingEnforced(MaxPositionSize) &&
		!IsCeilingEnforced(MaxDailyVolume) &&
		!IsCeilingEnforced(MaxCommissionTotal) &&
		!IsFrequencyEnforced;

	/// <summary>
	/// Selects the single most-specific active limit set for the given scope, mirroring the
	/// selection in dbo.usp_ValidatePreTradeRisk: portfolio+security rows outrank portfolio-only
	/// rows, which outrank security-only rows; ties are broken by the most recent
	/// <see cref="EffectiveDate"/>. Only <see cref="IsActive"/> rows whose scope keys match
	/// (or are null/wildcard) are considered.
	/// </summary>
	/// <param name="candidates">
	/// The candidate limit sets (e.g. the rows loaded from dbo.RiskLimits). Cannot be null; any
	/// <see langword="null"/> element is skipped defensively rather than throwing, so a sparse or
	/// partially-populated candidate collection produces a well-defined result instead of a
	/// <see cref="NullReferenceException"/>.
	/// </param>
	/// <param name="portfolioId">The portfolio identifier to match.</param>
	/// <param name="securityId">The security identifier to match.</param>
	/// <returns>The most-specific matching set, or null when none applies.</returns>
	public static RiskLimitSet SelectMostSpecific(IEnumerable<RiskLimitSet> candidates, int portfolioId, int securityId)
	{
		if (candidates is null)
			throw new ArgumentNullException(nameof(candidates));

		return candidates
			.Where(c => c is not null
				&& c.IsActive
				&& (c.PortfolioId == portfolioId || c.PortfolioId is null)
				&& (c.SecurityId == securityId || c.SecurityId is null))
			.OrderBy(c => c.PortfolioId is not null && c.SecurityId is not null ? 0
				: c.PortfolioId is not null ? 1
				: 2)
			.ThenByDescending(c => c.EffectiveDate)
			.FirstOrDefault();
	}
}
