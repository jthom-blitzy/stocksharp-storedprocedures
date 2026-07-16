namespace StockSharp.Tests;

using StockSharp.Algo.Risk;

// Characterization + parity tests for the pure, database-free core of the per-order pre-trade gate
// PreTradeRiskService.Evaluate. Each test pins one of the seven checks ported from the SQL
// dbo.usp_ValidatePreTradeRisk (Database/002_StoredProcedures.sql) to the DEMO seed values
// (Database/004_SeedData.sql) so every reject string matches the SQL CONVERT(VARCHAR, ...) output
// byte-for-byte. There is no SQL Server and no gateway here: Evaluate is intentionally pure, which
// is exactly what makes these unit tests fast and deterministic (AAP data-locality split, 0.6.7).
[TestClass]
public class PreTradeRiskServiceTests : BaseTestClass
{
	// Canonical DEMO seed (Database/004_SeedData.sql), portfolio-wide (SecurityId null). Every
	// threshold is populated so a given test can drive whichever single check it targets.
	private static RiskLimitSet SeedLimits() => new()
	{
		MaxOrderPrice = 500m,
		MaxOrderQty = 10000m,
		MaxOrderValue = 1000000m,
		MaxPositionSize = 100000m,
		MaxDailyVolume = 250000m,
		MaxOrderFreqCount = 5,
		MaxOrderFreqWindowSeconds = 60,
		MaxCommissionTotal = 5000m,
		CommissionRate = 0.0005m,
		PortfolioId = 1,
		IsActive = true,
		EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
	};

	// A limit set that enforces ONLY the resulting-position ceiling. Used to isolate the position
	// check (5) from the qty ceiling (2): with only MaxPositionSize set, checks 1-4 and 6-7 are
	// skipped, so large volumes exercise the signed post-fill projection without tripping the qty gate.
	private static RiskLimitSet PositionOnlyLimits() => new()
	{
		MaxPositionSize = 100000m,
		PortfolioId = 1,
		IsActive = true,
		EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
	};

	// ---- Task 1 : a fully compliant order is accepted -------------------------------------------

	[TestMethod]
	public void AcceptsCompliantOrder()
	{
		// Small compliant buy: price 100<500, qty 10<10000, value 1000<1M, freq 1<5, position 10<100000,
		// commission 0.5<5000, daily 10<250000 -> every one of the seven checks passes.
		var result = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, volume: 10m, price: 100m);

