namespace StockSharp.Tests;

using StockSharp.Algo.Risk;

/// <summary>
/// Characterization + parity tests for <see cref="PositionRecalculationService.Recalculate"/>, the pure
/// average-cost + realized-P&amp;L core ported from the SQL dbo.usp_RecalculatePositionOnTrade
/// (Database/002_StoredProcedures.sql:L262-L334) as part of the SQL-&gt;C# risk-consolidation refactor
/// (AAP 0.2.1, 0.4.1, 0.6.3, 0.6.4).
///
/// Every trace below is byte-verified against the SQL math and exercises both branches of the algorithm:
/// the same-sign weighted-average branch (opening / adding) and the opposite-sign realize-P&amp;L branch
/// (partial close, exact close, and flip). The double-count guard test proves the historical hazard
/// (LEGACY_LAYER.md:L74-L89) is understood and pins the single-apply contract.
///
/// The method under test is a deterministic, side-effect-free pure function, so these are DB-free unit
/// tests: no SQL Server, gateway, or connection is involved. Realized P&amp;L is asserted; unrealized P&amp;L
/// is intentionally NOT maintained by the service (it stays an end-of-day mark-to-market concern,
/// Database/001_Schema.sql:L193-L197) and is therefore never asserted here.
/// </summary>
[TestClass]
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
			positionExists: false,
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
			positionExists: true,
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
			positionExists: true,
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
			positionExists: true,
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
			positionExists: true,
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
			positionExists: true,
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
			positionExists: true,
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
			positionExists: true,
			side: Sides.Sell,
			tradeQty: 40m,
			tradePrice: 15m);

		result.Quantity.AssertEqual(60m);
		result.AveragePrice.AssertEqual(10m);
		result.RealizedPnl.AssertEqual(400m);
	}

	[TestMethod]
	public void DoubleCountGuard()
	{
		// The service itself is a deterministic pure function: ONE call == ONE apply. Idempotent
		// single-apply is guaranteed by the gateway invoking it EXACTLY ONCE per recorded trade
		// (RecordTradeAsync -> ApplyTradeAsync), which is why this test never asserts unrealized P&L.

		// Single apply of one trade against a flat position.
		var single = PositionRecalculationService.Recalculate(0m, 0m, 0m, false, Sides.Buy, 100m, 10m);
		single.Quantity.AssertEqual(100m);
		single.AveragePrice.AssertEqual(10m);
		single.RealizedPnl.AssertEqual(0m);

		// The OLD hazard (LEGACY_LAYER.md:L74-89): the AFTER-INSERT trigger AND the standalone
		// proc both recalculated the SAME trade, double-counting qty/avg/realized_pnl. If we
		// (wrongly) feed the first result back and re-apply the identical trade, the quantity
		// doubles - demonstrating why the refactor makes the C# service the SINGLE apply path
		// (RecordTradeAsync -> ApplyTradeAsync exactly once) and removed the trigger's calc driver.
		var doubleApplied = PositionRecalculationService.Recalculate(
			single.Quantity, single.AveragePrice, single.RealizedPnl, true, Sides.Buy, 100m, 10m);
		doubleApplied.Quantity.AssertEqual(200m);            // 100 doubled -> 200

		// Exactly-once yields 100; a spurious second apply yields 200. They MUST differ.
		(single.Quantity != doubleApplied.Quantity).AssertTrue();
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
			positionExists: false,
			side: Sides.Sell,
			tradeQty: 100m,
			tradePrice: 10m);

		result.Quantity.AssertEqual(-100m);
		result.AveragePrice.AssertEqual(10m);
		result.RealizedPnl.AssertEqual(0m);
	}
}
