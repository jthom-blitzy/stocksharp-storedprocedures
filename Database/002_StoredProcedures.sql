/*
	StockSharpLegacy - stored procedures
	----------------------------------------
	INTENTIONALLY EMPTY.

	The risk/order/position business logic that used to live here has been
	consolidated into the C# service layer under Algo/Risk, so that SQL Server
	is now pure data storage (tables, constraints, indexes) with no risk
	thresholds, accept/reject decisions, or P&L arithmetic.

	What moved, and where it went:
	  - usp_ValidatePreTradeRisk       -> PreTradeRiskService
	                                      (Algo/Risk/PreTradeRiskService.cs):
	                                      the per-order pre-trade gate.
	  - usp_RecalculatePositionOnTrade -> PositionRecalculationService
	                                      (Algo/Risk/PositionRecalculationService.cs):
	                                      average-cost + realized-P&L recompute.
	  - usp_SubmitOrder                -> SqlLegacyOrderGateway
	                                      (Algo/Storages/Sql/SqlLegacyOrderGateway.cs):
	                                      now does a direct INSERT INTO dbo.Orders
	                                      with the C#-decided status and
	                                      rejection reason.

	This file is kept (rather than deleted) so the 001 -> 002 -> 003 -> 004 run
	order and the Database/README.md file table stay intact. See /LEGACY_LAYER.md
	for the full consolidation rationale and the rule-by-rule reconciliation.
*/

USE StockSharpLegacy;
GO
