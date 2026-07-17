namespace StockSharp.Algo.Storages.Sql;

/// <summary>
/// Result of a <see cref="SqlLegacyOrderGateway.SubmitOrderAsync"/> call. The
/// validation outcome (<see cref="IsValid"/>/<see cref="RejectReason"/>) comes
/// from the C# <see cref="StockSharp.Algo.Risk.PreTradeRiskService"/> pre-trade
/// gate; <see cref="OrderId"/> is the identity of the gateway's insert into
/// dbo.Orders, or <see langword="null"/> when no row was persisted.
/// </summary>
public class SqlOrderSubmitResult
{
	/// <summary>
	/// SQL-side order id (dbo.Orders.order_id) - not the same value as
	/// <see cref="Order.TransactionId"/>. Populated when a row was persisted: an
	/// accepted order OR a risk-rejected order (rejections are recorded for the audit
	/// trail). <see langword="null"/> when the request was malformed (e.g. invalid
	/// side, non-positive quantity, or a bad/absent price) and was therefore rejected
	/// as an input error WITHOUT persisting an Orders row - see
	/// <see cref="StockSharp.Algo.Risk.PreTradeRejectionKind.InvalidRequest"/>.
	/// </summary>
	public long? OrderId { get; init; }

	/// <summary>Whether the C# <see cref="StockSharp.Algo.Risk.PreTradeRiskService"/> pre-trade gate accepted the order.</summary>
	public bool IsValid { get; init; }

	/// <summary>Set when <see cref="IsValid"/> is <see langword="false"/>; otherwise <see langword="null"/>.</summary>
	public string RejectReason { get; init; }
}
