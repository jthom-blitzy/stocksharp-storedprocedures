# Recording 02 — Demo scenarios (timestamped transcript)

> **What this file is.** A timestamped *text transcript*, not a video. This
> workload is a headless .NET console demo plus a PostgreSQL container, so there
> is no graphical UI to screen-record. Per the QA-evidence rule (AAP §0.7.2), a
> timestamped transcript is the sanctioned substitute for a recording. The block
> below is the **verbatim `app`-service output** of the same single
> `docker compose up --build` run captured in `01_end_to_end.md` — nothing is
> hand-authored.

Focused walkthrough of the three observable outcomes the refactor must preserve,
produced by the `LegacySqlDemo` console app running **inside** the Compose stack
against the freshly initialized PostgreSQL 16 `db` service
(`001_Schema → 003_Triggers → 004_SeedData`).

## Timeline (UTC, 2026-07-18, from the captured log)

| When (UTC) | Event |
|------------|-------|
| 07:39:36.186Z | demo resolved `DEMO` portfolio (id 1) and `AAPL@NASDAQ` (id 1) |
| 07:39:36.221Z | Scenario 1: `BUY 100 @ 150.00` → `is_valid=True` (accepted) |
| 07:39:36.225Z | Scenario 2: `BUY 10 @ 999.00` → `is_valid=False`, reason `Order price 999.00 meets/exceeds limit 500.0000` |
| 07:39:36.240Z | Scenario 3: record trade `100 @ 150.00` → position `qty=100.0000 avg_price=150.0000 realized_pnl=0.0000` |
| 07:39:38Z | demo container exited, exit code 0 |

The three scenarios execute within a sub-second window (the per-line UTC stamps
above are taken from `docker compose logs --timestamps`; the demo itself does not
print per-line timestamps, so none are invented here).

## Verbatim capture (`app` service output)

```
Portfolio 'DEMO' = portfolio_id 1
Security 'AAPL@NASDAQ' = security_id 1

Submitting BUY 100 @ 150.00 (within limits)...
  -> order_id=1 is_valid=True reject_reason=(none)

Submitting BUY 10 @ 999.00 (price exceeds the seeded max_order_price limit)...
  -> order_id=2 is_valid=False reject_reason=Order price 999.00 meets/exceeds limit 500.0000
     Note: this rejection comes from the canonical PreTradeRiskService gate (Algo/Risk).
     The RiskManager circuit breaker shares the same canonical rolling-frequency evaluator
     and comparison convention (CanonicalRiskRules), so given the same events the gate and
     the breaker compute the same frequency arithmetic; they still read different state
     (DB rows vs an in-memory stream) and act differently - see LEGACY_LAYER.md for the map.

Recording a trade: 100 @ 150.00 against order #1...
  -> position after recalculation: qty=100.0000 avg_price=150.0000 realized_pnl=0.0000
```

## Outcome

1. **Accept** — an order within every configured limit is accepted
   (`is_valid=True`).
2. **Reject with reason** — an order breaching the seeded `max_order_price=500.00`
   is rejected with the exact string `Order price 999.00 meets/exceeds limit
   500.0000`, produced by the canonical `PreTradeRiskService` gate (the C#
   re-expression of the retired SQL pre-trade-risk procedure).
3. **Automatic position update** — recording a trade against the accepted order
   recomputes the position (`qty=100.0000 avg_price=150.0000 realized_pnl=0.0000`)
   via `PositionRecalculationService`, with **no** database recalculation trigger.

Exit code 0. These are the same three outcomes the original SQL Server demo
demonstrated, now proven on PostgreSQL.
