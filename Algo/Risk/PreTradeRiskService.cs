namespace StockSharp.Algo.Risk;

using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Outcome of a pre-trade risk validation: whether the order is accepted and, when rejected, the
/// human-readable reason. Mirrors the { IsValid, RejectReason } shape the gateway maps onto
/// <c>SqlOrderSubmitResult</c> (which additionally carries the generated OrderId).
/// </summary>
public sealed class PreTradeRiskResult
{
	/// <summary>Whether the order passed every configured pre-trade check.</summary>
	public bool IsValid { get; init; }

	/// <summary>The rejection reason when <see cref="IsValid"/> is false; otherwise <see langword="null"/>.</summary>
	public string RejectReason { get; init; }

	/// <summary>A shared accepted result.</summary>
	public static PreTradeRiskResult Valid { get; } = new() { IsValid = true, RejectReason = null };

	/// <summary>Builds a rejected result carrying <paramref name="reason"/>.</summary>
	/// <param name="reason">The human-readable rejection reason.</param>
	/// <returns>A rejected <see cref="PreTradeRiskResult"/> whose <see cref="IsValid"/> is <see langword="false"/>.</returns>
	public static PreTradeRiskResult Reject(string reason) => new() { IsValid = false, RejectReason = reason };
}

/// <summary>
/// Fully-resolved input to the pure pre-trade GATE decision <see cref="PreTradeRiskService.EvaluateGate"/>:
/// the effective (already most-specific-selected) risk limits, the prospective order, and the
/// database-derived aggregates the seven checks consume. It carries NO Npgsql/provider type, so the pure
/// decision core is database-agnostic and CLS-compliant, and the staged parity tests can populate it from
/// EITHER engine (SQL Server for Step 2, PostgreSQL for Step 3) and evaluate the identical logic.
/// </summary>
/// <remarks>
/// Every ceiling follows the frozen "NULL-or-zero = not enforced" convention (AAP 0.6.4). All quantities,
/// prices and money values are <see cref="decimal"/> to preserve the schema's NUMERIC(18,4)/(9,6) scale;
/// <see cref="Qty"/> and <see cref="Price"/> are expected already quantised to NUMERIC(18,4) by the caller.
/// </remarks>
public readonly struct GateInputs
{
	/// <summary>Order side, "B" (buy) or "S" (sell); drives the signed position delta in the position-size check.</summary>
	public string Side { get; init; }

	/// <summary>Prospective order quantity, already quantised to NUMERIC(18,4) and strictly positive.</summary>
	public decimal Qty { get; init; }

	/// <summary>Prospective order price already quantised to NUMERIC(18,4), or <see langword="null"/> for a market order.</summary>
	public decimal? Price { get; init; }

	/// <summary>Ceiling on a single order's price (mirrors RiskOrderPriceRule); NULL or 0 = unlimited.</summary>
	public decimal? MaxOrderPrice { get; init; }

	/// <summary>Ceiling on a single order's quantity (mirrors RiskOrderVolumeRule); NULL or 0 = unlimited.</summary>
	public decimal? MaxOrderQty { get; init; }

	/// <summary>Ceiling on the notional qty*price value (SQL-only check promoted to C#); NULL or 0 = unlimited.</summary>
	public decimal? MaxOrderValue { get; init; }

	/// <summary>Ceiling on the absolute hypothetical post-fill position (shares RiskPositionSizeRule's threshold); NULL or 0 = unlimited.</summary>
	public decimal? MaxPositionSize { get; init; }

	/// <summary>Ceiling on cumulative same-day traded volume (SQL-only check promoted to C#); NULL or 0 = unlimited.</summary>
	public decimal? MaxDailyVolume { get; init; }

	/// <summary>Maximum orders permitted within <see cref="MaxOrderFreqWindowSec"/> (mirrors RiskOrderFreqRule.Count); NULL or 0 = unlimited.</summary>
	public int? MaxOrderFreqCount { get; init; }

	/// <summary>Rolling frequency window length in seconds (mirrors RiskOrderFreqRule.Interval); paired with <see cref="MaxOrderFreqCount"/>.</summary>
	public int? MaxOrderFreqWindowSec { get; init; }

	/// <summary>Ceiling on the pre-fill cumulative commission ESTIMATE; NULL or 0 = unlimited.</summary>
	public decimal? MaxCommissionTotal { get; init; }

	/// <summary>Commission rate (NUMERIC(9,6)) used to estimate commission; kept at full scale (it is a rate, not a ceiling).</summary>
	public decimal? CommissionRate { get; init; }

	/// <summary>Count of the portfolio's orders already inside the trailing frequency window (excludes the prospective order).</summary>
	public long RecentOrderCount { get; init; }

	/// <summary>Current signed position quantity for (portfolio, security); 0 when no position row exists yet.</summary>
	public decimal CurrentPositionQty { get; init; }

	/// <summary>Existing gross notional over the portfolio (exact SUM(t.qty * t.price) over the portfolio's trades); 0 when none.</summary>
	public decimal ExistingGrossNotional { get; init; }

	/// <summary>Best-effort last traded price for the security, used only to estimate a market order's commission; may be <see langword="null"/>.</summary>
	public decimal? LastTradePrice { get; init; }

	/// <summary>Cumulative same-day accepted/filled/part-filled order quantity for (portfolio, security); 0 when none.</summary>
	public decimal TodayVolume { get; init; }
}

