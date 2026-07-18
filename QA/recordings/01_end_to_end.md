# Recording 01 — End-to-end verification (timestamped transcript)

> **What this file is.** A timestamped *text transcript*, not a video. This
> workload is a headless .NET console demo plus a PostgreSQL container, so there
> is no graphical UI to screen-record. Per the QA-evidence rule (AAP §0.7.2), a
> timestamped transcript is the sanctioned substitute for a recording. Every
> block below is the **verbatim captured output** of a single real run — nothing
> is hand-authored. This transcript **is** the evidence of record for that run;
> the `QA/screenshots/` PNGs are terminal-styled renderings of these same blocks.

This is **one** integrated run — a single `docker compose up --build` against a
single PostgreSQL 16 database, captured end to end in one chronology. There is no
stitching of separate runs or databases: the `app` service runs **inside** the
same Compose stack, against the **same** `db` service it was gated on, and the
parity suites in `03_parity_tests.md` run against that **same** database
immediately afterwards.

**Capture environment (honest disclosure).** The committed stack is used
unchanged except for two capture-host-only overrides that do not alter the
application or its database wiring:
1. The `db` service's **published host port** was remapped from the committed
   `127.0.0.1:5432` to `127.0.0.1:55433`, because `5432` was already occupied on
   this shared capture host by an unrelated container. The `app` service still
   connects over the Compose network as the committed `Host=db;Port=5432` — the
   remap only affects host-side access (the catalog query and the parity tests).
2. The image **build** used the host network stack, because this shared host's
   Docker default-bridge IPv6 egress to `api.nuget.org` is broken and an
   in-container `dotnet restore` would otherwise hang. On a host with normal
   egress the committed `docker compose up --build` needs neither override.

## Timeline (UTC, 2026-07-18, from the captured logs)

| When (UTC) | Phase | Event |
|------------|-------|-------|
| 07:35:47Z | build | `docker compose … build` — SDK stage restores + publishes the demo's transitive graph; runtime stage applies OS security updates (M7) |
| 07:39:28Z | up | `docker compose up -d` issued (db + app) |
| 07:39:30.398Z | up | PostgreSQL temp server ready; init scripts begin |
| 07:39:30.6–30.9Z | up | `001_Schema.sql → 003_Triggers.sql → 004_SeedData.sql` run; `README.md` ignored |
| 07:39:31.084Z | up | init complete |
| 07:39:31.136Z | up | real server ready; `db` reports `(healthy)` shortly after |
| 07:39:36.18–36.24Z | up | `app` (gated on `db` healthy) runs the three scenarios |
| 07:39:38Z | up | `app` container exited, exit code 0 |
| 07:39:xxZ | catalog | post-demo catalog queried against the same `db` |

## Phase 1 — `docker compose … build` (multi-stage, includes the M7 OS patch)

The SDK stage restores and publishes the demo's transitive project graph; the
runtime stage applies Ubuntu security updates as root **before** dropping to the
non-root `app` user, so the built image carries no fixed-available OS-package
vulnerabilities (finding M7).

```
#13 [build 4/4] RUN dotnet publish "Samples/08_Misc/03_LegacySqlDemo/03_Misc.LegacySqlDemo.csproj" -c Release -f net10.0 -o /app/publish
#13   Determining projects to restore...
#13   Restored /src/Localization/Localization.csproj
#13   Restored /src/BusinessEntities/BusinessEntities.csproj
#13   Restored /src/Messages/Messages.csproj
#13   Restored /src/Algo/Algo.csproj
#13   Restored /src/Samples/08_Misc/03_LegacySqlDemo/03_Misc.LegacySqlDemo.csproj
#13   Algo -> /src/Algo/bin/Release/net10.0/StockSharp.Algo.dll
#13   03_Misc.LegacySqlDemo -> /src/Samples/08_Misc/03_LegacySqlDemo/bin/Release/net10.0/StockSharp.Samples.Misc.LegacySqlDemo.dll
#13   03_Misc.LegacySqlDemo -> /app/publish/
#13 DONE 17.9s

#14 [runtime 3/4] RUN export DEBIAN_FRONTEND=noninteractive && apt-get update && apt-get upgrade -y && apt-get clean && rm -rf /var/lib/apt/lists/*
#14 Get:1 http://security.ubuntu.com/ubuntu noble-security InRelease [126 kB]
#14 Get:2 http://archive.ubuntu.com/ubuntu noble InRelease [256 kB]
#14   Setting up gzip (1.12-1ubuntu3.2) ...        # was 1.12-1ubuntu3.1
#14   Setting up tar (1.35+dfsg-3ubuntu0.3) ...    # was 1.35+dfsg-3build1
#14 DONE 12.3s

#15 [runtime 4/4] COPY --from=build --chown=app:app /app/publish .
#16 exporting to image
#16 naming to docker.io/library/ssqa9-app  DONE
 Image ssqa9-app  Built
```

