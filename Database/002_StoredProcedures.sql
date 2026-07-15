/*
	StockSharpLegacy - stored procedures
	----------------------------------------
	usp_ValidatePreTradeRisk   - the risk gate. Ported from the C# IRiskRule
	                             classes in Algo/Risk (price/volume/value/
	                             frequency/position/commission/daily-volume).
	                             This is now the only place a subset of these
	                             checks are enforced - see the header comment
	                             on the proc and LEGACY_LAYER.md for which
	                             ones and why.
	usp_RecalculatePositionOnTrade - recomputes Positions.qty/avg_price/
	                             realized_pnl for a single trade. Called by
	                             both usp_SubmitOrder's callers (indirectly,
	                             via the Trades table) and trg_Trades_PositionRecalc.
	                             Do not call it twice for the same trade.
	usp_SubmitOrder            - front door for placing an order: validates,
	                             then inserts (accepted or rejected - rejects
	                             are still recorded, not discarded, so there's
	                             an audit trail of what got blocked and why).
*/

USE StockSharpLegacy;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================================
-- usp_ValidatePreTradeRisk
--
-- Ported (not just mirrored) from Algo/Risk/RiskOrderPriceRule,
-- RiskOrderVolumeRule, RiskOrderFreqRule, RiskPositionSizeRule,
-- RiskCommissionRule/RiskOrderCommissionRule. Two things are worth knowing
-- if you're comparing this against the C# rule engine:
--
--   1. The C# RiskManager (Algo/Risk/RiskManager.cs, RiskMessageAdapter.cs)
--      is NOT a per-order pre-trade gate - it's a portfolio-wide circuit
--      breaker. A triggered rule there fires ClosePositions / StopTrading /
--      CancelOrders against the whole portfolio, evaluated as messages flow
--      through the adapter; it does not block the specific order that
--      tripped it (unless trading is already halted). This proc is the
--      opposite model: a classic pre-trade gate that rejects the ONE order
--      being submitted, before it is ever accepted. Both patterns are real
--      and both exist in this codebase now; nothing reconciles them.
--   2. max_order_value, max_daily_volume have no C# equivalent at all - they
--      only exist here. If someone asks "does the risk engine check daily
--      volume", the honest answer is "only if the order goes through this
--      proc", not "yes" unconditionally.
--
-- @requested_by is unused below - it was added for a compliance ask
-- ("tag every risk check with who initiated it") that got descoped, but the
-- parameter shipped anyway because it was already threaded through
-- usp_SubmitOrder and the C# caller. Left as-is.
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

	-- pick the most specific RiskLimits row in scope: portfolio+security,
	-- then portfolio-only, then security-only
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

	-- no configured limits at all => nothing to enforce, same "unlimited"
	-- convention the C# RiskRule classes use for a 0/unset threshold
	IF @max_order_price IS NULL AND @max_order_qty IS NULL AND @max_order_value IS NULL
		AND @max_position_size IS NULL AND @max_daily_volume IS NULL
		AND @max_order_freq_count IS NULL AND @max_commission_total IS NULL
	BEGIN
		RETURN;
	END

	-- 1. order price ceiling - mirrors RiskOrderPriceRule (triggers when price >= limit)
	IF @is_valid = 1 AND @price IS NOT NULL AND @max_order_price IS NOT NULL AND @price >= @max_order_price
	BEGIN
		SET @is_valid = 0;
		SET @reject_reason = 'Order price ' + CONVERT(VARCHAR(30), @price) + ' meets/exceeds limit ' + CONVERT(VARCHAR(30), @max_order_price);
	END

	-- 2. order qty ceiling - mirrors RiskOrderVolumeRule (triggers when qty >= limit)
	IF @is_valid = 1 AND @max_order_qty IS NOT NULL AND @qty >= @max_order_qty
	BEGIN
		SET @is_valid = 0;
		SET @reject_reason = 'Order qty ' + CONVERT(VARCHAR(30), @qty) + ' meets/exceeds limit ' + CONVERT(VARCHAR(30), @max_order_qty);
	END

	-- 3. notional order value ceiling (qty * price) - DB-only, no C# equivalent
	IF @is_valid = 1 AND @price IS NOT NULL AND @max_order_value IS NOT NULL AND (@qty * @price) >= @max_order_value
	BEGIN
		SET @is_valid = 0;
		SET @reject_reason = 'Order value ' + CONVERT(VARCHAR(30), @qty * @price) + ' meets/exceeds limit ' + CONVERT(VARCHAR(30), @max_order_value);
	END

	-- 4. order frequency - mirrors RiskOrderFreqRule, with a real behavioral
	--    difference: RiskOrderFreqRule buckets time into non-overlapping
	--    windows (a burst right at a bucket boundary can dodge it); this is
	--    a true rolling window (COUNT over "now minus N seconds"), which is
	--    strictly stricter near the boundary. Same Count/Interval config,
	--    different answer for the same burst pattern.
	--    NOTE (compliance review, last year): window tightened to 30s per
	--    the desk's request. <- stale; the window has been config-driven via
	--    RiskLimits.max_order_freq_window_sec since this proc shipped, so
	--    there hasn't been a hardcoded "30s" here for a while. Nobody
	--    circled back to fix the comment.
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

	-- 5. resulting position size ceiling - mirrors RiskPositionSizeRule,
	--    evaluated on the hypothetical POST-fill position since this check
	--    runs before the order is accepted
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

	-- 6. cumulative commission ceiling - mirrors RiskCommissionRule /
	--    RiskOrderCommissionRule. Actual commission isn't known until
	--    execution, so this estimates cost using RiskLimits.commission_rate
	--    against @price (or, for market orders, the security's last traded
	--    price - best-effort only).
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

	-- 7. daily traded volume ceiling - DB-only, no C# equivalent
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
-- usp_RecalculatePositionOnTrade
--
-- Standard average-cost position/realized-P&L recompute for a single trade.
-- unrealized_pnl is deliberately left untouched here (see the comment on
-- dbo.Positions in 001_Schema.sql).
--
-- IMPORTANT: trg_Trades_PositionRecalc (003_Triggers.sql) already calls this
-- for every row inserted into Trades. Calling it again for a trade that went
-- through the normal Trades insert path will double-count that trade's
-- effect on qty/avg_price/realized_pnl. This proc is exposed standalone for
-- the reconciliation/backfill jobs that pre-date the trigger (some of which
-- still call it directly) - do not wire it into the normal order/trade flow
-- a second time.
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
		-- adding to (or opening) a position: weighted-average the price in
		SET @newQty = @existingQty + @tradeSignedQty;
		SET @newAvgPrice = (ABS(@existingQty) * @existingAvgPrice + @trade_qty * @trade_price) / ABS(@newQty);
	END
	ELSE
	BEGIN
		-- trade works against the existing position: realize P&L on the closed portion
		DECLARE @closingQty DECIMAL(18,4) = CASE WHEN ABS(@existingQty) < @trade_qty THEN ABS(@existingQty) ELSE @trade_qty END;
		DECLARE @remainingQty DECIMAL(18,4) = @trade_qty - @closingQty;

		SET @newRealizedPnl = @existingRealizedPnl + (@closingQty * (@trade_price - @existingAvgPrice) * SIGN(@existingQty));

		IF @remainingQty = 0
		BEGIN
			-- partial or exact close; average price is only meaningful while a position stays open
			SET @newQty = @existingQty + @tradeSignedQty;
			SET @newAvgPrice = CASE WHEN @newQty = 0 THEN 0 ELSE @existingAvgPrice END;
		END
		ELSE
		BEGIN
			-- fully closed and flipped: what's left of the trade opens a new position on the other side
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

