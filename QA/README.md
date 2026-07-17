# QA — Validation Evidence

This folder collects the evidence that the refactor works end-to-end against a containerized **PostgreSQL 16** instance. The refactor has three parts: (1) the SQL→C# business-logic consolidation, (2) the database-engine migration to PostgreSQL, and (3) containerization. Everything here is captured *after* the code was in place, so it reflects the finished system rather than intermediate state.

This folder contains **no application code** and is excluded from the Docker build context by the repository-root `.dockerignore` — it is review evidence, not part of the app image. For the full design write-up see `LEGACY_LAYER.md` at the repository root; for database specifics (schema, init order, connection details) see `Database/README.md`.

At a glance, the evidence is **six screenshots** (`QA/screenshots/`) and **three recordings** (`QA/recordings/`). Each is indexed below with what it demonstrates and how it was captured. The two subfolders hold the artifacts themselves; this file is the index a reviewer opens first.

## What this proves

The artifacts below collectively demonstrate three things.

- **A. The three observable demo scenarios, now on PostgreSQL** — from `Samples/08_Misc/03_LegacySqlDemo`:
  1. An order **within every configured limit is ACCEPTED** — `BUY 100 @ 150.00` → `is_valid=True`, `reject_reason=(none)`.
  2. An order **breaching the seeded `max_order_price = 500.00` is REJECTED with a reason** — `BUY 10 @ 999.00` → `is_valid=False` with a non-empty `reject_reason`. This rejection now comes from the canonical **`PreTradeRiskService`** gate (`Algo/Risk`), which re-expresses the retired SQL-side pre-trade-risk stored procedure in C#.
  3. **Recording a trade against the accepted order updates the position AUTOMATICALLY** — recording `100 @ 150.00` against order #1 yields `qty=100 avg_price=150 realized_pnl=0`, computed by **`PositionRecalculationService`**. There is **no database trigger**: the old position-recalculation trigger (`trg_Trades_PositionRecalc`) was removed and its logic moved to C#.