/// <summary>
/// Canonical per-order pre-trade risk GATE: the single C# source of truth for the accept/reject
/// decision that the retired SQL procedure <c>dbo.usp_ValidatePreTradeRisk</c> used to own (see
/// <c>Database/002_StoredProcedures.sql</c>, which this refactor deletes). It evaluates the seven
/// configured ceilings against the ONE order being submitted and rejects that specific order the
/// moment a ceiling is met or exceeded.
/// </summary>
/// <remarks>
/// <para>
/// This gate is deliberately DISTINCT from the <see cref="RiskManager"/> circuit breaker. The circuit
/// breaker is a portfolio-wide safety net: a tripped rule fires ClosePositions / StopTrading /
/// CancelOrders against the whole portfolio as messages flow through <see cref="RiskMessageAdapter"/>,
/// and it never rejects the specific order that tripped it. This service is the opposite model - a
/// classic pre-trade gate that blocks the single order before it is ever accepted. Both patterns are
/// preserved. The genuinely shared definition is the rolling ORDER-FREQUENCY evaluator in
/// <see cref="CanonicalRiskRules"/>, consumed by BOTH this gate and <see cref="RiskOrderFreqRule"/> -
/// that is what removes their former divergence (AAP 0.6.1). This gate additionally applies the canonical
/// enabled-limit / "meets or exceeds" ceiling convention from <see cref="CanonicalRiskRules"/>. The
/// circuit-breaker price / quantity / position / commission rules are NOT rewritten: they keep their own
/// threshold values and evaluate their own context-specific subjects (different by design, AAP 0.6.2), so
/// this service does not claim to share those threshold definitions with them.
/// </para>
/// <para>
/// Unlike the pure in-memory circuit-breaker rules, this gate is DATABASE-STATE-AWARE: it reads the
/// most-specific <c>risklimits</c> row, the current <c>positions</c> row, recent and same-day
/// <c>orders</c>, and (for a market order) the last <c>trades</c> price. It runs those reads on an
/// ALREADY-OPEN connection AND the in-flight transaction supplied by the gateway, so the validation
/// observes the same snapshot as - and is serialized with - the caller's order INSERT; it never opens,
/// closes, or disposes the connection and never begins, commits, or rolls back the transaction (the
/// gateway owns both lifecycles). Every accept/reject decision stays inside this service; the gateway
/// is a pure relay of the returned <see cref="PreTradeRiskResult"/>.
/// </para>
/// <para>
/// All money / quantity / price arithmetic uses <see cref="decimal"/> (never <c>double</c>/<c>float</c>)
/// to preserve the schema's <c>NUMERIC(18,4)</c> / <c>NUMERIC(9,6)</c> scale, so a "meets or exceeds"
/// comparison can never silently loosen (hard NFR, AAP 0.6.4). The "&gt;=" rejection semantics are
/// ported from the SQL procedure exactly; the "NULL-or-zero limit = not enforced" convention follows
/// the frozen AAP (0.6.4), which unifies the null/zero meaning across the SQL and C# sides.
/// </para>
/// </remarks>
public sealed class PreTradeRiskService
{
	// Injectable UTC clock: mirrors the SQL SYSUTCDATETIME() / Postgres now() at time zone 'utc' time
	// source the original proc used for the rolling frequency window and the daily-volume day boundary,
	// and makes the gate deterministic under the parity tests.
	private readonly Func<DateTime> _utcNow;

	/// <summary>
	/// Initializes a new instance of the <see cref="PreTradeRiskService"/> class.
	/// </summary>
	/// <param name="utcNow">
	/// Optional UTC clock used for the rolling frequency window and the daily-volume day boundary. When
	/// <see langword="null"/> the service uses <see cref="DateTime.UtcNow"/>. Supplying a fixed clock
	/// makes the parity tests deterministic.
	/// </param>
	public PreTradeRiskService(Func<DateTime> utcNow = null)
	{
		_utcNow = utcNow ?? (() => DateTime.UtcNow);
	}

