/*
	StockSharpLegacy - core schema
	----------------------------------------
	Origin: this was split out of the old "OMS_Core" database around the time
	positions moved off the nightly batch process and onto real-time trade
	inserts. Portfolio/Position/Order/Trade tables below are the tables the
	app tier actually depends on. RiskLimits now backs the C# canonical
	RiskLimitSet and the pre-trade gate PreTradeRiskService (namespace
	StockSharp.Algo.Risk), which read this table at runtime - the risk
	decisioning that used to live in T-SQL stored procedures has been
	consolidated into those C# services.

	Run order: 001 -> 002 -> 003 -> 004 (optional seed data).
*/

IF DB_ID(N'StockSharpLegacy') IS NULL
BEGIN
	CREATE DATABASE StockSharpLegacy;
END
GO

USE StockSharpLegacy;
GO

-- filtered indexes (IX_RiskLimits_portfolio/_security below) require these,
-- and sqlcmd/some drivers don't default them the way SSMS does
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================================
-- Portfolios
-- ============================================================================
IF OBJECT_ID(N'dbo.Portfolios', N'U') IS NOT NULL DROP TABLE dbo.Portfolios;
GO

CREATE TABLE dbo.Portfolios
(
	portfolio_id	INT IDENTITY(1,1)		NOT NULL,
	name			NVARCHAR(100)			NOT NULL,
	currency		CHAR(3)					NOT NULL CONSTRAINT DF_Portfolios_currency DEFAULT ('USD'),
	created_date	DATETIME2(3)			NOT NULL CONSTRAINT DF_Portfolios_created_date DEFAULT (SYSUTCDATETIME()),
	is_active		BIT						NOT NULL CONSTRAINT DF_Portfolios_is_active DEFAULT (1),

	CONSTRAINT PK_Portfolios PRIMARY KEY CLUSTERED (portfolio_id),
	CONSTRAINT UQ_Portfolios_name UNIQUE (name)
);
GO

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
IF OBJECT_ID(N'dbo.Securities', N'U') IS NOT NULL DROP TABLE dbo.Securities;
GO

CREATE TABLE dbo.Securities
(
	security_id		INT IDENTITY(1,1)		NOT NULL,
	security_code	NVARCHAR(50)			NOT NULL,	-- e.g. "AAPL"
	board_code		NVARCHAR(20)			NULL,		-- e.g. "NASDAQ"
	security_type	NVARCHAR(20)			NULL,		-- Stock/Future/Option/...

	CONSTRAINT PK_Securities PRIMARY KEY CLUSTERED (security_id),
	CONSTRAINT UQ_Securities_code_board UNIQUE (security_code, board_code)
);
GO

-- ============================================================================
-- RiskLimits
--
-- One row can be scoped to a portfolio, a security, or both (the more
-- specific row wins - the most-specific-row selection precedence is now
-- performed in C# by PreTradeRiskService, which loads the winning row into
-- the canonical RiskLimitSet). A row with both columns NULL is meaningless
-- (which single order would it even apply to?) so it's blocked by
-- CK_RiskLimits_scope.
--
-- All the max_* columns are optional ceilings - NULL/0 means "not enforced",
-- a convention that now carries into the C# canonical RiskLimitSet (and the
-- existing RiskRule classes, where a 0 Commission/Position means "no limit").
-- Whoever configures this table needs to know that convention; it isn't
-- enforced anywhere else.
-- ============================================================================
IF OBJECT_ID(N'dbo.RiskLimits', N'U') IS NOT NULL DROP TABLE dbo.RiskLimits;
GO

