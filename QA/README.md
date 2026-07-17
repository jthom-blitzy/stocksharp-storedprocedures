# QA evidence — StockSharpLegacy SQL→C# risk-consolidation refactor

This folder is the index of the QA evidence for the SQL→C# risk-consolidation refactor of the
StockSharpLegacy layer. After the refactor the SQL layer no longer contains risk or P&L
business logic: the seven pre-trade checks and the position-recalculation math now live in
cohesive C# services under [`../Algo/Risk/`](../Algo/Risk/) (`PreTradeRiskService`,
`PositionRecalculationService`, the canonical `RiskLimitSet`, plus the new `RiskOrderValueRule`
and `RiskDailyVolumeRule`), and the data-access gateway
([`../Algo/Storages/Sql/SqlLegacyOrderGateway.cs`](../Algo/Storages/Sql/SqlLegacyOrderGateway.cs))
delegates to those services; its data access is plain INSERT/SELECT and the only non-CRUD SQL it
issues is `sp_getapplock`/`sp_releaseapplock` for concurrency control (serializing same-portfolio
submissions and same-instrument fills, not any business decision). SQL Server is reduced to pure
DDL + CRUD (0 stored procedures; a single best-effort audit trigger - a trigger-derived
status-transition log, not a tamper-proof append-only guarantee). The demo's three
observable outcomes (accept, reject-by-price, trade-triggers-recalc) are preserved end-to-end.

There are **two intentional, documented behavioral changes**. First, the order-frequency rule now
adopts the stricter rolling-count algorithm instead of the old fixed-window bucketing, so it is
never *less* strict than the two originals (AAP §0.6.1). Second, a non-null but non-positive
ceiling (`0` or below) now means "not enforced" for that single check - the NULL/0 convention of
the `dbo.RiskLimits` table (AAP §0.3.1) - whereas the literal legacy proc would have let a stored
`0` reject *every* order (an effectively unusable block-all state); a `0` ceiling now disables just
that one check rather than blocking all orders (review finding MA-16). Both are proven intentional,
not regressions, by explicit characterization tests. See the reconciliation writeup in
[`../LEGACY_LAYER.md`](../LEGACY_LAYER.md) and the run/setup walkthrough in
[`../Database/README.md`](../Database/README.md).

## Artifact authenticity (read first)