	/// <summary>
	/// Pure, database-free core of the pre-trade GATE decision: given a fully-resolved <see cref="GateInputs"/>
	/// (the effective limits, the prospective order, and the database-derived aggregates the checks consume),
	/// evaluates the seven configured ceilings in the SAME order as the retired <c>dbo.usp_ValidatePreTradeRisk</c>
	/// procedure and returns the accept/reject outcome, rejecting on the FIRST breached ceiling.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This method is the single source of truth for the gate's accept/reject logic. <see cref="ValidateAsync"/>
	/// is its only production caller: it reads the required state from PostgreSQL and delegates here. Extracting
	/// the decision from the data access lets the staged parity tests (AAP 0.6.3) drive this EXACT logic with the
	/// database ENGINE held constant - state read from SQL Server (Step 2) versus PostgreSQL (Step 3) - so a
	/// logic regression and an engine/dialect regression stay on separate, attributable axes. It is intentionally
	/// free of any Npgsql type (hence CLS-compliant and directly callable from the test assembly) and never
	/// touches the database or the clock: every value it needs is supplied on <paramref name="g"/>.
	/// </para>
	/// <para>
	/// The "&gt;=" meets-or-exceeds semantics, the "NULL-or-zero limit = not enforced" convention and the
	/// <see cref="decimal"/>-only money arithmetic are applied through the shared <see cref="CanonicalRiskRules"/>
	/// helpers exactly as the SQL procedure applied them, so the comparison can never silently loosen (hard NFR,
	/// AAP 0.6.4). When no ceiling is enabled every guarded check is skipped and the order is accepted - the same
	/// outcome as the "no configured limits" early-out in <see cref="ValidateAsync"/>.
	/// </para>
	/// </remarks>
	/// <param name="g">The resolved limits, prospective order, and database-derived aggregates to evaluate.</param>
	/// <returns>
	/// <see cref="PreTradeRiskResult.Valid"/> when every enabled check passes; otherwise a rejected result
	/// carrying the reason for the first breached check.
	/// </returns>
	public static PreTradeRiskResult EvaluateGate(in GateInputs g)
	{
		// --- 0. Structural guard: a supplied price must be strictly positive (M11) ---
		// Mirrors the CK_Orders_price schema constraint and the ValidateAsync guard, so a zero/negative price
		// is rejected before any ceiling check and can never bypass the price / notional / commission gates.
		// A market order (Price is null) has no price and is unaffected.
		if (g.Price is not null && g.Price.Value <= 0m)
			return PreTradeRiskResult.Reject($"Invalid price {g.Price.Value.To<string>()}");

		// --- 1. Order price ceiling (mirrors RiskOrderPriceRule; rejects when price >= limit) ---
		// Shared CanonicalRiskRules.MeetsOrExceeds embeds the canonical enabled-limit guard (present AND > 0,
		// so NULL or 0 = unlimited), so a breach guarantees MaxOrderPrice.HasValue.
		if (g.Price is not null && CanonicalRiskRules.MeetsOrExceeds(g.Price.Value, g.MaxOrderPrice))
			return PreTradeRiskResult.Reject(
				$"Order price {g.Price.Value.To<string>()} meets/exceeds limit {g.MaxOrderPrice.Value.To<string>()}");

		// --- 2. Order qty ceiling (mirrors RiskOrderVolumeRule; rejects when qty >= limit) ---
		// Canonical MeetsOrExceeds: NULL or 0 max_order_qty = unlimited; positive => ">=" rejects.
		if (CanonicalRiskRules.MeetsOrExceeds(g.Qty, g.MaxOrderQty))
			return PreTradeRiskResult.Reject(
				$"Order qty {g.Qty.To<string>()} meets/exceeds limit {g.MaxOrderQty.Value.To<string>()}");

		// --- 3. Notional order value ceiling (qty * price) - SQL-only check promoted to a first-class C# gate rule ---
		// Only meaningful when a price is supplied (a market order has no ex-ante notional), matching the SQL
		// "@price IS NOT NULL". Product stays decimal and the canonical MeetsOrExceeds keeps ">=" from loosening.
		if (g.Price is not null && CanonicalRiskRules.MeetsOrExceeds(g.Qty * g.Price.Value, g.MaxOrderValue))
			return PreTradeRiskResult.Reject(
				$"Order value {(g.Qty * g.Price.Value).To<string>()} meets/exceeds limit {g.MaxOrderValue.Value.To<string>()}");

		// --- 4. Order frequency (mirrors RiskOrderFreqRule) ---
		// IsFrequencyEnabled: enforced only when BOTH count and window are present and > 0. The "recentCount + 1"
		// (prospective order) lives in the shared bounded-count canonical evaluator so this gate and
		// RiskOrderFreqRule can never disagree on a frequency decision (AAP 0.6.1).
		if (CanonicalRiskRules.IsFrequencyEnabled(g.MaxOrderFreqCount, g.MaxOrderFreqWindowSec)
			&& CanonicalRiskRules.IsOrderFrequencyBreached(g.RecentOrderCount, g.MaxOrderFreqCount.Value))
			return PreTradeRiskResult.Reject(
				$"Order frequency {(g.RecentOrderCount + 1).To<string>()} in {g.MaxOrderFreqWindowSec.Value.To<string>()}s meets/exceeds limit {g.MaxOrderFreqCount.Value.To<string>()}");

		// --- 5. Resulting position size ceiling (shares RiskPositionSizeRule's threshold) ---
		// GATE subject is the HYPOTHETICAL post-fill position (current qty + signed order qty). This differs from
		// RiskPositionSizeRule, which checks the CURRENT position from PositionChangeMessage.CurrentValue. Only the
		// threshold + ">=" direction are shared; the gate keeps the hypothetical subject on purpose, because its
		// job is to block the order BEFORE it is accepted (AAP 0.6.2).
		if (CanonicalRiskRules.IsCeilingEnabled(g.MaxPositionSize))
		{
			var signedDelta = g.Side == "B" ? g.Qty : -g.Qty; // 'B' adds, 'S' subtracts (SQL side sign).
			var resulting = g.CurrentPositionQty + signedDelta;

			// MeetsOrExceeds embeds the enabled-limit guard, so MaxPositionSize.HasValue holds inside the breach.
			if (CanonicalRiskRules.MeetsOrExceeds(Math.Abs(resulting), g.MaxPositionSize))
				return PreTradeRiskResult.Reject(
					$"Resulting position {resulting.To<string>()} meets/exceeds limit {g.MaxPositionSize.Value.To<string>()}");
		}

		// --- 6. Cumulative commission ceiling (pre-fill ESTIMATE) ---
		// A forecast, intentionally NOT merged with the circuit breaker's realized-commission rules
		// (RiskCommissionRule / RiskOrderCommissionRule / RiskTransactionCommissionRule), which accumulate ACTUAL
		// ExecutionMessage.Commission AFTER the fill. A forecast and a realized figure will not agree, so BOTH are
		// preserved (different by design, AAP 0.6.2). Quantise the summed notional and the final estimate to
		// NUMERIC(18,4) before the ">=" compare (F5); the rate keeps its full NUMERIC(9,6) scale. A market order
		// with no last trade contributes 0 (ISNULL(@estPrice, 0) parity). All decimal, so ">=" cannot loosen.
		if (CanonicalRiskRules.IsCeilingEnabled(g.MaxCommissionTotal))
		{
			var rate = g.CommissionRate ?? 0m; // commission_rate is NOT NULL in the schema, so present when a limit row exists.
			var estPrice = g.Price ?? g.LastTradePrice; // quantised limit price, else the market fallback (already 4-dp).
			var existingNotional = CanonicalRiskRules.QuantizeToScale(g.ExistingGrossNotional);
			var est = CanonicalRiskRules.QuantizeToScale(existingNotional * rate + g.Qty * (estPrice ?? 0m) * rate);
			if (CanonicalRiskRules.MeetsOrExceeds(est, g.MaxCommissionTotal))
				return PreTradeRiskResult.Reject(
					$"Estimated cumulative commission {est.To<string>()} meets/exceeds limit {g.MaxCommissionTotal.Value.To<string>()}");
		}

		// --- 7. Daily traded volume ceiling - SQL-only check promoted to a first-class C# gate rule ---
		// Canonical IsCeilingEnabled: NULL or 0 max_daily_volume = unlimited (kept as an explicit guard even though
		// MeetsOrExceeds re-checks it, mirroring the original block structure).
		if (CanonicalRiskRules.IsCeilingEnabled(g.MaxDailyVolume)
			&& CanonicalRiskRules.MeetsOrExceeds(g.TodayVolume + g.Qty, g.MaxDailyVolume))
			return PreTradeRiskResult.Reject(
				$"Daily volume {(g.TodayVolume + g.Qty).To<string>()} meets/exceeds limit {g.MaxDailyVolume.Value.To<string>()}");

		// Every configured check passed - accept the order.
		return PreTradeRiskResult.Valid;
	}

