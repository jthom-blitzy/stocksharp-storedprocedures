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
	/// (<c>dbo.RiskLimits.commission_rate</c>). Non-nullable; the default of <c>0.0005</c> mirrors the
	/// <c>DF_RiskLimits_commission_rate</c> database default.
	/// </summary>
	public decimal CommissionRate { get; init; } = 0.0005m;

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
	/// Gets a value indicating whether none of the seven order/position/commission ceilings is enforced.
	/// </summary>
	/// <remarks>
	/// This is <see langword="true"/> only when <see cref="MaxOrderPrice"/>, <see cref="MaxOrderQty"/>,
	/// <see cref="MaxOrderValue"/>, <see cref="MaxPositionSize"/>, <see cref="MaxDailyVolume"/>,
	/// <see cref="MaxOrderFreqCount"/> and <see cref="MaxCommissionTotal"/> are all <see langword="null"/>.
	/// It deliberately excludes <see cref="MaxOrderFreqWindowSeconds"/> and <see cref="CommissionRate"/>,
	/// which are not ceilings. This mirrors the branch of <c>dbo.usp_ValidatePreTradeRisk</c> where every
	/// threshold is NULL, so there is nothing to enforce and the order is accepted immediately;
	/// <c>PreTradeRiskService</c> uses it to short-circuit straight to ACCEPT.
	/// </remarks>
	public bool IsUnlimited =>
		MaxOrderPrice is null &&
		MaxOrderQty is null &&
		MaxOrderValue is null &&
		MaxPositionSize is null &&
		MaxDailyVolume is null &&
		MaxOrderFreqCount is null &&
		MaxCommissionTotal is null;

	/// <summary>
	/// Selects the single most-specific active limit set for the given scope, mirroring the
	/// selection in dbo.usp_ValidatePreTradeRisk: portfolio+security rows outrank portfolio-only
	/// rows, which outrank security-only rows; ties are broken by the most recent
	/// <see cref="EffectiveDate"/>. Only <see cref="IsActive"/> rows whose scope keys match
	/// (or are null/wildcard) are considered.
	/// </summary>
	/// <param name="candidates">The candidate limit sets (e.g. the rows loaded from dbo.RiskLimits). Cannot be null.</param>
	/// <param name="portfolioId">The portfolio identifier to match.</param>
	/// <param name="securityId">The security identifier to match.</param>
	/// <returns>The most-specific matching set, or null when none applies.</returns>
	public static RiskLimitSet SelectMostSpecific(IEnumerable<RiskLimitSet> candidates, int portfolioId, int securityId)
	{
		if (candidates is null)
			throw new ArgumentNullException(nameof(candidates));

		return candidates
			.Where(c => c.IsActive
				&& (c.PortfolioId == portfolioId || c.PortfolioId is null)
				&& (c.SecurityId == securityId || c.SecurityId is null))
			.OrderBy(c => c.PortfolioId is not null && c.SecurityId is not null ? 0
				: c.PortfolioId is not null ? 1
				: 2)
			.ThenByDescending(c => c.EffectiveDate)
			.FirstOrDefault();
	}
}
