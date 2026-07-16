namespace StockSharp.Algo.Storages.Sql;

/// <summary>
/// A row from the PostgreSQL <c>Positions</c> table. Positions are recomputed in C# by
/// <c>PositionRecalculationService</c> (Algo/Risk, average-cost); the legacy recalculation
/// stored procedure was retired. <see cref="UnrealizedPnL"/> is not maintained in real time
/// (it needs a live market price); see the comment on the Positions table in
/// Database/001_Schema.sql.
/// </summary>
public class SqlPosition
{
	/// <summary>Portfolios.portfolio_id.</summary>
	public int PortfolioId { get; init; }

	/// <summary>Securities.security_id.</summary>
	public int SecurityId { get; init; }

	/// <summary>Signed quantity: positive is long, negative is short.</summary>
	public decimal Quantity { get; init; }

	/// <summary>Volume-weighted average price of the open position.</summary>
	public decimal AveragePrice { get; init; }

	/// <summary>Realized P&amp;L accumulated from closed portions of trades.</summary>
	public decimal RealizedPnL { get; init; }

	/// <summary>Stale outside of the EOD mark-to-market batch - do not treat as live. <c>PositionRecalculationService</c> intentionally leaves it untouched because it has no live market price.</summary>
	public decimal UnrealizedPnL { get; init; }

	/// <summary>Last time this row was written by <c>PositionRecalculationService</c>.</summary>
	public DateTime UpdatedDate { get; init; }
}
