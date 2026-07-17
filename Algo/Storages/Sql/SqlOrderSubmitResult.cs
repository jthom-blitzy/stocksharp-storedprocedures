namespace StockSharp.Algo.Storages.Sql;

/// <summary>
/// Result of a <see cref="SqlLegacyOrderGateway.SubmitOrderAsync"/> call. The
/// validation outcome (<see cref="IsValid"/>/<see cref="RejectReason"/>) is decided
/// by the C# <see cref="StockSharp.Algo.Risk.PreTradeRiskService"/> pre-trade gate;
/// <see cref="OrderId"/> is the identity of the row the gateway inserts into
/// dbo.Orders.
/// </summary>
public class SqlOrderSubmitResult
{
	/// <summary>
	/// SQL-side order id (dbo.Orders.order_id) - not the same value as
	/// <see cref="Order.TransactionId"/>. Always the identity of a persisted row: an
	/// accepted order OR a risk-rejected order (rejections are recorded for the audit
	/// trail). A malformed request (invalid side, non-positive quantity, or a
	/// bad/absent price) is thrown back to the caller as an input error rather than
	/// returned as a result, so a returned result always carries a real order id.
	/// </summary>
	public long OrderId { get; init; }

	/// <summary>Whether the C# <see cref="StockSharp.Algo.Risk.PreTradeRiskService"/> pre-trade gate accepted the order.</summary>
	public bool IsValid { get; init; }

	/// <summary>Set when <see cref="IsValid"/> is <see langword="false"/>; otherwise <see langword="null"/>.</summary>
	public string RejectReason { get; init; }
}
