namespace StockSharp.Tests;

using System.ComponentModel;
using System.Data;

using Microsoft.Data.SqlClient;

using StockSharp.Algo.Risk;
using StockSharp.Algo.Storages.Sql;
using StockSharp.Algo.Testing;

[TestClass]
public class RiskTests : BaseTestClass
{
	private static readonly string _pfName = Helper.CreatePortfolio().Name;

	[TestMethod]
	public void ManagerAddRemoveRules()
	{
		var manager = new RiskManager();

		var rule1 = new RiskPnLRule { PnL = new() { Value = 1000, Type = UnitTypes.Absolute } };
		var rule2 = new RiskPositionSizeRule { Position = 100 };

		manager.Rules.Add(rule1);
		manager.Rules.Add(rule2);

		manager.Rules.Count.AssertEqual(2);
		manager.Rules.Count(r => r == rule1).AssertEqual(1);
		manager.Rules.Count(r => r == rule2).AssertEqual(1);

		manager.Rules.Remove(rule1);
		manager.Rules.Count.AssertEqual(1);

		manager.Rules.Clear();
		manager.Rules.Count.AssertEqual(0);
	}

	[TestMethod]
	public void ManagerReset()
	{
		var manager = new RiskManager();
		var rule = new TestRiskRule();

		manager.Rules.Add(rule);
		rule.IsReset.AssertFalse();

		manager.Reset();
		rule.IsReset.AssertTrue();
	}

	[TestMethod]
	public void ManagerProcessRules()
	{
		var manager = new RiskManager();
		var rule1 = new TestRiskRule { ShouldActivate = true };
		var rule2 = new TestRiskRule { ShouldActivate = false };

		manager.Rules.Add(rule1);
		manager.Rules.Add(rule2);

		var message = new TimeMessage { LocalTime = DateTime.UtcNow };
		var activatedRules = manager.ProcessRules(message).ToArray();

		activatedRules.Length.AssertEqual(1);
		activatedRules[0].AssertEqual(rule1);

		rule1.LastMessage.AssertEqual(message);
		rule2.LastMessage.AssertEqual(message);
	}

	[TestMethod]
	public void ManagerProcessResetMessage()
	{
		var manager = new RiskManager();
		var rule = new TestRiskRule();

		manager.Rules.Add(rule);
		rule.IsReset.AssertFalse();

		var resetMessage = new ResetMessage();
		var activatedRules = manager.ProcessRules(resetMessage);

		activatedRules.Count().AssertEqual(0);
		rule.IsReset.AssertTrue();
	}

	[TestMethod]
	public void ManagerSaveLoad()
	{
		var manager = new RiskManager();

		var pnlRule = new RiskPnLRule
		{
			PnL = new() { Value = 1000, Type = UnitTypes.Absolute },
			Action = RiskActions.ClosePositions
		};

		var positionRule = new RiskPositionSizeRule
		{
			Position = 100,
			Action = RiskActions.StopTrading
		};

		manager.Rules.Add(pnlRule);
		manager.Rules.Add(positionRule);

		var storage = new SettingsStorage();
		manager.Save(storage);

		var newManager = new RiskManager();
		newManager.Load(storage);

		newManager.Rules.Count.AssertEqual(2);

		var savedPnlRule = newManager.Rules.OfType<RiskPnLRule>().FirstOrDefault();
		savedPnlRule.AssertNotNull();
		savedPnlRule.PnL.Value.AssertEqual(1000);
		savedPnlRule.PnL.Type.AssertEqual(UnitTypes.Absolute);
		savedPnlRule.Action.AssertEqual(RiskActions.ClosePositions);

		var savedPositionRule = newManager.Rules.OfType<RiskPositionSizeRule>().FirstOrDefault();
		savedPositionRule.AssertNotNull();
		savedPositionRule.Position.AssertEqual(100);
		savedPositionRule.Action.AssertEqual(RiskActions.StopTrading);
	}

	[TestMethod]
	public void PnLAbsolute()
	{
		var rule = new RiskPnLRule
		{
			PnL = new() { Value = -1000, Type = UnitTypes.Absolute },
			Action = RiskActions.ClosePositions
		};

		var positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, -500m);

		rule.ProcessMessage(positionMsg).AssertFalse();

		positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, -900m);
		rule.ProcessMessage(positionMsg).AssertFalse();

		positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, -1200m);

		rule.ProcessMessage(positionMsg).AssertTrue();
	}

	[TestMethod]
	public void PnLReset()
	{
		var rule = new RiskPnLRule
		{
			PnL = new() { Value = -1000, Type = UnitTypes.Absolute }
		};

		var positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, -500m);

		rule.ProcessMessage(positionMsg).AssertFalse();

		positionMsg.Changes[PositionChangeTypes.CurrentValue] = -1500m;
		rule.ProcessMessage(positionMsg).AssertTrue();

		rule.Reset();

		rule.ProcessMessage(positionMsg).AssertFalse();
		positionMsg.Changes[PositionChangeTypes.CurrentValue] = -900m;
		rule.ProcessMessage(positionMsg).AssertFalse();
	}

	[TestMethod]
	public void PnLZeroLimit()
	{
		// Test for PnL.Value == 0 with Limit type
		// Verifies that RiskPnLRule correctly handles zero threshold
		// Fixed in RiskPnLRule.cs:77-78 to explicitly return false when PnL == 0
		var rule = new RiskPnLRule
		{
			PnL = new() { Value = 0, Type = UnitTypes.Absolute },
			Action = RiskActions.ClosePositions
		};

		// Test 1: currValue = 0
		// Should NOT activate - zero threshold means rule is effectively disabled
		var positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 0m);

		// Rule correctly does not activate for zero threshold
		rule.ProcessMessage(positionMsg).AssertFalse();

		// Test 2: currValue = -100
		// Should NOT activate - zero threshold means no risk limit is set
		positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, -100m);

		// Rule correctly does not activate
		rule.ProcessMessage(positionMsg).AssertFalse();

		// Test 3: currValue = 100 (positive value)
		// Should NOT activate - zero threshold means no limit
		positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 100m);

		// Rule correctly does not activate
		rule.ProcessMessage(positionMsg).AssertFalse();
	}

	[TestMethod]
	public void PnLZeroAbsolute()
	{
		// Test for PnL.Value == 0 with Absolute type
		// Verifies that RiskPnLRule correctly handles zero threshold for absolute PnL changes
		// Fixed in RiskPnLRule.cs:85-86 to explicitly return false when PnL == 0
		var rule = new RiskPnLRule
		{
			PnL = new() { Value = 0, Type = UnitTypes.Absolute },
			Action = RiskActions.ClosePositions
		};

		// Initialize with starting value of 1000
		var positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 1000m);

		// First message sets _initValue = 1000, should not activate
		rule.ProcessMessage(positionMsg).AssertFalse();

		// Test 1: currValue = 1000 (no change)
		// Should NOT activate - zero threshold means no risk limit
		positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 1000m);

		// Rule correctly does not activate when threshold is zero
		rule.ProcessMessage(positionMsg).AssertFalse();

		// Test 2: currValue = 900 (loss of 100)
		// Should NOT activate - zero threshold means rule is effectively disabled
		positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 900m);

		// Rule correctly does not activate
		rule.ProcessMessage(positionMsg).AssertFalse();

		// Test 3: currValue = 1100 (profit of 100)
		// Should NOT activate - zero threshold means no limit
		positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 1100m);

		// Rule correctly does not activate
		rule.ProcessMessage(positionMsg).AssertFalse();
	}

	[TestMethod]
	public void PositionSize()
	{
		var rule = new RiskPositionSizeRule
		{
			Position = 100,
			Action = RiskActions.CancelOrders
		};

		var positionMsg = new PositionChangeMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 50m);

		rule.ProcessMessage(positionMsg).AssertFalse();

		positionMsg = new PositionChangeMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 150m);

		rule.ProcessMessage(positionMsg).AssertTrue();
	}

	[TestMethod]
	public void PositionSizeNegative()
	{
		var rule = new RiskPositionSizeRule
		{
			Position = -100,
			Action = RiskActions.CancelOrders
		};

		var positionMsg = new PositionChangeMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, -50m);

		rule.ProcessMessage(positionMsg).AssertFalse();

		positionMsg = new PositionChangeMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, -150m);

		rule.ProcessMessage(positionMsg).AssertTrue();
	}

	[TestMethod]
	public void PositionSizeZero()
	{
		var rule = new RiskPositionSizeRule
		{
			Position = 0,
			Action = RiskActions.CancelOrders
		};

		var positionMsg = new PositionChangeMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 100m);

		rule.ProcessMessage(positionMsg).AssertFalse();

		positionMsg = new PositionChangeMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, -100m);

		rule.ProcessMessage(positionMsg).AssertFalse();

		// Missing current value should be ignored as well
		positionMsg = new PositionChangeMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};

		rule.ProcessMessage(positionMsg).AssertFalse();

		positionMsg = new PositionChangeMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 0m);

		rule.ProcessMessage(positionMsg).AssertFalse();
	}

	[TestMethod]
	public void PositionTime()
	{
		var rule = new RiskPositionTimeRule
		{
			Time = TimeSpan.FromMinutes(5),
			Action = RiskActions.ClosePositions
		};

		var securityId = Helper.CreateSecurityId();
		var startTime = DateTime.UtcNow;

		var positionMsg = new PositionChangeMessage
		{
			SecurityId = securityId,
			PortfolioName = _pfName,
			LocalTime = startTime,
			ServerTime = startTime
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 100m);

		rule.ProcessMessage(positionMsg).AssertFalse();

		var timeMsg = new TimeMessage
		{
			LocalTime = startTime.AddMinutes(3)
		};
		rule.ProcessMessage(timeMsg).AssertFalse();

		timeMsg = new TimeMessage
		{
			LocalTime = startTime.AddMinutes(6)
		};
		rule.ProcessMessage(timeMsg).AssertTrue();
	}

	[TestMethod]
	public void PositionTimeReset()
	{
		var rule = new RiskPositionTimeRule
		{
			Time = TimeSpan.FromMinutes(5)
		};

		var securityId = Helper.CreateSecurityId();

		var positionMsg = new PositionChangeMessage
		{
			SecurityId = securityId,
			PortfolioName = _pfName,
			LocalTime = DateTime.UtcNow,
			ServerTime = DateTime.UtcNow
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 100m);

		rule.ProcessMessage(positionMsg);

		rule.Reset();

		var timeMsg = new TimeMessage
		{
			LocalTime = DateTime.UtcNow.AddMinutes(6)
		};
		rule.ProcessMessage(timeMsg).AssertFalse();
	}

	[TestMethod]
	public void Commission()
	{
		var rule = new RiskCommissionRule
		{
			Commission = 1000,
			Action = RiskActions.StopTrading
		};

		var positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.Commission, 500m);

		rule.ProcessMessage(positionMsg).AssertFalse();

		positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.Commission, 1500m);

		rule.ProcessMessage(positionMsg).AssertTrue();
	}

	[TestMethod]
	public void CommissionNegative()
	{
		// Test for negative commission limit (lower bound)
		// Verifies that RiskCommissionRule correctly handles negative thresholds
		// Fixed in RiskCommissionRule.cs:57-60 to handle both positive and negative limits
		var rule = new RiskCommissionRule
		{
			Commission = -1000, // Negative limit means we're tracking downside
			Action = RiskActions.StopTrading
		};

		// First test: commission is -500, which is ABOVE -1000 (less negative)
		// Should NOT trigger the rule (within acceptable range)
		var positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.Commission, -500m);

		rule.ProcessMessage(positionMsg).AssertFalse();

		// Second test: commission is -1500, which is BELOW -1000 (more negative)
		// SHOULD trigger the rule - commission exceeded the negative threshold
		// Correctly uses "currValue <= Commission" for negative limits
		positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.Commission, -1500m);

		// Rule activates correctly when commission goes below negative threshold
		rule.ProcessMessage(positionMsg).AssertTrue();
	}

	[TestMethod]
	public void Slippage()
	{
		var rule = new RiskSlippageRule
		{
			Slippage = 10,
			Action = RiskActions.CancelOrders
		};

		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			Slippage = 5
		};

		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Slippage = 15;
		rule.ProcessMessage(execMsg).AssertTrue();
	}

	[TestMethod]
	public void SlippageNegative()
	{
		var rule = new RiskSlippageRule
		{
			Slippage = -10,
			Action = RiskActions.CancelOrders
		};

		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			Slippage = -5
		};

		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Slippage = -15;
		rule.ProcessMessage(execMsg).AssertTrue();
	}

	[TestMethod]
	public void SlippageZero()
	{
		var rule = new RiskSlippageRule
		{
			Slippage = 0,
			Action = RiskActions.CancelOrders
		};

		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			Slippage = 100
		};

		// Should not activate for positive slippage when limit is zero
		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Slippage = -100;
		// Should not activate for negative slippage either
		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Slippage = null;
		// Null slippage must be ignored
		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Slippage = 0;
		rule.ProcessMessage(execMsg).AssertFalse();
	}

	[TestMethod]
	public void OrderPrice()
	{
		var rule = new RiskOrderPriceRule
		{
			Price = 100,
			Action = RiskActions.CancelOrders
		};

		var orderRegMsg = new OrderRegisterMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			Price = 90
		};

		rule.ProcessMessage(orderRegMsg).AssertFalse();

		orderRegMsg.Price = 110;
		rule.ProcessMessage(orderRegMsg).AssertTrue();

		var orderReplaceMsg = new OrderReplaceMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			Price = 80
		};

		rule.ProcessMessage(orderReplaceMsg).AssertFalse();

		orderReplaceMsg.Price = 120;
		rule.ProcessMessage(orderReplaceMsg).AssertTrue();
	}

	[TestMethod]
	public void OrderVolume()
	{
		var rule = new RiskOrderVolumeRule
		{
			Volume = 1000,
			Action = RiskActions.StopTrading
		};

		var orderRegMsg = new OrderRegisterMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			Volume = 500
		};

		rule.ProcessMessage(orderRegMsg).AssertFalse();

		orderRegMsg.Volume = 1500;
		rule.ProcessMessage(orderRegMsg).AssertTrue();

		var orderReplaceMsg = new OrderReplaceMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			Volume = 800
		};

		rule.ProcessMessage(orderReplaceMsg).AssertFalse();

		orderReplaceMsg.Volume = 1200;
		rule.ProcessMessage(orderReplaceMsg).AssertTrue();
	}

	[TestMethod]
	public void OrderVolumeInvalidVolume()
	{
		var rule = new RiskOrderVolumeRule();

		ThrowsExactly<ArgumentOutOfRangeException>(() => rule.Volume = -100);
	}

	[TestMethod]
	public void OrderFreq()
	{
		var rule = new RiskOrderFreqRule
		{
			Count = 3,
			Interval = TimeSpan.FromSeconds(10),
			Action = RiskActions.CancelOrders
		};

		var startTime = DateTime.UtcNow;

		for (int i = 0; i < 2; i++)
		{
			var orderMsg = new OrderRegisterMessage
			{
				SecurityId = Helper.CreateSecurityId(),
				LocalTime = startTime.AddSeconds(i)
			};

			rule.ProcessMessage(orderMsg).AssertFalse();
		}

		var thirdOrderMsg = new OrderRegisterMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			LocalTime = startTime.AddSeconds(5)
		};

		rule.ProcessMessage(thirdOrderMsg).AssertTrue();

		var fourthOrderMsg = new OrderRegisterMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			LocalTime = startTime.AddSeconds(6)
		};

		// Canonical rolling-window semantics: at +6s all four orders (0s, 1s, 5s, 6s)
		// still fall inside the 10s window, so the rolling count (4) meets-or-exceeds
		// the limit (3) and the rule keeps rejecting. The former fixed, non-overlapping
		// window reset after tripping and would have admitted this order; the rolling
		// window is strictly stricter near a boundary (stricter-wins reconciliation).
		rule.ProcessMessage(fourthOrderMsg).AssertTrue();
	}

	[TestMethod]
	public void OrderFreqReset()
	{
		var rule = new RiskOrderFreqRule
		{
			Count = 2,
			Interval = TimeSpan.FromSeconds(10)
		};

		var orderMsg = new OrderRegisterMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			LocalTime = DateTime.UtcNow
		};

		rule.ProcessMessage(orderMsg);
		rule.Reset();

		rule.ProcessMessage(orderMsg).AssertFalse();
		rule.ProcessMessage(orderMsg).AssertTrue();
	}

	[TestMethod]
	public void OrderError()
	{
		var rule = new RiskOrderErrorRule
		{
			Count = 3,
			Action = RiskActions.StopTrading
		};

		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			Error = new InvalidOperationException("Test error")
		};

		rule.ProcessMessage(execMsg).AssertFalse();
		rule.ProcessMessage(execMsg).AssertFalse();
		rule.ProcessMessage(execMsg).AssertTrue();

		var successMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			HasOrderInfo = true,
			OrderState = OrderStates.Active
		};

		rule.ProcessMessage(successMsg);

		rule.ProcessMessage(execMsg).AssertFalse();
	}

	[TestMethod]
	public void TradePrice()
	{
		var rule = new RiskTradePriceRule
		{
			Price = 100,
			Action = RiskActions.ClosePositions
		};

		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			TradePrice = 90,
			TradeVolume = 10
		};

		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.TradePrice = 110;
		rule.ProcessMessage(execMsg).AssertTrue();
	}

	[TestMethod]
	public void TradeVolume()
	{
		var rule = new RiskTradeVolumeRule
		{
			Volume = 1000,
			Action = RiskActions.CancelOrders
		};

		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			TradePrice = 100,
			TradeVolume = 500
		};

		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.TradeVolume = 1500;
		rule.ProcessMessage(execMsg).AssertTrue();
	}

	[TestMethod]
	public void TradeFreq()
	{
		var rule = new RiskTradeFreqRule
		{
			Count = 2,
			Interval = TimeSpan.FromSeconds(5),
			Action = RiskActions.StopTrading
		};

		var startTime = DateTime.UtcNow;

		var tradeMsg1 = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			LocalTime = startTime,
			TradePrice = 100,
			TradeVolume = 10
		};

		rule.ProcessMessage(tradeMsg1).AssertFalse();

		var tradeMsg2 = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			LocalTime = startTime.AddSeconds(2),
			TradePrice = 100,
			TradeVolume = 20
		};

		rule.ProcessMessage(tradeMsg2).AssertTrue();
	}

	[TestMethod]
	public void Error()
	{
		var rule = new RiskErrorRule
		{
			Count = 2,
			Action = RiskActions.ClosePositions
		};

		var errorMsg = new ErrorMessage
		{
			Error = new InvalidOperationException("Test error")
		};

		rule.ProcessMessage(errorMsg).AssertFalse();
		rule.ProcessMessage(errorMsg).AssertTrue();
	}

	[TestMethod]
	public void PropertyNotification()
	{
		var rule = new RiskPnLRule();
		var propertyChanged = false;
		string changedPropertyName = null;

		((INotifyPropertyChanged)rule).PropertyChanged += (s, e) =>
		{
			propertyChanged = true;
			changedPropertyName = e.PropertyName;
		};

		rule.Action = RiskActions.StopTrading;

		propertyChanged.AssertTrue();
		changedPropertyName.AssertEqual(nameof(RiskPnLRule.Action));
	}

	[TestMethod]
	public void UpdateTitle()
	{
		var rule = new RiskPnLRule
		{
			PnL = new() { Value = 1000, Type = UnitTypes.Absolute }
		};

		rule.Title.AssertNotNull();
		rule.Title.Contains("1000").AssertTrue();

		rule.PnL = new() { Value = 2000, Type = UnitTypes.Absolute };
		rule.Title.Contains("2000").AssertTrue();
	}

	[TestMethod]
	public void SaveLoad()
	{
		var originalRule = new RiskPnLRule
		{
			PnL = new() { Value = 1500, Type = UnitTypes.Percent },
			Action = RiskActions.StopTrading
		};

		var storage = originalRule.Save();

		var restoredRule = new RiskPnLRule();
		restoredRule.Load(storage);

		restoredRule.PnL.Value.AssertEqual(1500);
		restoredRule.PnL.Type.AssertEqual(UnitTypes.Percent);
		restoredRule.Action.AssertEqual(RiskActions.StopTrading);
	}

	[TestMethod]
	public void ProviderDefaultRules()
	{
		IRiskRuleProvider provider = new InMemoryRiskRuleProvider();

		var rules = provider.All.ToList();
		rules.Count.AssertEqual(17);

		rules.Count(t => t == typeof(RiskPnLRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskPositionSizeRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskPositionTimeRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskCommissionRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskSlippageRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskOrderPriceRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskOrderVolumeRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskOrderValueRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskDailyVolumeRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskOrderFreqRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskOrderErrorRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskOrderCommissionRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskTradePriceRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskTradeVolumeRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskTradeFreqRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskTradeCommissionRule)).AssertEqual(1);
		rules.Count(t => t == typeof(RiskErrorRule)).AssertEqual(1);
	}

	[TestMethod]
	public void ProviderAddRemoveRule()
	{
		IRiskRuleProvider provider = new InMemoryRiskRuleProvider();

		var customRuleType = typeof(TestRiskRule);

		provider.Add(customRuleType);
		provider.All.Count(t => t == customRuleType).AssertEqual(1);

		provider.Remove(customRuleType);
		provider.All.Count(t => t == customRuleType).AssertEqual(0);
	}

	[TestMethod]
	public void Serialization()
	{
		IRiskRuleProvider provider = new InMemoryRiskRuleProvider();

		var excludeProps = new HashSet<string>
		{
			nameof(ILogSource.Id),
			nameof(ILogSource.Parent),
			nameof(ILogSource.IsRoot),
		};

		foreach (var ruleType in provider.All)
		{
			var rule = ruleType.CreateInstance<IRiskRule>();

			var props = ruleType
				.GetModifiableProps()
				.Where(p => !excludeProps.Contains(p.Name))
				.ToArray();

			foreach (var prop in props)
			{
				var propType = prop.PropertyType;
				propType = propType.GetUnderlyingType() ?? prop.PropertyType;

				object value;

				if (propType == typeof(Unit))
					value = new Unit { Value = RandomGen.GetInt(), Type = UnitTypes.Percent };
				else if (propType.IsNumeric())
					value = RandomGen.GetInt().To(propType);
				else if (propType.IsEnum)
					value = RandomGen.GetEnum(propType);
				else if (propType == typeof(TimeSpan))
					value = TimeSpan.FromSeconds(RandomGen.GetInt(1, 3600));
				else if (propType == typeof(string))
					value = RandomGen.GetString(3, 7);
				else
					throw new InvalidOperationException($"Unsupported property type: {propType.FullName}");

				prop.SetValue(rule, value);
			}

			var storage = rule.Save();

			var restored = ruleType.CreateInstance<IRiskRule>();
			restored.Load(storage);

			foreach (var prop in props)
			{
				var origValue = prop.GetValue(rule);
				var restoredValue = prop.GetValue(restored);

				origValue.AssertEqual(restoredValue);
			}
		}
	}

	[TestMethod]
	public void CommissionTrade()
	{
		var rule = new RiskTradeCommissionRule
		{
			Commission = 500,
			Action = RiskActions.StopTrading
		};

		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			Commission = 300m,
			TradePrice = 100,
			TradeVolume = 10
		};

		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Commission = 300m;
		rule.ProcessMessage(execMsg).AssertTrue();
	}

	[TestMethod]
	public void CommissionTradeNegative()
	{
		var rule = new RiskTradeCommissionRule
		{
			Commission = -500,
			Action = RiskActions.StopTrading
		};

		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			Commission = -300m,
			TradePrice = 100,
			TradeVolume = 10
		};

		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Commission = -300m;
		rule.ProcessMessage(execMsg).AssertTrue();
	}

	[TestMethod]
	public void CommissionOrder()
	{
		var rule = new RiskOrderCommissionRule
		{
			Commission = 100,
			Action = RiskActions.CancelOrders
		};

		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			Commission = 60,
			HasOrderInfo = true
		};

		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Commission = 60m;
		rule.ProcessMessage(execMsg).AssertTrue();
	}

	[TestMethod]
	public void CommissionOrderNegative()
	{
		var rule = new RiskOrderCommissionRule
		{
			Commission = -100,
			Action = RiskActions.CancelOrders
		};

		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			Commission = -60,
			HasOrderInfo = true
		};

		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Commission = -60m;
		rule.ProcessMessage(execMsg).AssertTrue();
	}

	[TestMethod]
	public async Task AdapterClosePositionsMode()
	{
		var token = CancellationToken;
		// Test that RiskMessageAdapter sends OrderGroupCancelMessage with ClosePositions mode
		// when a risk rule with ClosePositions action is triggered

		// Use a custom adapter to intercept messages sent to inner adapter
		var testAdapter = new TestInnerAdapter();
		var riskManager = new RiskManager();
		var adapter = new RiskMessageAdapter(testAdapter, riskManager);

		var rule = new RiskPnLRule
		{
			PnL = new() { Value = -1000, Type = UnitTypes.Absolute },
			Action = RiskActions.ClosePositions
		};
		riskManager.Rules.Add(rule);

		await adapter.SendInMessageAsync(new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		}.Add(PositionChangeTypes.CurrentValue, 0m), token);

		// Trigger the rule by sending a position change message with loss
		await adapter.SendInMessageAsync(new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		}.Add(PositionChangeTypes.CurrentValue, -1500m), token);

		// Check that OrderGroupCancelMessage was sent to inner adapter with ClosePositions mode
		var cancelMsg = testAdapter.ReceivedMessages.OfType<OrderGroupCancelMessage>().FirstOrDefault();
		cancelMsg.AssertNotNull();
		cancelMsg.Mode.AssertEqual(OrderGroupCancelModes.ClosePositions);
	}

	[TestMethod]
	public async Task AdapterCancelOrdersMode()
	{
		// Test that RiskMessageAdapter sends OrderGroupCancelMessage with CancelOrders mode
		// when a risk rule with CancelOrders action is triggered
		var emu = new MarketEmulator(new CollectionSecurityProvider([new() { Id = "TEST@TEST" }]), new CollectionPortfolioProvider([Portfolio.CreateSimulator()]), new InMemoryExchangeInfoProvider(), new IncrementalIdGenerator());
		var emuAdapter = new MarketEmulatorAdapter(emu, new IncrementalIdGenerator());
		var riskManager = new RiskManager();
		var adapter = new RiskMessageAdapter(emuAdapter, riskManager);

		var messages = new List<Message>();
		adapter.NewOutMessageAsync += (m, ct) => { messages.Add(m); return default; };

		var rule = new RiskPositionSizeRule
		{
			Position = 100,
			Action = RiskActions.CancelOrders
		};
		riskManager.Rules.Add(rule);

		// Trigger the rule by sending a position change message exceeding the limit
		var positionMsg = new PositionChangeMessage
		{
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.CurrentValue, 150m);

		await adapter.SendInMessageAsync(positionMsg, CancellationToken);

		// Check that OrderGroupCancelMessage was sent with CancelOrders mode (looped back)
		var cancelMsg = messages.OfType<OrderGroupCancelMessage>().FirstOrDefault();
		cancelMsg.AssertNotNull();
		// CancelOrders is the default mode, should be set
		(cancelMsg.Mode & OrderGroupCancelModes.CancelOrders).AssertEqual(OrderGroupCancelModes.CancelOrders);
	}

	[TestMethod]
	public async Task AdapterStopTradingBlocks()
	{
		var token = CancellationToken;
		// Test that RiskMessageAdapter blocks trading when StopTrading action is triggered
		var riskManager = new RiskManager();
		var adapter = new RiskMessageAdapter(new TestInnerAdapter(), riskManager);

		var messages = new List<Message>();
		adapter.NewOutMessageAsync += (m, ct) => { messages.Add(m); return default; };

		var rule = new RiskCommissionRule
		{
			Commission = 1000,
			Action = RiskActions.StopTrading
		};
		riskManager.Rules.Add(rule);

		// Trigger the rule by sending a position change message with high commission
		var positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.Commission, 1500m);

		await adapter.SendInMessageAsync(positionMsg, token);

		// Now try to register an order - it should be rejected
		var orderMsg = new OrderRegisterMessage
		{
			TransactionId = 1,
			SecurityId = Helper.CreateSecurityId(),
			Side = Sides.Buy,
			Price = 100,
			Volume = 10,
			PortfolioName = _pfName
		};

		await adapter.SendInMessageAsync(orderMsg, token);

		// Check that the order was rejected with Failed state
		var execMsg = messages.OfType<ExecutionMessage>()
			.FirstOrDefault(x => x.OriginalTransactionId == 1 && x.OrderState == OrderStates.Failed);
		execMsg.AssertNotNull();
		execMsg.Error.AssertNotNull();
	}

	[TestMethod]
	public async Task AdapterTradingUnblocks()
	{
		var token = CancellationToken;
		// Test that RiskMessageAdapter unblocks trading when risk limits are no longer exceeded
		var testAdapter = new TestInnerAdapter();
		var riskManager = new RiskManager();
		var adapter = new RiskMessageAdapter(testAdapter, riskManager);

		var messages = new List<Message>();
		adapter.NewOutMessageAsync += (m, ct) => { messages.Add(m); return default; };

		var rule = new RiskPnLRule
		{
			PnL = new() { Value = -1000, Type = UnitTypes.Absolute },
			Action = RiskActions.StopTrading
		};
		riskManager.Rules.Add(rule);

		await adapter.SendInMessageAsync(new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		}.Add(PositionChangeTypes.CurrentValue, 0m), token);

		// Trigger the rule by sending a position change message with loss
		await adapter.SendInMessageAsync(new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		}.Add(PositionChangeTypes.CurrentValue, -1500m), token);

		// Verify trading is blocked
		var orderMsg = new OrderRegisterMessage
		{
			TransactionId = 1,
			SecurityId = Helper.CreateSecurityId(),
			Side = Sides.Buy,
			Price = 100,
			Volume = 10,
			PortfolioName = _pfName
		};

		await adapter.SendInMessageAsync(orderMsg, token);

		var execMsg = messages.OfType<ExecutionMessage>()
			.FirstOrDefault(x => x.OriginalTransactionId == 1 && x.OrderState == OrderStates.Failed);
		execMsg.AssertNotNull();

		messages.Clear();
		testAdapter.ReceivedMessages.Clear();

		// Now send a position message that no longer exceeds the limit
		await adapter.SendInMessageAsync(new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		}.Add(PositionChangeTypes.CurrentValue, -500m), token);

		// Try to register an order again - it should now be accepted (not rejected)
		var orderMsg2 = new OrderRegisterMessage
		{
			TransactionId = 2,
			SecurityId = Helper.CreateSecurityId(),
			Side = Sides.Buy,
			Price = 100,
			Volume = 10,
			PortfolioName = _pfName
		};

		await adapter.SendInMessageAsync(orderMsg2, token);

		// Check that the order was NOT rejected
		var failedMsg = messages.OfType<ExecutionMessage>()
			.FirstOrDefault(x => x.OriginalTransactionId == 2 && x.OrderState == OrderStates.Failed);
		failedMsg.AssertNull();

		// Verify the message was sent to inner adapter
		var sentOrder = testAdapter.ReceivedMessages.OfType<OrderRegisterMessage>()
			.FirstOrDefault(x => x.TransactionId == 2);
		sentOrder.AssertNotNull();
	}

	[TestMethod]
	public void TradePriceNullPrice()
	{
		var rule = new RiskTradePriceRule
		{
			Price = 1000,
			Action = RiskActions.CancelOrders
		};

		// Trade message with null TradeVolume should not throw and should return false
		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			TradePrice = null,
			TradeVolume = 10
		};

		// Should not throw and should return false since volume is null
		rule.ProcessMessage(execMsg).AssertFalse();
	}

	[TestMethod]
	public void TradeVolumeNullVolume()
	{
		var rule = new RiskTradeVolumeRule
		{
			Volume = 1000,
			Action = RiskActions.CancelOrders
		};

		// Trade message with null TradeVolume should not throw and should return false
		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			TradePrice = 100,
			TradeVolume = null
		};

		// Should not throw and should return false since volume is null
		rule.ProcessMessage(execMsg).AssertFalse();
	}

	private class TestRiskRule : RiskRule
	{
		public bool ShouldActivate { get; set; }
		public bool IsReset { get; private set; }
		public Message LastMessage { get; private set; }

		protected override string GetTitle() => "Test Rule";

		public override void Reset()
		{
			base.Reset();
			IsReset = true;
		}

		public override bool ProcessMessage(Message message)
		{
			LastMessage = message;
			return ShouldActivate;
		}
	}

	// Wrapper adapter that captures all incoming messages
	private class TestInnerAdapter : PassThroughMessageAdapter
	{
		public List<Message> ReceivedMessages { get; } = [];

		public TestInnerAdapter()
			: base(new IncrementalIdGenerator())
		{
		}

		public override ValueTask SendInMessageAsync(Message message, CancellationToken cancellationToken)
		{
			ReceivedMessages.Add(message);
			return base.SendInMessageAsync(message, cancellationToken);
		}

		public override IMessageAdapter Clone() => new TestInnerAdapter();
	}

	[TestMethod]
	public void CommissionZero()
	{
		var rule = new RiskCommissionRule
		{
			Commission = 0,
			Action = RiskActions.StopTrading
		};

		var positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.Commission, 1500m);

		rule.ProcessMessage(positionMsg).AssertFalse();

		positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		positionMsg.Add(PositionChangeTypes.Commission, -1500m);

		rule.ProcessMessage(positionMsg).AssertFalse();

		positionMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		// No commission value
		rule.ProcessMessage(positionMsg).AssertFalse();
	}

	[TestMethod]
	public void CommissionTradeZero()
	{
		var rule = new RiskTradeCommissionRule
		{
			Commission = 0,
			Action = RiskActions.StopTrading
		};

		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			Commission = 600m,
			TradePrice = 100,
			TradeVolume = 10
		};

		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Commission = -600m;
		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Commission = null;
		rule.ProcessMessage(execMsg).AssertFalse();
	}

	[TestMethod]
	public void CommissionOrderZero()
	{
		var rule = new RiskOrderCommissionRule
		{
			Commission = 0,
			Action = RiskActions.CancelOrders
		};

		var execMsg = new ExecutionMessage
		{
			DataTypeEx = DataType.Transactions,
			SecurityId = Helper.CreateSecurityId(),
			ServerTime = DateTime.UtcNow,
			Commission = 150m,
			HasOrderInfo = true
		};

		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Commission = -150m;
		rule.ProcessMessage(execMsg).AssertFalse();

		execMsg.Commission = null;
		rule.ProcessMessage(execMsg).AssertFalse();
	}

	// ========================================================================
	// Reconciliation parity / characterization tests (AAP 0.6.7).
	//
	// These cover every rule in the LEGACY_LAYER.md coverage table plus the
	// documented edge cases, proving each rule either matches the chosen
	// canonical behaviour or correctly preserves an intentionally-distinct one.
	// They are net-new automated coverage for the risk/position logic that was
	// relocated out of the SQL layer (which previously had no unit tests).
	// ========================================================================

	// --- Price / quantity ceilings: canonical ">=" ("meets or exceeds") ------

	[TestMethod]
	public void OrderPriceBoundaryMeetsOrExceeds()
	{
		// Canonical RiskOrderPriceRule rejects when price EQUALS the limit, not
		// only when it strictly exceeds it. Switching to ">" would loosen the
		// control, so the boundary is inclusive (AAP 0.6.6).
		var rule = new RiskOrderPriceRule
		{
			Price = 100,
			Action = RiskActions.CancelOrders
		};

		var below = new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Price = 99.9999m };
		rule.ProcessMessage(below).AssertFalse();

		var atLimit = new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Price = 100m };
		rule.ProcessMessage(atLimit).AssertTrue();

		var above = new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Price = 100.0001m };
		rule.ProcessMessage(above).AssertTrue();
	}

	[TestMethod]
	public void OrderVolumeBoundaryMeetsOrExceeds()
	{
		// Canonical RiskOrderVolumeRule rejects at qty EQUAL to the limit ("qty
		// >= max_order_qty"), matching the SQL gate exactly.
		var rule = new RiskOrderVolumeRule
		{
			Volume = 1000,
			Action = RiskActions.StopTrading
		};

		var below = new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 999m };
		rule.ProcessMessage(below).AssertFalse();

		var atLimit = new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 1000m };
		rule.ProcessMessage(atLimit).AssertTrue();

		var above = new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 1001m };
		rule.ProcessMessage(above).AssertTrue();
	}

	// --- Order notional value: relocated from SQL-only to first-class C# rule -

	[TestMethod]
	public void OrderValueRuleNotional()
	{
		// RiskOrderValueRule is the canonical C# home of the previously SQL-only
		// "max_order_value" check: reject when qty*price >= limit.
		var rule = new RiskOrderValueRule
		{
			OrderValue = 1000,
			Action = RiskActions.CancelOrders
		};

		// 10 * 90 = 900 < 1000 -> allowed
		var below = new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 10m, Price = 90m };
		rule.ProcessMessage(below).AssertFalse();

		// 10 * 100 = 1000 >= 1000 -> rejected (boundary inclusive)
		var atLimit = new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 10m, Price = 100m };
		rule.ProcessMessage(atLimit).AssertTrue();

		// 10 * 110 = 1100 >= 1000 -> rejected
		var above = new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 10m, Price = 110m };
		rule.ProcessMessage(above).AssertTrue();
	}

	[TestMethod]
	public void OrderValueRuleZeroDisabled()
	{
		// 0 = "not enforced" (AAP 0.6.6): the rule never trips regardless of notional.
		var rule = new RiskOrderValueRule { OrderValue = 0 };

		var huge = new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 1_000_000m, Price = 1_000m };
		rule.ProcessMessage(huge).AssertFalse();
	}

	[TestMethod]
	public void OrderValueRuleInvalidValue()
	{
		// A negative threshold is a misconfiguration and must be rejected at the
		// setter (fail closed at configuration time).
		var rule = new RiskOrderValueRule();

		ThrowsExactly<ArgumentOutOfRangeException>(() => rule.OrderValue = -100);
	}

	// --- Order frequency: canonical ROLLING window (stricter-wins) -----------

	[TestMethod]
	public void OrderFreqRollingBoundary()
	{
		// The rolling window's lower bound is INCLUSIVE ([now - Interval, now]):
		// an order landing exactly Interval ago is still counted, matching the
		// SQL ">= now - window" predicate; an order strictly older than Interval
		// has aged out. This is the reconciled, stricter-wins behaviour.

		// (a) inclusive boundary: the t0 order is exactly Interval old at t0+10s
		//     and is still counted, so the 2nd order trips a Count=2 limit.
		var inclusive = new RiskOrderFreqRule { Count = 2, Interval = TimeSpan.FromSeconds(10) };
		var t0 = DateTime.UtcNow;

		inclusive.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), LocalTime = t0 }).AssertFalse();
		inclusive.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), LocalTime = t0.AddSeconds(10) }).AssertTrue();

		// (b) strictly older is evicted: at t0+11s the t0 order is > Interval old
		//     and drops out, so the window count falls back below the limit.
		var evicts = new RiskOrderFreqRule { Count = 2, Interval = TimeSpan.FromSeconds(10) };

		evicts.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), LocalTime = t0 }).AssertFalse();
		evicts.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), LocalTime = t0.AddSeconds(11) }).AssertFalse();
	}

	[TestMethod]
	public void OrderFreqZeroIntervalDisabled()
	{
		// A zero (or negative) Interval means "not enforced" - without this the
		// window would collapse and a Count=1 rule would trip on every order.
		var rule = new RiskOrderFreqRule { Count = 1, Interval = TimeSpan.Zero };
		var now = DateTime.UtcNow;

		for (var i = 0; i < 5; i++)
			rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), LocalTime = now.AddSeconds(i) }).AssertFalse();
	}

	[TestMethod]
	public void OrderFreqBoundedHighRate()
	{
		// The retained-timestamp buffer is bounded to Count, so a high order rate
		// cannot grow memory without bound (CWE-400). Behaviour must stay correct:
		// the first Count-1 orders are admitted, everything after keeps tripping.
		var rule = new RiskOrderFreqRule { Count = 3, Interval = TimeSpan.FromHours(1) };
		var start = DateTime.UtcNow;

		bool? third = null;
		bool? last = null;

		for (var i = 0; i < 50; i++)
		{
			var tripped = rule.ProcessMessage(new OrderRegisterMessage
			{
				SecurityId = Helper.CreateSecurityId(),
				LocalTime = start.AddSeconds(i)
			});

			if (i == 0)
				tripped.AssertFalse();   // 1st: prior 0 -> 1 >= 3 false
			if (i == 2)
				third = tripped;         // 3rd: prior 2 -> 3 >= 3 true
			if (i == 49)
				last = tripped;          // last: still within the 1h window -> true
		}

		third.Value.AssertTrue();
		last.Value.AssertTrue();
	}

	[TestMethod]
	public void FrequencyExceededHelperParity()
	{
		// The shared decision both enforcement patterns route through:
		// reject when (orders already in window) + 1 >= Count.
		RiskOrderFreqRule.IsFrequencyExceeded(0, 3).AssertFalse(); // 1 >= 3
		RiskOrderFreqRule.IsFrequencyExceeded(1, 3).AssertFalse(); // 2 >= 3
		RiskOrderFreqRule.IsFrequencyExceeded(2, 3).AssertTrue();  // 3 >= 3 (boundary)
		RiskOrderFreqRule.IsFrequencyExceeded(3, 3).AssertTrue();  // 4 >= 3
	}

	[TestMethod]
	public void OrderPriceExceededHelperParity()
	{
		// MJ-3: single shared decision for the order-price ceiling. Both the
		// RiskManager stream rule (ProcessMessage) and the PreTradeRiskService gate
		// route through IsOrderPriceExceeded, so 0 = "not enforced" and the ">="
		// boundary can never diverge between the two enforcement patterns.
		RiskOrderPriceRule.IsOrderPriceExceeded(90m, 100m).AssertFalse();
		RiskOrderPriceRule.IsOrderPriceExceeded(100m, 100m).AssertTrue();   // boundary (">=")
		RiskOrderPriceRule.IsOrderPriceExceeded(110m, 100m).AssertTrue();
		RiskOrderPriceRule.IsOrderPriceExceeded(1_000m, 0m).AssertFalse();  // 0 = not enforced

		// The canonical rule's ProcessMessage MUST agree with the helper, including
		// the 0 = "not enforced" case: before MJ-3 the rule tripped at price >= 0
		// (0 meant "always reject") while the gate treated 0 as disabled - divergent.
		var enforced = new RiskOrderPriceRule { Price = 100m };
		enforced.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Price = 100m }).AssertTrue();
		enforced.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Price = 90m }).AssertFalse();

		var disabled = new RiskOrderPriceRule { Price = 0m };
		disabled.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Price = 1_000m }).AssertFalse();
	}

	[TestMethod]
	public void OrderVolumeExceededHelperParity()
	{
		// MJ-3: single shared decision for the order-quantity ceiling, mirroring the
		// price rule. Both enforcement patterns route through IsOrderVolumeExceeded.
		RiskOrderVolumeRule.IsOrderVolumeExceeded(500m, 1_000m).AssertFalse();
		RiskOrderVolumeRule.IsOrderVolumeExceeded(1_000m, 1_000m).AssertTrue();  // boundary (">=")
		RiskOrderVolumeRule.IsOrderVolumeExceeded(1_500m, 1_000m).AssertTrue();
		RiskOrderVolumeRule.IsOrderVolumeExceeded(1_000_000m, 0m).AssertFalse(); // 0 = not enforced

		var enforced = new RiskOrderVolumeRule { Volume = 1_000m };
		enforced.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 1_000m }).AssertTrue();
		enforced.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 500m }).AssertFalse();

		var disabled = new RiskOrderVolumeRule { Volume = 0m };
		disabled.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 1_000_000m }).AssertFalse();
	}

	// --- Position size: canonical SYMMETRIC-ABSOLUTE comparison --------------

	[TestMethod]
	public void PositionSizeShortTripsPositiveLimit()
	{
		// Canonical rule uses |value| >= |limit|, so a SHORT position breaches a
		// POSITIVE configured limit once its magnitude reaches it. A directional
		// (signed) comparison would have missed this and been less strict.
		var rule = new RiskPositionSizeRule
		{
			Position = 100,
			Action = RiskActions.CancelOrders
		};

		var within = new PositionChangeMessage { SecurityId = Helper.CreateSecurityId(), ServerTime = DateTime.UtcNow, PortfolioName = _pfName };
		within.Add(PositionChangeTypes.CurrentValue, -50m);
		rule.ProcessMessage(within).AssertFalse();

		var breach = new PositionChangeMessage { SecurityId = Helper.CreateSecurityId(), ServerTime = DateTime.UtcNow, PortfolioName = _pfName };
		breach.Add(PositionChangeTypes.CurrentValue, -150m);
		rule.ProcessMessage(breach).AssertTrue();
	}

	[TestMethod]
	public void PositionSizeExceededHelperParity()
	{
		// Shared symmetric-absolute decision used by both the circuit breaker
		// (live value) and the pre-trade gate (post-fill projection).
		RiskPositionSizeRule.IsPositionSizeExceeded(50m, 100m).AssertFalse();
		RiskPositionSizeRule.IsPositionSizeExceeded(100m, 100m).AssertTrue();   // boundary
		RiskPositionSizeRule.IsPositionSizeExceeded(-150m, 100m).AssertTrue();  // short breaches positive limit
		RiskPositionSizeRule.IsPositionSizeExceeded(150m, -100m).AssertTrue();  // long breaches negative limit
		RiskPositionSizeRule.IsPositionSizeExceeded(1_000_000m, 0m).AssertFalse(); // 0 = not enforced
	}

	// --- Daily traded volume: relocated from SQL-only, cumulative in stream --

	[TestMethod]
	public void DailyVolumeCumulative()
	{
		// The stream path keeps a running daily total so a series of individually
		// sub-limit orders still trips the limit (the CRITICAL parity fix - the
		// old rule never accumulated). ">=" boundary; same UTC day.
		var rule = new RiskDailyVolumeRule
		{
			DailyVolume = 100,
			Action = RiskActions.StopTrading
		};

		var day = new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc);

		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 40m, LocalTime = day }).AssertFalse();      // 40
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 40m, LocalTime = day.AddMinutes(1) }).AssertFalse(); // 80
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 40m, LocalTime = day.AddMinutes(2) }).AssertTrue();  // 120 >= 100
	}

	[TestMethod]
	public void DailyVolumeDayRollover()
	{
		// The running total resets when the day rolls over, so yesterday's volume
		// does not carry into today.
		var rule = new RiskDailyVolumeRule { DailyVolume = 100 };

		var day1 = new DateTime(2024, 1, 2, 23, 0, 0, DateTimeKind.Utc);
		var day2 = new DateTime(2024, 1, 3, 1, 0, 0, DateTimeKind.Utc);

		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 80m, LocalTime = day1 }).AssertFalse();      // day1: 80
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 80m, LocalTime = day1.AddMinutes(1) }).AssertTrue(); // day1: 160 >= 100
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 80m, LocalTime = day2 }).AssertFalse();      // day2 reset: 80
	}

	[TestMethod]
	public void DailyVolumeExceededHelperParity()
	{
		// Shared decision used by both patterns: reject when limit != 0 and the
		// cumulative daily volume >= limit.
		RiskDailyVolumeRule.IsDailyVolumeExceeded(80m, 100m).AssertFalse();
		RiskDailyVolumeRule.IsDailyVolumeExceeded(100m, 100m).AssertTrue();  // boundary
		RiskDailyVolumeRule.IsDailyVolumeExceeded(120m, 100m).AssertTrue();
		RiskDailyVolumeRule.IsDailyVolumeExceeded(1_000m, 0m).AssertFalse(); // 0 = not enforced
	}

	[TestMethod]
	public void DailyVolumeInvalidValue()
	{
		var rule = new RiskDailyVolumeRule();

		ThrowsExactly<ArgumentOutOfRangeException>(() => rule.DailyVolume = -1);
	}

	[TestMethod]
	public void DailyVolumeIgnoresNonPositiveVolume()
	{
		// CR-2: a negative or zero order volume must NEVER move the authoritative
		// daily accumulator. Before the fix a negative OrderRegister volume
		// silently REDUCED today's running total, loosening the control.
		var rule = new RiskDailyVolumeRule { DailyVolume = 100 };
		var day = new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc);

		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 90m, LocalTime = day }).AssertFalse();                    // 90
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = -1000m, LocalTime = day.AddMinutes(1) }).AssertFalse();  // ignored, still 90
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 0m, LocalTime = day.AddMinutes(2) }).AssertFalse();      // ignored, still 90
		// A real +10 now reaches the limit, proving the 90 was preserved intact.
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 10m, LocalTime = day.AddMinutes(3) }).AssertTrue();       // 100 >= 100
	}

	[TestMethod]
	public void DailyVolumeIgnoresDefaultTimestamp()
	{
		// CR-2: a message with a default/unset timestamp must not be bucketed as
		// day 0001-01-01 and destructively reset the authoritative current-day total.
		var rule = new RiskDailyVolumeRule { DailyVolume = 100 };
		var day = new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc);

		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 90m, LocalTime = day }).AssertFalse();          // 90
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 50m, LocalTime = default }).AssertFalse();     // ignored, still 90
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 10m, LocalTime = day.AddMinutes(1) }).AssertTrue(); // 100 - current-day state survived
	}

	[TestMethod]
	public void DailyVolumeIgnoresOutOfOrderOlderDay()
	{
		// CR-2: once today's authoritative total is established, an out-of-order
		// message stamped for an EARLIER day must never roll the state backward and
		// erase today's accumulated volume.
		var rule = new RiskDailyVolumeRule { DailyVolume = 100 };
		var today = new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc);
		var yesterday = new DateTime(2024, 1, 2, 23, 0, 0, DateTimeKind.Utc);

		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 90m, LocalTime = today }).AssertFalse();     // today: 90
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 90m, LocalTime = yesterday }).AssertFalse(); // older day ignored, today still 90
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 10m, LocalTime = today.AddMinutes(1) }).AssertTrue(); // today: 100
	}

	[TestMethod]
	public void DailyVolumeStaysTrippedOnceOverLimit()
	{
		// CR-2: current-day totals at/over the limit keep tripping - the fix
		// preserves the authoritative running total rather than losing it.
		var rule = new RiskDailyVolumeRule { DailyVolume = 100 };
		var day = new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc);

		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 120m, LocalTime = day }).AssertTrue();               // 120 >= 100
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 1m, LocalTime = day.AddMinutes(1) }).AssertTrue();  // 121 >= 100
	}

	[TestMethod]
	public void DailyVolumeNormalizesLocalTimestampToUtc()
	{
		// CR-2: Local-kind timestamps are converted to UTC before bucketing so the
		// day boundary is consistent. A UTC instant and the equivalent Local-kind
		// time fall in the SAME UTC day and accumulate together.
		var rule = new RiskDailyVolumeRule { DailyVolume = 100 };
		var utc = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
		var localSameInstant = utc.ToLocalTime(); // Local kind, same instant

		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 60m, LocalTime = utc }).AssertFalse();                          // day: 60
		rule.ProcessMessage(new OrderRegisterMessage { SecurityId = Helper.CreateSecurityId(), Volume = 60m, LocalTime = localSameInstant.AddMinutes(1) }).AssertTrue(); // same UTC day: 120 >= 100
	}

	// --- Position recalculation: pure average-cost + realized-P&L port -------

	[TestMethod]
	public void RecalculateOpenAndAdd()
	{
		// Open a fresh long, then add to it and weighted-average the price in.
		var opened = PositionRecalculationService.Recalculate(0m, 0m, 0m, Sides.Buy, 100m, 150m);
		opened.Quantity.AssertEqual(100m);
		opened.AveragePrice.AssertEqual(150m);
		opened.RealizedPnl.AssertEqual(0m);

		// (100*150 + 50*180) / 150 = 24000 / 150 = 160
		var added = PositionRecalculationService.Recalculate(100m, 150m, 0m, Sides.Buy, 50m, 180m);
		added.Quantity.AssertEqual(150m);
		added.AveragePrice.AssertEqual(160m);
		added.RealizedPnl.AssertEqual(0m);
	}

	[TestMethod]
	public void RecalculateWeightedAverageRounding()
	{
		// (1*1 + 2*2) / 3 = 5/3 = 1.6666... -> rounded to the SQL DECIMAL(18,4)
		// scale (AwayFromZero) = 1.6667. Proves numeric parity with SQL Server.
		var result = PositionRecalculationService.Recalculate(1m, 1m, 0m, Sides.Buy, 2m, 2m);
		result.Quantity.AssertEqual(3m);
		result.AveragePrice.AssertEqual(1.6667m);
		result.RealizedPnl.AssertEqual(0m);
	}

	[TestMethod]
	public void RecalculatePartialCloseRealizesPnl()
	{
		// Long 100 @ 150, sell 40 @ 170: realize 40*(170-150) = 800 on the closed
		// portion; 60 remain at the original average price.
		var result = PositionRecalculationService.Recalculate(100m, 150m, 0m, Sides.Sell, 40m, 170m);
		result.Quantity.AssertEqual(60m);
		result.AveragePrice.AssertEqual(150m);
		result.RealizedPnl.AssertEqual(800m);
	}

	[TestMethod]
	public void RecalculateExactCloseFlat()
	{
		// Long 100 @ 150, sell 100 @ 170: fully closed, realize 100*(170-150) =
		// 2000; quantity and average price return to zero.
		var result = PositionRecalculationService.Recalculate(100m, 150m, 0m, Sides.Sell, 100m, 170m);
		result.Quantity.AssertEqual(0m);
		result.AveragePrice.AssertEqual(0m);
		result.RealizedPnl.AssertEqual(2000m);
	}

	[TestMethod]
	public void RecalculateCloseAndFlip()
	{
		// Long 100 @ 150, sell 150 @ 170: close the 100 (realize 2000) and open a
		// new short of 50 at the trade price.
		var result = PositionRecalculationService.Recalculate(100m, 150m, 0m, Sides.Sell, 150m, 170m);
		result.Quantity.AssertEqual(-50m);
		result.AveragePrice.AssertEqual(170m);
		result.RealizedPnl.AssertEqual(2000m);
	}

	[TestMethod]
	public void RecalculateShortCover()
	{
		// Short -100 @ 150, buy 40 @ 130: cover 40, realize 40*(130-150)*sign(-100)
		// = 40*(-20)*(-1) = 800; -60 remain at the original average price.
		var result = PositionRecalculationService.Recalculate(-100m, 150m, 0m, Sides.Buy, 40m, 130m);
		result.Quantity.AssertEqual(-60m);
		result.AveragePrice.AssertEqual(150m);
		result.RealizedPnl.AssertEqual(800m);
	}

	[TestMethod]
	public void RecalculateShortExactCloseFlat()
	{
		// Short -100 @ 150, buy 100 @ 130: fully cover the short; realize
		// 100*(130-150)*sign(-100) = 100*(-20)*(-1) = 2000; quantity and average
		// price return to zero. Sign-mirror of RecalculateExactCloseFlat on the
		// short side - the exact-close branch is asserted for both position signs
		// (AAP 0.6.5 exact-close).
		var result = PositionRecalculationService.Recalculate(-100m, 150m, 0m, Sides.Buy, 100m, 130m);
		result.Quantity.AssertEqual(0m);
		result.AveragePrice.AssertEqual(0m);
		result.RealizedPnl.AssertEqual(2000m);
	}

	[TestMethod]
	public void RecalculateShortCloseAndFlip()
	{
		// Short -100 @ 150, buy 150 @ 130: cover the 100 (realize 2000) and open a
		// new long of 50 at the trade price. Sign-mirror of RecalculateCloseAndFlip
		// on the short side - the close-and-flip branch is asserted for both
		// position signs (AAP 0.6.5 full-close-and-flip).
		var result = PositionRecalculationService.Recalculate(-100m, 150m, 0m, Sides.Buy, 150m, 130m);
		result.Quantity.AssertEqual(50m);
		result.AveragePrice.AssertEqual(130m);
		result.RealizedPnl.AssertEqual(2000m);
	}

	[TestMethod]
	public void RecalculateShortOpenAndAdd()
	{
		// Open a fresh short, then add to it and weighted-average the price in.
		// Sign-mirror of RecalculateOpenAndAdd on the short side - the same-sign/
		// flat accumulation branch is asserted for both position signs (AAP 0.6.5).
		var opened = PositionRecalculationService.Recalculate(0m, 0m, 0m, Sides.Sell, 100m, 150m);
		opened.Quantity.AssertEqual(-100m);
		opened.AveragePrice.AssertEqual(150m);
		opened.RealizedPnl.AssertEqual(0m);

		// (100*150 + 50*180) / 150 = 24000 / 150 = 160
		var added = PositionRecalculationService.Recalculate(-100m, 150m, 0m, Sides.Sell, 50m, 180m);
		added.Quantity.AssertEqual(-150m);
		added.AveragePrice.AssertEqual(160m);
		added.RealizedPnl.AssertEqual(0m);
	}

	[TestMethod]
	public void RecalculateInvalidInputThrows()
	{
		// Non-positive trade quantity / price violate the Trades CHECK constraints
		// and (when flat) would divide by zero: fail closed before any arithmetic.
		ThrowsExactly<ArgumentOutOfRangeException>(() => _ = PositionRecalculationService.Recalculate(0m, 0m, 0m, Sides.Buy, 0m, 150m));
		ThrowsExactly<ArgumentOutOfRangeException>(() => _ = PositionRecalculationService.Recalculate(0m, 0m, 0m, Sides.Buy, 100m, 0m));

		// An unrecognised side is never silently treated as a sell.
		ThrowsExactly<ArgumentOutOfRangeException>(() => _ = PositionRecalculationService.Recalculate(0m, 0m, 0m, (Sides)999, 100m, 150m));
	}

	// =====================================================================
	// DB-integration tests for the pre-trade gate (PreTradeRiskService)
	// against a live StockSharpLegacy SQL Server. These exercise the gate
	// end-to-end on real RiskLimits/Orders data: CR-3 order-type-aware price
	// validation, MJ-2 fail-closed configuration validation, MJ-7 DB-clock
	// daily scope, and the MJ-1 malformed-request classification. When the
	// database is unreachable the test is reported INCONCLUSIVE (not failed)
	// so the pure-unit suite still runs where SQL Server is absent. Each test
	// creates a uniquely-named portfolio/security so it is isolated from the
	// seeded DEMO data and from every other test.
	// =====================================================================

	private static async Task<SqlConnection> OpenLegacyOrInconclusiveAsync(CancellationToken ct = default)
	{
		var connection = new SqlConnection(SqlLegacyConnection.Resolve());

		try
		{
			await connection.OpenAsync(ct);
		}
		catch (Exception ex)
		{
			await connection.DisposeAsync();
			Assert.Inconclusive($"StockSharpLegacy SQL Server is not reachable; skipping integration test. {ex.Message}");
			throw; // unreachable - Assert.Inconclusive always throws
		}

		return connection;
	}

	private static async Task<int> InsertPortfolioAsync(SqlConnection conn, CancellationToken ct = default)
	{
		await using var cmd = new SqlCommand(
			"INSERT INTO dbo.Portfolios (name) OUTPUT INSERTED.portfolio_id VALUES (@name)", conn);
		cmd.Parameters.AddWithValue("@name", "ITPF_" + Guid.NewGuid().ToString("N"));
		return (int)await cmd.ExecuteScalarAsync(ct);
	}

	private static async Task<int> InsertSecurityAsync(SqlConnection conn, CancellationToken ct = default)
	{
		await using var cmd = new SqlCommand(
			"INSERT INTO dbo.Securities (security_code, board_code, security_type) OUTPUT INSERTED.security_id VALUES (@code, @board, @type)", conn);
		cmd.Parameters.AddWithValue("@code", "ITSEC_" + Guid.NewGuid().ToString("N"));
		cmd.Parameters.AddWithValue("@board", "ITBRD");
		cmd.Parameters.AddWithValue("@type", "Stock");
		return (int)await cmd.ExecuteScalarAsync(ct);
	}

	private static async Task InsertRiskLimitAsync(
		SqlConnection conn, int portfolioId, int? securityId = null,
		decimal? maxOrderPrice = null, decimal? maxOrderQty = null, decimal? maxOrderValue = null,
		decimal? maxPositionSize = null, decimal? maxDailyVolume = null,
		int? maxOrderFreqCount = null, int? maxOrderFreqWindowSec = null,
		decimal? maxCommissionTotal = null, decimal commissionRate = 0.0005m,
		CancellationToken ct = default)
	{
		await using var cmd = new SqlCommand(
			"""
			INSERT INTO dbo.RiskLimits
				(portfolio_id, security_id, max_order_price, max_order_qty, max_order_value,
				 max_position_size, max_daily_volume, max_order_freq_count, max_order_freq_window_sec,
				 max_commission_total, commission_rate)
			VALUES (@pf, @sec, @price, @qty, @value, @pos, @daily, @fc, @fw, @comm, @rate)
			""", conn);
		cmd.Parameters.AddWithValue("@pf", portfolioId);
		cmd.Parameters.AddWithValue("@sec", (object)securityId ?? DBNull.Value);
		cmd.Parameters.AddWithValue("@price", (object)maxOrderPrice ?? DBNull.Value);
		cmd.Parameters.AddWithValue("@qty", (object)maxOrderQty ?? DBNull.Value);
		cmd.Parameters.AddWithValue("@value", (object)maxOrderValue ?? DBNull.Value);
		cmd.Parameters.AddWithValue("@pos", (object)maxPositionSize ?? DBNull.Value);
		cmd.Parameters.AddWithValue("@daily", (object)maxDailyVolume ?? DBNull.Value);
		cmd.Parameters.AddWithValue("@fc", (object)maxOrderFreqCount ?? DBNull.Value);
		cmd.Parameters.AddWithValue("@fw", (object)maxOrderFreqWindowSec ?? DBNull.Value);
		cmd.Parameters.AddWithValue("@comm", (object)maxCommissionTotal ?? DBNull.Value);
		cmd.Parameters.AddWithValue("@rate", commissionRate);
		await cmd.ExecuteNonQueryAsync(ct);
	}

	// Inserts an ACCEPTED order with an explicit submitted_date computed from a
	// trusted DB-clock SQL expression (never user input, so no injection risk).
	private static async Task InsertAcceptedOrderAsync(
		SqlConnection conn, int portfolioId, int securityId, decimal qty, string submittedDateSql, CancellationToken ct = default)
	{
		await using var cmd = new SqlCommand(
			$"""
			INSERT INTO dbo.Orders (portfolio_id, security_id, side, qty, price, order_type, status, submitted_date)
			VALUES (@pf, @sec, 'B', @qty, 100.0, 'LIMIT', 'ACCEPTED', {submittedDateSql})
			""", conn);
		cmd.Parameters.AddWithValue("@pf", portfolioId);
		cmd.Parameters.AddWithValue("@sec", securityId);
		cmd.Parameters.Add(new SqlParameter("@qty", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = qty });
		await cmd.ExecuteNonQueryAsync(ct);
	}

	private static PreTradeRiskService NewGateService() => new(SqlLegacyConnection.Resolve());

	[TestMethod]
	public async Task GateAcceptsInLimitsLimitOrder()
	{
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 500m, maxOrderQty: 10000m);

		var result = await NewGateService().ValidateAsync(pfId, secId, Sides.Buy, 100m, 150m, OrderTypes.Limit);

		result.IsValid.AssertTrue();
		result.RejectionKind.AssertEqual(PreTradeRejectionKind.None);
	}

	[TestMethod]
	public async Task GateRejectsPriceCeilingBreachAsRiskLimit()
	{
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 500m);

		var result = await NewGateService().ValidateAsync(pfId, secId, Sides.Buy, 1m, 999m, OrderTypes.Limit);

		result.IsValid.AssertFalse();
		result.RejectionKind.AssertEqual(PreTradeRejectionKind.RiskLimit);
	}

	[TestMethod]
	public async Task GateRejectsLimitOrderWithNullPriceAsInvalidRequest()
	{
		// CR-3: a LIMIT order with no price is malformed - validated BEFORE any
		// limit is applied - and classified InvalidRequest (MJ-1), not RiskLimit.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 500m);

		var result = await NewGateService().ValidateAsync(pfId, secId, Sides.Buy, 10m, null, OrderTypes.Limit);

		result.IsValid.AssertFalse();
		result.RejectionKind.AssertEqual(PreTradeRejectionKind.InvalidRequest);
	}

	[TestMethod]
	public async Task GateRejectsNonPositivePriceAsInvalidRequest()
	{
		// CR-3: a zero or NEGATIVE price is malformed for ANY order type and must
		// never slip underneath the positive ceilings (as it did in the SQL proc).
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 500m);

		var svc = NewGateService();

		var negative = await svc.ValidateAsync(pfId, secId, Sides.Buy, 10m, -5m, OrderTypes.Limit);
		negative.IsValid.AssertFalse();
		negative.RejectionKind.AssertEqual(PreTradeRejectionKind.InvalidRequest);

		var zero = await svc.ValidateAsync(pfId, secId, Sides.Buy, 10m, 0m, OrderTypes.Limit);
		zero.IsValid.AssertFalse();
		zero.RejectionKind.AssertEqual(PreTradeRejectionKind.InvalidRequest);
	}

	[TestMethod]
	public async Task GateRejectsUnsupportedOrderTypeAsInvalidRequest()
	{
		// CR-3/MJ-1: only LIMIT/MARKET map to the schema; Conditional is malformed.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 500m);

		var result = await NewGateService().ValidateAsync(pfId, secId, Sides.Buy, 10m, 150m, OrderTypes.Conditional);

		result.IsValid.AssertFalse();
		result.RejectionKind.AssertEqual(PreTradeRejectionKind.InvalidRequest);
	}

	[TestMethod]
	public async Task GateRejectsInvalidInputsAsInvalidRequest()
	{
		// MJ-1 (service side): a malformed side or non-positive quantity is an
		// InvalidRequest returned as a result - the gate never throws and never
		// maps an unknown side to a valid one.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 500m);

		var svc = NewGateService();

		var badSide = await svc.ValidateAsync(pfId, secId, (Sides)999, 10m, 150m, OrderTypes.Limit);
		badSide.IsValid.AssertFalse();
		badSide.RejectionKind.AssertEqual(PreTradeRejectionKind.InvalidRequest);

		var badQty = await svc.ValidateAsync(pfId, secId, Sides.Buy, 0m, 150m, OrderTypes.Limit);
		badQty.IsValid.AssertFalse();
		badQty.RejectionKind.AssertEqual(PreTradeRejectionKind.InvalidRequest);
	}

	[TestMethod]
	public async Task GateAcceptsMarketOrderWithNullPrice()
	{
		// A MARKET order may omit the price; the price ceiling simply does not apply.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 500m);

		var result = await NewGateService().ValidateAsync(pfId, secId, Sides.Buy, 10m, null, OrderTypes.Market);

		result.IsValid.AssertTrue();
	}

	[TestMethod]
	public async Task GateFailsClosedOnNegativeFreqWindow()
	{
		// MJ-2: a negative frequency window is invalid config. Previously the
		// per-rule ">0" guard swallowed it (freq check skipped => fail OPEN). Now the
		// up-front validation rejects it (fail CLOSED).
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderFreqCount: 5, maxOrderFreqWindowSec: -60);

		var result = await NewGateService().ValidateAsync(pfId, secId, Sides.Buy, 1m, 150m, OrderTypes.Limit);

		result.IsValid.AssertFalse();
		result.RejectionKind.AssertEqual(PreTradeRejectionKind.RiskLimit);
	}

	[TestMethod]
	public async Task GateFailsClosedOnNegativeCommissionRate()
	{
		// MJ-2: a negative commission_rate previously flowed straight into the
		// estimate (never tripping => fail OPEN); now it fails CLOSED.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxCommissionTotal: 5000m, commissionRate: -0.5m);

		var result = await NewGateService().ValidateAsync(pfId, secId, Sides.Buy, 1m, 150m, OrderTypes.Limit);

		result.IsValid.AssertFalse();
		result.RejectionKind.AssertEqual(PreTradeRejectionKind.RiskLimit);
	}

	[TestMethod]
	public async Task GateFailsClosedOnInconsistentFreqConfig()
	{
		// MJ-2: a frequency count with no window (or vice versa) cannot be
		// evaluated; the gate fails closed rather than silently not enforcing it.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderFreqCount: 5, maxOrderFreqWindowSec: null);

		var result = await NewGateService().ValidateAsync(pfId, secId, Sides.Buy, 1m, 150m, OrderTypes.Limit);

		result.IsValid.AssertFalse();
		result.RejectionKind.AssertEqual(PreTradeRejectionKind.RiskLimit);
	}

	[TestMethod]
	public async Task GateSelectsMostSpecificRiskLimits()
	{
		// The gate must select the most-specific RiskLimits row: portfolio+security
		// wins over portfolio-only. A lenient portfolio-only ceiling must NOT mask a
		// strict portfolio+security ceiling.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, securityId: null, maxOrderPrice: 1000m); // portfolio-only, lenient
		await InsertRiskLimitAsync(conn, pfId, securityId: secId, maxOrderPrice: 100m); // portfolio+security, strict

		var result = await NewGateService().ValidateAsync(pfId, secId, Sides.Buy, 1m, 150m, OrderTypes.Limit);

		// 150 >= 100 (strict row) => rejected. If the lenient row were chosen, 150 < 1000 => accepted.
		result.IsValid.AssertFalse();
		result.RejectionKind.AssertEqual(PreTradeRejectionKind.RiskLimit);
	}

	[TestMethod]
	public async Task GateDailyVolumeUsesDbDayScope()
	{
		// MJ-7: the daily "today" boundary is derived from the DATABASE clock
		// (SYSUTCDATETIME) inside the transaction. An accepted order dated YESTERDAY
		// (per the DB clock) must be excluded; only TODAY's accepted volume counts.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxDailyVolume: 100m);

		// Yesterday's accepted 90 must NOT count toward today.
		await InsertAcceptedOrderAsync(conn, pfId, secId, 90m, "DATEADD(DAY, -1, SYSUTCDATETIME())");

		var svc = NewGateService();

		// today 0 + new 30 = 30 < 100 => accepted (proves yesterday's 90 excluded).
		var accepted = await svc.ValidateAsync(pfId, secId, Sides.Buy, 30m, 100m, OrderTypes.Limit);
		accepted.IsValid.AssertTrue();

		// Now add today's accepted 80.
		await InsertAcceptedOrderAsync(conn, pfId, secId, 80m, "SYSUTCDATETIME()");

		// today 80 + new 30 = 110 >= 100 => rejected (proves today's volume counts).
		var rejected = await svc.ValidateAsync(pfId, secId, Sides.Buy, 30m, 100m, OrderTypes.Limit);
		rejected.IsValid.AssertFalse();
		rejected.RejectionKind.AssertEqual(PreTradeRejectionKind.RiskLimit);
	}

	// =====================================================================
	// DB-integration tests for the gateway (SqlLegacyOrderGateway) against a
	// live StockSharpLegacy SQL Server. These exercise CR-4 idempotency
	// (submission + trade recording), the MJ-1 malformed-vs-risk persistence
	// contract, MJ-4 concurrency, and the end-to-end DB position recompute.
	// Each test uses a uniquely-named portfolio/security and a random 64-bit
	// idempotency key so it is isolated from seeded data, from other tests, and
	// from prior runs (the idempotency indexes are global, not per-portfolio).
	// =====================================================================

	private static SqlLegacyOrderGateway NewGateway() => new(SqlLegacyConnection.Resolve());

	// Random 64-bit idempotency key. Guid entropy makes cross-run collisions on the
	// global UX_Orders_external_transaction_id / UX_Trades_execution_id indexes negligible.
	private static long NextKey() => BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0);

	private static async Task<int> CountOrdersByTransactionAsync(SqlConnection conn, long externalTransactionId, CancellationToken ct = default)
	{
		await using var cmd = new SqlCommand(
			"SELECT COUNT(*) FROM dbo.Orders WHERE external_transaction_id = @txn", conn);
		cmd.Parameters.AddWithValue("@txn", externalTransactionId);
		return (int)await cmd.ExecuteScalarAsync(ct);
	}

	private static async Task<int> CountOrdersByPortfolioAsync(SqlConnection conn, int portfolioId, CancellationToken ct = default)
	{
		await using var cmd = new SqlCommand(
			"SELECT COUNT(*) FROM dbo.Orders WHERE portfolio_id = @pf", conn);
		cmd.Parameters.AddWithValue("@pf", portfolioId);
		return (int)await cmd.ExecuteScalarAsync(ct);
	}

	private static async Task<int> CountTradesByExecutionAsync(SqlConnection conn, long executionId, CancellationToken ct = default)
	{
		await using var cmd = new SqlCommand(
			"SELECT COUNT(*) FROM dbo.Trades WHERE execution_id = @exec", conn);
		cmd.Parameters.AddWithValue("@exec", executionId);
		return (int)await cmd.ExecuteScalarAsync(ct);
	}

	private static async Task<(string Status, string RejectReason)> ReadOrderStatusAsync(SqlConnection conn, long orderId, CancellationToken ct = default)
	{
		await using var cmd = new SqlCommand(
			"SELECT status, reject_reason FROM dbo.Orders WHERE order_id = @id", conn);
		cmd.Parameters.AddWithValue("@id", orderId);
		await using var reader = await cmd.ExecuteReaderAsync(ct);
		await reader.ReadAsync(ct);
		return (reader.GetString(0), await reader.IsDBNullAsync(1, ct) ? null : reader.GetString(1));
	}

	[TestMethod]
	public async Task GatewaySubmitIsIdempotentOnTransactionId()
	{
		// CR-4: a retried submit with the same external_transaction_id must return the
		// ORIGINAL order's outcome and never create a second order row.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 1_000_000m);

		var gateway = NewGateway();
		var txnId = NextKey();

		var first = await gateway.SubmitOrderAsync(pfId, secId, Sides.Buy, 100m, 150m, OrderTypes.Limit, externalTransactionId: txnId);
		var second = await gateway.SubmitOrderAsync(pfId, secId, Sides.Buy, 100m, 150m, OrderTypes.Limit, externalTransactionId: txnId);

		first.IsValid.AssertTrue();
		first.OrderId.AssertNotNull();
		second.IsValid.AssertTrue();
		second.OrderId.AssertEqual(first.OrderId); // same order returned, not a new one
		(await CountOrdersByTransactionAsync(conn, txnId)).AssertEqual(1);
	}

	[TestMethod]
	public async Task GatewayRecordTradeIsIdempotentOnExecutionId()
	{
		// CR-4: a retried RecordTrade with the same execution_id must insert exactly one
		// trade and apply the position effect exactly once (no double-count).
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 1_000_000m);

		var gateway = NewGateway();

		var submit = await gateway.SubmitOrderAsync(pfId, secId, Sides.Buy, 100m, 150m, OrderTypes.Limit);
		submit.OrderId.AssertNotNull();
		var orderId = submit.OrderId.Value;
		var execId = NextKey();

		await gateway.RecordTradeAsync(orderId, 100m, 150m, executionId: execId);
		await gateway.RecordTradeAsync(orderId, 100m, 150m, executionId: execId); // replay

		(await CountTradesByExecutionAsync(conn, execId)).AssertEqual(1);

		var position = await gateway.GetPositionAsync(pfId, secId);
		position.AssertNotNull();
		position.Quantity.AssertEqual(100m);   // applied once, not 200
		position.AveragePrice.AssertEqual(150m);
	}

	[TestMethod]
	public async Task GatewaySubmitInvalidRequestReturnsResultWithoutPersisting()
	{
		// MJ-1: a malformed request (bad side / non-positive qty) returns a rejection
		// RESULT (no throw) and does NOT persist an Orders row (OrderId is null).
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 1_000_000m);

		var gateway = NewGateway();

		var badSide = await gateway.SubmitOrderAsync(pfId, secId, (Sides)999, 100m, 150m, OrderTypes.Limit);
		badSide.IsValid.AssertFalse();
		badSide.OrderId.AssertNull();

		var badQty = await gateway.SubmitOrderAsync(pfId, secId, Sides.Buy, 0m, 150m, OrderTypes.Limit);
		badQty.IsValid.AssertFalse();
		badQty.OrderId.AssertNull();

		// Nothing was written for either malformed request.
		(await CountOrdersByPortfolioAsync(conn, pfId)).AssertEqual(0);
	}

	[TestMethod]
	public async Task GatewaySubmitRiskRejectedPersistsRejectedRow()
	{
		// MJ-1: a well-formed order that breaches a risk limit is RECORDED as REJECTED
		// (with a reason) for the audit trail - OrderId is populated, unlike a malformed
		// request.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 500m);

		var gateway = NewGateway();

		var result = await gateway.SubmitOrderAsync(pfId, secId, Sides.Buy, 10m, 999m, OrderTypes.Limit);

		result.IsValid.AssertFalse();
		result.OrderId.AssertNotNull();       // persisted for audit
		result.RejectReason.AssertNotNull();

		var (status, reason) = await ReadOrderStatusAsync(conn, result.OrderId.Value);
		status.AssertEqual("REJECTED");
		reason.AssertNotNull();
	}

	[TestMethod]
	public async Task GatewaySubmitConcurrentSameTransactionIdPersistsExactlyOne()
	{
		// MJ-4 + CR-4: several submits racing on the SAME transaction id must all
		// complete (deadlocks retried, unique-violation re-reads the winner), return the
		// same order id, and leave exactly ONE persisted order.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 1_000_000m);

		var gateway = NewGateway();
		var txnId = NextKey();

		var tasks = Enumerable.Range(0, 4)
			.Select(_ => gateway.SubmitOrderAsync(pfId, secId, Sides.Buy, 100m, 150m, OrderTypes.Limit, externalTransactionId: txnId))
			.ToArray();

		var results = await Task.WhenAll(tasks);

		foreach (var r in results)
		{
			r.IsValid.AssertTrue();
			r.OrderId.AssertNotNull();
			r.OrderId.AssertEqual(results[0].OrderId); // all observed the same winning order
		}

		(await CountOrdersByTransactionAsync(conn, txnId)).AssertEqual(1);
	}

	[TestMethod]
	public async Task GatewayRecordTradeRecomputesPositionEndToEnd()
	{
		// End-to-end DB recompute: submitting an accepted order and recording its fill
		// updates dbo.Positions (quantity/average price) via PositionRecalculationService.
		// Also covers the no-execution-id path (the demo's behaviour).
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 1_000_000m);

		var gateway = NewGateway();

		(await gateway.GetPositionAsync(pfId, secId)).AssertNull(); // flat before any trade

		var submit = await gateway.SubmitOrderAsync(pfId, secId, Sides.Buy, 100m, 150m, OrderTypes.Limit);
		submit.IsValid.AssertTrue();

		await gateway.RecordTradeAsync(submit.OrderId.Value, 100m, 150m);

		var position = await gateway.GetPositionAsync(pfId, secId);
		position.AssertNotNull();
		position.Quantity.AssertEqual(100m);
		position.AveragePrice.AssertEqual(150m);
		position.RealizedPnL.AssertEqual(0m);
	}

	[TestMethod]
	public async Task GatewayRecordTradeShortThenPartialCoverRealizesPnlEndToEnd()
	{
		// Fuller DB recompute path: open a SHORT then partially cover it, realizing P&L.
		// SELL 100 @ 150  -> qty -100, avg 150
		// BUY   60 @ 140  -> covers 60 of the short at 140: realized = (150-140)*60 = 600,
		//                    remaining qty -40, avg unchanged 150.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxOrderPrice: 1_000_000m);

		var gateway = NewGateway();

		var sell = await gateway.SubmitOrderAsync(pfId, secId, Sides.Sell, 100m, 150m, OrderTypes.Limit);
		sell.IsValid.AssertTrue();
		await gateway.RecordTradeAsync(sell.OrderId.Value, 100m, 150m, executionId: NextKey());

		var shortPos = await gateway.GetPositionAsync(pfId, secId);
		shortPos.Quantity.AssertEqual(-100m);
		shortPos.AveragePrice.AssertEqual(150m);

		var buy = await gateway.SubmitOrderAsync(pfId, secId, Sides.Buy, 60m, 140m, OrderTypes.Limit);
		buy.IsValid.AssertTrue();
		await gateway.RecordTradeAsync(buy.OrderId.Value, 60m, 140m, executionId: NextKey());

		var covered = await gateway.GetPositionAsync(pfId, secId);
		covered.Quantity.AssertEqual(-40m);
		covered.AveragePrice.AssertEqual(150m);
		covered.RealizedPnL.AssertEqual(600m);
	}

	// ---------------------------------------------------------------------------
	// MJ-5 - remaining coverage: retained fixed-window characterization,
	// commission estimate-vs-actual (kept-separate-by-design), prior realized-P&L
	// accumulation, daily-volume status scope, and gate position-size projection.
	// ---------------------------------------------------------------------------

	/// <summary>
	/// Frozen, executable reference of the RETIRED fixed, non-overlapping window
	/// order-frequency algorithm (the pre-consolidation <see cref="RiskOrderFreqRule"/>
	/// body: the <c>_endTime</c>/<c>_current</c> state machine). Retained per the
	/// characterization-first discipline so the behaviour the canonical rolling
	/// window replaced stays documented and executable. This is NOT production code -
	/// it is used only by
	/// <see cref="OrderFreqRetiredFixedWindowCharacterizationAdmitsBoundaryBurst"/> and
	/// reproduces the original Algo/Risk/RiskOrderFreqRule.cs ProcessMessage exactly as
	/// it stood before the rolling-window reconciliation (stricter-wins, AAP 0.6.3).
	/// </summary>
	private sealed class RetiredFixedWindowFreqReference
	{
		private readonly int _count;
		private readonly TimeSpan _interval;
		private DateTime? _endTime;
		private int _current;

		public RetiredFixedWindowFreqReference(int count, TimeSpan interval)
		{
			_count = count;
			_interval = interval;
		}

		/// <summary>
		/// Returns <see langword="true"/> when the incoming order at <paramref name="localTime"/>
		/// trips the limit, mirroring the retired fixed-window logic verbatim: open a
		/// window on the first order, increment while inside it, trip (and reset) when the
		/// count meets the limit, and open a fresh window once the current one expires.
		/// </summary>
		public bool Process(DateTime localTime)
		{
			if (localTime == default)
				return false;

			if (_endTime == null)
			{
				_endTime = localTime + _interval;
				_current = 1;
				return false;
			}

			if (localTime < _endTime)
			{
				_current++;

				if (_current >= _count)
				{
					_endTime = null;
					return true;
				}
			}
			else
			{
				_endTime = localTime + _interval;
				_current = 1;
			}

			return false;
		}
	}

	[TestMethod]
	public void OrderFreqRetiredFixedWindowCharacterizationAdmitsBoundaryBurst()
	{
		// CHARACTERIZATION (retained executable reference, MJ-5): the RETIRED fixed,
		// non-overlapping window admitted a boundary burst that the canonical rolling
		// window now rejects. Same config (Count=3, Interval=10s) and the same order
		// arrival sequence {0s, 1s, 5s, 6s} produce DIFFERENT answers for the 4th order:
		// the retired window trips at 5s then RESETS, so it ADMITS the order at 6s; the
		// rolling window still counts {0,1,5,6}=4 within the 10s interval and REJECTS it.
		// This is exactly the strictness gap the consolidation closed (stricter-wins,
		// AAP 0.6.3) - documented here so the retired behaviour is not silently lost.
		var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		var offsets = new[] { 0, 1, 5, 6 };

		// --- retired fixed-window reference: admits the 4th order (the dodge) ---
		var retired = new RetiredFixedWindowFreqReference(3, TimeSpan.FromSeconds(10));
		var retiredResults = new bool[offsets.Length];
		for (var i = 0; i < offsets.Length; i++)
			retiredResults[i] = retired.Process(start.AddSeconds(offsets[i]));

		retiredResults[0].AssertFalse();
		retiredResults[1].AssertFalse();
		retiredResults[2].AssertTrue();   // trips at 5s, then resets the window
		retiredResults[3].AssertFalse();  // 6s ADMITTED - the boundary-burst dodge

		// --- canonical rolling window: rejects the 4th order (strictly stricter) ---
		var canonical = new RiskOrderFreqRule
		{
			Count = 3,
			Interval = TimeSpan.FromSeconds(10),
			Action = RiskActions.CancelOrders
		};
		var canonicalResults = new bool[offsets.Length];
		for (var i = 0; i < offsets.Length; i++)
		{
			var msg = new OrderRegisterMessage
			{
				SecurityId = Helper.CreateSecurityId(),
				LocalTime = start.AddSeconds(offsets[i])
			};
			canonicalResults[i] = canonical.ProcessMessage(msg);
		}

		canonicalResults[0].AssertFalse();
		canonicalResults[1].AssertFalse();
		canonicalResults[2].AssertTrue();
		canonicalResults[3].AssertTrue();  // 6s REJECTED - rolling is strictly stricter

		// Prove the strictness gap at the boundary explicitly: the retired window
		// admitted precisely the order the canonical window rejects.
		retiredResults[3].AssertFalse();
		canonicalResults[3].AssertTrue();
	}

	[TestMethod]
	public void RecalculateAccumulatesPriorRealizedPnl()
	{
		// Prior realized P&L must ACCUMULATE, not be overwritten. Start long 100 @ 150
		// with 250 already realized from earlier activity; sell 40 @ 170 realizes an
		// additional 40*(170-150)=800 on the closed portion, so realized becomes
		// 250 + 800 = 1050 (60 remain at the original average price).
		var result = PositionRecalculationService.Recalculate(
			existingQty: 100m, existingAvgPrice: 150m, existingRealizedPnl: 250m,
			side: Sides.Sell, tradeQty: 40m, tradePrice: 170m);

		result.Quantity.AssertEqual(60m);
		result.AveragePrice.AssertEqual(150m);
		result.RealizedPnl.AssertEqual(1050m);
	}

	[TestMethod]
	public async Task GateCommissionEstimateRejectsWhileActualControlAccepts()
	{
		// KEEP-SEPARATE-BY-DESIGN (AAP 0.6.4, MJ-5): the pre-trade gate uses a PRE-FILL
		// ESTIMATE (commission_rate * notional) while the circuit-breaker path uses the
		// ACTUAL commission read off the message stream after the fill. They consume the
		// same max_commission_total limit but compute different figures, so the SAME
		// order/limit can decide differently. Here the rate-based estimate crosses the
		// limit (gate REJECTS) while the actual commission that ends up on the fill is
		// well under it (the actual-commission control ACCEPTS) - proving the two are
		// distinct implementations that are intentionally NOT merged.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);

		// limit 10; rate 1% => estimate for qty 100 @ 50 = 100*50*0.01 = 50 >= 10.
		await InsertRiskLimitAsync(conn, pfId, maxCommissionTotal: 10m, commissionRate: 0.01m);

		var gate = NewGateService();
		var estimateRejected = await gate.ValidateAsync(pfId, secId, Sides.Buy, 100m, 50m, OrderTypes.Limit);
		estimateRejected.IsValid.AssertFalse();
		estimateRejected.RejectionKind.AssertEqual(PreTradeRejectionKind.RiskLimit);

		// The ACTUAL post-fill control on the SAME limit (10) sees the real commission
		// (5) and does NOT trip - a different answer for the same order/limit, because
		// the actual figure is not the rate-based estimate.
		var actualRule = new RiskCommissionRule
		{
			Commission = 10m,
			Action = RiskActions.StopTrading
		};
		var posMsg = new PositionChangeMessage
		{
			SecurityId = SecurityId.Money,
			ServerTime = DateTime.UtcNow,
			PortfolioName = _pfName
		};
		posMsg.Add(PositionChangeTypes.Commission, 5m);
		actualRule.ProcessMessage(posMsg).AssertFalse();
	}

	[TestMethod]
	public async Task GateDailyVolumeExcludesNonAcceptedStatuses()
	{
		// The daily-volume aggregate counts only orders whose status is in
		// (ACCEPTED, FILLED, PARTFILLED); a PENDING/REJECTED/CANCELLED order does NOT
		// consume today's budget. Insert a large REJECTED order today and prove it is
		// excluded, then a same-size ACCEPTED order and prove that one DOES count.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxDailyVolume: 100m);

		// A REJECTED order today for 90 must NOT count toward the daily budget.
		await InsertOrderWithStatusAsync(conn, pfId, secId, 90m, "REJECTED", "SYSUTCDATETIME()");

		var svc = NewGateService();

		// counted today = 0 (the rejected 90 is excluded) + new 80 = 80 < 100 => accepted.
		var accepted = await svc.ValidateAsync(pfId, secId, Sides.Buy, 80m, 100m, OrderTypes.Limit);
		accepted.IsValid.AssertTrue();

		// Control: an ACCEPTED order today for 90 DOES count, so +80 => 170 >= 100 rejected.
		await InsertAcceptedOrderAsync(conn, pfId, secId, 90m, "SYSUTCDATETIME()");
		var rejected = await svc.ValidateAsync(pfId, secId, Sides.Buy, 80m, 100m, OrderTypes.Limit);
		rejected.IsValid.AssertFalse();
		rejected.RejectionKind.AssertEqual(PreTradeRejectionKind.RiskLimit);
	}

	[TestMethod]
	public async Task GatePositionSizeUsesPostFillProjection()
	{
		// The gate enforces max_position_size against the POST-FILL PROJECTION
		// (current signed position + signed order qty), not the current position -
		// preserving the pre-trade control (AAP 0.6.4). With a current long of 60 and a
		// limit of 100, a further BUY 50 projects to 110 >= 100 and must be rejected even
		// though the current position (60) is within the limit; a BUY 30 projects to 90
		// and is accepted.
		await using var conn = await OpenLegacyOrInconclusiveAsync();
		var pfId = await InsertPortfolioAsync(conn);
		var secId = await InsertSecurityAsync(conn);
		await InsertRiskLimitAsync(conn, pfId, maxPositionSize: 100m);

		// Seed a current long position of 60 (within the limit).
		await InsertPositionAsync(conn, pfId, secId, 60m, 150m);

		var svc = NewGateService();

		// BUY 50 => projected 60 + 50 = 110 >= 100 => rejected on the projection.
		var rejected = await svc.ValidateAsync(pfId, secId, Sides.Buy, 50m, 150m, OrderTypes.Limit);
		rejected.IsValid.AssertFalse();
		rejected.RejectionKind.AssertEqual(PreTradeRejectionKind.RiskLimit);

		// BUY 30 => projected 60 + 30 = 90 < 100 => accepted (current 60 within limit).
		var accepted = await svc.ValidateAsync(pfId, secId, Sides.Buy, 30m, 150m, OrderTypes.Limit);
		accepted.IsValid.AssertTrue();
	}

	/// <summary>
	/// Inserts an order with an explicit <paramref name="status"/> and a trusted
	/// (DB-clock) <paramref name="submittedDateSql"/> expression, for exercising the
	/// daily-volume status/date scope.
	/// </summary>
	private static async Task InsertOrderWithStatusAsync(
		SqlConnection conn, int portfolioId, int securityId, decimal qty, string status, string submittedDateSql, CancellationToken ct = default)
	{
		await using var cmd = new SqlCommand(
			$"""
			INSERT INTO dbo.Orders (portfolio_id, security_id, side, qty, price, order_type, status, reject_reason, submitted_date)
			VALUES (@pf, @sec, 'B', @qty, 100.0, 'LIMIT', @status, NULL, {submittedDateSql})
			""", conn);
		cmd.Parameters.AddWithValue("@pf", portfolioId);
		cmd.Parameters.AddWithValue("@sec", securityId);
		cmd.Parameters.Add(new SqlParameter("@qty", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = qty });
		cmd.Parameters.AddWithValue("@status", status);
		await cmd.ExecuteNonQueryAsync(ct);
	}

	/// <summary>
	/// Seeds a current <c>dbo.Positions</c> row (signed <paramref name="qty"/>,
	/// <paramref name="avgPrice"/>) so the gate's position-size projection has a live
	/// starting position to project from.
	/// </summary>
	private static async Task InsertPositionAsync(
		SqlConnection conn, int portfolioId, int securityId, decimal qty, decimal avgPrice, CancellationToken ct = default)
	{
		await using var cmd = new SqlCommand(
			"""
			INSERT INTO dbo.Positions (portfolio_id, security_id, qty, avg_price)
			VALUES (@pf, @sec, @qty, @avg)
			""", conn);
		cmd.Parameters.AddWithValue("@pf", portfolioId);
		cmd.Parameters.AddWithValue("@sec", securityId);
		cmd.Parameters.Add(new SqlParameter("@qty", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = qty });
		cmd.Parameters.Add(new SqlParameter("@avg", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = avgPrice });
		await cmd.ExecuteNonQueryAsync(ct);
	}
}
