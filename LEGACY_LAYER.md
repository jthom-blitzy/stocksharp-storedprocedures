# The StockSharpLegacy layer

SQL Server **data storage** plus a single, canonical C# **risk/position
business-logic layer** under `Algo/Risk`. Every pre-trade risk rule and all
position/realized-P&L arithmetic is defined exactly once in C#; SQL Server
holds the data (tables, constraints, indexes, the threshold *values* in
`RiskLimits`) and one pure audit trigger, and makes no accept/reject or P&L
decision.

If you are new to this codebase and looking for "the" risk check: there is
now exactly one definition per rule. A rule may be *enforced* through more
than one pattern (see below); the canonical threshold value and its `>=`
("meets or exceeds") boundary are defined once, so the patterns stay aligned
on what each limit is. Where the same rule is applied at different points -
notably resulting position size and cumulative commission - the two patterns
can still reach different per-order results *by design*; those cases are
called out explicitly below.

## History — why this file used to be a divergence warning

This layer once grew a *second*, SQL-side risk engine
(`dbo.usp_ValidatePreTradeRisk`, driven by `dbo.usp_SubmitOrder`) that was
added when the desk wanted to change a limit without waiting on a C# release.
For a while both engines were live, neither retired, and their per-rule
semantics drifted apart with no shared source of truth - the last several
"why did this order get rejected" tickets all turned out to be the C# risk
engine and the SQL risk engine disagreeing. This document used to catalogue
that divergence and warned, bluntly, that "nothing runs both."

That divergence has been consolidated away. The pre-trade validation logic
and the position/P&L math were moved wholesale into C#, each rule was
reconciled to a single canonical definition, and the SQL procedures and the
recalc trigger that carried business logic were retired. The rest of this
document describes the consolidated design and records, rule by rule, what
was merged (and into what) and what was deliberately kept as two
implementations (and why). That written judgment is the point of the
refactor.

## What's here

- `Algo/Risk/` - the canonical risk/position layer, the single source of
  truth for every rule and for the position math:
  - `PreTradeRiskService.cs` - the per-order **pre-trade gate**. C#
    replacement for the retired `dbo.usp_ValidatePreTradeRisk`; validates one
    prospective order against the most-specific `RiskLimits` row and returns
    accept/reject with a reason on the first failing rule (RULES 1-7).
  - `PositionRecalculationService.cs` - average-cost weighting and
    realized-P&L recompute. C# replacement for the retired
    `dbo.usp_RecalculatePositionOnTrade` and its trigger.
  - `RiskOrderValueRule.cs`, `RiskDailyVolumeRule.cs` - new first-class rules
    for checks that previously lived only in SQL.
  - `RiskOrderFreqRule.cs` - the order-frequency rule, now a true
    rolling-window count.
  - `RiskManager.cs` - the portfolio-wide **circuit breaker**. Its mechanics
    are unchanged; only the source of its thresholds moved.
  - the existing rule classes (`RiskOrderPriceRule`, `RiskOrderVolumeRule`,
    `RiskPositionSizeRule`, the commission rules, `RiskPnLRule`,
    `RiskPositionTimeRule`, `RiskSlippageRule`, ...) and the `IRiskRule` /
    `IRiskManager` / `IRiskRuleProvider` contracts. New rule classes are
    auto-discovered by `InMemoryRiskRuleProvider`, so no registration edit is
    needed.
- `Algo/Storages/Sql/` - `SqlLegacyOrderGateway`, now a **pure ADO.NET
  data-access gateway** (raw `SqlCommand`, not an ORM) that delegates every
  decision to the two services above, plus `SqlLegacyConnection` for
  connection-string resolution and the `SqlPosition` / `SqlOrderSubmitResult`
  DTOs.
- `Database/` - schema (`001_Schema.sql`), stored procedures
  (`002_StoredProcedures.sql`, which now installs no business logic),
  triggers (`003_Triggers.sql`, audit only), and seed data
  (`004_SeedData.sql`). Runnable against a fresh SQL Server instance -
  `Database/README.md` has the Docker one-liner and the script run order.
- `Samples/08_Misc/03_LegacySqlDemo` - a runnable end-to-end walkthrough:
  submit a compliant order, submit one that gets rejected with a reason,
  record a fill, and print the position the C# service recomputed.
- `Tests/RiskTests.cs` - characterization and parity tests covering every
  rule in the reconciliation matrix, including the frequency-boundary and
  position-size timing edge cases.

