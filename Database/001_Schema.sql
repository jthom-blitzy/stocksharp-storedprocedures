/*
	StockSharpLegacy - core schema (PostgreSQL 16)
	----------------------------------------
	Origin: this was split out of the old "OMS_Core" database around the time
	positions moved off the nightly batch process and onto real-time trade
	inserts. Portfolio/Position/Order/Trade tables below are the tables the
	app tier actually depends on. RiskLimits now backs the C# PreTradeRiskService
	gate under Algo/Risk/ - the stored procedures were retired and their logic
	moved to C#, so this schema is now pure storage (tables, constraints, and
	indexes only; no procedures, no business-logic triggers).

	Init order (auto-run by the postgres container from
	/docker-entrypoint-initdb.d, alphabetical, first init only):
	    001_Schema.sql -> 003_Triggers.sql -> 004_SeedData.sql
	The 002 numbering gap is harmless - 002_StoredProcedures.sql was removed
	when its business logic relocated to Algo/Risk/.

	The database itself is created by the postgres container via the POSTGRES_DB
	environment variable, so this script neither creates nor switches databases.
	PostgreSQL uses ';' statement terminators only, so the T-SQL batch separators
	and the ansi-nulls / quoted-identifier session pragmas from the original
	SQL Server script are intentionally dropped (they have no PostgreSQL
	equivalent and would error here). Drop-if-exists guards are omitted on
	purpose too - the init scripts run once against a fresh database, so this is
	clean forward-only DDL.
*/

-- ============================================================================
-- Portfolios
-- ============================================================================
CREATE TABLE Portfolios
(
    -- surrogate PK; GENERATED ALWAYS AS IDENTITY auto-assigns and implies NOT NULL
    portfolio_id   INT GENERATED ALWAYS AS IDENTITY,
    name           VARCHAR(100)  NOT NULL,
    currency       CHAR(3)       NOT NULL DEFAULT 'USD',
    -- Timestamp columns hold UTC instants: the DEFAULT writes now() at time
    -- zone 'utc' (a naive UTC timestamp), so a plain TIMESTAMP preserves the
    -- original UTC semantics without a driver-dependent zone shift. The parens
    -- around the DEFAULT expression are required for it to parse.
    created_date   TIMESTAMP     NOT NULL DEFAULT (now() at time zone 'utc'),
    is_active      BOOLEAN       NOT NULL DEFAULT TRUE,   -- active flag, defaults on

    CONSTRAINT PK_Portfolios PRIMARY KEY (portfolio_id)
);

-- F2: restore the SQL Server default (case-insensitive) collation behaviour for the portfolio name.
-- Under SQL Server the name column used the database's default CI collation, so 'DEMO' and 'demo' were
-- the SAME key; PostgreSQL's VARCHAR comparison is case-SENSITIVE, which let case-variant duplicates
-- persist (the drift this fixes). A functional UNIQUE INDEX on LOWER(name) makes the uniqueness key
-- case-insensitive WITHOUT the citext extension (extension-free and deterministic). The name is still
-- STORED as provided, so display casing is preserved; only the uniqueness key is case-folded. It cannot
-- be an inline table CONSTRAINT (a functional index must be a separate statement). EnsurePortfolioAsync's
-- atomic upsert targets ON CONFLICT (LOWER(name)), which infers THIS index.
CREATE UNIQUE INDEX UQ_Portfolios_name ON Portfolios (LOWER(name));