CREATE TABLE dbo.RiskLimits
(
	risk_limit_id			INT IDENTITY(1,1)	NOT NULL,
	portfolio_id			INT					NULL,
	security_id				INT					NULL,

	max_order_price			DECIMAL(18,4)		NULL,	-- ceiling on a single order's price   (read into RiskLimitSet; enforced by RiskOrderPriceRule)
	max_order_qty			DECIMAL(18,4)		NULL,	-- ceiling on a single order's qty     (read into RiskLimitSet; enforced by RiskOrderVolumeRule)
	max_order_value			DECIMAL(18,4)		NULL,	-- ceiling on qty*price notional       (read into RiskLimitSet; enforced in C# by RiskOrderValueRule)
	max_position_size		DECIMAL(18,4)		NULL,	-- ceiling on abs(position) post-fill  (read into RiskLimitSet; enforced by RiskPositionSizeRule)
	max_daily_volume		DECIMAL(18,4)		NULL,	-- ceiling on cumulative qty per day   (read into RiskLimitSet; enforced in C# by RiskDailyVolumeRule)
	max_order_freq_count	INT					NULL,	-- max orders per max_order_freq_window_sec (read into RiskLimitSet; enforced by RiskOrderFreqRule.Count)
	max_order_freq_window_sec INT				NULL,	-- window length in seconds                 (read into RiskLimitSet; enforced by RiskOrderFreqRule.Interval)
	max_commission_total	DECIMAL(18,4)		NULL,	-- ceiling on cumulative commission    (read into RiskLimitSet; enforced by RiskCommissionRule/RiskOrderCommissionRule)
	commission_rate			DECIMAL(9,6)		NOT NULL CONSTRAINT DF_RiskLimits_commission_rate DEFAULT (0.0005),

	is_active				BIT					NOT NULL CONSTRAINT DF_RiskLimits_is_active DEFAULT (1),
	effective_date			DATETIME2(3)		NOT NULL CONSTRAINT DF_RiskLimits_effective_date DEFAULT (SYSUTCDATETIME()),

	CONSTRAINT PK_RiskLimits PRIMARY KEY CLUSTERED (risk_limit_id),
	CONSTRAINT FK_RiskLimits_Portfolios FOREIGN KEY (portfolio_id) REFERENCES dbo.Portfolios (portfolio_id),
	CONSTRAINT FK_RiskLimits_Securities FOREIGN KEY (security_id) REFERENCES dbo.Securities (security_id),
	CONSTRAINT CK_RiskLimits_scope CHECK (portfolio_id IS NOT NULL OR security_id IS NOT NULL)
);
GO

CREATE NONCLUSTERED INDEX IX_RiskLimits_portfolio ON dbo.RiskLimits (portfolio_id) WHERE portfolio_id IS NOT NULL;
CREATE NONCLUSTERED INDEX IX_RiskLimits_security ON dbo.RiskLimits (security_id) WHERE security_id IS NOT NULL;
GO

-- ============================================================================
-- Orders
--
-- qty here vs Volume/Quantity on the C# Order object - never got reconciled
-- when this table was created, and by the time anyone noticed it was in
-- three years of stored procs and nobody wanted to touch it. Same story
-- elsewhere in this schema.
-- ============================================================================
IF OBJECT_ID(N'dbo.Orders', N'U') IS NOT NULL DROP TABLE dbo.Orders;
GO

CREATE TABLE dbo.Orders
(
	order_id				BIGINT IDENTITY(1,1)	NOT NULL,
	portfolio_id			INT						NOT NULL,
	security_id				INT						NOT NULL,
	side					CHAR(1)					NOT NULL,	-- 'B' = Buy, 'S' = Sell
	qty						DECIMAL(18,4)			NOT NULL,
	price					DECIMAL(18,4)			NULL,		-- NULL allowed for market orders
	order_type				VARCHAR(10)				NOT NULL CONSTRAINT DF_Orders_order_type DEFAULT ('LIMIT'),
	status					VARCHAR(12)				NOT NULL CONSTRAINT DF_Orders_status DEFAULT ('PENDING'),
	reject_reason			NVARCHAR(200)			NULL,

	-- carried over from the C# side so a support engineer can correlate a row
	-- in this table back to the in-memory Order.TransactionId when chasing a
	-- ticket. Added in a hurry, never made NOT NULL, never back-filled for
	-- older rows.
	external_transaction_id BIGINT					NULL,

	submitted_date			DATETIME2(3)			NOT NULL CONSTRAINT DF_Orders_submitted_date DEFAULT (SYSUTCDATETIME()),
	last_updated			DATETIME2(3)			NOT NULL CONSTRAINT DF_Orders_last_updated DEFAULT (SYSUTCDATETIME()),

	CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED (order_id),
	CONSTRAINT FK_Orders_Portfolios FOREIGN KEY (portfolio_id) REFERENCES dbo.Portfolios (portfolio_id),
	CONSTRAINT FK_Orders_Securities FOREIGN KEY (security_id) REFERENCES dbo.Securities (security_id),
	CONSTRAINT CK_Orders_side CHECK (side IN ('B','S')),
	CONSTRAINT CK_Orders_qty CHECK (qty > 0),
	CONSTRAINT CK_Orders_order_type CHECK (order_type IN ('LIMIT','MARKET')),
	CONSTRAINT CK_Orders_status CHECK (status IN ('PENDING','ACCEPTED','REJECTED','FILLED','PARTFILLED','CANCELLED'))
);
GO

CREATE NONCLUSTERED INDEX IX_Orders_portfolio_submitted ON dbo.Orders (portfolio_id, submitted_date);
CREATE NONCLUSTERED INDEX IX_Orders_security ON dbo.Orders (security_id);
CREATE NONCLUSTERED INDEX IX_Orders_status ON dbo.Orders (status);
GO

-- ============================================================================
-- Trades
--
-- external_trade_id is an optional business idempotency key (the external
-- execution/trade id of a logical fill). It was added so trade recording can be
-- made exactly-once under client and deadlock retries: when a caller supplies it,
-- SqlLegacyOrderGateway.RecordTradeAsync records the fill at most once and a retry
-- cannot double-count the position (QA finding F2; AAP 0.6.4 "exactly once per
-- successful logical trade"). This is still pure storage - the column plus the
-- filtered unique index UQ_Trades_external_trade_id enforce uniqueness only; no
-- business decisioning or P&L math lives in this schema.
-- ============================================================================
IF OBJECT_ID(N'dbo.Trades', N'U') IS NOT NULL DROP TABLE dbo.Trades;
GO

CREATE TABLE dbo.Trades
(
	trade_id		BIGINT IDENTITY(1,1)	NOT NULL,
	order_id		BIGINT					NOT NULL,
	qty				DECIMAL(18,4)			NOT NULL,
	price			DECIMAL(18,4)			NOT NULL,
	executed_date	DATETIME2(3)			NOT NULL CONSTRAINT DF_Trades_executed_date DEFAULT (SYSUTCDATETIME()),

	-- optional business idempotency key; NULL (the default) means "no key" and is
	-- exempt from UQ_Trades_external_trade_id, so unlimited un-keyed fills are allowed
	external_trade_id BIGINT				NULL,

	CONSTRAINT PK_Trades PRIMARY KEY CLUSTERED (trade_id),
	CONSTRAINT FK_Trades_Orders FOREIGN KEY (order_id) REFERENCES dbo.Orders (order_id),
	CONSTRAINT CK_Trades_qty CHECK (qty > 0),
	CONSTRAINT CK_Trades_price CHECK (price > 0)
);
GO

CREATE NONCLUSTERED INDEX IX_Trades_order ON dbo.Trades (order_id);
CREATE NONCLUSTERED INDEX IX_Trades_executed_date ON dbo.Trades (executed_date);
GO

-- Enforces at-most-one trade per business idempotency key. Filtered on IS NOT NULL so
-- NULL (un-keyed) fills are unlimited while any non-null external_trade_id is unique -
-- this is what lets RecordTradeAsync no-op on a duplicate and makes trade recording
-- idempotent under client/deadlock retries (QA finding F2 / AAP 0.6.4). Kept vendor-
-- neutral: PostgreSQL/Aurora support the same partial-unique-index form
-- (CREATE UNIQUE INDEX ... WHERE ...). The filtered index requires
-- SET ANSI_NULLS/QUOTED_IDENTIFIER ON (set at the top of this script).
CREATE UNIQUE NONCLUSTERED INDEX UQ_Trades_external_trade_id ON dbo.Trades (external_trade_id) WHERE external_trade_id IS NOT NULL;
GO

-- ============================================================================
-- Positions
--
-- unrealized_pnl is intentionally NOT maintained by the C# PositionRecalculationService
-- (which now performs the position and realized-P&L recompute) - doing that correctly
-- needs a live market price, which the service does not have. It's refreshed separately
-- by the EOD mark-to-market batch (outside the scope of this brief). Treat this
-- column as stale/EOD-only, not real-time.
-- ============================================================================
IF OBJECT_ID(N'dbo.Positions', N'U') IS NOT NULL DROP TABLE dbo.Positions;
GO

CREATE TABLE dbo.Positions
(
	position_id		INT IDENTITY(1,1)		NOT NULL,
	portfolio_id	INT						NOT NULL,
	security_id		INT						NOT NULL,
	qty				DECIMAL(18,4)			NOT NULL CONSTRAINT DF_Positions_qty DEFAULT (0),		-- signed: >0 long, <0 short
	avg_price		DECIMAL(18,4)			NOT NULL CONSTRAINT DF_Positions_avg_price DEFAULT (0),
	realized_pnl	DECIMAL(18,4)			NOT NULL CONSTRAINT DF_Positions_realized_pnl DEFAULT (0),
	unrealized_pnl	DECIMAL(18,4)			NOT NULL CONSTRAINT DF_Positions_unrealized_pnl DEFAULT (0),
	updated_date	DATETIME2(3)			NOT NULL CONSTRAINT DF_Positions_updated_date DEFAULT (SYSUTCDATETIME()),

	CONSTRAINT PK_Positions PRIMARY KEY CLUSTERED (position_id),
	CONSTRAINT FK_Positions_Portfolios FOREIGN KEY (portfolio_id) REFERENCES dbo.Portfolios (portfolio_id),
	CONSTRAINT FK_Positions_Securities FOREIGN KEY (security_id) REFERENCES dbo.Securities (security_id),
	CONSTRAINT UQ_Positions_portfolio_security UNIQUE (portfolio_id, security_id)
);
GO

-- ============================================================================
-- OrderStatusHistory
--
-- Populated by trg_Orders_StatusAudit (see 003_Triggers.sql). This is the
-- audit/history cascade the compliance team asked for - append-only, nothing
-- in this schema ever updates or deletes from it.
-- ============================================================================
IF OBJECT_ID(N'dbo.OrderStatusHistory', N'U') IS NOT NULL DROP TABLE dbo.OrderStatusHistory;
GO

CREATE TABLE dbo.OrderStatusHistory
(
	history_id		BIGINT IDENTITY(1,1)	NOT NULL,
	order_id		BIGINT					NOT NULL,
	old_status		VARCHAR(12)				NULL,
	new_status		VARCHAR(12)				NOT NULL,
	changed_date	DATETIME2(3)			NOT NULL CONSTRAINT DF_OrderStatusHistory_changed_date DEFAULT (SYSUTCDATETIME()),
	changed_by		NVARCHAR(128)			NOT NULL CONSTRAINT DF_OrderStatusHistory_changed_by DEFAULT (SUSER_SNAME()),

	CONSTRAINT PK_OrderStatusHistory PRIMARY KEY CLUSTERED (history_id),
	CONSTRAINT FK_OrderStatusHistory_Orders FOREIGN KEY (order_id) REFERENCES dbo.Orders (order_id)
);
GO

CREATE NONCLUSTERED INDEX IX_OrderStatusHistory_order ON dbo.OrderStatusHistory (order_id);
GO
