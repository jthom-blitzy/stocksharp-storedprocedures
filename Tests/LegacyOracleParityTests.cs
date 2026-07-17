namespace StockSharp.Tests;

using System.Data;

using Microsoft.Data.SqlClient;

using StockSharp.Algo.Risk;
using StockSharp.Algo.Storages.Sql;

/// <summary>
/// Characterization-first DIFFERENTIAL parity tests (review finding MA-17). These do not hard-code
/// expected values that merely mirror the new implementation; instead they execute the committed,
/// authoritative LEGACY stored procedures (Tests/Fixtures/LegacyRiskOracle.sql - a verbatim snapshot of
/// the pre-refactor dbo.usp_ValidatePreTradeRisk / dbo.usp_RecalculatePositionOnTrade) side-by-side with
/// the new C# services (<see cref="PreTradeRiskService"/> / <see cref="PositionRecalculationService"/>)
/// over identical database state, and assert the two agree.
///
/// <para>Pre-trade gate: for each of the seven checks (price, qty, notional value, frequency, resulting
/// position size, cumulative commission, daily volume) plus the accept case, the legacy proc and the C#
/// gate must return the SAME accept/reject decision AND the SAME reject-reason string.</para>
///
/// <para>Position recompute: for every branch (open, same-side add/average, partial close, exact close,
/// long-&gt;short flip) the legacy proc and the C# service, applied trade-by-trade over identical trade
/// sequences in two isolated scopes, must persist the SAME qty/avg_price/realized_pnl.</para>
///
/// <para>The oracle procedures are (re)created inside a transaction that is always rolled back, so the
/// fixture never persists in the shared database and cannot be mistaken for production business logic.
/// Each test uses fresh, collision-free scopes and the whole layer is gated on database reachability via
/// Inconclusive (AAP 0.6.7). Documented intentional divergences (zero-ceiling = not-enforced, MA-16) use
/// positive limits here and are covered separately, so they do not weaken this parity proof.</para>
/// </summary>
[TestClass]
[DoNotParallelize] // Live tests open real StockSharpLegacy transactions holding locks on shared tables.
public class LegacyOracleParityTests : BaseTestClass
{
	// ---- Pre-trade gate differential parity (all seven checks + accept) -------------------------------

	[TestMethod]
	public Task Live_Parity_Accept()
		=> RunParityAsync(async (c, t, gate, _) =>
		{
			var (pid, sid) = await InsertScopeAsync(c, t);
			await InsertSeededLimitsAsync(c, t, pid, sid);

			// A fully compliant order: below every ceiling. Both must ACCEPT (reason null).
			await AssertGateParityAsync(c, t, gate, pid, sid, 'B', 100m, 150m, OrderTypes.Limit);
		});

	[TestMethod]
	public Task Live_Parity_PriceCeiling()
		=> RunParityAsync(async (c, t, gate, _) =>
		{
			var (pid, sid) = await InsertScopeAsync(c, t);
			await InsertSeededLimitsAsync(c, t, pid, sid);

			// price 500 >= max_order_price 500 -> check #1 trips first in both.
			await AssertGateParityAsync(c, t, gate, pid, sid, 'B', 100m, 500m, OrderTypes.Limit);
		});

	[TestMethod]
	public Task Live_Parity_QtyCeiling()
		=> RunParityAsync(async (c, t, gate, _) =>
		{
			var (pid, sid) = await InsertScopeAsync(c, t);
			await InsertSeededLimitsAsync(c, t, pid, sid);

			// qty 10000 >= max_order_qty 10000 (price 10 < 500 passes check #1) -> check #2.
			await AssertGateParityAsync(c, t, gate, pid, sid, 'B', 10000m, 10m, OrderTypes.Limit);
		});

	[TestMethod]
	public Task Live_Parity_NotionalValueCeiling()
		=> RunParityAsync(async (c, t, gate, _) =>
		{
			var (pid, sid) = await InsertScopeAsync(c, t);
			await InsertSeededLimitsAsync(c, t, pid, sid);

			// qty*price = 5000*300 = 1,500,000 >= max_order_value 1,000,000 (price/qty pass) -> check #3.
			await AssertGateParityAsync(c, t, gate, pid, sid, 'B', 5000m, 300m, OrderTypes.Limit);
		});

