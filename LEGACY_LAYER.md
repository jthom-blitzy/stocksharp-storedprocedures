# The SQL layer (StockSharpLegacy)

Owner: whoever's on call for order/position issues, in practice. There's no
formal owner for this database - it grew out of the risk desk needing
pre-trade checks the C# rule engine wasn't fast enough to change without a
deploy, and it's been added to piecemeal since.

This document exists because the last three "why did this order get
rejected" tickets all turned out to be a disagreement between the C# risk
engine and the SQL risk engine, and nobody could point at a single place that
explained both. This is that place. If you're new to this codebase: read
this before you go looking for "the" risk check, because there isn't one.

## What's here

- `/Database` - schema (`001_Schema.sql`), stored procedures
  (`002_StoredProcedures.sql`), triggers (`003_Triggers.sql`), seed data
  (`004_SeedData.sql`). Runnable against a fresh SQL Server instance -
  `Database/README.md` has the Docker one-liner.
- `Algo/Storages/Sql/` - `SqlLegacyOrderGateway`, the ADO.NET (not Dapper, not
  EF - raw `SqlCommand`) client for the stored procs above, plus
  `SqlLegacyConnection` for connection-string resolution and `SqlPosition`/
  `SqlOrderSubmitResult` DTOs.
- `Samples/08_Misc/03_LegacySqlDemo` - a runnable walkthrough: submit a
  compliant order, submit one that gets rejected, record a fill, watch the
  trigger update the position.
- Comments on `Algo/Risk/RiskManager.cs` and `Algo/Risk/RiskOrderFreqRule.cs`
  pointing at where the SQL side now disagrees with them.

## Why there are two risk engines