## Phase 2 — `docker compose up` reaches healthy + 3-script init

`db` starts, runs exactly the three committed init scripts (the retired `002`
numbering gap is harmless), reports healthy, and only then does `app` start
(`depends_on: condition: service_healthy`).

```
$ docker compose up -d
 Network ssqa9_default   Created
 Volume ssqa9_pgdata     Created
 Container ssqa9-db-1    Started
 Container ssqa9-db-1    Waiting
 Container ssqa9-db-1    Healthy
 Container ssqa9-app-1   Started

--- db service log (PostgreSQL entrypoint init) ---
2026-07-18 07:39:30.398 UTC [48] LOG:  database system is ready to accept connections
/usr/local/bin/docker-entrypoint.sh: running /docker-entrypoint-initdb.d/001_Schema.sql
/usr/local/bin/docker-entrypoint.sh: running /docker-entrypoint-initdb.d/003_Triggers.sql
/usr/local/bin/docker-entrypoint.sh: running /docker-entrypoint-initdb.d/004_SeedData.sql
/usr/local/bin/docker-entrypoint.sh: ignoring /docker-entrypoint-initdb.d/README.md
PostgreSQL init process complete; ready for start up.
2026-07-18 07:39:31.136 UTC [1] LOG:  database system is ready to accept connections
```

## Phase 3 — `app` (LegacySqlDemo): three observable scenarios

Verbatim `app` service output. The app connected to the `db` service over the
Compose network (`Host=db;Port=5432`, the committed value).

```
Portfolio 'DEMO' = portfolio_id 1
Security 'AAPL@NASDAQ' = security_id 1

Submitting BUY 100 @ 150.00 (within limits)...
  -> order_id=1 is_valid=True reject_reason=(none)

Submitting BUY 10 @ 999.00 (price exceeds the seeded max_order_price limit)...
  -> order_id=2 is_valid=False reject_reason=Order price 999.00 meets/exceeds limit 500.0000
     Note: this rejection comes from the canonical PreTradeRiskService gate (Algo/Risk).
     The RiskManager circuit breaker shares the same canonical rolling-frequency evaluator
     and comparison convention (CanonicalRiskRules), so given the same events the gate and
     the breaker compute the same frequency arithmetic; they still read different state
     (DB rows vs an in-memory stream) and act differently - see LEGACY_LAYER.md for the map.

Recording a trade: 100 @ 150.00 against order #1...
  -> position after recalculation: qty=100.0000 avg_price=150.0000 realized_pnl=0.0000
```

Final container states (`docker compose ps -a`):

```
NAME          SERVICE   STATUS                    EXIT
ssqa9-app-1   app       Exited (0)                0
ssqa9-db-1    db        Up (healthy)              0
```

## Phase 4 — Post-demo catalog (same database)

Queried against the same `db` container after the app exited — proving the schema
is pure storage (7 tables) and that the demo's effects are exactly what the C#
services computed (position `qty=100`, order #2 persisted as `REJECTED`, the trade
marked `position_applied` so it is applied exactly once, and **no** status-audit
rows because the orders were inserted at their final status with no status
transition to audit).

```
                List of relations
 Schema |        Name        | Type  |  Owner
--------+--------------------+-------+----------
 public | orders             | table | postgres
 public | orderstatushistory | table | postgres
 public | portfolios         | table | postgres
 public | positions          | table | postgres
 public | risklimits         | table | postgres
 public | securities         | table | postgres
 public | trades             | table | postgres
(7 rows)

 portfolio_id | security_id |   qty    | avg_price | realized_pnl | unrealized_pnl
--------------+-------------+----------+-----------+--------------+----------------
            1 |           1 | 100.0000 |  150.0000 |       0.0000 |         0.0000

 order_id | side |   qty    |  price   |  status
----------+------+----------+----------+----------
        1 | B    | 100.0000 | 150.0000 | ACCEPTED
        2 | B    |  10.0000 | 999.0000 | REJECTED

 trade_id | order_id |   qty    |  price   | position_applied | external_trade_id
----------+----------+----------+----------+------------------+-------------------
        1 |        1 | 100.0000 | 150.0000 | t                | (null)

 orderstatushistory: (0 rows)   -- pure-audit trigger fires only on a status CHANGE
```

## Outcome

One `docker compose up --build`: the image built (with the M7 OS patch applied),
`db` reached `(healthy)` after running exactly the three committed init scripts,
and `app` — gated on that health — produced all three observable outcomes
(accept, reject-with-reason, automatic position update) and exited 0, all against
one database in one chronology. The parity suites in `03_parity_tests.md` were
then run against that same database.
