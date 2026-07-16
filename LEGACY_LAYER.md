# The risk/position layer (StockSharpLegacy)

Owner: whoever's on call for order/position issues, in practice.

This document used to exist because "why did this order get rejected" tickets
kept turning out to be a disagreement between the C# risk engine and a second,
independent SQL risk engine, and there was no single place that explained both.
That divergence is now gone. This is the writeup of the consolidation: every
risk rule is defined exactly once, in C#, and every enforcement path reads that
one definition. If you're new to this codebase: read this before you go looking
for "the" risk check, because now there genuinely is one canonical definition
per rule - even though it is still enforced through two different patterns.

## The short version

- **All pre-trade risk validation and all position/realized-P&L recalculation
  logic lives in C#, under `Algo/Risk`.** Each rule's threshold and comparison
  are owned by exactly one type and reused everywhere - a single source of
  truth.
- **SQL Server is now pure data storage** - tables, constraints, indexes, and
  one narrow audit trigger. It holds no risk thresholds, no accept/reject
  decisions, and no P&L arithmetic. The three business stored procedures and the
  position-recalc trigger have been retired.
- **There are still two enforcement *patterns*, and that is deliberate.** A
  portfolio-wide circuit breaker (`RiskManager`) and a per-order pre-trade gate
  (`PreTradeRiskService`) are different shapes of control for different jobs.
  They are **not** merged - but they no longer diverge either, because they
  consume the same canonical rule definitions and differ only in the input each
  one feeds those rules.

## What's here

- `Algo/Risk/` - the canonical risk/position layer:
  - `PreTradeRiskService` - the per-order pre-trade **gate** (accepts or rejects
    one prospective order). This is the C# replacement for the retired
    `usp_ValidatePreTradeRisk` and `usp_SubmitOrder`.
  - `PositionRecalculationService` - average-cost weighting and realized-P&L
    recompute. C# replacement for the retired `usp_RecalculatePositionOnTrade`
    and the retired `trg_Trades_PositionRecalc` trigger.
  - The `IRiskRule` subclasses that hold each rule's threshold and comparison,
    including the two promoted from SQL-only to first-class rules:
    `RiskOrderValueRule` (order notional) and `RiskDailyVolumeRule` (daily
    traded volume). New rules are auto-discovered by `InMemoryRiskRuleProvider`,
    so no registration edit was needed.
  - `RiskManager` / `RiskMessageAdapter` - the portfolio-wide **circuit
    breaker**. Its mechanics (`ClosePositions` / `StopTrading` / `CancelOrders`)
    are unchanged; only the source of each threshold moved into the shared rule
    classes.
- `Algo/Storages/Sql/` - `SqlLegacyOrderGateway`, now a pure ADO.NET (raw
  `SqlCommand`, not Dapper, not EF) data-access gateway. It performs CRUD only
  and delegates every decision to the two services above. `SqlLegacyConnection`
  resolves the connection string; `SqlPosition` / `SqlOrderSubmitResult` are
  DTOs.
- `/Database` - schema (`001_Schema.sql`), the now-logic-free
  `002_StoredProcedures.sql` (idempotently drops the three retired procedures),
  triggers (`003_Triggers.sql`, which drops the retired recalc trigger and keeps
  only the audit trigger), and seed data (`004_SeedData.sql`). `Database/README.md`
  has the Docker one-liner and run order.
- `Samples/08_Misc/03_LegacySqlDemo` - a runnable walkthrough: submit a compliant
  order (accepted), submit one that breaches `max_order_price` (rejected, with
  reason), record a fill, and print the position the C# service recomputed.

## One canonical definition, two enforcement patterns

