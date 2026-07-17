/*
	StockSharpLegacy - COMMITTED LEGACY RISK ORACLE (test fixture)
	--------------------------------------------------------------
	This file is the authoritative, executable snapshot of the ORIGINAL T-SQL
	business logic as it existed in Database/002_StoredProcedures.sql BEFORE the
	SQL->C# risk-consolidation refactor reduced the SQL layer to CRUD. It is the
	characterization-first oracle required by review finding MA-17: the differential
	tests in Tests/LegacyOracleParityTests.cs execute these procedures side-by-side
	with the new C# services (PreTradeRiskService / PositionRecalculationService) over
	identical database state and assert the two produce the SAME accept/reject + reason
	and the SAME qty/avg_price/realized_pnl, for all seven pre-trade checks and every
	position branch (open, add/average, partial close, exact close, flip).

	The two procedures are copied VERBATIM from the pre-refactor source (only their
	names carry a "_Legacy" suffix) so they can coexist with the consolidated schema,
	which no longer defines the production usp_ValidatePreTradeRisk /
	usp_RecalculatePositionOnTrade. Do NOT "improve" or reconcile this file - its value
	is that it is the frozen legacy behavior. The tests create these procedures inside a
	transaction that is always rolled back, so the fixture never persists in the shared
	database and cannot be mistaken for production business logic.

	Documented, intentional divergences the parity tests account for (NOT bugs):
	  * Zero/non-positive ceilings: the legacy proc treats a non-null 0 threshold as an
	    active reject-everything limit; the C# services treat 0/negative as "not enforced"
	    (AAP 0.3.1). The parity tests use positive seeded limits and cover the zero case
	    separately (review finding MA-16).
	  * The C# circuit-breaker RiskOrderFreqRule adopted a rolling-count algorithm; the
	    pre-trade GATE below already used a rolling COUNT(*), so the gate and this oracle
	    agree (AAP 0.6.1).
*/

-- ============================================================================
-- usp_ValidatePreTradeRisk_Legacy  (frozen pre-refactor pre-trade gate)
--
-- Ported (not just mirrored) from Algo/Risk/RiskOrderPriceRule,
-- RiskOrderVolumeRule, RiskOrderFreqRule, RiskPositionSizeRule,
-- RiskCommissionRule/RiskOrderCommissionRule, plus the two DB-only ceilings
-- (max_order_value, max_daily_volume). @requested_by is unused (a descoped
-- compliance ask that shipped anyway); it is preserved here for fidelity.
-- ============================================================================
CREATE OR ALTER PROCEDURE dbo.usp_ValidatePreTradeRisk_Legacy
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

	-- 4. order frequency - a true rolling window (COUNT over "now minus N seconds").
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

	-- 5. resulting position size ceiling - mirrors RiskPositionSizeRule, evaluated
	--    on the hypothetical POST-fill position since this runs before acceptance.
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
	--    RiskOrderCommissionRule. Estimates cost pre-fill using commission_rate.
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
-- usp_RecalculatePositionOnTrade_Legacy  (frozen pre-refactor position recompute)
--
-- Standard average-cost position/realized-P&L recompute for a single trade.
-- unrealized_pnl is deliberately left untouched. This is the frozen legacy math
-- the C# PositionRecalculationService.Recalculate is proven equal to.
-- ============================================================================
CREATE OR ALTER PROCEDURE dbo.usp_RecalculatePositionOnTrade_Legacy
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
		RAISERROR('usp_RecalculatePositionOnTrade_Legacy: order_id %d not found', 16, 1, @order_id);
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
