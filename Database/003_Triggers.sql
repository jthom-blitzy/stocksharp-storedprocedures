/*
	StockSharpLegacy - triggers (PostgreSQL 16)
	----------------------------------------
	trg_Orders_StatusAudit - cascades order status changes into
	OrderStatusHistory. Narrow on purpose: it only fires when the status
	column actually changed, not on every column update. It is a pure audit
	trigger - it appends a history row and makes no business decisions, so it
	stays in the database.

	The old Trades position-recalculation trigger that previously lived here is
	intentionally removed. That position / P&L recalculation now lives in the
	C# Algo/Risk/PositionRecalculationService, and removing the trigger is what
	guarantees the single-apply invariant: with no residual database trigger
	firing on a Trades row insert, the C# gateway's single recalculation call
	per trade can never be double-counted.

	This script runs SECOND in the container init sequence
	(001_Schema.sql -> 003_Triggers.sql -> 004_SeedData.sql), so the Orders and
	OrderStatusHistory tables already exist when it runs. PostgreSQL terminates
	statements with ';' only, so the T-SQL batch separators and the database /
	ansi-nulls / quoted-identifier session directives from the original SQL
	Server script are intentionally dropped (they have no PostgreSQL equivalent
	and would error here).
*/

-- ============================================================================
-- trg_Orders_StatusAudit  (PostgreSQL two-part form: trigger function + trigger)
--
-- Cascades a status change (e.g. -> 'FILLED') to OrderStatusHistory. Only
-- fires when the status column is part of the UPDATE and the value actually
-- changed - an UPDATE that touches other columns (a price amendment, etc.)
-- without changing status does not create a history row. In PostgreSQL that
-- "only when status actually changed" behavior is produced by two guards
-- working together: the trigger is scoped with AFTER UPDATE OF status (so its
-- function is only considered when status is in the UPDATE's column list), and
-- the function body then re-checks NEW.status IS DISTINCT FROM OLD.status to
-- confirm the value truly changed.
-- ============================================================================

-- Trigger function: append to OrderStatusHistory only when status actually changed.
CREATE OR REPLACE FUNCTION trg_orders_status_audit()
RETURNS TRIGGER AS $$
BEGIN
    -- IS DISTINCT FROM is null-safe, replacing the T-SQL
    -- ISNULL(d.status,'') <> ISNULL(i.status,'') guard.
    IF NEW.status IS DISTINCT FROM OLD.status THEN
        INSERT INTO OrderStatusHistory (order_id, old_status, new_status, changed_date)
        VALUES (NEW.order_id, OLD.status, NEW.status, now() at time zone 'utc');
        -- changed_by is left to its column DEFAULT current_user (the translation
        -- of the original SUSER_SNAME()): the source T-SQL INSERT did not set
        -- changed_by either, so this preserves that behavior and records the
        -- connection role automatically.
    END IF;

    RETURN NULL;  -- AFTER trigger: return value is ignored
END;
$$ LANGUAGE plpgsql;

-- AFTER UPDATE OF status: the column-list form means the trigger body is only
-- considered when the status column is part of the UPDATE (the Postgres analogue
-- of the T-SQL "IF NOT UPDATE(status) RETURN" guard); the IS DISTINCT FROM check
-- in the function above then ensures the value truly changed.
CREATE TRIGGER trg_Orders_StatusAudit
    AFTER UPDATE OF status ON Orders
    FOR EACH ROW
    EXECUTE FUNCTION trg_orders_status_audit();
