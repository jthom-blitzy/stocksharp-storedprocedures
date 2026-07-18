# QA — Validation Evidence

This folder collects the evidence that the refactor works end-to-end against a containerized **PostgreSQL 16** instance. The refactor has three parts: (1) the SQL→C# business-logic consolidation, (2) the database-engine migration to PostgreSQL, and (3) containerization. Everything here was captured *after* the code was in place, so it reflects the finished system rather than intermediate state.

This folder contains **no application code** and is excluded from the Docker build context by the repository-root `.dockerignore` — it is review evidence, not part of the app image. For the full design write-up see `LEGACY_LAYER.md` at the repository root; for database specifics (schema, init order, connection details) see `Database/README.md`.

At a glance, the artifacts that actually exist here are:

- **`QA/logs/`** — the raw command logs and MSTest `.trx` result files. **These are the evidence of record.** Each log carries a provenance header (exact command, host, .NET SDK, database, UTC timestamps, exit code).
- **`QA/screenshots/`** — **six PNGs** rendered *from* those logs (a terminal-styled view of the captured text). They are an illustration of the logs, not an independent source.
- **`QA/recordings/`** — **three timestamped text transcripts** (`.md`). This is a headless console-plus-container workload with no GUI to film, so per the QA-evidence rule (AAP §0.7.2) a timestamped transcript is the sanctioned substitute for a video; each transcript embeds the verbatim log content it is built from.

Nothing in this folder is presented as proof in place of the real run: the passing test suites (with their committed `.trx` files) and the demo output in `QA/logs/` are the record; the screenshots and transcripts are readable renderings of that record.

## What this proves

The artifacts below collectively demonstrate three things.

- **A. The three observable demo scenarios, now on PostgreSQL** — from `Samples/08_Misc/03_LegacySqlDemo` (see `QA/logs/02-04_demo_scenarios.log`):
  1. An order **within every configured limit is ACCEPTED** — `BUY 100 @ 150.00` → `order_id=1 is_valid=True reject_reason=(none)`.
  2. An order **breaching the seeded `max_order_price = 500.00` is REJECTED with a reason** — `BUY 10 @ 999.00` → `order_id=2 is_valid=False reject_reason=Order price 999.00 meets/exceeds limit 500.0000`. This rejection comes from the canonical **`PreTradeRiskService`** gate (`Algo/Risk`), which re-expresses the retired SQL-side pre-trade-risk stored procedure in C#.
  3. **Recording a trade against the accepted order updates the position AUTOMATICALLY** — recording `100 @ 150.00` against order #1 yields `qty=100.0000 avg_price=150.0000 realized_pnl=0.0000`, computed by **`PositionRecalculationService`**. There is **no database recalculation trigger**: the old position-recalculation trigger (`trg_Trades_PositionRecalc`) was removed and its logic moved to C#. The pure status-audit trigger on `Orders` is intentionally retained.
