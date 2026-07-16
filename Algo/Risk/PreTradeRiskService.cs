namespace StockSharp.Algo.Risk;

using System.Data;
using System.Globalization;

using Microsoft.Data.SqlClient;

/// <summary>
/// Outcome of a pre-trade risk evaluation: whether the order is accepted, and if not, why.
/// </summary>
public class PreTradeRiskResult
{
	/// <summary>Whether the order passes every pre-trade check.</summary>
	public bool IsValid { get; init; }

	/// <summary>The rejection reason when <see cref="IsValid"/> is false; otherwise null.</summary>
	public string RejectReason { get; init; }

	/// <summary>Creates an accepting result.</summary>
	/// <returns>A result whose <see cref="IsValid"/> is <see langword="true"/>.</returns>
	public static PreTradeRiskResult Accept() => new() { IsValid = true };

	/// <summary>Creates a rejecting result with the supplied reason.</summary>
	/// <param name="reason">The rejection reason.</param>
	/// <returns>A result whose <see cref="IsValid"/> is <see langword="false"/> and whose <see cref="RejectReason"/> is <paramref name="reason"/>.</returns>
	public static PreTradeRiskResult Reject(string reason) => new() { IsValid = false, RejectReason = reason };
}

/// <summary>
/// The database-derived state the pure <see cref="PreTradeRiskService.Evaluate"/> needs, so it can be
/// evaluated with hand-fed values in unit tests without a live database.
/// </summary>
public class PreTradeState
{
	/// <summary>Count of this portfolio's orders inside the rolling frequency window (check 4).</summary>
	public int RecentOrderCount { get; init; }

	/// <summary>Current signed position quantity for the portfolio+security, or null when there is no position (check 5).</summary>
	public decimal? CurrentPositionQty { get; init; }

	/// <summary>Sum of qty*price across all of this portfolio's trades, or null when none (check 6).</summary>
	public decimal? ExistingTradesNotional { get; init; }

	/// <summary>Most recent trade price for the security, used as the estimate price for market orders, or null (check 6).</summary>
	public decimal? LastTradePrice { get; init; }

	/// <summary>Sum of today's accepted/filled/part-filled order quantities for the portfolio+security, or null (check 7).</summary>
	public decimal? TodayAcceptedVolume { get; init; }
}

/// <summary>
/// Per-order pre-trade risk gate. Ports the seven checks of dbo.usp_ValidatePreTradeRisk into C#,
/// resolving every threshold from the canonical <see cref="RiskLimitSet"/>. Unlike the
/// portfolio-wide circuit breaker <see cref="RiskManager"/>, a failing check rejects the single
/// order being validated. The pure decision logic (<see cref="Evaluate"/>) is separated from the
/// database reads (<see cref="ValidateAsync"/>) so it is unit-testable without a database.
/// </summary>
public class PreTradeRiskService
{
	private readonly string _connectionString;

	/// <summary>
	/// Initializes a new instance of <see cref="PreTradeRiskService"/>.
	/// </summary>
	/// <param name="connectionString">The StockSharpLegacy SQL Server connection string.</param>
	public PreTradeRiskService(string connectionString)
	{
		if (connectionString.IsEmpty())
			throw new ArgumentNullException(nameof(connectionString));

		_connectionString = connectionString;
	}

	private SqlConnection CreateConnection() => new(_connectionString);

