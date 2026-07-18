namespace StockSharp.Algo.Storages.Sql;

/// <summary>
/// Result of a <see cref="SqlLegacyOrderGateway.SubmitOrderAsync"/> call. The gateway relays the
/// { IsValid, RejectReason } decision from the canonical <c>PreTradeRiskService</c> (Algo/Risk),
/// and fills <see cref="OrderId"/> from the PostgreSQL <c>INSERT ... RETURNING order_id</c> - it no
/// longer maps the output parameters of a retired SQL stored procedure.
/// </summary>
public class SqlOrderSubmitResult
{
	/// <summary>PostgreSQL-side order id (Orders.order_id, from INSERT ... RETURNING) - not the same value as <see cref="Order.TransactionId"/>.</summary>
	public long OrderId { get; init; }

	/// <summary>Whether the canonical <c>PreTradeRiskService</c> accepted the order.</summary>
	public bool IsValid { get; init; }

	/// <summary>Reason produced by <c>PreTradeRiskService</c>, set when <see cref="IsValid"/> is <see langword="false"/>; otherwise <see langword="null"/>.</summary>
	public string RejectReason { get; init; }
}
