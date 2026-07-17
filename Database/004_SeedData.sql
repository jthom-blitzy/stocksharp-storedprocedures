/*
	StockSharpLegacy - seed data
	----------------------------------------
	Minimal reference data so the schema and the C# demo are exercisable
	without wiring up the full C# app first. Safe to re-run: guarded by
	existence checks against the unique keys.
*/

-- Portfolios has a unique key on name (UQ_Portfolios_name), so ON CONFLICT
-- keeps this insert idempotent (matches the original IF NOT EXISTS check).
INSERT INTO Portfolios (name, currency) VALUES ('DEMO', 'USD')
	ON CONFLICT (name) DO NOTHING;

-- Securities has a unique key on (security_code, board_code)
-- (UQ_Securities_code_board); both seed rows carry a non-null board_code, so
-- the ON CONFLICT target is well-defined and the inserts stay idempotent.
INSERT INTO Securities (security_code, board_code, security_type) VALUES ('AAPL', 'NASDAQ', 'Stock')
	ON CONFLICT (security_code, board_code) DO NOTHING;

INSERT INTO Securities (security_code, board_code, security_type) VALUES ('MSFT', 'NASDAQ', 'Stock')
	ON CONFLICT (security_code, board_code) DO NOTHING;

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
WHERE p.name = 'DEMO'
	AND NOT EXISTS (
		SELECT 1 FROM RiskLimits rl
		WHERE rl.portfolio_id = p.portfolio_id AND rl.security_id IS NULL
	);
