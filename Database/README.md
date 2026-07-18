# StockSharpLegacy database

This folder is now **native PostgreSQL 16 DDL only** — pure storage: tables, constraints, indexes, and a single pure *audit* trigger. No business logic remains in the database.

All pre-trade risk validation and position/P&L recalculation now live in the C# services under `Algo/Risk/` — `PreTradeRiskService` (the per-order accept/reject gate) and `PositionRecalculationService` (average-cost position and realized-P&L recompute) — and are driven by `Algo/Storages/Sql/SqlLegacyOrderGateway`, which talks to PostgreSQL with Npgsql. The database keeps only the data and the one audit trigger.

## Init / run order

These `*.sql` files are the container's initialization scripts. The official `postgres` image auto-runs every `*.sql` file placed in `/docker-entrypoint-initdb.d` in **alphabetical order, on first initialization only** (i.e. against a fresh, empty data directory). The effective order is:

`001_Schema.sql` → `003_Triggers.sql` → `004_SeedData.sql`

These are exactly the three scripts the AAP freezes. The `002` gap is intentional and harmless: `002_StoredProcedures.sql` was removed when its business logic moved to the C# services under `Algo/Risk/`, leaving a numbering gap that has no effect on the run.

A few things to know:

- **The scripts neither create nor switch databases.** The container creates the `stocksharp` database itself from the `POSTGRES_DB=stocksharp` environment variable, so the DDL simply builds objects inside it.
- **First init only.** The init scripts run exactly once, against a fresh named volume (`pgdata`); they are *not* re-run on subsequent `up`s. To re-seed from scratch, drop the volume (see `docker compose down -v` below) and bring the stack up again.

## One-command startup (docker compose)

From the repository root, a single command brings up PostgreSQL and the demo app together:

```bash
# from the repository root
docker compose up
```

What happens:

- The **`db`** service (`postgres:16`) initializes, runs the init scripts above, and reports ready only once it is fully initialized and accepting TCP connections — gated by a `pg_isready` healthcheck.
- The **`app`** service waits for `db` to become *healthy* (`depends_on` … `condition: service_healthy`), then runs the console demo against PostgreSQL.

PostgreSQL is published on the host loopback interface only, as `127.0.0.1:5432:5432`, so you can point `psql` or a GUI client at `localhost:5432` while the stack is up.

Tear the stack down — and optionally force a clean re-initialization — with:

```bash
docker compose down -v   # -v drops the pgdata volume so the init scripts run again on the next up
```

Omit `-v` to stop the containers but keep the data (the init scripts will *not* re-run next time).

## Connecting from the C# side

`Algo/Storages/Sql/SqlLegacyConnection.Resolve()` reads the `STOCKSHARP_LEGACY_SQL_CONNECTION` environment variable and, when it is unset (null / empty / whitespace-only), falls back to a local-dev PostgreSQL connection string:

```
Host=localhost;Port=5432;Database=stocksharp;Username=postgres;Password=postgres;GSS Encryption Mode=Disable;Maximum Pool Size=50
```

- Inside `docker compose`, the `app` service **overrides** this by setting `STOCKSHARP_LEGACY_SQL_CONNECTION` with `Host=db` — the compose *service name* — instead of `localhost`, so the app reaches the database over the compose network rather than the published host port.
- The stack uses a **single role** (local-development only, deliberately not secret): `postgres` / `postgres`, the database's `POSTGRES_USER` / `POSTGRES_PASSWORD`. It runs the init scripts and the healthcheck and — per the frozen AAP (0.4.2), which has the application connect with `POSTGRES_USER` / `POSTGRES_PASSWORD` — is also the role the `app` service uses. Both the in-compose connection string and the host fallback above therefore use `Username=postgres;Password=postgres`.
- `GSS Encryption Mode=Disable` stops Npgsql from probing for a Kerberos/GSSAPI library on this password-authenticated local stack (none is installed), which would otherwise emit a noisy connect-time error.
- `Maximum Pool Size=50` bounds the Npgsql connection pool (default 100) so a single saturated app instance holds at most 50 of the server's `max_connections=200` slots, leaving headroom for a second instance, admin tooling, and the healthcheck. The in-compose `app` connection string sets the same cap, so a host-run demo behaves like the containers.

Prefer setting the environment variable to point at a different instance rather than editing the fallback in source.

## Running the sample

```bash
dotnet run --project Samples/08_Misc/03_LegacySqlDemo
```