	[TestMethod]
	public Task Live_Parity_FrequencyCeiling()
		=> RunParityAsync(async (c, t, gate, _) =>
		{
			var (pid, sid) = await InsertScopeAsync(c, t);
			await InsertSeededLimitsAsync(c, t, pid, sid);

			// Four recent orders in the portfolio; the fifth (this one) makes recentCount+1 = 5 >= 5.
			// Both the legacy proc and the C# gate COUNT(*) dbo.Orders in the rolling window -> check #4.
			for (var i = 0; i < 4; i++)
				await InsertOrderAsync(c, t, pid, sid, 'B', 1m, 1m, "ACCEPTED");

			await AssertGateParityAsync(c, t, gate, pid, sid, 'B', 1m, 1m, OrderTypes.Limit);
		});

	[TestMethod]
	public Task Live_Parity_PositionSizeCeiling()
		=> RunParityAsync(async (c, t, gate, _) =>
		{
			var (pid, sid) = await InsertScopeAsync(c, t);
			await InsertSeededLimitsAsync(c, t, pid, sid);

			// Existing long 99,000; buying 2,000 -> resulting |101,000| >= max_position_size 100,000.
			await InsertPositionAsync(c, t, pid, sid, 99000m);
			await AssertGateParityAsync(c, t, gate, pid, sid, 'B', 2000m, 10m, OrderTypes.Limit);
		});

	[TestMethod]
	public Task Live_Parity_CommissionCeiling()
		=> RunParityAsync(async (c, t, gate, _) =>
		{
			var (pid, sid) = await InsertScopeAsync(c, t);
			await InsertSeededLimitsAsync(c, t, pid, sid);

			// Existing filled notional 10,000,000 (one prior trade). Estimated cumulative commission for a
			// tiny new order = 10,000,000*0.0005 + ~0 = 5000.0005 >= max_commission_total 5000 -> check #6.
			var priorOrder = await InsertOrderAsync(c, t, pid, sid, 'B', 10000m, 1000m, "FILLED");
			await InsertTradeAsync(c, t, priorOrder, 10000m, 1000m);

			await AssertGateParityAsync(c, t, gate, pid, sid, 'B', 1m, 1m, OrderTypes.Limit);
		});

	[TestMethod]
	public Task Live_Parity_DailyVolumeCeiling()
		=> RunParityAsync(async (c, t, gate, _) =>
		{
			var (pid, sid) = await InsertScopeAsync(c, t);
			await InsertSeededLimitsAsync(c, t, pid, sid);

			// Accepted 249,000 today for the security; buying 2,000 -> 251,000 >= max_daily_volume 250,000.
			await InsertOrderAsync(c, t, pid, sid, 'B', 249000m, 10m, "ACCEPTED");
			await AssertGateParityAsync(c, t, gate, pid, sid, 'B', 2000m, 10m, OrderTypes.Limit);
		});

	[TestMethod]
	public Task Live_Parity_MarketOrderNullPrice()
		=> RunParityAsync(async (c, t, gate, _) =>
		{
			var (pid, sid) = await InsertScopeAsync(c, t);
			await InsertSeededLimitsAsync(c, t, pid, sid);

			// A MARKET order (null price) skips the price and notional-value checks in both implementations;
			// with no other breach it accepts. Proves the null-price path agrees too.
			await AssertGateParityAsync(c, t, gate, pid, sid, 'B', 100m, null, OrderTypes.Market);
		});

	// ---- Position recompute differential parity (every branch) ----------------------------------------

	[TestMethod]
	public Task Live_Parity_Position_OpenLong()
		=> RunParityAsync((c, t, _, svc) => AssertPositionParityAsync(c, t, svc,
			[('B', 100m, 150m)]));

	[TestMethod]
	public Task Live_Parity_Position_SameSideAverage()
		=> RunParityAsync((c, t, _, svc) => AssertPositionParityAsync(c, t, svc,
			[('B', 100m, 150m), ('B', 100m, 160m)]));

	[TestMethod]
	public Task Live_Parity_Position_PartialClose()
		=> RunParityAsync((c, t, _, svc) => AssertPositionParityAsync(c, t, svc,
			[('B', 100m, 150m), ('S', 40m, 170m)]));

	[TestMethod]
	public Task Live_Parity_Position_ExactClose()
		=> RunParityAsync((c, t, _, svc) => AssertPositionParityAsync(c, t, svc,
			[('B', 100m, 150m), ('S', 100m, 170m)]));

	[TestMethod]
	public Task Live_Parity_Position_FlipLongToShort()
		=> RunParityAsync((c, t, _, svc) => AssertPositionParityAsync(c, t, svc,
			[('B', 100m, 150m), ('S', 250m, 200m)]));

