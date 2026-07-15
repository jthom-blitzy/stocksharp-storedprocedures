/*
	StockSharpLegacy - seed data
	----------------------------------------
	Minimal reference data so the schema and stored procs are exercisable
	without wiring up the full C# app first. Safe to re-run: guarded by
	existence checks against the unique keys.
*/

USE StockSharpLegacy;
GO

-- RiskLimits has filtered indexes (see 001_Schema.sql), which requires these
-- session options to be ON for any INSERT/UPDATE/DELETE against it
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Portfolios WHERE name = 'DEMO')
	INSERT INTO dbo.Portfolios (name, currency) VALUES ('DEMO', 'USD');
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Securities WHERE security_code = 'AAPL' AND board_code = 'NASDAQ')
	INSERT INTO dbo.Securities (security_code, board_code, security_type) VALUES ('AAPL', 'NASDAQ', 'Stock');

IF NOT EXISTS (SELECT 1 FROM dbo.Securities WHERE security_code = 'MSFT' AND board_code = 'NASDAQ')
	INSERT INTO dbo.Securities (security_code, board_code, security_type) VALUES ('MSFT', 'NASDAQ', 'Stock');
GO

-- Portfolio-wide defaults for DEMO: no single order over $500 price, 10k qty,
-- $1M notional; frequency-capped at 5 orders / 60s; resulting position capped
-- at 100k shares; cumulative estimated commission capped at $5,000.
IF NOT EXISTS (SELECT 1 FROM dbo.RiskLimits rl JOIN dbo.Portfolios p ON p.portfolio_id = rl.portfolio_id WHERE p.name = 'DEMO' AND rl.security_id IS NULL)
BEGIN
	INSERT INTO dbo.RiskLimits (
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
	FROM dbo.Portfolios p WHERE p.name = 'DEMO';
END
GO
