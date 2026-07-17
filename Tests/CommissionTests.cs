namespace StockSharp.Tests;

using System.Data;

using Microsoft.Data.SqlClient;

using StockSharp.Algo.Commissions;
using StockSharp.Algo.Risk;
using StockSharp.Algo.Storages.Sql;

[TestClass]
[DoNotParallelize] // The Live_* commission tests open real StockSharpLegacy transactions that hold locks
                   // on the shared Orders/Trades/RiskLimits tables; running them concurrently would
                   // deadlock. This follows the repo convention (see StorageNotParallelizeTests).
public class CommissionTests
{
	private static DateTime Inc(ref DateTime time)
	{
		time = time.AddHours(1);
		return time;
	}

	private static ExecutionMessage CreateOrderMessage(decimal price, decimal volume, DateTime time)
	{
		return new()
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			OrderPrice = price,
			OrderVolume = volume,
			ServerTime = time
		};
	}

	private static ExecutionMessage CreateTradeMessage(decimal price, decimal volume, DateTime time)
	{
		return new()
		{
			DataTypeEx = DataType.Transactions,
			TradePrice = price,
			TradeVolume = volume,
			ServerTime = time
		};
	}

	[TestMethod]
	public void PerOrderRule()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionOrderRule
		{
			Value = 10m
		};

		// Act & Assert
		var orderMsg = CreateOrderMessage(100m, 10m, Inc(ref now));
		var result = rule.Process(orderMsg);
		result.AssertEqual(10m);

		// Test null when not order info
		var tradeMsg = CreateTradeMessage(100m, 10m, Inc(ref now));
		result = rule.Process(tradeMsg);
		result.AssertNull();

		// Test percent-based commission
		rule.Value = new Unit { Value = 5m, Type = UnitTypes.Percent };
		result = rule.Process(orderMsg);
		result.AssertEqual(50m); // 5% of 100 * 10 = 50
	}

	[TestMethod]
	public void PerTradeRule()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionTradeRule
		{
			Value = 15m
		};

		// Act & Assert
		var tradeMsg = CreateTradeMessage(200m, 5m, Inc(ref now));
		var result = rule.Process(tradeMsg);
		result.AssertEqual(15m);

		// Test null when not trade info
		var orderMsg = CreateOrderMessage(200m, 5m, Inc(ref now));
		result = rule.Process(orderMsg);
		result.AssertNull();

		// Test percent-based commission
		rule.Value = new Unit { Value = 2.5m, Type = UnitTypes.Percent };
		result = rule.Process(tradeMsg);
		result.AssertEqual(25m); // 2.5% of 200 * 5 = 25
	}

	[TestMethod]
	public void PerOrderVolumeRule()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionOrderVolumeRule
		{
			Value = 0.5m
		};

		// Act & Assert
		var orderMsg = CreateOrderMessage(150m, 20m, Inc(ref now));
		var result = rule.Process(orderMsg);
		result.AssertEqual(10m); // 0.5 * 20 = 10

		// Test null when not order info
		var tradeMsg = CreateTradeMessage(150m, 20m, Inc(ref now));
		result = rule.Process(tradeMsg);
		result.AssertNull();
	}

	[TestMethod]
	public void PerTradeVolumeRule()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionTradeVolumeRule
		{
			Value = 0.25m
		};

		// Act & Assert
		var tradeMsg = CreateTradeMessage(300m, 40m, Inc(ref now));
		var result = rule.Process(tradeMsg);
		result.AssertEqual(10m); // 0.25 * 40 = 10

		// Test null when not trade info
		var orderMsg = CreateOrderMessage(300m, 40m, Inc(ref now));
		result = rule.Process(orderMsg);
		result.AssertNull();
	}

	[TestMethod]
	public void PerOrderCountRule()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionOrderCountRule
		{
			Value = 25m,
			Count = 3
		};

		// Act & Assert
		var orderMsg = CreateOrderMessage(100m, 1m, Inc(ref now));

		// First 2 orders should return null
		var result = rule.Process(orderMsg);
		result.AssertNull();

		result = rule.Process(orderMsg);
		result.AssertNull();

		// 3rd order should apply commission
		result = rule.Process(orderMsg);
		result.AssertEqual(25m);

		// 4th order should be null again
		result = rule.Process(orderMsg);
		result.AssertNull();

		// Test reset functionality
		rule.Reset();

		result = rule.Process(orderMsg);
		result.AssertNull();

		// Test null when not order info
		var tradeMsg = CreateTradeMessage(100m, 1m, Inc(ref now));
		result = rule.Process(tradeMsg);
		result.AssertNull();
	}

	[TestMethod]
	public void PerTradeCountRule()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionTradeCountRule
		{
			Value = 30m,
			Count = 2
		};

		// Act & Assert
		var tradeMsg = CreateTradeMessage(200m, 1m, Inc(ref now));

		// First order should return null
		var result = rule.Process(tradeMsg);
		result.AssertNull();

		// 2nd order should apply commission
		result = rule.Process(tradeMsg);
		result.AssertEqual(30m);

		// 3rd order should be null again
		result = rule.Process(tradeMsg);
		result.AssertNull();

		// Test reset functionality
		rule.Reset();

		result = rule.Process(tradeMsg);
		result.AssertNull();

		// Test null when not trade info
		var orderMsg = CreateOrderMessage(200m, 1m, Inc(ref now));
		result = rule.Process(orderMsg);
		result.AssertNull();
	}

	[TestMethod]
	public void PerTradePriceRule()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionTradePriceRule
		{
			Value = 0.01m
		};

		// Act & Assert
		var tradeMsg = CreateTradeMessage(100m, 5m, Inc(ref now));
		var result = rule.Process(tradeMsg);
		result.AssertEqual(5m); // 100 * 5 * 0.01 = 5

		// Test null when not trade info
		var orderMsg = CreateOrderMessage(100m, 5m, Inc(ref now));
		result = rule.Process(orderMsg);
		result.AssertNull();
	}

	// =====================================================================================
	// Different-by-design commission pair (AAP 0.6.2, LEGACY_LAYER.md:L58-L59).
	//
	// These two tests replace an earlier pair that asserted an inline-computed "SQL estimate" against
	// itself (M14). They now source the pre-fill ESTIMATE from the REAL per-order gate
	// PreTradeRiskService.ValidateAsync against the StockSharpLegacy SQL Server, and compare it
	// INDEPENDENTLY against the REAL post-fill actual-commission rule RiskOrderCommissionRule. Both
	// enforcement patterns consume the single canonical threshold RiskLimitSet.MaxCommissionTotal, yet
	// because the gate PROJECTS an estimate from historical traded notional x rate before any fill while
	// the risk rule ACCUMULATES the broker-reported ExecutionMessage.Commission after fills, the two are
	// intentionally not merged under DRY. Gated on DB reachability via Assert.Inconclusive.
	// =====================================================================================

	private static async Task<SqlConnection> TryOpenLegacyAsync(CancellationToken cancellationToken = default)
	{
		SqlConnection connection = null;

		try
		{
			connection = new SqlConnection(SqlLegacyConnection.Resolve());
			await connection.OpenAsync(cancellationToken);
			return connection;
		}
		catch (Exception)
		{
			if (connection is not null)
				await connection.DisposeAsync();

			return null;
		}
	}

	private static SqlParameter IntParam(string name, int value) => new(name, SqlDbType.Int) { Value = value };

	private static SqlParameter NInt(string name, int? value) => new(name, SqlDbType.Int) { Value = (object)value ?? DBNull.Value };

	private static SqlParameter MoneyParam(string name, decimal? value)
		=> new(name, SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = (object)value ?? DBNull.Value };

	private static async Task<(int PortfolioId, int SecurityId)> InsertScopeAsync(SqlConnection c, SqlTransaction t, CancellationToken ct)
	{
		var tag = Guid.NewGuid().ToString("N");
		int portfolioId;
		int securityId;

		await using (var cmd = new SqlCommand("INSERT INTO dbo.Portfolios (name) OUTPUT INSERTED.portfolio_id VALUES (@n)", c, t))
		{
			cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.NVarChar, 100) { Value = "TEST_" + tag });
			portfolioId = (int)await cmd.ExecuteScalarAsync(ct);
		}

		await using (var cmd = new SqlCommand("INSERT INTO dbo.Securities (security_code, board_code) OUTPUT INSERTED.security_id VALUES (@c, @b)", c, t))
		{
			cmd.Parameters.Add(new SqlParameter("@c", SqlDbType.NVarChar, 50) { Value = "T" + tag.Substring(0, 10) });
			cmd.Parameters.Add(new SqlParameter("@b", SqlDbType.NVarChar, 20) { Value = "TB" + tag.Substring(0, 6) });
			securityId = (int)await cmd.ExecuteScalarAsync(ct);
		}

		return (portfolioId, securityId);
	}

	// Inserts a RiskLimits row that enforces ONLY the commission ceiling (all other limits NULL), so the
	// gate evaluates the commission check in isolation.
	private static async Task InsertCommissionLimitAsync(SqlConnection c, SqlTransaction t, int portfolioId, int securityId, decimal maxCommission, decimal rate, CancellationToken ct)
	{
		// Past-date effective_date by one second (review finding CR-32 test-helper pattern): the column is
		// DATETIME2(3), so assigning a raw SYSUTCDATETIME() can round the stored value UP to the next
		// millisecond, which the gate's "effective_date <= SYSUTCDATETIME()" cutoff - evaluated an instant
		// later in the same transaction - can then exclude, making the limit row invisible and the gate
		// spuriously accept. Seeding one second in the past keeps the just-inserted row unambiguously
		// eligible and removes that rounding race. (Production seeds effective_date via 004_SeedData.sql at
		// DB setup, so this affects tests only.)
		await using var cmd = new SqlCommand(
			"INSERT INTO dbo.RiskLimits (portfolio_id, security_id, max_commission_total, commission_rate, is_active, effective_date) " +
			"VALUES (@p, @s, @comm, @rate, 1, DATEADD(SECOND, -1, SYSUTCDATETIME()))", c, t);
		cmd.Parameters.Add(NInt("@p", portfolioId));
		cmd.Parameters.Add(NInt("@s", securityId));
		cmd.Parameters.Add(MoneyParam("@comm", maxCommission));
		cmd.Parameters.Add(new SqlParameter("@rate", SqlDbType.Decimal) { Precision = 9, Scale = 6, Value = rate });
		await cmd.ExecuteNonQueryAsync(ct);
	}

	private static async Task<long> InsertOrderAsync(SqlConnection c, SqlTransaction t, int portfolioId, int securityId, char side, decimal qty, decimal price, CancellationToken ct)
	{
		await using var cmd = new SqlCommand(
			"INSERT INTO dbo.Orders (portfolio_id, security_id, side, qty, price, order_type, status) " +
			"OUTPUT INSERTED.order_id VALUES (@p, @s, @side, @qty, @price, 'LIMIT', 'ACCEPTED')", c, t);
		cmd.Parameters.Add(IntParam("@p", portfolioId));
		cmd.Parameters.Add(IntParam("@s", securityId));
		cmd.Parameters.Add(new SqlParameter("@side", SqlDbType.Char, 1) { Value = side.ToString() });
		cmd.Parameters.Add(MoneyParam("@qty", qty));
		cmd.Parameters.Add(MoneyParam("@price", price));
		return (long)await cmd.ExecuteScalarAsync(ct);
	}

	private static async Task InsertTradeAsync(SqlConnection c, SqlTransaction t, long orderId, decimal qty, decimal price, CancellationToken ct)
	{
		await using var cmd = new SqlCommand("INSERT INTO dbo.Trades (order_id, qty, price) VALUES (@o, @qty, @price)", c, t);
		cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.BigInt) { Value = orderId });
		cmd.Parameters.Add(MoneyParam("@qty", qty));
		cmd.Parameters.Add(MoneyParam("@price", price));
		await cmd.ExecuteNonQueryAsync(ct);
	}

	// Seeds a scope whose HISTORICAL TRADED NOTIONAL is 20,000 while the net POSITION is flat (a buy and
	// an offsetting sell, both 100 @ 100). This deliberately makes traded-notional (20,000) and
	// position-notional (qty 0 x avg 0 = 0) disagree, so a commission estimate can prove which basis the
	// gate uses. Returns the fresh (portfolioId, securityId).
	private static async Task<(int PortfolioId, int SecurityId)> SeedFlatButHighTradedNotionalAsync(SqlConnection c, SqlTransaction t, CancellationToken ct)
	{
		var (portfolioId, securityId) = await InsertScopeAsync(c, t, ct);

		var buy = await InsertOrderAsync(c, t, portfolioId, securityId, 'B', 100m, 100m, ct);
		await InsertTradeAsync(c, t, buy, 100m, 100m, ct);          // traded notional 10,000

		var sell = await InsertOrderAsync(c, t, portfolioId, securityId, 'S', 100m, 100m, ct);
		await InsertTradeAsync(c, t, sell, 100m, 100m, ct);        // traded notional +10,000 = 20,000

		return (portfolioId, securityId);
	}

	private async Task RunWithFreshScopeAsync(Func<PreTradeRiskService, SqlConnection, SqlTransaction, int, int, Task> body)
	{
		await using var connection = await TryOpenLegacyAsync();

		if (connection is null)
		{
			Assert.Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live commission integration test.");
			return;
		}

		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

		try
		{
			var service = new PreTradeRiskService(SqlLegacyConnection.Resolve());
			var (portfolioId, securityId) = await SeedFlatButHighTradedNotionalAsync(connection, transaction, default);
			await body(service, connection, transaction, portfolioId, securityId);
		}
		finally
		{
			await transaction.RollbackAsync();
		}
	}

	[TestMethod]
	public Task Live_CommissionEstimateUsesTradedNotionalFromRealGate()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// Scope has 20,000 historical TRADED notional but a FLAT position. rate = 0.0005.
			// The real gate estimate for a new Buy 10 @ 100 (order notional 1,000) is:
			//   20,000 * 0.0005 + 1,000 * 0.0005 = 10 + 0.5 = 10.5   (TRADED-notional basis)
			// If the gate had (wrongly) used the position quantity x average price (= 0), the estimate
			// would be only 0.5. A limit of 5.0 sits between the two, so a REJECT here - with the 10.5000
			// figure in the reason - proves the estimate is sourced from historical TRADED notional
			// (correcting M14's misdescription) and comes from the real gate, not inline arithmetic.
			await InsertCommissionLimitAsync(c, t, pid, sid, maxCommission: 5.0m, rate: 0.0005m, default);

			var rejected = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 10m, 100m, OrderTypes.Limit);
			rejected.IsValid.AssertFalse();
			rejected.RejectReason.AssertEqual("Estimated cumulative commission 10.5000 meets/exceeds limit 5.0000");

			// Bracket the exact estimate: raising the ceiling just above 10.5 must ACCEPT (10.5 < 10.6).
			await using (var raise = new SqlCommand("UPDATE dbo.RiskLimits SET max_commission_total = 10.6 WHERE portfolio_id = @p", c, t))
			{
				raise.Parameters.Add(IntParam("@p", pid));
				await raise.ExecuteNonQueryAsync();
			}

			var accepted = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 10m, 100m, OrderTypes.Limit);
			accepted.IsValid.AssertTrue();
			accepted.RejectReason.AssertNull();
		});

	[TestMethod]
	public Task Live_CommissionEstimateVsActualCommissionRuleDifferByDesign()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// SHARED canonical threshold for BOTH enforcement patterns.
			const decimal canonicalLimit = 10.0m;

			// (1) PRE-FILL ESTIMATE from the REAL gate: 20,000*0.0005 + 10*100*0.0005 = 10.5 >= 10.0,
			// so the gate REJECTS the order before any fill exists.
			await InsertCommissionLimitAsync(c, t, pid, sid, maxCommission: canonicalLimit, rate: 0.0005m, default);

			var gate = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 10m, 100m, OrderTypes.Limit);
			gate.IsValid.AssertFalse();
			gate.RejectReason.AssertEqual("Estimated cumulative commission 10.5000 meets/exceeds limit 10.0000");

			// (2) POST-FILL ACTUAL from the REAL RiskOrderCommissionRule sharing the SAME threshold.
			// The broker reports the real commission on the executed order as 4.0 (NOT the 10.5 estimate).
			var actualRule = new RiskOrderCommissionRule
			{
				Commission = canonicalLimit,
				Action = RiskActions.StopTrading,
			};

			var fill1 = new ExecutionMessage
			{
				DataTypeEx = DataType.Transactions,
				SecurityId = Helper.CreateSecurityId(),
				ServerTime = DateTime.UtcNow,
				HasOrderInfo = true,
				Commission = 4.0m,
			};

			// DIFFERENT-BY-DESIGN: for the identical threshold (10.0) the gate rejects (estimate 10.5)
			// yet the actual rule does NOT trip (realized 4.0 < 10.0). The two disagree by construction
			// because one projects an estimate and the other measures the realized total.
			actualRule.ProcessMessage(fill1).AssertFalse();

			// SINGLE SOURCE OF TRUTH: the actual rule still enforces the SAME canonical ceiling - once the
			// realized commission reaches it (4.0 + 6.5 = 10.5 >= 10.0) the rule trips.
			var fill2 = new ExecutionMessage
			{
				DataTypeEx = DataType.Transactions,
				SecurityId = Helper.CreateSecurityId(),
				ServerTime = DateTime.UtcNow,
				HasOrderInfo = true,
				Commission = 6.5m,
			};

			actualRule.ProcessMessage(fill2).AssertTrue();
		});

	[TestMethod]
	public void SecurityIdRule()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var securityId = new SecurityId { SecurityCode = "AAPL", BoardCode = BoardCodes.Nasdaq };
		var security = new Security { Id = securityId.ToStringId() };

		var rule = new CommissionSecurityIdRule
		{
			Value = 10m,
			Security = security
		};

		// Act & Assert
		var tradeMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = securityId,
			TradePrice = 150m,
			TradeVolume = 2,
			ServerTime = Inc(ref now)
		};

		var result = rule.Process(tradeMsg);
		result.AssertEqual(10m);

		// Test null for different security ID
		var differentSecurityMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = new() { SecurityCode = "MSFT", BoardCode = BoardCodes.Nasdaq },
			TradePrice = 150m,
			ServerTime = Inc(ref now)
		};

		result = rule.Process(differentSecurityMsg);
		result.AssertNull();

		// Test percent-based commission
		rule.Value = new Unit { Value = 1m, Type = UnitTypes.Percent };
		result = rule.Process(tradeMsg);
		result.AssertEqual(3m); // 1% of (150 * 2) = 3

		// Test null when not trade info
		var orderMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			SecurityId = securityId,
			OrderPrice = 150m,
			ServerTime = Inc(ref now)
		};

		result = rule.Process(orderMsg);
		result.AssertNull();
	}

	[TestMethod]
	public void BoardCodeRule()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var board = new ExchangeBoard { Code = BoardCodes.Nasdaq };

		var rule = new CommissionBoardCodeRule
		{
			Value = 15m,
			Board = board
		};

		// Act & Assert
		var tradeMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = new() { BoardCode = BoardCodes.Nasdaq },
			TradePrice = 200m,
			TradeVolume = 2,
			ServerTime = Inc(ref now),
		};

		var result = rule.Process(tradeMsg);
		result.AssertEqual(15m);

		// Test null for different board
		var differentBoardMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = new() { BoardCode = "NYSE" },
			TradePrice = 200m,
			ServerTime = Inc(ref now)
		};

		result = rule.Process(differentBoardMsg);
		result.AssertNull();

		// Test percent-based commission
		rule.Value = new() { Value = 2m, Type = UnitTypes.Percent };
		result = rule.Process(tradeMsg);
		result.AssertEqual(8m); // 2% of (200 * 2) = 8

		// Test null when not trade info
		var orderMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			SecurityId = new() { BoardCode = BoardCodes.Nasdaq },
			OrderPrice = 200m,
			ServerTime = Inc(ref now)
		};

		result = rule.Process(orderMsg);
		result.AssertNull();
	}

	[TestMethod]
	public void TurnOverRule()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionTurnOverRule
		{
			Value = 50m,
			TurnOver = 1000m
		};

		// Act & Assert
		var tradeMsg1 = CreateTradeMessage(100m, 5m, Inc(ref now)); // 500
		var result = rule.Process(tradeMsg1);
		result.AssertNull(); // Turnover not reached yet

		var tradeMsg2 = CreateTradeMessage(200m, 3m, Inc(ref now)); // 600 (total 1100)
		result = rule.Process(tradeMsg2);
		result.AssertEqual(50m); // Turnover reached

		// Test reset functionality
		rule.Reset();

		var tradeMsg3 = CreateTradeMessage(100m, 5m, Inc(ref now)); // 500
		result = rule.Process(tradeMsg3);
		result.AssertNull(); // Turnover not reached after reset

		// Test null when not trade info
		var orderMsg = CreateOrderMessage(100m, 5m, Inc(ref now));
		result = rule.Process(orderMsg);
		result.AssertNull();
	}

	[TestMethod]
	public void SecurityTypeRule_UnknownSecurity_ReturnsNull()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionSecurityTypeRule
		{
			Value = 20m,
			SecurityType = SecurityTypes.Stock
		};

		var tradeMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			TradePrice = 150m,
			TradeVolume = 2,
			ServerTime = Inc(ref now)
		};

		var result = rule.Process(tradeMsg);
		result.AssertNull();
	}

	[TestMethod]
	public void SecurityTypeRuleSecProvider()
	{
		var now = DateTime.UtcNow;

		var secId = new SecurityId { SecurityCode = "AAPL", BoardCode = BoardCodes.Nasdaq };
		var appl = new Security { Id = secId.ToStringId(), Type = SecurityTypes.Stock };

		var provider = (CollectionSecurityProvider)ServicesRegistry.SecurityProvider;
		provider.Add(appl);

		// Arrange
		var rule = new CommissionSecurityTypeRule
		{
			Value = 20m,
			SecurityType = appl.Type.Value
		};

		var tradeMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = secId,
			TradePrice = 150m,
			TradeVolume = 2,
			ServerTime = Inc(ref now)
		};

		var result = rule.Process(tradeMsg);
		result.AssertEqual(20);
	}

	public static CommissionManager CreateManager()
	{
		var manager = new CommissionManager();

		var orderRule = new CommissionOrderRule
		{
			Value = new Unit { Value = 10m, Type = UnitTypes.Absolute }
		};
		var tradeRule = new CommissionTradeRule
		{
			Value = new Unit { Value = 15m, Type = UnitTypes.Absolute }
		};

		manager.Rules.Add(orderRule);
		manager.Rules.Add(tradeRule);

		return manager;
	}

	[TestMethod]
	public void ManagerOrderMessage()
	{
		var now = DateTime.UtcNow;

		var manager = CreateManager();

		// Arrange
		var orderMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			OrderPrice = 100m,
			OrderVolume = 5m,
			ServerTime = Inc(ref now)
		};

		// Act
		var commission = manager.Process(orderMsg);

		// Assert
		commission.AssertEqual(10m);
		manager.Commission.AssertEqual(10m);
	}

	[TestMethod]
	public void ManagerTradeMessage()
	{
		var now = DateTime.UtcNow;

		var manager = CreateManager();

		// Arrange
		var tradeMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			TradePrice = 200m,
			TradeVolume = 3m,
			ServerTime = Inc(ref now)
		};

		// Act
		var commission = manager.Process(tradeMsg);

		// Assert
		commission.AssertEqual(15m);
		manager.Commission.AssertEqual(15m);
	}

	[TestMethod]
	public void ManagerOrderAndTradeMessages()
	{
		var now = DateTime.UtcNow;

		var manager = CreateManager();

		// Arrange
		var orderMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			OrderPrice = 100m,
			OrderVolume = 5m,
			ServerTime = Inc(ref now)
		};

		var tradeMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			TradePrice = 200m,
			TradeVolume = 3m,
			ServerTime = Inc(ref now)
		};

		// Act
		manager.Process(orderMsg);
		manager.Process(tradeMsg);

		// Assert
		manager.Commission.AssertEqual(25m); // 10m + 15m
	}

	[TestMethod]
	public void ManagerResetMessage()
	{
		var now = DateTime.UtcNow;

		var manager = CreateManager();

		// Arrange
		var orderMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			OrderPrice = 100m,
			OrderVolume = 5m,
			ServerTime = Inc(ref now)
		};

		manager.Process(orderMsg);
		manager.Commission.AssertEqual(10m);

		var resetMsg = new ResetMessage();

		// Act
		manager.Process(resetMsg);

		// Assert
		manager.Commission.AssertEqual(0m);
	}

	[TestMethod]
	public void ManagerEmptyRules()
	{
		var now = DateTime.UtcNow;

		var manager = CreateManager();

		// Arrange
		manager.Rules.Clear();
		var orderMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			OrderPrice = 100m,
			OrderVolume = 5m,
			ServerTime = Inc(ref now)
		};

		// Act
		var commission = manager.Process(orderMsg);

		// Assert
		commission.AssertNull();
		manager.Commission.AssertEqual(0m);
	}

	[TestMethod]
	public void ManagerNonExecutionMessage()
	{
		var manager = CreateManager();

		// Arrange
		var quoteMsg = new QuoteChangeMessage
		{
		};

		// Act
		var commission = manager.Process(quoteMsg);

		// Assert
		commission.AssertNull();
		manager.Commission.AssertEqual(0m);
	}

	[TestMethod]
	public void ManagerSaveLoad()
	{
		var manager = CreateManager();

		// Arrange
		var storage = new SettingsStorage();

		// Act
		manager.Save(storage);

		var newManager = new CommissionManager();
		newManager.Load(storage);

		// Assert
		newManager.Rules.Count.AssertEqual(2);

		var savedOrderRule = newManager.Rules.OfType<CommissionOrderRule>().FirstOrDefault();
		savedOrderRule.AssertNotNull();
		savedOrderRule.Value.Value.AssertEqual(10m);
		savedOrderRule.Value.Type.AssertEqual(UnitTypes.Absolute);

		var savedTradeRule = newManager.Rules.OfType<CommissionTradeRule>().FirstOrDefault();
		savedTradeRule.AssertNotNull();
		savedTradeRule.Value.Value.AssertEqual(15m);
		savedTradeRule.Value.Type.AssertEqual(UnitTypes.Absolute);
	}

	[TestMethod]
	public void ManagerReset()
	{
		var now = DateTime.UtcNow;

		var manager = CreateManager();

		// Arrange
		var countRule = new CommissionOrderCountRule
		{
			Value = new Unit { Value = 5m, Type = UnitTypes.Absolute },
			Count = 2
		};
		manager.Rules.Add(countRule);

		var orderMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			OrderPrice = 100m,
			OrderVolume = 5m,
			ServerTime = Inc(ref now)
		};

		// Process first order to increase the counter in countRule
		manager.Process(orderMsg);

		// Act
		manager.Reset();

		// Assert
		manager.Commission.AssertEqual(0m);

		// Process one more order, it should not trigger countRule commission yet
		// since the counter should have been reset
		manager.Process(orderMsg);
		var commission = manager.Process(orderMsg);

		// This should be just the orderRule commission (10m) + countRule (5m) that triggered on second order after reset
		commission.AssertEqual(15m);
	}

	[TestMethod]
	public void ProviderDefaultRules()
	{
		ICommissionRuleProvider provider = new InMemoryCommissionRuleProvider();

		// Assert
		var rules = provider.All.ToList();
		rules.Count.AssertEqual(11);

		// Verify that common rule types are included
		rules.Count(t => t == typeof(CommissionOrderRule)).AssertEqual(1);
		rules.Count(t => t == typeof(CommissionTradeRule)).AssertEqual(1);
		rules.Count(t => t == typeof(CommissionOrderVolumeRule)).AssertEqual(1);
		rules.Count(t => t == typeof(CommissionTradeVolumeRule)).AssertEqual(1);
		rules.Count(t => t == typeof(CommissionOrderCountRule)).AssertEqual(1);
		rules.Count(t => t == typeof(CommissionTradeCountRule)).AssertEqual(1);
		rules.Count(t => t == typeof(CommissionTradePriceRule)).AssertEqual(1);
		rules.Count(t => t == typeof(CommissionSecurityIdRule)).AssertEqual(1);
		rules.Count(t => t == typeof(CommissionSecurityTypeRule)).AssertEqual(1);
		rules.Count(t => t == typeof(CommissionBoardCodeRule)).AssertEqual(1);
		rules.Count(t => t == typeof(CommissionTurnOverRule)).AssertEqual(1);
	}

	[TestMethod]
	public void ProviderAddRule()
	{
		ICommissionRuleProvider provider = new InMemoryCommissionRuleProvider();

		// Arrange
		var customRuleType = typeof(CustomCommissionRule);

		// Act
		provider.Add(customRuleType);

		// Assert
		var rules = provider.All.ToList();
		rules.Count(t => t == customRuleType).AssertEqual(1);
	}

	[TestMethod]
	public void ProviderRemoveExistingRule()
	{
		ICommissionRuleProvider provider = new InMemoryCommissionRuleProvider();

		// Arrange
		var ruleType = typeof(CommissionOrderRule);
		provider.All.Count(t => t == ruleType).AssertEqual(1);

		// Act
		provider.Remove(ruleType);

		// Assert
		provider.All.Count(t => t == ruleType).AssertEqual(0);
	}

	[TestMethod]
	public void ProviderRemoveNonExistingRule()
	{
		ICommissionRuleProvider provider = new InMemoryCommissionRuleProvider();

		// Arrange
		var nonExistingRuleType = typeof(CustomCommissionRule);
		provider.All.Count(t => t == nonExistingRuleType).AssertEqual(0);

		// Act
		provider.Remove(nonExistingRuleType);

		// Assert - should not throw exception
		provider.All.Count(t => t == nonExistingRuleType).AssertEqual(0);
	}

	[TestMethod]
	public void RuleSerialization()
	{
		var secProvider = (CollectionSecurityProvider)ServicesRegistry.SecurityProvider;
		var boards = ServicesRegistry.ExchangeInfoProvider.Boards.ToArray();
		ICommissionRuleProvider provider = new InMemoryCommissionRuleProvider();

		// Arrange
		var rules = provider.All.ToArray();

		foreach (var type in rules)
		{
			var rule = type.CreateInstance<ICommissionRule>();

			var props = type.GetModifiableProps();

			foreach (var prop in props)
			{
				var propType = prop.PropertyType;
				propType = propType.GetUnderlyingType() ?? prop.PropertyType;

				object value;

				if (propType == typeof(Unit))
					value = new Unit { Value = RandomGen.GetInt(), Type = UnitTypes.Percent };
				else if (propType.IsNumeric())
					value = RandomGen.GetInt().To(propType);
				else if (propType.IsEnum)
					value = RandomGen.GetEnum(propType);
				else if (propType == typeof(SecurityId))
					value = Helper.CreateSecurityId();
				else if (propType == typeof(Security))
				{
					var sec = new Security { Id = Helper.CreateSecurityId().ToStringId() };
					secProvider.Add(sec);
					value = sec;
				}
				else if (propType == typeof(ExchangeBoard))
					value = RandomGen.GetElement(boards);
				else if (propType == typeof(string))
					value = RandomGen.GetString(3, 7);
				else
					throw new InvalidOperationException(propType.FullName);

				prop.SetValue(rule, value);
			}

			// Save
			var storage = rule.Save();

			// Create new instance of the same type
			var restored = type.CreateInstance<ICommissionRule>();
			restored.Load(storage);

			// Compare all public settable properties
			foreach (var prop in props)
			{
				var origValue = prop.GetValue(rule);
				var restoredValue = prop.GetValue(restored);

				origValue.AssertEqual(restoredValue);
			}
		}
	}

	[TestMethod]
	public void PerOrderCountRulePartialFill()
	{
		var now = DateTime.UtcNow;
		var rule = new CommissionOrderCountRule
		{
			Value = 10m,
			Count = 1
		};

		var orderId = 123L;

		// Order registration (order info only)
		rule.Process(new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			OrderId = orderId,
			OrderPrice = 100m,
			OrderVolume = 10m,
			ServerTime = Inc(ref now)
		}).AssertEqual(10m);

		// First partial fill (own trade message with order info present)
		rule.Process(new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			OrderId = orderId,
			TradePrice = 100m,
			TradeVolume = 3m,
			ServerTime = Inc(ref now)
		}).AssertNull();

		// Second partial fill for the same order
		rule.Process(new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			OrderId = orderId,
			TradePrice = 101m,
			TradeVolume = 7m,
			ServerTime = Inc(ref now)
		}).AssertNull();
	}

	[TestMethod]
	public void PerOrderTradeTurnover()
	{
		var now = DateTime.UtcNow;
		var rule = new CommissionOrderRule
		{
			Value = new Unit { Value = 1m, Type = UnitTypes.Percent }
		};

		var msg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			OrderType = OrderTypes.Market,
			OrderPrice = 0m,
			OrderVolume = 4m,
			// actual execution info
			TradePrice = 120m,
			TradeVolume = 4m,
			ServerTime = Inc(ref now)
		};

		rule.Process(msg).AssertEqual(4.8m);
	}

	[TestMethod]
	public void TurnOverRuleRepeatedTrigger()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionTurnOverRule
		{
			Value = 50m,
			TurnOver = 1000m
		};

		// Act & Assert
		// First trade: 500 turnover (below threshold)
		var tradeMsg1 = CreateTradeMessage(100m, 5m, Inc(ref now));
		var result = rule.Process(tradeMsg1);
		result.AssertNull(); // Turnover not reached yet

		// Second trade: +600 = 1100 total (above threshold)
		var tradeMsg2 = CreateTradeMessage(200m, 3m, Inc(ref now));
		result = rule.Process(tradeMsg2);
		result.AssertEqual(50m); // Turnover reached, commission applied

		// After commission application the accumulated turnover is decreased by the threshold (1000),
		// so the remainder is100. Next small trade won't reach the threshold again and should return null.
		var tradeMsg3 = CreateTradeMessage(100m, 1m, Inc(ref now));
		result = rule.Process(tradeMsg3);
		// Expected: null (need1000 more turnover)
		result.AssertNull();
	}

	[TestMethod]
	public void PerOrderVolumeRuleWithPercent()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionOrderVolumeRule
		{
			Value = new Unit { Value = 5m, Type = UnitTypes.Percent }
		};

		// Act & Assert
		var orderMsg = CreateOrderMessage(100m, 20m, Inc(ref now));
		var result = rule.Process(orderMsg);
		result.AssertEqual(100m);

		rule.Value = new Unit { Value = 10m, Type = UnitTypes.Percent };
		result = rule.Process(orderMsg);
		result.AssertEqual(200m);

		// A different price distinguishes turnover percentage from volume multiplied by the raw percent.
		var orderMsg2 = CreateOrderMessage(50m, 10m, Inc(ref now));
		result = rule.Process(orderMsg2);
		result.AssertEqual(50m);
	}

	[TestMethod]
	public void PerTradeVolumeRuleWithPercent()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionTradeVolumeRule
		{
			Value = new Unit { Value = 10m, Type = UnitTypes.Percent }
		};

		// Act & Assert
		var tradeMsg = CreateTradeMessage(50m, 10m, Inc(ref now));
		var result = rule.Process(tradeMsg);

		// Expected (with GetValue): (50 * 10 * 10) / 100 = 50
		result.AssertEqual(50m);
	}

	[TestMethod]
	public void PerTradePriceRuleWithPercent()
	{
		var now = DateTime.UtcNow;

		// Arrange
		var rule = new CommissionTradePriceRule
		{
			Value = new Unit { Value = 10m, Type = UnitTypes.Percent }
		};

		// Act & Assert
		var tradeMsg = CreateTradeMessage(100m, 5m, Inc(ref now));
		var result = rule.Process(tradeMsg);

		// Expected (with GetValue): (100 * 5 * 10) / 100 = 50
		result.AssertEqual(50m);
	}

	[TestMethod]
	public void PerOrderVolumeRuleWithNullValues()
	{
		var now = DateTime.UtcNow;

		// Test with absolute value commission
		var rule = new CommissionOrderVolumeRule
		{
			Value = 0.5m
		};

		// Test with null OrderVolume
		var orderMsgNullVolume = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			HasOrderInfo = true,
			OrderPrice = 100m,
			OrderVolume = null,
			ServerTime = Inc(ref now)
		};
		var result = rule.Process(orderMsgNullVolume);
		result.AssertNull();

		// Test with percent-based commission and null volume
		rule.Value = new Unit { Value = 10m, Type = UnitTypes.Percent };
		result = rule.Process(orderMsgNullVolume);
		result.AssertNull(); // percent commission needs volume for turnover calculation
	}

	// A custom rule class for testing
	private class CustomCommissionRule : CommissionRule
	{
		public override decimal? Process(ExecutionMessage message)
		{
			return 1.0m; // Simple implementation for testing
		}
	}
}