- **B. One-command startup** — `docker compose up` brings up `postgres:16` and then the demo app. The database initialization scripts run in order `001_Schema.sql` → `003_Triggers.sql` → `004_SeedData.sql` (the `002` numbering gap is harmless — the stored-procedure script was retired when its logic moved to C#, and there is no `005`), gated behind a `pg_isready` healthcheck. The app service starts only once the database reports healthy. See `QA/logs/01_compose_up_healthy.log`, whose PostgreSQL entrypoint log shows exactly those three scripts running and `README.md` being ignored.
- **C. Staged-testing / behavioral parity** — the `Tests/` parity suites **`PreTradeRiskParityTests`** (42 passed) and **`PositionRecalculationTests`** (23 passed) pass against the live database, proving the consolidated C# logic matches the original behavior. That includes the rolling-frequency **threshold-strictness invariant** (the reconciled limit is never looser than the stricter original) and the average-cost / P&L recompute with its **single-apply** invariant and `unrealized_pnl` left untouched. See `QA/logs/05_pretraderisk_parity_tests.log` and `QA/logs/06_position_recalc_tests.log` (plus the committed `.trx` files).

Two independent change axes landed in this refactor at once — logic consolidation (moving rules into C#) and the engine migration (to PostgreSQL). The staged testing exists to keep those axes separable so a regression can be attributed to the right one: characterize the original behavior first, re-run the same parity checks after consolidating the logic (engine held constant), then re-run them again against the containerized PostgreSQL database. A failure that appears only in the last stage points at a dialect/engine issue rather than a logic bug.

## How to reproduce

One-command end-to-end, from the repository root (Docker Compose v2 — note the space, `docker compose`, not the legacy `docker-compose` script):

```bash
docker compose up --build
# postgres:16 comes up healthy (pg_isready), then the /Database scripts apply in order
# 001_Schema.sql -> 003_Triggers.sql -> 004_SeedData.sql, and the demo app runs.
# Tear down (including the data volume) when done:
docker compose down -v
```

`--build` compiles the demo from the repository-root `Dockerfile`, a multi-stage .NET 10 build (`mcr.microsoft.com/dotnet/sdk:10.0` to restore and publish, then the slim `mcr.microsoft.com/dotnet/runtime:10.0` for the console executable). The build context is the repository root so the demo's transitive project references resolve.

Run the parity tests locally against the host .NET SDK 10.0:

```bash
dotnet test Tests/Tests.csproj
```

The pure-logic parity tests are always green. The database-integration tests report **Inconclusive** (not failed) *only* when `STOCKSHARP_LEGACY_SQL_CONNECTION` is unset — so a missing database never looks like a test failure; when the variable *is* set but the database is unreachable, they fail loudly rather than silently skipping.

To run only the demo against an already-running PostgreSQL instance (for example the compose `db` service, which publishes `localhost:5432`):

```bash
dotnet run --project Samples/08_Misc/03_LegacySqlDemo
```

Point `STOCKSHARP_LEGACY_SQL_CONNECTION` at any other instance; when it is unset the gateway falls back to a local-development connection string (documented in `Database/README.md`).

## Repeatability

The demo scenarios assume a **freshly initialized** database. The seed leaves the `DEMO`/`AAPL` position empty, so the first run produces `qty=100.0000`. Because the compose data volume persists, a second run against the *same* volume accumulates state: the position becomes `qty=200.0000`, and — since the seeded order-frequency limit is `5 / 60s` — repeatedly re-running within the same 60-second window will eventually trip the frequency gate. To reproduce the exact documented numbers, start from a clean volume:

```bash
docker compose down -v && docker compose up --build
```

The capture logs in `QA/logs/` were all taken against a freshly re-initialized database (each log's provenance header records this), which is why they show the first-run values.

## Configuration

Source of truth: the repository-root `docker-compose.yml`.

| Setting | Value |
|---|---|
| Database image | `postgres:16` |
| `POSTGRES_USER` | `postgres` |
| `POSTGRES_PASSWORD` | `postgres` |
| `POSTGRES_DB` | `stocksharp` |
| Init scripts mount | `./Database` → `/docker-entrypoint-initdb.d:ro` |
| Healthcheck | `pg_isready -h localhost -U postgres -d stocksharp` |
| App connection env var | `STOCKSHARP_LEGACY_SQL_CONNECTION` |
| App connection string | `Host=db;Port=5432;Database=stocksharp;Username=postgres;Password=postgres;GSS Encryption Mode=Disable` |

The stack uses a **single role**, `postgres` / `postgres` (a local-development credential, deliberately not secret): it runs the init scripts, backs the healthcheck, and is the role the app authenticates as — matching AAP §0.4.2, which has the application connect with `POSTGRES_USER` / `POSTGRES_PASSWORD`. There is no separate application role and no init script beyond the three frozen by the AAP. `GSS Encryption Mode=Disable` stops Npgsql probing for a Kerberos library that this local, password-authenticated stack does not use.

The seeded risk limits that drive the demo outcomes are fixed by `Database/004_SeedData.sql` and must not change: `max_order_price=500.00`, `max_order_qty=10000`, `max_order_value=1000000.00`, `max_position_size=100000`, `max_daily_volume=250000`, order frequency `5 / 60s`, `max_commission_total=5000.00`, `commission_rate=0.0005` — seeded once as a portfolio-wide `RiskLimits` row for the `DEMO` portfolio, with securities `AAPL` and `MSFT` on `NASDAQ`.

## Screenshots

The screenshots live under `QA/screenshots/`. They are small, ordinary Git objects (not tracked by Git LFS). Each is a terminal-styled PNG **rendered directly from the corresponding raw log in `QA/logs/`** — every visible line is reproduced verbatim from the committed log, so a screenshot cannot drift from the run it depicts. Passwords in the provenance headers are redacted in the source logs.

| File | What it demonstrates | Rendered from |
|---|---|---|
| `screenshots/01_compose_up_healthy.png` | `docker compose up` — `postgres:16` reports healthy via `pg_isready`; the `/Database` init scripts apply in order `001_Schema.sql` → `003_Triggers.sql` → `004_SeedData.sql` (`README.md` ignored); `\dt` shows 7 tables | `QA/logs/01_compose_up_healthy.log` |
| `screenshots/02_order_accepted.png` | Demo scenario 1 — `BUY 100 @ 150.00` accepted: `is_valid=True`, `reject_reason=(none)` | `QA/logs/02-04_demo_scenarios.log` |
| `screenshots/03_order_rejected_by_price.png` | Demo scenario 2 — `BUY 10 @ 999.00` rejected: `is_valid=False`, reason `Order price 999.00 meets/exceeds limit 500.0000`, from the `PreTradeRiskService` gate | `QA/logs/02-04_demo_scenarios.log` |
| `screenshots/04_position_auto_updated.png` | Demo scenario 3 — after `RecordTradeAsync`, the position auto-updates to `qty=100.0000 avg_price=150.0000 realized_pnl=0.0000` via `PositionRecalculationService` (no trigger) | `QA/logs/02-04_demo_scenarios.log` |
| `screenshots/05_pretraderisk_parity_tests_pass.png` | `PreTradeRiskParityTests` — **42 passed, 0 failed, 0 skipped** against live PostgreSQL | `QA/logs/05_pretraderisk_parity_tests.log` |
| `screenshots/06_position_recalc_tests_pass.png` | `PositionRecalculationTests` — **23 passed, 0 failed, 0 skipped** against live PostgreSQL | `QA/logs/06_position_recalc_tests.log` |

## Raw logs and TRX (evidence of record)

The raw captures live under `QA/logs/`. Each `.log` opens with a provenance header (command, host, .NET SDK, database, UTC start/finish, exit code). The two `.trx` files are the MSTest result files produced by the parity runs and carry the per-test detail.

| File | Contents |
|---|---|
| `logs/01_compose_up_healthy.log` | `docker compose up -d db` reaching healthy + the PostgreSQL entrypoint init log (three scripts, `README.md` ignored, 7 tables) |
| `logs/02-04_demo_scenarios.log` | The demo run: all three observable scenarios, exit code 0 |
| `logs/05_pretraderisk_parity_tests.log` | `dotnet test … PreTradeRiskParityTests` → `Passed: 42`, exit code 0 |
| `logs/06_position_recalc_tests.log` | `dotnet test … PositionRecalculationTests` → `Passed: 23`, exit code 0 |
| `logs/PreTradeRiskParityTests.trx` | MSTest result file for the parity suite |
| `logs/PositionRecalculationTests.trx` | MSTest result file for the position-recalc suite |

## Recordings — timestamped transcripts

The recordings live under `QA/recordings/`. Each is a **timestamped text transcript**, not a video.

| File | What it demonstrates | Form |
|---|---|---|
| `recordings/01_end_to_end.md` | Full end-to-end flow: `docker compose up` → healthy PostgreSQL → the demo runs the three scenarios | Timestamped transcript (embeds `01_compose_up_healthy.log` + `02-04_demo_scenarios.log`) |
| `recordings/02_demo_scenarios.md` | Focused walkthrough of the three demo outcomes (accept / reject-by-price / automatic position update) | Timestamped transcript (embeds `02-04_demo_scenarios.log`) |
| `recordings/03_parity_tests.md` | The two `dotnet test` parity suites running green (42 and 23) | Timestamped transcript (embeds `05_…log` + `06_…log`) |

**Substitution policy.** Genuine screen-capture video is not feasible for this workload — it is a headless .NET console app plus a PostgreSQL container, with no graphical UI to record. Per AAP §0.7.2, each recording is therefore **substituted** by a timestamped text transcript that carries the same base name and embeds the verbatim raw log it is built from, alongside the numbered screenshot sequence in `QA/screenshots/`. This substitution is explicitly sanctioned; a reviewer should read the transcripts (and the underlying logs) as the intended evidence, not as a gap.

## File handling

- Every artifact in `QA/` is a small, plain-text or PNG file committed as an **ordinary Git object**. There are **no binary or oversized media** here — the "recordings" are text transcripts — so **Git LFS is not used and there is no repository-root `.gitattributes`**. (If a future run were to capture real video, that binary would warrant LFS and a `.gitattributes`; neither exists today because no such file exists.)
- Keeping the transcripts and logs as plain text makes them diff-able and searchable in review, which a binary recording would not be.
- The raw capture logs under `QA/logs/` are intentionally committed evidence. Because the repository-root `.gitignore` ignores `*.log` globally, a scoped `QA/logs/.gitignore` re-includes them (`!*.log`) so the evidence of record is not dropped.
- The whole `QA/` folder is excluded from the Docker build context by the repository-root `.dockerignore`: this evidence is for reviewers, not for the application image.

## Traceability

Each artifact traces back to a concrete part of the implementation:

- The three demo scenarios come from the demo `Samples/08_Misc/03_LegacySqlDemo/Program.cs`.
- The consolidated business logic lives in `Algo/Risk/` — `PreTradeRiskService` (per-order accept/reject gate), `PositionRecalculationService` (average-cost quantity / average price / realized P&L), and `CanonicalRiskRules`. What the gate and the `RiskManager` circuit breaker genuinely **share** is the canonical rolling-window frequency evaluator and the comparison convention (`>=` meets-or-exceeds; `NULL`-or-`0` means unlimited); the other circuit-breaker rules keep their own independently configured values and their own subjects (for example, current-position vs. hypothetical post-fill), so "consolidation" here means one shared definition where the two engines truly overlap, not a wholesale rewiring of every rule. See `LEGACY_LAYER.md` for the full merged-vs-preserved rule map.
- The pure-storage schema, the status-audit trigger, and the seed data live in the migrated `Database/*.sql` scripts (`001`, `003`, `004`).
- The behavioral parity is proven by the `Tests/` suites `PreTradeRiskParityTests` and `PositionRecalculationTests`. The **single-apply** guarantee for position recalculation rests on three concrete mechanisms — the recalculation trigger was removed, `RecordTradeAsync` applies each trade inside one transaction serialized by `pg_advisory_xact_lock`, and a durable `trades.position_applied` flag makes a re-applied `trade_id` a no-op — and is exercised by `PositionRecalculationTests` (it is deliberately **not** claimed "by construction").

This evidence was produced **last** — after the implementation folders (`Algo`, `Database`, `Samples`, `Tests`) and the containerization artifacts (`Dockerfile`, `docker-compose.yml`, `.dockerignore`) were complete — so it validates the finished refactor rather than a work-in-progress snapshot. The artifact filenames above are canonical and stable; if an artifact is regenerated it keeps its name, and only its contents change.
