/*
	StockSharpLegacy - seed data
	----------------------------------------
	Minimal reference data so the schema and the C# demo are exercisable
	without wiring up the full C# app first. Safe to re-run (idempotent):
	Portfolios and Securities are guarded by ON CONFLICT against their unique
	keys (UQ_Portfolios_name, UQ_Securities_code_board), while RiskLimits has
	no unique key on (portfolio_id, security_id) and is instead guarded by an
	explicit NOT EXISTS check (see each INSERT below).
*/

-- Portfolios has a case-insensitive unique key on LOWER(name) (the functional index UQ_Portfolios_name,
-- F2), so the ON CONFLICT target is LOWER(name); this keeps the insert idempotent (matches the original
-- IF NOT EXISTS check) AND case-insensitive, so re-seeding 'DEMO' never duplicates a 'demo' row.
INSERT INTO Portfolios (name, currency) VALUES ('DEMO', 'USD')
	ON CONFLICT (LOWER(name)) DO NOTHING;

-- Securities has a case-INSENSITIVE unique key on (LOWER(security_code), LOWER(board_code)) - the
-- functional index UQ_Securities_code_board (M4). The ON CONFLICT target must therefore be the SAME
-- expression list, LOWER(security_code), LOWER(board_code), so the upsert infers that functional index;
-- both seed rows carry a non-null board_code, so the target is well-defined and the inserts stay
-- idempotent (and case-insensitive - re-seeding 'aapl' never duplicates the 'AAPL' row).
INSERT INTO Securities (security_code, board_code, security_type) VALUES ('AAPL', 'NASDAQ', 'Stock')
	ON CONFLICT (LOWER(security_code), LOWER(board_code)) DO NOTHING;

INSERT INTO Securities (security_code, board_code, security_type) VALUES ('MSFT', 'NASDAQ', 'Stock')
	ON CONFLICT (LOWER(security_code), LOWER(board_code)) DO NOTHING;

-- Portfolio-wide defaults for DEMO: no single order over $500 price, 10k qty,
-- $1M notional; frequency-capped at 5 orders / 60s; resulting position capped
-- at 100k shares; cumulative estimated commission capped at $5,000.
-- RiskLimits has no unique key on (portfolio_id, security_id), so ON CONFLICT
-- cannot be used; guard with NOT EXISTS to stay idempotent (matches the
-- original IF NOT EXISTS check on the DEMO portfolio-wide row).
INSERT INTO RiskLimits (
	portfolio_id, security_id,
	max_order_price, max_order_qty, max_order_value,
	max_position_size, max_daily_volume,
	max_order_freq_count, max_order_freq_window_sec,
	max_commission_total, commission_rate
)
SELECT
	p.portfolio_id, NULL,
	500.00, 10000, 1000000.00,
	100000, 250000,
	5, 60,
	5000.00, 0.0005
FROM Portfolios p
-- M9: match the portfolio case-INSENSITIVELY via LOWER(name), consistent with the LOWER(name) functional
-- unique key (F2) and the gateway's ON CONFLICT (LOWER(name)) upsert. A case-sensitive `p.name = 'DEMO'`
-- here missed a portfolio row stored as 'demo' (e.g. auto-created by the gateway before seeding), so the
-- SELECT returned no rows and the DEMO portfolio was left with ZERO RiskLimits on a re-seed - which
-- silently disabled every ceiling (all orders accepted). LOWER(p.name) = 'demo' matches regardless of casing.
WHERE LOWER(p.name) = 'demo'
	AND NOT EXISTS (
		SELECT 1 FROM RiskLimits rl
		WHERE rl.portfolio_id = p.portfolio_id AND rl.security_id IS NULL
	);
