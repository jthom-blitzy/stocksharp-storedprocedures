namespace StockSharp.Algo.Risk;

/// <summary>
/// Risk-rule, tracking total commission for order registrations.
/// </summary>
/// <remarks>
/// This rule sums the actual commission reported on order-related <see cref="ExecutionMessage"/>s
/// (those matched by <c>ExecutionMessage.HasOrderInfo()</c>) as fills stream in, so the running total
/// is only known post-fill. It is deliberately different from the per-order pre-trade gate
/// <see cref="PreTradeRiskService"/>, which runs before any fill exists and can therefore only estimate
/// the commission. Both enforcement patterns consume the single canonical threshold
/// <see cref="RiskLimitSet.MaxCommissionTotal"/>, yet because one measures a realized total while the
/// other projects a pre-fill estimate the two are different-by-design and are intentionally not merged
/// under DRY, per AAP §0.6.2.
/// </remarks>
[Display(
	ResourceType = typeof(LocalizedStrings),
	Name = LocalizedStrings.OrderCommissionKey,
	Description = LocalizedStrings.RiskOrderCommissionKey,
	GroupName = LocalizedStrings.OrdersKey)]
public class RiskOrderCommissionRule : RiskTransactionCommissionRule
{
	/// <inheritdoc />
	protected override bool IsMatch(ExecutionMessage execMsg)
		=> execMsg.HasOrderInfo();
}
