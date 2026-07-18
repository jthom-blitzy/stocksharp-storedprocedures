# The SQL layer (StockSharpLegacy)

Owner: whoever's on call for order/position issues, in practice. There's no
formal owner for this layer - it grew out of the risk desk needing pre-trade
checks the C# rule engine wasn't fast enough to change without a deploy, and it
got added to piecemeal for a while.

This document exists because the last three "why did this order get rejected"
tickets all turned out to be a disagreement between the C# risk engine and the
SQL risk engine, and nobody could point at a single place that explained both.
So that got fixed: the business logic that used to live in SQL stored
procedures has been pulled out into C#, and the rules the two engines share now
key off one canonical definition in `CanonicalRiskRules` - most concretely the
rolling-window frequency evaluator, which the pre-trade gate and the circuit
breaker literally both call, plus the shared comparison convention (`>=`
meets-or-exceeds, and NULL-or-0 = "not enforced"). If you're new to this
codebase, read this before you go looking for "the" risk check. The pre-trade
gate and the circuit breaker are still two different patterns (a per-order gate
vs. a portfolio-wide breaker), and for most checks each keeps its own values and
its own subject; what changed is that the one rule they used to implement twice
and disagree on - order frequency - now runs through a single shared evaluator,
so they can no longer disagree on the frequency *arithmetic* the way they used to.
They still read different state (database rows vs. an in-memory message stream)
and take different actions, so their outcomes can still differ - the rule-by-rule
table below is precise about exactly what is shared and what is not.

While that consolidation happened the persistence tier moved too: the database
is PostgreSQL now (Npgsql, not `Microsoft.Data.SqlClient`), and the whole thing
- database plus the console demo - comes up with a single `docker compose up`.
The schema is pure storage again: tables, constraints, indexes, and exactly one
pure-audit trigger. No business logic runs in the database anymore.

## What's here

- `/Database` - native PostgreSQL 16 DDL only, exactly the three init scripts the
  AAP freezes: schema (`001_Schema.sql`), triggers (`003_Triggers.sql`, the
  status-audit trigger only), and seed data (`004_SeedData.sql`).
  **`002_StoredProcedures.sql` is gone** - all of its business logic moved into
  C# under `Algo/Risk/`, so the `002` slot is just a harmless numbering gap now.
  `Database/README.md` has the `docker compose` one-liner and the full
  init-script story.
- `Algo/Risk/` - this is where the business logic lives now. `PreTradeRiskService`
  is the per-order accept/reject gate (the C# re-expression of the retired
  `usp_ValidatePreTradeRisk`), `PositionRecalculationService` does the
  average-cost position accounting (the re-expression of the retired
  `usp_RecalculatePositionOnTrade`), and `CanonicalRiskRules` is the single
  source of truth for the shared thresholds convention and the rolling-window
  frequency evaluator. The existing circuit-breaker files (`RiskManager`,
  `RiskOrderFreqRule`, and the other `Risk*Rule` classes) are still here and now
  consume those canonical definitions wherever a rule is shared.
- `Algo/Storages/Sql/` - `SqlLegacyOrderGateway` is now a **pure** data-access
  gateway: raw ADO.NET (not Dapper, not EF - raw `NpgsqlCommand`) that makes no
  accept/reject or P&L decisions of its own. It delegates validation to
  `PreTradeRiskService` and position recomputation to
  `PositionRecalculationService`. Alongside it: `SqlLegacyConnection` resolves a
  PostgreSQL connection string, and the `SqlPosition` / `SqlOrderSubmitResult`
  DTO shapes are unchanged.
- `Samples/08_Misc/03_LegacySqlDemo` - a runnable walkthrough, now against
  PostgreSQL and the consolidated services: submit a compliant order, submit one
  that gets rejected, record a fill, watch the position update automatically.
- `Tests/` - the parity and characterization suites (`PreTradeRiskParityTests`,
  `PositionRecalculationTests`) that prove the consolidation. They run the AAP
  0.6.3 staged four-step sequence, which needs a *baseline* of the original SQL
  Server behaviour to compare against; that baseline schema+seed is the embedded
  resource `Tests/Resources/LegacySqlServer/OriginalCharacterizationSetup.sql`
  (bundled via the `Resources\LegacySqlServer\*.sql` EmbeddedResource glob in
  `Tests.csproj`). It is inventoried here because it is essential to the staged
  tests, even though it is not one of the `/Database` runtime init scripts.

