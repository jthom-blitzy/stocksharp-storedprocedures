/*
	StockSharpLegacy - triggers (consolidated state)
	----------------------------------------
	trg_Trades_PositionRecalc has been REMOVED. The per-trade position/P&L
	recalculation it used to drive now lives in
	Algo/Risk/PositionRecalculationService.cs, invoked exactly once per recorded trade
	by Algo/Storages/Sql/SqlLegacyOrderGateway.RecordTradeAsync inside the same
	transaction as the dbo.Trades INSERT. Removing the trigger also eliminates the
	long-standing double-count hazard - the trigger and the standalone reconciliation
	jobs both calling the recalc for the same trade - documented in LEGACY_LAYER.md.

	trg_Orders_StatusAudit is RETAINED (AAP 0.6.5, Option A). It is an append-only
	audit cascade: it copies an order's status transition into OrderStatusHistory and
	contains no risk decisioning, thresholds, or P&L math. Keeping it as a
	database-level cascade preserves the append-only history guarantee with the least
	risk and is defensible pure-storage behavior; its only conditional - "did the
	status actually change" - gates the audit write, it is not business logic being
	left behind in SQL. (Option B, relocating it to C#, is only required under a
	strict "zero conditional logic in SQL" reading, which the AAP does not mandate.)

	This script is idempotent: it DROPs the relocated trigger if it exists and
	(re)creates the retained audit trigger. Run it after 002_StoredProcedures.sql.
*/

USE StockSharpLegacy;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================================
-- Position/P&L recalculation relocated to Algo/Risk/PositionRecalculationService.cs (C#),
-- invoked exactly once per recorded trade from SqlLegacyOrderGateway.RecordTradeAsync.
-- Dropping the old trigger driver leaves the C# service as the single entry point and
-- eliminates the trigger-vs-standalone double-count hazard (AAP 0.6.4). DROP TRIGGER IF
-- EXISTS is portable, vendor-neutral cleanup and is a no-op when the trigger is absent,
-- so this script stays safe to re-run.
-- ============================================================================
DROP TRIGGER IF EXISTS dbo.trg_Trades_PositionRecalc;
GO

-- ============================================================================
-- trg_Orders_StatusAudit  (RETAINED - Option A, AAP 0.6.5)
--
-- Cascades a status change (e.g. -> 'FILLED') to OrderStatusHistory. Only fires when
-- the status column is part of the UPDATE and the value actually changed - an UPDATE
-- that touches other columns (a price amendment, etc.) without changing status does
-- not create a history row. This is a pure append-only audit cascade: no thresholds,
-- no accept/reject decision, no P&L math.
--
-- DECISION (Option A - KEEP; mandatory inline record, AAP 0.6.5 / 0.7.2): this trigger is
-- RETAINED at the database layer rather than relocated to C#. Rationale: the only
-- conditional here is the narrow "IF NOT UPDATE(status) RETURN" guard, which merely gates
-- the history write - it is defensible CRUD, not risk decisioning or P&L math - and keeping
-- the cascade in SQL preserves the append-only OrderStatusHistory guarantee at the storage
-- layer with the least risk. Option B (relocating this to C#) would only be required under a
-- strict "zero conditional logic in SQL" reading, which the AAP does not enforce.
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
