# StockSharpLegacy database

Run order: `001_Schema.sql` -> `002_StoredProcedures.sql` -> `003_Triggers.sql` -> `004_SeedData.sql` (optional but the C# sample/demo assumes it has run).

> **Consolidated state.** The trading-risk business logic that used to live in
> stored procedures and the position-recalc trigger has been relocated into
> unit-tested C# services in `Algo/Risk/` (`PreTradeRiskService`,
> `PositionRecalculationService`) that resolve their thresholds from the
> canonical `RiskLimitSet`. SQL Server is now pure data storage: after the
> scripts run there are **no stored procedures** and the **only trigger is the
> best-effort audit cascade** `trg_Orders_StatusAudit` (a trigger-derived
> status-transition log, not a tamper-proof append-only guarantee - see
> `/LEGACY_LAYER.md`). See `/LEGACY_LAYER.md` for the full rule-by-rule picture.

Each script is safe to re-run and non-destructive: `001` creates every object
only if it is missing - there are **no `DROP TABLE` statements**, so re-running
it neither raises the parent-before-child foreign-key drop error (SQL Msg 3726)
that the old drop-and-recreate form hit on a rerun nor discards existing data;
`002` is a no-logic script that just `DROP`s the relocated procedures if
they exist (so it transitions an already-provisioned database to the consolidated
state and is a harmless no-op on a fresh one); `003` uses `CREATE OR ALTER` for
the audit trigger and drops the removed recalc trigger if present; `004` checks
before inserting. All four are safe to run repeatably against an
already-provisioned database without data loss.

## Stand up a local instance (Docker)

> **Security (review finding MA-13, CWE-668).** Bind the container to the
> **loopback interface only** and use a **per-run secret**. Publishing
> `-p 14330:1433` binds the SA endpoint to *every* interface (`0.0.0.0`),
> exposing the credential to anything that can reach the host; prefixing the
> host IP (`-p 127.0.0.1:14330:1433`) keeps it reachable only from the local
> machine. Generate a fresh password per run instead of committing one, and do
> not paste the real secret into logs, screenshots, or recordings.

```bash
# Generate a per-run secret (do NOT commit it or publish it in evidence).
export MSSQL_SA_PASSWORD="$(openssl rand -base64 24)Aa1!"

docker run -d --name stocksharp-legacy-sql \
  -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=$MSSQL_SA_PASSWORD" \
  -p 127.0.0.1:14330:1433 \
  mcr.microsoft.com/mssql/server:2022-latest
```

The `127.0.0.1:` prefix is the security-relevant part: it publishes the port on
the loopback interface only, so the SA endpoint is not reachable from other
hosts. Verify with `docker port stocksharp-legacy-sql` - it must print
`127.0.0.1:14330`, **not** `0.0.0.0:14330`.

Wait for it to finish initializing (`docker logs stocksharp-legacy-sql | grep "Recovery is complete"`), then run the scripts, e.g. with `sqlcmd` inside the container. Pass the secret through the `SQLCMDPASSWORD` environment variable so it never appears on the command line / process list:

```bash
docker cp Database stocksharp-legacy-sql:/tmp/Database
for f in 001_Schema.sql 002_StoredProcedures.sql 003_Triggers.sql 004_SeedData.sql; do
  docker exec -e "SQLCMDPASSWORD=$MSSQL_SA_PASSWORD" stocksharp-legacy-sql \
    /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -i "/tmp/Database/$f"
done
```

**Least-privilege application login.** SA is for provisioning only. For the
application/demo, create a dedicated login scoped to the `StockSharpLegacy`
tables rather than reusing SA, so a leaked application credential cannot
administer the server (the app performs no DDL at runtime, only DML). The
steady-state runtime privileges are `SELECT, INSERT, UPDATE`: at runtime
`SqlLegacyOrderGateway` only reads, inserts (orders, trades, positions) and
updates (order status and position rows) - it never issues a `DELETE`, so the
runtime principal needs no delete rights:

```sql
CREATE LOGIN stocksharp_app WITH PASSWORD = '<application-secret>';
USE StockSharpLegacy;
CREATE USER stocksharp_app FOR LOGIN stocksharp_app;
GRANT SELECT, INSERT, UPDATE ON SCHEMA::dbo TO stocksharp_app;
```