`Algo/Risk` (C#) was there first. `dbo.usp_ValidatePreTradeRisk` (SQL) was
added later, when the desk wanted to change a limit without waiting on a
release. Nobody made a call to retire the C# side - some deployments still
depend on it, some don't - so both are live in this codebase and they are
**not the same shape of check**:

- **`Algo.Risk.RiskManager`** evaluates rules against the message stream and,
  when one trips, takes a portfolio-wide action - `ClosePositions`,
  `StopTrading`, or `CancelOrders` (see `RiskMessageAdapter.ProcessRiskAsync`).
  It does not reject the specific order that tripped the rule; it's a
  circuit breaker, not a gate. Whatever tripped it typically still goes out,
  unless `StopTrading` was already in effect from an earlier violation.
- **`dbo.usp_ValidatePreTradeRisk`** is a gate: it evaluates one order before
  it's accepted and rejects that order, specifically, if it fails. It is
  called from `usp_SubmitOrder`, which is what `SqlLegacyOrderGateway`
  actually calls.

Anyone asking "does this order get risk-checked" needs to know which path
it's going through. Going through `SqlLegacyOrderGateway.SubmitOrderAsync`
gets you the SQL rules. Going through a `Connector`/`RiskMessageAdapter`
pipeline gets you the C# rules. **Nothing runs both.**

### Rule-by-rule coverage

| Check | C# (`Algo/Risk`) | SQL (`usp_ValidatePreTradeRisk`) | Notes |
|---|---|---|---|
| Order price ceiling | `RiskOrderPriceRule` | `max_order_price` | Same semantics: rejects when `price >= limit`. |
| Order qty ceiling | `RiskOrderVolumeRule` | `max_order_qty` | Same semantics. |
| Order notional value (qty × price) | - | `max_order_value` | **SQL-only.** No C# rule checks this at all. |
| Order frequency | `RiskOrderFreqRule` | `max_order_freq_count` / `_window_sec` | Same config shape, **different algorithm** - see below. |
| Resulting position size | `RiskPositionSizeRule` | `max_position_size` | C# checks the *current* position from a `PositionChangeMessage`; SQL checks the *hypothetical post-fill* position, since it runs pre-trade. |
| Cumulative commission | `RiskCommissionRule` / `RiskOrderCommissionRule` | `max_commission_total` | SQL estimates commission from `qty * price * commission_rate` since actual commission isn't known pre-fill; C# tracks the real commission off `ExecutionMessage`. These will not agree. |
| Daily traded volume | - | `max_daily_volume` | **SQL-only.** |
| Position lifetime, P&L limit, slippage | `RiskPositionTimeRule`, `RiskPnLRule`, `RiskSlippageRule` | - | **C#-only** - nothing on the SQL side checks these; they need live state the pre-trade gate doesn't have. |

The frequency check is the one worth calling out specifically:
`RiskOrderFreqRule` buckets time into fixed, non-overlapping windows (a burst
that straddles a bucket boundary can dodge the limit). The SQL version runs a
true rolling `COUNT(*)` over "now minus N seconds", which is strictly
stricter near a boundary. Same `Count`/`Interval` configuration, different
answer, for the same input, depending on which one sees it first.

## The position-update hazard

`trg_Trades_PositionRecalc` fires on every `INSERT` into `dbo.Trades` and
calls `usp_RecalculatePositionOnTrade`. That proc is also exposed standalone,
because it predates the trigger - some of the overnight reconciliation jobs
still call it directly against trades that already fired the trigger. Doing
that double-counts the trade against `Positions.qty` /`avg_price` /
`realized_pnl`. Nothing in the schema prevents this; it relies on whoever
writes the next batch job knowing not to do it. `SqlLegacyOrderGateway`
itself never calls the proc directly - it only inserts into `Trades` and lets
the trigger handle it - but that's a convention, not an enforced rule.

`unrealized_pnl` on `Positions` is not maintained by either the trigger or
the proc - both require a live market price, which neither has. It's
refreshed by the EOD mark-to-market batch (not part of this brief). Treat it
as stale/EOD-only, not real-time, if you're reading that column.

## Half-migrated persistence

`SqlLegacyOrderGateway` is not a full `IEntityRegistry` implementation - it's
an adapter that sits alongside `CsvEntityRegistry`. Orders, trades, and
positions go through SQL now; securities, exchanges, and subscriptions are
still served by the existing CSV storage. `EnsurePortfolioAsync` /
`EnsureSecurityAsync` match rows by name / (code, board) because there is no
shared identifier between `BusinessEntities.Portfolio`/`Security` and their
SQL counterparts - if a portfolio gets renamed on one side, this silently
starts creating a second row on the other.

## Known rough edges (left in on purpose)

- `usp_ValidatePreTradeRisk` takes a `@requested_by` parameter that is never
  read in the proc body. It was added for a compliance ask (tag every risk
  check with who initiated it) that got descoped, but the parameter shipped
  anyway because `usp_SubmitOrder` and the C# caller already threaded it
  through.
- The frequency-check comment in `usp_ValidatePreTradeRisk` references a
  "compliance review, last year" that tightened the window to 30 seconds.
  That hasn't been a hardcoded truth since `max_order_freq_window_sec` became
  config-driven via `RiskLimits` - nobody went back and fixed the comment.
- `qty` in every SQL table vs. `Volume`/`Quantity` on the C# `Order`/`MyTrade`
  objects. Never reconciled; by the time anyone noticed, the column name was
  baked into three stored procs and a trigger.
- `Orders.external_transaction_id` exists to let a support engineer correlate
  a row back to the in-memory `Order.TransactionId`, but it's nullable and
  was never back-filled for anything inserted before it was added.
- Only `OrderTypes.Limit`/`.Market` map to `dbo.Orders.order_type`; sending a
  conditional/stop order through `SqlLegacyOrderGateway` throws
  `NotSupportedException`. Conditional orders were out of scope for this
  pass.

## Running it

See `Database/README.md` for the Docker command and script run order, and
`Samples/08_Misc/03_LegacySqlDemo` for a working end-to-end example. Every
scenario described above (accept, reject-by-price, reject-by-frequency,
trade-triggers-recalc, status-change-triggers-audit-row) has been run
against a real `mcr.microsoft.com/mssql/server` container while building
this, not just reviewed by eye.
