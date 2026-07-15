namespace StockSharp.Algo.Storages.Sql;

/// <summary>
/// A row from dbo.Positions. unrealized_pnl is not maintained in real time -
/// see the comment on the Positions table in Database/001_Schema.sql.
/// </summary>
public class SqlPosition
{
	/// <summary>dbo.Portfolios.portfolio_id.</summary>
	public int PortfolioId { get; init; }

	/// <summary>dbo.Securities.security_id.</summary>
	public int SecurityId { get; init; }

	/// <summary>Signed quantity: positive is long, negative is short.</summary>
	public decimal Quantity { get; init; }

	/// <summary>Volume-weighted average price of the open position.</summary>
	public decimal AveragePrice { get; init; }

	/// <summary>Realized P&amp;L accumulated from closed portions of trades.</summary>
	public decimal RealizedPnL { get; init; }

	/// <summary>Stale outside of the EOD mark-to-market batch - do not treat as live.</summary>
	public decimal UnrealizedPnL { get; init; }

	/// <summary>Last time this row was written by usp_RecalculatePositionOnTrade.</summary>
	public DateTime UpdatedDate { get; init; }
}
