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

    CONSTRAINT PK_Portfolios PRIMARY KEY (portfolio_id),
    CONSTRAINT UQ_Portfolios_name UNIQUE (name)
);

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

    CONSTRAINT PK_Securities PRIMARY KEY (security_id),
    CONSTRAINT UQ_Securities_code_board UNIQUE (security_code, board_code)
);

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
    CONSTRAINT CK_RiskLimits_scope CHECK (portfolio_id IS NOT NULL OR security_id IS NOT NULL)
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
    price                     NUMERIC(18,4)   NULL,       -- NULL allowed for market orders
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

    CONSTRAINT PK_Trades PRIMARY KEY (trade_id),
    CONSTRAINT FK_Trades_Orders FOREIGN KEY (order_id) REFERENCES Orders (order_id),
    CONSTRAINT CK_Trades_qty CHECK (qty > 0),
    CONSTRAINT CK_Trades_price CHECK (price > 0)
);

CREATE INDEX IX_Trades_order ON Trades (order_id);
CREATE INDEX IX_Trades_executed_date ON Trades (executed_date);

-- ============================================================================
-- Positions
--
-- unrealized_pnl is intentionally NOT maintained by PositionRecalculationService
-- - doing that correctly needs a live market price, which the recalculation
-- service (like the old logic before it) does not have access to. It's
-- refreshed separately by the EOD mark-to-market batch (outside the scope of
-- this brief). Treat this column as stale/EOD-only, not real-time.
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
-- ProcessedTrades
--
-- Durable, cross-process single-apply ledger for the C# PositionRecalculationService
-- (AAP 0.6.5). The position-recalc trigger was removed, so that service is the sole
-- applier of a trade's effect on a Position; it claims a trade_id here (INSERT ...
-- ON CONFLICT (trade_id) DO NOTHING) inside the SAME transaction as the position
-- write, so re-applying the same trade - from a second service instance, a process
-- restart, or a concurrent call - is an idempotent no-op at the DATABASE level rather
-- than relying on process-local memory. The PRIMARY KEY on trade_id is what enforces
-- that uniqueness. This is still PURE STORAGE (a uniqueness/audit ledger): it holds no
-- business logic. There is deliberately NO FK to Trades - the guard is keyed purely on
-- the trade_id value, so the applier stays decoupled from the Trades row lifecycle (and
-- so parity tests can drive it with synthetic trade ids that have no Trades row).
-- ============================================================================
CREATE TABLE ProcessedTrades
(
    trade_id       BIGINT        NOT NULL,   -- the applied trade's id (single-apply key)
    -- UTC instant the trade's position effect was applied; same now() at time zone 'utc'
    -- UTC time source and naive-TIMESTAMP convention as every other timestamp here.
    applied_date   TIMESTAMP     NOT NULL DEFAULT (now() at time zone 'utc'),

    CONSTRAINT PK_ProcessedTrades PRIMARY KEY (trade_id)
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
