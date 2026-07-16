/*
	StockSharpLegacy - stored procedures
	----------------------------------------
	This file installs NO business logic. The risk/order/position logic that
	used to live here has been consolidated into the C# service layer under
	Algo/Risk, so SQL Server is now pure data storage (tables, constraints,
	indexes) with no risk thresholds, accept/reject decisions, or P&L arithmetic.

	On a fresh database there is nothing to create. On an upgrade from an older
	StockSharpLegacy build this script idempotently DROPS the three retired
	procedures, so no stale business logic is left installed:

	  - usp_ValidatePreTradeRisk       -> PreTradeRiskService
	                                      (Algo/Risk/PreTradeRiskService.cs):
	                                      the per-order pre-trade gate.
	  - usp_RecalculatePositionOnTrade -> PositionRecalculationService
	                                      (Algo/Risk/PositionRecalculationService.cs):
	                                      average-cost + realized-P&L recompute.
	  - usp_SubmitOrder                -> SqlLegacyOrderGateway
	                                      (Algo/Storages/Sql/SqlLegacyOrderGateway.cs):
	                                      a direct INSERT INTO dbo.Orders with the
	                                      C#-decided status and rejection reason.

	The file is kept (rather than deleted) so the 001 -> 002 -> 003 -> 004 run
	order and the Database/README.md file table stay intact. See /LEGACY_LAYER.md
	for the full consolidation rationale and the rule-by-rule reconciliation.
*/

USE StockSharpLegacy;
GO

-- Idempotently remove the retired business procedures. No replacement logic is
-- installed here - every decision now lives in the C# service layer (Algo/Risk).
-- Each DROP is its own batch so a fresh install (nothing to drop) and a legacy
-- upgrade (all three present) both run cleanly.
DROP PROCEDURE IF EXISTS dbo.usp_SubmitOrder;
GO

DROP PROCEDURE IF EXISTS dbo.usp_ValidatePreTradeRisk;
GO

DROP PROCEDURE IF EXISTS dbo.usp_RecalculatePositionOnTrade;
GO
