/*
	StockSharp legacy layer - ORIGINAL (pre-refactor) SQL Server characterization setup
	===================================================================================
	This file is the *golden baseline* used by the staged four-step validation
	described in the Agent Action Plan (AAP §0.6.3). It re-creates - on a live
	SQL Server instance - the subset of the original T-SQL schema and the two
	business-logic stored procedures that were retired from the repository when
	their logic was consolidated into C# (Database/002_StoredProcedures.sql was
	removed). It is embedded verbatim into the Tests assembly and executed by the
	parity tests so that:

	  * Step 1 captures the behaviour of the ORIGINAL dbo.usp_ValidatePreTradeRisk
	    and dbo.usp_RecalculatePositionOnTrade against SQL Server (the golden truth).
	  * Step 2 replays the SAME scenarios through the consolidated C# decision core
	    while the ENGINE is held constant at SQL Server (isolates logic regressions).

	It is intentionally NON-DESTRUCTIVE and idempotent:
	  * tables are created only when absent (IF OBJECT_ID(...) IS NULL) - existing
	    data on a shared instance is never dropped;
	  * indexes are guarded by sys.indexes existence checks;
	  * procedures use CREATE OR ALTER so re-running simply refreshes the definition.

	The two procedures are reproduced byte-for-byte from the pre-refactor
	Database/002_StoredProcedures.sql (git-preserved), because the whole point of
	Step 1 is to characterize the ORIGINAL behaviour, divergences and all (notably
	the "0 rejects" semantics of the SQL IS NOT NULL guards, which the C# side
	deliberately treats as "unlimited" - see AAP §0.6.4). Do NOT "fix" them here.

	Batches are separated by the standard `GO` directive; the test harness splits
	on GO and executes each batch as its own SqlCommand (CREATE PROCEDURE must be
	the first statement in its batch).
*/

-- Filtered indexes on RiskLimits require these session options to be ON for any
-- INSERT/UPDATE/DELETE against the table. SqlClient defaults them ON already;
-- set them explicitly so the script is correct regardless of connection state.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================================
-- Portfolios
-- ============================================================================
IF OBJECT_ID(N'dbo.Portfolios', N'U') IS NULL
BEGIN
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
END
GO

-- ============================================================================
-- Securities
-- ============================================================================
IF OBJECT_ID(N'dbo.Securities', N'U') IS NULL
BEGIN
	CREATE TABLE dbo.Securities
	(
		security_id		INT IDENTITY(1,1)		NOT NULL,
		security_code	NVARCHAR(50)			NOT NULL,
		board_code		NVARCHAR(20)			NULL,
		security_type	NVARCHAR(20)			NULL,

		CONSTRAINT PK_Securities PRIMARY KEY CLUSTERED (security_id),
		CONSTRAINT UQ_Securities_code_board UNIQUE (security_code, board_code)
	);
END
GO

