# StockSharpLegacy database

This folder is now **native PostgreSQL 16 DDL only** — pure storage: tables, constraints, indexes, and a single pure *audit* trigger. No business logic remains in the database.

All pre-trade risk validation and position/P&L recalculation now live in the C# services under `Algo/Risk/` — `PreTradeRiskService` (the per-order accept/reject gate) and `PositionRecalculationService` (average-cost position and realized-P&L recompute) — and are driven by `Algo/Storages/Sql/SqlLegacyOrderGateway`, which talks to PostgreSQL with Npgsql. The database keeps only the data and the one audit trigger.

## Init / run order

These `*.sql` files are the container's initialization scripts. The official `postgres` image auto-runs every `*.sql` file placed in `/docker-entrypoint-initdb.d` in **alphabetical order, on first initialization only** (i.e. against a fresh, empty data directory). The effective order is:

`001_Schema.sql` → `003_Triggers.sql` → `004_SeedData.sql` → `005_AppRole.sql`

The `002` gap is intentional and harmless: `002_StoredProcedures.sql` was removed when its business logic moved to the C# services under `Algo/Risk/`, leaving a numbering gap that has no effect on the run.

A few things to know:

- **The scripts neither create nor switch databases.** The container creates the `stocksharp` database itself from the `POSTGRES_DB=stocksharp` environment variable, so the DDL simply builds objects inside it.
- **First init only.** The init scripts run exactly once, against a fresh named volume (`pgdata`); they are *not* re-run on subsequent `up`s. To re-seed from scratch, drop the volume (see `docker-compose down -v` below) and bring the stack up again.
- **`005_AppRole.sql` runs last**, provisioning a least-privilege application login (`app_user`) *after* the schema, triggers, and seed data already exist — see "Connecting from the C# side".

## One-command startup (docker-compose)

From the repository root, a single command brings up PostgreSQL and the demo app together:

```bash
# from the repository root
docker-compose up
```

What happens:

- The **`db`** service (`postgres:16`) initializes, runs the init scripts above, and reports ready only once it is fully initialized and accepting TCP connections — gated by a `pg_isready` healthcheck.
- The **`app`** service waits for `db` to become *healthy* (`depends_on` … `condition: service_healthy`), then runs the console demo against PostgreSQL.

PostgreSQL is published on the host loopback interface only, as `127.0.0.1:5432:5432`, so you can point `psql` or a GUI client at `localhost:5432` while the stack is up.

Tear the stack down — and optionally force a clean re-initialization — with:

```bash
docker-compose down -v   # -v drops the pgdata volume so the init scripts run again on the next up
```

Omit `-v` to stop the containers but keep the data (the init scripts will *not* re-run next time).

## Connecting from the C# side

`Algo/Storages/Sql/SqlLegacyConnection.Resolve()` reads the `STOCKSHARP_LEGACY_SQL_CONNECTION` environment variable and, when it is unset (null / empty / whitespace-only), falls back to a local-dev PostgreSQL connection string:

```
Host=localhost;Port=5432;Database=stocksharp;Username=postgres;Password=postgres;GSS Encryption Mode=Disable
```

- Inside `docker-compose`, the `app` service **overrides** this by setting `STOCKSHARP_LEGACY_SQL_CONNECTION` with `Host=db` — the compose *service name* — instead of `localhost`, so the app reaches the database over the compose network rather than the published host port.
- The stack uses **two roles** (local-development only, deliberately not secret). `postgres` / `postgres` is the bootstrap superuser used to run the init scripts and the healthcheck; the application authenticates as a **least-privilege** `app_user` (created by `005_AppRole.sql`) that holds DML rights on the seven tables only. So the in-compose connection string uses `Username=app_user;Password=app_pw`, while the host fallback above uses the bootstrap `postgres` role for convenience.
- `GSS Encryption Mode=Disable` stops Npgsql from probing for a Kerberos/GSSAPI library on this password-authenticated local stack (none is installed), which would otherwise emit a noisy connect-time error.

Prefer setting the environment variable to point at a different instance rather than editing the fallback in source.

## Running the sample

```bash
dotnet run --project Samples/08_Misc/03_LegacySqlDemo
```

The demo needs a reachable PostgreSQL instance — either the compose `db` service (published at `localhost:5432` on the host), or simply let `docker-compose up` run the demo automatically as the `app` service. It walks through three scenarios:

1. **Submit a compliant order** — one within every configured limit → **accepted**.
2. **Submit an order that breaches the seeded `max_order_price` limit** → **rejected**, with a reason (the `PreTradeRiskService` gate supplies it).
3. **Record a fill against the accepted order** → the position updates **automatically**: `SqlLegacyOrderGateway` inserts the trade and then calls `PositionRecalculationService` exactly once, which recomputes quantity, average price, and realized P&L. The position is updated by that C# `PositionRecalculationService` (the old position-recalculation trigger was removed to guarantee a single apply per trade).

## What's in each file

| File | Contents |
|---|---|
| `001_Schema.sql` | Tables: `Portfolios`, `Securities`, `RiskLimits`, `Orders`, `Trades`, `Positions`, `OrderStatusHistory` (pure storage: constraints + indexes). |
| `003_Triggers.sql` | `trg_Orders_StatusAudit` only (AFTER UPDATE OF status on `Orders`; pure audit → `OrderStatusHistory`). |
| `004_SeedData.sql` | One `DEMO` portfolio, two securities (`AAPL`, `MSFT`), one portfolio-wide `RiskLimits` row. |
| `005_AppRole.sql` | Least-privilege `app_user` login: `CONNECT` + `USAGE` on `public` + DML (`SELECT`/`INSERT`/`UPDATE`/`DELETE`) on the seven tables only — no DDL, no ownership, no superuser. |

Pre-trade risk validation and position/P&L recalculation are **no longer in the database** — they live in `Algo/Risk/PreTradeRiskService.cs` and `Algo/Risk/PositionRecalculationService.cs`, invoked by `Algo/Storages/Sql/SqlLegacyOrderGateway.cs`. In particular, `003_Triggers.sql` no longer contains the old position-recalculation trigger (`trg_Trades_PositionRecalc`); it was removed to establish the single-apply invariant, so the sole `PositionRecalculationService` call per trade can never be double-counted.

See `/LEGACY_LAYER.md` at the repo root for what this layer is modeling and why it was added.