-- ============================================================================
-- usp_SubmitOrder
--
-- Front door for placing an order: runs usp_ValidatePreTradeRisk, then
-- inserts the order either as ACCEPTED or REJECTED (rejected orders are
-- still recorded, with reject_reason populated, for the audit trail - they
-- are not silently dropped).
-- ============================================================================
CREATE OR ALTER PROCEDURE dbo.usp_SubmitOrder
	@portfolio_id				INT,
	@security_id				INT,
	@side						CHAR(1),
	@qty						DECIMAL(18,4),
	@price						DECIMAL(18,4)	= NULL,
	@order_type					VARCHAR(10)		= 'LIMIT',
	@external_transaction_id	BIGINT			= NULL,
	@requested_by				NVARCHAR(50)	= NULL,
	@order_id					BIGINT OUTPUT,
	@is_valid					BIT OUTPUT,
	@reject_reason				NVARCHAR(200) OUTPUT
AS
BEGIN
	SET NOCOUNT ON;

	EXEC dbo.usp_ValidatePreTradeRisk
		@portfolio_id = @portfolio_id,
		@security_id = @security_id,
		@side = @side,
		@qty = @qty,
		@price = @price,
		@order_type = @order_type,
		@requested_by = @requested_by,
		@is_valid = @is_valid OUTPUT,
		@reject_reason = @reject_reason OUTPUT;

	INSERT INTO dbo.Orders (portfolio_id, security_id, side, qty, price, order_type, status, reject_reason, external_transaction_id)
		VALUES (
			@portfolio_id, @security_id, @side, @qty, @price, @order_type,
			CASE WHEN @is_valid = 1 THEN 'ACCEPTED' ELSE 'REJECTED' END,
			@reject_reason,
			@external_transaction_id
		);

	SET @order_id = SCOPE_IDENTITY();
END
GO
