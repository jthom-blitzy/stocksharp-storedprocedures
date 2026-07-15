/*
	StockSharpLegacy - triggers
	----------------------------------------
	trg_Trades_PositionRecalc - fires on every Trades insert and drives the
	position/P&L recalculation. This is the trigger that makes
	usp_RecalculatePositionOnTrade "automatic" - see the warning on that
	proc in 002_StoredProcedures.sql about not calling it a second time.

	trg_Orders_StatusAudit - cascades order status changes into
	OrderStatusHistory. Narrow on purpose: only fires when status actually
	changed, not on every column update.
*/

USE StockSharpLegacy;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================================
-- trg_Trades_PositionRecalc
--
-- Cursor-based on purpose (or at least, that's the polite way to put it -
-- this was written back when multi-row trade inserts were rare enough that
-- nobody optimized for them, and it's never been revisited). Processes
-- inserted rows oldest-first so avg_price/realized_pnl land the same as if
-- the trades had been inserted one at a time.
-- ============================================================================
CREATE OR ALTER TRIGGER dbo.trg_Trades_PositionRecalc
ON dbo.Trades
AFTER INSERT
AS
BEGIN
	SET NOCOUNT ON;

	DECLARE @order_id BIGINT, @qty DECIMAL(18,4), @price DECIMAL(18,4);

	DECLARE trade_cursor CURSOR LOCAL FAST_FORWARD FOR
		SELECT order_id, qty, price
		FROM inserted
		ORDER BY executed_date ASC, trade_id ASC;

	OPEN trade_cursor;
	FETCH NEXT FROM trade_cursor INTO @order_id, @qty, @price;

	WHILE @@FETCH_STATUS = 0
	BEGIN
		EXEC dbo.usp_RecalculatePositionOnTrade
			@order_id = @order_id,
			@trade_qty = @qty,
			@trade_price = @price;

		FETCH NEXT FROM trade_cursor INTO @order_id, @qty, @price;
	END

	CLOSE trade_cursor;
	DEALLOCATE trade_cursor;
END
GO

-- ============================================================================
-- trg_Orders_StatusAudit
--
-- Cascades a status change (e.g. -> 'FILLED') to OrderStatusHistory. Only
-- fires when the status column is part of the UPDATE and the value actually
-- changed - an UPDATE that touches other columns (price amendment, etc.)
-- without changing status does not create a history row.
-- ============================================================================
CREATE OR ALTER TRIGGER dbo.trg_Orders_StatusAudit
ON dbo.Orders
AFTER UPDATE
AS
BEGIN
	SET NOCOUNT ON;

	IF NOT UPDATE(status)
		RETURN;

	INSERT INTO dbo.OrderStatusHistory (order_id, old_status, new_status, changed_date)
	SELECT
		i.order_id,
		d.status,
		i.status,
		SYSUTCDATETIME()
	FROM inserted i
	JOIN deleted d ON d.order_id = i.order_id
	WHERE ISNULL(d.status, '') <> ISNULL(i.status, '');
END
GO