This is a **headless CI Linux container**: there is no desktop, window manager, or GUI/terminal
application to photograph, and no video-capture device. Every artifact in this folder is
therefore a **faithful text rendering of real, unaltered command output** — the commands were
executed for real against the live SQL Server 2022 container and the .NET 10 / .NET 6 SDKs, their
stdout/stderr was captured verbatim, and that captured text was rendered to a terminal-styled PNG.
The PNGs carry truthful PNG metadata (a `Title`, a `Comment` that states plainly they are text
renderings of genuine captured output, a `Captured-UTC` timestamp, the `Source-Transcript`
filename, and the `Branch`/`Commit` identity); **none carries a "screenshot"/photographic claim
and none carries a generator/branding tag.** The raw transcripts that back **both** the six
screenshots **and** the three recordings are committed alongside the PNGs — each
`screenshots/NN_*.png` has a committed `screenshots/NN_*.txt` source (named by its
`Source-Transcript` metadata), and each recording ships its `.txt` transcript (see
[Recordings](#recordings-qarecordings)) — so every rendered frame can be diffed against its source
text. All captures in this index were taken on **2026-07-17**.

**Exact commit identity (review finding CR-1 / MA-19).** All evidence in this folder was
regenerated from the exact feature commit under review:

| | |
|--|--|
| Branch | `blitzy-e686e219-30ce-45cc-8608-8ea778391629` |
| Commit | `78ec6ebefb583a72c36476b12255ddc68b511d64` |
| Baseline (pre-refactor) | `37dc57c9683653d09a9ec92979e0b6ad2c87cb61` |

Every screenshot/recording transcript repeats this branch/commit in its header, and every PNG
repeats it in `Branch`/`Commit` metadata, so the whole evidence set is traceable to one commit.

## Reproducing the evidence

### Solution / project layout (important — the repo is multi-project)

The primary, Linux-buildable solution is **`StockSharp_Tests.slnx`** (22 projects, including
`Tests`). The full **`StockSharp.slnx`** is Windows-only (it pulls in `../Connectors`) and must
**not** be built on Linux. Because a bare `dotnet build` / `dotnet test` in the repo root is
ambiguous across projects and target frameworks, always name the project **and** the framework
**and** the configuration explicitly, exactly as below.

Target frameworks: **`net10.0`** is primary; **`net6.0`** is the legacy CI target. All new code
compiles on both.

### 1. Stand up the SQL Server container

> **Credentials & network exposure (review finding MA-13, CWE-668).** Two things matter here.
> **(1) Bind to loopback.** Publishing `-p 14330:1433` binds the SA endpoint to *every* interface
> (`0.0.0.0`), so it is reachable from other hosts - **not** "host-only" as earlier drafts of this
> runbook claimed. Prefix the host IP (`-p 127.0.0.1:14330:1433`) so the port is published on the
> loopback interface only; verify with `docker port stocksharp-legacy-sql` (it must print
> `127.0.0.1:14330`, not `0.0.0.0:14330`). **(2) Use a per-run secret.** Generate a fresh password
> each run instead of committing one, pass it through the environment (so it stays off the command
> line / process list and out of logs, screenshots, and recordings), and for the application/demo
> use a least-privilege login scoped to the `StockSharpLegacy` tables rather than SA. For any
> non-disposable target, provide the connection string through the
> `STOCKSHARP_LEGACY_SQL_CONNECTION` environment variable (backed by a secret manager) and rotate
> the password; do not edit the source fallback. No real or production credential is committed
> anywhere in this repository.

```bash
# Per-run secret - not committed, not published in evidence.
export MSSQL_SA_PASSWORD="$(openssl rand -base64 24)Aa1!"

docker run -d --name stocksharp-legacy-sql \
  -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=$MSSQL_SA_PASSWORD" \
  -p 127.0.0.1:14330:1433 \
  mcr.microsoft.com/mssql/server:2022-latest

# wait until initialization finishes
docker logs stocksharp-legacy-sql | grep "Recovery is complete"
```

### 2. Apply the DDL/CRUD scripts in run order 001 → 002 → 003 → 004

All four scripts are **idempotent and non-destructive** (review findings MA-11, CR-5): `001`
creates every object only if missing (no `DROP TABLE`, so a rerun neither raises the SQL Msg 3726
FK-drop error nor discards data), auto-creates the `StockSharpLegacy` database if absent, and
upgrades an older database in place (adding `Trades.external_trade_id` and its filtered unique
index when they are missing); `002` and `003` use guarded `DROP` / `CREATE OR ALTER`; `004` seeds
the demo data behind existence checks. Safe to re-run against an already-provisioned database
without data loss.

```bash
docker cp Database/. stocksharp-legacy-sql:/tmp/db
for f in 001_Schema.sql 002_StoredProcedures.sql 003_Triggers.sql 004_SeedData.sql; do
  docker exec -e "SQLCMDPASSWORD=$MSSQL_SA_PASSWORD" stocksharp-legacy-sql \
    /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -N -i "/tmp/db/$f"
done
```

The C# side resolves its connection from the `STOCKSHARP_LEGACY_SQL_CONNECTION` environment
variable, falling back (for local disposable runs only) to a string of the form (supply your
per-run secret, or the least-privilege application login, through the environment variable):

```
Server=localhost,14330;Database=StockSharpLegacy;User Id=sa;Password=<sa-password>;TrustServerCertificate=True;
```

### 3. Build, test, and run the demo (exact commands)

```bash
# Build the refactored assembly on both targets (0 errors on each).
dotnet build Algo/Algo.csproj -c Release --framework net10.0
dotnet build Algo/Algo.csproj -c Release -p:StockSharpTargets=net6.0 --framework net6.0

# Run the risk-consolidation test suite (the five classes that cover this refactor): 198/198.
dotnet test Tests/Tests.csproj -c Release --framework net10.0 --nologo \
  --filter "FullyQualifiedName~PreTradeRiskServiceTests|FullyQualifiedName~PositionRecalculationTests|FullyQualifiedName~CommissionTests|FullyQualifiedName~RiskTests|FullyQualifiedName~LegacyOracleParityTests"

# Run the console demo (three observable outcomes) against the live container.
dotnet run --project Samples/08_Misc/03_LegacySqlDemo/03_Misc.LegacySqlDemo.csproj -c Release --framework net10.0
```

### Seeded `DEMO` limits (`Database/004_SeedData.sql`) used throughout the evidence

| limit | value |
|-------|-------|
| `max_order_price` | 500 |
| `max_order_qty` | 10000 |
| `max_order_value` | 1000000 |
| `max_position_size` | 100000 |
| `max_daily_volume` | 250000 |
| `max_order_freq_count` | 5 |
| `max_order_freq_window_sec` | 60 |
| `max_commission_total` | 5000 |
| `commission_rate` | 0.0005 |

The `DEMO` portfolio (USD) and the `AAPL`/`MSFT` (NASDAQ) securities exist after seeding
(`portfolio_id = 1`, `security_id = 1` for AAPL, `security_id = 2` for MSFT).

## How this evidence was produced (characterization-first)

1. **Characterized** the live SQL gate/recalc behavior — including the edge cases (frequency
   window boundary, position-size pre-fill vs post-fill, commission estimate-vs-actual, and the
   `0.00004 → "Invalid qty"` decimal-scale case) — by capturing the legacy stored-procedure
   outputs before any SQL change.
2. **Consolidated** the logic into the canonical C# definitions (`RiskLimitSet`) plus the two
   services (`PreTradeRiskService`, `PositionRecalculationService`), and reduced the SQL to CRUD.
3. **Proved parity**: every rule has passing tests asserting the reconciled thresholds are
   at-least-as-strict and never looser; **65** of the tests (`Live_*`) execute against the real
   SQL Server, of which **15** are differential tests in `Tests/LegacyOracleParityTests.cs`
   (review finding MA-17) that run the C# gate/recompute and the **verbatim legacy stored
   procedures** — committed as [`../Tests/Fixtures/LegacyRiskOracle.sql`](../Tests/Fixtures/LegacyRiskOracle.sql),
   installed inside a rolled-back transaction — against the same inputs and assert identical
   results for all seven pre-trade checks and every position branch (open, same-side average,
   partial close, exact close, flip long→short, flip short→long).
4. **Re-ran the demo** against the live SQL Server and captured the real console output for the
   three outcomes.
5. **Captured** the transcripts and rendered the frames indexed below.

## Screenshots (`QA/screenshots/`)

Each row states the exact command and the exact, real result shown in the frame. Each PNG is
rendered from a committed plain-text source of the same name (its `Source-Transcript` metadata),
so the evidence chain is complete and diffable (review finding MA-19):
[`screenshots/01_clean_build.txt`](screenshots/01_clean_build.txt),
[`02_demo_three_scenarios.txt`](screenshots/02_demo_three_scenarios.txt),
[`03_test_suite_passing.txt`](screenshots/03_test_suite_passing.txt),
[`04_sql_business_logic_removed.txt`](screenshots/04_sql_business_logic_removed.txt),
[`05_legacy_layer_before_after.txt`](screenshots/05_legacy_layer_before_after.txt), and
[`06_docker_recovery_complete.txt`](screenshots/06_docker_recovery_complete.txt).

| # | File | Exact command | What the real output shows |
|---|------|---------------|----------------------------|
| 1 | [`screenshots/01_clean_build.png`](screenshots/01_clean_build.png) | `dotnet build Algo/Algo.csproj -c Release --framework net10.0` (and the `net6.0` variant) | **Build succeeded, 0 errors** on both targets. Warnings are **not zero**: net10.0 shows **2** and net6.0 **3** — all pre-existing `CS0618` obsolete-API notices in two *unmodified* files (`Algo/TraderHelper.cs:1196`, `Algo/Storages/Csv/CsvEntityList.cs:157`) plus, on net6.0, the `System.Text.Encoding.CodePages` TFM-support notice. **No warning originates in any refactored file.** |
| 2 | [`screenshots/02_demo_three_scenarios.png`](screenshots/02_demo_three_scenarios.png) | `dotnet run --project Samples/08_Misc/03_LegacySqlDemo/03_Misc.LegacySqlDemo.csproj -c Release --framework net10.0` | The demo is run **twice** to prove repeatability (review finding MA-14). Each run shows the three outcomes: `BUY 100 @ 150` **accepted** (`is_valid=True`); `BUY 10 @ 999` **rejected** with `Order price 999.0000 meets/exceeds limit 500.0000` (`is_valid=False`); a `100 @ 150` trade **recomputes the position** to `qty=100.0000 avg_price=150.0000 realized_pnl=0.0000`. The position is `qty=100` on **both** runs (not `200`) because the demo self-resets the DEMO portfolio's transactional rows at start; `order_id` values increment across runs (IDENTITY is not reset), which is cosmetic only. |
| 3 | [`screenshots/03_test_suite_passing.png`](screenshots/03_test_suite_passing.png) | `dotnet test Tests/Tests.csproj -c Release --framework net10.0 --filter "FullyQualifiedName~PreTradeRiskServiceTests\|…~PositionRecalculationTests\|…~CommissionTests\|…~RiskTests\|…~LegacyOracleParityTests"` | The five risk-consolidation classes: **198 passed, 0 failed, 0 skipped** (incl. 65 `Live_*` tests run against the real container, 15 of them the `LegacyOracleParityTests` differential tests). Per-class: PreTradeRiskServiceTests 35, PositionRecalculationTests 42, CommissionTests 34, RiskTests 72, LegacyOracleParityTests 15. Below the summary is an **honest disclosure** of the full multi-project suite (see the note under the table). |
| 4 | [`screenshots/04_sql_business_logic_removed.png`](screenshots/04_sql_business_logic_removed.png) | `sqlcmd -d StockSharpLegacy -Q "SELECT … FROM sys.procedures / sys.triggers"` + `sp_helptext` | `stored_procedures = 0`; exactly **one** user trigger, `trg_Orders_StatusAudit`, whose `sp_helptext` body is a best-effort status→history INSERT (a trigger-derived transition log, not a tamper-proof append-only guarantee) — **no thresholds, no accept/reject decision, no P&L math.** |
| 5 | [`screenshots/05_legacy_layer_before_after.png`](screenshots/05_legacy_layer_before_after.png) | `git diff --stat 37dc57c96836 -- LEGACY_LAYER.md` + before/after excerpts | The real diffstat versus the pre-refactor baseline (**1 file changed, 240 insertions(+), 93 deletions(−)**) beside the BEFORE (old two-column C#/SQL table) and AFTER (consolidated canonical-limit / circuit-breaker / pre-trade-gate / status table) excerpts. |
| 6 | [`screenshots/06_docker_recovery_complete.png`](screenshots/06_docker_recovery_complete.png) | `docker ps` + `docker port stocksharp-legacy-sql` + `docker logs stocksharp-legacy-sql \| grep "Recovery is complete"` | The container `Up`, publishing on the **loopback interface** `127.0.0.1:14330->1433/tcp` (review finding MA-13 — bound to `127.0.0.1`, **not** `0.0.0.0`), the `SQL Server is now ready` and `Recovery is complete.` markers, and the clean-seed row counts (`orders=0, trades=0, positions=0, riskLimits=1, portfolios=1, securities=2`). |

### Note on the full test suite (screenshot #3)

Screenshot #3 is deliberately scoped to the **five test classes this refactor owns**, because that
is the authoritative, fast (~5 s), deterministic, fully-green evidence for the work delivered
(**198/198, 0 failed, 0 skipped**, including 65 live-SQL tests of which 15 are the
`LegacyOracleParityTests` differential tests). The five classes and their counts are:
`PreTradeRiskServiceTests` 35 (19 `Live_`), `PositionRecalculationTests` 42 (29 `Live_`),
`CommissionTests` 34 (2 `Live_`), `RiskTests` 72 (0 `Live_`), and `LegacyOracleParityTests`
15 (15 `Live_`).

For completeness and honesty (review finding CR-1): running the **entire** `Tests/Tests.csproj`
unfiltered is a different matter. That project is the whole StockSharp platform — **4,395
`[TestMethod]`/`[DataTestMethod]` declarations across 179 test files** (and the data-parameterized
methods expand to more individual cases at run time) — spanning many subsystems unrelated to this
refactor (indicators, backtesting, candle compression, live-adapter subscription, storage, …), a
large fraction of which require external market-data feeds, network access, or other
infrastructure not present in a headless CI container. It is therefore very long-running and does
**not** complete within a 45-minute budget (it is terminated by `timeout`, leaving a tail of tests
uncompleted — this is pre-existing suite behavior, not caused by this changeset). The only test
failures observed anywhere in the unfiltered run are the two **environmental** cases
`PathsTests.CompanyPath_IsValid` and `PathsTests.AppDataPath_IsValid`, which assert machine-level
filesystem paths that do not exist in a headless container (no per-user app-data root); neither is
part of this refactor's changeset (verified with `git diff --name-only 37dc57c96836` —
`Tests/PathsTests.cs` is not listed), and **zero** failures, exceptions, hangs, or deadlocks
occurred in any risk-consolidation test. The unfiltered run does not reach a completion summary
(nor a full-suite `.trx`) within the 45-minute budget — it is terminated by `timeout` with a tail
of external-dependency tests uncompleted — so **no** full-suite pass/fail/skip tally is claimed
here; the only failures observed across the partial run are the two environmental `PathsTests`
above. By contrast the focused risk-consolidation run **does** complete, and its machine-generated
`.trx` records `ResultSummary total="198" executed="198" passed="198" failed="0" notExecuted="0"`
(outcome `Completed`). The invariant this evidence establishes is that the five risk-consolidation
classes are 198/198 green (65 of them `Live_*` against the real container) and the only full-suite
failures are the two pre-existing environmental `PathsTests`.

Example introspection query behind screenshot #4 (the procedures are gone, so a name-specific
`sp_helptext` would now error; query the catalog views and the one retained trigger instead):

```sql
SELECT name, type_desc FROM sys.procedures;                         -- 0 rows
SELECT name, type_desc FROM sys.triggers WHERE is_ms_shipped = 0;   -- trg_Orders_StatusAudit
EXEC sp_helptext 'dbo.trg_Orders_StatusAudit';                      -- pure audit cascade
```

## Recordings (`QA/recordings/`)

**No video-capture environment exists in this headless container, so all three recordings ship as
the documented substitution: a captioned `.txt` transcript of the real command output plus a
numbered PNG frame sequence rendered from that transcript** (never a `*dates.txt` suffix, which is
git-ignored). The transcripts are committed so each frame can be verified against its source text.

| # | Scenario | Substitution artifacts shipped | Status |
|---|----------|--------------------------------|--------|
| 1 | End-to-end: container healthy → apply scripts `001 → 004` → introspection (0 procs, 1 audit trigger) → demo three outcomes → persisted rows | [`01_end_to_end_docker_scripts_demo.txt`](recordings/01_end_to_end_docker_scripts_demo.txt) + `01_end_to_end_docker_scripts_demo_01.png … _03.png` | **substituted (.txt + 3 PNGs)** |
| 2 | Build + test from a clean checkout: clean build on both TFMs, then the focused risk-consolidation suite green (198/198) | [`02_test_suite_clean_checkout.txt`](recordings/02_test_suite_clean_checkout.txt) + `02_test_suite_clean_checkout_01.png`, `_02.png`, `_03.png` | **substituted (.txt + 3 PNGs)** |
| 3 | Coverage-table → code walkthrough: each `LEGACY_LAYER.md` row mapped to its C# implementation (`RiskLimitSet`, `PreTradeRiskService`, the rules, `PositionRecalculationService`, and the `RiskManager.ApplyCanonicalLimits` wiring) | [`03_coverage_table_to_code_walkthrough.txt`](recordings/03_coverage_table_to_code_walkthrough.txt) + `03_coverage_table_to_code_walkthrough_01.png … _03.png` | **substituted (.txt + 3 PNGs)** |

## Large-file / Git LFS note

Every artifact here is small — the largest PNG is ~0.4 MB and the whole `QA/` folder is ~2.5 MB —
so all artifacts are committed directly to the repository and **no `.gitattributes` / Git LFS is
used**. (Per the plan, a root `.gitattributes` with `git lfs track "QA/recordings/*.mp4"` would be
added only if some artifact exceeded ~50 MB; none does, and there are no `.mp4` files.)

## Cross-references

- [`../LEGACY_LAYER.md`](../LEGACY_LAYER.md) — the consolidated rule-by-rule coverage table.
- [`../Database/README.md`](../Database/README.md) — Docker command, script run order, and demo
  walkthrough.
- [`../Samples/08_Misc/03_LegacySqlDemo/`](../Samples/08_Misc/03_LegacySqlDemo/) — the runnable
  end-to-end demo (the three observable outcomes).
- The consolidated C# services under [`../Algo/Risk/`](../Algo/Risk/):
  [`PreTradeRiskService.cs`](../Algo/Risk/PreTradeRiskService.cs),
  [`PositionRecalculationService.cs`](../Algo/Risk/PositionRecalculationService.cs), and
  [`RiskLimitSet.cs`](../Algo/Risk/RiskLimitSet.cs).
