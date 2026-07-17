namespace StockSharp.Algo.Risk;

using Microsoft.Data.SqlClient;

/// <summary>
/// Per-order pre-trade risk GATE. C# replacement for the retired SQL proc
/// dbo.usp_ValidatePreTradeRisk: validates a single prospective order against
/// the most-specific configured RiskLimits row and returns accept/reject with a
/// descriptive reason on the first failing rule.
/// </summary>
/// <remarks>
/// <para>
/// One of the two distinct, never-merged enforcement patterns that consume the
/// canonical <see cref="IRiskRule"/> definitions (the other is the stream-based
/// <see cref="RiskManager"/> circuit breaker). Each rule is defined exactly once;
/// the two patterns differ only in the input they supply.
/// </para>
/// <para>
/// The pure-threshold ceilings run the canonical rule classes directly on the
/// prospective order (<see cref="RiskOrderPriceRule"/>, <see cref="RiskOrderVolumeRule"/>,
/// <see cref="RiskOrderValueRule"/>). The stateful rules delegate to the canonical
/// rules' shared decision helpers -
/// <see cref="RiskOrderFreqRule.IsFrequencyExceeded"/>,
/// <see cref="RiskPositionSizeRule.IsPositionSizeExceeded"/> and
/// <see cref="RiskDailyVolumeRule.IsDailyVolumeExceeded"/> - fed an aggregate or
/// projection read from SQL Server (rolling order count, hypothetical post-fill
/// position, today's accepted/filled volume). Those same helpers back the
/// <see cref="RiskManager"/> stream path, so the gate and the circuit breaker can
/// never disagree on a shared rule.
/// </para>
/// <para>
/// Cumulative commission is the single rule kept deliberately separate (AAP 0.6.4):
/// the gate can only estimate cost before the fill exists, so it applies
/// RiskLimits.commission_rate to the order/last price, whereas the circuit-breaker
/// commission rules read the actual figure off the post-fill ExecutionMessage.
/// </para>
/// <para>
/// Strictness conventions preserved verbatim: every comparison uses the
/// "&gt;=" ("meets or exceeds") boundary; a NULL or exactly-zero threshold means
/// "not enforced"; a negative threshold is invalid configuration and fails closed
/// (rejects) rather than silently disabling the control. All qty/price/value inputs
/// and aggregates are normalized to the schema's DECIMAL(18,4) scale (commission_rate
/// to DECIMAL(9,6)) with round-half-away-from-zero before any comparison, matching
/// how SQL Server coerced the corresponding procedure variables.
/// </para>
/// <para>
/// The primary <see cref="ValidateAsync(SqlConnection, SqlTransaction, int, int, Sides, decimal, decimal?, OrderTypes, string, CancellationToken)"/>
/// overload runs on a caller-supplied connection and transaction so the caller can
/// perform validation and the resulting order insert as one atomic, appropriately
/// isolated unit - closing the check-to-insert race the standalone SQL gate had.
/// </para>
/// </remarks>
public class PreTradeRiskService
{
	// dbo.RiskLimits.commission_rate column default (001_Schema.sql); used only if
	// a selected RiskLimits row carries a NULL rate.
	private const decimal _defaultCommissionRate = 0.0005m;

	private readonly string _connectionString;

	/// <summary>
	/// Initializes a new instance of the <see cref="PreTradeRiskService"/>.
	/// </summary>
	/// <param name="connectionString">SQL Server connection string for the StockSharpLegacy database.</param>
	public PreTradeRiskService(string connectionString)
	{
		if (connectionString.IsEmpty())
			throw new ArgumentNullException(nameof(connectionString));

		_connectionString = connectionString;
	}

	private SqlConnection CreateConnection() => new(_connectionString);