		result.IsValid.AssertTrue();
		result.RejectReason.AssertNull();
	}

	// ---- Task 2 : null limits short-circuit to accept -------------------------------------------

	[TestMethod]
	public void NullLimitsAccepts()
	{
		// A null limit set means "nothing configured" -> accept (after the side/qty preamble).
		var result = PreTradeRiskService.Evaluate(null, new PreTradeState(), Sides.Buy, 999999m, 999999m);

		result.IsValid.AssertTrue();
		result.RejectReason.AssertNull();
	}

	// ---- Task 3 : all-NULL ceilings => IsUnlimited => accept ------------------------------------

	[TestMethod]
	public void UnlimitedWhenAllNullAccepts()
	{
		// All seven ceilings null -> IsUnlimited -> short-circuit accept. Mirrors the SQL all-NULL
		// branch (usp_ValidatePreTradeRisk L122-127) and the NULL/0 = "not enforced" convention of
		// dbo.RiskLimits (Database/001_Schema.sql L80-83). CommissionRate is NOT a ceiling and does
		// not defeat IsUnlimited.
		var limits = new RiskLimitSet { PortfolioId = 1, CommissionRate = 0.0005m };

		limits.IsUnlimited.AssertTrue();

		var result = PreTradeRiskService.Evaluate(limits, new PreTradeState(), Sides.Buy, 999999m, 999999m);

		result.IsValid.AssertTrue();
		result.RejectReason.AssertNull();
	}

	// ---- Task 4 : non-positive quantity is rejected in the preamble -----------------------------

	[TestMethod]
	public void InvalidQtyRejected()
	{
		// volume <= 0 is rejected in preamble B, which runs BEFORE the unlimited short-circuit.
		var zero = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, 0m, 100m);

		zero.IsValid.AssertFalse();
		zero.RejectReason.AssertEqual("Invalid qty");

		var negative = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, -5m, 100m);

		negative.IsValid.AssertFalse();
		negative.RejectReason.AssertEqual("Invalid qty");
	}

	// ---- Task 5 : check 1 (order price ceiling) -------------------------------------------------

	[TestMethod]
	public void Check1_PriceCeiling()
	{
		// Reject at the exact >= boundary: price 500 meets the 500 ceiling.
		var reject = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, volume: 1m, price: 500m);

		reject.IsValid.AssertFalse();
		reject.RejectReason.AssertEqual("Order price 500.0000 meets/exceeds limit 500.0000");

		// Just below the ceiling accepts (checks 2-7 all pass with volume 1).
		var accept = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, volume: 1m, price: 499m);

		accept.IsValid.AssertTrue();
		accept.RejectReason.AssertNull();
	}

	// ---- Task 6 : check 2 (order quantity ceiling) ----------------------------------------------

	[TestMethod]
	public void Check2_QtyCeiling()
	{
		// price 1<500 so check 1 passes; qty 10000 meets the 10000 ceiling.
		var reject = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, volume: 10000m, price: 1m);

		reject.IsValid.AssertFalse();
		reject.RejectReason.AssertEqual("Order qty 10000.0000 meets/exceeds limit 10000.0000");

		// 9999 < 10000 -> accept.
		var accept = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, volume: 9999m, price: 1m);

		accept.IsValid.AssertTrue();
		accept.RejectReason.AssertNull();
	}

	// ---- Task 7 : check 3 (notional value ceiling) ----------------------------------------------

	[TestMethod]
	public void Check3_NotionalValue()
	{
		// price 400<500 and qty 2500<10000 pass; product 2500*400 = 1,000,000 meets the 1M ceiling.
		// The SQL product is a DECIMAL(37,8), so it renders with EIGHT fractional digits (F8) while
		// the limit stays DECIMAL(18,4) (F4).
		var reject = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, volume: 2500m, price: 400m);

		reject.IsValid.AssertFalse();
		reject.RejectReason.AssertEqual("Order value 1000000.00000000 meets/exceeds limit 1000000.0000");

		// 2499*400 = 999,600 < 1,000,000 -> accept.
		var accept = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, volume: 2499m, price: 400m);

		accept.IsValid.AssertTrue();
		accept.RejectReason.AssertNull();
	}

	// ---- Task 8 : check 4 (order frequency, rolling-count parity) -------------------------------

	[TestMethod]
	public void Check4_Frequency()
	{
		// Canonical rolling-count parity (AAP 0.6.1): projected = RecentOrderCount + 1; reject when
		// projected >= count. With 4 recent orders inside the window the candidate is the 5th, which
		// meets the limit of 5. The burst-across-window rolling behaviour itself is proven on the
		// RiskOrderFreqRule in RiskTests; here we pin the gate's arithmetic and its reject string.
		var reject = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState { RecentOrderCount = 4 }, Sides.Buy, 10m, 100m);

		reject.IsValid.AssertFalse();
		reject.RejectReason.AssertEqual("Order frequency 5 in 60s meets/exceeds limit 5");

		// 3 recent -> projected 4 < 5 -> accept.
		var accept = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState { RecentOrderCount = 3 }, Sides.Buy, 10m, 100m);

		accept.IsValid.AssertTrue();
		accept.RejectReason.AssertNull();
	}

	// ---- Task 9 : check 5 (resulting position, POST-FILL projection) ----------------------------

	[TestMethod]
	public void Check5_PositionPostFill()
	{
		// POST-FILL projection: the CURRENT position 99,000 is UNDER the 100,000 ceiling; it is the
		// hypothetical post-fill position (99,000 + 1,000 = 100,000) that trips. This is precisely
		// what distinguishes the pre-trade gate from the C# RiskPositionSizeRule, which inspects the
		// current position instead (AAP 0.6.2 "different-by-design").
		var buyReject = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState { CurrentPositionQty = 99000m }, Sides.Buy, 1000m, 100m);

		buyReject.IsValid.AssertFalse();
		buyReject.RejectReason.AssertEqual("Resulting position 100000.0000 meets/exceeds limit 100000.0000");

		// A sell applies a signed delta of -volume: -99,000 - 1,000 = -100,000; |.| = 100,000 -> reject.
		// The negative resulting position renders with a leading minus sign.
		var sellReject = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState { CurrentPositionQty = -99000m }, Sides.Sell, 1000m, 100m);

		sellReject.IsValid.AssertFalse();
		sellReject.RejectReason.AssertEqual("Resulting position -100000.0000 meets/exceeds limit 100000.0000");

		// 99,000 + 999 = 99,999 < 100,000 -> accept.
		var accept = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState { CurrentPositionQty = 99000m }, Sides.Buy, 999m, 100m);

		accept.IsValid.AssertTrue();
		accept.RejectReason.AssertNull();
	}

	// ---- Task 10 : check 6 (commission estimate, PRE-fill) --------------------------------------

	[TestMethod]
	public void Check6_CommissionEstimate()
	{
		// PRE-fill estimate: existingNotional*rate + qty*price*rate
		// = 10,000,000*0.0005 + 10*100*0.0005 = 5000 + 0.5 = 5000.5 >= 5000. Checks 1-5 pass first
		// (price 100<500, qty 10<10000, value 1000<1M, freq 1<5, position 10<100000).
		var reject = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState { ExistingTradesNotional = 10000000m }, Sides.Buy, 10m, 100m);

		reject.IsValid.AssertFalse();
		reject.RejectReason.AssertEqual("Estimated cumulative commission 5000.5000 meets/exceeds limit 5000.0000");

		// Market order (price null): estPrice falls back to LastTradePrice(100); the price-dependent
		// checks 1 and 3 are skipped; the commission estimate is identical -> reject with the same reason.
		var marketReject = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState { ExistingTradesNotional = 10000000m, LastTradePrice = 100m }, Sides.Buy, 10m, price: null);

		marketReject.IsValid.AssertFalse();
		marketReject.RejectReason.AssertEqual("Estimated cumulative commission 5000.5000 meets/exceeds limit 5000.0000");

		// 9,000,000*0.0005 + 10*100*0.0005 = 4500 + 0.5 = 4500.5 < 5000 -> accept.
		var accept = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState { ExistingTradesNotional = 9000000m }, Sides.Buy, 10m, 100m);

		accept.IsValid.AssertTrue();
		accept.RejectReason.AssertNull();
	}

	// ---- Task 11 : check 7 (daily traded volume) ------------------------------------------------

	[TestMethod]
	public void Check7_DailyVolume()
	{
		// today + volume = 249,000 + 1,000 = 250,000 >= 250,000 -> reject (checks 1-6 all pass first).
		var reject = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState { TodayAcceptedVolume = 249000m }, Sides.Buy, 1000m, 100m);

		reject.IsValid.AssertFalse();
		reject.RejectReason.AssertEqual("Daily volume 250000.0000 meets/exceeds limit 250000.0000");

		// 248,999 + 1,000 = 249,999 < 250,000 -> accept.
		var accept = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState { TodayAcceptedVolume = 248999m }, Sides.Buy, 1000m, 100m);

		accept.IsValid.AssertTrue();
		accept.RejectReason.AssertNull();
	}

	// ---- Task 12 : first-fail-wins check ordering -----------------------------------------------

	[TestMethod]
	public void FirstFailWinsOrdering()
	{
		// This order violates price (600>=500), qty (20000>=10000) and value simultaneously. The
		// service must return the FIRST failing check in SQL order (price -> qty -> value -> frequency
		// -> position -> commission -> daily), i.e. the PRICE reason, proving the ordering is preserved.
		var result = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, volume: 20000m, price: 600m);

		result.IsValid.AssertFalse();
		result.RejectReason.AssertEqual("Order price 600.0000 meets/exceeds limit 500.0000");
	}

	// ---- Task 13 : RiskLimitSet.SelectMostSpecific precedence -----------------------------------

	[TestMethod]
	public void SelectMostSpecific_Precedence()
	{
		// Distinguishing tag: MaxOrderPrice. A = portfolio+security (rank 0), B = portfolio-only
		// (rank 1), C = security-only (rank 2); all active and matching portfolio 1 / security 2.
		var a = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 1m, EffectiveDate = new DateTime(2024, 1, 1) };
		var b = new RiskLimitSet { PortfolioId = 1, SecurityId = null, IsActive = true, MaxOrderPrice = 2m, EffectiveDate = new DateTime(2024, 1, 1) };
		var c = new RiskLimitSet { PortfolioId = null, SecurityId = 2, IsActive = true, MaxOrderPrice = 3m, EffectiveDate = new DateTime(2024, 1, 1) };

		// portfolio+security outranks portfolio-only and security-only.
		var mostSpecific = RiskLimitSet.SelectMostSpecific(new[] { c, b, a }, 1, 2);
		mostSpecific.AssertNotNull();
		mostSpecific.MaxOrderPrice.Value.AssertEqual(1m);

		// Without the portfolio+security row, the portfolio-only row wins.
		var portfolioOnly = RiskLimitSet.SelectMostSpecific(new[] { c, b }, 1, 2);
		portfolioOnly.AssertNotNull();
		portfolioOnly.MaxOrderPrice.Value.AssertEqual(2m);

		// Only the security-only row remains.
		var securityOnly = RiskLimitSet.SelectMostSpecific(new[] { c }, 1, 2);
		securityOnly.AssertNotNull();
		securityOnly.MaxOrderPrice.Value.AssertEqual(3m);

		// Tie on specificity -> the most recent EffectiveDate wins, independent of input order.
		var early = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 10m, EffectiveDate = new DateTime(2024, 1, 1) };
		var late = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 20m, EffectiveDate = new DateTime(2024, 6, 1) };
		RiskLimitSet.SelectMostSpecific(new[] { early, late }, 1, 2).MaxOrderPrice.Value.AssertEqual(20m);
		RiskLimitSet.SelectMostSpecific(new[] { late, early }, 1, 2).MaxOrderPrice.Value.AssertEqual(20m);

		// An inactive most-specific row is filtered out; the next active match is returned.
		var inactiveMostSpecific = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = false, MaxOrderPrice = 1m, EffectiveDate = new DateTime(2024, 1, 1) };
		var activePortfolio = new RiskLimitSet { PortfolioId = 1, SecurityId = null, IsActive = true, MaxOrderPrice = 2m, EffectiveDate = new DateTime(2024, 1, 1) };
		var afterFilter = RiskLimitSet.SelectMostSpecific(new[] { inactiveMostSpecific, activePortfolio }, 1, 2);
		afterFilter.AssertNotNull();
		afterFilter.MaxOrderPrice.Value.AssertEqual(2m);

		// No candidates at all -> null.
		RiskLimitSet.SelectMostSpecific(new List<RiskLimitSet>(), 1, 2).AssertNull();

		// Candidates scoped to unrelated ids never match -> null.
		var otherScope = new RiskLimitSet { PortfolioId = 99, SecurityId = 99, IsActive = true, MaxOrderPrice = 5m, EffectiveDate = new DateTime(2024, 1, 1) };
		RiskLimitSet.SelectMostSpecific(new[] { otherScope }, 1, 2).AssertNull();
	}

	// ---- Task 14 : Buy/Sell signed-delta handling in the position check -------------------------

	[TestMethod]
	public void SideBuySellHandling()
	{
		// Isolate check 5 with a position-only limit set so the large volumes below do not trip the
		// qty ceiling. Buy adds +volume, Sell adds -volume (mirrors the SQL
		// "CASE WHEN @side='B' THEN @qty ELSE -@qty END" signed-quantity derivation).

		// From a flat position both directions stay within +/-100,000 -> accept.
		var buyFlat = PreTradeRiskService.Evaluate(PositionOnlyLimits(), new PreTradeState { CurrentPositionQty = 0m }, Sides.Buy, 50000m, 1m);
		buyFlat.IsValid.AssertTrue();
		buyFlat.RejectReason.AssertNull();

		var sellFlat = PreTradeRiskService.Evaluate(PositionOnlyLimits(), new PreTradeState { CurrentPositionQty = 0m }, Sides.Sell, 50000m, 1m);
		sellFlat.IsValid.AssertTrue();
		sellFlat.RejectReason.AssertNull();

		// From +60,000: a buy projects to +110,000 (reject); a sell projects to +10,000 (accept).
		var buyOver = PreTradeRiskService.Evaluate(PositionOnlyLimits(), new PreTradeState { CurrentPositionQty = 60000m }, Sides.Buy, 50000m, 1m);
		buyOver.IsValid.AssertFalse();
		buyOver.RejectReason.AssertEqual("Resulting position 110000.0000 meets/exceeds limit 100000.0000");

		var sellUnder = PreTradeRiskService.Evaluate(PositionOnlyLimits(), new PreTradeState { CurrentPositionQty = 60000m }, Sides.Sell, 50000m, 1m);
		sellUnder.IsValid.AssertTrue();
		sellUnder.RejectReason.AssertNull();
	}
}
