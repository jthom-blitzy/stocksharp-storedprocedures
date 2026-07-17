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
		var q = QuantizeToScale(qty.Value);

		if (q <= 0m)
			return PreTradeRiskResult.Reject("Invalid qty");

		// Quantise the limit price to the same NUMERIC(18,4) scale (a market order has no price => null). Every
		// downstream check and reject message uses this quantised price, not the raw parameter, for the same
		// decision-matches-persistence reason (F5).
		var priceQ = price.HasValue ? (decimal?)QuantizeToScale(price.Value) : null;

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
			"effective_date DESC " +
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

		// --- 1. Order price ceiling (mirrors RiskOrderPriceRule; rejects when price >= limit) ---
		// Uses the shared CanonicalRiskRules.MeetsOrExceeds so the gate and the circuit-breaker price rule
		// apply the identical ">=" comparison. MeetsOrExceeds embeds the canonical enabled-limit guard
		// (present AND > 0, so NULL or 0 = unlimited), so a breach guarantees maxOrderPrice.HasValue.
		if (priceQ is not null && CanonicalRiskRules.MeetsOrExceeds(priceQ.Value, maxOrderPrice))
			return PreTradeRiskResult.Reject(
				$"Order price {priceQ.Value.To<string>()} meets/exceeds limit {maxOrderPrice.Value.To<string>()}");

		// --- 2. Order qty ceiling (mirrors RiskOrderVolumeRule; rejects when qty >= limit) ---
		// Canonical MeetsOrExceeds: NULL or 0 max_order_qty = unlimited; positive => ">=" rejects (F3).
		if (CanonicalRiskRules.MeetsOrExceeds(q, maxOrderQty))
			return PreTradeRiskResult.Reject(
				$"Order qty {q.To<string>()} meets/exceeds limit {maxOrderQty.Value.To<string>()}");

		// --- 3. Notional order value ceiling (qty * price) ---
		// Order notional value existed only in SQL; promoted to a first-class C# gate rule (AAP 0.6.2).
		// RiskManager's circuit breaker does not enforce it, so this gate is now its only home. It is only
		// meaningful when a price is supplied (a market order has no ex-ante notional), matching the SQL
		// "@price IS NOT NULL". Product stays decimal and the canonical MeetsOrExceeds (NULL or 0 = unlimited)
		// keeps the ">=" comparison from silently loosening (F3).
		if (priceQ is not null && CanonicalRiskRules.MeetsOrExceeds(q * priceQ.Value, maxOrderValue))
			return PreTradeRiskResult.Reject(
				$"Order value {(q * priceQ.Value).To<string>()} meets/exceeds limit {maxOrderValue.Value.To<string>()}");

		// --- 4. Order frequency (mirrors RiskOrderFreqRule) ---
		// Canonical IsFrequencyEnabled: enforced only when BOTH count and window are present and > 0 (F3).
		if (CanonicalRiskRules.IsFrequencyEnabled(maxOrderFreqCount, maxOrderFreqWindowSec))
		{
			var window = TimeSpan.FromSeconds(maxOrderFreqWindowSec.Value);
			var cutoff = now - window; // Kind=Unspecified, matches the `timestamp` values Npgsql reads back.

			// BOUNDED COUNT(*) of the orders already inside the trailing window - only the count crosses the
			// wire, never the rows (F12). This is exact parity with the proc's
			// "WHERE submitted_date >= DATEADD(SECOND, -window, SYSUTCDATETIME())": the inclusive lower bound
			// is the same one CanonicalRiskRules.CountWithinWindow applies, so the count is identical to
			// materialising the timestamps and counting them, without the unbounded fetch. Rejected orders are
			// ALSO persisted in `orders`, so they legitimately count toward frequency - matching the SQL COUNT(*).
			long recentCount;

			using (var freqCommand = new NpgsqlCommand(
				"SELECT COUNT(*) FROM orders WHERE portfolio_id = @portfolio_id AND submitted_date >= @cutoff", connection, transaction))
			{
				freqCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
				freqCommand.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.Timestamp) { Value = cutoff });

				// Postgres COUNT(*) is bigint -> long.
				var scalar = await freqCommand.ExecuteScalarAsync(cancellationToken);
				recentCount = scalar is long countValue ? countValue : Convert.ToInt64(scalar);
			}

			// recentCount + 1 >= max is the exact SQL "@recentOrderCount + 1 >= @max_order_freq_count", routed
			// through the shared bounded-count canonical evaluator so the gate and RiskOrderFreqRule can never
			// disagree on a frequency decision (AAP 0.6.1). The "+1" accounts for the prospective order.
			if (CanonicalRiskRules.IsOrderFrequencyBreached(recentCount, maxOrderFreqCount.Value))
				return PreTradeRiskResult.Reject(
					$"Order frequency {(recentCount + 1).To<string>()} in {maxOrderFreqWindowSec.Value.To<string>()}s meets/exceeds limit {maxOrderFreqCount.Value.To<string>()}");
		}

		// --- 5. Resulting position size ceiling (shares RiskPositionSizeRule's threshold) ---
		// Canonical IsCeilingEnabled: NULL or 0 max_position_size = unlimited (F3).
		if (CanonicalRiskRules.IsCeilingEnabled(maxPositionSize))
		{
			// GATE subject is the HYPOTHETICAL post-fill position (current qty + signed order qty). This differs
			// from RiskPositionSizeRule, which checks the CURRENT position from PositionChangeMessage.CurrentValue.
			// Only the threshold + ">=" direction are shared; the gate keeps the hypothetical subject on purpose,
			// because its job is to block the order BEFORE it is accepted (AAP 0.6.2).
			var currentQty = 0m;

			using (var positionCommand = new NpgsqlCommand(
				"SELECT qty FROM positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id", connection, transaction))
			{
				positionCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
				positionCommand.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });

				// SELECT qty returns no row when a position has not been opened yet => 0 (SQL ISNULL(@currentQty, 0)).
				currentQty = await ExecuteDecimalScalarAsync(positionCommand, 0m, cancellationToken);
			}

			var signedDelta = side == "B" ? q : -q; // 'B' adds, 'S' subtracts (SQL side sign).
			var resulting = currentQty + signedDelta;

			// MeetsOrExceeds embeds the enabled-limit guard, so maxPositionSize.HasValue holds inside the breach.
			if (CanonicalRiskRules.MeetsOrExceeds(Math.Abs(resulting), maxPositionSize))
				return PreTradeRiskResult.Reject(
					$"Resulting position {resulting.To<string>()} meets/exceeds limit {maxPositionSize.Value.To<string>()}");
		}

		// --- 6. Cumulative commission ceiling (pre-fill ESTIMATE) ---
		// Canonical IsCeilingEnabled: NULL or 0 max_commission_total = unlimited (F3).
		if (CanonicalRiskRules.IsCeilingEnabled(maxCommissionTotal))
		{
			// This is a pre-fill ESTIMATE (a forecast). It is intentionally NOT merged with the circuit breaker's
			// realized-commission rules (RiskCommissionRule / RiskOrderCommissionRule / RiskTransactionCommissionRule),
			// which accumulate ACTUAL ExecutionMessage.Commission AFTER the fill. A forecast and a realized figure
			// will not agree, so BOTH are preserved (different by design, AAP 0.6.2).
			var rate = commissionRate ?? 0m; // commission_rate is NOT NULL in the schema, so present when a limit row exists.
			decimal? estPrice = priceQ; // use the quantised limit price (F5); the market fallback below is already 4-dp.

			if (estPrice is null)
			{
				// Market order: best-effort last traded price for the security (may stay null if it never traded).
				using var lastTradeCommand = new NpgsqlCommand(
					"SELECT t.price FROM trades t JOIN orders o ON o.order_id = t.order_id " +
					"WHERE o.security_id = @security_id ORDER BY t.executed_date DESC LIMIT 1", connection, transaction);
				lastTradeCommand.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });

				var lastPriceResult = await lastTradeCommand.ExecuteScalarAsync(cancellationToken);
				if (lastPriceResult is decimal lastPrice)
					estPrice = lastPrice;
			}

			// Existing gross notional over this portfolio, read as a BOUNDED rollup: SUM(cumulative_gross_notional)
			// over the portfolio's positions rows (at most one per security) instead of re-summing every historical
			// trade row (F13). PositionRecalculationService maintains cumulative_gross_notional as SUM(qty*price)
			// per (portfolio, security) under the single-apply invariant, so this equals the retired unbounded
			// SUM(t.qty*t.price) over the portfolio's trades exactly. SUM is NULL when there are no positions => 0.
			var existingNotional = 0m;

			using (var notionalCommand = new NpgsqlCommand(
				"SELECT SUM(cumulative_gross_notional) FROM positions WHERE portfolio_id = @portfolio_id", connection, transaction))
			{
				notionalCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
				existingNotional = await ExecuteDecimalScalarAsync(notionalCommand, 0m, cancellationToken);
			}

			// F5 parity: the retired proc held @existingNotional and @estCommission as DECIMAL(18,4), so quantise the
			// summed notional to 4 dp and the final estimate to 4 dp before the ">=" compare. rate stays at its full
			// NUMERIC(9,6) scale (it is a rate, not a ceiling). ISNULL(@estPrice, 0) parity via (estPrice ?? 0m) so a
			// market order with no last trade contributes 0. All decimal so the compare can never silently loosen.
			existingNotional = QuantizeToScale(existingNotional);
			var est = QuantizeToScale(existingNotional * rate + q * (estPrice ?? 0m) * rate);
			if (CanonicalRiskRules.MeetsOrExceeds(est, maxCommissionTotal))
				return PreTradeRiskResult.Reject(
					$"Estimated cumulative commission {est.To<string>()} meets/exceeds limit {maxCommissionTotal.Value.To<string>()}");
		}

		// --- 7. Daily traded volume ceiling ---
		// Canonical IsCeilingEnabled: NULL or 0 max_daily_volume = unlimited (F3).
		if (CanonicalRiskRules.IsCeilingEnabled(maxDailyVolume))
		{
			// Daily traded volume existed only in SQL; promoted to a first-class C# gate rule. The half-open
			// [dayStart, dayEnd) range reproduces CAST(submitted_date AS DATE) = CAST(SYSUTCDATETIME() AS DATE)
			// over a UTC day, is index-friendly, and avoids a per-row CAST (AAP 0.6.2/0.6.4).
			var dayStart = now.Date;            // Kind=Unspecified midnight UTC (now is already Unspecified).
			var dayEnd = dayStart.AddDays(1);
			var todayQty = 0m;

			using (var dailyCommand = new NpgsqlCommand(
				"SELECT SUM(qty) FROM orders " +
				"WHERE portfolio_id = @portfolio_id AND security_id = @security_id " +
				"AND status IN ('ACCEPTED','FILLED','PARTFILLED') " +
				"AND submitted_date >= @day_start AND submitted_date < @day_end", connection, transaction))
			{
				dailyCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
				dailyCommand.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });
				dailyCommand.Parameters.Add(new NpgsqlParameter("day_start", NpgsqlDbType.Timestamp) { Value = dayStart });
				dailyCommand.Parameters.Add(new NpgsqlParameter("day_end", NpgsqlDbType.Timestamp) { Value = dayEnd });

				todayQty = await ExecuteDecimalScalarAsync(dailyCommand, 0m, cancellationToken);
			}

			if (CanonicalRiskRules.MeetsOrExceeds(todayQty + q, maxDailyVolume))
				return PreTradeRiskResult.Reject(
					$"Daily volume {(todayQty + q).To<string>()} meets/exceeds limit {maxDailyVolume.Value.To<string>()}");
		}

		// Every configured check passed - accept the order.
		return PreTradeRiskResult.Valid;
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

	// F5 helper: rounds a decimal to the schema's NUMERIC(18,4) scale using away-from-zero (half-up) rounding - the
	// exact coercion the retired proc applied by declaring @qty/@price/@max_*/@estCommission as DECIMAL(18,4), and the
	// same rounding Postgres uses when persisting into a NUMERIC(18,4) column. Quantising the gate's inputs and money
	// intermediates to this scale keeps the accept/reject decision consistent with the value that is actually stored,
	// so a >4-dp input can no longer be accepted on its full-precision value yet persisted at (or above) a ceiling it
	// should have met. Rounding to a fixed scale away from zero is never LESS strict (hard NFR, AAP 0.6.4).
	private const int NumericScale = 4;

	private static decimal QuantizeToScale(decimal value)
		=> Math.Round(value, NumericScale, MidpointRounding.AwayFromZero);
}