The one operation that needs `DELETE` is **not** part of steady-state runtime:
it is the demo's repeatable-reset step (`ResetDemoTransactionalStateAsync` in
`Samples/08_Misc/03_LegacySqlDemo/Program.cs`), a dev/maintenance action that
clears the `DEMO` portfolio's disposable transactional rows (`Orders`, `Trades`,
`Positions`, `OrderStatusHistory`) so the walkthrough reproduces the same three
outcomes on every run. Because it is a maintenance concern rather than a runtime
one, run the reset under the **provisioning** login (SA) - which already has the
rights - **or**, if you prefer to run the whole demo as `stocksharp_app`, add a
narrowly scoped disposable `DELETE` on exactly those four transactional tables
(never a schema-wide `DELETE`), which a production runtime principal would *not*
be granted:

```sql
-- Dev convenience only, for running the demo's built-in reset as stocksharp_app.
-- A production runtime principal would NOT be granted these.
GRANT DELETE ON dbo.OrderStatusHistory TO stocksharp_app;
GRANT DELETE ON dbo.Trades             TO stocksharp_app;
GRANT DELETE ON dbo.Positions          TO stocksharp_app;
GRANT DELETE ON dbo.Orders             TO stocksharp_app;
```

Or point any SQL Server client (SSMS, Azure Data Studio, `sqlcmd` from the host) at `127.0.0.1,14330` with the credentials above.

## Connecting from the C# side

`Algo/Storages/Sql/SqlLegacyConnection.Resolve()` reads the `STOCKSHARP_LEGACY_SQL_CONNECTION` environment variable, falling back to a local-development connection string of the form:

```
Server=localhost,14330;Database=StockSharpLegacy;User Id=sa;Password=<sa-password>;TrustServerCertificate=True;
```

Set `STOCKSHARP_LEGACY_SQL_CONNECTION` to your per-run secret - and, outside throwaway local dev, to the least-privilege `stocksharp_app` login shown above - rather than editing the fallback in source or committing a real password. The built-in fallback is a convenience for the local Docker instance only; do not rely on it (or the SA account) outside local development.

## Running the sample

```bash
dotnet run --project Samples/08_Misc/03_LegacySqlDemo
```

Walks through: ensure portfolio/security rows exist, reset the DEMO portfolio's transactional state, submit a compliant order (accepted), submit one that breaches the seeded `max_order_price` limit (rejected, with reason), record a fill against the accepted order, and print the position. The pre-trade decision now comes from `PreTradeRiskService` and the position is recomputed by `PositionRecalculationService`, invoked once per recorded trade from `SqlLegacyOrderGateway.RecordTradeAsync` inside the trade's transaction, not from a stored procedure or trigger.

**Repeatable by design (reset requirement).** Because the demo persists real rows against the seeded `DEMO` portfolio, running it twice would otherwise accumulate state - the printed position would read `qty 200` on the second run, and after enough runs the compliant order would trip the seeded order-frequency limit (5 orders / 60s) and be rejected instead of accepted. To make the three observable outcomes (accept, reject-by-price, position recomputed to `qty 100`) reproducible on **every** run, the sample first clears that portfolio's disposable transactional rows - `Orders`, `Trades`, `Positions`, and the derived `OrderStatusHistory` - in a single FK-safe transaction, while **preserving** the seeded `RiskLimits` row, the portfolio, and the securities. No manual reset between runs is needed; re-running `dotnet run --project Samples/08_Misc/03_LegacySqlDemo` reproduces the identical output each time (the only value that changes is the auto-increment `order_id`).

## What's in each file

| File | Contents |
|---|---|
| `001_Schema.sql` | Tables: `Portfolios`, `Securities`, `RiskLimits`, `Orders`, `Trades`, `Positions`, `OrderStatusHistory`. Pure DDL. |
| `002_StoredProcedures.sql` | No stored procedures. Idempotently `DROP`s the relocated `usp_ValidatePreTradeRisk`, `usp_RecalculatePositionOnTrade`, and `usp_SubmitOrder` (now `PreTradeRiskService` / `PositionRecalculationService` / the gateway in C#). |
| `003_Triggers.sql` | `trg_Orders_StatusAudit` only (AFTER UPDATE on `Orders`, a best-effort status→history audit cascade - not a tamper-proof append-only log). `DROP`s the removed `trg_Trades_PositionRecalc`. |
| `004_SeedData.sql` | One `DEMO` portfolio, two securities (`AAPL`, `MSFT`), one portfolio-wide `RiskLimits` row (values unchanged). |

See `/LEGACY_LAYER.md` at the repo root for what this layer is modeling and why it was added.
