# QA evidence — StockSharpLegacy SQL→C# risk-consolidation refactor

This folder holds the evidence that the SQL→C# risk-consolidation refactor of the
StockSharpLegacy layer is **complete and behavior-preserving**. After the refactor the
SQL layer no longer contains risk or P&L business logic: the seven pre-trade checks and
the position-recalculation math now live in cohesive C# services under
[`../Algo/Risk/`](../Algo/Risk/) (`PreTradeRiskService`, `PositionRecalculationService`,
the canonical `RiskLimitSet`, plus the new `RiskOrderValueRule` and `RiskDailyVolumeRule`),
with test coverage, while SQL Server is reduced to pure DDL + CRUD. The demo's three
observable outcomes (accept, reject-by-price, trade-triggers-recalc) are preserved
end-to-end.

There is exactly **one intentional, documented behavioral change**: the order-frequency
rule now adopts the stricter rolling-count algorithm instead of the old fixed-window
bucketing, so it is never *less* strict than the two originals. See the reconciliation
writeup in [`../LEGACY_LAYER.md`](../LEGACY_LAYER.md) and the run/setup walkthrough in
[`../Database/README.md`](../Database/README.md).

Each artifact below maps to the exact command or scenario it captures, so the evidence is
reproducible from a clean checkout.

## Environment

The evidence was produced against a local SQL Server 2022 container
(`mcr.microsoft.com/mssql/server:2022-latest`) published on host port **14330**, with the
`StockSharpLegacy` database created by the `Database/` scripts.

Stand up the container:

```bash
docker run -d --name stocksharp-legacy-sql \
  -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=DevTest_Passw0rd!" \
  -p 14330:1433 \
  mcr.microsoft.com/mssql/server:2022-latest
```

Wait until initialization completes:

```bash
docker logs stocksharp-legacy-sql | grep "Recovery is complete"
```

Apply the scripts in run order **001 → 002 → 003 → 004**:

```bash
docker cp Database stocksharp-legacy-sql:/tmp/Database
for f in 001_Schema.sql 002_StoredProcedures.sql 003_Triggers.sql 004_SeedData.sql; do
  docker exec stocksharp-legacy-sql /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P 'DevTest_Passw0rd!' -C -i "/tmp/Database/$f"
done
```

The C# side resolves its connection via the `STOCKSHARP_LEGACY_SQL_CONNECTION`
environment variable, falling back to a string matching the container above:

```
Server=localhost,14330;Database=StockSharpLegacy;User Id=sa;Password=DevTest_Passw0rd!;TrustServerCertificate=True;
```

Set `STOCKSHARP_LEGACY_SQL_CONNECTION` to point at a different instance rather than editing
the fallback in source. Build, test, and run the demo with:

```bash
dotnet build
dotnet test
dotnet run --project Samples/08_Misc/03_LegacySqlDemo
```

Seeded `DEMO` portfolio-wide limits (from `Database/004_SeedData.sql`) used throughout the
evidence:

- `max_order_price = 500`
- `max_order_qty = 10000`
- `max_order_value = 1000000`
- `max_position_size = 100000`
- `max_daily_volume = 250000`
- `max_order_freq_count = 5`
- `max_order_freq_window_sec = 60`
- `max_commission_total = 5000`
- `commission_rate = 0.0005`

The `DEMO` portfolio (USD) and the `AAPL`/`MSFT` (NASDAQ) securities must exist.

## How this evidence was produced (characterization-first)

The refactor and its evidence follow a characterization-first sequence:

1. **Characterize** the current observable behavior with tests — including the edge cases
   (order-frequency window boundary, position-size pre-fill vs post-fill, and commission
   estimate-vs-actual) — captured against the live SQL gate.
2. **Consolidate** the logic into the canonical C# definitions (`RiskLimitSet`) plus the
   two services (`PreTradeRiskService`, `PositionRecalculationService`).
3. **Prove parity**: every rule has at least one passing test, asserting the reconciled
   thresholds are at-least-as-strict and never looser than the originals.
4. **Re-run the demo** against a live SQL Server and capture the real console output for
   the three outcomes.
5. **Capture** the screenshots and recordings indexed below.

## Screenshots (`QA/screenshots/`)

| # | File | What it proves | Command / scenario |
|---|------|----------------|--------------------|
| 1 | [`screenshots/01_clean_build.png`](screenshots/01_clean_build.png) | Clean build with no errors or warnings | `dotnet build` |
| 2 | [`screenshots/02_demo_three_scenarios.png`](screenshots/02_demo_three_scenarios.png) | The demo's three outcomes: accepted `BUY 100 @ 150`; rejected `BUY 10 @ 999` against `max_order_price=500`; trade `100 @ 150` recomputes the position | `dotnet run --project Samples/08_Misc/03_LegacySqlDemo` |
| 3 | [`screenshots/03_test_suite_passing.png`](screenshots/03_test_suite_passing.png) | Full test suite green, including the new `PreTradeRiskServiceTests` and `PositionRecalculationTests` | `dotnet test` |
| 4 | [`screenshots/04_sql_business_logic_removed.png`](screenshots/04_sql_business_logic_removed.png) | The stored procedures and triggers hold no thresholds, accept/reject decisions, or P&L math (CRUD only) | SQL introspection over `sys.procedures` / `sys.triggers` / `sp_helptext` (see query below) |
| 5 | [`screenshots/05_legacy_layer_before_after.png`](screenshots/05_legacy_layer_before_after.png) | The `LEGACY_LAYER.md` rule-by-rule coverage table BEFORE vs AFTER the rewrite to the consolidated state | `git show` of the old table beside the new one |
| 6 | [`screenshots/06_docker_recovery_complete.png`](screenshots/06_docker_recovery_complete.png) | The SQL Server container is healthy ("Recovery is complete") | `docker logs stocksharp-legacy-sql \| grep "Recovery is complete"` |

