namespace StockSharp.Tests;

using System.Data;

using Microsoft.Data.SqlClient;

using StockSharp.Algo.Risk;
using StockSharp.Algo.Storages.Sql;

/// <summary>
/// Tests for <see cref="PositionRecalculationService"/>, the average-cost + realized-P&amp;L logic ported
/// from the SQL dbo.usp_RecalculatePositionOnTrade (Database/002_StoredProcedures.sql:L262-L334) as part
/// of the SQL-&gt;C# risk-consolidation refactor (AAP 0.2.1, 0.4.1, 0.6.3, 0.6.4). The suite has two layers:
///
///  (1) Pure decision-core UNIT tests of the database-free <see cref="PositionRecalculationService.Recalculate"/>.
///      Each trace is byte-verified against the SQL math and exercises both branches of the algorithm: the
///      same-sign weighted-average branch (opening / adding) and the opposite-sign realize-P&amp;L branch
///      (partial close, exact close, and flip), plus the input-domain guards (M07/M08). These are DB-free
///      and deterministic. Realized P&amp;L is asserted; unrealized P&amp;L is intentionally NOT maintained by
///      the service (it stays an end-of-day mark-to-market concern, Database/001_Schema.sql:L193-L197).
///
///  (2) Live INTEGRATION + characterization/parity tests (the "Live_*" methods) that exercise the real
///      <see cref="PositionRecalculationService.ApplyTradeAsync(SqlConnection, SqlTransaction, long, System.Threading.CancellationToken)"/>
///      persistence path against the StockSharpLegacy SQL Server: they INSERT trades into dbo.Trades and
///      assert both the returned state and the persisted dbo.Positions row equal the legacy oracle captured
///      (via the original dbo.usp_RecalculatePositionOnTrade) BEFORE the SQL layer was reduced to CRUD.
///      They prove the recompute-from-persisted-trades design is idempotent (the historical double-count
///      hazard, LEGACY_LAYER.md:L74-L89, is structurally eliminated: re-running for the same trades does
///      NOT double the position), that transaction rollback discards the write, and that the gateway's
///      RecordTradeAsync applies exactly once end-to-end even under concurrent fills. Each rolled-back test
///      uses a fresh, collision-free scope and leaves the database pristine; the whole layer is gated on
///      database reachability via Inconclusive (AAP 0.6.7).
/// </summary>
[TestClass]
[DoNotParallelize] // The Live_* tests open real StockSharpLegacy transactions that hold locks on the
                   // shared Positions/Trades/Orders tables; running them concurrently would deadlock.
                   // This follows the repo convention (see StorageNotParallelizeTests).
public class PositionRecalculationTests : BaseTestClass
{
	[TestMethod]
	public void OpensFlatPosition()
	{
		// Same-sign branch opening from flat: buy 100 @ 10 => long 100, avg 10, no realized P&L.
		var result = PositionRecalculationService.Recalculate(
			existingQty: 0m,
			existingAvgPrice: 0m,
			existingRealizedPnl: 0m,
			side: Sides.Buy,
			tradeQty: 100m,
			tradePrice: 10m);

		result.Quantity.AssertEqual(100m);
		result.AveragePrice.AssertEqual(10m);
		result.RealizedPnl.AssertEqual(0m);
	}

	[TestMethod]
	public void AddsSameSideWeightedAverage()
	{
		// Same-sign add: 100 @ 10 then +100 @ 20 => 200 @ weighted (100*10 + 100*20)/200 = 15, P&L unchanged.
		var result = PositionRecalculationService.Recalculate(
			existingQty: 100m,
			existingAvgPrice: 10m,
			existingRealizedPnl: 0m,
			side: Sides.Buy,
			tradeQty: 100m,
			tradePrice: 20m);

		result.Quantity.AssertEqual(200m);
		result.AveragePrice.AssertEqual(15m);
		result.RealizedPnl.AssertEqual(0m);
	}

	[TestMethod]
	public void PartialCloseRealizesPnl()
	{
		// Opposite-sign partial close: sell 40 of a long 100 @ 10 at 15.
		// closingQty = 40, realized = 40*(15-10)*sign(+100) = 200; remaining long keeps its avg (10).
		var result = PositionRecalculationService.Recalculate(
			existingQty: 100m,
			existingAvgPrice: 10m,
			existingRealizedPnl: 0m,
			side: Sides.Sell,
			tradeQty: 40m,
			tradePrice: 15m);

		result.Quantity.AssertEqual(60m);
		result.AveragePrice.AssertEqual(10m);
		result.RealizedPnl.AssertEqual(200m);
	}

	[TestMethod]
	public void ExactCloseZeroesPosition()
	{
		// Opposite-sign exact close: sell 100 of a long 100 @ 10 at 15.
		// realized = 100*(15-10)*sign(+100) = 500; newQty == 0 => avg price resets to 0.
		var result = PositionRecalculationService.Recalculate(
			existingQty: 100m,
			existingAvgPrice: 10m,
			existingRealizedPnl: 0m,
			side: Sides.Sell,
			tradeQty: 100m,
			tradePrice: 15m);

		result.Quantity.AssertEqual(0m);
		result.AveragePrice.AssertEqual(0m);
		result.RealizedPnl.AssertEqual(500m);
	}

	[TestMethod]
	public void OverSellFlipsPosition()
	{
		// Opposite-sign flip: sell 150 of a long 100 @ 10 at 15.
		// closingQty = 100 (realized = 100*(15-10)*sign(+100) = 500); remaining 50 opens a SHORT:
		// newQty = sign(-150)*50 = -50, and the flipped leg takes the trade price (15) as its new avg.
		var result = PositionRecalculationService.Recalculate(
			existingQty: 100m,
			existingAvgPrice: 10m,
			existingRealizedPnl: 0m,
			side: Sides.Sell,
			tradeQty: 150m,
			tradePrice: 15m);

		result.Quantity.AssertEqual(-50m);
		result.AveragePrice.AssertEqual(15m);
		result.RealizedPnl.AssertEqual(500m);
	}

	[TestMethod]
	public void ShortCoverExactClose()
	{
		// Opposite-sign exact cover of a SHORT: buy 100 to cover short 100 @ 20 at 15.
		// realized = 100*(15-20)*sign(-100) = 100*(-5)*(-1) = 500; newQty == 0 => avg resets to 0.
		var result = PositionRecalculationService.Recalculate(
			existingQty: -100m,
			existingAvgPrice: 20m,
			existingRealizedPnl: 0m,
			side: Sides.Buy,
			tradeQty: 100m,
			tradePrice: 15m);

		result.Quantity.AssertEqual(0m);
		result.AveragePrice.AssertEqual(0m);
		result.RealizedPnl.AssertEqual(500m);
	}

	[TestMethod]
	public void AddsShortSideWeightedAverageWithRounding()
	{
		// Same-sign SHORT add that exercises 4-dp away-from-zero rounding:
		// short 100 @ 20 then sell +50 @ 10 => qty -150, avg = (100*20 + 50*10)/150 = 2500/150
		// = 16.66666... => Math.Round(_, 4, AwayFromZero) = 16.6667 (the only non-integer expectation).
		var result = PositionRecalculationService.Recalculate(
			existingQty: -100m,
			existingAvgPrice: 20m,
			existingRealizedPnl: 0m,
			side: Sides.Sell,
			tradeQty: 50m,
			tradePrice: 10m);

		result.Quantity.AssertEqual(-150m);
		result.AveragePrice.AssertEqual(16.6667m);
		result.RealizedPnl.AssertEqual(0m);
	}

	[TestMethod]
	public void RealizedPnlAccumulatesOntoExisting()
	{
		// Proves realized P&L ACCUMULATES onto the incoming existingRealizedPnl rather than overwriting it:
		// prior 200 + this close (40*(15-10)*sign(+100) = 200) = 400.
		var result = PositionRecalculationService.Recalculate(
			existingQty: 100m,
			existingAvgPrice: 10m,
			existingRealizedPnl: 200m,
			side: Sides.Sell,
			tradeQty: 40m,
			tradePrice: 15m);

		result.Quantity.AssertEqual(60m);
		result.AveragePrice.AssertEqual(10m);
		result.RealizedPnl.AssertEqual(400m);
	}

