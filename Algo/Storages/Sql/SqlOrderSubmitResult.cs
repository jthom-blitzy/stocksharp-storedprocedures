namespace StockSharp.Algo.Storages.Sql;

/// <summary>
/// Result of a <see cref="SqlLegacyOrderGateway.SubmitOrderAsync"/> call. The
/// validation outcome (<see cref="IsValid"/>/<see cref="RejectReason"/>) comes
/// from the C# <see cref="StockSharp.Algo.Risk.PreTradeRiskService"/> pre-trade
/// gate; <see cref="OrderId"/> is the identity returned by the gateway's direct
/// insert into dbo.Orders.
/// </summary>
public class SqlOrderSubmitResult
{
	/// <summary>SQL-side order id (dbo.Orders.order_id) - not the same value as <see cref="Order.TransactionId"/>.</summary>
	public long OrderId { get; init; }

	/// <summary>Whether the C# <see cref="StockSharp.Algo.Risk.PreTradeRiskService"/> pre-trade gate accepted the order.</summary>
	public bool IsValid { get; init; }

	/// <summary>Set when <see cref="IsValid"/> is <see langword="false"/>; otherwise <see langword="null"/>.</summary>
	public string RejectReason { get; init; }
}