	// SQL Server coerced every RiskLimits money/qty column and the procedure
	// variables to DECIMAL(18,4), and commission_rate to DECIMAL(9,6), rounding
	// half away from zero on assignment. The gate normalizes to the same scale so
	// a value SQL rounded up to the limit (e.g. 0.99995 -> 1.0000) is rejected here
	// exactly as it was in the proc, honouring the stricter-wins hard constraint.
	private static decimal Round4(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
	private static decimal Round6(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

	// Rejection/audit text must be byte-identical regardless of host culture.
	private static string Inv(FormattableString reason) => FormattableString.Invariant(reason);

	/// <summary>
	/// Validates a prospective order on a freshly opened, dedicated connection.
	/// Convenience entry point for standalone use and tests; the production path
	/// uses the transaction-aware overload so validation and the order insert share
	/// one atomic unit.
	/// </summary>
	/// <param name="portfolioId">Target portfolio identifier.</param>
	/// <param name="securityId">Target security identifier.</param>
	/// <param name="side">Order side; only <see cref="Sides.Buy"/>/<see cref="Sides.Sell"/> are valid.</param>
	/// <param name="qty">Prospective order quantity (must be positive).</param>
	/// <param name="price">Limit price, or <see langword="null"/> for a market order.</param>
	/// <param name="orderType">Order type (LIMIT/MARKET); carried for parity with the retired proc signature.</param>
	/// <param name="requestedBy">Optional requester identifier; carried for parity with the retired proc signature.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Accept/reject decision with a reason on the first failing rule.</returns>
	public async Task<PreTradeRiskResult> ValidateAsync(
		int portfolioId, int securityId, Sides side, decimal qty, decimal? price,
		OrderTypes orderType, string requestedBy = null, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		return await ValidateAsync(
			connection, null, portfolioId, securityId, side, qty, price,
			orderType, requestedBy, cancellationToken);
	}

	/// <summary>
	/// Validates a prospective order using a caller-supplied open connection and
	/// (optionally) the transaction that also inserts the resulting order, so the
	/// read-decide-insert sequence is one atomic unit under the caller's isolation
	/// level. Ports dbo.usp_ValidatePreTradeRisk RULES 1-7 in order (first failure wins).
	/// </summary>
	/// <param name="connection">Open SQL Server connection.</param>
	/// <param name="transaction">Ambient transaction for the connection, or <see langword="null"/> for autocommit reads.</param>
	/// <param name="portfolioId">Target portfolio identifier.</param>
	/// <param name="securityId">Target security identifier.</param>
	/// <param name="side">Order side; only <see cref="Sides.Buy"/>/<see cref="Sides.Sell"/> are valid.</param>
	/// <param name="qty">Prospective order quantity (must be positive).</param>
	/// <param name="price">Limit price, or <see langword="null"/> for a market order.</param>
	/// <param name="orderType">Order type (LIMIT/MARKET); carried for parity with the retired proc signature.</param>
	/// <param name="requestedBy">Optional requester identifier; carried for parity with the retired proc signature.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Accept/reject decision with a reason on the first failing rule.</returns>
	public async Task<PreTradeRiskResult> ValidateAsync(
		SqlConnection connection, SqlTransaction transaction,
		int portfolioId, int securityId, Sides side, decimal qty, decimal? price,
		OrderTypes orderType, string requestedBy = null, CancellationToken cancellationToken = default)
	{
		if (connection is null)
			throw new ArgumentNullException(nameof(connection));

		// --- input pre-checks (SQL L73-85) ---------------------------------
		// Sides only models Buy/Sell, but guard defensively to mirror the SQL
		// "side NOT IN ('B','S')" pre-check exactly.
		if (side != Sides.Buy && side != Sides.Sell)
			return RejectInvalid(Inv($"Invalid side: {side}"));

		// Coerce qty to the schema scale first (the proc parameter was DECIMAL(18,4)),
		// then apply the positive-quantity pre-check on the coerced value so a sub-tick
		// quantity that rounds to zero is rejected exactly as SQL rejected it.
		qty = Round4(qty);

		if (qty <= 0)
			return RejectInvalid("Invalid qty");

		var normPrice = price.HasValue ? Round4(price.Value) : (decimal?)null;

		// --- order-type-aware price validation (CR-3) ----------------------
		// The retired SQL proc carried @order_type but only checked "@price IS NOT
		// NULL" for a LIMIT order - it never validated positivity, so a zero or
		// NEGATIVE limit price slipped underneath every positive ceiling and was
		// accepted. Validate the price against the order type BEFORE any RiskLimits
		// row is loaded or applied. A malformed request is a bad INPUT, not a
		// risk-limit breach, so it is rejected with the InvalidRequest kind (the
		// gateway must not persist a REJECTED audit row for it - see MJ-1).
		//
		// Only LIMIT and MARKET are supported: the schema's order_type CHECK accepts
		// exactly these and the gateway maps only these, so anything else (e.g.
		// Conditional) is a malformed request.
		if (orderType != OrderTypes.Limit && orderType != OrderTypes.Market)
			return RejectInvalid(Inv($"Unsupported order type: {orderType}"));

		// A supplied price must be strictly positive for ANY order type; a
		// non-positive value (including a sub-tick price that rounds to zero at the
		// schema scale) is malformed and must never be silently accepted.
		if (normPrice.HasValue && normPrice.Value <= 0)
			return RejectInvalid(Inv($"Invalid non-positive price {normPrice.Value}"));

		// A LIMIT order MUST carry a price: without one there is no ceiling to test
		// and the notional/commission estimates are undefined. A MARKET order may
		// omit the price (null) - the commission estimate falls back to the
		// security's last traded price - or carry a positive reference price
		// (already validated positive above).
		if (orderType == OrderTypes.Limit && normPrice is null)
			return RejectInvalid("LIMIT order requires a price");

		// --- most-specific RiskLimits row (SQL L100-118): portfolio+security,
		//     then portfolio-only, then security-only, newest effective_date ---
		decimal? maxOrderPrice = null, maxOrderQty = null, maxOrderValue = null,
			maxPositionSize = null, maxDailyVolume = null, maxCommissionTotal = null, commissionRate = null;
		int? maxOrderFreqCount = null, maxOrderFreqWindowSec = null;

		await using (var limitsCmd = new SqlCommand(
			"""
			SELECT TOP (1)
				max_order_price, max_order_qty, max_order_value, max_position_size,
				max_daily_volume, max_order_freq_count, max_order_freq_window_sec,
				max_commission_total, commission_rate
			FROM dbo.RiskLimits
			WHERE is_active = 1
				AND (portfolio_id = @portfolio_id OR portfolio_id IS NULL)
				AND (security_id = @security_id OR security_id IS NULL)
			ORDER BY
				CASE WHEN portfolio_id IS NOT NULL AND security_id IS NOT NULL THEN 0
					 WHEN portfolio_id IS NOT NULL THEN 1
					 ELSE 2 END,
				effective_date DESC
			""", connection)
		{
			Transaction = transaction,
		})
		{
			limitsCmd.Parameters.AddWithValue("@portfolio_id", portfolioId);
			limitsCmd.Parameters.AddWithValue("@security_id", securityId);

			await using var reader = await limitsCmd.ExecuteReaderAsync(cancellationToken);

			if (await reader.ReadAsync(cancellationToken))
			{
				maxOrderPrice = GetNullableDecimal(reader, 0);
				maxOrderQty = GetNullableDecimal(reader, 1);
				maxOrderValue = GetNullableDecimal(reader, 2);
				maxPositionSize = GetNullableDecimal(reader, 3);
				maxDailyVolume = GetNullableDecimal(reader, 4);
				maxOrderFreqCount = GetNullableInt(reader, 5);
				maxOrderFreqWindowSec = GetNullableInt(reader, 6);
				maxCommissionTotal = GetNullableDecimal(reader, 7);
				commissionRate = GetNullableDecimal(reader, 8);
			}
		}

		// no configured limits at all => nothing to enforce (SQL L120-127)
		if (maxOrderPrice is null && maxOrderQty is null && maxOrderValue is null
			&& maxPositionSize is null && maxDailyVolume is null
			&& maxOrderFreqCount is null && maxCommissionTotal is null)
		{
			return Accept();
		}

		// --- RiskLimits configuration validation (MJ-2) --------------------
		// Validate the ENTIRE selected row up front, BEFORE any per-rule enable/
		// disable guard runs. A negative threshold, commission rate, frequency count
		// or frequency window is invalid configuration and must FAIL CLOSED (reject)
		// - never silently disable a control. Previously a negative frequency window
		// was swallowed by the per-rule ">0" guard (the whole frequency check was
		// skipped => fail OPEN) and a negative commission_rate flowed straight into
		// the estimate. A NULL threshold is genuinely "not configured" and is left
		// alone (a nullable "< 0" comparison is false when the value is NULL).
		if (maxOrderPrice < 0)
			return Reject(Inv($"Invalid negative max_order_price {maxOrderPrice}; failing closed"));
		if (maxOrderQty < 0)
			return Reject(Inv($"Invalid negative max_order_qty {maxOrderQty}; failing closed"));
		if (maxOrderValue < 0)
			return Reject(Inv($"Invalid negative max_order_value {maxOrderValue}; failing closed"));
		if (maxPositionSize < 0)
			return Reject(Inv($"Invalid negative max_position_size {maxPositionSize}; failing closed"));
		if (maxDailyVolume < 0)
			return Reject(Inv($"Invalid negative max_daily_volume {maxDailyVolume}; failing closed"));
		if (maxCommissionTotal < 0)
			return Reject(Inv($"Invalid negative max_commission_total {maxCommissionTotal}; failing closed"));
		if (commissionRate < 0)
			return Reject(Inv($"Invalid negative commission_rate {commissionRate}; failing closed"));
		if (maxOrderFreqCount < 0)
			return Reject(Inv($"Invalid negative max_order_freq_count {maxOrderFreqCount}; failing closed"));
		if (maxOrderFreqWindowSec < 0)
			return Reject(Inv($"Invalid negative max_order_freq_window_sec {maxOrderFreqWindowSec}; failing closed"));

		// Frequency fields must be internally consistent: the rolling-window check
		// needs BOTH a positive count and a positive window. If exactly one side is
		// configured (the other NULL or zero) the rule cannot be evaluated - fail
		// closed rather than silently not enforcing it. When both are NULL/0 the
		// frequency check is simply "not enforced".
		var freqCountSet = maxOrderFreqCount is not null && maxOrderFreqCount.Value != 0;
		var freqWindowSet = maxOrderFreqWindowSec is not null && maxOrderFreqWindowSec.Value != 0;
		if (freqCountSet != freqWindowSet)
			return Reject(Inv($"Inconsistent frequency config: count={maxOrderFreqCount}, window_sec={maxOrderFreqWindowSec}; failing closed"));

		// Prospective-order message fed to the canonical pure-threshold rules: the
		// gate executes the SAME rule comparison the RiskManager circuit breaker uses.
		var orderMessage = new OrderRegisterMessage
		{
			Side = side,
			Volume = qty,
			Price = normPrice ?? 0m,
		};

		// RULE 1 - order price ceiling (SQL L129-134): the gate routes through the
		// canonical RiskOrderPriceRule.IsOrderPriceExceeded helper - the SAME 0 = "not
		// enforced" + ">=" comparison the RiskManager stream path uses - so the two
		// enforcement patterns can never diverge on the enable/disable convention (MJ-3).
		// Negative configuration already failed closed in the validation block above (MJ-2).
		if (normPrice.HasValue && maxOrderPrice.HasValue)
		{
			if (RiskOrderPriceRule.IsOrderPriceExceeded(normPrice.Value, maxOrderPrice.Value))
				return Reject(Inv($"Order price {normPrice.Value} meets/exceeds limit {maxOrderPrice.Value}"));
		}

		// RULE 2 - order qty ceiling (SQL L136-141): the gate routes through the
		// canonical RiskOrderVolumeRule.IsOrderVolumeExceeded helper - the SAME
		// 0 = "not enforced" + ">=" comparison the RiskManager stream path uses (MJ-3).
		// Negative configuration already failed closed in the validation block above (MJ-2).
		if (maxOrderQty.HasValue)
		{
			if (RiskOrderVolumeRule.IsOrderVolumeExceeded(qty, maxOrderQty.Value))
				return Reject(Inv($"Order qty {qty} meets/exceeds limit {maxOrderQty.Value}"));
		}

		// RULE 3 - notional value ceiling (SQL L143-148): canonical RiskOrderValueRule,
		// relocated from SQL-only max_order_value. qty*price >= limit (full-precision
		// product vs the scale-4 limit, exactly as SQL compared @qty*@price). Negative
		// configuration already failed closed in the validation block above (MJ-2).
		if (normPrice.HasValue && maxOrderValue.HasValue && maxOrderValue.Value != 0)
		{
			if (new RiskOrderValueRule { OrderValue = maxOrderValue.Value }.ProcessMessage(orderMessage))
				return Reject(Inv($"Order value {qty * normPrice.Value} meets/exceeds limit {maxOrderValue.Value}"));
		}

		// RULE 4 - order frequency (SQL L161-175): canonical rolling-window definition.
		// The gate supplies the true rolling COUNT (orders within "now - window") read
		// from SQL and defers the decision to RiskOrderFreqRule.IsFrequencyExceeded -
		// the SAME helper the stream path calls, so both agree at the boundary. Rolling
		// wins under stricter-wins (AAP 0.6.3). The validation block above guarantees
		// that when a frequency limit is configured BOTH the count and window are
		// positive (negative or half-configured values already failed closed - MJ-2),
		// so the rolling-window check runs whenever freqCountSet is true. This closes
		// the earlier fail-OPEN hole where a negative window silently skipped the check.
		if (freqCountSet)
		{
			int recentCount;

			await using (var freqCmd = new SqlCommand(
				"SELECT COUNT(*) FROM dbo.Orders WHERE portfolio_id = @portfolio_id AND submitted_date >= DATEADD(SECOND, -@window_sec, SYSUTCDATETIME())",
				connection)
			{
				Transaction = transaction,
			})
			{
				freqCmd.Parameters.AddWithValue("@portfolio_id", portfolioId);
				freqCmd.Parameters.AddWithValue("@window_sec", maxOrderFreqWindowSec.Value);

				recentCount = Convert.ToInt32(await freqCmd.ExecuteScalarAsync(cancellationToken));
			}

			if (RiskOrderFreqRule.IsFrequencyExceeded(recentCount, maxOrderFreqCount.Value))
				return Reject(Inv($"Order frequency {recentCount + 1} in {maxOrderFreqWindowSec.Value}s meets/exceeds limit {maxOrderFreqCount.Value}"));
		}

		// RULE 5 - resulting position size (SQL L177-192): shared RiskPositionSizeRule
		// definition applied at the POST-FILL projection (current + signed order qty).
		// The decision uses RiskPositionSizeRule.IsPositionSizeExceeded (symmetric ABS
		// ceiling, SQL semantics) - the SAME helper the circuit breaker applies to the
		// live position. Dropping the projection would loosen the gate and violate the
		// stricter-wins hard constraint (AAP 0.6.2/0.6.4).
		if (maxPositionSize.HasValue && maxPositionSize.Value != 0)
		{
			decimal currentQty;

			await using (var posCmd = new SqlCommand(
				"SELECT qty FROM dbo.Positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id",
				connection)
			{
				Transaction = transaction,
			})
			{
				posCmd.Parameters.AddWithValue("@portfolio_id", portfolioId);
				posCmd.Parameters.AddWithValue("@security_id", securityId);

				var raw = await posCmd.ExecuteScalarAsync(cancellationToken);
				currentQty = raw is decimal q ? q : 0m;
			}

			var signedDelta = side == Sides.Buy ? qty : -qty;
			var projected = Round4(currentQty + signedDelta);

			if (RiskPositionSizeRule.IsPositionSizeExceeded(projected, maxPositionSize.Value))
				return Reject(Inv($"Resulting position {projected} meets/exceeds limit {maxPositionSize.Value}"));
		}

		// RULE 6 - cumulative commission (SQL L199-224): PRE-FILL ESTIMATE. This is
		// DELIBERATELY DIFFERENT from the actual post-fill RiskCommissionRule /
		// RiskOrderCommissionRule / RiskTransactionCommissionRule, which read the real
		// commission off ExecutionMessage after a fill. Different data-availability
		// windows: the gate can only estimate before the trade exists, so it uses
		// RiskLimits.commission_rate against the order price (or the security's last
		// traded price for a market order). Kept separate by design (AAP 0.6.4); the
		// circuit-breaker path keeps the actual figure. ">=" preserved. The estimate is
		// coerced to DECIMAL(18,4) with a single final rounding, exactly as SQL assigned
		// the @estCommission variable.
		if (maxCommissionTotal.HasValue && maxCommissionTotal.Value != 0)
		{
			var rate = Round6(commissionRate ?? _defaultCommissionRate);
			var estPrice = normPrice;

			if (estPrice is null)
			{
				await using var lastPriceCmd = new SqlCommand(
					"""
					SELECT TOP (1) t.price
					FROM dbo.Trades t
					JOIN dbo.Orders o ON o.order_id = t.order_id
					WHERE o.security_id = @security_id
					ORDER BY t.executed_date DESC
					""", connection)
				{
					Transaction = transaction,
				};
				lastPriceCmd.Parameters.AddWithValue("@security_id", securityId);

				var rawPrice = await lastPriceCmd.ExecuteScalarAsync(cancellationToken);
				estPrice = rawPrice is decimal lp ? Round4(lp) : (decimal?)null;
			}

			decimal existingNotional;

			await using (var notionalCmd = new SqlCommand(
				"""
				SELECT SUM(t.qty * t.price)
				FROM dbo.Trades t
				JOIN dbo.Orders o ON o.order_id = t.order_id
				WHERE o.portfolio_id = @portfolio_id
				""", connection)
			{
				Transaction = transaction,
			})
			{
				notionalCmd.Parameters.AddWithValue("@portfolio_id", portfolioId);

				var rawNotional = await notionalCmd.ExecuteScalarAsync(cancellationToken);
				existingNotional = rawNotional is decimal n ? Round4(n) : 0m;
			}

			var estCommission = Round4(existingNotional * rate + qty * (estPrice ?? 0m) * rate);

			if (estCommission >= maxCommissionTotal.Value)
				return Reject(Inv($"Estimated cumulative commission {estCommission} meets/exceeds limit {maxCommissionTotal.Value}"));
		}

		// RULE 7 - daily traded volume (SQL L226-243): canonical RiskDailyVolumeRule,
		// relocated from SQL-only max_daily_volume. The decision uses
		// RiskDailyVolumeRule.IsDailyVolumeExceeded - the SAME helper the stream path
		// calls - fed today's accepted/filled volume (from SQL) plus the new qty.
		// The "today" boundary is derived from the DATABASE clock inside the same
		// transaction (CAST(SYSUTCDATETIME() AS DATE)), matching the retired SQL proc
		// which compared CAST(submitted_date AS DATE) = CAST(SYSUTCDATETIME() AS DATE).
		// Using the DB clock rather than the client's DateTime.UtcNow avoids clock skew
		// and midnight-race UNDERcounting (MJ-7); the filter stays a sargable half-open
		// range over the indexed submitted_date rather than wrapping the column in CAST.
		// Negative configuration already failed closed in the validation block above (MJ-2).
		if (maxDailyVolume.HasValue && maxDailyVolume.Value != 0)
		{
			decimal todayQty;

			await using (var dailyCmd = new SqlCommand(
				"""
				SELECT SUM(qty)
				FROM dbo.Orders
				WHERE portfolio_id = @portfolio_id
					AND security_id = @security_id
					AND status IN ('ACCEPTED','FILLED','PARTFILLED')
					AND submitted_date >= CAST(SYSUTCDATETIME() AS DATE)
					AND submitted_date < DATEADD(DAY, 1, CAST(SYSUTCDATETIME() AS DATE))
				""", connection)
			{
				Transaction = transaction,
			})
			{
				dailyCmd.Parameters.AddWithValue("@portfolio_id", portfolioId);
				dailyCmd.Parameters.AddWithValue("@security_id", securityId);

				var rawToday = await dailyCmd.ExecuteScalarAsync(cancellationToken);
				todayQty = rawToday is decimal d ? Round4(d) : 0m;
			}

			var effectiveDaily = Round4(todayQty + qty);

			if (RiskDailyVolumeRule.IsDailyVolumeExceeded(effectiveDaily, maxDailyVolume.Value))
				return Reject(Inv($"Daily volume {effectiveDaily} meets/exceeds limit {maxDailyVolume.Value}"));
		}

		return Accept();
	}

	private static PreTradeRiskResult Accept() => new() { IsValid = true, RejectionKind = PreTradeRejectionKind.None };

	// A risk-LIMIT rejection: the request was well-formed but breached a configured
	// (or fail-closed misconfigured) risk control. The gateway persists a REJECTED
	// audit row for these (MJ-1).
	private static PreTradeRiskResult Reject(string reason)
		=> new() { IsValid = false, RejectReason = reason, RejectionKind = PreTradeRejectionKind.RiskLimit };

	// A malformed-REQUEST rejection: the order itself is invalid (bad side, order
	// type, quantity or price) and could never be persisted - it would violate a
	// schema CHECK constraint or has no valid enum mapping. The gateway returns the
	// result WITHOUT inserting anything (MJ-1), so an invalid input never throws or
	// leaves a spurious audit row.
	private static PreTradeRiskResult RejectInvalid(string reason)
		=> new() { IsValid = false, RejectReason = reason, RejectionKind = PreTradeRejectionKind.InvalidRequest };

	private static decimal? GetNullableDecimal(SqlDataReader reader, int ordinal)
		=> reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

	private static int? GetNullableInt(SqlDataReader reader, int ordinal)
		=> reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}

/// <summary>
/// Classifies why a pre-trade validation rejected an order so the caller can react
/// correctly - persisting a risk-limit rejection as an audit row versus returning a
/// malformed request that could never be stored.
/// </summary>
public enum PreTradeRejectionKind
{
	/// <summary>The order was accepted; there is no rejection.</summary>
	None,

	/// <summary>
	/// The request itself is malformed (invalid side, order type, quantity or price).
	/// It cannot be persisted (it would violate a schema CHECK constraint or has no
	/// valid mapping) and must be returned to the caller without any database side effect.
	/// </summary>
	InvalidRequest,

	/// <summary>
	/// The request was well-formed but breached a configured (or fail-closed
	/// misconfigured) risk control. A REJECTED audit row may be persisted for it.
	/// </summary>
	RiskLimit,
}

/// <summary>
/// Result of a <see cref="PreTradeRiskService.ValidateAsync(int, int, Sides, decimal, decimal?, OrderTypes, string, CancellationToken)"/> call.
/// </summary>
public class PreTradeRiskResult
{
	/// <summary>
	/// <see langword="true"/> if the order passed every configured pre-trade check.
	/// </summary>
	public bool IsValid { get; init; }

	/// <summary>
	/// Human-readable rejection reason when <see cref="IsValid"/> is
	/// <see langword="false"/>; otherwise <see langword="null"/>.
	/// </summary>
	public string RejectReason { get; init; }

	/// <summary>
	/// Classifies the rejection when <see cref="IsValid"/> is <see langword="false"/>
	/// (<see cref="PreTradeRejectionKind.InvalidRequest"/> for a malformed order,
	/// <see cref="PreTradeRejectionKind.RiskLimit"/> for a risk-control breach);
	/// <see cref="PreTradeRejectionKind.None"/> when the order was accepted.
	/// </summary>
	public PreTradeRejectionKind RejectionKind { get; init; }
}