	[TestMethod]
	public void RejectsInvalidSide()
	{
		// M07 input-domain guard: a side that is neither Buy nor Sell must fail loudly, never be
		// silently coerced to Sell (which would corrupt the signed-quantity math).
		ThrowsExactly<ArgumentOutOfRangeException>(
			() => PositionRecalculationService.Recalculate(0m, 0m, 0m, (Sides)999, 100m, 10m));
	}

	[TestMethod]
	public void RejectsNonPositiveQty()
	{
		// Zero or negative trade quantity is invalid (it would divide-by-zero or corrupt the average).
		ThrowsExactly<ArgumentOutOfRangeException>(
			() => PositionRecalculationService.Recalculate(0m, 0m, 0m, Sides.Buy, 0m, 10m));
		ThrowsExactly<ArgumentOutOfRangeException>(
			() => PositionRecalculationService.Recalculate(0m, 0m, 0m, Sides.Buy, -5m, 10m));
	}

	[TestMethod]
	public void RejectsNonPositivePrice()
	{
		// dbo.Trades.CHECK constrains price > 0; the pure core enforces the same contract.
		ThrowsExactly<ArgumentOutOfRangeException>(
			() => PositionRecalculationService.Recalculate(0m, 0m, 0m, Sides.Buy, 100m, 0m));
		ThrowsExactly<ArgumentOutOfRangeException>(
			() => PositionRecalculationService.Recalculate(0m, 0m, 0m, Sides.Buy, 100m, -1m));
	}

	[TestMethod]
	public void RejectsOverflowInput()
	{
		// M08 range guard: a magnitude beyond DECIMAL(18,4) cannot be stored and must fail deterministically.
		ThrowsExactly<OverflowException>(
			() => PositionRecalculationService.Recalculate(0m, 0m, 0m, Sides.Buy, 1e20m, 10m));
	}

	[TestMethod]
	public void SellOpensShortFromFlat()
	{
		// Same-sign branch opening a SHORT from flat: sell 100 @ 10 => qty -100, avg 10, no realized P&L.
		// Confirms sell-from-flat sign handling (Abs(newQty) = 100 keeps the divide safe).
		var result = PositionRecalculationService.Recalculate(
			existingQty: 0m,
			existingAvgPrice: 0m,
			existingRealizedPnl: 0m,
			side: Sides.Sell,
			tradeQty: 100m,
			tradePrice: 10m);

		result.Quantity.AssertEqual(-100m);
		result.AveragePrice.AssertEqual(10m);
		result.RealizedPnl.AssertEqual(0m);
	}

	// =====================================================================================
	// Layer 2 - live integration + characterization/parity against the StockSharpLegacy DB.
	// The rolled-back "Live_*" tests exercise the real ApplyTradeAsync persistence path over a fresh,
	// unique (portfolio, security) scope and assert BOTH the returned state and the persisted
	// dbo.Positions row against the legacy oracle captured from dbo.usp_RecalculatePositionOnTrade BEFORE
	// the SQL layer was reduced to CRUD. The committed-scope tests (rollback, end-to-end, concurrency)
	// clean up after themselves. All are gated on DB reachability via Inconclusive.
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

	private static SqlParameter Money(string name, decimal value)
		=> new(name, SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = value };

	// Inserts a fresh, collision-free portfolio + security and returns their IDENTITY ids.
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

	// Inserts one ACCEPTED order and returns its BIGINT id (side 'B' or 'S').
	private static async Task<long> InsertOrderAsync(SqlConnection c, SqlTransaction t, int portfolioId, int securityId, char side, decimal qty, decimal price, CancellationToken ct)
	{
		await using var cmd = new SqlCommand(
			"INSERT INTO dbo.Orders (portfolio_id, security_id, side, qty, price, order_type, status) " +
			"OUTPUT INSERTED.order_id VALUES (@p, @s, @side, @qty, @price, 'LIMIT', 'ACCEPTED')", c, t);

		cmd.Parameters.Add(IntParam("@p", portfolioId));
		cmd.Parameters.Add(IntParam("@s", securityId));
		cmd.Parameters.Add(new SqlParameter("@side", SqlDbType.Char, 1) { Value = side.ToString() });
		cmd.Parameters.Add(Money("@qty", qty));
		cmd.Parameters.Add(Money("@price", price));

		return (long)await cmd.ExecuteScalarAsync(ct);
	}