## One canonical source of truth (and two patterns that share it)

`Algo/Risk` (C#) came first. `usp_ValidatePreTradeRisk` (SQL) was added later,
when the desk wanted to change a limit without waiting on a release, and for a
long time the two were independent implementations of overlapping rules that
drifted apart. That is the part that got fixed. The SQL procedure is retired;
its logic now lives in C# as `PreTradeRiskService`, and the rules that both
sides genuinely share are defined exactly once in `CanonicalRiskRules`.

There are still two *patterns* here, because they answer two different
questions:

- **`PreTradeRiskService`** is a gate. It evaluates one order before it's
  accepted and rejects that order, specifically, with a reason, if it fails. It
  is the C# re-expression of the retired `usp_ValidatePreTradeRisk` and it's
  what `SqlLegacyOrderGateway.SubmitOrderAsync` calls. Unlike the in-memory
  circuit-breaker rules it is *database-state-aware* - it has to read the
  `Positions` table, the recent `Orders`, and today's traded volume to evaluate
  the position, frequency, and daily-volume checks.
- **`Algo.Risk.RiskManager`** is a circuit breaker. It evaluates rules against
  the message stream and, when one trips, takes a portfolio-wide action -
  `ClosePositions`, `StopTrading`, or `CancelOrders` (see
  `RiskMessageAdapter.ProcessRiskAsync`). It does not reject the specific order
  that tripped the rule; whatever tripped it typically still goes out, unless
  `StopTrading` was already in effect from an earlier violation. That
  portfolio-wide action behavior is intentionally **unchanged** by this work -
  it was out of scope, and nothing about the mechanics of `ProcessRiskAsync`
  moved.

The important change is order frequency - the one rule the two sides used to
implement twice and disagree on. It used to be two separate code paths (a fixed
bucket in C#, a rolling `COUNT(*)` in SQL) that could give different answers for
the same burst; now the gate and `RiskOrderFreqRule` both call the one
rolling-window evaluator in `CanonicalRiskRules`. The shared surface is precise
and narrow: the rolling-window count definition, the `>= Count` meets-or-exceeds
threshold, and the "window must be positive to be enforced" convention. Given the
SAME observed events the two now compute the SAME in-window verdict, so they can
no longer disagree on the frequency *arithmetic*. What they do **not** share, by
design, is everything around that arithmetic - and it is enough that the two can
still reach different outcomes on the same real order flow:

- **Data source.** The circuit breaker feeds the evaluator an in-memory buffer of
  message-stream timestamps; the gate queries the `Orders` table at evaluation
  time.
- **Rejected-order handling.** The gate's `COUNT(*)` includes every recent order
  row regardless of status (rejected orders count toward the window); the circuit
  breaker only ever sees what actually flowed through the message stream.
- **State & lifecycle.** The circuit breaker keeps its buffer and RESETS it when
  it trips; the gate is stateless per call and re-derives the count from the
  database every time.
- **Reaction.** The circuit breaker takes a portfolio-wide action
  (`ClosePositions` / `StopTrading` / `CancelOrders`); the gate rejects the single
  offending order.

That shared evaluator, and the comparison convention it applies, are the concrete
"single source of truth" here; the other shared-name checks are more subtle, and
the table below says exactly how. Anyone asking "does this order get
risk-checked" still needs to know which path it went through -
`SqlLegacyOrderGateway.SubmitOrderAsync` runs the gate; a `Connector` /
`RiskMessageAdapter` pipeline runs the circuit breaker - and, even for the
frequency rule they genuinely share, "consolidated" means they can no longer
disagree on the *arithmetic*, not that they became the same check: the state each
feeds it, and the action each takes, still differ by design.


### Merged, preserved, or promoted: the rule-by-rule verdict

When the SQL checks came across, each one that overlapped a C# rule had to be
classified: was it the *same* check written twice by accident (merge it), or a
*deliberately different* check that happens to share a name (keep both)? A few
checks only ever existed on the SQL side and had to be promoted to first-class
C# gate rules. Here's where each one landed.

| Check | C# rule | SQL check (retired) | Classification / Verdict |
|---|---|---|---|
| Order price ceiling | `RiskOrderPriceRule` | `max_order_price` | Same semantics → **gate re-expresses it via the canonical `>=`/NULL-or-0 convention; the circuit-breaker `RiskOrderPriceRule` keeps its own value** |
| Order qty ceiling | `RiskOrderVolumeRule` | `max_order_qty` | Same semantics (also unifies qty/Volume naming) → **gate re-expresses it via the canonical convention; `RiskOrderVolumeRule` keeps its own value** |
| Order frequency | `RiskOrderFreqRule` | `max_order_freq_count` / `_window_sec` | Same config, different algorithm → **merged to the stricter ROLLING evaluator** |
| Resulting position size | `RiskPositionSizeRule` | `max_position_size` | Different subject: current (C#) vs hypothetical post-fill (gate) → **shared threshold, context-specific subject** |
| Cumulative commission | `RiskCommissionRule` / `RiskOrderCommissionRule` | `max_commission_total` | Actual (C#, post-fill) vs estimate (gate, pre-fill) → **different-by-design: preserve BOTH, document why** |
| Order notional value (qty × price) | (none before) | `max_order_value` | SQL-only → **promoted to a first-class C# gate rule** |
| Daily traded volume | (none before) | `max_daily_volume` | SQL-only → **promoted to a first-class C# gate rule** |
| Position lifetime / P&L limit / slippage | `RiskPositionTimeRule` / `RiskPnLRule` / `RiskSlippageRule` | (none) | C#-only, needs live streaming state → **remain in the circuit breaker** |

One thing the "merged" verdicts above do **not** mean: they do not mean the
circuit-breaker rules were rewired to read canonical configuration. In code,
the single genuinely shared piece of rule *logic* is the rolling-frequency
evaluator - both the gate and `RiskOrderFreqRule` call it - plus the gate's
comparison convention (`>=`, and NULL-or-0 = unlimited) that `CanonicalRiskRules`
codifies. For price, qty, and position the gate re-expresses the retired SQL
check through that convention against the seeded `RiskLimits` values, while the
circuit-breaker `RiskOrderPriceRule` / `RiskOrderVolumeRule` / `RiskPositionSizeRule`
keep their own independent values, comparisons, subjects, and lifecycle. What was
eliminated is the *frequency* divergence and the two-implementations-of-one-check
problem for the checks the gate now owns - not every circuit-breaker rule folding
into one shared configuration object.

Three of these are worth spelling out, because "merge" was the wrong answer for
two of them and the frequency merge changed behavior on purpose.

**Order frequency - merged, and made stricter on purpose.** The old
`RiskOrderFreqRule` bucketed time into fixed, non-overlapping windows: a burst
that straddled a bucket boundary (say four orders at the tail of one bucket and
four at the head of the next) could dodge a "five per window" limit that a true
rolling window would have caught. The SQL side ran a real rolling `COUNT(*)`
over "now minus N seconds", which was strictly stricter near a boundary. The
canonical evaluator in `CanonicalRiskRules` now implements the rolling
semantics for *both* sides: keep the timestamps of recent orders, count the
ones inside the trailing `max_order_freq_window_sec`, add the prospective order
(`+1`), and reject on `>=`. That satisfies the hard rule that a reconciled
threshold may never end up *less* strict than the stricter of the two originals
- so the fixed-bucket behavior was retired rather than kept. Both the gate and
`RiskOrderFreqRule` consume this one evaluator.

**Resulting position size - shared threshold, different subject.** This one is
*not* a naive merge, because the two sides are asking about different things.
The gate is a pre-trade check, so it evaluates the *hypothetical post-fill*
position (current signed qty + the signed order qty) and rejects if that would
meet or exceed the limit. The circuit-breaker `RiskPositionSizeRule` evaluates
the *current* position off a `PositionChangeMessage`. Only the *meaning* of the
check and the comparison direction line up; each side carries its own configured
value and its own subject, because each subject is the right one for its job.

**Cumulative commission - different by design, both kept.** The gate estimates
commission *before* the fill (`qty * price * commission_rate`) because that's
all it can know pre-trade. The circuit breaker accumulates the *actual*
commission off `ExecutionMessage.Commission` *after* the fill. A forecast and a
realized figure are not going to agree, and pretending they're one check would
be wrong - so both are preserved, and this note is why.


## The single-apply invariant (what replaced the position-update hazard)

There used to be a genuine footgun here. `trg_Trades_PositionRecalc` fired on
every `INSERT` into `Trades` and called `usp_RecalculatePositionOnTrade`, but
that proc was *also* exposed standalone - some overnight reconciliation jobs
called it directly against trades that had already fired the trigger, which
double-counted the trade against `Positions.qty` / `avg_price` /
`realized_pnl`. Nothing in the schema prevented it; it relied on whoever wrote
the next batch job knowing not to.

That whole hazard is addressed now, and it's worth being precise about *how* -
because removing the trigger is necessary but not by itself sufficient. Three
things combine:

1. **The trigger was removed** (see `Database/003_Triggers.sql`, which now keeps
   only the pure status-audit trigger), so no database-side applier fires on a
   `Trades` insert - that eliminates the old duplicate path.
2. **Transactional atomicity.** `SqlLegacyOrderGateway.RecordTradeAsync` inserts
   the trade and then calls `PositionRecalculationService` inside the *same*
   transaction, serialized per portfolio by a `pg_advisory_xact_lock`, so a trade
   and its position effect commit together or not at all and two trades for one
   portfolio can't interleave.
3. **A durable idempotency guard.** `Trades.position_applied` is a boolean the
   service flips with an atomic conditional `UPDATE ... WHERE trade_id = @id AND
   position_applied = false`; if that matches zero rows the apply is skipped.
   Because the flag lives in the database, a repeat apply of the same `trade_id`
   is a no-op even across a process restart or a second gateway/service instance -
   not just within one process.

Together those give a single apply per trade that holds across retries, restarts,
and concurrent instances. It is deliberately **not** claimed "by construction"
from the trigger removal alone - the trigger removal only closes the database-side
path; the durable `position_applied` guard is what makes single-apply hold
cross-instance. All of it is covered by the `PositionRecalculationTests`
idempotency cases (duplicate apply, restart/retry, rollback-then-retry, and a
concurrent race).

`unrealized_pnl` on `Positions` is still not maintained by the recalculation
logic - it needs a live market price, which the service doesn't have. It's
refreshed by the EOD mark-to-market batch (not part of this brief), and the C#
service deliberately leaves that column untouched rather than invent a value.
Treat it as stale / EOD-only, not real-time, if you're reading that column.

The average-cost math itself carried over faithfully from the retired proc.
Adding to a same-sign position (or opening one from flat) takes a
volume-weighted average of the price. A trade on the opposite side realizes P&L
on the closed portion - `closingQty * (trade_price - avg_price) *
SIGN(existingQty)`, where `closingQty` is `MIN(ABS(existingQty), tradeQty)` -
and it handles the three cases you'd expect: an exact close (position goes
flat), a partial close (the average price is left as-is while a position
remains open), and a flip (the residual quantity opens a new position on the
other side at the trade price).


## Half-migrated persistence

`SqlLegacyOrderGateway` is not a full `IEntityRegistry` implementation - it's an
adapter that sits alongside `CsvEntityRegistry`. Orders, trades, and positions
go through the database now; securities, exchanges, and subscriptions are still
served by the existing CSV storage. `EnsurePortfolioAsync` /
`EnsureSecurityAsync` match rows by name / (code, board) because there is no
shared identifier between `BusinessEntities.Portfolio` / `Security` and their
database counterparts - if a portfolio gets renamed on one side, this silently
starts creating a second row on the other. Migrating to PostgreSQL didn't
change any of that; the split is the same, and so is the identifier gap.

## Known rough edges (left in on purpose)

- The retired `usp_ValidatePreTradeRisk` took an `@order_type` and a
  `@requested_by` parameter. The C# gate deliberately does **not** carry them
  over: `order_type` was never actually used by any check (a market order is
  detected by a null price), and `requested_by` was a compliance-tagging ask
  ("tag every risk check with who initiated it") that got descoped - the proc
  never read it. Dropping both keeps the gate's contract to exactly what it
  uses, so if you're looking for where the order type feeds the risk decision:
  it doesn't, and it didn't before either.
- The old frequency check had a comment referencing a "compliance review, last
  year" that had supposedly hardcoded the window to 30 seconds - which hadn't
  been true since the window became config-driven via `RiskLimits`. That's
  resolved now, not just re-commented: the single rolling-window evaluator in
  `CanonicalRiskRules` reads `max_order_freq_window_sec` from the applicable
  limits row, and the stale comment went away with the fixed-bucket algorithm it
  described.
- `qty` in every database table vs. `Volume` / `Quantity` on the C#
  `Order` / `MyTrade` objects. Never fully reconciled in the storage layer - by
  the time anyone noticed, the column name was baked into the schema. The
  order-qty *check* means the same thing on both sides now (the retired SQL
  `max_order_qty` and the C# `RiskOrderVolumeRule`), but that is a shared
  *meaning*, not one shared implementation: the gate evaluates it through the
  canonical convention while `RiskOrderVolumeRule` keeps its own configured value.
- `Orders.external_transaction_id` exists to let a support engineer correlate a
  row back to the in-memory `Order.TransactionId`, but it's nullable and was
  never back-filled for anything inserted before it was added.
- Only `OrderTypes.Limit` / `.Market` map to `Orders.order_type`; sending a
  conditional / stop order through `SqlLegacyOrderGateway` throws
  `NotSupportedException`. Conditional orders were out of scope for this pass.


## Running it

One command, from the repository root:

```bash
docker compose up
```

That brings up two services. The `db` service is `postgres:16`; on first init
it auto-runs the `Database/*.sql` scripts (mounted read-only into
`/docker-entrypoint-initdb.d`) in alphabetical order -
`001_Schema.sql` → `003_Triggers.sql` → `004_SeedData.sql`
- and only reports healthy once it's actually accepting TCP connections, gated
by a `pg_isready` healthcheck. The `app` service (the console demo, built from
the repo-root `Dockerfile`) waits for `db` to become *healthy*
(`depends_on … condition: service_healthy`) before it connects, so the schema,
triggers, and seed data all exist by the time the demo runs.

The connection string is resolved by `SqlLegacyConnection.Resolve()`, which
reads the `STOCKSHARP_LEGACY_SQL_CONNECTION` environment variable and falls back
to a local PostgreSQL string when it's unset. That variable name was
**retained** across the SQL Server → PostgreSQL move on purpose - renaming it
would have been a bigger change than the migration warranted. Inside
`docker compose` the `app` service sets it to point at the `db` service by its
Compose DNS name:

```
Host=db;Port=5432;Database=stocksharp;Username=postgres;Password=postgres;GSS Encryption Mode=Disable;Maximum Pool Size=50
```

Those are dev-only credentials, deliberately not secret. `Maximum Pool Size=50`
bounds the Npgsql pool (default 100) against the `db` service's
`max_connections=200`, leaving headroom for a second instance, admin tooling, and
the healthcheck; the host fallback string uses the same cap. Per the frozen AAP
(0.4.2) the app connects with the `db` service's `POSTGRES_USER` /
`POSTGRES_PASSWORD` - the single `postgres` role that also runs the init scripts
and the healthcheck; there is no separate application role or extra init script.
`Database/README.md` is the authoritative reference for the compose commands, the
single-role setup, and the `docker compose down -v` re-init trick.

One repeatability note: the demo's exact output assumes a **clean** database.
Because the `pgdata` volume persists between runs, re-running the demo (or the
`app` service) against an already-seeded database accumulates the position (qty
100 → 200 → …) and the repeated submissions can trip the seeded 5-orders /
60-seconds frequency limit, changing the order ids and the accept/reject
outcomes. Run `docker compose down -v` immediately before any run whose output
you want to reproduce exactly.

Every scenario described above is exercised by the automated suites in `Tests/`
against a real containerized PostgreSQL 16 instance: accept-within-limits,
reject-by-price, reject-by-frequency, and the notional / daily-volume / position
checks live in `PreTradeRiskParityTests`; the recorded-trade position recalc and
its single-apply idempotency (duplicate apply, restart/retry, rollback-then-retry,
and a concurrent race) live in `PositionRecalculationTests`; and the pure
status-audit trigger writing an `OrderStatusHistory` row is covered specifically
by the `Audit_StatusChange_WritesHistoryRow_PureAuditOnly` characterization test
in `PreTradeRiskParityTests` (it asserts a row is written on a real status change
and that nothing else - price amendments, no-op same-status updates - writes one).
The runnable `Samples/08_Misc/03_LegacySqlDemo` walkthrough additionally
demonstrates the three OBSERVABLE outcomes end to end (accept within limits,
reject-by-price with a reason, and the automatic position update after a recorded
fill); it does not by itself exercise the frequency or audit paths - the suites
do. Those passing suites and the demo run are the evidence of record; `QA/` holds
captured artifacts (screenshots and transcripts) from real runs, indexed by
`QA/README.md`, as an illustration of that - not as a stand-in for the tests.
