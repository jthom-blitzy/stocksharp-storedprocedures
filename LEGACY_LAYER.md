# The SQL layer (StockSharpLegacy)

Owner: whoever's on call for order/position issues, in practice. There's no
formal owner for this database - it grew out of the risk desk needing
pre-trade checks the C# rule engine wasn't fast enough to change without a
deploy, and it was added to piecemeal for years.

This document exists because the last three "why did this order get
rejected" tickets all turned out to be a disagreement between the C# risk
engine and a *second*, parallel risk engine that lived in T-SQL, and nobody
could point at a single place that explained both. That split is now gone.
The risk decisioning that used to live in stored procedures has been
consolidated into unit-tested C# services, every limit is defined exactly
once, and SQL Server is back to being pure data storage. This is still the
place to read first if you're new to the codebase - but now it describes one
set of rules, not two that quietly disagree.

## What's here

- `/Database` - schema (`001_Schema.sql`), stored procedures
  (`002_StoredProcedures.sql`, now a no-logic script that just drops the
  relocated procs), triggers (`003_Triggers.sql`, now only the audit
  cascade), seed data (`004_SeedData.sql`). Runnable against a fresh SQL
  Server instance - `Database/README.md` has the Docker one-liner.
- `Algo/Risk/` - the consolidated business logic. `RiskLimitSet` is the
  canonical threshold definition (the single source of truth); `PreTradeRiskService`
  is the seven-check per-order gate (the C# port of `usp_ValidatePreTradeRisk`);
  `PositionRecalculationService` is the average-cost / realized-P&L math (the
  C# port of `usp_RecalculatePositionOnTrade`); and the `Risk*Rule` family is the
  portfolio-wide circuit breaker, including the two rules
  (`RiskOrderValueRule`, `RiskDailyVolumeRule`) that used to exist only in SQL.
- `Algo/Storages/Sql/` - `SqlLegacyOrderGateway`, the ADO.NET (not Dapper, not
  EF - raw `SqlCommand`) client. It is now a pure CRUD adapter: it delegates
  every decision to the C# services above and only does parameterized
  `INSERT`/`SELECT`. Plus `SqlLegacyConnection` for connection-string
  resolution and `SqlPosition`/`SqlOrderSubmitResult` DTOs.
- `Samples/08_Misc/03_LegacySqlDemo` - a runnable walkthrough: submit a
  compliant order, submit one that gets rejected, record a fill, watch the
  position get recomputed. The three observable outcomes are unchanged; only
  the machinery behind them moved from SQL into C#.
- Comments on `Algo/Risk/RiskManager.cs` and `Algo/Risk/RiskOrderFreqRule.cs`
  now describe the consolidated state rather than pointing at a disagreement.

## Two enforcement patterns, one set of rules

There used to be two risk *engines* that never both ran for a given order and
did not agree. There are still two enforcement **patterns** - and that is
deliberate, per AAP §0.6 - but they are no longer defined independently. Both
now resolve their thresholds from the same canonical `RiskLimitSet`, so a
limit is written down once and consumed in two places:

- **`Algo.Risk.RiskManager`** evaluates rules against the message stream and,
  when one trips, takes a portfolio-wide action - `ClosePositions`,
  `StopTrading`, or `CancelOrders` (see `RiskMessageAdapter.ProcessRiskAsync`).
  It does not reject the specific order that tripped the rule; it's a
  circuit breaker, not a gate. Its action behaviour is unchanged by the
  consolidation. Call `RiskManager.ApplyCanonicalLimits(RiskLimitSet)` to seed
  its circuit-breaker rules from the shared definition.
- **`Algo.Risk.PreTradeRiskService`** is a gate: it evaluates one order before
  it's accepted and rejects that order, specifically, if it fails. It is what
  `SqlLegacyOrderGateway.SubmitOrderAsync` calls, inside the same transaction
  that then inserts the order row - so validation and the write are atomic.

Anyone asking "does this order get risk-checked" still needs to know which
path it's going through. Going through `SqlLegacyOrderGateway.SubmitOrderAsync`
gets you the pre-trade gate. Going through a `Connector`/`RiskMessageAdapter`
pipeline gets you the circuit breaker. **Nothing runs both** - but whichever
one runs, it is reading the *same* canonical limits, so they can no longer
disagree about a threshold.

### Rule-by-rule coverage

Every threshold below is defined once on `RiskLimitSet`. "Merged" means the C#
rule and the old SQL check were duplicative-by-accident and collapsed to one
definition. "Preserved-distinct" means the two evaluation contexts are
genuinely different (they answer different questions) and were intentionally
*not* force-merged under DRY (AAP §0.6.2) - they share the one threshold but
keep two implementations.

| Check | Canonical limit (`RiskLimitSet`) | Circuit breaker (`Algo/Risk`, stream) | Pre-trade gate (`PreTradeRiskService`, per-order) | Status |
|---|---|---|---|---|
| Order price ceiling | `MaxOrderPrice` | `RiskOrderPriceRule` | check 1 | **Merged.** Both reject when `price >= limit`. |
| Order qty ceiling | `MaxOrderQty` | `RiskOrderVolumeRule` | check 2 | **Merged.** Both reject when `qty >= limit`. |
| Order notional value (qty × price) | `MaxOrderValue` | `RiskOrderValueRule` *(new)* | check 3 | **Was SQL-only.** Now a first-class C# rule; both reject when `qty*price >= limit`. |
| Order frequency | `MaxOrderFreqCount` / `MaxOrderFreqWindowSeconds` | `RiskOrderFreqRule` | check 4 | **Reconciled to the stricter algorithm** - rolling count, see below. |
| Resulting position size | `MaxPositionSize` | `RiskPositionSizeRule` | check 5 | **Preserved-distinct.** The rule checks the *current* position from a `PositionChangeMessage`; the gate checks the *hypothetical post-fill* position, because it runs pre-trade. |
| Cumulative commission | `MaxCommissionTotal` (+ `CommissionRate`) | `RiskCommissionRule` / `RiskOrderCommissionRule` / `RiskTransactionCommissionRule` | check 6 | **Preserved-distinct.** The rules track the *actual* commission reported on `ExecutionMessage` post-fill; the gate *estimates* it pre-fill from historical traded notional × rate. Same threshold, different basis - so they do not agree by construction. |
| Daily traded volume | `MaxDailyVolume` | `RiskDailyVolumeRule` *(new)* | check 7 | **Was SQL-only.** Now a first-class C# rule (an in-stream running total, partitioned by portfolio+security and rolled over on the UTC day); the gate reads the persisted accepted/filled/part-filled volume for the current UTC day. Preserved-distinct evaluation contexts sharing the one threshold. |
| Position lifetime, P&L limit, slippage | (rule-owned) | `RiskPositionTimeRule`, `RiskPnLRule`, `RiskSlippageRule` | - | **Circuit-breaker-only.** These need live state the pre-trade gate doesn't have; unchanged. |

The frequency check is the one worth calling out specifically. It used to be
the clearest disagreement: `RiskOrderFreqRule` bucketed time into fixed,
non-overlapping windows (a burst that straddles a bucket boundary could dodge
the limit), while the SQL version ran a true rolling `COUNT(*)` over "now
minus N seconds", which is strictly stricter near a boundary. The
consolidation resolved this by adopting the **rolling count in C#** - the
never-less-strict choice. `RiskOrderFreqRule` now counts the prior orders
still inside the trailing `Interval` window and rejects once that count plus
the current order reaches `Count`, exactly like the gate. To stay
"never less strict than a correct rolling count" under out-of-order events,
the rule tracks a high watermark and treats a strictly-earlier (late) event as
a breach rather than silently under-counting it. This is the single
intentional behaviour change in the whole refactor, and it makes the two
patterns agree on frequency instead of disagreeing.

## The position update is now single-apply (hazard removed)

There used to be a genuine hazard here. `trg_Trades_PositionRecalc` fired on
every `INSERT` into `dbo.Trades` and called `usp_RecalculatePositionOnTrade`;
that proc was *also* exposed standalone, so an overnight reconciliation job
that called it directly against trades that had already fired the trigger
double-counted the trade against `Positions.qty` / `avg_price` /
`realized_pnl`. Nothing in the schema prevented it.

That duality is gone. `PositionRecalculationService` is now the single entry
point, and `SqlLegacyOrderGateway.RecordTradeAsync` invokes it exactly once
per recorded trade, inside the same gateway-owned transaction that inserts the
trade, taking `UPDLOCK`/`HOLDLOCK` on the `Positions` row so concurrent fills
serialize. The apply is idempotent: it recomputes the position deterministically
from the full persisted trade set for that portfolio+security rather than
folding a delta, so re-running it cannot double-apply. `trg_Trades_PositionRecalc`
and its calculation logic have been removed. There is a live test
(`Live_ApplyTradeIsIdempotent`, plus the gateway end-to-end and concurrent-fill
tests) that proves applying the same trade twice leaves the position at its
single-apply value, not double.

`unrealized_pnl` on `Positions` is still not maintained here - it needs a live
market price, which this path doesn't have. It's refreshed by the EOD
mark-to-market batch (not part of this brief). Treat it as stale/EOD-only, not
real-time, if you're reading that column.

## What moved, and the two disposition choices

The SQL business logic was relocated as follows (the header comment in each
script records the same thing at the point of change):

- `usp_ValidatePreTradeRisk` → `Algo/Risk/PreTradeRiskService.cs` (the
  seven-check gate, resolving thresholds from `RiskLimitSet`, including the
  most-specific limit-row selection and the unlimited-when-all-NULL
  short-circuit).
- `usp_RecalculatePositionOnTrade` → `Algo/Risk/PositionRecalculationService.cs`
  (average-cost weighted branch + realized-P&L branch for close/partial/flip).
- `usp_SubmitOrder` → **removed entirely.** Choice recorded (AAP §0.4.1 /
  §0.6.3): the gateway already owns the transaction that must span validation +
  `INSERT` atomically (finding C03 / CWE-367 TOCTOU), so a pass-through proc
  would add a round-trip and a second place to keep in sync without adding
  value. The order "front door" is now
  `SqlLegacyOrderGateway.SubmitOrderAsync`.
- `trg_Trades_PositionRecalc` → **removed;** the per-trade recalculation is
  driven from C# as described above.
- `trg_Orders_StatusAudit` → **kept, as a pure audit cascade** (Option A, AAP
  §0.6.5). It only inserts a row into `OrderStatusHistory` when an order's
  status changes; it contains no thresholds, no accept/reject decision, and no
  P&L math, so it is defensible CRUD and preserves the append-only history
  guarantee. It was left in the database rather than relocated to C# because a
  status-change-to-history insert is the lowest-risk option and does not
  reintroduce business logic into SQL.

After `002_StoredProcedures.sql` and `003_Triggers.sql` run, no stored
procedure exists and the only trigger is the audit cascade - verifiable with
`sys.procedures` / `sys.triggers`. The DDL is left plain and vendor-neutral so
a future PostgreSQL/Aurora migration is unobstructed (that migration is
explicitly *not* part of this brief).

## Half-migrated persistence

`SqlLegacyOrderGateway` is still not a full `IEntityRegistry` implementation -
it's an adapter that sits alongside `CsvEntityRegistry`. Orders, trades, and
positions go through SQL; securities, exchanges, and subscriptions are still
served by the existing CSV storage. `EnsurePortfolioAsync` /
`EnsureSecurityAsync` match rows by name / (code, board) because there is no
shared identifier between `BusinessEntities.Portfolio`/`Security` and their
SQL counterparts - if a portfolio gets renamed on one side, this silently
starts creating a second row on the other. None of that changed in this
refactor; it's noted here so nobody assumes the consolidation touched it.

## Rough edges cleaned up during the extraction

These long-standing rough edges were addressed opportunistically while porting
the logic (AAP §0.6.6):

- The unused `@requested_by` parameter (added for a descoped compliance ask,
  never read in the proc body) is gone - it does not exist in the C# gate.
- The stale frequency-check comment referencing a "compliance review, last
  year" that hardcoded a 30-second window is gone; the window is config-driven
  from `RiskLimits.max_order_freq_window_sec` / `RiskLimitSet.MaxOrderFreqWindowSeconds`.
- `qty` (SQL columns) vs `Volume`/`Quantity` (C# `Order`/`MyTrade`) is kept
  consistent in the C# port - the service methods take a `volume`/`qty`
  argument and document the mapping rather than silently mixing the two.
- `Orders.external_transaction_id` still exists, is still nullable, and is
  still un-backfilled for old rows - left as-is; it's a correlation aid, not
  business logic.
- Only `OrderTypes.Limit`/`.Market` map to `dbo.Orders.order_type`; sending a
  conditional/stop order through the gateway still throws
  `NotSupportedException`. Conditional orders were out of scope for this pass.

## Running it

See `Database/README.md` for the Docker command and script run order, and
`Samples/08_Misc/03_LegacySqlDemo` for a working end-to-end example. Every
scenario described above (accept, reject-by-price, trade-recomputes-position,
status-change-triggers-audit-row) has been run against a real
`mcr.microsoft.com/mssql/server` container while building this, and the C#
services have characterization + parity tests (`Tests/PreTradeRiskServiceTests.cs`,
`Tests/PositionRecalculationTests.cs`, `Tests/CommissionTests.cs`,
`Tests/RiskTests.cs`) that assert their behaviour against that live database,
not just by eye.
