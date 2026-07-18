# Recording 03 - Parity & position-recalc test suites (timestamped transcript)

> **What this file is.** A timestamped *text transcript*, not a video. This workload is a headless .NET console demo plus a PostgreSQL container, so there is no graphical UI to screen-record. Per the QA-evidence rule (AAP 0.7.2), a timestamped transcript backed by the raw command log is the sanctioned substitute for a recording. Every block below is reproduced verbatim from the committed raw log named in its heading, so the transcript is provably derived from a real run - nothing here is hand-authored output.

Focused transcript of the two in-scope MSTest suites that prove behavioural parity
of the consolidated C# logic, executed against the live PostgreSQL 16 container
(the frequency/day-window and average-cost logic reads real database state). Each
run wrote a TRX result file that is committed alongside the log.

## Timeline (UTC, from the raw logs)

| When (UTC) | Suite | Result |
|------------|-------|--------|
| 00:29:06Z | PreTradeRiskParityTests started | - |
| 00:29:10Z | PreTradeRiskParityTests finished | **Passed 42 / Failed 0 / Skipped 0**, exit 0 |
| 00:29:25Z | PositionRecalculationTests started | - |
| 00:29:29Z | PositionRecalculationTests finished | **Passed 23 / Failed 0 / Skipped 0**, exit 0 |

## PreTradeRiskParityTests - 42 passed, 0 failed, 0 skipped

Source: `QA/logs/05_pretraderisk_parity_tests.log` (TRX: `QA/logs/PreTradeRiskParityTests.trx`)

```
================================================================================
 AUTHENTIC CAPTURE — PreTradeRiskParityTests
================================================================================
 Command : dotnet test Tests/Tests.csproj --no-build -c Release \
             --filter FullyQualifiedName~PreTradeRiskParityTests --logger trx
 Host    : Linux 6.6.122+ x86_64
 .NET SDK: 10.0.302
 DB      : postgres:16 (container stocksharp-review-cp2-db-1) via Host=localhost;Port=5432;Database=stocksharp;Username=postgres  [password redacted]
 MSSQL   : mssql/server:2022 (staged steps 1-2) Server=localhost,14330  [password redacted]
 Started : 2026-07-18T00:29:06Z
================================================================================

Test run for /tmp/blitzy/stocksharp-storedprocedures/blitzy-390f5b52-a9a3-4610-9322-1e65e0fd4d60_f38f36/Tests/bin/Release/net10.0/StockSharp.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
Results File: /tmp/blitzy/stocksharp-storedprocedures/blitzy-390f5b52-a9a3-4610-9322-1e65e0fd4d60_f38f36/QA/logs/PreTradeRiskParityTests.trx

Passed!  - Failed:     0, Passed:    42, Skipped:     0, Total:    42, Duration: 2 s - StockSharp.Tests.dll (net10.0)

Exit code: 0
Finished (UTC): 2026-07-18T00:29:10Z
TRX: QA/logs/PreTradeRiskParityTests.trx
```

## PositionRecalculationTests - 23 passed, 0 failed, 0 skipped

Source: `QA/logs/06_position_recalc_tests.log` (TRX: `QA/logs/PositionRecalculationTests.trx`)

```
================================================================================
 AUTHENTIC CAPTURE — PositionRecalculationTests
================================================================================
 Command : dotnet test Tests/Tests.csproj --no-build -c Release \
             --filter FullyQualifiedName~PositionRecalculationTests --logger trx
 Host    : Linux 6.6.122+ x86_64
 .NET SDK: 10.0.302
 DB      : postgres:16 (container stocksharp-review-cp2-db-1) via Host=localhost;Port=5432;Database=stocksharp;Username=postgres  [password redacted]
 Started : 2026-07-18T00:29:25Z
================================================================================

Test run for /tmp/blitzy/stocksharp-storedprocedures/blitzy-390f5b52-a9a3-4610-9322-1e65e0fd4d60_f38f36/Tests/bin/Release/net10.0/StockSharp.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
Results File: /tmp/blitzy/stocksharp-storedprocedures/blitzy-390f5b52-a9a3-4610-9322-1e65e0fd4d60_f38f36/QA/logs/PositionRecalculationTests.trx

Passed!  - Failed:     0, Passed:    23, Skipped:     0, Total:    23, Duration: 2 s - StockSharp.Tests.dll (net10.0)

Exit code: 0
Finished (UTC): 2026-07-18T00:29:29Z
TRX: QA/logs/PositionRecalculationTests.trx
```

## Outcome

Both suites pass with zero failures and zero skips against live PostgreSQL. The
pass counts (42 and 23) are the real counts for the current suites; the committed
TRX files carry the per-test detail.
