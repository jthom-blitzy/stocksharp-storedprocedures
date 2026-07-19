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

	/// <summary>
	/// The maximum magnitude representable by the StockSharpLegacy money columns, which are all
	/// <c>DECIMAL(18,4)</c> (the largest 18-digit value with four fractional digits). Exposed so every
	/// consumer that must coerce a value to the persisted scale shares the same domain bound.
	/// </summary>
	public const decimal MoneyScaleMax = 99_999_999_999_999.9999m;

	/// <summary>
	/// Coerces a monetary value to the <c>DECIMAL(18,4)</c> domain of the StockSharpLegacy money columns,
	/// the single canonical normalization shared by every risk-enforcement pattern. The value is rounded
	/// to four fractional digits away from zero (matching SQL Server's arithmetic rounding when a value is
	/// assigned to a <c>DECIMAL(18,4)</c> column or variable) and an out-of-range magnitude fails
	/// deterministically, the C# analogue of the SQL arithmetic-overflow error. Both the pre-trade gate
	/// (<see cref="PreTradeRiskService"/>) and the stream circuit-breaker rules call this so their
	/// comparisons use the exact value that would be persisted, closing the sub-scale drift where stream
	/// enforcement could otherwise be less strict than the gate at the fourth-decimal boundary.
	/// </summary>
	/// <param name="value">The raw monetary value to coerce.</param>
	/// <param name="name">The name of the value, used in the overflow message.</param>
	/// <returns>The value rounded to <c>DECIMAL(18,4)</c> scale.</returns>
	/// <exception cref="OverflowException"><paramref name="value"/> rounds to a magnitude outside the <c>DECIMAL(18,4)</c> range.</exception>
	public static decimal NormalizeMoney(decimal value, string name)
	{
		var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);

		if (rounded > MoneyScaleMax || rounded < -MoneyScaleMax)
			throw new OverflowException($"{name} value {value} is outside the DECIMAL(18,4) range supported by the StockSharpLegacy schema.");

		return rounded;
	}

	/// <summary>
	/// Coerces a value to the <c>DECIMAL(18,4)</c> scale like <see cref="NormalizeMoney"/>, but
	/// <b>saturates</b> to &#177;<see cref="MoneyScaleMax"/> instead of throwing when the value is out of
	/// range. The stream circuit-breaker rules use this in their "breach when value &gt;= ceiling"
	/// comparisons so that an out-of-range input is coerced to the largest representable magnitude - which
	/// is necessarily at or above any finite ceiling and therefore treated as a breach - rather than raising
	/// mid-stream. This keeps the stream rules at least as strict as the gate at the fourth-decimal boundary
	/// (review finding CR-27) while never throwing on adversarial input during message processing.
	/// </summary>
	/// <param name="value">The raw value to coerce.</param>
	/// <returns>The value rounded to <c>DECIMAL(18,4)</c> scale, clamped to the representable range.</returns>
	public static decimal NormalizeMoneySaturating(decimal value)
	{
		var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);

		if (rounded > MoneyScaleMax)
			return MoneyScaleMax;

		if (rounded < -MoneyScaleMax)
			return -MoneyScaleMax;

		return rounded;
	}

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
	/// The single authoritative validator for a limit set, shared by <b>both</b> enforcement patterns so a
	/// malformed configuration fails closed identically in each. A <see langword="null"/> or <c>0</c>
	/// ceiling remains the AAP-mandated "not enforced" convention (see <see cref="IsCeilingEnforced(decimal?)"/>)
	/// and is accepted, but a <b>negative</b> ceiling, a negative frequency count/window, a negative
	/// <see cref="CommissionRate"/>, or a half-specified frequency pair
	/// (<see cref="IsFrequencyMalformed"/>) is a configuration error that this method rejects by throwing.
	/// This closes the fail-open hole where a negative stored value was silently treated as "not enforced"
	/// (which disabled the check) and reconciles the gate and the manager factory, which previously
	/// diverged (the gate silently ignored a malformed frequency pair while the factory threw).
	/// </summary>
	/// <remarks>
	/// This is a C#-layer guard by design: the SQL→C# consolidation relocates every risk decision out of
	/// the database (AAP §0.1.1, §0.7.1), so the authoritative rejection lives here in the business layer
	/// rather than as a <c>CHECK</c> constraint on <c>dbo.RiskLimits</c> (adding one would reintroduce a
	/// business rule into SQL and modify the frozen schema the refactor keeps as pure DDL, AAP §0.2.1).
	/// The pre-trade gate (<see cref="PreTradeRiskService"/>) calls this on every loaded row before it
	/// enforces, and the circuit-breaker factory (<see cref="RiskManager.CreateRules(RiskLimitSet, RiskActions)"/>)
	/// calls it before it builds rules, so no consumer can act on an invalid configuration.
	/// </remarks>
	/// <exception cref="ArgumentException">
	/// A ceiling, the frequency count/window, or the commission rate is negative, or exactly one of the
	/// frequency count/window is specified (a malformed pair).
	/// </exception>
	public void Validate()
	{
		static void RejectNegativeCeiling(decimal? value, string name)
		{
			if (value is decimal v && v < 0m)
				throw new ArgumentException(
					$"Malformed risk limit: {name} must not be negative (was {v.ToString(System.Globalization.CultureInfo.InvariantCulture)}). " +
					"Use null or 0 to leave the limit unenforced; a positive value to enforce it.", name);
		}

		RejectNegativeCeiling(MaxOrderPrice, nameof(MaxOrderPrice));
		RejectNegativeCeiling(MaxOrderQty, nameof(MaxOrderQty));
		RejectNegativeCeiling(MaxOrderValue, nameof(MaxOrderValue));
		RejectNegativeCeiling(MaxPositionSize, nameof(MaxPositionSize));
		RejectNegativeCeiling(MaxDailyVolume, nameof(MaxDailyVolume));
		RejectNegativeCeiling(MaxCommissionTotal, nameof(MaxCommissionTotal));

		if (MaxOrderFreqCount is int fc && fc < 0)
			throw new ArgumentException(
				$"Malformed risk limit: {nameof(MaxOrderFreqCount)} must not be negative (was {fc.ToString(System.Globalization.CultureInfo.InvariantCulture)}).",
				nameof(MaxOrderFreqCount));

		if (MaxOrderFreqWindowSeconds is int fw && fw < 0)
			throw new ArgumentException(
				$"Malformed risk limit: {nameof(MaxOrderFreqWindowSeconds)} must not be negative (was {fw.ToString(System.Globalization.CultureInfo.InvariantCulture)}).",
				nameof(MaxOrderFreqWindowSeconds));

		if (CommissionRate < 0m)
			throw new ArgumentException(
				$"Malformed risk limit: {nameof(CommissionRate)} must not be negative (was {CommissionRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}). " +
				"The legacy commission estimate used the configured rate directly, so a negative rate is a configuration error rather than a value to silently clamp.",
				nameof(CommissionRate));

		if (IsFrequencyMalformed)
			throw new ArgumentException(
				$"Malformed order-frequency limit: exactly one of count ({MaxOrderFreqCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}) " +
				$"and window-seconds ({MaxOrderFreqWindowSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}) is set. " +
				"Specify both (to enforce) or neither (to disable).", nameof(MaxOrderFreqCount));
	}

	/// <summary>
	/// Selects the single most-specific active limit set for the given scope, mirroring the
	/// selection in dbo.usp_ValidatePreTradeRisk: portfolio+security rows outrank portfolio-only
	/// rows, which outrank security-only rows; ties are broken by the most recent
	/// <see cref="EffectiveDate"/>. Only <see cref="IsActive"/> rows whose scope keys match
	/// (or are null/wildcard) are considered.
	/// </summary>
	/// <remarks>
	/// <b>Deterministic, fail-closed tie resolution (finding DB-P5-01).</b> When two or more equally
	/// specific rows share the <em>same</em> most-recent <see cref="EffectiveDate"/>, ordering by scope
	/// specificity and effective date alone leaves the winner undetermined: the previous implementation
	/// stopped at effective date and returned whichever row the candidate sequence (i.e. the database's
	/// arbitrary, index-order-dependent row order) yielded first, so the same dataset could select
	/// <c>max_order_price = 111</c> when inserted A→B and <c>222</c> when inserted B→A - a
	/// nondeterministic choice that could silently pick the <em>looser</em> limit. This is resolved by
	/// folding the tied rows into a single STRICTEST canonical set via <see cref="ComposeStrictest"/>:
	/// the result is identical regardless of input order (each field takes the strictest tied value, and
	/// min/max are order-independent) and is provably at-least-as-strict as every tied row, honouring the
	/// AAP mandate (§0.6.1, §0.7.1) that a reconciled rule "must never be less strict than the stricter of
	/// the two originals". A conflicting scope+effective-date tie is a configuration ambiguity, so failing
	/// toward the most restrictive interpretation is the correct fail-closed behaviour. When exactly one
	/// row wins outright (the ordinary case) it is returned unchanged, so this adds no behaviour change
	/// outside a genuine tie.
	/// </remarks>
	/// <remarks>
	/// When <paramref name="nowUtc"/> is supplied, rows whose <see cref="EffectiveDate"/> is in the
	/// future relative to that instant are excluded, so a limit set scheduled to take effect later cannot
	/// supersede the current row before its effective date. This is the injectable analogue of the
	/// <c>effective_date &lt;= SYSUTCDATETIME()</c> cutoff the pre-trade gate applies server-side, and it
	/// keeps the pure selection deterministic in unit tests (pass a fixed instant) while closing the
	/// future-effective activation hole. When <paramref name="nowUtc"/> is <see langword="null"/> no
	/// time cutoff is applied and selection depends only on scope specificity and recency, preserving the
	/// historical behaviour for callers that have already time-filtered their candidates (such as the gate,
	/// whose SQL <c>WHERE</c> clause performs the authoritative cutoff against the database clock).
	/// </remarks>
	/// <param name="candidates">
	/// The candidate limit sets (e.g. the rows loaded from dbo.RiskLimits). Cannot be null; any
	/// <see langword="null"/> element is skipped defensively rather than throwing, so a sparse or
	/// partially-populated candidate collection produces a well-defined result instead of a
	/// <see cref="NullReferenceException"/>.
	/// </param>
	/// <param name="portfolioId">The portfolio identifier to match.</param>
	/// <param name="securityId">The security identifier to match.</param>
	/// <param name="nowUtc">
	/// The current UTC instant used as the effective-date cutoff: rows with a later
	/// <see cref="EffectiveDate"/> are excluded. <see langword="null"/> (the default) applies no cutoff.
	/// </param>
	/// <returns>The most-specific matching set, or null when none applies.</returns>
	public static RiskLimitSet SelectMostSpecific(IEnumerable<RiskLimitSet> candidates, int portfolioId, int securityId, DateTime? nowUtc = null)
	{
		if (candidates is null)
			throw new ArgumentNullException(nameof(candidates));

		// Scope-specificity rank: portfolio+security (0) outranks portfolio-only (1), which outranks
		// security-only / global (2) - the same precedence dbo.usp_ValidatePreTradeRisk applied.
		static int SpecificityRank(RiskLimitSet c) =>
			c.PortfolioId is not null && c.SecurityId is not null ? 0
			: c.PortfolioId is not null ? 1
			: 2;

		var applicable = candidates
			.Where(c => c is not null
				&& c.IsActive
				&& (c.PortfolioId == portfolioId || c.PortfolioId is null)
				&& (c.SecurityId == securityId || c.SecurityId is null)
				&& (nowUtc is null || c.EffectiveDate <= nowUtc.Value))
			.ToList();

		if (applicable.Count == 0)
			return null;

		// The winning bucket is the most-specific rank and, within it, the most recent effective date -
		// exactly what OrderBy(rank).ThenByDescending(EffectiveDate) picked before. Computing it
		// explicitly lets us detect a residual tie (multiple rows sharing that rank AND date) instead of
		// silently taking the first in an arbitrary sequence.
		var bestRank = applicable.Min(SpecificityRank);
		var tier = applicable.Where(c => SpecificityRank(c) == bestRank).ToList();
		var bestDate = tier.Max(c => c.EffectiveDate);
		var winners = tier.Where(c => c.EffectiveDate == bestDate).ToList();

		// Ordinary case: a single row wins outright - return it unchanged (no behaviour change).
		if (winners.Count == 1)
			return winners[0];

		// DB-P5-01: an equally-specific, equally-dated tie. Resolve it deterministically and fail-closed
		// by composing the strictest tied row so selection never depends on candidate/row order and is
		// never less strict than any tied definition.
		return ComposeStrictest(winners, portfolioId, securityId, bestDate);
	}

	/// <summary>
	/// Folds a set of tied, equally-specific, equally-dated limit rows (finding DB-P5-01) into one
	/// canonical <see cref="RiskLimitSet"/> whose every threshold is the <b>strictest</b> among the tied
	/// rows. The result is deterministic (order-independent, because min/max are commutative) and provably
	/// at-least-as-strict as each input row, so a scope+date tie can never yield a looser limit than any of
	/// the definitions it reconciles.
	/// </summary>
	/// <remarks>
	/// Per-field strictness direction:
	/// <list type="bullet">
	/// <item><description>
	/// Ceilings (price, qty, notional value, position size, daily volume, commission total): the smallest
	/// <see cref="IsCeilingEnforced(decimal?)">enforced</see> value wins; a null/non-positive ("not
	/// enforced") value is ignored, and a ceiling no tied row enforces stays unenforced.
	/// </description></item>
	/// <item><description>
	/// Order-frequency: strictest is the smallest count paired with the largest window. A smaller count
	/// rejects at fewer orders and a longer window counts more orders, so <c>(minCount, maxWindow)</c>
	/// rejects at least whenever any individual <c>(count, window)</c> pair would. Only
	/// <see cref="IsFrequencyEnforced">enforced</see> pairs participate, and count/window are always
	/// carried together so the composed pair is never malformed.
	/// </description></item>
	/// <item><description>
	/// Commission rate: a <em>higher</em> rate is stricter because it inflates the pre-fill commission
	/// estimate (which then reaches <see cref="MaxCommissionTotal"/> sooner), so the maximum rate wins.
	/// </description></item>
	/// </list>
	/// The composed set carries the tied rows' shared scope (their <see cref="EffectiveDate"/> and the
	/// most-specific scope keys) and is <see cref="IsActive"/>; it therefore passes <see cref="Validate"/>
	/// like any real row.
	/// </remarks>
	/// <param name="tied">The two-or-more tied rows to reconcile. Must be non-empty.</param>
	/// <param name="portfolioId">The portfolio identifier the selection was made for.</param>
	/// <param name="securityId">The security identifier the selection was made for.</param>
	/// <param name="effectiveDate">The shared effective date of the tied rows.</param>
	/// <returns>A single canonical limit set that is the strictest reconciliation of the tied rows.</returns>
	private static RiskLimitSet ComposeStrictest(IReadOnlyList<RiskLimitSet> tied, int portfolioId, int securityId, DateTime effectiveDate)
	{
		// Strictest ceiling = the smallest ENFORCED value across the tied rows (null/non-positive means
		// "not enforced" and is skipped); stays null when no tied row enforces it.
		static decimal? StrictestCeiling(IReadOnlyList<RiskLimitSet> rows, Func<RiskLimitSet, decimal?> selector)
		{
			decimal? strictest = null;

			foreach (var row in rows)
			{
				var value = selector(row);

				if (IsCeilingEnforced(value) && (strictest is null || value.Value < strictest.Value))
					strictest = value;
			}

			return strictest;
		}

		// Frequency: min enforced count, max enforced window (see remarks). Both are set together or not
		// at all, so the composed pair is coherent (never a half-specified/malformed pair).
		int? strictestCount = null;
		int? strictestWindow = null;

		foreach (var row in tied)
		{
			if (!row.IsFrequencyEnforced)
				continue;

			var count = row.MaxOrderFreqCount.Value;
			var window = row.MaxOrderFreqWindowSeconds.Value;

			strictestCount = strictestCount is null ? count : Math.Min(strictestCount.Value, count);
			strictestWindow = strictestWindow is null ? window : Math.Max(strictestWindow.Value, window);
		}

		return new RiskLimitSet
		{
			// The composed set represents the winning tier's scope. Within a rank the portfolio key is
			// uniform; the security key is the concrete securityId when any tied row is security-scoped,
			// otherwise the null/global wildcard.
			PortfolioId = tied[0].PortfolioId is null ? null : portfolioId,
			SecurityId = tied.Any(r => r.SecurityId is not null) ? securityId : null,
			IsActive = true,
			EffectiveDate = effectiveDate,

			MaxOrderPrice = StrictestCeiling(tied, r => r.MaxOrderPrice),
			MaxOrderQty = StrictestCeiling(tied, r => r.MaxOrderQty),
			MaxOrderValue = StrictestCeiling(tied, r => r.MaxOrderValue),
			MaxPositionSize = StrictestCeiling(tied, r => r.MaxPositionSize),
			MaxDailyVolume = StrictestCeiling(tied, r => r.MaxDailyVolume),
			MaxCommissionTotal = StrictestCeiling(tied, r => r.MaxCommissionTotal),

			MaxOrderFreqCount = strictestCount,
			MaxOrderFreqWindowSeconds = strictestWindow,

			// Higher commission rate = stricter estimate (see remarks); take the maximum.
			CommissionRate = tied.Max(r => r.CommissionRate),
		};
	}
}
