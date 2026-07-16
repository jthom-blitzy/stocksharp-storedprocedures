/*
	StockSharpLegacy - triggers
	----------------------------------------
	trg_Orders_StatusAudit - cascades order status changes into
	OrderStatusHistory. Narrow on purpose: only fires when status actually
	changed, not on every column update. Pure audit CRUD - no risk thresholds,
	no accept/reject decisions, no P&L math - so it stays in SQL.

	The old position-recalc trigger on dbo.Trades was removed in the risk/position
	consolidation: position and realized-P&L recompute now happens exactly once
	per trade in the C# PositionRecalculationService (Algo/Risk), called by
	SqlLegacyOrderGateway.RecordTradeAsync after the Trades insert. Moving it to
	a single explicit C# call also removed the old double-count hazard. See
	/LEGACY_LAYER.md.
*/

USE StockSharpLegacy;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
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
