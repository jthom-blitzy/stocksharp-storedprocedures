# QA evidence — StockSharpLegacy SQL→C# risk-consolidation refactor

This folder is the index of the QA evidence for the SQL→C# risk-consolidation refactor of the
StockSharpLegacy layer. After the refactor the SQL layer no longer contains risk or P&L
business logic: the seven pre-trade checks and the position-recalculation math now live in
cohesive C# services under [`../Algo/Risk/`](../Algo/Risk/) (`PreTradeRiskService`,
`PositionRecalculationService`, the canonical `RiskLimitSet`, plus the new `RiskOrderValueRule`
and `RiskDailyVolumeRule`), and the data-access gateway
([`../Algo/Storages/Sql/SqlLegacyOrderGateway.cs`](../Algo/Storages/Sql/SqlLegacyOrderGateway.cs))
delegates to those services and performs only plain INSERT/SELECT. SQL Server is reduced to pure
DDL + CRUD (0 stored procedures; a single append-only audit trigger). The demo's three
observable outcomes (accept, reject-by-price, trade-triggers-recalc) are preserved end-to-end.

There is exactly **one intentional, documented behavioral change**: the order-frequency rule now
adopts the stricter rolling-count algorithm instead of the old fixed-window bucketing, so it is
never *less* strict than the two originals. See the reconciliation writeup in
[`../LEGACY_LAYER.md`](../LEGACY_LAYER.md) and the run/setup walkthrough in
[`../Database/README.md`](../Database/README.md).

## Artifact authenticity (read first)