	private static async Task InsertTradeAsync(SqlConnection c, SqlTransaction t, long orderId, decimal qty, decimal price, CancellationToken ct)
	{
		await using var cmd = new SqlCommand("INSERT INTO dbo.Trades (order_id, qty, price) VALUES (@o, @qty, @price)", c, t);
		cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.BigInt) { Value = orderId });
		cmd.Parameters.Add(Money("@qty", qty));
		cmd.Parameters.Add(Money("@price", price));
		await cmd.ExecuteNonQueryAsync(ct);
	}

	// Reads the persisted position row for a scope (null when no row exists).
	private static async Task<(decimal Qty, decimal Avg, decimal Realized)?> ReadPositionAsync(SqlConnection c, SqlTransaction t, int portfolioId, int securityId, CancellationToken ct)
	{
		await using var cmd = new SqlCommand(
			"SELECT qty, avg_price, realized_pnl FROM dbo.Positions WHERE portfolio_id = @p AND security_id = @s", c, t);
		cmd.Parameters.Add(IntParam("@p", portfolioId));
		cmd.Parameters.Add(IntParam("@s", securityId));

		await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);

		if (!await reader.ReadAsync(ct))
			return null;

		return (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2));
	}

	// Reads the MI-2 recompute checkpoint (last_applied_trade_id) persisted on the position row. Returns -1
	// when no row exists yet (distinct from the initial stored value of 0 on a freshly created row).
	private static async Task<long> ReadCheckpointAsync(SqlConnection c, SqlTransaction t, int portfolioId, int securityId, CancellationToken ct)
	{
		await using var cmd = new SqlCommand(
			"SELECT last_applied_trade_id FROM dbo.Positions WHERE portfolio_id = @p AND security_id = @s", c, t);
		cmd.Parameters.Add(IntParam("@p", portfolioId));
		cmd.Parameters.Add(IntParam("@s", securityId));

		var value = await cmd.ExecuteScalarAsync(ct);
		return value is null or DBNull ? -1L : (long)value;
	}

	// Reads the highest dbo.Trades.trade_id for a scope, used to assert the checkpoint tracks the latest
	// applied trade. Returns 0 when the scope has no trades.
	private static async Task<long> MaxTradeIdAsync(SqlConnection c, SqlTransaction t, int portfolioId, int securityId, CancellationToken ct)
	{
		await using var cmd = new SqlCommand(
			"SELECT ISNULL(MAX(tr.trade_id), 0) FROM dbo.Trades tr " +
			"INNER JOIN dbo.Orders o ON o.order_id = tr.order_id " +
			"WHERE o.portfolio_id = @p AND o.security_id = @s", c, t);
		cmd.Parameters.Add(IntParam("@p", portfolioId));
		cmd.Parameters.Add(IntParam("@s", securityId));

		return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
	}

	// Counts how many dbo.Trades rows exist for an order. Used by the F2 idempotency tests to prove that a
	// duplicate RecordTradeAsync (same external_trade_id) inserts the fill exactly once, while an un-keyed
	// duplicate inserts each call.
	private static async Task<int> CountTradesAsync(string connectionString, long orderId)
	{
		await using var connection = new SqlConnection(connectionString);
		await connection.OpenAsync();

		await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Trades WHERE order_id = @o", connection);
		cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.BigInt) { Value = orderId });

		return (int)await cmd.ExecuteScalarAsync();
	}

	// Runs the body against a fresh scope inside a transaction that is always rolled back.
	private async Task RunWithFreshScopeAsync(Func<PositionRecalculationService, SqlConnection, SqlTransaction, int, int, Task> body)
	{
		await using var connection = await TryOpenLegacyAsync();

		if (connection is null)
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live position-recalculation integration test.");
			return;
		}

		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

		try
		{
			var service = new PositionRecalculationService(SqlLegacyConnection.Resolve());
			var (portfolioId, securityId) = await InsertScopeAsync(connection, transaction, default);
			await body(service, connection, transaction, portfolioId, securityId);
		}
		finally
		{
			await transaction.RollbackAsync();
		}
	}

	// Removes every row a committed scope produced, children before parents (FK order).
	private static async Task CleanupScopeAsync(string connectionString, int portfolioId, int securityId)
	{
		await using var cleanup = new SqlConnection(connectionString);
		await cleanup.OpenAsync();

		await using var cmd = new SqlCommand(
			"""
			DELETE h FROM dbo.OrderStatusHistory h JOIN dbo.Orders o ON o.order_id = h.order_id WHERE o.portfolio_id = @p;
			DELETE FROM dbo.Trades WHERE order_id IN (SELECT order_id FROM dbo.Orders WHERE portfolio_id = @p);
			DELETE FROM dbo.Orders WHERE portfolio_id = @p;
			DELETE FROM dbo.Positions WHERE portfolio_id = @p;
			DELETE FROM dbo.RiskLimits WHERE portfolio_id = @p;
			DELETE FROM dbo.Securities WHERE security_id = @s;
			DELETE FROM dbo.Portfolios WHERE portfolio_id = @p;
			""", cleanup);
		cmd.Parameters.Add(IntParam("@p", portfolioId));
		cmd.Parameters.Add(IntParam("@s", securityId));
		await cmd.ExecuteNonQueryAsync();
	}

	[TestMethod]
	public Task Live_OpenLongPersists()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			var orderId = await InsertOrderAsync(c, t, pid, sid, 'B', 100m, 150m, default);
			await InsertTradeAsync(c, t, orderId, 100m, 150m, default);

			var result = await svc.ApplyTradeAsync(c, t, orderId);
			result.Quantity.AssertEqual(100m);
			result.AveragePrice.AssertEqual(150m);
			result.RealizedPnl.AssertEqual(0m);

			// The persisted dbo.Positions row must match the returned state (real persistence path).
			var persisted = await ReadPositionAsync(c, t, pid, sid, default);
			persisted.HasValue.AssertTrue();
			persisted.Value.Qty.AssertEqual(100m);
			persisted.Value.Avg.AssertEqual(150m);
			persisted.Value.Realized.AssertEqual(0m);
		});

	[TestMethod]
	public Task Live_AddThenPartialClosePersists()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// Legacy oracle: open long 100@150 -> add 100@160 (qty200 avg155) -> sell 50@170 partial.
			var buy1 = await InsertOrderAsync(c, t, pid, sid, 'B', 100m, 150m, default);
			await InsertTradeAsync(c, t, buy1, 100m, 150m, default);
			await svc.ApplyTradeAsync(c, t, buy1);

			var buy2 = await InsertOrderAsync(c, t, pid, sid, 'B', 100m, 160m, default);
			await InsertTradeAsync(c, t, buy2, 100m, 160m, default);
			var added = await svc.ApplyTradeAsync(c, t, buy2);
			added.Quantity.AssertEqual(200m);
			added.AveragePrice.AssertEqual(155m);
			added.RealizedPnl.AssertEqual(0m);

			var sell = await InsertOrderAsync(c, t, pid, sid, 'S', 50m, 170m, default);
			await InsertTradeAsync(c, t, sell, 50m, 170m, default);
			var partial = await svc.ApplyTradeAsync(c, t, sell);
			// closingQty 50 * (170 - 155) = 750 realized; remaining long 150 keeps avg 155.
			partial.Quantity.AssertEqual(150m);
			partial.AveragePrice.AssertEqual(155m);
			partial.RealizedPnl.AssertEqual(750m);

			var persisted = await ReadPositionAsync(c, t, pid, sid, default);
			persisted.Value.Qty.AssertEqual(150m);
			persisted.Value.Avg.AssertEqual(155m);
			persisted.Value.Realized.AssertEqual(750m);
		});

	[TestMethod]
	public Task Live_FlipLongToShortPersists()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// Legacy oracle flip: long 100@150 then sell 250@200 -> qty -150, avg 200, realized 5000.
			var buy = await InsertOrderAsync(c, t, pid, sid, 'B', 100m, 150m, default);
			await InsertTradeAsync(c, t, buy, 100m, 150m, default);
			await svc.ApplyTradeAsync(c, t, buy);

			var sell = await InsertOrderAsync(c, t, pid, sid, 'S', 250m, 200m, default);
			await InsertTradeAsync(c, t, sell, 250m, 200m, default);
			var flipped = await svc.ApplyTradeAsync(c, t, sell);
			flipped.Quantity.AssertEqual(-150m);
			flipped.AveragePrice.AssertEqual(200m);
			flipped.RealizedPnl.AssertEqual(5000m);
		});

	[TestMethod]
	public Task Live_FlipShortToLongPersists()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// Short-to-long flip: short 100@200 then buy 250@150.
			// closingQty 100 * (150-200) * sign(-100) = 5000 realized; remaining 150 opens long at 150.
			var sell = await InsertOrderAsync(c, t, pid, sid, 'S', 100m, 200m, default);
			await InsertTradeAsync(c, t, sell, 100m, 200m, default);
			await svc.ApplyTradeAsync(c, t, sell);

			var buy = await InsertOrderAsync(c, t, pid, sid, 'B', 250m, 150m, default);
			await InsertTradeAsync(c, t, buy, 250m, 150m, default);
			var flipped = await svc.ApplyTradeAsync(c, t, buy);
			flipped.Quantity.AssertEqual(150m);
			flipped.AveragePrice.AssertEqual(150m);
			flipped.RealizedPnl.AssertEqual(5000m);
		});

	[TestMethod]
	public Task Live_LossRealizationPersists()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// Legacy oracle loss: buy 100@150 then sell 100@140 -> qty 0, avg 0, realized -1000.
			var buy = await InsertOrderAsync(c, t, pid, sid, 'B', 100m, 150m, default);
			await InsertTradeAsync(c, t, buy, 100m, 150m, default);
			await svc.ApplyTradeAsync(c, t, buy);

			var sell = await InsertOrderAsync(c, t, pid, sid, 'S', 100m, 140m, default);
			await InsertTradeAsync(c, t, sell, 100m, 140m, default);
			var closed = await svc.ApplyTradeAsync(c, t, sell);
			closed.Quantity.AssertEqual(0m);
			closed.AveragePrice.AssertEqual(0m);
			closed.RealizedPnl.AssertEqual(-1000m);
		});

	[TestMethod]
	public Task Live_MidpointRoundingPersists()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// Same-sign short add with a non-terminating average: short 100@20 then sell 50@10.
			// avg = (100*20 + 50*10)/150 = 16.6666... -> DECIMAL(18,4) away-from-zero = 16.6667.
			var sell1 = await InsertOrderAsync(c, t, pid, sid, 'S', 100m, 20m, default);
			await InsertTradeAsync(c, t, sell1, 100m, 20m, default);
			await svc.ApplyTradeAsync(c, t, sell1);

			var sell2 = await InsertOrderAsync(c, t, pid, sid, 'S', 50m, 10m, default);
			await InsertTradeAsync(c, t, sell2, 50m, 10m, default);
			var result = await svc.ApplyTradeAsync(c, t, sell2);
			result.Quantity.AssertEqual(-150m);
			result.AveragePrice.AssertEqual(16.6667m);

			// The persisted DECIMAL(18,4) row must carry the same rounded scale.
			var persisted = await ReadPositionAsync(c, t, pid, sid, default);
			persisted.Value.Avg.AssertEqual(16.6667m);
		});

	[TestMethod]
	public Task Live_ApplyTradeIsIdempotent()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// C11 REAL double-count guard. One order, one persisted trade. Applying twice over the SAME
			// persisted trade set must yield the SAME position (100), NOT double it (200) - the recompute
			// reads all trades and folds from flat, so it is idempotent. The historical trigger+standalone
			// hazard (LEGACY_LAYER.md:L74-L89) is structurally impossible here.
			var orderId = await InsertOrderAsync(c, t, pid, sid, 'B', 100m, 150m, default);
			await InsertTradeAsync(c, t, orderId, 100m, 150m, default);

			var first = await svc.ApplyTradeAsync(c, t, orderId);
			first.Quantity.AssertEqual(100m);

			var second = await svc.ApplyTradeAsync(c, t, orderId);
			second.Quantity.AssertEqual(100m);          // NOT 200 - idempotent
			second.AveragePrice.AssertEqual(150m);
			second.RealizedPnl.AssertEqual(0m);

			var persisted = await ReadPositionAsync(c, t, pid, sid, default);
			persisted.Value.Qty.AssertEqual(100m);      // persisted row is single-applied, not doubled
		});

	[TestMethod]
	public Task Live_RecomputeSeesAllPersistedTrades()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// Complements the idempotency guard: after a SECOND real trade is persisted, the recompute
			// reflects the full set (qty 200, avg 155) - proving it reads all trades, not a stale row.
			var buy1 = await InsertOrderAsync(c, t, pid, sid, 'B', 100m, 150m, default);
			await InsertTradeAsync(c, t, buy1, 100m, 150m, default);
			var afterOne = await svc.ApplyTradeAsync(c, t, buy1);
			afterOne.Quantity.AssertEqual(100m);

			var buy2 = await InsertOrderAsync(c, t, pid, sid, 'B', 100m, 160m, default);
			await InsertTradeAsync(c, t, buy2, 100m, 160m, default);
			var afterTwo = await svc.ApplyTradeAsync(c, t, buy2);
			afterTwo.Quantity.AssertEqual(200m);
			afterTwo.AveragePrice.AssertEqual(155m);
		});

	[TestMethod]
	public Task Live_RecomputeCheckpointAdvancesAndBoundsReplay()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// MI-2: the recompute persists a checkpoint (dbo.Positions.last_applied_trade_id) so each fill
			// folds only the NEW trades (trade_id > checkpoint) rather than replaying the entire history.
			// Prove the checkpoint advances to the latest applied trade after each fill, that the position
			// still reflects the full set, and that a re-apply is idempotent (folds nothing, no double-count).

			// First fill: the position row is created and the checkpoint advances from the initial 0.
			var buy1 = await InsertOrderAsync(c, t, pid, sid, 'B', 100m, 150m, default);
			await InsertTradeAsync(c, t, buy1, 100m, 150m, default);
			var afterOne = await svc.ApplyTradeAsync(c, t, buy1);
			afterOne.Quantity.AssertEqual(100m);

			var maxAfterOne = await MaxTradeIdAsync(c, t, pid, sid, default);
			var cpAfterOne = await ReadCheckpointAsync(c, t, pid, sid, default);
			(cpAfterOne > 0).AssertTrue();              // advanced past the initial 0
			cpAfterOne.AssertEqual(maxAfterOne);         // checkpoint tracks the latest applied trade

			// Second fill: the checkpoint must advance again and the position reflect BOTH trades.
			var buy2 = await InsertOrderAsync(c, t, pid, sid, 'B', 100m, 160m, default);
			await InsertTradeAsync(c, t, buy2, 100m, 160m, default);
			var afterTwo = await svc.ApplyTradeAsync(c, t, buy2);
			afterTwo.Quantity.AssertEqual(200m);
			afterTwo.AveragePrice.AssertEqual(155m);

			var maxAfterTwo = await MaxTradeIdAsync(c, t, pid, sid, default);
			var cpAfterTwo = await ReadCheckpointAsync(c, t, pid, sid, default);
			(cpAfterTwo > cpAfterOne).AssertTrue();      // advanced beyond the first fill's checkpoint
			cpAfterTwo.AssertEqual(maxAfterTwo);

			// Re-apply the SAME latest trade: every trade_id is <= checkpoint, so the fold is empty and the
			// position and checkpoint are unchanged (bounded, idempotent; the double-count hazard stays gone).
			var reapply = await svc.ApplyTradeAsync(c, t, buy2);
			reapply.Quantity.AssertEqual(200m);
			reapply.AveragePrice.AssertEqual(155m);
			var cpReapply = await ReadCheckpointAsync(c, t, pid, sid, default);
			cpReapply.AssertEqual(cpAfterTwo);           // checkpoint did not move on the no-op re-apply
		});

	[TestMethod]
	public Task Live_OrderNotFoundThrows()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// An orderId with no dbo.Orders row cannot resolve a portfolio/security and must fail loudly.
			await ThrowsAsync<InvalidOperationException>(async () => await svc.ApplyTradeAsync(c, t, 9_000_000_000L));
		});

	[TestMethod]
	public async Task Live_RollbackDiscardsPersistedPosition()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		await using (var probe = await TryOpenLegacyAsync())
		{
			if (probe is null)
			{
				Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live rollback test.");
				return;
			}
		}

		int portfolioId;
		int securityId;
		long orderId;

		// Commit a scope with one buy order but NO trade/position yet.
		await using (var setup = new SqlConnection(connectionString))
		{
			await setup.OpenAsync();
			await using var tx = (SqlTransaction)await setup.BeginTransactionAsync(IsolationLevel.ReadCommitted);
			(portfolioId, securityId) = await InsertScopeAsync(setup, tx, default);
			orderId = await InsertOrderAsync(setup, tx, portfolioId, securityId, 'B', 100m, 150m, default);
			await tx.CommitAsync();
		}

		try
		{
			var service = new PositionRecalculationService(connectionString);

			// In a SEPARATE transaction, insert a trade and persist the position, then ROLL BACK.
			await using (var work = new SqlConnection(connectionString))
			{
				await work.OpenAsync();
				await using var tx = (SqlTransaction)await work.BeginTransactionAsync(IsolationLevel.ReadCommitted);
				await InsertTradeAsync(work, tx, orderId, 100m, 150m, default);
				var applied = await service.ApplyTradeAsync(work, tx, orderId);
				applied.Quantity.AssertEqual(100m);      // visible within the transaction

				var within = await ReadPositionAsync(work, tx, portfolioId, securityId, default);
				within.HasValue.AssertTrue();            // present before rollback

				await tx.RollbackAsync();
			}

			// A fresh connection must see NO position row - the write was inside the rolled-back transaction.
			await using (var verify = new SqlConnection(connectionString))
			{
				await verify.OpenAsync();
				await using var tx = (SqlTransaction)await verify.BeginTransactionAsync(IsolationLevel.ReadCommitted);
				var after = await ReadPositionAsync(verify, tx, portfolioId, securityId, default);
				(after is null).AssertTrue();
				await tx.RollbackAsync();
			}
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	[TestMethod]
	public async Task Live_GatewayRecordTradeSingleApplyEndToEnd()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		await using (var probe = await TryOpenLegacyAsync())
		{
			if (probe is null)
			{
				Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live gateway end-to-end test.");
				return;
			}
		}

		int portfolioId;
		int securityId;

		await using (var setup = new SqlConnection(connectionString))
		{
			await setup.OpenAsync();
			await using var tx = (SqlTransaction)await setup.BeginTransactionAsync(IsolationLevel.ReadCommitted);
			(portfolioId, securityId) = await InsertScopeAsync(setup, tx, default);
			await tx.CommitAsync();
		}

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);

			// No RiskLimits row for this fresh scope -> unlimited -> the order is accepted.
			var submit = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 150m, OrderTypes.Limit);
			submit.IsValid.AssertTrue();

			// A single RecordTradeAsync must apply the fill EXACTLY once end-to-end (not double-count).
			await gateway.RecordTradeAsync(submit.OrderId, 100m, 150m);

			var position = await gateway.GetPositionAsync(portfolioId, securityId);
			position.AssertNotNull();
			position.Quantity.AssertEqual(100m);       // single apply -> 100, NOT 200
			position.AveragePrice.AssertEqual(150m);
			position.RealizedPnL.AssertEqual(0m);
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	[TestMethod]
	public async Task Live_ConcurrentFillsSerializeThroughGateway()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		await using (var probe = await TryOpenLegacyAsync())
		{
			if (probe is null)
			{
				Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live concurrent-fill test.");
				return;
			}
		}

		int portfolioId;
		int securityId;
		long orderId;

		await using (var setup = new SqlConnection(connectionString))
		{
			await setup.OpenAsync();
			await using var tx = (SqlTransaction)await setup.BeginTransactionAsync(IsolationLevel.ReadCommitted);
			(portfolioId, securityId) = await InsertScopeAsync(setup, tx, default);
			orderId = await InsertOrderAsync(setup, tx, portfolioId, securityId, 'B', 200m, 155m, default);
			await tx.CommitAsync();
		}

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);

			// Two concurrent fills of the same instrument. The gateway acquires a per-position application
			// lock BEFORE inserting each trade, so the two fills serialize cleanly (no insert-then-lock
			// deadlock, QA finding F1); because each recompute then folds over ALL committed trades, the
			// last writer sees both fills - so neither fill is lost (no double-count, no lost update).
			// Final: qty 200, avg (100*150 + 100*160)/200 = 155.
			var fillA = gateway.RecordTradeAsync(orderId, 100m, 150m);
			var fillB = gateway.RecordTradeAsync(orderId, 100m, 160m);
			await Task.WhenAll(fillA, fillB);

			var position = await gateway.GetPositionAsync(portfolioId, securityId);
			position.AssertNotNull();
			position.Quantity.AssertEqual(200m);
			position.AveragePrice.AssertEqual(155m);
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	[TestMethod]
	public async Task Live_HighContentionConcurrentFillsNeverDeadlockOrLoseFills()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		await using (var probe = await TryOpenLegacyAsync())
		{
			if (probe is null)
			{
				Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live high-contention concurrent-fill test.");
				return;
			}
		}

		int portfolioId;
		int securityId;
		long orderId;

		await using (var setup = new SqlConnection(connectionString))
		{
			await setup.OpenAsync();
			await using var tx = (SqlTransaction)await setup.BeginTransactionAsync(IsolationLevel.ReadCommitted);
			(portfolioId, securityId) = await InsertScopeAsync(setup, tx, default);
			orderId = await InsertOrderAsync(setup, tx, portfolioId, securityId, 'B', 1000m, 100m, default);
			await tx.CommitAsync();
		}

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);

			// F1 regression guard. Fire MANY simultaneous un-keyed fills of the SAME position - the exact
			// scenario that deadlocked (SQL 1205) and silently lost fills under the old insert-then-lock
			// ordering (a Barrier-forced standalone harness reproduced it 100% of the time). With the fix
			// (per-position application lock acquired BEFORE the INSERT, plus a bounded deadlock retry) every
			// fill serializes cleanly: Task.WhenAll must complete WITHOUT throwing and no fill may be lost.
			const int fills = 12;
			const decimal each = 10m;

			var tasks = new Task[fills];
			for (var i = 0; i < fills; i++)
				tasks[i] = gateway.RecordTradeAsync(orderId, each, 100m);

			await Task.WhenAll(tasks);   // no unhandled deadlock (1205) may surface

			// Every fill applied exactly once: qty == fills*each, and dbo.Trades holds exactly one row per call.
			var position = await gateway.GetPositionAsync(portfolioId, securityId);
			position.AssertNotNull();
			position.Quantity.AssertEqual(fills * each);           // 120 - no lost fills
			(await CountTradesAsync(connectionString, orderId)).AssertEqual(fills);
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	[TestMethod]
	public async Task Live_RecordTradeIsIdempotentWithExternalTradeId()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		await using (var probe = await TryOpenLegacyAsync())
		{
			if (probe is null)
			{
				Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live idempotency-key test.");
				return;
			}
		}

		int portfolioId;
		int securityId;

		await using (var setup = new SqlConnection(connectionString))
		{
			await setup.OpenAsync();
			await using var tx = (SqlTransaction)await setup.BeginTransactionAsync(IsolationLevel.ReadCommitted);
			(portfolioId, securityId) = await InsertScopeAsync(setup, tx, default);
			await tx.CommitAsync();
		}

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);
			var submit = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 150m, OrderTypes.Limit);
			submit.IsValid.AssertTrue();

			// F2: recording the SAME logical fill twice (same external_trade_id) - e.g. a client retry - must
			// record it exactly once and apply it to the position exactly once (qty 100, NOT double-counted 200).
			const long externalTradeId = 918_273_645L;
			await gateway.RecordTradeAsync(submit.OrderId, 100m, 150m, externalTradeId);
			await gateway.RecordTradeAsync(submit.OrderId, 100m, 150m, externalTradeId);

			(await CountTradesAsync(connectionString, submit.OrderId)).AssertEqual(1);   // one trade row, not two

			var position = await gateway.GetPositionAsync(portfolioId, securityId);
			position.AssertNotNull();
			position.Quantity.AssertEqual(100m);        // single apply -> 100, NOT 200
			position.AveragePrice.AssertEqual(150m);
			position.RealizedPnL.AssertEqual(0m);
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	[TestMethod]
	public async Task Live_RecordTradeWithoutKeyRecordsEachFill()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		await using (var probe = await TryOpenLegacyAsync())
		{
			if (probe is null)
			{
				Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live no-key duplicate test.");
				return;
			}
		}

		int portfolioId;
		int securityId;

		await using (var setup = new SqlConnection(connectionString))
		{
			await setup.OpenAsync();
			await using var tx = (SqlTransaction)await setup.BeginTransactionAsync(IsolationLevel.ReadCommitted);
			(portfolioId, securityId) = await InsertScopeAsync(setup, tx, default);
			await tx.CommitAsync();
		}

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);
			var submit = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 200m, 150m, OrderTypes.Limit);
			submit.IsValid.AssertTrue();

			// Backward-compatibility: without an idempotency key each call is a DISTINCT fill (original
			// behavior preserved for callers that do not supply a key), so two calls record two trades and
			// the position reflects both (qty 200).
			await gateway.RecordTradeAsync(submit.OrderId, 100m, 150m);
			await gateway.RecordTradeAsync(submit.OrderId, 100m, 150m);

			(await CountTradesAsync(connectionString, submit.OrderId)).AssertEqual(2);   // both fills recorded

			var position = await gateway.GetPositionAsync(portfolioId, securityId);
			position.AssertNotNull();
			position.Quantity.AssertEqual(200m);        // both applied - legacy behavior preserved
			position.AveragePrice.AssertEqual(150m);
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	[TestMethod]
	public async Task Live_ConcurrentDuplicateKeyAppliesOnce()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		await using (var probe = await TryOpenLegacyAsync())
		{
			if (probe is null)
			{
				Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live concurrent-duplicate-key test.");
				return;
			}
		}

		int portfolioId;
		int securityId;

		await using (var setup = new SqlConnection(connectionString))
		{
			await setup.OpenAsync();
			await using var tx = (SqlTransaction)await setup.BeginTransactionAsync(IsolationLevel.ReadCommitted);
			(portfolioId, securityId) = await InsertScopeAsync(setup, tx, default);
			await tx.CommitAsync();
		}

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);
			var submit = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 150m, OrderTypes.Limit);
			submit.IsValid.AssertTrue();

			// F1 + F2 together: fire MANY concurrent recordings of the SAME logical fill (same
			// external_trade_id) - the retry-storm the deadlock retry (F1) could otherwise turn into a
			// double-count. The per-position application lock serializes them and the idempotency key (F2)
			// makes recording exactly-once regardless of overlap or retry: exactly one trade row, applied once.
			const long externalTradeId = 112_233_445L;
			const int attempts = 8;

			var tasks = new Task[attempts];
			for (var i = 0; i < attempts; i++)
				tasks[i] = gateway.RecordTradeAsync(submit.OrderId, 100m, 150m, externalTradeId);

			await Task.WhenAll(tasks);

			(await CountTradesAsync(connectionString, submit.OrderId)).AssertEqual(1);   // exactly one, despite 8 concurrent calls

			var position = await gateway.GetPositionAsync(portfolioId, securityId);
			position.AssertNotNull();
			position.Quantity.AssertEqual(100m);        // applied once
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	// ==================================================================================================
	// MA-18 - deterministic integration coverage for every listed gateway failure mode / contract.
	// These commit real rows through the public gateway API (the methods open their own connections and
	// commit), so each sets up a committed scope, exercises the gateway, asserts, and always cleans up in
	// a finally. Post-commit-retry idempotency (#2) is proven by Live_RecordTradeIsIdempotentWithExternalTradeId
	// and public overload compatibility (#14) by Live_RecordTradeWithoutKeyRecordsEachFill above.
	// ==================================================================================================

	// #9 - a REJECTED order (recorded for audit) can never be filled: the M08 fillable guard rejects the
	// fill and nothing is persisted.
	[TestMethod]
	public async Task Live_RejectedOrderCannotBeFilled()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		if (!await LegacyReachableAsync())
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live rejected-order fill test.");
			return;
		}

		var (portfolioId, securityId) = await SetupCommittedScopeAsync(connectionString, limitsPrice: 500m);

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);

			// 600 >= max_order_price 500 -> the order is RECORDED as REJECTED (not dropped), with a reason.
			var submit = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 600m, OrderTypes.Limit);
			submit.IsValid.AssertFalse();
			submit.RejectReason.AssertNotNull();

			// Attempting to fill the rejected order throws and records nothing.
			await ThrowsExactlyAsync<InvalidOperationException>(async ()
				=> await gateway.RecordTradeAsync(submit.OrderId, 100m, 600m));

			(await CountTradesAsync(connectionString, submit.OrderId)).AssertEqual(0);
			(await gateway.GetPositionAsync(portfolioId, securityId)).AssertNull();
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	// #10 - a fill whose cumulative quantity would exceed the ordered quantity is rejected, and the partial
	// state of the failed attempt is rolled back (the earlier legal fill remains).
	[TestMethod]
	public async Task Live_OverfillIsRejected()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		if (!await LegacyReachableAsync())
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live overfill test.");
			return;
		}

		var (portfolioId, securityId) = await SetupCommittedScopeAsync(connectionString);

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);
			var submit = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 150m, OrderTypes.Limit);
			submit.IsValid.AssertTrue();

			await gateway.RecordTradeAsync(submit.OrderId, 60m, 150m);   // legal partial fill

			// 60 + 50 = 110 > ordered 100 -> over-fill, rejected.
			await ThrowsExactlyAsync<InvalidOperationException>(async ()
				=> await gateway.RecordTradeAsync(submit.OrderId, 50m, 150m));

			// Only the legal fill survives; the over-fill attempt's transaction rolled back atomically.
			(await CountTradesAsync(connectionString, submit.OrderId)).AssertEqual(1);
			var position = await gateway.GetPositionAsync(portfolioId, securityId);
			position.AssertNotNull();
			position.Quantity.AssertEqual(60m);
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	// #3 - replaying a trade idempotency key with a DIFFERENT payload is a conflict (thrown), never a
	// silent second fill; the original fill is untouched.
	[TestMethod]
	public async Task Live_DuplicateTradeKeyMismatchThrowsConflict()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		if (!await LegacyReachableAsync())
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live trade-key-mismatch test.");
			return;
		}

		var (portfolioId, securityId) = await SetupCommittedScopeAsync(connectionString);

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);
			var submit = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 200m, 150m, OrderTypes.Limit);
			submit.IsValid.AssertTrue();

			const long tradeKey = 555_000_1L;
			await gateway.RecordTradeAsync(submit.OrderId, 100m, 150m, tradeKey);

			// Same key, different quantity -> conflict.
			await ThrowsExactlyAsync<SqlLegacyIdempotencyConflictException>(async ()
				=> await gateway.RecordTradeAsync(submit.OrderId, 50m, 150m, tradeKey));

			(await CountTradesAsync(connectionString, submit.OrderId)).AssertEqual(1);   // the mismatch never inserted
			var position = await gateway.GetPositionAsync(portfolioId, securityId);
			position.AssertNotNull();
			position.Quantity.AssertEqual(100m);
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	// #4 - the trade idempotency key is GLOBAL (filtered unique index). Reusing a key already recorded for
	// one order against a DIFFERENT order (a different position) is a conflict, not a cross-position double.
	[TestMethod]
	public async Task Live_CrossPositionSameTradeKeyThrowsConflict()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		if (!await LegacyReachableAsync())
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live cross-position trade-key test.");
			return;
		}

		var (portfolioA, securityA) = await SetupCommittedScopeAsync(connectionString);
		var (portfolioB, securityB) = await SetupCommittedScopeAsync(connectionString);

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);

			var submitA = await gateway.SubmitOrderAsync(portfolioA, securityA, Sides.Buy, 100m, 150m, OrderTypes.Limit);
			var submitB = await gateway.SubmitOrderAsync(portfolioB, securityB, Sides.Buy, 100m, 150m, OrderTypes.Limit);
			submitA.IsValid.AssertTrue();
			submitB.IsValid.AssertTrue();

			const long sharedKey = 777_000_2L;
			await gateway.RecordTradeAsync(submitA.OrderId, 100m, 150m, sharedKey);

			// Same key, different order -> conflict (existing.OrderId != submitB.OrderId).
			await ThrowsExactlyAsync<SqlLegacyIdempotencyConflictException>(async ()
				=> await gateway.RecordTradeAsync(submitB.OrderId, 100m, 150m, sharedKey));

			// Scope A recorded exactly one fill; scope B recorded none.
			(await CountTradesAsync(connectionString, submitA.OrderId)).AssertEqual(1);
			(await CountTradesAsync(connectionString, submitB.OrderId)).AssertEqual(0);
			(await gateway.GetPositionAsync(portfolioB, securityB)).AssertNull();
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioA, securityA);
			await CleanupScopeAsync(connectionString, portfolioB, securityB);
		}
	}

	// #5 - order-submit idempotency: replaying a submit key with the SAME payload returns the ORIGINAL
	// outcome (one order); replaying it with a DIFFERENT payload is a conflict. Never a silent second order.
	[TestMethod]
	public async Task Live_DuplicateSubmitKeyBehavior()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		if (!await LegacyReachableAsync())
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live submit-key test.");
			return;
		}

		var (portfolioId, securityId) = await SetupCommittedScopeAsync(connectionString);

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);
			const long submitKey = 333_000_3L;

			var first = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 150m, OrderTypes.Limit, submitKey);
			first.IsValid.AssertTrue();

			// Same key + same payload -> returns the ORIGINAL order, no second row inserted.
			var replay = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 150m, OrderTypes.Limit, submitKey);
			replay.OrderId.AssertEqual(first.OrderId);
			replay.IsValid.AssertTrue();
			(await CountOrdersAsync(connectionString, portfolioId)).AssertEqual(1);

			// Same key + different payload (different qty) -> conflict.
			await ThrowsExactlyAsync<SqlLegacyIdempotencyConflictException>(async ()
				=> await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 200m, 150m, OrderTypes.Limit, submitKey));

			(await CountOrdersAsync(connectionString, portfolioId)).AssertEqual(1);   // still exactly one order
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	// #6 - a submit and a fill for the SAME portfolio, fired concurrently, serialize through the shared
	// per-portfolio application lock (review finding C04) without deadlocking, and both complete with a
	// consistent final state.
	[TestMethod]
	public async Task Live_SubmitAndFillRaceSerializeThroughPortfolioLock()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		if (!await LegacyReachableAsync())
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live submit-vs-fill race test.");
			return;
		}

		int portfolioId;
		int securityId;
		long seededOrderId;

		await using (var setup = new SqlConnection(connectionString))
		{
			await setup.OpenAsync();
			await using var tx = (SqlTransaction)await setup.BeginTransactionAsync(IsolationLevel.ReadCommitted);
			(portfolioId, securityId) = await InsertScopeAsync(setup, tx, default);
			seededOrderId = await InsertOrderAsync(setup, tx, portfolioId, securityId, 'B', 200m, 150m, default);
			await tx.CommitAsync();
		}

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);

			// Concurrent submit (new order) + fill (of the seeded order) on the SAME portfolio. The shared
			// portfolio lock serializes them; neither may deadlock or error.
			var submitTask = gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 150m, OrderTypes.Limit);
			var fillTask = gateway.RecordTradeAsync(seededOrderId, 200m, 150m);
			await Task.WhenAll(submitTask, fillTask);

			(await submitTask).IsValid.AssertTrue();   // the new order was accepted

			// The seeded order's fill applied exactly once; the still-open new order does not affect the
			// booked position, so qty reflects only the 200 filled.
			var position = await gateway.GetPositionAsync(portfolioId, securityId);
			position.AssertNotNull();
			position.Quantity.AssertEqual(200m);
			(await CountTradesAsync(connectionString, seededOrderId)).AssertEqual(1);
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	// #7 - CURRENT behavior (AAP 0.7.1 preserve-observable-behavior): an ACCEPTED but unfilled order does
	// NOT reserve position capacity. The pre-trade gate mirrors the original SQL check, which counted only
	// BOOKED positions. This test documents that decision - the reservation model is intentionally NOT added.
	[TestMethod]
	public async Task Live_AcceptedOpenOrderDoesNotReserveExposure()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		if (!await LegacyReachableAsync())
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live open-order reservation test.");
			return;
		}

		// Only the position-size ceiling is enforced (150); every other ceiling is NULL/not-enforced so it
		// cannot mask the effect under test.
		var (portfolioId, securityId) = await SetupCommittedScopeAsync(connectionString, limitsPosition: 150m);

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);

			// First order accepted: resulting |0 + 100| = 100 < 150.
			var first = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 10m, OrderTypes.Limit);
			first.IsValid.AssertTrue();

			// Second order (first still UNFILLED): the gate sees the CURRENT booked position (0), so
			// |0 + 100| = 100 < 150 and it is ACCEPTED. If open orders reserved exposure it would be
			// |100 + 100| = 200 >= 150 and rejected. Accepted == current, documented, behavior.
			var second = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 10m, OrderTypes.Limit);
			second.IsValid.AssertTrue();
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	// #8 - the persisted dbo.Orders row contract for both an accepted and a rejected submission: every
	// column the demo/consumers rely on is written exactly as expected (side, qty, price, type, status,
	// reject_reason, external_transaction_id).
	[TestMethod]
	public async Task Live_PersistedOrderRowContract()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		if (!await LegacyReachableAsync())
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live order-row contract test.");
			return;
		}

		var (portfolioId, securityId) = await SetupCommittedScopeAsync(connectionString, limitsPrice: 500m);

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);

			var accepted = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 150m, OrderTypes.Limit, 8001L);
			var rejected = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Sell, 50m, 600m, OrderTypes.Limit, 8002L);
			accepted.IsValid.AssertTrue();
			rejected.IsValid.AssertFalse();

			var a = await ReadOrderRowAsync(connectionString, accepted.OrderId);
			a.Side.AssertEqual("B");
			a.Qty.AssertEqual(100m);
			a.Price.AssertEqual(150m);
			a.OrderType.AssertEqual("LIMIT");
			a.Status.AssertEqual("ACCEPTED");
			a.RejectReason.AssertNull();
			a.ExternalTransactionId.AssertEqual(8001L);

			var r = await ReadOrderRowAsync(connectionString, rejected.OrderId);
			r.Side.AssertEqual("S");
			r.Qty.AssertEqual(50m);
			r.Price.AssertEqual(600m);
			r.OrderType.AssertEqual("LIMIT");
			r.Status.AssertEqual("REJECTED");
			r.RejectReason.AssertNotNull();
			r.ExternalTransactionId.AssertEqual(8002L);
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	// #1 - failure injection + atomic rollback: after a legal fill commits, an over-fill attempt (the
	// injected mid-operation failure, thrown from the M08 guard under the locks) must leave NO partial
	// state - the trade count and the position are exactly what the successful fill left.
	[TestMethod]
	public async Task Live_FailedFillLeavesNoPartialState()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		if (!await LegacyReachableAsync())
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live failure-injection test.");
			return;
		}

		var (portfolioId, securityId) = await SetupCommittedScopeAsync(connectionString);

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);
			var submit = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 150m, OrderTypes.Limit);
			submit.IsValid.AssertTrue();

			await gateway.RecordTradeAsync(submit.OrderId, 100m, 150m);   // fully fills the order

			var before = await gateway.GetPositionAsync(portfolioId, securityId);
			before.AssertNotNull();
			before.Quantity.AssertEqual(100m);

			// Injected failure: any further fill over-fills (100 + 10 > 100) and throws after the locks are
			// taken but before/around the insert - the whole attempt rolls back.
			await ThrowsExactlyAsync<InvalidOperationException>(async ()
				=> await gateway.RecordTradeAsync(submit.OrderId, 10m, 150m));

			// State is byte-identical to before the failed attempt: no orphan trade, no position drift.
			(await CountTradesAsync(connectionString, submit.OrderId)).AssertEqual(1);
			var after = await gateway.GetPositionAsync(portfolioId, securityId);
			after.AssertNotNull();
			after.Quantity.AssertEqual(100m);
			after.AveragePrice.AssertEqual(150m);
			after.RealizedPnL.AssertEqual(0m);
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	// #11 - the schema's idempotent upgrade guards (CR-5 external_trade_id, MI-2 last_applied_trade_id) are
	// re-runnable and non-destructive: re-executing them when the columns already exist is a clean no-op
	// (no error, columns intact, seed rows untouched). Runs in a rolled-back transaction, so it changes
	// nothing even if a column were somehow missing.
	[TestMethod]
	public async Task Live_SchemaGuardsAreIdempotentAndNonDestructive()
	{
		await using var connection = await TryOpenLegacyAsync();

		if (connection is null)
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live schema-rerun test.");
			return;
		}

		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

		try
		{
			var rlBefore = await ScalarIntAsync(connection, transaction, "SELECT COUNT(*) FROM dbo.RiskLimits");
			var pfBefore = await ScalarIntAsync(connection, transaction, "SELECT COUNT(*) FROM dbo.Portfolios");
			var secBefore = await ScalarIntAsync(connection, transaction, "SELECT COUNT(*) FROM dbo.Securities");

			// The verbatim guarded upgrade statements from Database/001_Schema.sql. Executing them twice
			// must not throw and must not alter data because the columns already exist.
			const string tradeGuard =
				"IF COL_LENGTH(N'dbo.Trades', N'external_trade_id') IS NULL " +
				"ALTER TABLE dbo.Trades ADD external_trade_id BIGINT NULL;";
			const string positionGuard =
				"IF COL_LENGTH(N'dbo.Positions', N'last_applied_trade_id') IS NULL " +
				"ALTER TABLE dbo.Positions ADD last_applied_trade_id BIGINT NOT NULL CONSTRAINT DF_Positions_last_applied_trade_id DEFAULT (0);";

			for (var i = 0; i < 2; i++)
			{
				await ExecAsync(connection, transaction, tradeGuard);
				await ExecAsync(connection, transaction, positionGuard);
			}

			// Both upgrade columns are present after the schema has been applied.
			(await ScalarIntAsync(connection, transaction, "SELECT ISNULL(COL_LENGTH(N'dbo.Trades', N'external_trade_id'), 0)") > 0).AssertTrue();
			(await ScalarIntAsync(connection, transaction, "SELECT ISNULL(COL_LENGTH(N'dbo.Positions', N'last_applied_trade_id'), 0)") > 0).AssertTrue();

			// Non-destructive: seed row counts unchanged by the rerun.
			(await ScalarIntAsync(connection, transaction, "SELECT COUNT(*) FROM dbo.RiskLimits")).AssertEqual(rlBefore);
			(await ScalarIntAsync(connection, transaction, "SELECT COUNT(*) FROM dbo.Portfolios")).AssertEqual(pfBefore);
			(await ScalarIntAsync(connection, transaction, "SELECT COUNT(*) FROM dbo.Securities")).AssertEqual(secBefore);
		}
		finally
		{
			await transaction.RollbackAsync();
		}
	}

	// #12 - audit semantics (MA-12/MA-20): trg_Orders_StatusAudit is AFTER UPDATE only. An INSERT creates
	// NO history row; a status UPDATE creates exactly one; a non-status UPDATE creates none. This is a
	// best-effort append audit of status CHANGES, not a tamper-proof or insert-time log. Rolled back.
	[TestMethod]
	public async Task Live_AuditTriggerRecordsStatusUpdatesNotInserts()
	{
		await using var connection = await TryOpenLegacyAsync();

		if (connection is null)
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live audit-semantics test.");
			return;
		}

		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

		try
		{
			var (portfolioId, securityId) = await InsertScopeAsync(connection, transaction, default);
			var orderId = await InsertOrderAsync(connection, transaction, portfolioId, securityId, 'B', 100m, 150m, default);

			// INSERT does not audit (the trigger is AFTER UPDATE only).
			(await CountOrderHistoryAsync(connection, transaction, orderId)).AssertEqual(0);

			// A status change is audited exactly once.
			await ExecOrderUpdateAsync(connection, transaction, orderId, "status = 'FILLED'");
			(await CountOrderHistoryAsync(connection, transaction, orderId)).AssertEqual(1);

			// A non-status update is NOT audited (IF NOT UPDATE(status) RETURN).
			await ExecOrderUpdateAsync(connection, transaction, orderId, "last_updated = SYSUTCDATETIME()");
			(await CountOrderHistoryAsync(connection, transaction, orderId)).AssertEqual(1);
		}
		finally
		{
			await transaction.RollbackAsync();
		}
	}

	// #13 - demo automation: the three observable outcomes of Samples/08_Misc/03_LegacySqlDemo, driven
	// through the gateway - a compliant order accepted, an order over the seeded max_order_price rejected
	// with a reason, and a recorded fill that recomputes the position automatically.
	[TestMethod]
	public async Task Live_DemoScenarioThreeOutcomes()
	{
		var connectionString = SqlLegacyConnection.Resolve();

		if (!await LegacyReachableAsync())
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live demo-scenario test.");
			return;
		}

		var (portfolioId, securityId) = await SetupCommittedScopeAsync(connectionString, limitsPrice: 500m);

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);

			// Outcome 1 - compliant order accepted.
			var accepted = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 150m, OrderTypes.Limit);
			accepted.IsValid.AssertTrue();

			// Outcome 2 - order over the seeded max_order_price (500) rejected with a reason.
			var rejected = await gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 600m, OrderTypes.Limit);
			rejected.IsValid.AssertFalse();
			rejected.RejectReason.AssertNotNull();

			// Outcome 3 - a recorded fill recomputes the position automatically.
			await gateway.RecordTradeAsync(accepted.OrderId, 100m, 150m);
			var position = await gateway.GetPositionAsync(portfolioId, securityId);
			position.AssertNotNull();
			position.Quantity.AssertEqual(100m);
			position.AveragePrice.AssertEqual(150m);
		}
		finally
		{
			await CleanupScopeAsync(connectionString, portfolioId, securityId);
		}
	}

	// ==================================================================================================
	// MA-18 helpers
	// ==================================================================================================

	private static async Task<bool> LegacyReachableAsync()
	{
		await using var probe = await TryOpenLegacyAsync();
		return probe is not null;
	}

	// Commits a fresh scope (and, when a ceiling is supplied, a scoped RiskLimits row) so the gateway - which
	// opens its own connection - can see it. Returns the committed portfolio+security ids.
	private static async Task<(int PortfolioId, int SecurityId)> SetupCommittedScopeAsync(
		string connectionString, decimal? limitsPrice = null, decimal? limitsPosition = null, decimal? limitsDaily = null)
	{
		await using var setup = new SqlConnection(connectionString);
		await setup.OpenAsync();
		await using var tx = (SqlTransaction)await setup.BeginTransactionAsync(IsolationLevel.ReadCommitted);

		var (portfolioId, securityId) = await InsertScopeAsync(setup, tx, default);

		if (limitsPrice is not null || limitsPosition is not null || limitsDaily is not null)
			await InsertRiskLimitsAsync(setup, tx, portfolioId, securityId, limitsPrice, limitsPosition, limitsDaily);

		await tx.CommitAsync();
		return (portfolioId, securityId);
	}

	// Inserts a portfolio+security-scoped RiskLimits row. Only the ceilings supplied are enforced; every
	// other max_* column is left NULL ("not enforced"), and commission_rate takes the schema default.
	private static async Task InsertRiskLimitsAsync(
		SqlConnection c, SqlTransaction t, int portfolioId, int securityId,
		decimal? price, decimal? position, decimal? daily)
	{
		await using var cmd = new SqlCommand(
			"""
			INSERT INTO dbo.RiskLimits
			  (portfolio_id, security_id, max_order_price, max_position_size, max_daily_volume, is_active, effective_date)
			VALUES (@p, @s, @price, @pos, @daily, 1, SYSUTCDATETIME())
			""", c, t);

		cmd.Parameters.Add(IntParam("@p", portfolioId));
		cmd.Parameters.Add(IntParam("@s", securityId));
		cmd.Parameters.Add(NullableMoney("@price", price));
		cmd.Parameters.Add(NullableMoney("@pos", position));
		cmd.Parameters.Add(NullableMoney("@daily", daily));

		await cmd.ExecuteNonQueryAsync();
	}

	private static SqlParameter NullableMoney(string name, decimal? value)
		=> new(name, SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = (object)value ?? DBNull.Value };

	private static async Task<int> CountOrdersAsync(string connectionString, int portfolioId)
	{
		await using var connection = new SqlConnection(connectionString);
		await connection.OpenAsync();

		await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Orders WHERE portfolio_id = @p", connection);
		cmd.Parameters.Add(IntParam("@p", portfolioId));

		return (int)await cmd.ExecuteScalarAsync();
	}

	private static async Task<(string Side, decimal Qty, decimal? Price, string OrderType, string Status, string RejectReason, long? ExternalTransactionId)>
		ReadOrderRowAsync(string connectionString, long orderId)
	{
		await using var connection = new SqlConnection(connectionString);
		await connection.OpenAsync();

		await using var cmd = new SqlCommand(
			"SELECT side, qty, price, order_type, status, reject_reason, external_transaction_id FROM dbo.Orders WHERE order_id = @o",
			connection);
		cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.BigInt) { Value = orderId });

		await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow);

		if (!await reader.ReadAsync())
			throw new InvalidOperationException($"order_id {orderId} not found.");

		return (
			reader.GetString(0).Trim(),
			reader.GetDecimal(1),
			reader.IsDBNull(2) ? null : reader.GetDecimal(2),
			reader.GetString(3).Trim(),
			reader.GetString(4).Trim(),
			reader.IsDBNull(5) ? null : reader.GetString(5),
			reader.IsDBNull(6) ? null : reader.GetInt64(6));
	}

	private static async Task<int> CountOrderHistoryAsync(SqlConnection c, SqlTransaction t, long orderId)
	{
		await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.OrderStatusHistory WHERE order_id = @o", c, t);
		cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.BigInt) { Value = orderId });
		return (int)await cmd.ExecuteScalarAsync();
	}

	private static async Task ExecOrderUpdateAsync(SqlConnection c, SqlTransaction t, long orderId, string setClause)
	{
		await using var cmd = new SqlCommand($"UPDATE dbo.Orders SET {setClause} WHERE order_id = @o", c, t);
		cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.BigInt) { Value = orderId });
		await cmd.ExecuteNonQueryAsync();
	}

	private static async Task ExecAsync(SqlConnection c, SqlTransaction t, string sql)
	{
		await using var cmd = new SqlCommand(sql, c, t);
		await cmd.ExecuteNonQueryAsync();
	}

	private static async Task<int> ScalarIntAsync(SqlConnection c, SqlTransaction t, string sql)
	{
		await using var cmd = new SqlCommand(sql, c, t);
		var value = await cmd.ExecuteScalarAsync();
		return value is null or DBNull ? 0 : Convert.ToInt32(value);
	}
}