The demo needs a reachable PostgreSQL instance — either the compose `db` service (published at `localhost:5432` on the host), or simply let `docker compose up` run the demo automatically as the `app` service. It walks through three scenarios:

1. **Submit a compliant order** — one within every configured limit → **accepted**.
2. **Submit an order that breaches the seeded `max_order_price` limit** → **rejected**, with a reason (the `PreTradeRiskService` gate supplies it).
3. **Record a fill against the accepted order** → the position updates **automatically**: `SqlLegacyOrderGateway` inserts the trade and then calls `PositionRecalculationService`, which recomputes quantity, average price, and realized P&L. The old position-recalculation database trigger was removed, so that C# call is the only apply path (see the accurate single-apply note below for how exactly-once is enforced).

> **Repeatability.** The demo's exact output assumes a **clean** database. Because state persists in the `pgdata` volume, re-running the demo (or the `app` service) against an already-seeded database accumulates the position (qty 100 → 200 → …) and the repeated submissions can trip the seeded 5-orders / 60-seconds frequency limit, changing the order ids and the accept/reject outcomes. Run `docker compose down -v` immediately before any run whose output you want to reproduce exactly.

## What's in each file

| File | Contents |
|---|---|
| `001_Schema.sql` | Tables: `Portfolios`, `Securities`, `RiskLimits`, `Orders`, `Trades`, `Positions`, `OrderStatusHistory` (pure storage: constraints + indexes). |
| `003_Triggers.sql` | `trg_Orders_StatusAudit` only (AFTER UPDATE OF status on `Orders`; pure audit → `OrderStatusHistory`). |
| `004_SeedData.sql` | One `DEMO` portfolio, two securities (`AAPL`, `MSFT`), one portfolio-wide `RiskLimits` row. |

Pre-trade risk validation and position/P&L recalculation are **no longer in the database** — they live in `Algo/Risk/PreTradeRiskService.cs` and `Algo/Risk/PositionRecalculationService.cs`, invoked by `Algo/Storages/Sql/SqlLegacyOrderGateway.cs`. In particular, `003_Triggers.sql` no longer contains the old position-recalculation trigger (`trg_Trades_PositionRecalc`).

**Single-apply per trade — how it is actually enforced.** Removing the trigger eliminates the database-side duplicate apply path, but on its own it does not guarantee exactly-once. `RecordTradeAsync` establishes single-apply through three layered mechanisms:

1. *Transactional atomicity* — the trade `INSERT` and the position `UPDATE` run in one transaction, serialized per portfolio by a `pg_advisory_xact_lock`, so concurrent fills for a portfolio apply one at a time.
2. *Durable per-`trade_id` guard* — a `Trades.position_applied` boolean, flipped by an atomic conditional `UPDATE`, makes re-applying an **already-persisted** `trade_id` (a second apply on the same row, a process restart mid-apply, or a second gateway/service instance racing on that row) match zero rows and become a no-op.
3. *Stable external fill key* — the per-`trade_id` guard alone does **not** cover a *retry of `RecordTradeAsync` itself* after an ambiguous commit or a replay, because a naive retry would `INSERT` a **fresh** `trade_id` for the same real fill and apply it a second time. The idempotency-carrying overload `RecordTradeAsync(orderId, qty, price, externalTradeId)` closes that gap: the fill's stable external id is written to `Trades.external_trade_id`, which carries a **partial** unique index (`UQ_Trades_external_trade_id … WHERE external_trade_id IS NOT NULL`), and the insert uses `ON CONFLICT (external_trade_id) WHERE external_trade_id IS NOT NULL DO NOTHING` — the predicate is repeated because a bare `ON CONFLICT (external_trade_id)` cannot infer a *partial* index. A retry therefore collapses onto the existing trade row instead of creating a duplicate, so the fill is recorded — and applied — exactly once. The original three-argument `RecordTradeAsync(orderId, qty, price)` signature is preserved unchanged (AAP 0.7.1) for callers that do not carry an external key (those legacy fills leave `external_trade_id` NULL and are exempt from the partial index).

This durable, cross-instance behaviour is covered by the `PositionRecalculationTests` idempotency cases (duplicate apply, restart/retry, rollback-then-retry, a post-commit retry keyed by the external fill id, and a concurrent race). `unrealized_pnl` is intentionally left untouched because it needs a live market price.

See `/LEGACY_LAYER.md` at the repo root for what this layer is modeling and why it was added.
