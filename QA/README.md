# QA evidence — StockSharpLegacy SQL→C# risk-consolidation refactor

This folder indexes the QA evidence for the SQL→C# risk-consolidation refactor of the
StockSharpLegacy layer. After the refactor the SQL layer no longer contains risk or P&L
business logic: the seven pre-trade checks and the position-recalculation math now live in
cohesive C# services under [`../Algo/Risk/`](../Algo/Risk/) (`PreTradeRiskService`,
`PositionRecalculationService`, the canonical `RiskLimitSet`, plus the new `RiskOrderValueRule`
and `RiskDailyVolumeRule`), and the data-access gateway
([`../Algo/Storages/Sql/SqlLegacyOrderGateway.cs`](../Algo/Storages/Sql/SqlLegacyOrderGateway.cs))
delegates to those services; its data access is parameterized INSERT/SELECT (plus up-front
structural validation, a fillability guard, and position UPDATE/INSERT orchestration — but no risk
or P&L decisioning), and the only non-CRUD SQL it issues is `sp_getapplock` (released automatically
when the owning transaction commits or rolls back — there is no explicit `sp_releaseapplock` call)
for concurrency control (serializing same-portfolio submissions and same-instrument fills, not any
business decision). SQL Server is reduced to pure DDL + CRUD (0 stored procedures; a single
best-effort audit trigger — a trigger-derived status-transition log, not a tamper-proof
append-only guarantee). The demo's three observable outcomes (accept, reject-by-price,
trade-triggers-recalc) are preserved end-to-end.

There are **two intentional, documented behavioral changes**. First, the order-frequency rule now
adopts the stricter rolling-count algorithm instead of the old fixed-window bucketing, so it is
never *less* strict than the two originals (AAP §0.6.1). Second, a non-null but non-positive
ceiling (`0` or below) now means "not enforced" for that single check — the NULL/0 convention of
the `dbo.RiskLimits` table (AAP §0.3.1) — whereas the literal legacy proc would have let a stored
`0` reject *every* order; a `0` ceiling now disables just that one check rather than blocking all
orders. Both are proven intentional, not regressions, by explicit characterization tests. See the
reconciliation writeup in [`../LEGACY_LAYER.md`](../LEGACY_LAYER.md) and the run/setup walkthrough
in [`../Database/README.md`](../Database/README.md).

## Artifact authenticity (read first)

This is a **headless CI Linux container**: there is no desktop, window manager, or GUI/terminal
application to photograph, and no video-capture device. The **authoritative proof** for every
claim in this folder is therefore the set of **raw, unaltered machine logs and the test `.trx`**
captured under [`logs/`](logs/) — real stdout/stderr from real commands run against the live SQL
Server 2022 container and the .NET 10 SDK, saved verbatim. The PNG "screenshots" and "recording
frames" are **secondary, terminal-styled renderings** of that same genuine output (and, for the
coverage-to-code walkthrough, of the real repository source files); each PNG is paired with a
`.txt` sidecar containing the exact text that was rendered, so every frame can be diffed against
its source. Per **AAP §0.6.7**, this raw-log + captioned-transcript + numbered-PNG substitution is
the mandated stand-in when no video/GUI capture device is available. **No artifact here carries a
photographic/"screenshot" claim, and none carries a generator or branding tag.**

Every PNG carries the same **uniform, truthful** metadata keys — `Title`, `Artifact`, `Branch`,
`Framework` (`net10.0`), `EvidenceCommit`, `AuthoritativeSource`, `CaptureMode`, and `CapturedUTC`
— none of which asserts a photographic capture or a stale commit. All captures in this index were
taken on **2026-07-17**.