`Algo/Risk` (C#) was there first as a stream-based circuit breaker. The SQL
`usp_ValidatePreTradeRisk` was added later as a per-order gate, and over time the
two drifted apart with no shared source of truth - the same rule was encoded
twice and the two encodings disagreed. The consolidation keeps the two *patterns*
(they solve different problems) but removes the divergence by giving both a
single rule definition to read:

- **`Algo.Risk.RiskManager`** evaluates rules against the live message stream and,
  when one trips, takes a portfolio-wide action - `ClosePositions`,
  `StopTrading`, or `CancelOrders` (see `RiskMessageAdapter.ProcessRiskAsync`).
  It does not reject the specific order that tripped the rule; it is a circuit
  breaker, not a gate.
- **`Algo.Risk.PreTradeRiskService`** is a gate: it evaluates one order before it
  is accepted and rejects that order, specifically, if it fails. It is what
  `SqlLegacyOrderGateway.SubmitOrderAsync` calls.

The important change from the old world: **both patterns now read the same
`IRiskRule` definition for any rule that genuinely exists in both.** Where a rule
needs a runtime decision, that decision is a single shared static comparison
(for example `RiskOrderFreqRule.IsFrequencyExceeded`,
`RiskPositionSizeRule.IsPositionSizeExceeded`,
`RiskDailyVolumeRule.IsDailyVolumeExceeded`) that the circuit breaker and the
gate both route through. The circuit breaker feeds it live streaming state; the
gate feeds it a projection or an aggregate read from SQL. Same rule, same
comparison, different input - and the input difference is by design, not an
accident of two implementations.

## Rule-by-rule reconciliation

This is the analytical core of the consolidation: for every rule that was
present on both sides (or on only one), what happened to it and why. Each rule
resolves to exactly one disposition - a single canonical implementation used by
every caller, or two intentionally-distinct implementations that are documented
as allowed to disagree.

| Check | Canonical implementation | Was (C# / SQL) | Decision | Why |
|---|---|---|---|---|
| Order price ceiling | `RiskOrderPriceRule` (`price >= limit`) | C# `RiskOrderPriceRule` / SQL `max_order_price` | **MERGE** | Already identical `>=` semantics on both sides; one class, both callers. |
| Order quantity ceiling | `RiskOrderVolumeRule` (`qty >= limit`) | C# `RiskOrderVolumeRule` / SQL `max_order_qty` | **MERGE** | Identical semantics; only the `qty` (SQL) vs `Volume` (C#) naming differed, reconciled in docs. |
| Order notional value (qty x price) | `RiskOrderValueRule` (`qty*price >= limit`) | - / SQL `max_order_value` | **RELOCATE** | Was SQL-only. Promoted to a first-class C# rule; no counterpart to merge. |
| Order frequency | `RiskOrderFreqRule` - rolling window | C# fixed non-overlapping window / SQL rolling `COUNT(*)` | **MERGE (rolling wins)** | Different algorithm. Rolling is strictly stricter near a boundary, so it is canonical (see below). |
| Resulting position size | `RiskPositionSizeRule` (symmetric-absolute `>=`) | C# checks *current* position / SQL checks *hypothetical post-fill* | **SHARED DEFINITION, TWO APPLICATION POINTS** | One threshold and comparison; the gate feeds a post-fill projection, the circuit breaker feeds the live value. A by-design timing difference, not two rules. |
| Cumulative commission | Two implementations, on purpose | C# `RiskCommissionRule` / `RiskOrderCommissionRule` (actual post-fill) / SQL `max_commission_total` (pre-fill estimate) | **KEEP SEPARATE BY DESIGN** | Same limit value, fundamentally different figures available at different times. These will not agree, and are not meant to. |
| Daily traded volume | `RiskDailyVolumeRule` | - / SQL `max_daily_volume` | **RELOCATE** | Was SQL-only. Promoted to a first-class C# rule. |
| Position lifetime, P&L limit, slippage | `RiskPositionTimeRule`, `RiskPnLRule`, `RiskSlippageRule` | C#-only / - | **NO CHANGE** | Need live state the pre-trade gate does not have; outside the reconciliation surface. |

## The stricter-wins hard constraint

**Reconciled risk thresholds must never end up less strict than the stricter of
the two original implementations. This is a hard constraint, not a preference.**
Consolidating a control is not a licence to silently loosen it. Wherever the two
old implementations disagreed on strictness, the canonical behaviour equals the
stricter one, and the choice is documented both here and at the point of
implementation in the code. The two places this bites are the frequency check
(rolling beats fixed) and the pre-trade position-size check (the gate must
project the post-fill position, not fall back to the current one).

## Deep dive: order frequency (rolling wins)

This is the flagship reconciliation. The old C# rule bucketed time into fixed,
non-overlapping windows: it opened a window on the first order, counted while
inside it, and reset at the boundary - so a burst straddling a boundary could
send a full quota at the end of one window and another full quota at the start of
the next and dodge the limit. The old SQL rule ran a true rolling `COUNT(*)` of
orders whose timestamp fell within the last N seconds, which rejects exactly
those boundary bursts.

Because the rolling algorithm rejects bursts the fixed algorithm would admit, it
is strictly stricter, so **the canonical `RiskOrderFreqRule` adopts rolling-window
semantics.** It keeps its public `Count` / `Interval` configuration (so
persistence and existing wiring are unaffected) but internally evicts timestamps
older than `now - Interval` and rejects when `priorCountInWindow + 1 >= Count`.
That `>=` comparison lives in the shared `RiskOrderFreqRule.IsFrequencyExceeded`
static method: the circuit breaker feeds it the count from its own rolling buffer;
the pre-trade gate feeds it a SQL `COUNT(*)` over the same window. One rule,
one comparison, two inputs.

## Deep dive: position size (shared definition, two application points)

`RiskPositionSizeRule` owns one threshold and one comparison - a symmetric
absolute test, `Math.Abs(value) >= Math.Abs(limit)`, exposed as the shared
`RiskPositionSizeRule.IsPositionSizeExceeded` static method. It is applied at two
points:

- The **circuit breaker** feeds it the *current* position value from a live
  `PositionChangeMessage`.
- The **pre-trade gate** feeds it a *post-fill projection* - `current position +
  signed order quantity` - so the control is genuinely pre-trade. Dropping the
  projection and checking only the current position would loosen the gate and
  violate the stricter-wins constraint.

Same threshold, same comparison, deliberately different input. This is one rule
applied twice, not two rules.

## Deep dive: commission (kept separate by design)

Pre-trade estimated commission and post-fill actual commission are **not the same
thing and are deliberately not forced into one implementation.**

- The **pre-trade gate** computes an *estimate* from the order itself:
  `existing_notional * rate + qty * est_price * rate`, rounded once at the end to
  the SQL money scale. It has to estimate, because the real commission is not
  known until the order fills.
- The **circuit-breaker path** accumulates the *actual* commission off
  `ExecutionMessage` after a fill, via `RiskCommissionRule` /
  `RiskOrderCommissionRule`.

Both consume the same `max_commission_total` limit, but they operate on different
figures available at different times, so the same order can legitimately pass one
and fail the other. They remain two implementations, each documented at its
point of use. This is the one case where "two implementations" is the correct
answer rather than a divergence to be eliminated.

## Position recalculation and the retired double-count hazard

The average-cost math moved wholesale into `PositionRecalculationService`:
signed-quantity handling, weighted-average price on same-sign / flat
accumulation, and realized P&L on the closed portion with partial-close,
exact-close, and full-close-and-flip branches - a faithful port of the retired
`usp_RecalculatePositionOnTrade`.

The old design had a latent hazard: `trg_Trades_PositionRecalc` fired on every
`Trades` insert **and** the proc was also exposed standalone, so calling both
double-counted a trade against `qty` / `avg_price` / `realized_pnl`. Nothing in
the schema prevented it. The consolidation eliminates this by construction:
`SqlLegacyOrderGateway.RecordTradeAsync` inserts the trade and then calls
`PositionRecalculationService` **exactly once**, inside the same transaction as
the insert. There is a single, unambiguous source of recompute and no trigger
racing it.

As before, `unrealized_pnl` on `Positions` is left untouched, because it requires
a live market price the recompute does not have. Treat that column as stale /
EOD-only if you read it.

## Cross-cutting strictness conventions (preserved verbatim)

These subtle conventions were preserved exactly so the consolidation neither
loosened nor tightened behaviour unintentionally:

- **`>=` ("meets or exceeds") boundary.** Every rule rejects when the value
  *equals* the limit. Switching to `>` would loosen the control, so `>=` is kept.
- **`NULL` or `0` means "not enforced".** A null or zero threshold disables that
  check, on both the old SQL side and the canonical C# rules. A **negative**
  threshold is treated as a misconfiguration and the gate **fails closed**
  (rejects) rather than silently skipping the check.
- **Most-specific limit precedence.** The gate selects the applicable
  `RiskLimits` row by precedence - portfolio+security first, then
  portfolio-only, then security-only - ordered by `effective_date` descending.
  This selection logic now lives in `PreTradeRiskService`.
- **Input pre-checks.** `side` in {`B`, `S`} and `qty > 0` are validated before
  any threshold check.

## What stayed in SQL

Only one piece of logic remains in SQL, and it is pure data-audit CRUD:
`trg_Orders_StatusAudit` cascades an order status change into
`OrderStatusHistory`. It is narrow on purpose (fires only when the `status`
column actually changes), holds no thresholds and makes no decisions, so it is
correctly a data concern and stays in the database. Everything else in
`002_StoredProcedures.sql` / `003_Triggers.sql` is now just idempotent
`DROP ... IF EXISTS` for the retired objects, so a legacy database upgraded in
place cannot leave stale business logic installed.

The schema stays vendor-neutral - plain DDL with no procedural logic to port -
which keeps a future move to a Postgres-compatible engine unobstructed. That
migration is explicitly not part of this work.

## Half-migrated persistence (unchanged)

`SqlLegacyOrderGateway` is still not a full `IEntityRegistry` implementation -
it is an adapter that sits alongside `CsvEntityRegistry`. Orders, trades, and
positions go through SQL; securities, exchanges, and subscriptions are still
served by the existing CSV storage. `EnsurePortfolioAsync` /
`EnsureSecurityAsync` match rows by name / (code, board) because there is no
shared identifier between `BusinessEntities.Portfolio` / `Security` and their
SQL counterparts - if a portfolio is renamed on one side, this silently starts
creating a second row on the other. This was out of scope for the risk/position
consolidation and is unchanged.

## Known rough edges (left in on purpose)

- `PreTradeRiskService.ValidateAsync` accepts an optional `requestedBy`
  parameter that is carried for parity with the retired proc signature but is not
  currently read or persisted. It corresponds to a compliance ask (tag every risk
  check with who initiated it) that was descoped; the parameter is kept so the
  audit hook can be wired later without a signature change.
- `qty` in every SQL table vs. `Volume` / `Quantity` on the C#
  `Order` / `MyTrade` objects. The column name was baked into the schema before
  anyone noticed; the mapping is handled at the gateway boundary and the naming
  is left as-is.
- `Orders.external_transaction_id` exists to let a support engineer correlate a
  row back to the in-memory `Order.TransactionId`, but it is nullable and was
  never back-filled for anything inserted before it was added.
- Only `OrderTypes.Limit` / `.Market` map to `dbo.Orders.order_type`; sending a
  conditional/stop order through `SqlLegacyOrderGateway` throws
  `NotSupportedException`. Conditional orders were out of scope for this pass.

## Testing

The SQL business layer previously had no automated coverage at all - its
correctness was demonstrated only through the runnable sample. The consolidation
adds net-new automated coverage in `Tests/RiskTests.cs`, following a strict
order: **characterization first** (capture the behaviour that existed), then
**parity after** (prove each rule either matches the chosen canonical behaviour
or correctly preserves an intentionally-distinct one). The matrix covered:

| Behaviour under test | What the tests assert |
|---|---|
| Price / quantity ceilings | Reject at the `>=` limit (both sides already agreed). |
| Order notional value | `RiskOrderValueRule` rejects `qty*price >= limit`. |
| Order frequency (boundary) | The canonical rolling rule rejects a boundary burst the old fixed window would have admitted; boundary is inclusive; a zero interval disables the check; the buffer resets and stays bounded. |
| Position size (timing) | The gate projects the post-fill position; a short position trips a positive limit under the symmetric-absolute comparison; the circuit-breaker path still checks the live value. |
| Commission (estimate vs actual) | Both computations are preserved; the same order can pass/fail differently under the estimate vs the actual, and that is expected. |
| Daily traded volume | `RiskDailyVolumeRule` rejects `today + qty >= limit`; the cumulative resets on a UTC day rollover. |
| Position recompute | A single C# service call recomputes across open / add / partial-close / exact-close / flip / short-cover branches with SQL-parity rounding, guards invalid input, and cannot double-count. |

These run within the existing MSTest + Ecng.UnitTesting harness.

## Running it

See `Database/README.md` for the Docker command and script run order, and
`Samples/08_Misc/03_LegacySqlDemo` for a working end-to-end example. The
demonstrated outcomes are preserved exactly: an order within every configured
limit is accepted, an order breaching `max_order_price` is rejected with a
reason, and recording a trade against an accepted order updates the position
(quantity / average price / realized P&L) automatically - now via the C#
`PositionRecalculationService` rather than a trigger. Every scenario has been run
against a real `mcr.microsoft.com/mssql/server` container, not just reviewed by
eye.
