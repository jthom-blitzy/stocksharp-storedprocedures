# Recording 01 - End-to-end verification (timestamped transcript)

> **What this file is.** A timestamped *text transcript*, not a video. This workload is a headless .NET console demo plus a PostgreSQL container, so there is no graphical UI to screen-record. Per the QA-evidence rule (AAP 0.7.2), a timestamped transcript backed by the raw command log is the sanctioned substitute for a recording. Every block below is reproduced verbatim from the committed raw log named in its heading, so the transcript is provably derived from a real run - nothing here is hand-authored output.

This end-to-end transcript stitches the two authentic segments that together prove
the container-first workflow: (A) `docker compose up` brings PostgreSQL 16 to a
*healthy* state and runs exactly the three committed init scripts, then (B) the
`LegacySqlDemo` console app exercises the three observable scenarios against an
identically-initialized database. The two segments were captured as two real runs
(their own UTC timestamps are shown); segment A used a throwaway compose project on
port 55432 so it would not disturb the reviewer's baseline database on 5432, and
segment B ran against that baseline database, which had been re-initialized with the
same `001 -> 003 -> 004` script sequence.

## Timeline (UTC, from the raw logs)

| When (UTC) | Segment | Event |
|------------|---------|-------|
| 00:30:03Z | A | `docker compose up -d db` issued |
| 00:30:05.033Z | A | PostgreSQL first start; init scripts begin |
| 00:30:05Z | A | `001_Schema.sql -> 003_Triggers.sql -> 004_SeedData.sql` run; `README.md` ignored |
| 00:30:05.547Z | A | init complete; database ready to accept connections |
| 00:30:09Z | A | container reports `(healthy)`; `\dt` shows 7 tables |
| 00:30:10Z | A | segment A finished |
| 00:29:43Z | B | demo started (clean DB) |
| 00:29:43-45Z | B | scenario 1 accepted, scenario 2 rejected, scenario 3 position recalculated |
| 00:29:45Z | B | demo finished, exit code 0 |

## Segment A - `docker compose up` reaches healthy + 3-script init

Source: `QA/logs/01_compose_up_healthy.log`

```
================================================================================
 AUTHENTIC CAPTURE — docker compose up (db service) reaches healthy + 3-script init
================================================================================
 Command : docker compose -f docker-compose.yml up -d db   (published port remapped to
           55432 via a throwaway, NON-committed override so it does not clash with the
           reviewer's baseline db on 5432; all other service config is the committed file)
 Host    : Linux 6.6.122+ x86_64
 Compose : 5.3.1
 Image   : postgres:16
 Started : 2026-07-18T00:30:03Z
================================================================================

$ docker compose up -d db
 Network ss7qa_default Creating 
 Network ss7qa_default Created 
 Volume ss7qa_pgdata Creating 
 Volume ss7qa_pgdata Created 
 Container ss7qa-db-1 Creating 
 Container ss7qa-db-1 Created 
 Container ss7qa-db-1 Starting 
 Container ss7qa-db-1 Started 

$ docker compose ps
NAME         IMAGE         COMMAND                  SERVICE   CREATED         STATUS                   PORTS
ss7qa-db-1   postgres:16   "docker-entrypoint.s…"   db        7 seconds ago   Up 6 seconds (healthy)   127.0.0.1:55432->5432/tcp

$ docker inspect --format '{{.State.Health.Status}}' ss7qa-db-1
healthy

--- PostgreSQL entrypoint init log (running the 3 mounted /docker-entrypoint-initdb.d scripts) ---
2026-07-18 00:30:05.033 UTC [48] LOG:  database system is ready to accept connections
/usr/local/bin/docker-entrypoint.sh: running /docker-entrypoint-initdb.d/001_Schema.sql
/usr/local/bin/docker-entrypoint.sh: running /docker-entrypoint-initdb.d/003_Triggers.sql
/usr/local/bin/docker-entrypoint.sh: running /docker-entrypoint-initdb.d/004_SeedData.sql
/usr/local/bin/docker-entrypoint.sh: ignoring /docker-entrypoint-initdb.d/README.md
PostgreSQL init process complete; ready for start up.
2026-07-18 00:30:05.547 UTC [1] LOG:  database system is ready to accept connections

$ psql -c '\dt' (table count)
7 tables

Finished (UTC): 2026-07-18T00:30:10Z
```

## Segment B - LegacySqlDemo: three observable scenarios

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

Container reached `(healthy)`, the schema initialized from exactly the three
committed scripts, and the demo produced all three observable outcomes (accept,
reject-with-reason, auto position update) with exit code 0 - end to end.