## One canonical definition, two enforcement patterns

Each risk rule's threshold-and-comparison is owned by exactly one `IRiskRule`
subclass. Two *distinct* enforcement patterns consume those same definitions;
they are deliberately **not** merged into one component, because they answer
two different questions:

1. **`Algo/Risk/RiskManager.cs` - the portfolio-wide circuit breaker.** It
   evaluates the live message stream against its configured rule set and, when
   a rule trips, drives a global action - `ClosePositions`, `StopTrading`, or
   `CancelOrders` - through `RiskMessageAdapter`. It does not reject the
   specific order that tripped the rule; it is a circuit breaker, not a gate.
   Its mechanics are unchanged by the consolidation; **only the source of each
   rule's threshold and comparison moved** - it now reuses the canonical rule
   definitions rather than re-encoding them.
2. **`Algo/Risk/PreTradeRiskService.cs` - the per-order pre-trade gate.** It
   evaluates one prospective order *before* it is accepted and rejects that
   order, specifically, with a reason, if it fails. This is the C# replacement
   for `dbo.usp_ValidatePreTradeRisk`, and it is what
   `SqlLegacyOrderGateway.SubmitOrderAsync` calls.

The two patterns share each rule's **canonical threshold and `>=` boundary**,
but they apply it at different points and with different inputs - the circuit
breaker feeds live stream/position state, the gate feeds a prospective order
(and, for the stateful rules, an aggregate or projection it reads from SQL
Server). For frequency, daily volume and order notional value the shared
decision is a single method both call (`RiskOrderFreqRule.IsFrequencyExceeded`,
`RiskDailyVolumeRule.IsDailyVolumeExceeded`, and `RiskOrderValueRule`'s
`ProcessMessage`); for the order price and quantity ceilings the gate applies
the same `>=` comparison to the prospective order while the circuit breaker
evaluates the live order stream through the rule's `ProcessMessage`. Because
they are applied at different points, the two patterns can reach different
per-order results **by design** - resulting position size and cumulative
commission are the documented cases below - but the canonical threshold value
and the `>=` boundary for a merged rule never drift. Both patterns honour the
SQL `NULL`/`0` = "not enforced" convention: a null or exactly-zero threshold
disables the corresponding check on the gate **and** on the circuit-breaker
path - the price, quantity and position-size rules each early-return "not
triggered" on a zero limit - so a zero limit can never trip a control on either
side.