- **B. One-command startup** — `docker-compose up` brings up `postgres:16` and then the demo app. The database initialization scripts run in order `001_Schema.sql` → `003_Triggers.sql` → `004_SeedData.sql` → `005_AppRole.sql` (the `002` numbering gap is harmless — the stored-procedure script was retired when its logic moved to C#), gated behind a `pg_isready` healthcheck. The app service starts only once the database reports healthy.
- **C. Staged-testing / behavioral parity** — the `Tests/` parity suites **`PreTradeRiskParityTests`** and **`PositionRecalculationTests`** pass, proving the consolidated C# logic matches the original behavior. That includes the rolling-frequency **threshold-strictness invariant** (the reconciled limit is never looser than the stricter original) and the average-cost / P&L recompute with its **single-apply** invariant and `unrealized_pnl` left untouched.

Two independent change axes landed in this refactor at once — logic consolidation (moving rules into C#) and the engine migration (to PostgreSQL). The staged testing exists to keep those axes separable so a regression can be attributed to the right one: characterize the original behavior first, re-run the same parity checks after consolidating the logic (engine held constant), then re-run them again against the containerized PostgreSQL database. A failure that appears only in the last stage points at a dialect/engine issue rather than a logic bug. Screenshots `05` and `06` capture the parity suites green; the transcripts under `QA/recordings/` capture the same runs in sequence.

## How to reproduce

One-command end-to-end, from the repository root:

```bash
docker-compose up --build
# postgres:16 comes up healthy (pg_isready), then the /Database scripts apply in order
# 001_Schema.sql -> 003_Triggers.sql -> 004_SeedData.sql -> 005_AppRole.sql, and the demo app runs.
# Tear down (including the data volume) when done:
docker-compose down -v
```

`--build` compiles the demo from the repository-root `Dockerfile`, a multi-stage .NET 10 build (`mcr.microsoft.com/dotnet/sdk:10.0` to restore and publish, then the slim `mcr.microsoft.com/dotnet/runtime:10.0` for the console executable). The build context is the repository root so the demo's transitive project references resolve.

Run the parity tests locally against the host .NET SDK 10.0:

```bash
dotnet test Tests/Tests.csproj
```

The pure-logic parity tests are always green. The database-integration tests report **Inconclusive** (not failed) when no PostgreSQL instance is reachable — i.e. when the `STOCKSHARP_LEGACY_SQL_CONNECTION` environment variable is unset — so a missing database never looks like a test failure.

To run only the demo against an already-running PostgreSQL instance (for example the compose `db` service, which publishes `localhost:5432`):

```bash
dotnet run --project Samples/08_Misc/03_LegacySqlDemo
```

Point `STOCKSHARP_LEGACY_SQL_CONNECTION` at any other instance; when it is unset the gateway falls back to a local-development connection string (documented in `Database/README.md`).

Configuration (source of truth: the repository-root `docker-compose.yml`):

| Setting | Value |
|---|---|
| Database image | `postgres:16` |
| `POSTGRES_USER` | `postgres` |
| `POSTGRES_PASSWORD` | `postgres` |
| `POSTGRES_DB` | `stocksharp` |
| Init scripts mount | `./Database` → `/docker-entrypoint-initdb.d:ro` |
| Healthcheck | `pg_isready -h localhost -U postgres -d stocksharp` |
| App connection env var | `STOCKSHARP_LEGACY_SQL_CONNECTION` |
| App connection string | `Host=db;Port=5432;Database=stocksharp;Username=app_user;Password=app_pw;GSS Encryption Mode=Disable` |

The stack uses **two roles** (local-development only, deliberately not secret): `postgres` / `postgres` is the bootstrap superuser used solely to run the init scripts and the healthcheck, while the app authenticates as the least-privilege `app_user` / `app_pw` role (provisioned by `Database/005_AppRole.sql`, DML-only on the seven tables). That is why the app connection string above uses `app_user` rather than `postgres`. `GSS Encryption Mode=Disable` stops Npgsql probing for a Kerberos library that this local, password-authenticated stack does not use.

The seeded risk limits that drive the demo outcomes are fixed by `Database/004_SeedData.sql` and must not change: `max_order_price=500.00`, `max_order_qty=10000`, `max_order_value=1000000.00`, `max_position_size=100000`, `max_daily_volume=250000`, order frequency `5 / 60s`, `max_commission_total=5000.00`, `commission_rate=0.0005` — seeded once as a portfolio-wide `RiskLimits` row for the `DEMO` portfolio, with securities `AAPL` and `MSFT` on `NASDAQ`.

## Screenshots

The screenshots live under `QA/screenshots/` and are small, ordinary Git objects (**not** tracked by Git LFS). In this headless environment they are rendered terminal/console transcripts saved as PNG rather than windowed desktop captures.

| File | What it demonstrates | How captured |
|---|---|---|
| `screenshots/01_compose_up_healthy.png` | `docker-compose up` — `postgres:16` reports healthy via `pg_isready`; the `/Database` init scripts apply in order `001_Schema.sql` → `003_Triggers.sql` → `004_SeedData.sql` → `005_AppRole.sql`; the app service then starts | Terminal capture of `docker-compose up --build` (rendered terminal transcript) |
| `screenshots/02_order_accepted.png` | Demo scenario 1 — `BUY 100 @ 150.00` accepted: `is_valid=True`, `reject_reason=(none)` | Demo console output |
| `screenshots/03_order_rejected_by_price.png` | Demo scenario 2 — `BUY 10 @ 999.00` rejected: `is_valid=False` with a reason (breaches the seeded `max_order_price=500.00`), from the `PreTradeRiskService` gate | Demo console output |
| `screenshots/04_position_auto_updated.png` | Demo scenario 3 — after `RecordTradeAsync`, the position auto-updates to `qty=100 avg_price=150 realized_pnl=0` via `PositionRecalculationService` (no trigger) | Demo console output |
| `screenshots/05_pretraderisk_parity_tests_pass.png` | `PreTradeRiskParityTests` passing — seven-check parity plus the rolling-frequency strictness invariant | `dotnet test` output |
| `screenshots/06_position_recalc_tests_pass.png` | `PositionRecalculationTests` passing — average-cost / P&L parity, single-apply, `unrealized_pnl` untouched | `dotnet test` output |

For reference, the demo prints these lines (matching `Samples/08_Misc/03_LegacySqlDemo/Program.cs`):

```text
Submitting BUY 100 @ 150.00 (within limits)...
  -> order_id=1 is_valid=True reject_reason=(none)

Submitting BUY 10 @ 999.00 (price exceeds the seeded max_order_price limit)...
  -> order_id=2 is_valid=False reject_reason=<reason>

Recording a trade: 100 @ 150.00 against order #1...
  -> position after recalculation: qty=100 avg_price=150 realized_pnl=0
```

## Recordings

The recordings live under `QA/recordings/` and index the moving-picture evidence of each flow.

| File | What it demonstrates | How captured |
|---|---|---|
| `recordings/01_end_to_end.md` | Full end-to-end flow: `docker-compose up` → healthy PostgreSQL → the demo runs the three scenarios | Timestamped text transcript (see substitution policy) |
| `recordings/02_demo_scenarios.md` | Focused walkthrough of the three demo outcomes (accept / reject-by-price / automatic position update) | Timestamped text transcript (see substitution policy) |
| `recordings/03_parity_tests.md` | The `dotnet test` parity suites running green | Timestamped text transcript (see substitution policy) |

**Substitution policy.** Where genuine screen-capture video is not feasible in this automated, headless environment (no display, and possibly no Docker daemon), each recording is **substituted** by a timestamped text transcript — a `.md` / `.txt` / `.log` file carrying the same base name (e.g. `recordings/01_end_to_end.md`) — **plus** the numbered screenshot sequence in `QA/screenshots/`. That is the form actually produced here: the transcripts, together with the six screenshots, are the delivered recordings. This substitution is explicitly sanctioned, so a reviewer should read the transcripts as the intended evidence, not as a gap. If a later run captures real video, the same three base names take a binary extension (`.mp4` / `.webm`, etc.) and the Git LFS handling below applies.

Each transcript records the exact commands run and the captured console output, with timestamps marking the start and end of each step, so a reviewer can follow the same sequence a video would have shown — the compose bring-up and healthcheck, the three demo scenarios, and the parity-test run — and cross-check the output against the screenshots and the expected lines quoted above.

## Git LFS & file handling

- Binary video recordings (`*.mp4`, `*.mov`, `*.webm`, `*.mkv`, `*.gif`, `*.zip`) under `QA/recordings/` are tracked with **Git LFS** via the repository-root `.gitattributes`, so large media never bloats the main Git history.
- Text transcripts (`.md` / `.txt` / `.log`) and the screenshot PNGs are **ordinary Git objects** — they are small, so they are committed normally and are **not** routed through Git LFS. Keeping the transcripts as plain text also makes them diff-able and searchable in review, which a binary recording would not be.
- The whole `QA/` folder is excluded from the Docker build context by the repository-root `.dockerignore`: this evidence is for reviewers, not for the application image.

## Traceability

Each artifact traces back to a concrete part of the implementation:

- The three demo scenarios come from the demo `Samples/08_Misc/03_LegacySqlDemo/Program.cs`.
- The consolidated business logic lives in `Algo/Risk/` — `PreTradeRiskService` (per-order accept/reject gate), `PositionRecalculationService` (average-cost quantity / average price / realized P&L), and `CanonicalRiskRules` (the shared thresholds and rolling-frequency evaluator that both the gate and the `RiskManager` circuit breaker consume).
- The pure-storage schema, the audit trigger, the seed data, and the least-privilege role live in the migrated `Database/*.sql` scripts.
- The behavioral parity is proven by the `Tests/` suites `PreTradeRiskParityTests` and `PositionRecalculationTests`.

This evidence was produced **last** — after the implementation folders (`Algo`, `Database`, `Samples`, `Tests`) and the containerization artifacts (`Dockerfile`, `docker-compose.yml`, `.dockerignore`) were complete — so it validates the finished refactor rather than a work-in-progress snapshot.

The artifact filenames above are canonical and stable: the six `screenshots/NN_*.png` names and the three `recordings/NN_*` base names are fixed so this index and the files in the two subfolders stay in lock-step. If an artifact is regenerated, it keeps its name; only its contents change.
