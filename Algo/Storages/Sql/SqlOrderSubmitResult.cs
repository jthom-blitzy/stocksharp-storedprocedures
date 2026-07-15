namespace StockSharp.Algo.Storages.Sql;

/// <summary>
/// Result of a <see cref="SqlLegacyOrderGateway.SubmitOrderAsync"/> call (maps to
/// dbo.usp_SubmitOrder's output parameters).
/// </summary>
public class SqlOrderSubmitResult
{
	/// <summary>SQL-side order id (dbo.Orders.order_id) - not the same value as <see cref="Order.TransactionId"/>.</summary>
	public long OrderId { get; init; }

	/// <summary>Whether usp_ValidatePreTradeRisk accepted the order.</summary>
	public bool IsValid { get; init; }

	/// <summary>Set when <see cref="IsValid"/> is <see langword="false"/>; otherwise <see langword="null"/>.</summary>
	public string RejectReason { get; init; }
}