	/// <summary>
	/// Validates a single prospective order against every configured pre-trade ceiling and returns the
	/// accept/reject outcome. A faithful C# port of the retired <c>dbo.usp_ValidatePreTradeRisk</c>
	/// procedure: the seven checks run in the SAME order, each rejects immediately on the first breach,
	/// and each uses the "&gt;=" meets-or-exceeds comparison with the "NULL-or-zero = not enforced" convention.
	/// </summary>
	/// <param name="connection">
	/// An already-open <see cref="NpgsqlConnection"/> owned by the caller. This service never opens,
	/// closes, or disposes it.
	/// </param>
	/// <param name="transaction">
	/// The caller's in-flight <see cref="NpgsqlTransaction"/> on <paramref name="connection"/>. Every read
	/// this gate performs runs on it, so the validation observes the same snapshot as - and is serialized
	/// with - the caller's subsequent order INSERT; this service NEVER begins, commits, or rolls it back
	/// (the gateway owns that lifecycle).
	/// </param>
	/// <param name="portfolioId">The portfolio the order is submitted for (<c>orders.portfolio_id</c>).</param>
	/// <param name="securityId">The security the order is for (<c>orders.security_id</c>).</param>
	/// <param name="side">Order side, already mapped to <c>"B"</c> (buy) or <c>"S"</c> (sell) by the caller.</param>
	/// <param name="qty">Order quantity; must be non-null and strictly positive.</param>
	/// <param name="price">Order price, or <see langword="null"/> for a market order.</param>
	/// <param name="cancellationToken">Token used to cancel the database reads.</param>
	/// <returns>
	/// <see cref="PreTradeRiskResult.Valid"/> when every configured check passes; otherwise a rejected
	/// result carrying the reason for the first breached check.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="transaction"/> is <see langword="null"/>.</exception>
	// order_type and requested_by from the original SQL signature are intentionally OMITTED: order_type is
	// unused by every check (market vs limit is detected by price == null), and requested_by was a descoped
	// compliance tag the proc never read. Dropping them keeps this gate's contract to exactly what it uses.
	//
	// The assembly is [CLSCompliant(true)] (common_meta.props), but the Npgsql provider types are not
	// CLS-compliant. Surfacing the gateway's open NpgsqlConnection/NpgsqlTransaction here is intentional (this
	// gate is database-state-aware and must run on the gateway's connection/transaction), so opt this member
	// out of CLS checking, matching PositionRecalculationService.ApplyAsync and the repository convention.
	[CLSCompliant(false)]
	public async Task<PreTradeRiskResult> ValidateAsync(
		NpgsqlConnection connection,
		NpgsqlTransaction transaction,
		int portfolioId,
		int securityId,
		string side,
		decimal? qty,
		decimal? price,
		CancellationToken cancellationToken = default)
	{
		if (connection is null)
			throw new ArgumentNullException(nameof(connection));

		if (transaction is null)
			throw new ArgumentNullException(nameof(transaction));

		// One UTC instant for the whole validation (the SQL proc used a single SYSUTCDATETIME()).
		// The Postgres columns are `timestamp WITHOUT time zone` holding UTC; Npgsql rejects a DateTime
		// with Kind=Utc bound to such a column, and reads them back as Kind=Unspecified. Normalise to
		// Unspecified so every DB comparison and the rolling evaluator run on one consistent timeline.
		var now = DateTime.SpecifyKind(_utcNow(), DateTimeKind.Unspecified);

		// --- Guards (mirror the SQL RETURNs that run before any RiskLimits lookup) ---
		if (side != "B" && side != "S")
			return PreTradeRiskResult.Reject($"Invalid side: {side}");

		if (qty is null)
			return PreTradeRiskResult.Reject("Invalid qty");

		// F5: quantise to the schema's NUMERIC(18,4) scale FIRST, before the qty <= 0 guard. The retired proc
		// declared @qty as DECIMAL(18,4), so a value such as 0.00005 was coerced to 0.0001 (and 0.00004 to 0.0000)
		// on binding - BEFORE its own qty <= 0 guard - and every subsequent comparison ran on that coerced value.
		// Matching that here makes the gate's accept/reject decision on exactly the value Postgres will persist into
		// the NUMERIC(18,4) column, so a >4-dp input can no longer be accepted on its full-precision value yet stored
		// at (or above) a ceiling it should have met. Away-from-zero rounding is never LESS strict (hard NFR, AAP 0.6.4).
		var q = CanonicalRiskRules.QuantizeToScale(qty.Value);

		if (q <= 0m)
			return PreTradeRiskResult.Reject("Invalid qty");

		// Quantise the limit price to the same NUMERIC(18,4) scale (a market order has no price => null). Every
		// downstream check and reject message uses this quantised price, not the raw parameter, for the same
		// decision-matches-persistence reason (F5).
		var priceQ = price.HasValue ? (decimal?)CanonicalRiskRules.QuantizeToScale(price.Value) : null;

		// M11: a supplied price must be strictly positive. Guard here (before any RiskLimits read), mirroring
		// the qty guard and the CK_Orders_price schema constraint, so a zero/negative price is rejected up
		// front rather than silently bypassing the price / notional / commission ceilings. A market order
		// (priceQ is null) is unaffected. EvaluateGate re-checks this so the pure-logic parity tests cover it too.
		if (priceQ is not null && priceQ.Value <= 0m)
			return PreTradeRiskResult.Reject($"Invalid price {priceQ.Value.To<string>()}");

		// --- Most-specific RiskLimits row selection ---
		// Prefer portfolio+security, then portfolio-only, then security-only; newest effective_date wins.
		// Every ceiling is read into a NULLABLE local; when no row matches they all stay null (nothing enforced).
		decimal? maxOrderPrice = null;
		decimal? maxOrderQty = null;
		decimal? maxOrderValue = null;
		decimal? maxPositionSize = null;
		decimal? maxDailyVolume = null;
		decimal? maxCommissionTotal = null;
		decimal? commissionRate = null;
		int? maxOrderFreqCount = null;
		int? maxOrderFreqWindowSec = null;

		// Postgres dialect: unquoted lowercase table (risklimits), snake_case columns, boolean literal TRUE,
		// dbo. qualifier dropped, TOP(1)/ORDER BY -> ORDER BY ... LIMIT 1. Named parameters bind via NpgsqlParameter.
		const string limitsSql =
			"SELECT max_order_price, max_order_qty, max_order_value, max_position_size, " +
			"max_daily_volume, max_order_freq_count, max_order_freq_window_sec, " +
			"max_commission_total, commission_rate " +
			"FROM risklimits " +
			"WHERE is_active = TRUE " +
			"AND (portfolio_id = @portfolio_id OR portfolio_id IS NULL) " +
			"AND (security_id = @security_id OR security_id IS NULL) " +
			"ORDER BY " +
			"CASE WHEN portfolio_id IS NOT NULL AND security_id IS NOT NULL THEN 0 " +
			"WHEN portfolio_id IS NOT NULL THEN 1 " +
			"ELSE 2 END, " +
			"effective_date DESC, " +
			// Deterministic final tie-break: two equal-specificity rows with the SAME effective_date would
			// otherwise return an arbitrary winner. risk_limit_id DESC picks the most recently inserted row.
			"risk_limit_id DESC " +
			"LIMIT 1";

		using (var limitsCommand = new NpgsqlCommand(limitsSql, connection, transaction))
		{
			limitsCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
			limitsCommand.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });

