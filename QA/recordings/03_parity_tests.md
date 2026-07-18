# Recording 03 — Parity & position-recalc test suites (timestamped transcript)

> **What this file is.** A timestamped *text transcript*, not a video. Per the
> QA-evidence rule (AAP §0.7.2), a timestamped transcript is the sanctioned
> substitute for a recording. The blocks below are the **verbatim `dotnet test`
> summary lines** captured in the same chronology as `01_end_to_end.md`, run
> against the **same** PostgreSQL 16 `db` container the demo used (reached from
> the host on the remapped published port `55433`). Each run also wrote an MSTest
> `.trx`; those raw result files are **not** committed (they embed absolute
> capture-host paths and the ephemeral pod hostname), so this transcript carries
> the summary of record.

The two in-scope MSTest suites prove behavioural parity of the consolidated C#
logic against the live PostgreSQL engine (the frequency / day-window and
average-cost logic reads real database state).

## Why some tests report *Skipped* here (and that is correct)

The staged four-step validation (AAP §0.6.3) spans **two** engines, gated by two
independent connection variables:

| Variable | Engine | Gates |
|---|---|---|
| `STOCKSHARP_LEGACY_SQL_CONNECTION` | PostgreSQL 16 | the `Step3_*` / `Stage3_*` parity checks and the PostgreSQL half of `Step4` / `Stage4` |
| `STOCKSHARP_LEGACY_MSSQL_CONNECTION` | SQL Server 2022 | the golden-baseline `Step1` / `Step2` / `Step4` and `Stage1` / `Stage2` / `Stage4` |

This capture is **PostgreSQL-only** (it is the containerized migration target;
the Compose stack has no SQL Server). With `STOCKSHARP_LEGACY_MSSQL_CONNECTION`
unset, the SQL-Server-gated golden-baseline tests report **Inconclusive
(Skipped)** — deliberately, *never* a false failure. So the honest single-run
counts below show a small number of skips, each of which is a SQL-Server baseline
test with no reachable SQL Server. To drive the full sequence to **zero skips**,
provision a disposable SQL Server and set both variables as described in
`QA/README.md` → *"The full four-step dual-engine sequence (zero skips)"*.

## Timeline (UTC, 2026-07-18)

| When (UTC) | Suite | Result |
|------------|-------|--------|
| 07:41:49Z | PreTradeRiskParityTests | **Passed 46 / Failed 0 / Skipped 4**, exit 0 |
| 07:42:09Z | PositionRecalculationTests | **Passed 25 / Failed 0 / Skipped 3**, exit 0 |

## PreTradeRiskParityTests — 46 passed, 0 failed, 4 skipped

Command (host-side, against the same Compose `db` on the remapped port 55433):

```
export STOCKSHARP_LEGACY_SQL_CONNECTION="Host=localhost;Port=55433;Database=stocksharp;Username=postgres;Password=postgres"
dotnet test Tests/Tests.csproj --no-build -c Release --filter "FullyQualifiedName~PreTradeRiskParityTests"
```

Verbatim summary line:

```
Passed!  - Failed:     0, Passed:    46, Skipped:     4, Total:    50, Duration: 2 s - StockSharp.Tests.dll (net10.0)
```

The 4 skipped are the SQL-Server golden-baseline checks (`Step1`, `Step2`, the two
`Step4` attribution-matrix cases) — Inconclusive because no SQL Server is
configured in this PostgreSQL-only capture. The 46 passed include the seven
pre-trade checks' PostgreSQL parity plus the adversarial/structural gate
regressions added in this change (malformed input, fail-closed negative limits,
clock-skew via DB transaction time).

## PositionRecalculationTests — 25 passed, 0 failed, 3 skipped

Command:

```
dotnet test Tests/Tests.csproj --no-build -c Release --filter "FullyQualifiedName~PositionRecalculationTests"
```

Verbatim summary line:

```
Passed!  - Failed:     0, Passed:    25, Skipped:     3, Total:    28, Duration: 2 s - StockSharp.Tests.dll (net10.0)
```

The 3 skipped are the SQL-Server golden-baseline stages (`Stage1`, `Stage2`,
`Stage4`). The 25 passed cover the average-cost / realized-P&L recompute parity
(weighted-average in, partial/exact/flip close), the **single-apply** invariant
(atomic per-`trade_id` claim, `unrealized_pnl` left untouched), and the
adversarial regressions added in this change (trade/order mismatch, unknown
trade, replay idempotency, and the mixed submit/fill advisory-lock ordering).

## Outcome

Both suites pass with **zero failures** against the same live PostgreSQL 16
database the demo ran against, in one chronology. The only non-passing results are
the SQL-Server golden-baseline tests, correctly reported as Inconclusive (skipped)
because this capture provisions only the PostgreSQL migration target.
