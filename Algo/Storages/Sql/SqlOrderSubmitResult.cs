namespace StockSharp.Algo.Storages.Sql;

using StockSharp.Algo.Risk;

/// <summary>
/// Outcome of a <see cref="SqlLegacyOrderGateway.SubmitOrderAsync"/> call: the result of the
/// <see cref="PreTradeRiskService"/> pre-trade evaluation together with the plain dbo.Orders
/// INSERT the gateway performs.
/// </summary>
public class SqlOrderSubmitResult
{
	/// <summary>SQL-side order id (dbo.Orders.order_id) - not the same value as <see cref="Order.TransactionId"/>.</summary>
	public long OrderId { get; init; }

	/// <summary>Whether <see cref="PreTradeRiskService"/> accepted the order.</summary>
	public bool IsValid { get; init; }

	/// <summary>Set when <see cref="IsValid"/> is <see langword="false"/>; otherwise <see langword="null"/>.</summary>
	public string RejectReason { get; init; }
}
