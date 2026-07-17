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

	trg_Orders_StatusAudit is RETAINED (AAP 0.6.5, Option A). It is a best-effort audit
	cascade that copies each order status TRANSITION (an UPDATE that changes the status
	column) into OrderStatusHistory. It contains no risk decisioning, thresholds, or P&L
	math. Keeping it as a database-level cascade is defensible pure-storage behavior; its
	only conditional - "did the status actually change" - gates the audit write, it is not
	business logic being left behind in SQL. (Option B, relocating it to C#, is only
	required under a strict "zero conditional logic in SQL" reading, which the AAP does not
	mandate.)

	Audit integrity (review finding MA-12): OrderStatusHistory is a BEST-EFFORT,
	trigger-derived record of status transitions - NOT a tamper-proof, append-only log.
	The previously-claimed "append-only history guarantee" wording has been removed because
	it was inaccurate: this cascade is not backed by least-privilege application credentials
	or an immutable-audit mechanism, so a sufficiently privileged principal (the
	application/test login, or SA) could still insert, update, or delete history rows or
	alter this trigger. Enforcing true immutability is a deployment/infrastructure concern
	(least-privilege credentials + enforced DML boundaries) that is out of scope for this
	code refactor. The order's authoritative CURRENT status always lives on
	dbo.Orders.status; the history table is a derived, supplementary transition log. The
	initial status set at INSERT time is not separately written to history - only
	subsequent transitions are - which is part of this best-effort, derived characterization.

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
-- EXISTS is a SQL Server built-in (2016+) used here for repeatable, non-destructive
-- cleanup; it is a no-op when the trigger is absent, so this script stays safe to re-run.
-- ============================================================================
DROP TRIGGER IF EXISTS dbo.trg_Trades_PositionRecalc;
GO

-- ============================================================================
-- trg_Orders_StatusAudit  (RETAINED - Option A, AAP 0.6.5)
--
-- Cascades an order status TRANSITION to OrderStatusHistory. Fires AFTER UPDATE and only
-- records when the status column is part of the UPDATE and the value actually changed - an
-- UPDATE that touches other columns (a price amendment, etc.) without changing status does
-- not create a history row. This is a pure audit cascade: no thresholds, no accept/reject
-- decision, no P&L math.
--
-- It intentionally fires on UPDATE only (not INSERT). Two reasons: (1) AAP 0.6.5 describes
-- this trigger's role as recording status CHANGES and recommends the least-risk audit
-- cascade; (2) dbo.Orders is written by the gateway with an INSERT ... OUTPUT INSERTED
-- pattern, and SQL Server forbids an OUTPUT clause (without INTO) on a table that has an
-- enabled INSERT trigger. The order's initial status is always authoritatively available on
-- dbo.Orders.status, so it is not duplicated into the derived history table.
--
-- DECISION (Option A - KEEP; mandatory inline record, AAP 0.6.5 / 0.7.2): this trigger is
-- RETAINED at the database layer rather than relocated to C#. Rationale: the only
-- conditional here is the narrow "IF NOT UPDATE(status) RETURN" guard, which merely gates
-- the history write - it is defensible CRUD, not risk decisioning or P&L math. Option B
-- (relocating this to C#) would only be required under a strict "zero conditional logic in
-- SQL" reading, which the AAP does not enforce.
--
-- AUDIT INTEGRITY (review finding MA-12): OrderStatusHistory is BEST-EFFORT derived history,
-- not a tamper-proof append-only log - the cascade is not backed by least-privilege
-- credentials or immutable-audit controls (a deployment concern out of scope here). The
-- overclaimed "append-only guarantee" wording has been removed; see the file header.
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