	[TestMethod]
	public Task Live_Parity_Position_FlipShortToLong()
		=> RunParityAsync((c, t, _, svc) => AssertPositionParityAsync(c, t, svc,
			[('S', 100m, 200m), ('B', 250m, 150m)]));

	// ==================================================================================================
	// Differential assertion helpers
	// ==================================================================================================

	// Runs the legacy gate proc and the C# gate over the SAME connection/transaction (identical state) and
	// asserts they return the same decision and the same reject-reason string.
	private static async Task AssertGateParityAsync(SqlConnection c, SqlTransaction t, PreTradeRiskService gate,
		int pid, int sid, char side, decimal qty, decimal? price, OrderTypes orderType)
	{
		var legacy = await RunLegacyGateAsync(c, t, pid, sid, side, qty, price,
			orderType == OrderTypes.Market ? "MARKET" : "LIMIT");

		var csharp = await gate.ValidateAsync(c, t, pid, sid,
			side == 'B' ? Sides.Buy : Sides.Sell, qty, price, orderType);

		csharp.IsValid.AssertEqual(legacy.IsValid);
		// Compare reason strings with a sentinel so an accept (null) on one side and a reject on the other
		// fails loudly rather than both collapsing to null-equals-null.
		(csharp.RejectReason ?? "<accept>").AssertEqual(legacy.Reason ?? "<accept>");
	}

	// Replays an identical trade sequence in two isolated scopes - one driven by the legacy proc applied
	// trade-by-trade (as the removed trigger did), one by the C# service - and asserts the persisted
	// positions are identical.
	private async Task AssertPositionParityAsync(SqlConnection c, SqlTransaction t,
		PositionRecalculationService svc, (char Side, decimal Qty, decimal Price)[] trades)
	{
		var (pidL, sidL) = await InsertScopeAsync(c, t);
		foreach (var tr in trades)
		{
			var oid = await InsertOrderAsync(c, t, pidL, sidL, tr.Side, tr.Qty, tr.Price, "ACCEPTED");
			await InsertTradeAsync(c, t, oid, tr.Qty, tr.Price);
			await RunLegacyRecalcAsync(c, t, oid, tr.Qty, tr.Price);
		}
		var legacyPos = await ReadPositionAsync(c, t, pidL, sidL);

		var (pidC, sidC) = await InsertScopeAsync(c, t);
		foreach (var tr in trades)
		{
			var oid = await InsertOrderAsync(c, t, pidC, sidC, tr.Side, tr.Qty, tr.Price, "ACCEPTED");
			await InsertTradeAsync(c, t, oid, tr.Qty, tr.Price);
			await svc.ApplyTradeAsync(c, t, oid);
		}
		var csharpPos = await ReadPositionAsync(c, t, pidC, sidC);

		legacyPos.HasValue.AssertTrue();
		csharpPos.HasValue.AssertTrue();
		csharpPos.Value.Qty.AssertEqual(legacyPos.Value.Qty);
		csharpPos.Value.Avg.AssertEqual(legacyPos.Value.Avg);
		csharpPos.Value.Realized.AssertEqual(legacyPos.Value.Realized);
	}

	// ==================================================================================================
	// Oracle fixture + scope infrastructure
	// ==================================================================================================

	// Opens a real connection, begins a ReadCommitted transaction, (re)creates the legacy oracle
	// procedures inside it, runs the body, and ALWAYS rolls back (dropping the oracle procs and every row).
	private async Task RunParityAsync(Func<SqlConnection, SqlTransaction, PreTradeRiskService, PositionRecalculationService, Task> body)
	{
		SqlConnection connection;

		try
		{
			connection = new SqlConnection(SqlLegacyConnection.Resolve());
			await connection.OpenAsync();
		}
		catch (Exception)
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping legacy-oracle parity test.");
			return;
		}