**SQL is now pure data storage.** `Database/001_Schema.sql` remains
tables/constraints/indexes only; `RiskLimits` still *stores* the ceilings, but
the comparison and the accept/reject decision are defined in C#.
`Database/002_StoredProcedures.sql` installs no business logic at all - it
drops the three retired procedures with `DROP ... IF EXISTS`
(`usp_ValidatePreTradeRisk`, `usp_RecalculatePositionOnTrade`,
`usp_SubmitOrder`) so no stale logic is left installed on an upgrade, and the
file is kept only so the `001` -> `002` -> `003` -> `004` run order stays
intact. `Database/003_Triggers.sql` no longer contains any
calculation-driving trigger: the position-recalc trigger
(`trg_Trades_PositionRecalc`) was removed, and only the pure-audit
`trg_Orders_StatusAudit` (append-only `OrderStatusHistory` CRUD, fired only
when an order's status actually changes) remains, because auditing is a data
concern, not a business rule.

**Position/P&L math moved to C#.**
`Algo/Risk/PositionRecalculationService.cs` owns the average-cost weighting
and the realized-P&L close/flip logic that used to live in
`usp_RecalculatePositionOnTrade`. `SqlLegacyOrderGateway.RecordTradeAsync`
inserts the trade and then calls this service exactly once, inside the same
transaction, so the trade row and its position effect commit or roll back
together.

## Rule-by-rule reconciliation

This is the analytical core. Every rule that appeared on both sides, or only
on one, resolves to a single disposition: either one canonical C# class used
by every caller, or two intentionally-distinct implementations with the reason
stated.

| Rule | Canonical C# implementation | Decision | Justification |
|---|---|---|---|
| Order price ceiling | `RiskOrderPriceRule` (`price >= limit`) | **MERGE** | Identical `>=` semantics on both sides; no strictness change. |
| Order quantity ceiling | `RiskOrderVolumeRule` (`qty/Volume >= limit`) | **MERGE** | Identical semantics; only the `qty` (SQL) vs `Volume`/`Quantity` (C#) naming differed, reconciled in docs. |
| Order notional value (qty x price) | `RiskOrderValueRule` (new) | **RELOCATE** | Existed only in SQL (`max_order_value`); promoted to a first-class C# rule - there was no C# counterpart to merge. |
| Order frequency | `RiskOrderFreqRule` (rolling window) | **MERGE (rolling wins)** | Different algorithm; the rolling `COUNT(*)` is strictly stricter near a boundary, so it is canonical under the stricter-wins constraint. |
| Resulting position size | `RiskPositionSizeRule` | **SHARED DEFINITION, TWO APPLICATION POINTS** | Same threshold **and** the same absolute-magnitude `>=` comparison on both sides; the only difference is the *input* - the gate feeds a post-fill projection (`current + signed order qty`), the circuit breaker feeds the live position. A by-design input/timing difference, not a difference in the comparison. |
| Cumulative commission | `RiskCommissionRule` / `RiskOrderCommissionRule` (actual, post-fill) **and** the gate's pre-fill estimate | **KEEP SEPARATE BY DESIGN** | Same commission-ceiling *concept*, but each side owns its own configured limit (no shared wiring); two computations on figures available at different times. **They will not always agree, and that is intentional** - they are not forced into one implementation. |
| Daily traded volume | `RiskDailyVolumeRule` (new) | **RELOCATE** | Existed only in SQL (`max_daily_volume`); promoted to a first-class C# rule. |
| Position lifetime / P&L limit / slippage | `RiskPositionTimeRule` / `RiskPnLRule` / `RiskSlippageRule` | **NO CHANGE (C#-only)** | Need live state the pre-trade gate does not have; outside the reconciliation surface, noted for completeness. |

Three of these rows carry nuance worth spelling out - the frequency
canonicalization, the two "same definition, different input" cases (position
size and daily volume), and the commission split. They are covered next, along
with the hard constraint that governs all of them.

### The stricter-wins hard constraint

Consolidating a control is not a licence to loosen it. This is a **hard
constraint, not a preference**: a reconciled risk threshold must **never** end
up less strict than the stricter of the two original implementations. Where
the two implementations disagreed, the canonical behaviour equals the stricter
one, and the choice is documented both at the point of implementation (inline)
and here. The frequency rule below is the clearest instance.

### Order frequency: rolling window wins

The old C# rule bucketed time into fixed, non-overlapping windows: it opened a
window on the first order and tripped only when the count reached the limit
before the window expired. A burst that straddled a bucket boundary could dodge
the limit (a full quota at the end of one window and another full quota at the
start of the next). The SQL rule instead ran a true rolling `COUNT(*)` of
orders whose `submitted_date` fell within the last `window_sec` seconds. Near a
boundary the rolling count rejects bursts the fixed window would admit, so it is
strictly stricter.

Under the stricter-wins constraint, the canonical `RiskOrderFreqRule` therefore
adopts **rolling-window** semantics: an order counts while its timestamp lies
within `[now - Interval, now]`, with the lower bound **inclusive** to match the
SQL predicate `submitted_date >= now - window` exactly (an event landing
precisely on `now - Interval` is still counted). The public `Count`/`Interval`
configuration is retained unchanged, so persistence and existing wiring are
unaffected, and the `>=` ("meets or exceeds") reject boundary is preserved. The
decision itself lives in the shared `RiskOrderFreqRule.IsFrequencyExceeded`
helper: the circuit breaker supplies a count derived from its own streaming
window state, the gate supplies a SQL `COUNT(*)` over the same window, and both
route through the one comparison.

### Position size and commission: different by design

Two rows in the matrix share a limit value but must **not** be naively merged.

**Position size** is one threshold, one comparison, applied at two points that
differ only in their *input*. `RiskPositionSizeRule` owns the canonical limit
value and compares against the limit's **absolute magnitude**
(`Math.Abs(position) >= Math.Abs(limit)`) on **both** paths, so a limit of 100
caps exposure to 100 whether the book is long **or** short, and a zero limit
disables the check. On the circuit-breaker path it evaluates the *current* live
position; the pre-trade gate cannot wait for the fill, so it evaluates a
*post-fill projection* - the current position plus the signed order quantity
(`current + signed order qty`) - exactly as the retired SQL
`usp_ValidatePreTradeRisk` did (`ABS(@current + @signed_qty) >= @max_position_size`).
The threshold and the comparison are shared; only the input differs by design,
and dropping the gate's projection would loosen the gate and violate the
stricter-wins constraint, so the projection is mandatory. (The absolute-magnitude
comparison on the circuit-breaker path is the canonical form: an earlier
directional variant, which only capped the side matching the limit's sign and so
let a short slip under a positive cap, has been retired so the single definition
is applied identically everywhere.) (Daily traded volume follows the same "one definition, two
application points" shape: the circuit breaker keeps a running daily
accumulator, while the gate reads today's authoritative accepted/filled volume
from SQL and adds the prospective order - both call the single
`RiskDailyVolumeRule.IsDailyVolumeExceeded` comparison.)

**Commission** is deliberately **two implementations**, and this is the case
the refactor is most explicit about keeping apart. The C# commission rules
(`RiskCommissionRule` / `RiskOrderCommissionRule`) accumulate the **actual**
commission reported on an `ExecutionMessage` *after* a fill. The pre-trade gate
cannot see an actual commission - the trade does not exist yet - so it computes
a **pre-fill estimate** (`existing_notional * rate + qty * est_price * rate`,
using `RiskLimits.commission_rate` against the order price, or the security's
last traded price for a market order). Each side is configured with its own
commission ceiling - the gate reads `RiskLimits.max_commission_total` from SQL,
while the C# commission rules carry their own configured limit - and there is
no shared runtime wiring binding the two to a single value; they also operate
on fundamentally different figures available at fundamentally different times.
**These two computations will not always agree, and that disagreement is
intentional** - forcing them
into one implementation would either blind the gate (no pre-fill number) or
misreport the circuit breaker (a guess instead of the real figure). The gate
keeps the estimate; the circuit-breaker path keeps the actual.

One estimate edge is kept deliberately for parity: a **market** order with no
supplied price, on a security with no last-traded price to fall back on,
contributes a **zero** new-order estimate (so it can be accepted on the
new-order term alone), exactly matching the retired SQL's `ISNULL(@est_price, 0)`
handling. This is a documented parity limitation, not a bypass - moving to a
conservative fallback or an outright rejection would be a deliberate product
decision, so it is preserved rather than silently changed.

### Position recompute and the (eliminated) double-count hazard

The old design carried a latent hazard. `trg_Trades_PositionRecalc` fired on
every `INSERT` into `dbo.Trades` and called `usp_RecalculatePositionOnTrade`,
but that proc was **also** exposed standalone (it predated the trigger, and
overnight reconciliation jobs called it directly). Applying both to the same
trade double-counted it against `Positions.qty` / `avg_price` /
`realized_pnl`, and nothing in the schema prevented it - it relied on whoever
wrote the next batch job knowing not to.

The refactor eliminates the hazard by design. There is no auto-recompute
trigger any more; recompute is a single, explicit C# call.
`SqlLegacyOrderGateway.RecordTradeAsync` inserts the trade and then calls
`PositionRecalculationService` **exactly once**, inside the very transaction
that inserted the trade, so there is one unambiguous source of recompute. The
service handles signed-quantity accumulation, weighted-average price on
same-sign/flat accumulation, and realized P&L on the closed portion for the
partial-close, exact-close, and full-close-and-flip cases.

As before, `unrealized_pnl` on `Positions` is left untouched by the recompute:
it needs a live market price, which the recompute path does not have. It is
refreshed by the EOD mark-to-market batch (not part of this layer). Treat that
column as stale/EOD-only, not real-time, if you read it.

### The pre-trade gate checks committed state, not reserved state (no cross-order reservation)

The gate validates each order against the **committed** SQL state at validation
time: the live `Positions.qty` for the position-size projection, and the summed
**actual fills** in `dbo.Trades` for the commission estimate. It does **not**
reserve the exposure of accepted-but-unfilled orders, so two or more orders that
are each validated before any of them commits are all measured against the same
pre-existing state. Under a burst of concurrent submissions this means the
*aggregate* accepted-but-unfilled exposure can exceed a ceiling even though no
single order does (e.g. four BUY 30s can each pass a `max_position_size` of 100,
for 120 accepted-but-unfilled).

This is deliberate, and it matches the layer it replaces, rule for rule:

- The retired `usp_ValidatePreTradeRisk` **RULE 5** read the **committed**
  position (`SELECT @currentQty = qty FROM dbo.Positions`) and projected only
  *this* order onto it
  (`ABS(ISNULL(@currentQty, 0) + @signedDelta) >= @max_position_size`) - it never
  counted other in-flight orders.
- **RULE 6** read the commission base from **actual fills**
  (`SUM(t.qty * t.price) FROM dbo.Trades`) - likewise blind to
  accepted-but-unfilled orders.

The canonical C# gate is a faithful port of exactly that behaviour, so it is at
least as strict as **both** originals: the stricter-wins constraint governs each
rule's per-order *comparison* (which is preserved verbatim), and since neither
original reserved, the canonical form is not looser than either. AAP §0.6.4
spells the projected-position semantics out as *"current + signed order
quantity"* - a single-order projection, explicitly **not** an aggregate
reservation - and the minimal-change clause (§0.7.2) scopes this pass to
relocation and reconciliation, not to adding a brand-new cross-order
reservation/settlement feature. True reservation would need a reserved-exposure
ledger keyed by order, released on fill **or** cancel, plus a matching commission
reservation - none of which existed on either side to reconcile. It is therefore
recorded here as a known, by-design limitation and left for a separate,
deliberate enhancement rather than being introduced silently under a
consolidation pass.

The two stateful rules that *do* accumulate across accepted orders continue to,
because their **originals** read from `dbo.Orders` (status-filtered), not from
positions or fills: order **frequency** (a rolling `COUNT(*)` of recent orders)
and **daily traded volume** (today's accepted/filled qty plus the new order).
Those are per-portfolio aggregates by original design and are unaffected by the
non-reservation of position and commission.

Separately, the gateway records a fill **only** against an order in a
**fillable** state (`ACCEPTED` / `PARTFILLED` / `FILLED`): `RecordTradeAsync`
reads and row-locks the order's status inside the same serializable transaction
as the trade insert, so a `REJECTED`, `CANCELLED` or still-`PENDING` order can
never drive a phantom trade or position change - the request is refused
atomically, writing neither a trade row nor a position effect.

### Cross-cutting strictness conventions

These subtle conventions are preserved verbatim so consolidation neither
loosens nor tightens behaviour by accident:

- **`>=` ("meets or exceeds") reject boundary.** Every canonical rule rejects
  when the value *equals* the limit, not only when it strictly exceeds it.
  Switching any check to `>` would loosen the control, so `>=` is preserved
  everywhere.
- **`NULL`/`0` means "not enforced".** A null or exactly-zero threshold
  disables that check - the same convention the SQL `RiskLimits` rows and the
  C# rules share. This holds on **both** enforcement paths: the pre-trade gate
  skips a zero/absent limit, and the circuit-breaker rules (price, quantity and
  position size) each early-return "not triggered" on a zero threshold rather
  than treating `0` as an infinitely-strict ceiling.
- **A negative threshold is invalid configuration and fails closed.** Rather
  than silently disabling a control, the pre-trade gate rejects (fails closed)
  on a negative limit; a half-configured frequency limit (one of
  count/window set, the other not) likewise fails closed.
- **Most-specific `RiskLimits` precedence.** The applicable limits row is
  chosen by precedence - portfolio+security, then portfolio-only, then
  security-only - ordered by `effective_date` descending. This selection now
  lives in `PreTradeRiskService`.
- **Input pre-checks.** `side` must be Buy/Sell and `qty` must be positive;
  these are validated in the gate before any threshold check (a malformed
  request is returned without persisting anything). Only `OrderTypes.Limit`
  and `.Market` map to a stored `order_type`.
- **Decimal scale.** The gate validates that every qty/price/value input lies
  within the schema's `DECIMAL(18,4)` range and then compares the **raw**
  decimal values - it does **not** round before comparing, so it matches the
  un-modified canonical rule classes exactly (an out-of-range input fails
  closed). `PositionRecalculationService` rounds its *results* to `DECIMAL(18,4)`
  (commission rate to `DECIMAL(9,6)`) for **persistence** only - to match the
  column scale the retired proc wrote - never as part of an accept/reject
  comparison.

## Half-migrated persistence

`SqlLegacyOrderGateway` is not a full `IEntityRegistry` implementation - it is
an adapter that sits *alongside* `Csv.CsvEntityRegistry`. Orders, trades, and
positions go through SQL; securities, exchanges, and subscriptions are still
served by the existing CSV storage. `EnsurePortfolioAsync` /
`EnsureSecurityAsync` match rows by name / (code, board) because there is no
shared identifier between `BusinessEntities.Portfolio`/`Security` and their SQL
counterparts - if a portfolio is renamed on one side, this silently starts
creating a second row on the other. This entity-storage split is unchanged by
the risk consolidation and is unrelated to it.

## Known rough edges (left in on purpose)

- `qty` in every SQL table vs. `Volume`/`Quantity` on the C# `Order`/`MyTrade`
  objects. The two ceilings that use it (`RiskOrderVolumeRule`,
  `RiskDailyVolumeRule`) now share one canonical definition, so the *rule* no
  longer diverges - but the column/property **name** difference was left as-is
  and reconciled only in docs, because the column name is baked into the schema.
- `Orders.external_transaction_id` correlates a row back to the in-memory
  `Order.TransactionId`. It is a plain (non-unique) correlation column, not an
  idempotency key - it is nullable and was never back-filled for rows inserted
  before it was added. `RecordTradeAsync` likewise has **no replay protection**:
  calling it twice for the same fill inserts two `dbo.Trades` rows and applies
  the position effect twice, by design. Duplicate fills are deliberately **not**
  inferred from value equality (two genuine fills can legitimately share the same
  qty/price); if replay protection is ever required it must come from an explicit
  external trade id plus a unique constraint, not from guessing.
- Only `OrderTypes.Limit`/`.Market` map to `dbo.Orders.order_type`; sending a
  conditional/stop order through `SqlLegacyOrderGateway` throws
  `NotSupportedException`. Conditional orders were out of scope for this pass.
- The pre-trade gate carries an optional `requestedBy` parameter for parity
  with the retired proc's `@requested_by` argument, but it is not read by any
  decision - it is threaded through and ignored, exactly as the SQL proc used
  to ignore it. (The compliance ask that motivated it - tag every risk check
  with who initiated it - was descoped.)

The following rough edges from the old, two-engine world **no longer apply**
and are recorded here only so nobody goes looking for them: the "SQL-only
`max_order_value` / `max_daily_volume`, no C# rule checks these" note (both are
now first-class C# rules), and the stale "compliance review tightened the
window to 30 seconds" comment that lived in `usp_ValidatePreTradeRisk` (the
proc is gone, and the frequency window has been config-driven via `RiskLimits`
for a long time).

## Running it

See `Database/README.md` for the Docker command and the script run order
(`001` -> `002` -> `003` -> `004`), and `Samples/08_Misc/03_LegacySqlDemo` for
a working end-to-end example. The demo, and the scenarios below, were run
against a real `mcr.microsoft.com/mssql/server` container while building this,
not just reviewed by eye:

- an order within every configured `RiskLimits` ceiling is **accepted**;
- an order breaching `max_order_price` is **rejected with a reason** - the
  reason is now decided in C# by `PreTradeRiskService` (the per-order gate),
  not by any SQL procedure;
- recording a trade against an accepted order updates the position
  (quantity / average price / realized P&L) through the single
  `PositionRecalculationService` call the gateway makes per `RecordTradeAsync`
  - there is no auto-recompute trigger, so the position is recomputed exactly
  once end-to-end in C#;
- the order's status-change audit row is still written by
  `trg_Orders_StatusAudit`, the one trigger that remains in SQL.

Because this layer previously had no automated tests - its correctness was
demonstrated only through the runnable sample and manual container runs - the
consolidation also added net-new coverage in `Tests/RiskTests.cs`. These
include characterization tests that pin the pre-consolidation behaviour of the
reconciled rules and parity tests that exercise each rule's chosen canonical
behaviour (or its intentionally-distinct one), with the frequency-boundary,
position-size timing, and commission estimate-vs-actual cases called out
specifically. The suite is not an exhaustive proof of every path; it targets
the reconciliation decisions this document records.

Run this layer's tests with a filter -
`dotnet test StockSharp_Tests.slnx -c Release --filter FullyQualifiedName~RiskTests` -
rather than the whole `Tests.csproj`. The full unfiltered suite does **not**
complete cleanly in the container, but for reasons **outside** this layer and
outside this refactor's scope: `Tests/PathsTests.cs` makes container-specific
path assertions that do not hold here, and `Tests/AsyncMessageChannelTests.Close_StopsProcessing`
reproducibly hangs. Both live in unchanged platform code
(`Configuration/Paths.cs`, `Messages/AsyncMessageChannel.cs` and their tests)
that the minimal-change scope explicitly does not touch; they are pre-existing
and environmental, not regressions from this work. The `RiskTests` class - which
covers everything this document describes - runs green on its own.
