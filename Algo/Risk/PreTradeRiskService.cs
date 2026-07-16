namespace StockSharp.Algo.Risk;

using System.Data;

using Microsoft.Data.SqlClient;

/// <summary>
/// Per-order pre-trade risk GATE. C# replacement for the SQL proc
/// dbo.usp_ValidatePreTradeRisk: validates a single prospective order against
/// the most-specific configured RiskLimits row and returns accept/reject with a
/// descriptive reason on the first failing rule.
/// </summary>
/// <remarks>
/// One of the two distinct, never-merged enforcement patterns that consume the
/// canonical <see cref="IRiskRule"/> definitions (the other is the stream-based
/// <see cref="RiskManager"/> circuit breaker). Where a rule is a pure threshold
/// comparison the gate executes the canonical rule class
/// (<see cref="RiskOrderPriceRule"/>, <see cref="RiskOrderVolumeRule"/>,
/// <see cref="RiskOrderValueRule"/>, <see cref="RiskDailyVolumeRule"/>); where a
/// rule needs historical/positional data it applies the same canonical threshold
/// to an aggregate/projection read from SQL Server. Every comparison uses the
/// ">=" ("meets or exceeds") boundary and a NULL/0 threshold means "not enforced".
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

	/// <summary>
	/// Validates a prospective order against the applicable RiskLimits, porting
	/// dbo.usp_ValidatePreTradeRisk RULES 1-7 in order (first failure wins).
	/// </summary>
	public async Task<PreTradeRiskResult> ValidateAsync(
		int portfolioId, int securityId, Sides side, decimal qty, decimal? price,
		OrderTypes orderType, string requestedBy = null, CancellationToken cancellationToken = default)
	{
		// --- input pre-checks (SQL L73-85) ---------------------------------
		// Sides only models Buy/Sell, but guard defensively to mirror the SQL
		// "side NOT IN ('B','S')" pre-check exactly.
		if (side != Sides.Buy && side != Sides.Sell)
			return Reject($"Invalid side: {side}");

		if (qty <= 0)
			return Reject("Invalid qty");

		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

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
			""", connection))
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

		// Prospective-order message fed to the canonical pure-threshold rules: the
		// gate executes the SAME rule comparison the RiskManager circuit breaker uses.
		var orderMessage = new OrderRegisterMessage
		{
			Side = side,
			Volume = qty,
			Price = price ?? 0m,
		};

		// RULE 1 - order price ceiling (SQL L129-134): canonical RiskOrderPriceRule.
		// ">=" preserved; NULL/0 = not enforced; only when both price and limit exist.
		if (price.HasValue && maxOrderPrice > 0)
		{
			if (new RiskOrderPriceRule { Price = maxOrderPrice.Value }.ProcessMessage(orderMessage))
				return Reject($"Order price {price.Value} meets/exceeds limit {maxOrderPrice.Value}");
		}

		// RULE 2 - order qty ceiling (SQL L136-141): canonical RiskOrderVolumeRule.
		if (maxOrderQty > 0)
		{
			if (new RiskOrderVolumeRule { Volume = maxOrderQty.Value }.ProcessMessage(orderMessage))
				return Reject($"Order qty {qty} meets/exceeds limit {maxOrderQty.Value}");
		}

		// RULE 3 - notional value ceiling (SQL L143-148): canonical RiskOrderValueRule,
		// relocated from SQL-only max_order_value. qty*price >= limit.
		if (price.HasValue && maxOrderValue > 0)
		{
			if (new RiskOrderValueRule { OrderValue = maxOrderValue.Value }.ProcessMessage(orderMessage))
				return Reject($"Order value {qty * price.Value} meets/exceeds limit {maxOrderValue.Value}");
		}

		// RULE 4 - order frequency (SQL L161-175): canonical rolling-window RiskOrderFreqRule.
		// The rule class owns the rolling algorithm for the stream path; here the gate
		// reproduces the SAME true rolling COUNT (orders within "now - window") from SQL
		// and rejects when recentCount + 1 >= count. Rolling wins under stricter-wins
		// (AAP 0.6.3); ">=" preserved.
		if (maxOrderFreqCount > 0 && maxOrderFreqWindowSec > 0)
		{
			int recentCount;

			await using (var freqCmd = new SqlCommand(
				"SELECT COUNT(*) FROM dbo.Orders WHERE portfolio_id = @portfolio_id AND submitted_date >= DATEADD(SECOND, -@window_sec, SYSUTCDATETIME())",
				connection))
			{
				freqCmd.Parameters.AddWithValue("@portfolio_id", portfolioId);
				freqCmd.Parameters.AddWithValue("@window_sec", maxOrderFreqWindowSec.Value);

				recentCount = (int)(await freqCmd.ExecuteScalarAsync(cancellationToken));
			}

			if (recentCount + 1 >= maxOrderFreqCount.Value)
				return Reject($"Order frequency {recentCount + 1} in {maxOrderFreqWindowSec.Value}s meets/exceeds limit {maxOrderFreqCount.Value}");
		}

		// RULE 5 - resulting position size (SQL L177-192): shared RiskPositionSizeRule
		// definition applied at the POST-FILL projection (current + signed order qty).
		// The gate uses a symmetric ABS ceiling (SQL semantics) so the limit is enforced
		// in both directions before acceptance. Dropping the projection would loosen the
		// gate and violate the stricter-wins hard constraint (AAP 0.6.2/0.6.4).
		if (maxPositionSize > 0)
		{
			decimal currentQty;

			await using (var posCmd = new SqlCommand(
				"SELECT qty FROM dbo.Positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id",
				connection))
			{
				posCmd.Parameters.AddWithValue("@portfolio_id", portfolioId);
				posCmd.Parameters.AddWithValue("@security_id", securityId);

				var raw = await posCmd.ExecuteScalarAsync(cancellationToken);
				currentQty = raw is decimal q ? q : 0m;
			}

			var signedDelta = side == Sides.Buy ? qty : -qty;
			var projected = currentQty + signedDelta;

			if (Math.Abs(projected) >= maxPositionSize.Value)
				return Reject($"Resulting position {projected} meets/exceeds limit {maxPositionSize.Value}");
		}

		// RULE 6 - cumulative commission (SQL L199-224): PRE-FILL ESTIMATE. This is
		// DELIBERATELY DIFFERENT from the actual post-fill RiskCommissionRule /
		// RiskOrderCommissionRule / RiskTransactionCommissionRule, which read the real
		// commission off ExecutionMessage after a fill. Different data-availability
		// windows: the gate can only estimate before the trade exists, so it uses
		// RiskLimits.commission_rate against the order price (or the security's last
		// traded price for a market order). Kept separate by design (AAP 0.6.4); the
		// circuit-breaker path keeps the actual figure. ">=" preserved.
		if (maxCommissionTotal > 0)
		{
			var rate = commissionRate ?? _defaultCommissionRate;
			var estPrice = price;

			if (estPrice is null)
			{
				await using var lastPriceCmd = new SqlCommand(
					"""
					SELECT TOP (1) t.price
					FROM dbo.Trades t
					JOIN dbo.Orders o ON o.order_id = t.order_id
					WHERE o.security_id = @security_id
					ORDER BY t.executed_date DESC
					""", connection);
				lastPriceCmd.Parameters.AddWithValue("@security_id", securityId);

				var rawPrice = await lastPriceCmd.ExecuteScalarAsync(cancellationToken);
				estPrice = rawPrice is decimal lp ? lp : (decimal?)null;
			}

			decimal existingNotional;

			await using (var notionalCmd = new SqlCommand(
				"""
				SELECT SUM(t.qty * t.price)
				FROM dbo.Trades t
				JOIN dbo.Orders o ON o.order_id = t.order_id
				WHERE o.portfolio_id = @portfolio_id
				""", connection))
			{
				notionalCmd.Parameters.AddWithValue("@portfolio_id", portfolioId);

				var rawNotional = await notionalCmd.ExecuteScalarAsync(cancellationToken);
				existingNotional = rawNotional is decimal n ? n : 0m;
			}

			var estCommission = existingNotional * rate + qty * (estPrice ?? 0m) * rate;

			if (estCommission >= maxCommissionTotal.Value)
				return Reject($"Estimated cumulative commission {estCommission} meets/exceeds limit {maxCommissionTotal.Value}");
		}

		// RULE 7 - daily traded volume (SQL L226-243): canonical RiskDailyVolumeRule,
		// relocated from SQL-only max_daily_volume. The rule holds the threshold + ">=";
		// the gate supplies today's accepted/filled volume (from SQL) plus the new qty
		// as the effective volume.
		if (maxDailyVolume > 0)
		{
			decimal todayQty;

			await using (var dailyCmd = new SqlCommand(
				"""
				SELECT SUM(qty)
				FROM dbo.Orders
				WHERE portfolio_id = @portfolio_id
					AND security_id = @security_id
					AND status IN ('ACCEPTED','FILLED','PARTFILLED')
					AND CAST(submitted_date AS DATE) = CAST(SYSUTCDATETIME() AS DATE)
				""", connection))
			{
				dailyCmd.Parameters.AddWithValue("@portfolio_id", portfolioId);
				dailyCmd.Parameters.AddWithValue("@security_id", securityId);

				var rawToday = await dailyCmd.ExecuteScalarAsync(cancellationToken);
				todayQty = rawToday is decimal d ? d : 0m;
			}

			var dailyMessage = new OrderRegisterMessage { Side = side, Volume = todayQty + qty, Price = price ?? 0m };

			if (new RiskDailyVolumeRule { DailyVolume = maxDailyVolume.Value }.ProcessMessage(dailyMessage))
				return Reject($"Daily volume {todayQty + qty} meets/exceeds limit {maxDailyVolume.Value}");
		}

		return Accept();
	}

	private static PreTradeRiskResult Accept() => new() { IsValid = true };

	private static PreTradeRiskResult Reject(string reason) => new() { IsValid = false, RejectReason = reason };

	private static decimal? GetNullableDecimal(SqlDataReader reader, int ordinal)
		=> reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

	private static int? GetNullableInt(SqlDataReader reader, int ordinal)
		=> reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}

/// <summary>
/// Result of a <see cref="PreTradeRiskService.ValidateAsync"/> call.
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
}