Example introspection query used for screenshot #4 (proves the procedures/triggers are
CRUD-only):

```sql
SELECT name, type_desc FROM sys.procedures;
SELECT name, type_desc FROM sys.triggers;
EXEC sp_helptext 'dbo.usp_SubmitOrder';
```

## Recordings (`QA/recordings/`)

Each recording is screen-only and under five minutes.

| # | File | Scenario | Duration / notes |
|---|------|----------|------------------|
| 1 | [`recordings/01_end_to_end_docker_scripts_demo.mp4`](recordings/01_end_to_end_docker_scripts_demo.mp4) | End-to-end: `docker run` the SQL container, apply scripts `001 → 004`, then `dotnet run --project Samples/08_Misc/03_LegacySqlDemo` showing the three outcomes | < 5 min, screen-only |
| 2 | [`recordings/02_test_suite_clean_checkout.mp4`](recordings/02_test_suite_clean_checkout.mp4) | `dotnet test` executed from a clean checkout — all green, including the new characterization/parity tests | < 5 min, screen-only |
| 3 | [`recordings/03_coverage_table_to_code_walkthrough.mp4`](recordings/03_coverage_table_to_code_walkthrough.mp4) | Walk the `LEGACY_LAYER.md` coverage-table rows and map each to its C# implementation (`RiskLimitSet`, `PreTradeRiskService`, `RiskOrderPriceRule`, `RiskOrderVolumeRule`, `RiskOrderValueRule`, `RiskOrderFreqRule`, `RiskPositionSizeRule`, `RiskCommissionRule`/`RiskOrderCommissionRule`, `RiskDailyVolumeRule`, and the circuit-breaker-only `RiskPositionTimeRule`/`RiskPnLRule`/`RiskSlippageRule`), plus `PositionRecalculationService` | < 5 min, screen-only |

## Recording substitution note

**Substitution policy (mandatory).** If no video-capture environment is available, each
`recordings/NN_name.mp4` is replaced by a captioned transcript `recordings/NN_name.txt`
**plus** a numbered PNG sequence (`recordings/NN_name_01.png`, `recordings/NN_name_02.png`,
…) that steps through the same scenario. The substitution never uses a `*dates.txt`
filename suffix, because that suffix is git-ignored.

This section is the authoritative record of which form each recording takes. The agent that
populates `QA/recordings/` **must update the table below** to state, per recording, whether
the artifact shipped as an `.mp4` or as the `.txt` + numbered-PNG substitution.

| # | Primary artifact | Substitution artifacts (if no video env) | Status |
|---|------------------|------------------------------------------|--------|
| 1 | `recordings/01_end_to_end_docker_scripts_demo.mp4` | `recordings/01_end_to_end_docker_scripts_demo.txt` + `recordings/01_end_to_end_docker_scripts_demo_NN.png` | **To be confirmed** by the `QA/recordings/` agent — mark `mp4` or `substituted (.txt + PNG)` when produced |
| 2 | `recordings/02_test_suite_clean_checkout.mp4` | `recordings/02_test_suite_clean_checkout.txt` + `recordings/02_test_suite_clean_checkout_NN.png` | **To be confirmed** by the `QA/recordings/` agent — mark `mp4` or `substituted (.txt + PNG)` when produced |
| 3 | `recordings/03_coverage_table_to_code_walkthrough.mp4` | `recordings/03_coverage_table_to_code_walkthrough.txt` + `recordings/03_coverage_table_to_code_walkthrough_NN.png` | **To be confirmed** by the `QA/recordings/` agent — mark `mp4` or `substituted (.txt + PNG)` when produced |

## Large-file / Git LFS note

All artifacts are expected to be small — PNG screenshots and short screen-only recordings
(or the `.txt` + numbered-PNG substitution) — and are committed directly to the repository.
**Only if** any single artifact exceeds ~50 MB is a root-level `.gitattributes` added with
`git lfs track "QA/recordings/*.mp4"`, tracking the `.mp4` files via Git LFS. No
`.gitattributes` lives inside `QA/`.

## Cross-references

- [`../LEGACY_LAYER.md`](../LEGACY_LAYER.md) — the rule-by-rule coverage table for the
  consolidated state.
- [`../Database/README.md`](../Database/README.md) — Docker command, script run order, and
  demo walkthrough.
- [`../Samples/08_Misc/03_LegacySqlDemo/`](../Samples/08_Misc/03_LegacySqlDemo/) — the
  runnable end-to-end demo (the three observable outcomes).
- The consolidated C# services under [`../Algo/Risk/`](../Algo/Risk/):
  [`PreTradeRiskService.cs`](../Algo/Risk/PreTradeRiskService.cs),
  [`PositionRecalculationService.cs`](../Algo/Risk/PositionRecalculationService.cs), and
  [`RiskLimitSet.cs`](../Algo/Risk/RiskLimitSet.cs).