			// No MARS on a single Npgsql connection: fully read and dispose this reader before the next command.
			await using var reader = await limitsCommand.ExecuteReaderAsync(cancellationToken);

			if (await reader.ReadAsync(cancellationToken))
			{
				// NUMERIC(18,4)/(9,6) -> decimal via GetDecimal (never GetDouble) to preserve scale; INT -> GetInt32.
				maxOrderPrice = reader.IsDBNull(0) ? (decimal?)null : reader.GetDecimal(0);
				maxOrderQty = reader.IsDBNull(1) ? (decimal?)null : reader.GetDecimal(1);
				maxOrderValue = reader.IsDBNull(2) ? (decimal?)null : reader.GetDecimal(2);
				maxPositionSize = reader.IsDBNull(3) ? (decimal?)null : reader.GetDecimal(3);
				maxDailyVolume = reader.IsDBNull(4) ? (decimal?)null : reader.GetDecimal(4);
				maxOrderFreqCount = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
				maxOrderFreqWindowSec = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
				maxCommissionTotal = reader.IsDBNull(7) ? (decimal?)null : reader.GetDecimal(7);
				commissionRate = reader.IsDBNull(8) ? (decimal?)null : reader.GetDecimal(8);
			}
		}

		// No ENFORCED ceilings at all => nothing to check (a NULL or 0 limit means "unlimited", the AAP 0.6.4
		// convention the C# RiskRule classes also use). Evaluated through the canonical enabled-limit predicates
		// so this early-out uses the SAME null-or-zero-is-unlimited rule as the checks below (F3). Note that
		// max_order_freq_window_sec and commission_rate are not tested on their own: a window or a rate alone
		// enforces nothing without its paired ceiling (IsFrequencyEnabled requires BOTH count and window; the
		// commission check requires max_commission_total).
		if (!CanonicalRiskRules.IsCeilingEnabled(maxOrderPrice)
			&& !CanonicalRiskRules.IsCeilingEnabled(maxOrderQty)
			&& !CanonicalRiskRules.IsCeilingEnabled(maxOrderValue)
			&& !CanonicalRiskRules.IsCeilingEnabled(maxPositionSize)
			&& !CanonicalRiskRules.IsCeilingEnabled(maxDailyVolume)
			&& !CanonicalRiskRules.IsFrequencyEnabled(maxOrderFreqCount, maxOrderFreqWindowSec)
			&& !CanonicalRiskRules.IsCeilingEnabled(maxCommissionTotal))
			return PreTradeRiskResult.Valid;

		// --- Read the database-derived aggregates the enabled checks need, THEN decide ---
		// The seven-check accept/reject DECISION now lives in the pure, database-free EvaluateGate, so the
		// staged parity tests (AAP 0.6.3) can drive that EXACT logic with the database ENGINE held constant.
		// This method keeps the DATA-ACCESS half: it reads each aggregate only when the check that consumes
		// it is enabled (same queries, cutoff and day-boundary math as before), then hands the fully-gathered
		// state to EvaluateGate. One deliberate, decision-NEUTRAL change from the original inline form: an
		// earlier breached ceiling no longer short-circuits the remaining reads (state is gathered up front).
		// EvaluateGate still returns on the FIRST breach in the exact same order, so the accept/reject outcome
		// and reject reason are identical; only a few extra bounded, index-friendly reads may run on an order
		// that will be rejected anyway. None of these reads can throw on an empty result (each defaults to
		// 0/null), and all run on the caller's transaction snapshot.

		// 4. order frequency: BOUNDED COUNT(*) of the orders already inside the trailing window (only the
		//    count crosses the wire, never the rows). Inclusive lower bound matches the proc's DATEADD cutoff.
		var recentOrderCount = 0L;
		if (CanonicalRiskRules.IsFrequencyEnabled(maxOrderFreqCount, maxOrderFreqWindowSec))
		{
			var window = TimeSpan.FromSeconds(maxOrderFreqWindowSec.Value);
			var cutoff = now - window; // Kind=Unspecified, matches the `timestamp` values Npgsql reads back.

			using var freqCommand = new NpgsqlCommand(
				"SELECT COUNT(*) FROM orders WHERE portfolio_id = @portfolio_id AND submitted_date >= @cutoff", connection, transaction);
			freqCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
			freqCommand.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.Timestamp) { Value = cutoff });

			// Postgres COUNT(*) is bigint -> long.
			var scalar = await freqCommand.ExecuteScalarAsync(cancellationToken);
			recentOrderCount = scalar is long countValue ? countValue : Convert.ToInt64(scalar);
		}

		// 5. resulting position size: current signed qty (0 when no position row yet; SQL ISNULL(@currentQty, 0)).
		var currentPositionQty = 0m;
		if (CanonicalRiskRules.IsCeilingEnabled(maxPositionSize))
		{
			using var positionCommand = new NpgsqlCommand(
				"SELECT qty FROM positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id", connection, transaction);
			positionCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
			positionCommand.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });
			currentPositionQty = await ExecuteDecimalScalarAsync(positionCommand, 0m, cancellationToken);
		}

		// 6. cumulative commission estimate: last traded price (market order only) + existing gross notional.
		//    existingGrossNotional is the EXACT SUM(t.qty * t.price) over the portfolio's Trades
		//    (join Trades -> Orders, portfolio-wide across all securities), reproducing the retired
		//    usp_ValidatePreTradeRisk @existingNotional at full NUMERIC precision (PostgreSQL evaluates the
		//    NUMERIC product and its SUM without intermediate rounding). It is read directly from Trades
		//    rather than a maintained rollup column: a NUMERIC(18,4) rollup would round each contribution to
		//    four decimals before summing (0.00000001 -> 0.0000) and could under-state the gross, letting the
		//    commission ceiling stop triggering (a loosening forbidden by AAP 0.6.4). SUM is NULL => 0.
		var existingGrossNotional = 0m;
		decimal? lastTradePrice = null;
		if (CanonicalRiskRules.IsCeilingEnabled(maxCommissionTotal))
		{
			if (priceQ is null)
			{
				// Market order: best-effort last traded price for the security (may stay null if it never traded).
				// executed_date can tie (same-instant fills); trade_id DESC is the authoritative secondary key so a
				// tie deterministically picks the newest trade (the highest identity) rather than an arbitrary row.
				using var lastTradeCommand = new NpgsqlCommand(
					"SELECT t.price FROM trades t JOIN orders o ON o.order_id = t.order_id " +
					"WHERE o.security_id = @security_id ORDER BY t.executed_date DESC, t.trade_id DESC LIMIT 1", connection, transaction);
				lastTradeCommand.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });

				var lastPriceResult = await lastTradeCommand.ExecuteScalarAsync(cancellationToken);
				if (lastPriceResult is decimal lastPrice)
					lastTradePrice = lastPrice;
			}

			// Exact portfolio-wide gross from Trades (parity with the retired proc), NOT a rounded rollup (AAP 0.6.4 / C5).
			using var notionalCommand = new NpgsqlCommand(
				"SELECT SUM(t.qty * t.price) FROM trades t JOIN orders o ON o.order_id = t.order_id " +
				"WHERE o.portfolio_id = @portfolio_id", connection, transaction);
			notionalCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
			existingGrossNotional = await ExecuteDecimalScalarAsync(notionalCommand, 0m, cancellationToken);
		}

		// 7. daily traded volume: half-open [dayStart, dayEnd) UTC-day range reproduces the proc's
		//    CAST(submitted_date AS DATE) = CAST(SYSUTCDATETIME() AS DATE), index-friendly, no per-row CAST.
		var todayVolume = 0m;
		if (CanonicalRiskRules.IsCeilingEnabled(maxDailyVolume))
		{
			var dayStart = now.Date;            // Kind=Unspecified midnight UTC (now is already Unspecified).
			var dayEnd = dayStart.AddDays(1);

			using var dailyCommand = new NpgsqlCommand(
				"SELECT SUM(qty) FROM orders " +
				"WHERE portfolio_id = @portfolio_id AND security_id = @security_id " +
				"AND status IN ('ACCEPTED','FILLED','PARTFILLED') " +
				"AND submitted_date >= @day_start AND submitted_date < @day_end", connection, transaction);
			dailyCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
			dailyCommand.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });
			dailyCommand.Parameters.Add(new NpgsqlParameter("day_start", NpgsqlDbType.Timestamp) { Value = dayStart });
			dailyCommand.Parameters.Add(new NpgsqlParameter("day_end", NpgsqlDbType.Timestamp) { Value = dayEnd });
			todayVolume = await ExecuteDecimalScalarAsync(dailyCommand, 0m, cancellationToken);
		}

		// Hand the fully-gathered state to the pure decision core. This is the SAME EvaluateGate the parity
		// tests call with SQL-Server-read state (Step 2), so the gateway path (Step 3, PostgreSQL) and the
		// staged tests share ONE implementation of the seven-check decision (single source of truth, AAP 0.6.1).
		var inputs = new GateInputs
		{
			Side = side,
			Qty = q,
			Price = priceQ,
			MaxOrderPrice = maxOrderPrice,
			MaxOrderQty = maxOrderQty,
			MaxOrderValue = maxOrderValue,
			MaxPositionSize = maxPositionSize,
			MaxDailyVolume = maxDailyVolume,
			MaxOrderFreqCount = maxOrderFreqCount,
			MaxOrderFreqWindowSec = maxOrderFreqWindowSec,
			MaxCommissionTotal = maxCommissionTotal,
			CommissionRate = commissionRate,
			RecentOrderCount = recentOrderCount,
			CurrentPositionQty = currentPositionQty,
			ExistingGrossNotional = existingGrossNotional,
			LastTradePrice = lastTradePrice,
			TodayVolume = todayVolume,
		};

		return EvaluateGate(in inputs);
	}

	// Reads a single decimal value/aggregate on the caller's open connection, treating a NULL/DBNull result
	// or an empty result set as <paramref name="defaultValue"/> - the C# equivalent of the SQL ISNULL(..., 0)
	// used by the position, notional, and daily-volume reads. NUMERIC maps to decimal, so a real value boxes
	// as decimal; this member is private, so its NpgsqlCommand parameter needs no CLS-compliance annotation.
	private static async Task<decimal> ExecuteDecimalScalarAsync(NpgsqlCommand command, decimal defaultValue, CancellationToken cancellationToken)
	{
		var result = await command.ExecuteScalarAsync(cancellationToken);
		return result is decimal value ? value : defaultValue;
	}

	// Numeric quantization now lives in CanonicalRiskRules.QuantizeToScale (the single canonical NUMERIC(18,4)
	// rounding shared with the data-access gateway), so the gate DECIDES on exactly the value PostgreSQL PERSISTS
	// and the gateway binds that same normalized value (decision == persistence, F5 / M12 / AAP 0.6.4).
}
