namespace StockSharp.Tests;

using System.Data;

using Microsoft.Data.SqlClient;

using StockSharp.Algo.Risk;
using StockSharp.Algo.Storages.Sql;

// This suite exercises the per-order pre-trade gate PreTradeRiskService at two layers:
//
//  (1) Pure decision-core UNIT tests of the database-free PreTradeRiskService.Evaluate. Each test
//      pins one of the seven checks ported from dbo.usp_ValidatePreTradeRisk
//      (Database/002_StoredProcedures.sql) to the DEMO seed values (Database/004_SeedData.sql) so
//      every reject string matches the SQL CONVERT(VARCHAR, ...) output byte-for-byte. Evaluate is
//      intentionally pure, which is what makes these fast and deterministic (AAP data-locality split,
//      0.6.7). These are unit tests of the decision logic - NOT live characterization.
//
//  (2) Live INTEGRATION + characterization/parity tests (the "Live_*" methods) that exercise the real
//      ValidateAsync query path against the StockSharpLegacy SQL Server: they load dbo.RiskLimits and
//      query dbo.Orders/dbo.Positions/dbo.Trades through the actual ADO.NET commands and parameter
//      metadata, then assert the outcome equals the legacy oracle captured (via the original
//      dbo.usp_ValidatePreTradeRisk) BEFORE the SQL layer was reduced to pure CRUD. Every live test
//      runs inside a transaction that is ROLLED BACK, so it uses fresh, collision-free scope rows and
//      leaves the database pristine. The whole live layer is gated on database reachability: when the
//      Docker SQL Server is absent the tests report Inconclusive rather than fail (AAP 0.6.7).
[TestClass]
[DoNotParallelize] // The Live_* tests open real StockSharpLegacy transactions that hold locks on the
                   // shared Portfolios/Securities/RiskLimits/Orders tables; running them concurrently
                   // would deadlock. This follows the repo convention (see StorageNotParallelizeTests).
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

	// ---- MA-16 : a non-null ZERO ceiling is "not enforced" (intentional divergence from legacy) --

	[TestMethod]
	public void ZeroCeilingNotEnforcedIntentionalDivergence()
	{
		// MA-16 characterization: this is the SECOND intentional, documented behaviour change of the
		// refactor (after the order-frequency tightening, AAP §0.6.1). The legacy proc
		// dbo.usp_ValidatePreTradeRisk guarded each check only with "IF @max_x IS NOT NULL", so a stored
		// max_order_price = 0 made "price >= 0" reject EVERY order (an unusable block-all state). The
		// canonical model adopts the NULL/0 = "not enforced" convention AAP §0.3.1 mandates, so a zero
		// ceiling disables that single check instead. This test PINS the new behaviour so the divergence
		// is proven intentional rather than a regression.
		var zeroPrice = new RiskLimitSet { PortfolioId = 1, MaxOrderPrice = 0m };

		RiskLimitSet.IsCeilingEnforced(zeroPrice.MaxOrderPrice).AssertFalse();
		((object)zeroPrice.EffectiveMaxOrderPrice).AssertNull();
		zeroPrice.IsUnlimited.AssertTrue(); // the only populated ceiling is a disabled zero

		// An order that the literal legacy proc would have rejected (any price >= 0) is now accepted.
		var accepted = PreTradeRiskService.Evaluate(zeroPrice, new PreTradeState(), Sides.Buy, volume: 10m, price: 999999m);
		accepted.IsValid.AssertTrue();
		accepted.RejectReason.AssertNull();

		// Per-check: a zero PRICE ceiling disables ONLY the price check; a co-populated qty ceiling of 5
		// still trips for a volume of 10, proving the "not enforced" semantics are scoped to that ceiling.
		var zeroPriceWithQty = new RiskLimitSet { PortfolioId = 1, MaxOrderPrice = 0m, MaxOrderQty = 5m };
		var rejected = PreTradeRiskService.Evaluate(zeroPriceWithQty, new PreTradeState(), Sides.Buy, volume: 10m, price: 999999m);
		rejected.IsValid.AssertFalse();
		rejected.RejectReason.AssertEqual("Order qty 10.0000 meets/exceeds limit 5.0000");
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

	// ---- Task 4b : DECIMAL(18,4) parity - a sub-tick quantity rounds to the persisted scale (C02) ----
	// The legacy gate's @qty parameter is DECIMAL(18,4); a value below half a tick is rounded to 0.0000
	// at the parameter boundary and rejected as "Invalid qty". The C# gate must coerce identically before
	// its zero-guard rather than evaluating the raw high-precision decimal. Both boundary values below were
	// captured directly from the live dbo.usp_ValidatePreTradeRisk proc (round-half-away-from-zero):
	//   qty = 0.00004 -> rounds to 0.0000 -> is_valid = 0, reject_reason = "Invalid qty"
	//   qty = 0.00005 -> rounds to 0.0001 -> is_valid = 1, reject_reason = NULL (accepted)
	[TestMethod]
	public void SubTickQtyMatchesDecimalScale()
	{
		// Below half a tick: rounds down to zero and is rejected, exactly like the SQL DECIMAL(18,4) coercion.
		var rejected = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, 0.00004m, 150m);

		rejected.IsValid.AssertFalse();
		rejected.RejectReason.AssertEqual("Invalid qty");

		// At/above half a tick: rounds up to the smallest representable quantity (0.0001) and survives the
		// preamble; with a compliant price every downstream check passes, so the order is accepted.
		var accepted = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, 0.00005m, 150m);

		accepted.IsValid.AssertTrue();
		accepted.RejectReason.AssertNull();
	}

	// ---- CR-13 : a Limit price that rounds to <= 0 at DECIMAL(18,4) is rejected ------------------
	// The gateway coerces the price to the persisted scale BEFORE the positive-price check, and the gate's
	// preamble does the same, so a sub-tick 0.00004 (which stores as 0.0000) can never be accepted just
	// because a 0.0000 price never trips the "price >= limit" ceiling comparison.
	[TestMethod]
	public void SubScalePriceRejected()
	{
		// Below half a tick: rounds to 0.0000 and is rejected as "Invalid price" in the preamble.
		var subScale = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, volume: 10m, price: 0.00004m);
		subScale.IsValid.AssertFalse();
		subScale.RejectReason.AssertEqual("Invalid price");

		// At/above half a tick: rounds up to 0.0001 (> 0) and survives; price 0.0001 < 500 -> accept.
		var justAbove = PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, volume: 10m, price: 0.00005m);
		justAbove.IsValid.AssertTrue();
		justAbove.RejectReason.AssertNull();

		// An explicit zero or negative price is likewise rejected in the preamble (not silently coerced).
		PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, 10m, 0m).RejectReason.AssertEqual("Invalid price");
		PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, 10m, -5m).RejectReason.AssertEqual("Invalid price");

		// A market order (null price) is unaffected by the price preamble and passes on the seed limits.
		PreTradeRiskService.Evaluate(SeedLimits(), new PreTradeState(), Sides.Buy, volume: 10m, price: null).IsValid.AssertTrue();
	}

	// ---- CR-7 / CR-12 : the gate FAILS CLOSED on a malformed configuration ----------------------
	// A negative ceiling, a negative commission rate, or a half-specified frequency pair is a configuration
	// error the gate must never act on: Evaluate throws via RiskLimitSet.Validate rather than silently
	// disabling the check (the old fail-open behaviour). This is the SAME validator RiskManager.CreateRules
	// uses, so the per-order gate and the portfolio circuit-breaker factory reject invalid config identically.
	[TestMethod]
	public void EvaluateFailsClosedOnMalformedConfig()
	{
		var state = new PreTradeState();

		ThrowsExactly<ArgumentException>(() => PreTradeRiskService.Evaluate(new RiskLimitSet { MaxOrderPrice = -1m }, state, Sides.Buy, 10m, 100m));
		ThrowsExactly<ArgumentException>(() => PreTradeRiskService.Evaluate(new RiskLimitSet { MaxCommissionTotal = 5000m, CommissionRate = -0.0001m }, state, Sides.Buy, 10m, 100m));
		ThrowsExactly<ArgumentException>(() => PreTradeRiskService.Evaluate(new RiskLimitSet { MaxOrderFreqCount = 5 }, state, Sides.Buy, 10m, 100m));
		ThrowsExactly<ArgumentException>(() => PreTradeRiskService.Evaluate(new RiskLimitSet { MaxOrderFreqWindowSeconds = 60 }, state, Sides.Buy, 10m, 100m));

		// A malformed configuration fails closed even when the ORDER itself is also invalid: the
		// configuration error is surfaced (thrown) rather than masked by a business reject.
		ThrowsExactly<ArgumentException>(() => PreTradeRiskService.Evaluate(new RiskLimitSet { MaxOrderPrice = -1m }, state, Sides.Buy, -5m, 100m));

		// A valid null/0-only (unlimited) or positive configuration does NOT throw.
		PreTradeRiskService.Evaluate(new RiskLimitSet { PortfolioId = 1, MaxOrderPrice = 0m }, state, Sides.Buy, 10m, 100m).IsValid.AssertTrue();
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

	// ---- DB-P5-01 : equal-scope, equal-effective-date tie is deterministic AND never less strict ----

	[TestMethod]
	public void SelectMostSpecific_EqualEffectiveDateTieIsDeterministicAndStrict()
	{
		var date = new DateTime(2024, 1, 1);

		// Exact reproduction of finding DB-P5-01: two equally-specific rows with the SAME effective_date,
		// one max_order_price=111 and one =222. Before the fix, insertion order A->B selected 111 and
		// B->A selected 222 (nondeterministic). Now BOTH orders must select the stricter 111.
		var a = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 111m, EffectiveDate = date };
		var b = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 222m, EffectiveDate = date };

		var ab = RiskLimitSet.SelectMostSpecific(new[] { a, b }, 1, 2);
		var ba = RiskLimitSet.SelectMostSpecific(new[] { b, a }, 1, 2);

		ab.AssertNotNull();
		ba.AssertNotNull();
		ab.MaxOrderPrice.Value.AssertEqual(111m);   // deterministic
		ba.MaxOrderPrice.Value.AssertEqual(111m);   // independent of input order
		ab.MaxOrderPrice.Value.AssertEqual(ba.MaxOrderPrice.Value); // and never the looser 222

		// Multi-column incomparable tie: row X is stricter on price, row Y is stricter on qty. Neither is
		// uniformly strictest, so the composed set must take the strictest of EACH column (never looser).
		var x = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 100m, MaxOrderQty = 5000m, EffectiveDate = date };
		var y = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 200m, MaxOrderQty = 1000m, EffectiveDate = date };
		foreach (var seq in new[] { new[] { x, y }, new[] { y, x } })
		{
			var composed = RiskLimitSet.SelectMostSpecific(seq, 1, 2);
			composed.AssertNotNull();
			composed.MaxOrderPrice.Value.AssertEqual(100m);  // strictest (min) price
			composed.MaxOrderQty.Value.AssertEqual(1000m);   // strictest (min) qty
		}

		// A null/unenforced ceiling on one tied row is ignored: the enforced value from the other row wins
		// (a tie must never DISABLE a limit that at least one definition enforces).
		var withPrice = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 100m, EffectiveDate = date };
		var withoutPrice = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = null, MaxOrderQty = 1000m, EffectiveDate = date };
		var merged = RiskLimitSet.SelectMostSpecific(new[] { withoutPrice, withPrice }, 1, 2);
		merged.MaxOrderPrice.Value.AssertEqual(100m);
		merged.MaxOrderQty.Value.AssertEqual(1000m);

		// Frequency tie: strictest is the smallest count paired with the largest window, so (5/60s) tied
		// with (3/120s) composes to (3 orders / 120s) - the most restrictive rate. Carried as a coherent pair.
		var freqLoose = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderFreqCount = 5, MaxOrderFreqWindowSeconds = 60, EffectiveDate = date };
		var freqTight = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderFreqCount = 3, MaxOrderFreqWindowSeconds = 120, EffectiveDate = date };
		var freq = RiskLimitSet.SelectMostSpecific(new[] { freqLoose, freqTight }, 1, 2);
		freq.MaxOrderFreqCount.Value.AssertEqual(3);
		freq.MaxOrderFreqWindowSeconds.Value.AssertEqual(120);
		freq.IsFrequencyEnforced.AssertTrue();

		// Commission tie: a smaller total ceiling AND a higher rate are both stricter, so they compose to
		// (min total, max rate). The composed row must still validate as a real, well-formed configuration.
		var commA = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxCommissionTotal = 1000m, CommissionRate = 0.001m, EffectiveDate = date };
		var commB = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxCommissionTotal = 2000m, CommissionRate = 0.002m, EffectiveDate = date };
		var comm = RiskLimitSet.SelectMostSpecific(new[] { commA, commB }, 1, 2);
		comm.MaxCommissionTotal.Value.AssertEqual(1000m);
		comm.CommissionRate.AssertEqual(0.002m);
		comm.EffectiveDate.AssertEqual(date);
		comm.IsActive.AssertTrue();
		comm.Validate(); // composed set is well-formed and never throws

		// Three-way tie is still order-independent: every permutation selects the same strictest price.
		var p1 = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 300m, EffectiveDate = date };
		var p2 = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 150m, EffectiveDate = date };
		var p3 = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 250m, EffectiveDate = date };
		RiskLimitSet.SelectMostSpecific(new[] { p1, p2, p3 }, 1, 2).MaxOrderPrice.Value.AssertEqual(150m);
		RiskLimitSet.SelectMostSpecific(new[] { p3, p1, p2 }, 1, 2).MaxOrderPrice.Value.AssertEqual(150m);
		RiskLimitSet.SelectMostSpecific(new[] { p2, p3, p1 }, 1, 2).MaxOrderPrice.Value.AssertEqual(150m);

		// A tie must NOT override a strictly-more-specific single row: a portfolio+security row still
		// outranks any portfolio-only tie, so composition only ever runs WITHIN the winning tier.
		var specific = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 400m, EffectiveDate = date };
		var portfolioTieA = new RiskLimitSet { PortfolioId = 1, SecurityId = null, IsActive = true, MaxOrderPrice = 10m, EffectiveDate = date };
		var portfolioTieB = new RiskLimitSet { PortfolioId = 1, SecurityId = null, IsActive = true, MaxOrderPrice = 20m, EffectiveDate = date };
		RiskLimitSet.SelectMostSpecific(new[] { portfolioTieA, specific, portfolioTieB }, 1, 2).MaxOrderPrice.Value.AssertEqual(400m);
	}

	// ---- CR-32 : SelectMostSpecific excludes future-effective rows when a UTC cutoff is supplied --

	[TestMethod]
	public void SelectMostSpecific_ExcludesFutureEffective()
	{
		var now = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);

		// Two rows of the SAME (portfolio+security) specificity. A stricter row is effective in the past; a
		// looser row is scheduled to take effect TOMORROW. Without a cutoff the future row (more recent
		// EffectiveDate) wins the tie-break; WITH the cutoff it is excluded so the current stricter row wins.
		var current = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 500m, EffectiveDate = now.AddDays(-30) };
		var future = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 999999m, EffectiveDate = now.AddDays(1) };

		// No cutoff (legacy behaviour, callers that already time-filter): the future, most-recent row wins.
		RiskLimitSet.SelectMostSpecific(new[] { current, future }, 1, 2).MaxOrderPrice.Value.AssertEqual(999999m);

		// With the cutoff: the future row is excluded, the current stricter row is selected.
		var selected = RiskLimitSet.SelectMostSpecific(new[] { current, future }, 1, 2, now);
		selected.AssertNotNull();
		selected.MaxOrderPrice.Value.AssertEqual(500m);

		// A row effective exactly AT the cutoff instant is still eligible (<= is inclusive).
		var atCutoff = new RiskLimitSet { PortfolioId = 1, SecurityId = 2, IsActive = true, MaxOrderPrice = 250m, EffectiveDate = now };
		RiskLimitSet.SelectMostSpecific(new[] { current, atCutoff }, 1, 2, now).MaxOrderPrice.Value.AssertEqual(250m);

		// When every candidate is in the future, nothing applies as of the cutoff.
		RiskLimitSet.SelectMostSpecific(new[] { future }, 1, 2, now).AssertNull();
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

	// =====================================================================================
	// Layer 2 - live integration + characterization/parity against the StockSharpLegacy DB.
	// Every "Live_*" test below runs inside a rolled-back transaction over a fresh, unique
	// (portfolio, security) scope, so it exercises the real ValidateAsync ADO.NET query path
	// (parameter metadata, nullable scalars, the RiskLimits load and the Orders/Positions/Trades
	// reads) yet leaves the shared database exactly as it found it. Reject strings are asserted
	// against the legacy oracle captured from dbo.usp_ValidatePreTradeRisk BEFORE the SQL layer
	// was reduced to CRUD, proving the C# gate reproduces the retired proc byte-for-byte.
	// =====================================================================================

	// Opens a connection to the StockSharpLegacy database, or returns null when it is unreachable
	// so the live layer degrades to Inconclusive rather than a hard failure on a DB-less machine.
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

	// A DECIMAL(18,4) parameter carrying DBNull for a C# null (the "not enforced" convention).
	private static SqlParameter Money(string name, decimal? value)
		=> new(name, SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = (object)value ?? DBNull.Value };

	// A nullable INT parameter carrying DBNull for a C# null.
	private static SqlParameter NInt(string name, int? value)
		=> new(name, SqlDbType.Int) { Value = (object)value ?? DBNull.Value };

	// Inserts a fresh, collision-free portfolio + security (IDENTITY ids returned via OUTPUT) so no
	// pre-existing seed/demo rows can pollute the state-dependent checks.
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

	// Inserts one RiskLimits row for the scope. A null argument persists SQL NULL, i.e. "not enforced".
	private static async Task InsertLimitsAsync(SqlConnection c, SqlTransaction t, int portfolioId, int securityId, CancellationToken ct,
		decimal? price = null, decimal? qty = null, decimal? value = null, decimal? position = null,
		decimal? daily = null, int? freqCount = null, int? freqWindow = null, decimal? commission = null, decimal rate = 0.0005m,
		DateTime? effectiveDate = null)
	{
		// effective_date defaults to one second in the PAST (via the DB clock) so a seeded row is
		// unambiguously already in force for the CR-32 "effective_date <= SYSUTCDATETIME()" cutoff. Using a
		// bare SYSUTCDATETIME() here would store the value rounded up to the DATETIME2(3) column scale, which
		// can land a fraction of a millisecond AFTER an immediately-following SELECT's clock and spuriously
		// exclude the just-inserted row - a same-transaction insert-then-query artifact that never occurs in
		// production (rows are seeded long before orders arrive). Pass an explicit value to seed a genuinely
		// past- or future-effective row for the cutoff tests.
		await using var cmd = new SqlCommand(
			"""
			INSERT INTO dbo.RiskLimits
			  (portfolio_id, security_id, max_order_price, max_order_qty, max_order_value, max_position_size,
			   max_daily_volume, max_order_freq_count, max_order_freq_window_sec, max_commission_total,
			   commission_rate, is_active, effective_date)
			VALUES (@p, @s, @price, @qty, @value, @pos, @daily, @fc, @fw, @comm, @rate, 1, ISNULL(@effective, DATEADD(SECOND, -1, SYSUTCDATETIME())))
			""", c, t);

		cmd.Parameters.Add(NInt("@p", portfolioId));
		cmd.Parameters.Add(NInt("@s", securityId));
		cmd.Parameters.Add(Money("@price", price));
		cmd.Parameters.Add(Money("@qty", qty));
		cmd.Parameters.Add(Money("@value", value));
		cmd.Parameters.Add(Money("@pos", position));
		cmd.Parameters.Add(Money("@daily", daily));
		cmd.Parameters.Add(NInt("@fc", freqCount));
		cmd.Parameters.Add(NInt("@fw", freqWindow));
		cmd.Parameters.Add(Money("@comm", commission));
		cmd.Parameters.Add(new SqlParameter("@rate", SqlDbType.Decimal) { Precision = 9, Scale = 6, Value = rate });
		cmd.Parameters.Add(new SqlParameter("@effective", SqlDbType.DateTime2) { Value = (object)effectiveDate ?? DBNull.Value });

		await cmd.ExecuteNonQueryAsync(ct);
	}

	// Inserts an order and returns its BIGINT id. When submitted is null the DB clock stamps the row
	// (so freq-window tests are measured against SYSUTCDATETIME, immune to host/container clock skew).
	private static async Task<long> InsertOrderAsync(SqlConnection c, SqlTransaction t, int portfolioId, int securityId,
		char side, decimal qty, decimal? price, string status, CancellationToken ct, DateTime? submitted = null)
	{
		await using var cmd = new SqlCommand(
			"""
			INSERT INTO dbo.Orders (portfolio_id, security_id, side, qty, price, order_type, status, submitted_date)
			OUTPUT INSERTED.order_id
			VALUES (@p, @s, @side, @qty, @price, 'LIMIT', @status, ISNULL(@submitted, SYSUTCDATETIME()))
			""", c, t);

		cmd.Parameters.Add(NInt("@p", portfolioId));
		cmd.Parameters.Add(NInt("@s", securityId));
		cmd.Parameters.Add(new SqlParameter("@side", SqlDbType.Char, 1) { Value = side.ToString() });
		cmd.Parameters.Add(Money("@qty", qty));
		cmd.Parameters.Add(Money("@price", price));
		cmd.Parameters.Add(new SqlParameter("@status", SqlDbType.VarChar, 12) { Value = status });
		cmd.Parameters.Add(new SqlParameter("@submitted", SqlDbType.DateTime2) { Value = (object)submitted ?? DBNull.Value });

		return (long)await cmd.ExecuteScalarAsync(ct);
	}

	// Inserts a trade against an order.
	private static async Task InsertTradeAsync(SqlConnection c, SqlTransaction t, long orderId, decimal qty, decimal price, CancellationToken ct)
	{
		await using var cmd = new SqlCommand("INSERT INTO dbo.Trades (order_id, qty, price) VALUES (@o, @qty, @price)", c, t);
		cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.BigInt) { Value = orderId });
		cmd.Parameters.Add(Money("@qty", qty));
		cmd.Parameters.Add(Money("@price", price));
		await cmd.ExecuteNonQueryAsync(ct);
	}

	// Inserts a Positions row (avg_price/pnl default to 0) for the scope.
	private static async Task InsertPositionAsync(SqlConnection c, SqlTransaction t, int portfolioId, int securityId, decimal qty, CancellationToken ct)
	{
		await using var cmd = new SqlCommand("INSERT INTO dbo.Positions (portfolio_id, security_id, qty) VALUES (@p, @s, @qty)", c, t);
		cmd.Parameters.Add(NInt("@p", portfolioId));
		cmd.Parameters.Add(NInt("@s", securityId));
		cmd.Parameters.Add(Money("@qty", qty));
		await cmd.ExecuteNonQueryAsync(ct);
	}

	// Runs the given body against a fresh scope inside a transaction that is always rolled back.
	private async Task RunWithFreshScopeAsync(Func<PreTradeRiskService, SqlConnection, SqlTransaction, int, int, Task> body)
	{
		await using var connection = await TryOpenLegacyAsync();

		if (connection is null)
		{
			Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live pre-trade integration test.");
			return;
		}

		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

		try
		{
			var service = new PreTradeRiskService(SqlLegacyConnection.Resolve());
			var (portfolioId, securityId) = await InsertScopeAsync(connection, transaction, default);
			await body(service, connection, transaction, portfolioId, securityId);
		}
		finally
		{
			await transaction.RollbackAsync();
		}
	}

	[TestMethod]
	public Task Live_AcceptsCompliantOrder()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default,
				price: 500m, qty: 10000m, value: 1000000m, position: 100000m, daily: 250000m,
				freqCount: 5, freqWindow: 60, commission: 5000m);

			var r = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 10m, 100m, OrderTypes.Limit);
			r.IsValid.AssertTrue();
			r.RejectReason.AssertNull();
		});

	// ---- DB-P5-01 live regression : an equal-scope, equal-effective-date tie loads deterministically ----
	// Reproduces the finding against the real RiskLimits table + LoadLimitsAsync query path: two active
	// rows for the SAME (portfolio, security) scope with the SAME effective_date, differing only in
	// max_order_price (111 vs 222). Whichever physical order the rows are inserted in, the loaded canonical
	// limit must be the stricter 111 - never the looser 222, and never order-dependent.
	[TestMethod]
	public Task Live_EqualEffectiveDateTieLoadsStrictAndDeterministic()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			var shared = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); // safely in force (<= now)

			// Insert order A(111) -> B(222) into the first scope.
			await InsertLimitsAsync(c, t, pid, sid, default, price: 111m, effectiveDate: shared);
			await InsertLimitsAsync(c, t, pid, sid, default, price: 222m, effectiveDate: shared);

			var ab = await svc.LoadLimitsAsync(c, t, pid, sid);
			ab.AssertNotNull();
			ab.MaxOrderPrice.Value.AssertEqual(111m);

			// Fresh scope, reversed physical insert order B(222) -> A(111): must still load 111.
			var (pid2, sid2) = await InsertScopeAsync(c, t, default);
			await InsertLimitsAsync(c, t, pid2, sid2, default, price: 222m, effectiveDate: shared);
			await InsertLimitsAsync(c, t, pid2, sid2, default, price: 111m, effectiveDate: shared);

			var ba = await svc.LoadLimitsAsync(c, t, pid2, sid2);
			ba.AssertNotNull();
			ba.MaxOrderPrice.Value.AssertEqual(111m);

			// Both insertion orders resolve identically to the stricter limit.
			ab.MaxOrderPrice.Value.AssertEqual(ba.MaxOrderPrice.Value);
		});

	[TestMethod]
	public Task Live_PriceCeilingRejects()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, price: 500m);

			var r = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 1m, 500m, OrderTypes.Limit);
			r.IsValid.AssertFalse();
			r.RejectReason.AssertEqual("Order price 500.0000 meets/exceeds limit 500.0000");
		});

	[TestMethod]
	public Task Live_QtyCeilingRejects()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, qty: 10000m);

			var r = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 10000m, 10m, OrderTypes.Limit);
			r.IsValid.AssertFalse();
			r.RejectReason.AssertEqual("Order qty 10000.0000 meets/exceeds limit 10000.0000");
		});

	[TestMethod]
	public Task Live_NotionalValueRejects()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, value: 1000000m);

			// 5000 * 200 = 1,000,000; the SQL product renders with EIGHT fractional digits (DECIMAL(37,8)).
			var r = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 5000m, 200m, OrderTypes.Limit);
			r.IsValid.AssertFalse();
			r.RejectReason.AssertEqual("Order value 1000000.00000000 meets/exceeds limit 1000000.0000");
		});

	[TestMethod]
	public Task Live_SubTickQtyMatchesPersistedScale()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, price: 500m);

			// C02 parity through the DB path: 0.00004 rounds to DECIMAL(18,4) 0.0000 -> "Invalid qty".
			var rejected = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 0.00004m, 150m, OrderTypes.Limit);
			rejected.IsValid.AssertFalse();
			rejected.RejectReason.AssertEqual("Invalid qty");

			// 0.00005 rounds up to 0.0001 -> survives the preamble; price 150 < 500 -> accept.
			var accepted = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 0.00005m, 150m, OrderTypes.Limit);
			accepted.IsValid.AssertTrue();
			accepted.RejectReason.AssertNull();
		});

	[TestMethod]
	public Task Live_FutureEffectiveRowExcluded()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// CR-32: a stricter row effective in the past and a looser row scheduled a year in the future,
			// same scope. The gate's "effective_date <= SYSUTCDATETIME()" cutoff must exclude the future row
			// so the CURRENT stricter ceiling (price 500) applies: an order priced 600 is REJECTED. Without
			// the cutoff the future, most-recent looser row (price 999999) would be selected and 600 accepted.
			await InsertLimitsAsync(c, t, pid, sid, default, price: 500m, effectiveDate: DateTime.UtcNow.AddDays(-30));
			await InsertLimitsAsync(c, t, pid, sid, default, price: 999999m, effectiveDate: DateTime.UtcNow.AddDays(365));

			var rejected = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 1m, 600m, OrderTypes.Limit);
			rejected.IsValid.AssertFalse();
			rejected.RejectReason.AssertEqual("Order price 600.0000 meets/exceeds limit 500.0000");

			// Sanity: a sub-500 price is still ACCEPTED, confirming the past (current) row is the one in force
			// and the future looser row is genuinely excluded rather than simply never matched.
			var ok = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 1m, 499m, OrderTypes.Limit);
			ok.IsValid.AssertTrue();
			ok.RejectReason.AssertNull();
		});

	[TestMethod]
	public Task Live_CanonicalWiringIsScopeIsolated()
		=> RunWithFreshScopeAsync(async (svc, c, t, pidA, sidA) =>
		{
			// CR-1: the scope-aware provider (PreTradeRiskService.LoadLimitsAsync) selects the row for a
			// specific (portfolio, security), and seeding ONE RiskManager per exact scope from it keeps
			// scopes isolated. A single shared, scope-blind manager could not represent two different scoped
			// rows at once (the finding's "CreateRules discards scope"), so the correct wiring is per-scope.
			var (pidB, sidB) = await InsertScopeAsync(c, t, default);

			// Two scopes, two DIFFERENT canonical price ceilings on the same shared RiskLimitSet type.
			await InsertLimitsAsync(c, t, pidA, sidA, default, price: 500m);
			await InsertLimitsAsync(c, t, pidB, sidB, default, price: 999m);

			// The provider returns each scope's OWN row (scope-aware selection, not a global default).
			var limitsA = await svc.LoadLimitsAsync(c, t, pidA, sidA);
			var limitsB = await svc.LoadLimitsAsync(c, t, pidB, sidB);
			limitsA.AssertNotNull();
			limitsB.AssertNotNull();
			limitsA.EffectiveMaxOrderPrice.Value.AssertEqual(500m);
			limitsB.EffectiveMaxOrderPrice.Value.AssertEqual(999m);

			// One circuit breaker per exact scope, each seeded from that scope's canonical row: isolated.
			var managerA = new RiskManager();
			var managerB = new RiskManager();
			managerA.ApplyCanonicalLimits(limitsA);
			managerB.ApplyCanonicalLimits(limitsB);
			managerA.Rules.OfType<RiskOrderPriceRule>().Single().Price.AssertEqual(500m);
			managerB.Rules.OfType<RiskOrderPriceRule>().Single().Price.AssertEqual(999m);

			// Both enforcement patterns for a scope read the SAME canonical source: the per-order gate for
			// scope A (ceiling 500) REJECTS a 600 price while scope B (ceiling 999) ACCEPTS it - matching the
			// per-scope managers seeded above, proving one canonical definition drives both patterns.
			(await svc.ValidateAsync(c, t, pidA, sidA, Sides.Buy, 10m, 600m, OrderTypes.Limit)).IsValid.AssertFalse();
			(await svc.ValidateAsync(c, t, pidB, sidB, Sides.Buy, 10m, 600m, OrderTypes.Limit)).IsValid.AssertTrue();
		});

	[TestMethod]
	public Task Live_FrequencyRollingCount()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, freqCount: 5, freqWindow: 60);

			// Four prior orders inside the rolling 60s window (DB-clock stamped). The 5th is the candidate.
			for (var i = 0; i < 4; i++)
				await InsertOrderAsync(c, t, pid, sid, 'B', 10m, 100m, "ACCEPTED", default);

			var r = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 10m, 100m, OrderTypes.Limit);
			r.IsValid.AssertFalse();
			r.RejectReason.AssertEqual("Order frequency 5 in 60s meets/exceeds limit 5");
		});

	[TestMethod]
	public Task Live_PositionPostFillProjection()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, position: 100000m);
			await InsertPositionAsync(c, t, pid, sid, 99000m, default);

			// current 99,000 (read from dbo.Positions) + 1,000 buy = 100,000 >= 100,000 -> reject.
			var r = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 1000m, 100m, OrderTypes.Limit);
			r.IsValid.AssertFalse();
			r.RejectReason.AssertEqual("Resulting position 100000.0000 meets/exceeds limit 100000.0000");
		});

	[TestMethod]
	public Task Live_CommissionEstimateFromTrades()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, commission: 5000m, rate: 0.0005m);

			// existing traded notional = 100,000 * 100 = 10,000,000 (read via Trades JOIN Orders).
			var orderId = await InsertOrderAsync(c, t, pid, sid, 'B', 100000m, 100m, "FILLED", default);
			await InsertTradeAsync(c, t, orderId, 100000m, 100m, default);

			// estimate = 10,000,000*0.0005 + 10*100*0.0005 = 5000 + 0.5 = 5000.5 >= 5000 -> reject.
			var r = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 10m, 100m, OrderTypes.Limit);
			r.IsValid.AssertFalse();
			r.RejectReason.AssertEqual("Estimated cumulative commission 5000.5000 meets/exceeds limit 5000.0000");
		});

	[TestMethod]
	public Task Live_DailyVolumeStatusAndDayFilter()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, daily: 250000m);

			// Counted (ACCEPTED/FILLED, today): 200,000 + 49,000 = 249,000.
			await InsertOrderAsync(c, t, pid, sid, 'B', 200000m, 100m, "ACCEPTED", default);
			await InsertOrderAsync(c, t, pid, sid, 'B', 49000m, 100m, "FILLED", default);
			// NOT counted: a REJECTED order today (status filter) and an ACCEPTED order dated yesterday
			// (UTC-day boundary). If either leaked in, the reject number below would differ.
			await InsertOrderAsync(c, t, pid, sid, 'B', 500000m, 100m, "REJECTED", default);
			await InsertOrderAsync(c, t, pid, sid, 'B', 500000m, 100m, "ACCEPTED", default, submitted: DateTime.UtcNow.AddDays(-1));

			// today 249,000 + 1,000 = 250,000 >= 250,000 -> reject, proving both filters.
			var r = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 1000m, 100m, OrderTypes.Limit);
			r.IsValid.AssertFalse();
			r.RejectReason.AssertEqual("Daily volume 250000.0000 meets/exceeds limit 250000.0000");
		});

	[TestMethod]
	public Task Live_ZeroCeilingsAreNotEnforced()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// A row of all-zero ceilings must load as "unlimited" (the NULL/0 convention), not always-reject.
			await InsertLimitsAsync(c, t, pid, sid, default,
				price: 0m, qty: 0m, value: 0m, position: 0m, daily: 0m, freqCount: 0, freqWindow: 0, commission: 0m);

			var r = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 999999m, 999999m, OrderTypes.Limit);
			r.IsValid.AssertTrue();
			r.RejectReason.AssertNull();
		});

	[TestMethod]
	public Task Live_MalformedFrequencyPairFailsClosed()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			// CR-12: a count WITHOUT a window is a malformed pair. Previously the gate silently DISABLED the
			// frequency check (fail open) while the manager factory THREW - an inconsistency the review flagged.
			// The gate now fails CLOSED through the same authoritative RiskLimitSet.Validate, so ValidateAsync
			// throws rather than accepting an order under an un-evaluable frequency configuration. This is the
			// live counterpart of the pure EvaluateFailsClosedOnMalformedConfig / factory
			// ManagerCreateRulesNegativeConfigFailsClosed assertions.
			await InsertLimitsAsync(c, t, pid, sid, default, freqCount: 5, freqWindow: null);

			await ThrowsExactlyAsync<ArgumentException>(async ()
				=> await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 10m, 100m, OrderTypes.Limit));
		});

	[TestMethod]
	public Task Live_FirstFailWinsThroughDbPath()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, price: 500m, qty: 10000m, value: 1000000m);

			// Violates price, qty and value at once; the price reason (check 1) must win.
			var r = await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 20000m, 600m, OrderTypes.Limit);
			r.IsValid.AssertFalse();
			r.RejectReason.AssertEqual("Order price 600.0000 meets/exceeds limit 500.0000");
		});

	[TestMethod]
	public Task Live_InvalidSideRejectedBeforeDbRead()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, price: 500m);

			// The side/qty preamble runs before any DB read, so an invalid side is an authoritative reject.
			var r = await svc.ValidateAsync(c, t, pid, sid, (Sides)999, 10m, 100m, OrderTypes.Limit);
			r.IsValid.AssertFalse();
			r.RejectReason.AssertEqual("Invalid side: 999");
		});

	[TestMethod]
	public Task Live_UnsupportedOrderTypeThrows()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, price: 500m);

			await ThrowsExactlyAsync<NotSupportedException>(async ()
				=> await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 10m, 100m, OrderTypes.Conditional));
		});

	[TestMethod]
	public Task Live_InvalidIdentifiersThrow()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await ThrowsExactlyAsync<ArgumentOutOfRangeException>(async ()
				=> await svc.ValidateAsync(c, t, 0, sid, Sides.Buy, 10m, 100m, OrderTypes.Limit));

			await ThrowsExactlyAsync<ArgumentOutOfRangeException>(async ()
				=> await svc.ValidateAsync(c, t, pid, -1, Sides.Buy, 10m, 100m, OrderTypes.Limit));
		});

	[TestMethod]
	public Task Live_OverflowQtyThrows()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, price: 500m);

			// A magnitude beyond DECIMAL(18,4) fails deterministically (the C# analogue of SQL overflow).
			await ThrowsExactlyAsync<OverflowException>(async ()
				=> await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 1e20m, 100m, OrderTypes.Limit));
		});

	[TestMethod]
	public Task Live_CancellationIsObserved()
		=> RunWithFreshScopeAsync(async (svc, c, t, pid, sid) =>
		{
			await InsertLimitsAsync(c, t, pid, sid, default, freqCount: 5, freqWindow: 60);

			using var cts = new CancellationTokenSource();
			cts.Cancel();

			// A pre-cancelled token is observed at the first database read (the RiskLimits load).
			await ThrowsAsync<OperationCanceledException>(async ()
				=> await svc.ValidateAsync(c, t, pid, sid, Sides.Buy, 10m, 100m, OrderTypes.Limit, cts.Token));
		});

	[TestMethod]
	public async Task Live_ConvenienceOverloadRejectsOverPriceOrder()
	{
		await using (var probe = await TryOpenLegacyAsync())
		{
			if (probe is null)
			{
				Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live convenience-overload test.");
				return;
			}
		}

		// The connection/transaction-free overload opens and commits its OWN read-only transaction. The
		// seeded DEMO portfolio (1) caps price at 500, so an order at 999 is rejected at check 1 regardless
		// of order history - deterministic against the shared seed and writes no rows.
		var service = new PreTradeRiskService(SqlLegacyConnection.Resolve());

		var r = await service.ValidateAsync(1, 1, Sides.Buy, 1m, 999m, OrderTypes.Limit);
		r.IsValid.AssertFalse();
		r.RejectReason.AssertEqual("Order price 999.0000 meets/exceeds limit 500.0000");
	}

	[TestMethod]
	public async Task Live_ConcurrentSubmissionsSerializeThroughGateway()
	{
		await using (var probe = await TryOpenLegacyAsync())
		{
			if (probe is null)
			{
				Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live concurrency test.");
				return;
			}
		}

		var connectionString = SqlLegacyConnection.Resolve();
		int portfolioId;
		int securityId;

		// Commit a fresh scope with a small daily-volume ceiling (150). Two concurrent buys of 100 each
		// would BOTH pass if validated against stale (pre-insert) state; the gateway's per-portfolio
		// application lock + transaction must serialize them so exactly ONE is accepted (C03/TOCTOU).
		await using (var setup = new SqlConnection(connectionString))
		{
			await setup.OpenAsync();
			await using var tx = (SqlTransaction)await setup.BeginTransactionAsync(IsolationLevel.ReadCommitted);
			(portfolioId, securityId) = await InsertScopeAsync(setup, tx, default);
			await InsertLimitsAsync(setup, tx, portfolioId, securityId, default, daily: 150m);
			await tx.CommitAsync();
		}

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);

			var first = gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 100m, OrderTypes.Limit);
			var second = gateway.SubmitOrderAsync(portfolioId, securityId, Sides.Buy, 100m, 100m, OrderTypes.Limit);

			var results = await Task.WhenAll(first, second);

			// Exactly one accepted, one rejected (the race did not let both through).
			var accepted = results.Count(r => r.IsValid);
			var rejected = results.Count(r => !r.IsValid);
			accepted.AssertEqual(1);
			rejected.AssertEqual(1);

			// The rejected one failed the daily-volume ceiling once the first order's 100 was committed.
			var rejectedResult = results.Single(r => !r.IsValid);
			rejectedResult.RejectReason.AssertEqual("Daily volume 200.0000 meets/exceeds limit 150.0000");
		}
		finally
		{
			// Remove every row the committed scope produced, children before parents (FK order).
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
			cmd.Parameters.Add(NInt("@p", portfolioId));
			cmd.Parameters.Add(NInt("@s", securityId));
			await cmd.ExecuteNonQueryAsync();
		}
	}

	// ---- DB-P5-02 live regression : concurrent FIRST-CREATE Ensure* converges, never leaks 2601/2627 ----
	// Many callers race to create the SAME brand-new portfolio through EnsurePortfolioAsync. Before the fix
	// the losers of the UQ_Portfolios_name race surfaced a raw unique-key SqlException (2627/2601); now
	// every racing caller must converge on the one committed portfolio_id, exactly one row is created, and
	// no unique-key exception escapes - proven because Task.WhenAll completes without throwing.
	[TestMethod]
	public async Task Live_ConcurrentEnsurePortfolioFirstCreateConvergesToSingleId()
	{
		await using (var probe = await TryOpenLegacyAsync())
		{
			if (probe is null)
			{
				Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live Ensure-portfolio concurrency test.");
				return;
			}
		}

		var connectionString = SqlLegacyConnection.Resolve();
		// A brand-new, collision-free name so the very first create is what races (GUID-tagged so it can
		// never clash with the shared DEMO seed or any parallel scope row).
		var name = "DBP502_PF_" + Guid.NewGuid().ToString("N");
		var portfolio = new Portfolio { Name = name };

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);

			// Release all workers at once so they hit the SELECT-miss -> INSERT race simultaneously.
			const int concurrency = 24;
			var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var workers = Enumerable.Range(0, concurrency)
				.Select(async _ =>
				{
					await gate.Task;
					return await gateway.EnsurePortfolioAsync(portfolio);
				})
				.ToArray();

			gate.SetResult();

			// If ANY worker leaked a unique-key SqlException, WhenAll rethrows here and the test fails.
			var ids = await Task.WhenAll(workers);

			// Every racing caller converged on the same, single portfolio_id.
			ids.Distinct().Count().AssertEqual(1);
			(ids[0] > 0).AssertTrue();

			// A later call is idempotent - it returns that same id rather than creating another row.
			(await gateway.EnsurePortfolioAsync(portfolio)).AssertEqual(ids[0]);

			// Exactly one physical row exists for the raced name (no duplicate created).
			await using var verify = new SqlConnection(connectionString);
			await verify.OpenAsync();
			await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.Portfolios WHERE name = @name", verify);
			count.Parameters.AddWithValue("@name", name);
			((int)await count.ExecuteScalarAsync()).AssertEqual(1);
		}
		finally
		{
			// The test creates only a Portfolios row (no orders/positions/limits), so a delete by the
			// unique name fully restores the database.
			await using var cleanup = new SqlConnection(connectionString);
			await cleanup.OpenAsync();
			await using var cmd = new SqlCommand("DELETE FROM dbo.Portfolios WHERE name = @name", cleanup);
			cmd.Parameters.AddWithValue("@name", name);
			await cmd.ExecuteNonQueryAsync();
		}
	}

	// Same race for EnsureSecurityAsync, keyed on UQ_Securities_code_board (security_code + board_code).
	[TestMethod]
	public async Task Live_ConcurrentEnsureSecurityFirstCreateConvergesToSingleId()
	{
		await using (var probe = await TryOpenLegacyAsync())
		{
			if (probe is null)
			{
				Inconclusive("StockSharpLegacy SQL Server is not reachable; skipping live Ensure-security concurrency test.");
				return;
			}
		}

		var connectionString = SqlLegacyConnection.Resolve();
		// A brand-new, collision-free security_code (<= 50 chars); fixed NASDAQ board so the (code, board)
		// key is unique to this test run.
		var code = "DBP502SEC" + Guid.NewGuid().ToString("N")[..20];
		var boardCode = ExchangeBoard.Nasdaq.Code;
		var security = new Security { Id = code + "@" + boardCode, Code = code, Board = ExchangeBoard.Nasdaq, Type = SecurityTypes.Stock };

		try
		{
			var gateway = new SqlLegacyOrderGateway(connectionString);

			const int concurrency = 24;
			var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var workers = Enumerable.Range(0, concurrency)
				.Select(async _ =>
				{
					await gate.Task;
					return await gateway.EnsureSecurityAsync(security);
				})
				.ToArray();

			gate.SetResult();

			var ids = await Task.WhenAll(workers);

			ids.Distinct().Count().AssertEqual(1);
			(ids[0] > 0).AssertTrue();
			(await gateway.EnsureSecurityAsync(security)).AssertEqual(ids[0]);

			await using var verify = new SqlConnection(connectionString);
			await verify.OpenAsync();
			await using var count = new SqlCommand(
				"SELECT COUNT(*) FROM dbo.Securities WHERE security_code = @code AND (board_code = @board OR (@board IS NULL AND board_code IS NULL))", verify);
			count.Parameters.AddWithValue("@code", code);
			count.Parameters.AddWithValue("@board", (object)boardCode ?? DBNull.Value);
			((int)await count.ExecuteScalarAsync()).AssertEqual(1);
		}
		finally
		{
			await using var cleanup = new SqlConnection(connectionString);
			await cleanup.OpenAsync();
			await using var cmd = new SqlCommand(
				"DELETE FROM dbo.Securities WHERE security_code = @code AND (board_code = @board OR (@board IS NULL AND board_code IS NULL))", cleanup);
			cmd.Parameters.AddWithValue("@code", code);
			cmd.Parameters.AddWithValue("@board", (object)boardCode ?? DBNull.Value);
			await cmd.ExecuteNonQueryAsync();
		}
	}
}
