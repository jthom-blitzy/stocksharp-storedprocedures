# Recording 02 - Demo scenarios (timestamped transcript)

> **What this file is.** A timestamped *text transcript*, not a video. This workload is a headless .NET console demo plus a PostgreSQL container, so there is no graphical UI to screen-record. Per the QA-evidence rule (AAP 0.7.2), a timestamped transcript backed by the raw command log is the sanctioned substitute for a recording. Every block below is reproduced verbatim from the committed raw log named in its heading, so the transcript is provably derived from a real run - nothing here is hand-authored output.

Focused transcript of the `LegacySqlDemo` console app demonstrating the three
observable outcomes the refactor must preserve, run against a freshly
re-initialized PostgreSQL 16 database (`001_Schema -> 003_Triggers -> 004_SeedData`).

## Timeline (UTC, from the raw log)

| When (UTC) | Event |
|------------|-------|
| 00:29:43Z | demo started; resolved DEMO portfolio (id 1) and AAPL@NASDAQ (id 1) |
| within run window | Scenario 1: BUY 100 @ 150.00 -> `is_valid=True` (accepted) |
| within run window | Scenario 2: BUY 10 @ 999.00 -> `is_valid=False`, reason `Order price 999.00 meets/exceeds limit 500.0000` |
| within run window | Scenario 3: record trade 100 @ 150.00 -> position `qty=100.0000 avg_price=150.0000 realized_pnl=0.0000` |
| 00:29:45Z | demo finished, exit code 0 |

The three scenarios execute in sequence inside the two-second run window
00:29:43Z -> 00:29:45Z (sub-second per-line timestamps are not emitted by the demo,
so none are invented here).

## Verbatim capture

Source: `QA/logs/02-04_demo_scenarios.log`

```
================================================================================
 AUTHENTIC CAPTURE — LegacySqlDemo (three observable scenarios)
================================================================================
 Command : dotnet run --project Samples/08_Misc/03_LegacySqlDemo/03_Misc.LegacySqlDemo.csproj -c Release --no-build
 Host    : Linux 6.6.122+ x86_64
 .NET SDK: 10.0.302
 DB      : postgres:16 (container stocksharp-review-cp2-db-1) via Host=localhost;Port=5432;Database=stocksharp;Username=postgres  [password redacted]
 DB state: freshly re-initialized (001_Schema -> 003_Triggers -> 004_SeedData)
 Started : 2026-07-18T00:29:43Z
================================================================================

Portfolio 'DEMO' = portfolio_id 1
Security 'AAPL@NASDAQ' = security_id 1

Submitting BUY 100 @ 150.00 (within limits)...
  -> order_id=1 is_valid=True reject_reason=(none)

Submitting BUY 10 @ 999.00 (price exceeds the seeded max_order_price limit)...
  -> order_id=2 is_valid=False reject_reason=Order price 999.00 meets/exceeds limit 500.0000
     Note: this rejection comes from the canonical PreTradeRiskService gate (Algo/Risk).
     The RiskManager circuit breaker shares the same canonical rolling-frequency evaluator
     and comparison convention (CanonicalRiskRules), so the gate and the breaker can no
     longer disagree on a frequency decision - see LEGACY_LAYER.md for the full rule map.

Recording a trade: 100 @ 150.00 against order #1...
  -> position after recalculation: qty=100.0000 avg_price=150.0000 realized_pnl=0.0000

Exit code: 0
Finished (UTC): 2026-07-18T00:29:45Z
```

## Outcome

Rejection reason is the exact string `Order price 999.00 meets/exceeds limit
500.0000`, produced by the canonical `PreTradeRiskService` gate; the position auto-
updates from the C# `PositionRecalculationService` (no database recalculation
trigger). Exit code 0.