	/// <summary>
	/// Evaluates the seven pre-trade checks against a resolved limit set and pre-fetched state,
	/// returning accept or the first failing reject reason. Pure and side-effect-free.
	/// </summary>
	/// <param name="limits">The most-specific limit set (may be null =&gt; nothing enforced).</param>
	/// <param name="state">The pre-fetched database state the checks need.</param>
	/// <param name="side">The order side.</param>
	/// <param name="volume">The order quantity (must be &gt; 0).</param>
	/// <param name="price">The order price, or null for a market order.</param>
	/// <returns>The evaluation outcome.</returns>
	public static PreTradeRiskResult Evaluate(RiskLimitSet limits, PreTradeState state, Sides side, decimal volume, decimal? price)
	{
		// Number-formatting helpers reproduce SQL Server's CONVERT(VARCHAR, DECIMAL) output
		// byte-for-byte: a DECIMAL(18,4) always renders with four fractional digits and a
		// DECIMAL(37,8) product with eight, using invariant culture (no thousands separators).
		static string F4(decimal d) => d.ToString("F4", CultureInfo.InvariantCulture);
		static string F8(decimal d) => d.ToString("F8", CultureInfo.InvariantCulture);

		// Preamble A - side. Sides is Buy/Sell, so the null (unknown) branch is only reachable
		// via an out-of-range cast; the parity reject string mirrors the SQL 'Invalid side: ' + @side.
		var sideCode = side switch
		{
			Sides.Buy => "B",
			Sides.Sell => "S",
			_ => null,
		};

		if (sideCode is null)
			return PreTradeRiskResult.Reject("Invalid side: " + side);

		// Preamble B - quantity. The non-nullable decimal parameter makes only the "<= 0"
		// half of the SQL "@qty IS NULL OR @qty <= 0" guard reachable here.
		if (volume <= 0)
			return PreTradeRiskResult.Reject("Invalid qty");

		// No configured limits at all => nothing to enforce (the SQL all-NULL short-circuit),
		// which is the same "unlimited" convention the C# RiskRule classes use for an unset threshold.
		if (limits is null || limits.IsUnlimited)
			return PreTradeRiskResult.Accept();

		// 1. order price ceiling - mirrors RiskOrderPriceRule (triggers when price >= limit).
		if (price is decimal p1 && limits.MaxOrderPrice is decimal mop && p1 >= mop)
			return PreTradeRiskResult.Reject("Order price " + F4(p1) + " meets/exceeds limit " + F4(mop));

		// 2. order qty ceiling - mirrors RiskOrderVolumeRule (triggers when qty >= limit).
		if (limits.MaxOrderQty is decimal moq && volume >= moq)
			return PreTradeRiskResult.Reject("Order qty " + F4(volume) + " meets/exceeds limit " + F4(moq));

		// 3. notional order value ceiling (qty * price). The product is a DECIMAL(37,8) in SQL,
		// so it is rendered with eight fractional digits while the limit stays DECIMAL(18,4).
		if (price is decimal p3 && limits.MaxOrderValue is decimal mov && (volume * p3) >= mov)
			return PreTradeRiskResult.Reject("Order value " + F8(volume * p3) + " meets/exceeds limit " + F4(mov));

		// 4. order frequency - the canonical rolling-count algorithm (COUNT over "now minus N
		// seconds"), which is at-least-as-strict as the old fixed-window bucketing. The "+1"
		// accounts for the candidate order. Count and window are config-driven from RiskLimitSet.
		if (limits.MaxOrderFreqCount is int cnt && limits.MaxOrderFreqWindowSeconds is int win)
		{
			var projected = state.RecentOrderCount + 1;

			if (projected >= cnt)
				return PreTradeRiskResult.Reject("Order frequency " + projected.ToString(CultureInfo.InvariantCulture) + " in " + win.ToString(CultureInfo.InvariantCulture) + "s meets/exceeds limit " + cnt.ToString(CultureInfo.InvariantCulture));
		}

		// 5. resulting position size ceiling - mirrors RiskPositionSizeRule, evaluated on the
		// hypothetical POST-fill position since this gate runs before the order is accepted.
		if (limits.MaxPositionSize is decimal mps)
		{
			var signedDelta = side == Sides.Buy ? volume : -volume;
			var resulting = (state.CurrentPositionQty ?? 0m) + signedDelta;

			if (Math.Abs(resulting) >= mps)
				return PreTradeRiskResult.Reject("Resulting position " + F4(resulting) + " meets/exceeds limit " + F4(mps));
		}

		// 6. cumulative commission ceiling - a PRE-fill estimate. Actual commission is not known
		// until execution, so cost is estimated with RiskLimitSet.CommissionRate against the order
		// price (or, for market orders, the security's last traded price). Rounded to four decimals
		// away from zero to mirror the DECIMAL(18,4) SQL variable before comparing and formatting.
		if (limits.MaxCommissionTotal is decimal mct)
		{
			var estPrice = price ?? state.LastTradePrice;
			var existingNotional = state.ExistingTradesNotional ?? 0m;
			var estCommission = Math.Round(existingNotional * limits.CommissionRate + volume * (estPrice ?? 0m) * limits.CommissionRate, 4, MidpointRounding.AwayFromZero);

			if (estCommission >= mct)
				return PreTradeRiskResult.Reject("Estimated cumulative commission " + F4(estCommission) + " meets/exceeds limit " + F4(mct));
		}

		// 7. daily traded volume ceiling.
		if (limits.MaxDailyVolume is decimal mdv)
		{
			var today = state.TodayAcceptedVolume ?? 0m;

			if (today + volume >= mdv)
				return PreTradeRiskResult.Reject("Daily volume " + F4(today + volume) + " meets/exceeds limit " + F4(mdv));
		}

		return PreTradeRiskResult.Accept();
	}