-- ============================================================================
-- Securities
--
-- Not called out explicitly in the original design doc, but Orders/Positions
-- can't carry an FK to "the instrument" without something to point at. Kept
-- deliberately thin - this table only exists to support the OMS tables in
-- this database, it is NOT meant to be the master security reference (that's
-- still the C#-side Security/ISecurityStorage catalog). security_code is the
-- StockSharp Code@Board id so the two sides can be joined/reconciled.
-- ============================================================================
CREATE TABLE Securities
(
    security_id     INT GENERATED ALWAYS AS IDENTITY,
    security_code   VARCHAR(50)   NOT NULL,   -- e.g. "AAPL"
    board_code      VARCHAR(20)   NULL,       -- e.g. "NASDAQ"
    security_type   VARCHAR(20)   NULL,       -- Stock/Future/Option/...

    CONSTRAINT PK_Securities PRIMARY KEY (security_id)
    -- Uniqueness on (security_code, board_code) is enforced by the case-insensitive FUNCTIONAL
    -- unique index declared just below. A functional (expression) index cannot be written as an
    -- inline table CONSTRAINT, so it must be a separate CREATE UNIQUE INDEX statement.
);

-- M4 (case-insensitive migration parity): the original SQL Server table collated security_code and
-- board_code case-INSENSITIVELY (default CI collation), so 'AAPL'/'NASDAQ' and 'aapl'/'nasdaq' resolved
-- to the SAME instrument. PostgreSQL's VARCHAR comparison is case-SENSITIVE, which silently allowed
-- 'aapl' and 'AAPL' to coexist as two distinct rows - a migration-parity defect (a live probe inserted
-- both). A functional UNIQUE INDEX over LOWER(security_code), LOWER(board_code) restores the original
-- case-folding identity so the two casings collide again. NULLS NOT DISTINCT (PostgreSQL 15+) treats a
-- LOWER(NULL) board value as EQUAL to another so a second (code, NULL board) row is still rejected -
-- reproducing SQL Server's UNIQUE, which also treated NULLs as equal, and preserving the original
-- single-NULL-per-code semantics. The gateway's matching conflict target is
-- ON CONFLICT (LOWER(security_code), LOWER(board_code)) so its upsert infers THIS index (see
-- SqlLegacyOrderGateway.EnsureSecurityAsync and 004_SeedData.sql).
CREATE UNIQUE INDEX UQ_Securities_code_board
    ON Securities (LOWER(security_code), LOWER(board_code)) NULLS NOT DISTINCT;

-- ============================================================================
-- RiskLimits
--
-- One row can be scoped to a portfolio, a security, or both (the more
-- specific row wins - selected in PreTradeRiskService). A row with both columns
-- NULL is meaningless (which single order would it even apply to?) so it's
-- blocked by CK_RiskLimits_scope.
--
-- All the max_* columns are optional ceilings - NULL/0 means "not enforced",
-- same convention the C# RiskRule classes use (a 0 Commission/Position means
-- "no limit"). Whoever configures this table needs to know that convention;
-- it isn't enforced anywhere else.
-- ============================================================================
CREATE TABLE RiskLimits
(
    risk_limit_id                 INT GENERATED ALWAYS AS IDENTITY,
    portfolio_id                  INT             NULL,
    security_id                   INT             NULL,

    -- NUMERIC(18,4) for every money/qty ceiling: exact scale/precision is a
    -- hard NFR - a ceiling stored at a lower scale (or as an inexact/floating
    -- point type) could make a >= risk comparison silently stop triggering.
    max_order_price               NUMERIC(18,4)   NULL,   -- ceiling on a single order's price   (mirrors RiskOrderPriceRule)
    max_order_qty                 NUMERIC(18,4)   NULL,   -- ceiling on a single order's qty     (mirrors RiskOrderVolumeRule)
    max_order_value               NUMERIC(18,4)   NULL,   -- ceiling on qty*price notional       (now enforced by PreTradeRiskService gate)
    max_position_size             NUMERIC(18,4)   NULL,   -- ceiling on abs(position) post-fill  (mirrors RiskPositionSizeRule)
    max_daily_volume              NUMERIC(18,4)   NULL,   -- ceiling on cumulative qty per day   (now enforced by PreTradeRiskService gate)
    max_order_freq_count          INT             NULL,   -- max orders per max_order_freq_window_sec (mirrors RiskOrderFreqRule.Count)
    max_order_freq_window_sec     INT             NULL,   -- window length in seconds                 (mirrors RiskOrderFreqRule.Interval)
    max_commission_total          NUMERIC(18,4)   NULL,   -- ceiling on cumulative commission    (mirrors RiskCommissionRule/RiskOrderCommissionRule)
    commission_rate               NUMERIC(9,6)    NOT NULL DEFAULT 0.0005,   -- NUMERIC(9,6): exact scale is likewise a hard NFR

    is_active                     BOOLEAN         NOT NULL DEFAULT TRUE,
    effective_date                TIMESTAMP       NOT NULL DEFAULT (now() at time zone 'utc'),

    CONSTRAINT PK_RiskLimits PRIMARY KEY (risk_limit_id),
    CONSTRAINT FK_RiskLimits_Portfolios FOREIGN KEY (portfolio_id) REFERENCES Portfolios (portfolio_id),
    CONSTRAINT FK_RiskLimits_Securities FOREIGN KEY (security_id) REFERENCES Securities (security_id),
    CONSTRAINT CK_RiskLimits_scope CHECK (portfolio_id IS NOT NULL OR security_id IS NOT NULL),

    -- M2 (fail-CLOSED configuration): every ceiling is a non-negative magnitude. NULL/0 still means
    -- "not enforced" (the shared C#/SQL convention), but a NEGATIVE ceiling is nonsensical. Previously a
    -- negative value slipped past the DDL and was then silently treated as "disabled" by the `> 0`
    -- enable-predicates - a fail-OPEN hole in which an operator fat-fingering `-1` would DISABLE the control
    -- rather than tighten it (a live all-negative row inserted successfully). Rejecting negatives at the
    -- storage layer makes misconfiguration fail-CLOSED. PreTradeRiskService carries a matching runtime guard
    -- (M2 code fix) that fails closed for any negative limit that predates this constraint or arrives by a
    -- path that bypasses it.
    CONSTRAINT CK_RiskLimits_max_order_price     CHECK (max_order_price      IS NULL OR max_order_price      >= 0),
    CONSTRAINT CK_RiskLimits_max_order_qty       CHECK (max_order_qty        IS NULL OR max_order_qty        >= 0),
    CONSTRAINT CK_RiskLimits_max_order_value     CHECK (max_order_value      IS NULL OR max_order_value      >= 0),
    CONSTRAINT CK_RiskLimits_max_position_size   CHECK (max_position_size    IS NULL OR max_position_size    >= 0),
    CONSTRAINT CK_RiskLimits_max_daily_volume    CHECK (max_daily_volume     IS NULL OR max_daily_volume     >= 0),
    CONSTRAINT CK_RiskLimits_max_commission      CHECK (max_commission_total IS NULL OR max_commission_total >= 0),
    CONSTRAINT CK_RiskLimits_commission_rate     CHECK (commission_rate >= 0),
    CONSTRAINT CK_RiskLimits_freq_count          CHECK (max_order_freq_count      IS NULL OR max_order_freq_count      >= 0),
    CONSTRAINT CK_RiskLimits_freq_window         CHECK (max_order_freq_window_sec IS NULL OR max_order_freq_window_sec >= 0),

    -- Paired-frequency: the rolling order-frequency control is only meaningful when BOTH a count and a
    -- window are present. Allowing one without the other (a count with no window, or a window with no
    -- count) is a half-configured control that silently enforces nothing. Require the pair to be either
    -- both-set or both-NULL. `(a IS NULL) = (b IS NULL)` is TRUE exactly when the two share NULL-ness.
    CONSTRAINT CK_RiskLimits_freq_paired CHECK (
        (max_order_freq_count IS NULL) = (max_order_freq_window_sec IS NULL)
    )
);

-- filtered index -> Postgres partial index (identical WHERE-predicate syntax).
-- Partial indexes need no special session pragmas in PostgreSQL.
CREATE INDEX IX_RiskLimits_portfolio ON RiskLimits (portfolio_id) WHERE portfolio_id IS NOT NULL;
CREATE INDEX IX_RiskLimits_security ON RiskLimits (security_id) WHERE security_id IS NOT NULL;

-- ============================================================================
-- Orders
--
-- qty here vs Volume/Quantity on the C# Order object - never got reconciled
-- when this table was created, and by the time anyone noticed it was in
-- three years of stored procs and nobody wanted to touch it. Same story
-- elsewhere in this schema.
-- ============================================================================
CREATE TABLE Orders
(
    order_id                  BIGINT GENERATED ALWAYS AS IDENTITY,
    portfolio_id              INT             NOT NULL,
    security_id               INT             NOT NULL,
    side                      CHAR(1)         NOT NULL,   -- 'B' = Buy, 'S' = Sell
    qty                       NUMERIC(18,4)   NOT NULL,
    price                     NUMERIC(18,4)   NULL,       -- NULL for market orders; when present must be > 0 (CK_Orders_price)
    order_type                VARCHAR(10)     NOT NULL DEFAULT 'LIMIT',
    status                    VARCHAR(12)     NOT NULL DEFAULT 'PENDING',
    reject_reason             VARCHAR(200)    NULL,

    -- carried over from the C# side so a support engineer can correlate a row
    -- in this table back to the in-memory Order.TransactionId when chasing a
    -- ticket. Added in a hurry, never made NOT NULL, never back-filled for
    -- older rows.
    external_transaction_id   BIGINT          NULL,

    submitted_date            TIMESTAMP       NOT NULL DEFAULT (now() at time zone 'utc'),
    last_updated              TIMESTAMP       NOT NULL DEFAULT (now() at time zone 'utc'),

    CONSTRAINT PK_Orders PRIMARY KEY (order_id),
    CONSTRAINT FK_Orders_Portfolios FOREIGN KEY (portfolio_id) REFERENCES Portfolios (portfolio_id),
    CONSTRAINT FK_Orders_Securities FOREIGN KEY (security_id) REFERENCES Securities (security_id),
    CONSTRAINT CK_Orders_side CHECK (side IN ('B','S')),
    CONSTRAINT CK_Orders_qty CHECK (qty > 0),
    -- A supplied order price must be strictly positive; NULL stays allowed for market orders. Mirrors
    -- CK_Trades_price and the PreTradeRiskService price<=0 guard, so a zero/negative price can neither be
    -- persisted nor bypass the price/notional/commission checks (schema + C# defense-in-depth).
    CONSTRAINT CK_Orders_price CHECK (price IS NULL OR price > 0),
    CONSTRAINT CK_Orders_order_type CHECK (order_type IN ('LIMIT','MARKET')),
    CONSTRAINT CK_Orders_status CHECK (status IN ('PENDING','ACCEPTED','REJECTED','FILLED','PARTFILLED','CANCELLED'))
);

CREATE INDEX IX_Orders_portfolio_submitted ON Orders (portfolio_id, submitted_date);
CREATE INDEX IX_Orders_security ON Orders (security_id);
CREATE INDEX IX_Orders_status ON Orders (status);

-- ============================================================================
-- Trades
-- ============================================================================
CREATE TABLE Trades
(
    trade_id        BIGINT GENERATED ALWAYS AS IDENTITY,
    order_id        BIGINT          NOT NULL,
    qty             NUMERIC(18,4)   NOT NULL,
    price           NUMERIC(18,4)   NOT NULL,
    executed_date   TIMESTAMP       NOT NULL DEFAULT (now() at time zone 'utc'),

    -- Durable single-apply key for position recalculation (replaces the former in-process HashSet guard).
    -- PositionRecalculationService.ApplyAsync flips this FALSE->TRUE with a conditional UPDATE in the SAME
    -- transaction that writes the position, so a trade's position effect is applied exactly once and the
    -- mark commits/rolls back atomically with it. Being persisted, the guarantee survives process
    -- restarts and holds across instances (fixes the non-durable/non-atomic idempotency); a replay of the
    -- same persisted trade_id sees TRUE and becomes a no-op.
    position_applied BOOLEAN        NOT NULL DEFAULT FALSE,

    -- C2 (idempotency / exactly-once fills): OPTIONAL stable external fill identifier supplied by the
    -- upstream execution source (e.g. a broker/venue fill id). position_applied alone only dedups a replay
    -- of the SAME persisted trade_id; it does NOT stop a RETRY of RecordTradeAsync from inserting a
    -- BRAND-NEW trade_id for a fill that already posted, so one real fill could apply twice after an
    -- ambiguous commit/replay. A caller holding a durable external fill key passes it via the 4-arg
    -- RecordTradeAsync overload; the partial UNIQUE index below turns the second insert of the same key
    -- into a no-op (INSERT ... ON CONFLICT DO NOTHING), collapsing retries to exactly one persisted fill.
    -- NULL is allowed and is NOT deduplicated (the partial index skips NULLs), so the legacy 3-arg path -
    -- which has no external key - keeps working unchanged.
    external_trade_id BIGINT        NULL,

    CONSTRAINT PK_Trades PRIMARY KEY (trade_id),
    CONSTRAINT FK_Trades_Orders FOREIGN KEY (order_id) REFERENCES Orders (order_id),
    CONSTRAINT CK_Trades_qty CHECK (qty > 0),
    CONSTRAINT CK_Trades_price CHECK (price > 0)
);

CREATE INDEX IX_Trades_order ON Trades (order_id);
CREATE INDEX IX_Trades_executed_date ON Trades (executed_date);

-- C2: partial UNIQUE index enforcing at-most-one persisted trade per external fill key. Partial
-- (WHERE external_trade_id IS NOT NULL) so the many legacy trades WITHOUT an external key are exempt and
-- unconstrained; only rows that DO carry a key are deduplicated. This is the unique index the 4-arg
-- RecordTradeAsync overload infers. NOTE: to infer a PARTIAL index, the upsert MUST repeat the index
-- predicate in its conflict clause -
--     INSERT ... ON CONFLICT (external_trade_id) WHERE external_trade_id IS NOT NULL DO NOTHING
-- - a bare "ON CONFLICT (external_trade_id)" fails with "no unique or exclusion constraint matching the
-- ON CONFLICT specification" because PostgreSQL will not silently pick a partial index. With the matching
-- WHERE, a post-commit retry of the same fill becomes a no-op (0 rows), making the fill exactly-once.
CREATE UNIQUE INDEX UQ_Trades_external_trade_id
    ON Trades (external_trade_id) WHERE external_trade_id IS NOT NULL;

-- ============================================================================
-- Positions
--
-- unrealized_pnl is intentionally NOT maintained by PositionRecalculationService
-- - doing that correctly needs a live market price, which the recalculation
-- service (like the old logic before it) does not have access to. It's
-- refreshed separately by the EOD mark-to-market batch (outside the scope of
-- this brief). Treat this column as stale/EOD-only, not real-time.
--
-- There is deliberately NO cumulative_gross_notional rollup column. A NUMERIC(18,4) rollup would round
-- every wide qty*price contribution to four decimals BEFORE summing (live: 0.00000001 -> 0.0000), which
-- can under-state the portfolio gross and let the commission ceiling stop triggering - a loosening of a
-- risk control forbidden by the strictness NFR (AAP 0.6.4). Instead the PreTradeRiskService commission
-- gate computes the existing gross EXACTLY as SUM(t.qty * t.price) over the portfolio's Trades
-- (join Trades -> Orders), reproducing the original usp_ValidatePreTradeRisk arithmetic at full NUMERIC
-- precision (PostgreSQL evaluates NUMERIC*NUMERIC and its SUM without intermediate rounding). This keeps
-- the schema pure storage - the C# service owns all arithmetic, and no maintained aggregate can drift.
-- ============================================================================
CREATE TABLE Positions
(
    position_id      INT GENERATED ALWAYS AS IDENTITY,
    portfolio_id     INT             NOT NULL,
    security_id      INT             NOT NULL,
    qty              NUMERIC(18,4)   NOT NULL DEFAULT 0,   -- signed: >0 long, <0 short
    avg_price        NUMERIC(18,4)   NOT NULL DEFAULT 0,
    realized_pnl     NUMERIC(18,4)   NOT NULL DEFAULT 0,
    unrealized_pnl   NUMERIC(18,4)   NOT NULL DEFAULT 0,
    updated_date     TIMESTAMP       NOT NULL DEFAULT (now() at time zone 'utc'),

    CONSTRAINT PK_Positions PRIMARY KEY (position_id),
    CONSTRAINT FK_Positions_Portfolios FOREIGN KEY (portfolio_id) REFERENCES Portfolios (portfolio_id),
    CONSTRAINT FK_Positions_Securities FOREIGN KEY (security_id) REFERENCES Securities (security_id),
    CONSTRAINT UQ_Positions_portfolio_security UNIQUE (portfolio_id, security_id)
);

-- ============================================================================
-- OrderStatusHistory
--
-- Populated by trg_Orders_StatusAudit (see 003_Triggers.sql). This is the
-- audit/history cascade the compliance team asked for - append-only, nothing
-- in this schema ever updates or deletes from it.
-- ============================================================================
CREATE TABLE OrderStatusHistory
(
    history_id     BIGINT GENERATED ALWAYS AS IDENTITY,
    order_id       BIGINT        NOT NULL,
    old_status     VARCHAR(12)   NULL,
    new_status     VARCHAR(12)   NOT NULL,
    changed_date   TIMESTAMP     NOT NULL DEFAULT (now() at time zone 'utc'),
    -- changed_by defaults to current_user - the DB login/role, resolved at
    -- INSERT time (e.g. 'postgres'). trg_Orders_StatusAudit does not set this
    -- column, so it relies on this default to record who made the change.
    changed_by     VARCHAR(128)  NOT NULL DEFAULT current_user,

    CONSTRAINT PK_OrderStatusHistory PRIMARY KEY (history_id),
    CONSTRAINT FK_OrderStatusHistory_Orders FOREIGN KEY (order_id) REFERENCES Orders (order_id)
);

CREATE INDEX IX_OrderStatusHistory_order ON OrderStatusHistory (order_id);
