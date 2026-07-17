# StockSharpLegacy database

This database is **data storage**: tables, constraints, indexes, the threshold
*values* in `RiskLimits`, and one pure audit trigger. It contains **no
risk-decision logic and no P&L arithmetic**. All pre-trade risk validation and
position/realized-P&L recalculation now live in the canonical C# service layer
under `Algo/Risk` (`PreTradeRiskService`, `PositionRecalculationService`),
consumed through `Algo/Storages/Sql/SqlLegacyOrderGateway`. `RiskLimits` still
*stores* the ceilings (the data), but the comparison and the accept/reject
decision for each rule are defined exactly once, in C#. The only SQL-side
procedural code that remains is `trg_Orders_StatusAudit`, an append-only audit
trigger that copies `Orders` status changes into `OrderStatusHistory` (pure CRUD,
no business rules). This split keeps each risk rule's decision defined once and
keeps the schema portable. See `/LEGACY_LAYER.md` at the repo root for the full
consolidation rationale and the rule-by-rule reconciliation.

Run order: `001_Schema.sql` -> `002_StoredProcedures.sql` -> `003_Triggers.sql` -> `004_SeedData.sql` (optional but the C# sample/demo assumes it has run).

Each script is safe to re-run. `001` drops every table in **reverse foreign-key-dependency order** (children before parents - `OrderStatusHistory`, `Trades`, `Positions`, `Orders`, `RiskLimits`, `Securities`, `Portfolios`) in one up-front block, then recreates them, so re-applying it to an already-populated database succeeds instead of failing with FK error 3726 (which the earlier forward-order, drop-inline layout caused). Be aware `001` is **destructive**: rerunning it against a database with real data wipes it. `002` idempotently `DROP ... IF EXISTS` the three retired procedures; `003` idempotently drops the retired position-recalc trigger and `CREATE OR ALTER`s the audit trigger; `004` checks before inserting. Running `002`/`003` against a fresh database (nothing to drop) and against a legacy build (retired objects present) both run cleanly.

## Stand up a local instance (Docker)

```bash
docker run -d --name stocksharp-legacy-sql \
  -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=DevTest_Passw0rd!" \
  -p 14330:1433 \
  mcr.microsoft.com/mssql/server:2022-latest
```

Wait for it to finish initializing (`docker logs stocksharp-legacy-sql | grep "Recovery is complete"`), then run the scripts, e.g. with `sqlcmd` inside the container:

```bash
# Remove any previous copy first so the target is deterministic: repeating
# `docker cp Database ...:/tmp/Database` onto an existing directory would
# otherwise nest it as /tmp/Database/Database and the loop would keep running
# the stale top-level files. The cleanup runs as root (`-u root`) because
# `docker cp` writes the files owned by root, while the image's default exec
# user is the non-root `mssql` service account, which cannot delete them.
docker exec -u root stocksharp-legacy-sql rm -rf /tmp/Database
docker cp Database stocksharp-legacy-sql:/tmp/Database

# Apply in order, failing fast on the first error: `set -e` stops the loop and
# `sqlcmd -b` returns a non-zero exit code on any SQL error (so a failed script
# is not silently followed by the next one).
set -e
for f in 001_Schema.sql 002_StoredProcedures.sql 003_Triggers.sql 004_SeedData.sql; do
  docker exec stocksharp-legacy-sql /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P 'DevTest_Passw0rd!' -C -b -i "/tmp/Database/$f"
done
```

To **reset** an existing instance to a clean seeded state, re-run the same block: because `001` drops in reverse-dependency order it recreates every table from scratch, and `002`-`004` reinstall the procedures/triggers/seed. (This wipes existing data - it is a dev/test reset, not an upgrade path.)

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

Walks through: ensure portfolio/security rows exist, submit a compliant order (accepted), submit one that breaches the seeded `max_order_price` limit (rejected, with the reason recorded), record a fill against the accepted order, and print the position that `PositionRecalculationService` recomputed. The gateway calls the recompute once per `RecordTradeAsync`, inside the trade's own transaction; supplying an execution key additionally makes a retried fill idempotent (the trade is inserted and its position effect applied exactly once end-to-end).

## What's in each file

| File | Contents |
|---|---|
| `001_Schema.sql` | Tables (`Portfolios`, `Securities`, `RiskLimits`, `Orders`, `Trades`, `Positions`, `OrderStatusHistory`) with their constraints and indexes, the up-front reverse-dependency drop block that makes the script rerunnable, and the filtered unique idempotency-key indexes on `Orders.external_transaction_id` and `Trades.execution_id`. |
| `002_StoredProcedures.sql` | Installs no business logic. Idempotently `DROP ... IF EXISTS` the three retired procedures (`usp_ValidatePreTradeRisk`, `usp_RecalculatePositionOnTrade`, `usp_SubmitOrder`) - that logic now lives in the C# service layer (`Algo/Risk`, `Algo/Storages/Sql`). |
| `003_Triggers.sql` | `trg_Orders_StatusAudit` (AFTER UPDATE on `Orders`) - pure audit CRUD cascading status changes to `OrderStatusHistory`. Idempotently drops the retired `trg_Trades_PositionRecalc`; position/P&L recompute is now a single C# call (`PositionRecalculationService`). |
| `004_SeedData.sql` | One `DEMO` portfolio, two securities (`AAPL`, `MSFT`), one portfolio-wide `RiskLimits` row. |

See `/LEGACY_LAYER.md` at the repo root for what this layer is modeling and why it was added.
