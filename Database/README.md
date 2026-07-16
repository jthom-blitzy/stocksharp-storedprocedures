# StockSharpLegacy database

This database is **pure data storage**: tables, constraints, and indexes only. All
pre-trade risk validation and position/realized-P&L recalculation now live in the
canonical C# service layer under `Algo/Risk` (`PreTradeRiskService`,
`PositionRecalculationService`), consumed through `Algo/Storages/Sql/SqlLegacyOrderGateway`.
SQL Server holds **no** risk thresholds, accept/reject decisions, or P&L arithmetic.
This keeps every risk rule defined exactly once (in C#) and keeps the schema portable.
See `/LEGACY_LAYER.md` at the repo root for the full consolidation rationale and the
rule-by-rule reconciliation.

Run order: `001_Schema.sql` -> `002_StoredProcedures.sql` -> `003_Triggers.sql` -> `004_SeedData.sql` (optional but the C# sample/demo assumes it has run).

Each script is safe to re-run (`001` drops and recreates every table; `002` idempotently `DROP ... IF EXISTS` the three retired procedures; `003` idempotently drops the retired position-recalc trigger and `CREATE OR ALTER`s the audit trigger; `004` checks before inserting) - convenient when iterating, but be aware `001` is destructive: rerunning it against a database with real data wipes it. Running `002`/`003` against a fresh database (nothing to drop) and against a legacy build (retired objects present) both run cleanly.

## Stand up a local instance (Docker)

```bash
docker run -d --name stocksharp-legacy-sql \
  -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=DevTest_Passw0rd!" \
  -p 14330:1433 \
  mcr.microsoft.com/mssql/server:2022-latest
```

Wait for it to finish initializing (`docker logs stocksharp-legacy-sql | grep "Recovery is complete"`), then run the scripts, e.g. with `sqlcmd` inside the container:

```bash
docker cp Database stocksharp-legacy-sql:/tmp/Database
for f in 001_Schema.sql 002_StoredProcedures.sql 003_Triggers.sql 004_SeedData.sql; do
  docker exec stocksharp-legacy-sql /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P 'DevTest_Passw0rd!' -C -i "/tmp/Database/$f"
done
```

Or point any SQL Server client (SSMS, Azure Data Studio, `sqlcmd` from the host) at `localhost,14330` with the same credentials.

## Connecting from the C# side

`Algo/Storages/Sql/SqlLegacyConnection.Resolve()` reads the `STOCKSHARP_LEGACY_SQL_CONNECTION` environment variable, falling back to a connection string matching the Docker command above:

```
Server=localhost,14330;Database=StockSharpLegacy;User Id=sa;Password=DevTest_Passw0rd!;TrustServerCertificate=True;
```

Set the environment variable to point at a different instance rather than editing the fallback in source.

## Running the sample

```bash
dotnet run --project Samples/08_Misc/03_LegacySqlDemo
```

Walks through: ensure portfolio/security rows exist, submit a compliant order (accepted), submit one that breaches the seeded `max_order_price` limit (rejected, with reason), record a fill against the accepted order, and print the position that `PositionRecalculationService` (invoked once by the gateway inside the trade's transaction) recomputed.

## What's in each file

| File | Contents |
|---|---|
| `001_Schema.sql` | Tables: `Portfolios`, `Securities`, `RiskLimits`, `Orders`, `Trades`, `Positions`, `OrderStatusHistory`. |
| `002_StoredProcedures.sql` | Installs no business logic. Idempotently `DROP ... IF EXISTS` the three retired procedures (`usp_ValidatePreTradeRisk`, `usp_RecalculatePositionOnTrade`, `usp_SubmitOrder`) - that logic now lives in the C# service layer (`Algo/Risk`, `Algo/Storages/Sql`). |
| `003_Triggers.sql` | `trg_Orders_StatusAudit` (AFTER UPDATE on `Orders`) - pure audit CRUD cascading status changes to `OrderStatusHistory`. Idempotently drops the retired `trg_Trades_PositionRecalc`; position/P&L recompute is now a single C# call (`PositionRecalculationService`). |
| `004_SeedData.sql` | One `DEMO` portfolio, two securities (`AAPL`, `MSFT`), one portfolio-wide `RiskLimits` row. |

See `/LEGACY_LAYER.md` at the repo root for what this layer is modeling and why it was added.