**Exact commit identity (findings CR-1 / MA-19 / #23).**

| | |
|--|--|
| Branch | `blitzy-e686e219-30ce-45cc-8608-8ea778391629` |
| Evidence commit | `EVIDENCE_COMMIT_PENDING` |
| Baseline (pre-refactor) | `37dc57c9683653d09a9ec92979e0b6ad2c87cb61` |

> **Commit-hash stamping (finding #23).** The evidence is regenerated from the **final** working
> tree, but it must name the **final commit that contains it** — a chicken-and-egg. It is resolved
> by stamping: the code/doc/test/QA changes are committed first; a dedicated **provenance commit**
> made immediately afterward replaces the placeholder token `EVIDENCE_COMMIT_PENDING` (present in
> this file and in every transcript header) with the real commit hash and re-measures the `QA/`
> folder size. Until that stamp lands, `EVIDENCE_COMMIT_PENDING` marks every spot to be filled.

## Authoritative raw logs (`QA/logs/`)

These are the genuine machine logs the rest of this folder renders. They are captured verbatim
(no editing, no synthesis) and are the source of truth for every screenshot and recording frame.

| File | What it is | Key result (verbatim) |
|---|---|---|
| [`logs/00_env.log`](logs/00_env.log) | environment capture | `dotnet --version` = **10.0.302**; container `Up` on `127.0.0.1:14330->1433` |
| [`logs/01_build_algo_net10.0_release.log`](logs/01_build_algo_net10.0_release.log) | `dotnet build Algo/Algo.csproj -c Release -f net10.0 --no-incremental` | **0 Error(s)**, 2 pre-existing `CS0618` warnings |
| [`logs/02_build_tests_net10.0_release.log`](logs/02_build_tests_net10.0_release.log) | `dotnet build Tests/Tests.csproj -c Release -f net10.0 --no-incremental` | **0 Error(s)**, 48 pre-existing warnings (none in modified files) |
| [`logs/03_build_demo_net10.0_release.log`](logs/03_build_demo_net10.0_release.log) | `dotnet build .../03_Misc.LegacySqlDemo.csproj -c Release -f net10.0 --no-incremental` | **0 Error(s)**, 2 pre-existing warnings |
| [`logs/04_test_targeted_net10.0.log`](logs/04_test_targeted_net10.0.log) | `dotnet test` (four-class filter, live SQL) | **Passed!  Failed: 0, Passed: 196, Skipped: 0, Total: 196** |
| [`logs/test_targeted_net10.0.trx`](logs/test_targeted_net10.0.trx) | machine-generated TRX for that run | `total="196" passed="196" failed="0" notExecuted="0"` |
| [`logs/05_demo_run_net10.0.log`](logs/05_demo_run_net10.0.log) | `dotnet run` console demo (live SQL) | three outcomes + circuit-breaker seeding (8 rules, ceiling 500.0000) |
| [`logs/06_sql_no_business_logic.log`](logs/06_sql_no_business_logic.log) | `sqlcmd` catalog introspection | `stored_procedure_count = 0`; one audit trigger `trg_Orders_StatusAudit` |
| [`logs/07_docker_recovery.log`](logs/07_docker_recovery.log) | `docker logs … | grep` | `SQL Server is now ready` + `Recovery is complete` |
| [`logs/08_apply_scripts_net10.0.log`](logs/08_apply_scripts_net10.0.log) | apply `Database/001..004` in run order | `exit=0` for each script (idempotent) |

## Reproducing the evidence

### Solution / project layout (important — the repo is multi-project)

The primary, Linux-buildable solution is **`StockSharp_Tests.slnx`** (22 projects, including
`Tests`). The full **`StockSharp.slnx`** is Windows-only (it pulls in `../Connectors`) and must
**not** be built on Linux. Because a bare `dotnet build` / `dotnet test` in the repo root is
ambiguous across projects, always name the project **and** the framework **and** the configuration
explicitly, exactly as below.

**Target framework.** The affected projects build and run on **`net10.0`** in this workspace
(`StockSharpTargets=net10.0`). **`net6.0` is not a configured build target here** (and is out of
mainstream support); the .NET 6 SDK is present on the machine but this evidence neither builds nor
tests against it, and makes no net6.0 claim.

### 1. Stand up the SQL Server container

> **Credentials & network exposure (finding MA-13, CWE-668).** **(1) Bind to loopback:** prefix the
> host IP (`-p 127.0.0.1:14330:1433`) so the SA endpoint is published on the loopback interface
> only; verify with `docker port stocksharp-legacy-sql` (must print `127.0.0.1:14330`, not
> `0.0.0.0:14330`). **(2) Use a per-run secret:** generate a fresh password each run, pass it
> through the environment (so it stays off the command line / process list and out of logs and
> rendered frames), and for the application/demo prefer a least-privilege login scoped to the
> `StockSharpLegacy` tables rather than SA. For any non-disposable target, supply the connection
> string via the `STOCKSHARP_LEGACY_SQL_CONNECTION` environment variable (backed by a secret
> manager) and rotate the password. No real or production credential is committed in this repo.

```bash
# Per-run secret — not committed, not published in evidence.
export MSSQL_SA_PASSWORD="$(openssl rand -base64 24)Aa1!"

docker run -d --name stocksharp-legacy-sql \
  -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=$MSSQL_SA_PASSWORD" \
  -p 127.0.0.1:14330:1433 \
  mcr.microsoft.com/mssql/server:2022-latest

docker logs stocksharp-legacy-sql | grep "Recovery is complete"
```

### 2. Apply the DDL/CRUD scripts in run order 001 → 002 → 003 → 004

All four scripts are **idempotent and non-destructive**: `001` creates every object only if
missing (no `DROP TABLE`) and auto-creates the `StockSharpLegacy` database if absent; `002` and
`003` use guarded `DROP` / `CREATE OR ALTER`; `004` seeds the demo data behind existence checks.
Safe to re-run against an already-provisioned database without data loss (see
[`logs/08_apply_scripts_net10.0.log`](logs/08_apply_scripts_net10.0.log): `exit=0` per script).

```bash
for f in 001_Schema.sql 002_StoredProcedures.sql 003_Triggers.sql 004_SeedData.sql; do
  docker exec -i -e "SQLCMDPASSWORD=$MSSQL_SA_PASSWORD" stocksharp-legacy-sql \
    /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -d StockSharpLegacy -i /dev/stdin < "Database/$f"
done
```

The C# side resolves its connection from `STOCKSHARP_LEGACY_SQL_CONNECTION`, falling back (for
local disposable runs only) to a string of the form:

```
Server=localhost,14330;Database=StockSharpLegacy;User Id=sa;Password=<sa-password>;TrustServerCertificate=True;
```

### 3. Build, test, and run the demo (exact commands)

```bash
# Build the refactored assembly (net10.0 Release): 0 errors.
dotnet build Algo/Algo.csproj -c Release --framework net10.0

# Run the focused risk-consolidation suite — the FOUR classes that cover this refactor: 196/196.
dotnet test Tests/Tests.csproj -c Release --framework net10.0 --no-build \
  --filter "FullyQualifiedName~PreTradeRiskServiceTests|FullyQualifiedName~PositionRecalculationTests|FullyQualifiedName~CommissionTests|FullyQualifiedName~RiskTests" \
  --logger "trx;LogFileName=test_targeted_net10.0.trx" --results-directory QA/logs

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
   decimal-scale boundary case) — capturing the legacy behavior before any SQL change.
2. **Consolidated** the logic into the canonical C# definitions (`RiskLimitSet`) plus the two
   services (`PreTradeRiskService`, `PositionRecalculationService`), and reduced the SQL to CRUD.
3. **Proved parity**: every rule has passing tests asserting the reconciled thresholds are
   at-least-as-strict and never looser. **47** of the tests (`Live_*`) execute against the real
   SQL Server 2022 container. Parity is proven by the characterization + parity assertions inside
   the four owned classes (the earlier over-engineered differential-oracle fixture was reverted as
   out of AAP scope; see findings #3/#5 in the resolution report).
4. **Re-ran the demo** against the live SQL Server and captured the real console output for the
   three outcomes (the demo self-resets its DEMO transactional rows at start, so the run is
   repeatable).
5. **Captured** the raw machine logs and the `.trx` under [`logs/`](logs/), then rendered the
   transcripts and numbered frames indexed below from that genuine output.

## Screenshots (`QA/screenshots/`)

Each PNG is a terminal-styled rendering of a genuine raw log (named in the right-hand column); its
`.txt` sidecar of the same name holds the exact rendered text, so the evidence chain is complete
and diffable.

| # | File | What the real output shows | Raw source |
|---|------|----------------------------|-----------|
| 1 | [`screenshots/01_clean_build.png`](screenshots/01_clean_build.png) | **Build succeeded, 0 errors** for Algo, Tests, and the demo on `net10.0`. Warnings are non-zero but **all pre-existing** `CS0618` obsolete-API notices in *unmodified* files (`Algo/TraderHelper.cs:1196`, `Algo/Storages/Csv/CsvEntityList.cs:157`, and unrelated Tests fixtures); **no warning originates in any refactored file.** | `logs/00..03_*.log` |
| 2 | [`screenshots/02_demo_three_scenarios.png`](screenshots/02_demo_three_scenarios.png) | The three outcomes: `BUY 100 @ 150` **accepted** (`is_valid=True`); `BUY 10 @ 999` **rejected** with `Order price 999.0000 meets/exceeds limit 500.0000` (`is_valid=False`); a `100 @ 150` trade **recomputes the position** to `qty=100.0000 avg_price=150.0000 realized_pnl=0.0000`. It also seeds the portfolio-wide circuit breaker (8 rules) from the *same* canonical `RiskLimitSet` the gate uses. | `logs/05_demo_run_net10.0.log` |
| 3 | [`screenshots/03_test_suite_passing.png`](screenshots/03_test_suite_passing.png) | The **four** risk-consolidation classes: **196 passed, 0 failed, 0 skipped** (47 `Live_*` against the real container). Per-class (from the TRX): RiskTests 85, CommissionTests 34, PreTradeRiskServiceTests 40, PositionRecalculationTests 37. Scope of the full suite is disclosed honestly in the note below. | `logs/04_*.log` + `logs/…trx` |
| 4 | [`screenshots/04_sql_business_logic_removed.png`](screenshots/04_sql_business_logic_removed.png) | `stored_procedure_count = 0`; exactly **one** user trigger, `trg_Orders_StatusAudit`, whose `sp_helptext` body is a best-effort status→history INSERT — **no thresholds, no accept/reject decision, no P&L math.** | `logs/06_sql_no_business_logic.log` |
| 5 | [`screenshots/05_legacy_layer_before_after.png`](screenshots/05_legacy_layer_before_after.png) | The consolidated `LEGACY_LAYER.md` rule-by-rule coverage table (the AFTER state), with a note on the BEFORE state (logic split across T-SQL and C#): every rule now has one canonical `RiskLimitSet` threshold consumed by **both** the circuit breaker and the pre-trade gate. | `../LEGACY_LAYER.md` |
| 6 | [`screenshots/06_docker_recovery_complete.png`](screenshots/06_docker_recovery_complete.png) | The container `Up`, publishing on the **loopback interface** `127.0.0.1:14330->1433/tcp` (MA-13 — bound to `127.0.0.1`, **not** `0.0.0.0`), and the `SQL Server is now ready` + `Recovery is complete.` markers. | `logs/07_docker_recovery.log` |

### Note on the full test suite (screenshot #3, finding #21)

Screenshot #3 is scoped to the **four test classes this refactor owns** — `RiskTests`,
`CommissionTests`, `PreTradeRiskServiceTests`, `PositionRecalculationTests` — because that is the
authoritative, fast (~7 s), deterministic, fully-green evidence for the work delivered:
**196/196, 0 failed, 0 skipped**, of which **47** are `Live_*` tests against the real container.
Its machine-generated TRX ([`logs/test_targeted_net10.0.trx`](logs/test_targeted_net10.0.trx))
records `total="196" executed="196" passed="196" failed="0" notExecuted="0"`.

For completeness and honesty: running the **entire** `Tests/Tests.csproj` unfiltered is a
different matter. That project spans the whole StockSharp platform (thousands of test methods
across many subsystems — indicators, backtesting, candle compression, live-adapter subscription,
storage, …), a large fraction of which require external market-data feeds, network access, or
other infrastructure absent from a headless CI container, so it is very long-running and is not
executed to completion here. **Therefore this evidence makes NO full-solution pass/fail/skip tally
claim** — it limits its claim to the observed 196/196 focused run, for which the complete raw
`.log` and `.trx` are retained under [`logs/`](logs/). No claim is made about the number of
failures in the unfiltered suite.

## Recordings (`QA/recordings/`)

**No video-capture environment exists in this headless container, so all three recordings ship as
the AAP §0.6.7 substitution: a captioned `.txt` transcript of the real command output/file content
plus a numbered PNG frame sequence rendered from it.** The transcripts are committed so each frame
can be verified against its source. **Frame counts are indexed exactly (finding #11):**

| # | Scenario | Substitution artifacts shipped | Frames |
|---|----------|--------------------------------|--------|
| 1 | End-to-end: container healthy → apply scripts `001 → 004` → clean build → focused suite 196/196 → demo three outcomes | [`01_end_to_end_docker_scripts_demo.txt`](recordings/01_end_to_end_docker_scripts_demo.txt) + `01_end_to_end_docker_scripts_demo_01.png … _05.png` | **5** |
| 2 | Test suite from a clean checkout: build Tests (net10.0), run the focused suite (196/196), per-class tally from the TRX | [`02_test_suite_clean_checkout.txt`](recordings/02_test_suite_clean_checkout.txt) + `02_test_suite_clean_checkout_01.png … _03.png` | **3** |
| 3 | Coverage-table → code walkthrough: each `LEGACY_LAYER.md` row mapped to its C# implementation (`RiskLimitSet`, `PreTradeRiskService`, the rules, `PositionRecalculationService`, and the `RiskManager.CreateRules`/`ApplyCanonicalLimits` wiring) | [`03_coverage_table_to_code_walkthrough.txt`](recordings/03_coverage_table_to_code_walkthrough.txt) + `03_coverage_table_to_code_walkthrough_01.png … _06.png` | **6** |

## Large-file / Git LFS note

Every artifact here is small — the largest PNG is ~0.17 MB and the whole `QA/` folder is ~2.0 MB
(re-measured at the Phase-10 provenance commit) — so all artifacts are committed directly and
**no `.gitattributes` / Git LFS is used**. (Per the plan, a root `.gitattributes` with
`git lfs track "QA/recordings/*.mp4"` would be added only if some artifact exceeded ~50 MB; none
does, and there are no `.mp4` files.)

## Cross-references

- [`logs/`](logs/) — the authoritative raw machine logs and the test `.trx` (source of truth).
- [`../LEGACY_LAYER.md`](../LEGACY_LAYER.md) — the consolidated rule-by-rule coverage table.
- [`../Database/README.md`](../Database/README.md) — Docker command, script run order, and demo
  walkthrough.
- [`../Samples/08_Misc/03_LegacySqlDemo/`](../Samples/08_Misc/03_LegacySqlDemo/) — the runnable
  end-to-end demo (the three observable outcomes).
- The consolidated C# services under [`../Algo/Risk/`](../Algo/Risk/):
  [`PreTradeRiskService.cs`](../Algo/Risk/PreTradeRiskService.cs),
  [`PositionRecalculationService.cs`](../Algo/Risk/PositionRecalculationService.cs), and
  [`RiskLimitSet.cs`](../Algo/Risk/RiskLimitSet.cs).