-- ============================================================================
-- RiskLimits (NULL/0 max_* means "not enforced" on the SQL side; note the SQL
-- guards below use IS NOT NULL only, so a literal 0 ceiling REJECTS on SQL -
-- this is the documented divergence from the C# "0 == unlimited" convention)
-- ============================================================================
IF OBJECT_ID(N'dbo.RiskLimits', N'U') IS NULL
BEGIN
	CREATE TABLE dbo.RiskLimits
	(
		risk_limit_id			INT IDENTITY(1,1)	NOT NULL,
		portfolio_id			INT					NULL,
		security_id				INT					NULL,

		max_order_price			DECIMAL(18,4)		NULL,
		max_order_qty			DECIMAL(18,4)		NULL,
		max_order_value			DECIMAL(18,4)		NULL,
		max_position_size		DECIMAL(18,4)		NULL,
		max_daily_volume		DECIMAL(18,4)		NULL,
		max_order_freq_count	INT					NULL,
		max_order_freq_window_sec INT				NULL,
		max_commission_total	DECIMAL(18,4)		NULL,
		commission_rate			DECIMAL(9,6)		NOT NULL CONSTRAINT DF_RiskLimits_commission_rate DEFAULT (0.0005),

		is_active				BIT					NOT NULL CONSTRAINT DF_RiskLimits_is_active DEFAULT (1),
		effective_date			DATETIME2(3)		NOT NULL CONSTRAINT DF_RiskLimits_effective_date DEFAULT (SYSUTCDATETIME()),

		CONSTRAINT PK_RiskLimits PRIMARY KEY CLUSTERED (risk_limit_id),
		CONSTRAINT FK_RiskLimits_Portfolios FOREIGN KEY (portfolio_id) REFERENCES dbo.Portfolios (portfolio_id),
		CONSTRAINT FK_RiskLimits_Securities FOREIGN KEY (security_id) REFERENCES dbo.Securities (security_id),
		CONSTRAINT CK_RiskLimits_scope CHECK (portfolio_id IS NOT NULL OR security_id IS NOT NULL)
	);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RiskLimits_portfolio' AND object_id = OBJECT_ID(N'dbo.RiskLimits'))
	CREATE NONCLUSTERED INDEX IX_RiskLimits_portfolio ON dbo.RiskLimits (portfolio_id) WHERE portfolio_id IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RiskLimits_security' AND object_id = OBJECT_ID(N'dbo.RiskLimits'))
	CREATE NONCLUSTERED INDEX IX_RiskLimits_security ON dbo.RiskLimits (security_id) WHERE security_id IS NOT NULL;
GO

-- ============================================================================
-- Orders
-- ============================================================================
IF OBJECT_ID(N'dbo.Orders', N'U') IS NULL
BEGIN
	CREATE TABLE dbo.Orders
	(
		order_id				BIGINT IDENTITY(1,1)	NOT NULL,
		portfolio_id			INT						NOT NULL,
		security_id				INT						NOT NULL,
		side					CHAR(1)					NOT NULL,
		qty						DECIMAL(18,4)			NOT NULL,
		price					DECIMAL(18,4)			NULL,
		order_type				VARCHAR(10)				NOT NULL CONSTRAINT DF_Orders_order_type DEFAULT ('LIMIT'),
		status					VARCHAR(12)				NOT NULL CONSTRAINT DF_Orders_status DEFAULT ('PENDING'),
		reject_reason			NVARCHAR(200)			NULL,
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
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Orders_portfolio_submitted' AND object_id = OBJECT_ID(N'dbo.Orders'))
	CREATE NONCLUSTERED INDEX IX_Orders_portfolio_submitted ON dbo.Orders (portfolio_id, submitted_date);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Orders_security' AND object_id = OBJECT_ID(N'dbo.Orders'))
	CREATE NONCLUSTERED INDEX IX_Orders_security ON dbo.Orders (security_id);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Orders_status' AND object_id = OBJECT_ID(N'dbo.Orders'))
	CREATE NONCLUSTERED INDEX IX_Orders_status ON dbo.Orders (status);
GO

-- ============================================================================
-- Trades
-- ============================================================================
IF OBJECT_ID(N'dbo.Trades', N'U') IS NULL
BEGIN
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
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Trades_order' AND object_id = OBJECT_ID(N'dbo.Trades'))
	CREATE NONCLUSTERED INDEX IX_Trades_order ON dbo.Trades (order_id);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Trades_executed_date' AND object_id = OBJECT_ID(N'dbo.Trades'))
	CREATE NONCLUSTERED INDEX IX_Trades_executed_date ON dbo.Trades (executed_date);
GO

-- ============================================================================
-- Positions (unrealized_pnl intentionally NOT maintained by the proc below -
-- it needs a live market price, refreshed by the EOD batch only)
-- ============================================================================
IF OBJECT_ID(N'dbo.Positions', N'U') IS NULL
BEGIN
	CREATE TABLE dbo.Positions
	(
		position_id		INT IDENTITY(1,1)		NOT NULL,
		portfolio_id	INT						NOT NULL,
		security_id		INT						NOT NULL,
		qty				DECIMAL(18,4)			NOT NULL CONSTRAINT DF_Positions_qty DEFAULT (0),
		avg_price		DECIMAL(18,4)			NOT NULL CONSTRAINT DF_Positions_avg_price DEFAULT (0),
		realized_pnl	DECIMAL(18,4)			NOT NULL CONSTRAINT DF_Positions_realized_pnl DEFAULT (0),
		unrealized_pnl	DECIMAL(18,4)			NOT NULL CONSTRAINT DF_Positions_unrealized_pnl DEFAULT (0),
		updated_date	DATETIME2(3)			NOT NULL CONSTRAINT DF_Positions_updated_date DEFAULT (SYSUTCDATETIME()),

		CONSTRAINT PK_Positions PRIMARY KEY CLUSTERED (position_id),
		CONSTRAINT FK_Positions_Portfolios FOREIGN KEY (portfolio_id) REFERENCES dbo.Portfolios (portfolio_id),
		CONSTRAINT FK_Positions_Securities FOREIGN KEY (security_id) REFERENCES dbo.Securities (security_id),
		CONSTRAINT UQ_Positions_portfolio_security UNIQUE (portfolio_id, security_id)
	);
END
GO

-- ============================================================================
-- usp_ValidatePreTradeRisk - ORIGINAL pre-trade gate (7 checks). Reproduced
-- verbatim from the retired Database/002_StoredProcedures.sql. The `>=`
-- meets-or-exceeds semantics and the IS NOT NULL (0 rejects) guards are
-- preserved deliberately as the golden Step-1 behaviour.
-- ============================================================================
CREATE OR ALTER PROCEDURE dbo.usp_ValidatePreTradeRisk
	@portfolio_id			INT,
	@security_id			INT,
	@side					CHAR(1),
	@qty					DECIMAL(18,4),
	@price					DECIMAL(18,4)	= NULL,
	@order_type				VARCHAR(10)		= 'LIMIT',
	@requested_by			NVARCHAR(50)	= NULL,
	@is_valid				BIT OUTPUT,
	@reject_reason			NVARCHAR(200) OUTPUT
AS
BEGIN
	SET NOCOUNT ON;

	SET @is_valid = 1;
	SET @reject_reason = NULL;

	IF @side NOT IN ('B','S')
	BEGIN
		SET @is_valid = 0;
		SET @reject_reason = 'Invalid side: ' + @side;
		RETURN;
	END

	IF @qty IS NULL OR @qty <= 0
	BEGIN
		SET @is_valid = 0;
		SET @reject_reason = 'Invalid qty';
		RETURN;
	END

	DECLARE
		@max_order_price			DECIMAL(18,4),
		@max_order_qty				DECIMAL(18,4),
		@max_order_value			DECIMAL(18,4),
		@max_position_size			DECIMAL(18,4),
		@max_daily_volume			DECIMAL(18,4),
		@max_order_freq_count		INT,
		@max_order_freq_window_sec	INT,
		@max_commission_total		DECIMAL(18,4),
		@commission_rate			DECIMAL(9,6);

	SELECT TOP (1)
		@max_order_price = max_order_price,
		@max_order_qty = max_order_qty,
		@max_order_value = max_order_value,
		@max_position_size = max_position_size,
		@max_daily_volume = max_daily_volume,
		@max_order_freq_count = max_order_freq_count,
		@max_order_freq_window_sec = max_order_freq_window_sec,
		@max_commission_total = max_commission_total,
		@commission_rate = commission_rate
	FROM dbo.RiskLimits
	WHERE is_active = 1
		AND (portfolio_id = @portfolio_id OR portfolio_id IS NULL)
		AND (security_id = @security_id OR security_id IS NULL)
	ORDER BY
		CASE WHEN portfolio_id IS NOT NULL AND security_id IS NOT NULL THEN 0
			 WHEN portfolio_id IS NOT NULL THEN 1
			 ELSE 2 END,
		effective_date DESC;

	IF @max_order_price IS NULL AND @max_order_qty IS NULL AND @max_order_value IS NULL
		AND @max_position_size IS NULL AND @max_daily_volume IS NULL
		AND @max_order_freq_count IS NULL AND @max_commission_total IS NULL
	BEGIN
		RETURN;
	END

	-- 1. order price ceiling
	IF @is_valid = 1 AND @price IS NOT NULL AND @max_order_price IS NOT NULL AND @price >= @max_order_price
	BEGIN
		SET @is_valid = 0;
		SET @reject_reason = 'Order price ' + CONVERT(VARCHAR(30), @price) + ' meets/exceeds limit ' + CONVERT(VARCHAR(30), @max_order_price);
	END

	-- 2. order qty ceiling
	IF @is_valid = 1 AND @max_order_qty IS NOT NULL AND @qty >= @max_order_qty
	BEGIN
		SET @is_valid = 0;
		SET @reject_reason = 'Order qty ' + CONVERT(VARCHAR(30), @qty) + ' meets/exceeds limit ' + CONVERT(VARCHAR(30), @max_order_qty);
	END

	-- 3. notional order value ceiling (qty * price)
	IF @is_valid = 1 AND @price IS NOT NULL AND @max_order_value IS NOT NULL AND (@qty * @price) >= @max_order_value
	BEGIN
		SET @is_valid = 0;
		SET @reject_reason = 'Order value ' + CONVERT(VARCHAR(30), @qty * @price) + ' meets/exceeds limit ' + CONVERT(VARCHAR(30), @max_order_value);
	END

	-- 4. order frequency (true rolling window: COUNT over "now minus N seconds")
	IF @is_valid = 1 AND @max_order_freq_count IS NOT NULL AND @max_order_freq_window_sec IS NOT NULL
	BEGIN
		DECLARE @recentOrderCount INT;

		SELECT @recentOrderCount = COUNT(*)
		FROM dbo.Orders
		WHERE portfolio_id = @portfolio_id
			AND submitted_date >= DATEADD(SECOND, -@max_order_freq_window_sec, SYSUTCDATETIME());

		IF @recentOrderCount + 1 >= @max_order_freq_count
		BEGIN
			SET @is_valid = 0;
			SET @reject_reason = 'Order frequency ' + CONVERT(VARCHAR(10), @recentOrderCount + 1) + ' in ' + CONVERT(VARCHAR(10), @max_order_freq_window_sec) + 's meets/exceeds limit ' + CONVERT(VARCHAR(10), @max_order_freq_count);
		END
	END

	-- 5. resulting position size ceiling (hypothetical post-fill)
	IF @is_valid = 1 AND @max_position_size IS NOT NULL
	BEGIN
		DECLARE @currentQty DECIMAL(18,4) = 0, @signedDelta DECIMAL(18,4);

		SELECT @currentQty = qty FROM dbo.Positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id;
		SET @signedDelta = CASE WHEN @side = 'B' THEN @qty ELSE -@qty END;

		IF ABS(ISNULL(@currentQty, 0) + @signedDelta) >= @max_position_size
		BEGIN
			SET @is_valid = 0;
			SET @reject_reason = 'Resulting position ' + CONVERT(VARCHAR(30), ISNULL(@currentQty, 0) + @signedDelta) + ' meets/exceeds limit ' + CONVERT(VARCHAR(30), @max_position_size);
		END
	END

	-- 6. cumulative commission ceiling (estimate)
	IF @is_valid = 1 AND @max_commission_total IS NOT NULL
	BEGIN
		DECLARE @estPrice DECIMAL(18,4) = @price, @existingNotional DECIMAL(18,4), @estCommission DECIMAL(18,4);

		IF @estPrice IS NULL
		BEGIN
			SELECT TOP (1) @estPrice = t.price
			FROM dbo.Trades t
			JOIN dbo.Orders o ON o.order_id = t.order_id
			WHERE o.security_id = @security_id
			ORDER BY t.executed_date DESC;
		END

		SELECT @existingNotional = SUM(t.qty * t.price)
		FROM dbo.Trades t
		JOIN dbo.Orders o ON o.order_id = t.order_id
		WHERE o.portfolio_id = @portfolio_id;

		SET @estCommission = (ISNULL(@existingNotional, 0) * @commission_rate) + (@qty * ISNULL(@estPrice, 0) * @commission_rate);

		IF @estCommission >= @max_commission_total
		BEGIN
			SET @is_valid = 0;
			SET @reject_reason = 'Estimated cumulative commission ' + CONVERT(VARCHAR(30), @estCommission) + ' meets/exceeds limit ' + CONVERT(VARCHAR(30), @max_commission_total);
		END
	END

	-- 7. daily traded volume ceiling
	IF @is_valid = 1 AND @max_daily_volume IS NOT NULL
	BEGIN
		DECLARE @todayQty DECIMAL(18,4);

		SELECT @todayQty = SUM(qty)
		FROM dbo.Orders
		WHERE portfolio_id = @portfolio_id
			AND security_id = @security_id
			AND status IN ('ACCEPTED','FILLED','PARTFILLED')
			AND CAST(submitted_date AS DATE) = CAST(SYSUTCDATETIME() AS DATE);

		IF ISNULL(@todayQty, 0) + @qty >= @max_daily_volume
		BEGIN
			SET @is_valid = 0;
			SET @reject_reason = 'Daily volume ' + CONVERT(VARCHAR(30), ISNULL(@todayQty, 0) + @qty) + ' meets/exceeds limit ' + CONVERT(VARCHAR(30), @max_daily_volume);
		END
	END
END
GO

-- ============================================================================
-- usp_RecalculatePositionOnTrade - ORIGINAL average-cost recompute. Reproduced
-- verbatim. Call it DIRECTLY (without inserting a Trades row) in Step 1 so the
-- retired trg_Trades_PositionRecalc trigger - which is NOT created by this
-- setup - can never double-count.
-- ============================================================================
CREATE OR ALTER PROCEDURE dbo.usp_RecalculatePositionOnTrade
	@order_id		BIGINT,
	@trade_qty		DECIMAL(18,4),
	@trade_price	DECIMAL(18,4)
AS
BEGIN
	SET NOCOUNT ON;

	DECLARE @portfolio_id INT, @security_id INT, @side CHAR(1);

	SELECT @portfolio_id = portfolio_id, @security_id = security_id, @side = side
	FROM dbo.Orders WHERE order_id = @order_id;

	IF @portfolio_id IS NULL
	BEGIN
		RAISERROR('usp_RecalculatePositionOnTrade: order_id %d not found', 16, 1, @order_id);
		RETURN;
	END

	DECLARE @tradeSignedQty DECIMAL(18,4) = CASE WHEN @side = 'B' THEN @trade_qty ELSE -@trade_qty END;
	DECLARE @existingQty DECIMAL(18,4) = 0, @existingAvgPrice DECIMAL(18,4) = 0, @existingRealizedPnl DECIMAL(18,4) = 0;
	DECLARE @positionExists BIT = 0;

	SELECT
		@existingQty = qty,
		@existingAvgPrice = avg_price,
		@existingRealizedPnl = realized_pnl,
		@positionExists = 1
	FROM dbo.Positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id;

	DECLARE @newQty DECIMAL(18,4), @newAvgPrice DECIMAL(18,4), @newRealizedPnl DECIMAL(18,4) = @existingRealizedPnl;

	IF @existingQty = 0 OR SIGN(@existingQty) = SIGN(@tradeSignedQty)
	BEGIN
		SET @newQty = @existingQty + @tradeSignedQty;
		SET @newAvgPrice = (ABS(@existingQty) * @existingAvgPrice + @trade_qty * @trade_price) / ABS(@newQty);
	END
	ELSE
	BEGIN
		DECLARE @closingQty DECIMAL(18,4) = CASE WHEN ABS(@existingQty) < @trade_qty THEN ABS(@existingQty) ELSE @trade_qty END;
		DECLARE @remainingQty DECIMAL(18,4) = @trade_qty - @closingQty;

		SET @newRealizedPnl = @existingRealizedPnl + (@closingQty * (@trade_price - @existingAvgPrice) * SIGN(@existingQty));

		IF @remainingQty = 0
		BEGIN
			SET @newQty = @existingQty + @tradeSignedQty;
			SET @newAvgPrice = CASE WHEN @newQty = 0 THEN 0 ELSE @existingAvgPrice END;
		END
		ELSE
		BEGIN
			SET @newQty = SIGN(@tradeSignedQty) * @remainingQty;
			SET @newAvgPrice = @trade_price;
		END
	END

	IF @positionExists = 1
	BEGIN
		UPDATE dbo.Positions
			SET qty = @newQty, avg_price = @newAvgPrice, realized_pnl = @newRealizedPnl, updated_date = SYSUTCDATETIME()
			WHERE portfolio_id = @portfolio_id AND security_id = @security_id;
	END
	ELSE
	BEGIN
		INSERT INTO dbo.Positions (portfolio_id, security_id, qty, avg_price, realized_pnl, unrealized_pnl, updated_date)
			VALUES (@portfolio_id, @security_id, @newQty, @newAvgPrice, @newRealizedPnl, 0, SYSUTCDATETIME());
	END
END
GO