		await using (connection)
		{
			await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

			try
			{
				await CreateOracleAsync(connection, transaction);

				var gate = new PreTradeRiskService(SqlLegacyConnection.Resolve());
				var recalc = new PositionRecalculationService(SqlLegacyConnection.Resolve());

				await body(connection, transaction, gate, recalc);
			}
			finally
			{
				await transaction.RollbackAsync();
			}
		}
	}

	private static string[] _oracleBatches;

	// Loads and splits the committed fixture into individual batches (on GO), then executes each within the
	// caller's transaction so the CREATE OR ALTER PROCEDURE statements are undone on rollback.
	private static async Task CreateOracleAsync(SqlConnection c, SqlTransaction t)
	{
		_oracleBatches ??= LoadOracleBatches();

		foreach (var batch in _oracleBatches)
		{
			await using var cmd = new SqlCommand(batch, c, t);
			await cmd.ExecuteNonQueryAsync();
		}
	}

	private static string[] LoadOracleBatches()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyRiskOracle.sql");
		var text = File.ReadAllText(path);

		return System.Text.RegularExpressions.Regex
			.Split(text, @"(?im)^\s*GO\s*$")
			.Where(b => !string.IsNullOrWhiteSpace(b))
			.ToArray();
	}

	// ---- legacy proc invocations ----------------------------------------------------------------------

	private static async Task<(bool IsValid, string Reason)> RunLegacyGateAsync(SqlConnection c, SqlTransaction t,
		int pid, int sid, char side, decimal qty, decimal? price, string orderType)
	{
		await using var cmd = new SqlCommand("dbo.usp_ValidatePreTradeRisk_Legacy", c, t)
		{
			CommandType = CommandType.StoredProcedure
		};

		cmd.Parameters.Add(IntParam("@portfolio_id", pid));
		cmd.Parameters.Add(IntParam("@security_id", sid));
		cmd.Parameters.Add(new SqlParameter("@side", SqlDbType.Char, 1) { Value = side.ToString() });
		cmd.Parameters.Add(Money("@qty", qty));
		cmd.Parameters.Add(new SqlParameter("@price", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = (object)price ?? DBNull.Value });
		cmd.Parameters.Add(new SqlParameter("@order_type", SqlDbType.VarChar, 10) { Value = orderType });
		cmd.Parameters.Add(new SqlParameter("@requested_by", SqlDbType.NVarChar, 50) { Value = DBNull.Value });

		var isValid = new SqlParameter("@is_valid", SqlDbType.Bit) { Direction = ParameterDirection.Output };
		var reason = new SqlParameter("@reject_reason", SqlDbType.NVarChar, 200) { Direction = ParameterDirection.Output };
		cmd.Parameters.Add(isValid);
		cmd.Parameters.Add(reason);

		await cmd.ExecuteNonQueryAsync();

		return ((bool)isValid.Value, reason.Value is DBNull ? null : (string)reason.Value);
	}

	private static async Task RunLegacyRecalcAsync(SqlConnection c, SqlTransaction t, long orderId, decimal qty, decimal price)
	{
		await using var cmd = new SqlCommand("dbo.usp_RecalculatePositionOnTrade_Legacy", c, t)
		{
			CommandType = CommandType.StoredProcedure
		};
		cmd.Parameters.Add(new SqlParameter("@order_id", SqlDbType.BigInt) { Value = orderId });
		cmd.Parameters.Add(Money("@trade_qty", qty));
		cmd.Parameters.Add(Money("@trade_price", price));
		await cmd.ExecuteNonQueryAsync();
	}

	// ---- scope / row helpers --------------------------------------------------------------------------

	private static SqlParameter IntParam(string name, int value) => new(name, SqlDbType.Int) { Value = value };

	private static SqlParameter Money(string name, decimal value)
		=> new(name, SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = value };

	private static SqlParameter NullableMoney(string name, decimal? value)
		=> new(name, SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = (object)value ?? DBNull.Value };

	private static async Task<(int PortfolioId, int SecurityId)> InsertScopeAsync(SqlConnection c, SqlTransaction t)
	{
		var tag = Guid.NewGuid().ToString("N");
		int portfolioId;
		int securityId;

		await using (var cmd = new SqlCommand("INSERT INTO dbo.Portfolios (name) OUTPUT INSERTED.portfolio_id VALUES (@n)", c, t))
		{
			cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.NVarChar, 100) { Value = "PAR_" + tag });
			portfolioId = (int)await cmd.ExecuteScalarAsync();
		}

		await using (var cmd = new SqlCommand("INSERT INTO dbo.Securities (security_code, board_code) OUTPUT INSERTED.security_id VALUES (@c, @b)", c, t))
		{
			cmd.Parameters.Add(new SqlParameter("@c", SqlDbType.NVarChar, 50) { Value = "P" + tag.Substring(0, 10) });
			cmd.Parameters.Add(new SqlParameter("@b", SqlDbType.NVarChar, 20) { Value = "PB" + tag.Substring(0, 6) });
			securityId = (int)await cmd.ExecuteScalarAsync();
		}

		return (portfolioId, securityId);
	}

	// Inserts a portfolio+security-scoped RiskLimits row carrying the standard seeded ceilings, so both the
	// legacy proc and the C# gate resolve the identical row. All ceilings are positive (the zero-ceiling
	// divergence, MA-16, is covered separately and intentionally not exercised here).
	private static async Task InsertSeededLimitsAsync(SqlConnection c, SqlTransaction t, int pid, int sid)
	{
		await using var cmd = new SqlCommand(
			"INSERT INTO dbo.RiskLimits (portfolio_id, security_id, max_order_price, max_order_qty, max_order_value, " +
			"max_position_size, max_daily_volume, max_order_freq_count, max_order_freq_window_sec, max_commission_total, " +
			"commission_rate, is_active) VALUES (@p, @s, @mop, @moq, @mov, @mps, @mdv, @mfc, @mfw, @mct, @rate, 1)", c, t);

		cmd.Parameters.Add(IntParam("@p", pid));
		cmd.Parameters.Add(IntParam("@s", sid));
		cmd.Parameters.Add(Money("@mop", 500m));
		cmd.Parameters.Add(Money("@moq", 10000m));
		cmd.Parameters.Add(Money("@mov", 1000000m));
		cmd.Parameters.Add(Money("@mps", 100000m));
		cmd.Parameters.Add(Money("@mdv", 250000m));
		cmd.Parameters.Add(new SqlParameter("@mfc", SqlDbType.Int) { Value = 5 });
		cmd.Parameters.Add(new SqlParameter("@mfw", SqlDbType.Int) { Value = 60 });
		cmd.Parameters.Add(Money("@mct", 5000m));
		cmd.Parameters.Add(new SqlParameter("@rate", SqlDbType.Decimal) { Precision = 9, Scale = 6, Value = 0.0005m });

		await cmd.ExecuteNonQueryAsync();
	}

	private static async Task<long> InsertOrderAsync(SqlConnection c, SqlTransaction t, int pid, int sid, char side, decimal qty, decimal price, string status)
	{
		await using var cmd = new SqlCommand(
			"INSERT INTO dbo.Orders (portfolio_id, security_id, side, qty, price, order_type, status) " +
			"OUTPUT INSERTED.order_id VALUES (@p, @s, @side, @qty, @price, 'LIMIT', @status)", c, t);

		cmd.Parameters.Add(IntParam("@p", pid));
		cmd.Parameters.Add(IntParam("@s", sid));
		cmd.Parameters.Add(new SqlParameter("@side", SqlDbType.Char, 1) { Value = side.ToString() });
		cmd.Parameters.Add(Money("@qty", qty));
		cmd.Parameters.Add(Money("@price", price));
		cmd.Parameters.Add(new SqlParameter("@status", SqlDbType.VarChar, 12) { Value = status });

		return (long)await cmd.ExecuteScalarAsync();
	}

	private static async Task InsertTradeAsync(SqlConnection c, SqlTransaction t, long orderId, decimal qty, decimal price)
	{
		await using var cmd = new SqlCommand("INSERT INTO dbo.Trades (order_id, qty, price) VALUES (@o, @qty, @price)", c, t);
		cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.BigInt) { Value = orderId });
		cmd.Parameters.Add(Money("@qty", qty));
		cmd.Parameters.Add(Money("@price", price));
		await cmd.ExecuteNonQueryAsync();
	}

	private static async Task InsertPositionAsync(SqlConnection c, SqlTransaction t, int pid, int sid, decimal qty)
	{
		await using var cmd = new SqlCommand("INSERT INTO dbo.Positions (portfolio_id, security_id, qty) VALUES (@p, @s, @qty)", c, t);
		cmd.Parameters.Add(IntParam("@p", pid));
		cmd.Parameters.Add(IntParam("@s", sid));
		cmd.Parameters.Add(Money("@qty", qty));
		await cmd.ExecuteNonQueryAsync();
	}

	private static async Task<(decimal Qty, decimal Avg, decimal Realized)?> ReadPositionAsync(SqlConnection c, SqlTransaction t, int pid, int sid)
	{
		await using var cmd = new SqlCommand(
			"SELECT qty, avg_price, realized_pnl FROM dbo.Positions WHERE portfolio_id = @p AND security_id = @s", c, t);
		cmd.Parameters.Add(IntParam("@p", pid));
		cmd.Parameters.Add(IntParam("@s", sid));

		await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow);

		if (!await reader.ReadAsync())
			return null;

		return (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2));
	}
}
