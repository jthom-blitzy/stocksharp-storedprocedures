/*
	StockSharpLegacy - stored procedures (consolidated state)
	----------------------------------------
	As part of the SQL -> C# risk-logic consolidation, ALL business logic that
	used to live in stored procedures has been relocated into dedicated,
	unit-tested C# services in the StockSharp.Algo.Risk namespace, leaving SQL
	Server as pure data storage:

	  usp_ValidatePreTradeRisk        -> Algo/Risk/PreTradeRiskService.cs
	                                     (the seven-check per-order pre-trade gate,
	                                     resolving thresholds from the canonical
	                                     RiskLimitSet).
	  usp_RecalculatePositionOnTrade  -> Algo/Risk/PositionRecalculationService.cs
	                                     (average-cost + realized-P&L math, applied
	                                     exactly once per recorded trade).
	  usp_SubmitOrder                 -> removed entirely. The order "front door" is
	                                     now Algo/Storages/Sql/SqlLegacyOrderGateway.
	                                     SubmitOrderAsync, which calls
	                                     PreTradeRiskService and then performs a plain
	                                     parameterized INSERT into dbo.Orders (accepted
	                                     or rejected) inside one gateway-owned
	                                     transaction.

	Choice recorded (AAP 0.4.1 / 0.6.3): usp_SubmitOrder was REMOVED rather than
	reduced to a thin CRUD wrapper, because the gateway already owns the transaction
	that must span validation + INSERT atomically (review finding C03 / CWE-367), and
	a pass-through proc would add a round-trip plus a second place to keep in sync
	without adding value. After this script runs, NO stored procedure in this database
	contains a threshold, an accept/reject decision, or any P&L math - the procedures
	tier holds only pure data storage, keeping the DDL vendor-neutral.

	This script is idempotent: it DROPs the relocated procedures if they exist, so it
	correctly transitions an already-provisioned StockSharpLegacy database to the
	consolidated state and is a harmless no-op on a fresh one. Run it after
	001_Schema.sql and before 003_Triggers.sql (see Database/README.md).
*/

USE StockSharpLegacy;
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- Order "front door" relocated to SqlLegacyOrderGateway.SubmitOrderAsync (direct C# CRUD).
-- Dropped first because it depended on usp_ValidatePreTradeRisk.
DROP PROCEDURE IF EXISTS dbo.usp_SubmitOrder;
GO

-- Seven-check pre-trade gate relocated to PreTradeRiskService (C#).
DROP PROCEDURE IF EXISTS dbo.usp_ValidatePreTradeRisk;
GO

-- Average-cost + realized-P&L recompute relocated to PositionRecalculationService (C#).
DROP PROCEDURE IF EXISTS dbo.usp_RecalculatePositionOnTrade;
GO