	/// <summary>
	/// Loads the most-specific limit set and the state the checks need from the StockSharpLegacy
	/// database, then applies <see cref="Evaluate"/>. This is the entry point the gateway calls in
	/// place of the old EXEC dbo.usp_ValidatePreTradeRisk.
	/// </summary>
	/// <param name="portfolioId">The portfolio identifier.</param>
	/// <param name="securityId">The security identifier.</param>
	/// <param name="side">The order side.</param>
	/// <param name="volume">The order quantity.</param>
	/// <param name="price">The order price, or null for a market order.</param>
	/// <param name="orderType">The order type (accepted for gateway parity; market vs limit is expressed by <paramref name="price"/> being null).</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The evaluation outcome.</returns>
	public async Task<PreTradeRiskResult> ValidateAsync(int portfolioId, int securityId, Sides side, decimal volume, decimal? price, OrderTypes orderType, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		// Load every candidate RiskLimits row in scope, then defer to the canonical selector so the
		// load path and the unit tests share ONE ordering (portfolio+security > portfolio > security,
		// most-recent effective_date wins). Reproduces the TOP(1) ... ORDER BY of the stored proc.
		var candidates = new List<RiskLimitSet>();

		await using (var limitsCommand = new SqlCommand(
			"""
			SELECT portfolio_id, security_id, max_order_price, max_order_qty, max_order_value,
			       max_position_size, max_daily_volume, max_order_freq_count, max_order_freq_window_sec,
			       max_commission_total, commission_rate, is_active, effective_date
			FROM dbo.RiskLimits
			WHERE is_active = 1
			  AND (portfolio_id = @p OR portfolio_id IS NULL)
			  AND (security_id = @s OR security_id IS NULL)
			""", connection)
		{
			CommandType = CommandType.Text,
		})
		{
			limitsCommand.Parameters.AddWithValue("@p", portfolioId);
			limitsCommand.Parameters.AddWithValue("@s", securityId);

			await using var reader = await limitsCommand.ExecuteReaderAsync(cancellationToken);

			while (await reader.ReadAsync(cancellationToken))
			{
				candidates.Add(new RiskLimitSet
				{
					PortfolioId = ReadNullableInt(reader, 0),
					SecurityId = ReadNullableInt(reader, 1),
					MaxOrderPrice = ReadNullableDecimal(reader, 2),
					MaxOrderQty = ReadNullableDecimal(reader, 3),
					MaxOrderValue = ReadNullableDecimal(reader, 4),
					MaxPositionSize = ReadNullableDecimal(reader, 5),
					MaxDailyVolume = ReadNullableDecimal(reader, 6),
					MaxOrderFreqCount = ReadNullableInt(reader, 7),
					MaxOrderFreqWindowSeconds = ReadNullableInt(reader, 8),
					MaxCommissionTotal = ReadNullableDecimal(reader, 9),
					CommissionRate = reader.GetDecimal(10),
					IsActive = reader.GetBoolean(11),
					EffectiveDate = reader.GetDateTime(12),
				});
			}
		}

		var limits = RiskLimitSet.SelectMostSpecific(candidates, portfolioId, securityId);

		// Nothing to enforce => skip every state read and accept (after Evaluate still runs the
		// side/quantity preamble), exactly like the SQL all-NULL short-circuit.
		if (limits is null || limits.IsUnlimited)
			return Evaluate(limits, new PreTradeState(), side, volume, price);

		// Read ONLY the state the configured ceilings require, mirroring the stored proc which
		// queries inside each IF. Every read reuses the single open connection.
		var recentOrderCount = 0;
		decimal? currentPositionQty = null;
		decimal? existingTradesNotional = null;
		decimal? lastTradePrice = null;
		decimal? todayAcceptedVolume = null;

		// Check 4 state - rolling order count. Let SQL Server compute the cutoff with
		// SYSUTCDATETIME() so the window is measured against the database clock.
		if (limits.MaxOrderFreqCount is not null && limits.MaxOrderFreqWindowSeconds is int win)
		{
			await using var command = new SqlCommand(
				"SELECT COUNT(*) FROM dbo.Orders WHERE portfolio_id = @p AND submitted_date >= DATEADD(SECOND, -@win, SYSUTCDATETIME())",
				connection)
			{
				CommandType = CommandType.Text,
			};
			command.Parameters.AddWithValue("@p", portfolioId);
			command.Parameters.AddWithValue("@win", win);

			var scalar = await command.ExecuteScalarAsync(cancellationToken);
			recentOrderCount = scalar is int c ? c : 0;
		}

		// Check 5 state - current signed position quantity (null when there is no position row).
		if (limits.MaxPositionSize is not null)
		{
			await using var command = new SqlCommand(
				"SELECT qty FROM dbo.Positions WHERE portfolio_id = @p AND security_id = @s",
				connection)
			{
				CommandType = CommandType.Text,
			};
			command.Parameters.AddWithValue("@p", portfolioId);
			command.Parameters.AddWithValue("@s", securityId);

			currentPositionQty = await ReadNullableDecimalScalarAsync(command, cancellationToken);
		}

		// Check 6 state - existing traded notional for the portfolio, plus (only for market orders,
		// i.e. price is null) the security's last traded price used as the estimate price.
		if (limits.MaxCommissionTotal is not null)
		{
			await using (var command = new SqlCommand(
				"SELECT SUM(t.qty * t.price) FROM dbo.Trades t JOIN dbo.Orders o ON o.order_id = t.order_id WHERE o.portfolio_id = @p",
				connection)
			{
				CommandType = CommandType.Text,
			})
			{
				command.Parameters.AddWithValue("@p", portfolioId);

				var notional = await ReadNullableDecimalScalarAsync(command, cancellationToken);
				// Round to four decimals away from zero to mirror the DECIMAL(18,4) SQL variable.
				existingTradesNotional = notional is null ? null : Math.Round(notional.Value, 4, MidpointRounding.AwayFromZero);
			}

			if (price is null)
			{
				await using var command = new SqlCommand(
					"SELECT TOP(1) t.price FROM dbo.Trades t JOIN dbo.Orders o ON o.order_id = t.order_id WHERE o.security_id = @s ORDER BY t.executed_date DESC",
					connection)
				{
					CommandType = CommandType.Text,
				};
				command.Parameters.AddWithValue("@s", securityId);

				lastTradePrice = await ReadNullableDecimalScalarAsync(command, cancellationToken);
			}
		}

		// Check 7 state - today's accepted/filled/part-filled volume for the portfolio+security.
		if (limits.MaxDailyVolume is not null)
		{
			await using var command = new SqlCommand(
				"""
				SELECT SUM(qty) FROM dbo.Orders
				WHERE portfolio_id = @p AND security_id = @s
				  AND status IN ('ACCEPTED','FILLED','PARTFILLED')
				  AND CAST(submitted_date AS DATE) = CAST(SYSUTCDATETIME() AS DATE)
				""", connection)
			{
				CommandType = CommandType.Text,
			};
			command.Parameters.AddWithValue("@p", portfolioId);
			command.Parameters.AddWithValue("@s", securityId);

			todayAcceptedVolume = await ReadNullableDecimalScalarAsync(command, cancellationToken);
		}

		var state = new PreTradeState
		{
			RecentOrderCount = recentOrderCount,
			CurrentPositionQty = currentPositionQty,
			ExistingTradesNotional = existingTradesNotional,
			LastTradePrice = lastTradePrice,
			TodayAcceptedVolume = todayAcceptedVolume,
		};

		return Evaluate(limits, state, side, volume, price);
	}

	// Reads a nullable DECIMAL column, treating SQL NULL as a C# null.
	private static decimal? ReadNullableDecimal(SqlDataReader reader, int ordinal)
		=> reader.IsDBNull(ordinal) ? (decimal?)null : reader.GetDecimal(ordinal);

	// Reads a nullable INT column, treating SQL NULL as a C# null.
	private static int? ReadNullableInt(SqlDataReader reader, int ordinal)
		=> reader.IsDBNull(ordinal) ? (int?)null : reader.GetInt32(ordinal);

	// Executes a scalar query returning a single DECIMAL, mapping both "no rows" (null) and
	// SQL NULL (DBNull) to a C# null so callers can apply the "not present" convention.
	private static async Task<decimal?> ReadNullableDecimalScalarAsync(SqlCommand command, CancellationToken cancellationToken)
	{
		var result = await command.ExecuteScalarAsync(cancellationToken);
		return result is null or DBNull ? (decimal?)null : (decimal)result;
	}
}
