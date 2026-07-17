/*
	StockSharpLegacy - core schema
	----------------------------------------
	Origin: this was split out of the old "OMS_Core" database around the time
	positions moved off the nightly batch process and onto real-time trade
	inserts. Portfolio/Position/Order/Trade tables below are the tables the
	app tier actually depends on. RiskLimits now backs the C# pre-trade risk
	gate PreTradeRiskService (StockSharp.Algo.Risk, Algo/Risk/PreTradeRiskService.cs);
	the SQL stored procedures that used to read this table were removed when the
	risk/position business logic moved to C# (see /LEGACY_LAYER.md).

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
-- Teardown
--
-- Re-running this script is the documented in-place reset (see Database/README.md),
-- so it has to be able to drop every table even when the database already holds
-- data. Tables are dropped CHILD-FIRST here - the exact reverse of the
-- parent-first creation order below - because a table that is still referenced by
-- a FOREIGN KEY cannot be dropped while the referencing children exist. Dropping
-- in creation order instead fails with "Msg 3726 ... could not drop object
-- because it is referenced by a FOREIGN KEY constraint" (FK_OrderStatusHistory_Orders,
-- FK_Trades_Orders, FK_Positions_Portfolios/_Securities, FK_Orders_Portfolios/_Securities,
-- FK_RiskLimits_Portfolios/_Securities), which aborts the reset and leaves stale
-- data behind. On a fresh database every drop below is a harmless no-op.
-- ============================================================================
IF OBJECT_ID(N'dbo.OrderStatusHistory', N'U') IS NOT NULL DROP TABLE dbo.OrderStatusHistory;
IF OBJECT_ID(N'dbo.Trades', N'U') IS NOT NULL DROP TABLE dbo.Trades;
IF OBJECT_ID(N'dbo.Positions', N'U') IS NOT NULL DROP TABLE dbo.Positions;
IF OBJECT_ID(N'dbo.Orders', N'U') IS NOT NULL DROP TABLE dbo.Orders;
IF OBJECT_ID(N'dbo.RiskLimits', N'U') IS NOT NULL DROP TABLE dbo.RiskLimits;
IF OBJECT_ID(N'dbo.Securities', N'U') IS NOT NULL DROP TABLE dbo.Securities;
IF OBJECT_ID(N'dbo.Portfolios', N'U') IS NOT NULL DROP TABLE dbo.Portfolios;
GO

-- ============================================================================
-- Portfolios
-- ============================================================================
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
-- specific row wins - see PreTradeRiskService in Algo/Risk). A row with both columns
-- NULL is meaningless (which single order would it even apply to?) so it's
-- blocked by CK_RiskLimits_scope.
--
-- All the max_* columns are optional ceilings - NULL/0 means "not enforced",
-- same convention the C# RiskRule classes use (a 0 Commission/Position means
-- "no limit"). Whoever configures this table needs to know that convention;
-- it isn't enforced anywhere else.
-- ============================================================================
CREATE TABLE dbo.RiskLimits
(
	risk_limit_id			INT IDENTITY(1,1)	NOT NULL,
	portfolio_id			INT					NULL,
	security_id				INT					NULL,

	max_order_price			DECIMAL(18,4)		NULL,	-- ceiling on a single order's price   (mirrors RiskOrderPriceRule)
	max_order_qty			DECIMAL(18,4)		NULL,	-- ceiling on a single order's qty     (mirrors RiskOrderVolumeRule)
	max_order_value			DECIMAL(18,4)		NULL,	-- ceiling on qty*price notional       (mirrors RiskOrderValueRule)
	max_position_size		DECIMAL(18,4)		NULL,	-- ceiling on abs(position) post-fill  (mirrors RiskPositionSizeRule)
	max_daily_volume		DECIMAL(18,4)		NULL,	-- ceiling on cumulative qty per day   (mirrors RiskDailyVolumeRule)
	max_order_freq_count	INT					NULL,	-- max orders per max_order_freq_window_sec (mirrors RiskOrderFreqRule.Count)
	max_order_freq_window_sec INT				NULL,	-- window length in seconds                 (mirrors RiskOrderFreqRule.Interval)
	max_commission_total	DECIMAL(18,4)		NULL,	-- ceiling on cumulative commission    (mirrors RiskCommissionRule/RiskOrderCommissionRule)
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
-- ============================================================================
CREATE TABLE dbo.Trades
(
	trade_id		BIGINT IDENTITY(1,1)	NOT NULL,
	order_id		BIGINT					NOT NULL,
	qty				DECIMAL(18,4)			NOT NULL,
	price			DECIMAL(18,4)			NOT NULL,
	executed_date	DATETIME2(3)			NOT NULL CONSTRAINT DF_Trades_executed_date DEFAULT (SYSUTCDATETIME()),

	CONSTRAINT PK_Trades PRIMARY KEY CLUSTERED (trade_id),
	CONSTRAINT FK_Trades_Orders FOREIGN KEY (order_id) REFERENCES dbo.Orders (order_id),
	CONSTRAINT CK_Trades_qty CHECK (qty > 0),
	CONSTRAINT CK_Trades_price CHECK (price > 0)
);
GO

CREATE NONCLUSTERED INDEX IX_Trades_order ON dbo.Trades (order_id);
CREATE NONCLUSTERED INDEX IX_Trades_executed_date ON dbo.Trades (executed_date);
GO

-- ============================================================================
-- Positions
--
-- unrealized_pnl is intentionally NOT maintained by the C# position recompute
-- (PositionRecalculationService in Algo/Risk) - doing that correctly needs a live
-- market price, which the recompute path does not have access to. It's refreshed
-- separately by the EOD mark-to-market batch (outside the scope of this brief).
-- Treat this column as stale/EOD-only, not real-time.
-- ============================================================================
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
