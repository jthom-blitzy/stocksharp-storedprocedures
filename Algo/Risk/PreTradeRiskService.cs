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
/// database reads (<see cref="ValidateAsync(SqlConnection, SqlTransaction, int, int, Sides, decimal, decimal?, OrderTypes, CancellationToken)"/>)
/// so it is unit-testable without a database.
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
		// M04 - a null state means "no prior activity" (the baseline the SQL sees against empty tables).
		// The checks that read state only run when their ceiling is enforced, so defaulting is always safe.
		state ??= new PreTradeState();

		// Number-formatting helpers reproduce SQL Server's CONVERT(VARCHAR, DECIMAL) output
		// byte-for-byte: a DECIMAL(18,4) always renders with four fractional digits and a
		// DECIMAL(37,8) product with eight, using invariant culture (no thousands separators).
		static string F4(decimal d) => d.ToString("F4", CultureInfo.InvariantCulture);
		static string F8(decimal d) => d.ToString("F8", CultureInfo.InvariantCulture);

		// Preamble - side validity plus the DECIMAL(18,4) coercion of qty/price (C02). Extracted into a
		// shared helper so ValidateAsync can run these cheap, authoritative checks identically and BEFORE
		// any database access (M03). The normalized values are fed back into volume/price so every
		// downstream comparison and reject string uses the exact value that would be persisted.
		var preamble = TryPreamble(side, volume, price, out volume, out price);
		if (preamble is not null)
			return preamble;

		// No configured limits at all (every ceiling null OR non-positive) => nothing to enforce (the SQL
		// all-NULL short-circuit), the same canonical "unlimited" convention the RiskLimitSet model applies.
		if (limits is null || limits.IsUnlimited)
			return PreTradeRiskResult.Accept();

		// Every threshold below is read through the canonical Effective* projections on RiskLimitSet,
		// so a zero or negative stored ceiling is treated as "not enforced" instead of rejecting every
		// order (M01), and the frequency count/window are validated together as one positive pair (M02).

		// 1. order price ceiling - mirrors RiskOrderPriceRule (triggers when price >= limit).
		if (price is decimal p1 && limits.EffectiveMaxOrderPrice is decimal mop && p1 >= mop)
			return PreTradeRiskResult.Reject("Order price " + F4(p1) + " meets/exceeds limit " + F4(mop));

		// 2. order qty ceiling - mirrors RiskOrderVolumeRule (triggers when qty >= limit).
		if (limits.EffectiveMaxOrderQty is decimal moq && volume >= moq)
			return PreTradeRiskResult.Reject("Order qty " + F4(volume) + " meets/exceeds limit " + F4(moq));

		// 3. notional order value ceiling (qty * price). The product is a DECIMAL(37,8) in SQL,
		// so it is rendered with eight fractional digits while the limit stays DECIMAL(18,4). Both
		// operands are already normalized to DECIMAL(18,4) range above, so the product cannot overflow
		// the decimal type (max ~1e28 < decimal.MaxValue), matching the SQL DECIMAL(37,8) computation.
		if (price is decimal p3 && limits.EffectiveMaxOrderValue is decimal mov && (volume * p3) >= mov)
			return PreTradeRiskResult.Reject("Order value " + F8(volume * p3) + " meets/exceeds limit " + F4(mov));

		// 4. order frequency - the canonical rolling-count algorithm (COUNT over "now minus N
		// seconds"), which is at-least-as-strict as the old fixed-window bucketing. The "+1"
		// accounts for the candidate order. Count and window are a validated positive pair (M02).
		if (limits.IsFrequencyEnforced)
		{
			var cnt = limits.MaxOrderFreqCount.Value;
			var win = limits.MaxOrderFreqWindowSeconds.Value;
			var projected = state.RecentOrderCount + 1;

			if (projected >= cnt)
				return PreTradeRiskResult.Reject("Order frequency " + projected.ToString(CultureInfo.InvariantCulture) + " in " + win.ToString(CultureInfo.InvariantCulture) + "s meets/exceeds limit " + cnt.ToString(CultureInfo.InvariantCulture));
		}

		// 5. resulting position size ceiling - mirrors RiskPositionSizeRule, evaluated on the
		// hypothetical POST-fill position since this gate runs before the order is accepted.
		if (limits.EffectiveMaxPositionSize is decimal mps)
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
		// A negative configured rate is clamped to zero so a malformed rate cannot manufacture a
		// spurious reject (M04); the SQL seed never stores a negative rate.
		if (limits.EffectiveMaxCommissionTotal is decimal mct)
		{
			var rate = limits.CommissionRate < 0m ? 0m : limits.CommissionRate;
			var estPrice = price ?? state.LastTradePrice;
			var existingNotional = state.ExistingTradesNotional ?? 0m;
			var estCommission = Math.Round(existingNotional * rate + volume * (estPrice ?? 0m) * rate, 4, MidpointRounding.AwayFromZero);

			if (estCommission >= mct)
				return PreTradeRiskResult.Reject("Estimated cumulative commission " + F4(estCommission) + " meets/exceeds limit " + F4(mct));
		}

		// 7. daily traded volume ceiling.
		if (limits.EffectiveMaxDailyVolume is decimal mdv)
		{
			var today = state.TodayAcceptedVolume ?? 0m;

			if (today + volume >= mdv)
				return PreTradeRiskResult.Reject("Daily volume " + F4(today + volume) + " meets/exceeds limit " + F4(mdv));
		}

		return PreTradeRiskResult.Accept();
	}

	// Runs the side-validity and DECIMAL(18,4) coercion preamble that must precede every other check
	// (the SQL proc's 'Invalid side' / 'Invalid qty' guards). Returns a reject result on failure, or
	// null when the preamble passes. The coerced quantity and price are returned via out parameters so
	// callers (both Evaluate and ValidateAsync) reuse the exact persisted values - ValidateAsync uses
	// them to reject BEFORE opening any database resources (M03), while Evaluate flows them into the
	// seven checks. Sides is a Buy/Sell enum, so the unknown branch is only reachable via an
	// out-of-range cast; the reject strings mirror the SQL 'Invalid side: ' + @side and 'Invalid qty'.
	private static PreTradeRiskResult TryPreamble(Sides side, decimal volume, decimal? price, out decimal normalizedVolume, out decimal? normalizedPrice)
	{
		normalizedVolume = volume;
		normalizedPrice = price;

		if (side is not (Sides.Buy or Sides.Sell))
			return PreTradeRiskResult.Reject("Invalid side: " + side);

		// C02 / SQL parity - coerce the quantity to DECIMAL(18,4) BEFORE the zero-guard, so a sub-tick
		// value such as 0.00004 rounds to 0.0000 and is rejected as "Invalid qty" exactly as the SQL gate
		// does (its @qty parameter is DECIMAL(18,4)). An out-of-range magnitude fails deterministically.
		normalizedVolume = NormalizeMoney(volume, nameof(volume));

		if (normalizedVolume <= 0)
			return PreTradeRiskResult.Reject("Invalid qty");

		// Coerce the price to DECIMAL(18,4) too (a null price is a market order and stays null).
		if (price is decimal rawPrice)
			normalizedPrice = NormalizeMoney(rawPrice, nameof(price));

		return null;
	}

	// DECIMAL(18,4) domain guard shared by the pre-trade checks. The StockSharpLegacy money columns
	// (qty, price, and the derived notionals) are all DECIMAL(18,4); this rounds an incoming value to
	// that scale (away from zero, matching SQL Server's arithmetic-rounding of an assignment) and fails
	// deterministically when the magnitude cannot be represented, which is the C# analogue of the SQL
	// arithmetic-overflow error. Making the coercion explicit is what gives the gate byte-for-byte parity
	// with the persisted values (C02) and keeps every product overflow-safe (M04/M08).
	private const decimal _moneyMax = 99_999_999_999_999.9999m;

	private static decimal NormalizeMoney(decimal value, string name)
	{
		var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);

		if (rounded > _moneyMax || rounded < -_moneyMax)
			throw new OverflowException($"{name} value {value} is outside the DECIMAL(18,4) range supported by the StockSharpLegacy schema.");

		return rounded;
	}

	/// <summary>
	/// Loads the most-specific limit set and the state the checks need from the StockSharpLegacy
	/// database on a caller-supplied connection and transaction, then applies <see cref="Evaluate"/>.
	/// This is the entry point the gateway calls in place of the old EXEC dbo.usp_ValidatePreTradeRisk.
	/// </summary>
	/// <remarks>
	/// The connection and transaction are owned by the caller (the gateway), so the state reads here and
	/// the subsequent dbo.Orders INSERT execute inside a <b>single</b> READ COMMITTED transaction.
	/// Isolation alone would NOT close the check-then-act race; it is the gateway's per-portfolio
	/// application lock (<c>sp_getapplock</c>, held for the life of the transaction) that serializes
	/// concurrent submissions, so two concurrent orders can no longer both read stale state and both be
	/// accepted (C03/CWE-367).
	/// The cheap side/quantity preamble runs BEFORE any command so an invalid order is rejected without
	/// touching the database and a transient database fault can never mask that authoritative reject (M03).
	/// </remarks>
	/// <param name="connection">The open connection owned by the caller.</param>
	/// <param name="transaction">The transaction owned by the caller that every read enlists in.</param>
	/// <param name="portfolioId">The portfolio identifier (must be positive).</param>
	/// <param name="securityId">The security identifier (must be positive).</param>
	/// <param name="side">The order side (Buy or Sell).</param>
	/// <param name="volume">The order quantity.</param>
	/// <param name="price">The order price, or null for a market order.</param>
	/// <param name="orderType">The order type; only <see cref="OrderTypes.Limit"/> and <see cref="OrderTypes.Market"/> are supported.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The evaluation outcome.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="transaction"/> is null.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="portfolioId"/> or <paramref name="securityId"/> is not positive.</exception>
	/// <exception cref="NotSupportedException"><paramref name="orderType"/> is neither Limit nor Market.</exception>
	public async Task<PreTradeRiskResult> ValidateAsync(SqlConnection connection, SqlTransaction transaction, int portfolioId, int securityId, Sides side, decimal volume, decimal? price, OrderTypes orderType, CancellationToken cancellationToken = default)
	{
		if (connection is null)
			throw new ArgumentNullException(nameof(connection));

		if (transaction is null)
			throw new ArgumentNullException(nameof(transaction));

		// M04 - validate the whole public contract up front. The gateway resolves these identifiers from
		// dbo.Portfolios/dbo.Securities before calling, and only Limit/Market map to the legacy layer,
		// mirroring the gateway's MapOrderType (which raises NotSupportedException for conditional/stop).
		if (portfolioId <= 0)
			throw new ArgumentOutOfRangeException(nameof(portfolioId), portfolioId, "Portfolio id must be positive.");

		if (securityId <= 0)
			throw new ArgumentOutOfRangeException(nameof(securityId), securityId, "Security id must be positive.");

		if (orderType is not (OrderTypes.Limit or OrderTypes.Market))
			throw new NotSupportedException($"Order type '{orderType}' is not supported by the legacy risk gate; only Limit and Market are.");

		// M03 - run the cheap, authoritative side/quantity preamble FIRST, before touching the database,
		// so an invalid side or quantity is rejected even if the database is unreachable and a transient
		// database fault can never replace that reject. The coerced values are reused for every read below.
		var preamble = TryPreamble(side, volume, price, out volume, out price);

		if (preamble is not null)
			return preamble;

		// Load every candidate RiskLimits row in scope on the caller's connection+transaction, then defer
		// to the canonical selector so the load path and the unit tests share ONE ordering
		// (portfolio+security > portfolio > security, most-recent effective_date wins). The WHERE clause
		// bounds the result to at most the rows that can apply, reproducing the proc's TOP(1) ... ORDER BY.
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
			""", connection, transaction)
		{
			CommandType = CommandType.Text,
		})
		{
			limitsCommand.Parameters.Add(IntParam("@p", portfolioId));
			limitsCommand.Parameters.Add(IntParam("@s", securityId));

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

		// Nothing to enforce => skip every state read and accept (Evaluate still runs the side/quantity
		// preamble), exactly like the SQL all-NULL short-circuit and the canonical "unlimited" convention.
		if (limits is null || limits.IsUnlimited)
			return Evaluate(limits, new PreTradeState(), side, volume, price);

		// Read ONLY the state the enforced ceilings require, in the stored proc's check order (4->7), each
		// on the caller's connection+transaction. Ceilings are tested through the canonical Effective*
		// projections / IsFrequencyEnforced so a zero or malformed threshold never triggers a state read.
		var recentOrderCount = 0;
		decimal? currentPositionQty = null;
		decimal? existingTradesNotional = null;
		decimal? lastTradePrice = null;
		decimal? todayAcceptedVolume = null;

		// Check 4 state - rolling order count. SQL Server computes the cutoff with SYSUTCDATETIME() so the
		// window is measured against the database clock; the bare column keeps the predicate sargable.
		if (limits.IsFrequencyEnforced)
		{
			var win = limits.MaxOrderFreqWindowSeconds.Value;

			await using var command = new SqlCommand(
				"SELECT COUNT(*) FROM dbo.Orders WHERE portfolio_id = @p AND submitted_date >= DATEADD(SECOND, -@win, SYSUTCDATETIME())",
				connection, transaction)
			{
				CommandType = CommandType.Text,
			};
			command.Parameters.Add(IntParam("@p", portfolioId));
			command.Parameters.Add(IntParam("@win", win));

			var scalar = await command.ExecuteScalarAsync(cancellationToken);
			recentOrderCount = scalar is int c ? c : 0;
		}

		// Check 5 state - current signed position quantity (null when there is no position row).
		if (limits.EffectiveMaxPositionSize is not null)
		{
			await using var command = new SqlCommand(
				"SELECT qty FROM dbo.Positions WHERE portfolio_id = @p AND security_id = @s",
				connection, transaction)
			{
				CommandType = CommandType.Text,
			};
			command.Parameters.Add(IntParam("@p", portfolioId));
			command.Parameters.Add(IntParam("@s", securityId));

			currentPositionQty = await ReadNullableDecimalScalarAsync(command, cancellationToken);
		}

		// Check 6 state - existing traded notional for the portfolio, plus (only for market orders,
		// i.e. price is null) the security's last traded price used as the estimate price.
		if (limits.EffectiveMaxCommissionTotal is not null)
		{
			await using (var command = new SqlCommand(
				"SELECT SUM(t.qty * t.price) FROM dbo.Trades t JOIN dbo.Orders o ON o.order_id = t.order_id WHERE o.portfolio_id = @p",
				connection, transaction)
			{
				CommandType = CommandType.Text,
			})
			{
				command.Parameters.Add(IntParam("@p", portfolioId));

				var notional = await ReadNullableDecimalScalarAsync(command, cancellationToken);
				// Round to four decimals away from zero to mirror the DECIMAL(18,4) SQL variable.
				existingTradesNotional = notional is null ? null : Math.Round(notional.Value, 4, MidpointRounding.AwayFromZero);
			}

			if (price is null)
			{
				await using var command = new SqlCommand(
					"SELECT TOP(1) t.price FROM dbo.Trades t JOIN dbo.Orders o ON o.order_id = t.order_id WHERE o.security_id = @s ORDER BY t.executed_date DESC",
					connection, transaction)
				{
					CommandType = CommandType.Text,
				};
				command.Parameters.Add(IntParam("@s", securityId));

				lastTradePrice = await ReadNullableDecimalScalarAsync(command, cancellationToken);
			}
		}

		// Check 7 state - today's accepted/filled/part-filled volume for the portfolio+security.
		// M06 - use a half-open UTC-day range [start, next) whose bounds are runtime constants derived from
		// the database clock (SYSUTCDATETIME). The column stays bare, so the predicate is sargable and can
		// seek IX_Orders_portfolio_submitted, while preserving the original CAST(... AS DATE) UTC-day semantics.
		if (limits.EffectiveMaxDailyVolume is not null)
		{
			await using var command = new SqlCommand(
				"""
				SELECT SUM(qty) FROM dbo.Orders
				WHERE portfolio_id = @p AND security_id = @s
				  AND status IN ('ACCEPTED','FILLED','PARTFILLED')
				  AND submitted_date >= CAST(CAST(SYSUTCDATETIME() AS DATE) AS DATETIME2)
				  AND submitted_date <  DATEADD(DAY, 1, CAST(CAST(SYSUTCDATETIME() AS DATE) AS DATETIME2))
				""", connection, transaction)
			{
				CommandType = CommandType.Text,
			};
			command.Parameters.Add(IntParam("@p", portfolioId));
			command.Parameters.Add(IntParam("@s", securityId));

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

	/// <summary>
	/// Convenience overload that opens its own connection and transaction for a stand-alone pre-trade
	/// evaluation (for example ad-hoc validation or tests). The gateway uses the transaction-accepting
	/// overload so that validation and the order INSERT share one atomic unit; this overload performs a
	/// read-only snapshot and commits without writing any rows.
	/// </summary>
	/// <param name="portfolioId">The portfolio identifier (must be positive).</param>
	/// <param name="securityId">The security identifier (must be positive).</param>
	/// <param name="side">The order side (Buy or Sell).</param>
	/// <param name="volume">The order quantity.</param>
	/// <param name="price">The order price, or null for a market order.</param>
	/// <param name="orderType">The order type; only <see cref="OrderTypes.Limit"/> and <see cref="OrderTypes.Market"/> are supported.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The evaluation outcome.</returns>
	public async Task<PreTradeRiskResult> ValidateAsync(int portfolioId, int securityId, Sides side, decimal volume, decimal? price, OrderTypes orderType, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);
		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

		var result = await ValidateAsync(connection, transaction, portfolioId, securityId, side, volume, price, orderType, cancellationToken);

		// Read-only path: commit to release the read locks cleanly (no rows were written).
		await transaction.CommitAsync(cancellationToken);

		return result;
	}

	// Builds an explicitly-typed integer parameter (SqlDbType.Int) instead of relying on AddWithValue's
	// runtime type inference, which gives every query a stable, plan-cache-friendly parameter shape (M05).
	private static SqlParameter IntParam(string name, int value)
		=> new(name, SqlDbType.Int) { Value = value };

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