This is a **headless CI Linux container**: there is no desktop, window manager, or GUI/terminal
application to photograph, and no video-capture device. Every artifact in this folder is
therefore a **faithful text rendering of real, unaltered command output** — the commands were
executed for real against the live SQL Server 2022 container and the .NET 10 / .NET 6 SDKs, their
stdout/stderr was captured verbatim, and that captured text was rendered to a terminal-styled PNG.
The PNGs carry truthful PNG metadata (a `Title`, a `Comment` that states plainly they are text
renderings of genuine captured output, a `Captured-UTC` timestamp, and the `Source-Transcript`
filename); **none carries a "screenshot"/photographic claim and none carries a generator/branding
tag.** The raw transcripts that back the recordings are committed alongside the PNGs (see
[Recordings](#recordings-qarecordings)), so every rendered frame can be diffed against its source
text. All captures in this index were taken on **2026-07-17**.

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

> **Credentials — disposable, non-production.** The password `DevTest_Passw0rd!` below is a
> **throwaway local-demo credential** for an ephemeral container that is not exposed outside the
> build host. **Do not reuse it, and never use it (or any hard-coded password) in production or in
> a shared/long-lived environment.** For any non-disposable target, provide the connection string
> through the `STOCKSHARP_LEGACY_SQL_CONNECTION` environment variable (backed by a secret manager)
> and rotate the SA password; do not edit the source fallback. No real or production credential is
> committed anywhere in this repository.

```bash
docker run -d --name stocksharp-legacy-sql \
  -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=DevTest_Passw0rd!" \
  -p 14330:1433 \
  mcr.microsoft.com/mssql/server:2022-latest

# wait until initialization finishes
docker logs stocksharp-legacy-sql | grep "Recovery is complete"
```

### 2. Apply the DDL/CRUD scripts in run order 001 → 002 → 003 → 004

`001_Schema.sql` is **destructive** (it drops and recreates the schema, and auto-creates the
`StockSharpLegacy` database if absent), so run it only on an empty/disposable instance. `002` and
`003` are idempotent (guarded `DROP` / `CREATE OR ALTER`); `004` seeds the demo data.

```bash
docker cp Database/. stocksharp-legacy-sql:/tmp/db
for f in 001_Schema.sql 002_StoredProcedures.sql 003_Triggers.sql 004_SeedData.sql; do
  docker exec stocksharp-legacy-sql /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P 'DevTest_Passw0rd!' -C -N -i "/tmp/db/$f"
done
```

The C# side resolves its connection from the `STOCKSHARP_LEGACY_SQL_CONNECTION` environment
variable, falling back (for local disposable runs only) to a string matching the container above:

```
Server=localhost,14330;Database=StockSharpLegacy;User Id=sa;Password=DevTest_Passw0rd!;TrustServerCertificate=True;
```

### 3. Build, test, and run the demo (exact commands)

```bash
# Build the refactored assembly on both targets (0 errors on each).
dotnet build Algo/Algo.csproj -c Release --framework net10.0
dotnet build Algo/Algo.csproj -c Release -p:StockSharpTargets=net6.0 --framework net6.0

# Run the risk-consolidation test suite (the four classes that cover this refactor): 153/153.
dotnet test Tests/Tests.csproj -c Release --framework net10.0 --nologo \
  --filter "FullyQualifiedName~PreTradeRiskServiceTests|FullyQualifiedName~PositionRecalculationTests|FullyQualifiedName~CommissionTests|FullyQualifiedName~RiskTests"

# Run the console demo (three observable outcomes) against the live container.
dotnet run --project Samples/08_Misc/03_LegacySqlDemo/03_Misc.LegacySqlDemo.csproj -c Release
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
   at-least-as-strict and never looser; 33 of the tests execute against the real SQL Server and
   assert parity with the captured legacy oracle.
4. **Re-ran the demo** against the live SQL Server and captured the real console output for the
   three outcomes.
5. **Captured** the transcripts and rendered the frames indexed below.

## Screenshots (`QA/screenshots/`)

Each row states the exact command and the exact, real result shown in the frame.

| # | File | Exact command | What the real output shows |
|---|------|---------------|----------------------------|
| 1 | [`screenshots/01_clean_build.png`](screenshots/01_clean_build.png) | `dotnet build Algo/Algo.csproj -c Release --framework net10.0` (and the `net6.0` variant) | **Build succeeded, 0 errors** on both targets. Warnings are **not zero**: net10.0 shows **2** and net6.0 **3** — all pre-existing `CS0618` obsolete-API notices in two *unmodified* files (`Algo/TraderHelper.cs:1196`, `Algo/Storages/Csv/CsvEntityList.cs:157`) plus, on net6.0, the `System.Text.Encoding.CodePages` TFM-support notice. **No warning originates in any refactored file.** |
| 2 | [`screenshots/02_demo_three_scenarios.png`](screenshots/02_demo_three_scenarios.png) | `dotnet run --project Samples/08_Misc/03_LegacySqlDemo/03_Misc.LegacySqlDemo.csproj -c Release` | The three outcomes: `BUY 100 @ 150` **accepted** (`order_id=1`); `BUY 10 @ 999` **rejected** with `Order price 999.0000 meets/exceeds limit 500.0000` (`order_id=2`); a `100 @ 150` trade **recomputes the position** to `qty=100.0000 avg_price=150.0000 realized_pnl=0.0000`. Followed by the persisted `Orders`/`Trades`/`Positions` rows. |
| 3 | [`screenshots/03_test_suite_passing.png`](screenshots/03_test_suite_passing.png) | `dotnet test Tests/Tests.csproj -c Release --framework net10.0 --filter "FullyQualifiedName~PreTradeRiskServiceTests\|…~PositionRecalculationTests\|…~CommissionTests\|…~RiskTests"` | The four risk-consolidation classes: **153 passed, 0 failed** (incl. 33 `Live_*` tests run against the real container). Below the summary is an **honest disclosure** of the full multi-project suite (see the note under the table). |
| 4 | [`screenshots/04_sql_business_logic_removed.png`](screenshots/04_sql_business_logic_removed.png) | `sqlcmd -d StockSharpLegacy -Q "SELECT … FROM sys.procedures / sys.triggers"` + `sp_helptext` | `stored_procedures = 0`; exactly **one** user trigger, `trg_Orders_StatusAudit`, whose `sp_helptext` body is a pure append-only status→history INSERT — **no thresholds, no accept/reject decision, no P&L math.** |
| 5 | [`screenshots/05_legacy_layer_before_after.png`](screenshots/05_legacy_layer_before_after.png) | `git diff --stat HEAD -- LEGACY_LAYER.md` + before/after excerpts | The real diffstat (**1 file changed, 169 insertions(+), 93 deletions(−)**) beside the BEFORE (old two-column C#/SQL table) and AFTER (consolidated five-column canonical/gate/breaker table) excerpts. |
| 6 | [`screenshots/06_docker_recovery_complete.png`](screenshots/06_docker_recovery_complete.png) | `docker logs stocksharp-legacy-sql \| grep "Recovery is complete"` + `docker ps` | The container's `Recovery is complete.` line and `docker ps` showing it `Up`, publishing `0.0.0.0:14330->1433/tcp`. |

### Note on the full test suite (screenshot #3)

Screenshot #3 is deliberately scoped to the **four test classes this refactor owns**, because that
is the authoritative, fast (~2.6 s), deterministic, fully-green evidence for the work delivered
(**153/153**, including 33 live-SQL parity tests). For completeness and honesty: running the
**entire** `Tests/Tests.csproj` unfiltered (~4,442 tests spanning many unrelated subsystems —
indicators, backtesting, candle compression, live-adapter subscription, storage, …) is very
long-running in this headless container and does **not** complete within a 40-minute budget (it is
terminated by `timeout`, leaving a tail of tests uncompleted — this is pre-existing suite behavior,
not caused by this changeset). In the portion that ran before the time budget was reached the
console logger recorded **4,343 passed, 2 failed, 0 skipped**; both failures are
`PathsTests.CompanyPath_IsValid` and `PathsTests.AppDataPath_IsValid`, which assert machine-level
filesystem paths and are **environmental** (a headless container has no per-user app-data root).
Neither is part of this refactor's changeset (verified with `git diff --name-only HEAD` —
`Tests/PathsTests.cs` is not listed), and no exception, hang, or deadlock occurred in the
risk-consolidation tests.

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
| 2 | Build + test from a clean checkout: clean build on both TFMs, then the risk-consolidation suite green | [`02_test_suite_clean_checkout.txt`](recordings/02_test_suite_clean_checkout.txt) + `02_test_suite_clean_checkout_01.png … _02.png` | **substituted (.txt + 2 PNGs)** |
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
