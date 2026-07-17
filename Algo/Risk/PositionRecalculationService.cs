namespace StockSharp.Algo.Risk;

using System.Data;

using Microsoft.Data.SqlClient;

/// <summary>
/// The recomputed position after applying a single trade: new signed quantity, new average price,
/// and cumulative realized profit-and-loss.
/// </summary>
public class PositionRecalcResult
{
	/// <summary>The new signed position quantity (positive long, negative short).</summary>
	public decimal Quantity { get; init; }

	/// <summary>The new average entry price of the open position (0 when flat).</summary>
	public decimal AveragePrice { get; init; }

	/// <summary>The cumulative realized profit-and-loss after this trade.</summary>
	public decimal RealizedPnl { get; init; }
}

/// <summary>
/// Recomputes portfolio positions on each recorded trade using average-cost accounting and
/// realized-P&amp;L tracking, ported from dbo.usp_RecalculatePositionOnTrade. This is the SINGLE
/// entry point for position recalculation, invoked exactly once per trade by the gateway; there is
/// no competing database trigger, which eliminates the historical double-count hazard. The pure math
/// (<see cref="Recalculate"/>) is separated from the database read/write
/// (<see cref="ApplyTradeAsync(SqlConnection, SqlTransaction, long, CancellationToken)"/>)
/// so it is unit-testable without a database.
/// </summary>
public class PositionRecalculationService
{
	private readonly string _connectionString;

	/// <summary>
	/// Initializes a new instance of <see cref="PositionRecalculationService"/>.
	/// </summary>
	/// <param name="connectionString">The StockSharpLegacy SQL Server connection string.</param>
	public PositionRecalculationService(string connectionString)
	{
		if (connectionString.IsEmpty())
			throw new ArgumentNullException(nameof(connectionString));

		_connectionString = connectionString;
	}

	private SqlConnection CreateConnection() => new(_connectionString);

	/// <summary>
	/// Computes the new position state produced by applying one trade to an existing position, using the
	/// same average-cost and realized-P&amp;L rules as dbo.usp_RecalculatePositionOnTrade. Pure and
	/// side-effect-free; results are rounded to 4 decimal places (away-from-zero) to mirror the DECIMAL(18,4)
	/// storage columns.
	/// </summary>
	/// <param name="existingQty">The current signed position quantity (0 when flat / no row).</param>
	/// <param name="existingAvgPrice">The current average price (0 when flat / no row).</param>
	/// <param name="existingRealizedPnl">The current cumulative realized P&amp;L (0 when no row).</param>
	/// <param name="side">The trade side (must be <see cref="Sides.Buy"/> or <see cref="Sides.Sell"/>).</param>
	/// <param name="tradeQty">The trade quantity (must be &gt; 0).</param>
	/// <param name="tradePrice">The trade price (must be &gt; 0).</param>
	/// <returns>The recomputed position.</returns>
	/// <exception cref="ArgumentOutOfRangeException">
	/// <paramref name="side"/> is not Buy/Sell, or <paramref name="tradeQty"/>/<paramref name="tradePrice"/> is non-positive.
	/// </exception>
	/// <exception cref="OverflowException">Any input or computed output falls outside the DECIMAL(18,4) range.</exception>
	public static PositionRecalcResult Recalculate(decimal existingQty, decimal existingAvgPrice, decimal existingRealizedPnl, Sides side, decimal tradeQty, decimal tradePrice)
	{
		// M07 - validate the full domain contract before any arithmetic (a non-Buy side must NOT be
		// silently treated as Sell, and zero/negative qty or price must fail rather than divide by zero
		// or corrupt the average-cost math).
		if (side is not (Sides.Buy or Sides.Sell))
			throw new ArgumentOutOfRangeException(nameof(side), side, "Trade side must be Buy or Sell.");

		if (tradeQty <= 0)
			throw new ArgumentOutOfRangeException(nameof(tradeQty), tradeQty, "Trade quantity must be positive.");

		if (tradePrice <= 0)
			throw new ArgumentOutOfRangeException(nameof(tradePrice), tradePrice, "Trade price must be positive.");

		// M08 - normalize inputs to the persisted DECIMAL(18,4) scale/range so the C# math operates on
		// exactly the values SQL Server stores, and out-of-range magnitudes fail deterministically.
		existingQty = NormalizeMoney(existingQty, nameof(existingQty));
		existingAvgPrice = NormalizeMoney(existingAvgPrice, nameof(existingAvgPrice));
		existingRealizedPnl = NormalizeMoney(existingRealizedPnl, nameof(existingRealizedPnl));
		tradeQty = NormalizeMoney(tradeQty, nameof(tradeQty));
		tradePrice = NormalizeMoney(tradePrice, nameof(tradePrice));

		// Signed trade quantity: buys add, sells subtract (mirrors the SQL CASE on @side).
		var tradeSignedQty = side == Sides.Buy ? tradeQty : -tradeQty;

		var newRealizedPnl = existingRealizedPnl;
		decimal newQty;
		decimal newAvgPrice;

		if (existingQty == 0 || Math.Sign(existingQty) == Math.Sign(tradeSignedQty))
		{
			// Adding to (or opening) a position on the same side: weighted-average the price in.
			// newQty can never be 0 on this branch (same sign, non-zero addend), so the divide is safe.
			newQty = existingQty + tradeSignedQty;
			newAvgPrice = (Math.Abs(existingQty) * existingAvgPrice + tradeQty * tradePrice) / Math.Abs(newQty);
		}
		else
		{
			// Trade works against the existing position: realize P&L on the closed portion.
			var closingQty = Math.Min(Math.Abs(existingQty), tradeQty);
			var remainingQty = tradeQty - closingQty;

			newRealizedPnl = existingRealizedPnl + closingQty * (tradePrice - existingAvgPrice) * Math.Sign(existingQty);

			if (remainingQty == 0)
			{
				// Partial or exact close (no flip); avg price is only meaningful while the position stays open.
				newQty = existingQty + tradeSignedQty;
				newAvgPrice = newQty == 0 ? 0 : existingAvgPrice;
			}
			else
			{
				// Fully closed and flipped: what is left of the trade opens a new position on the other side.
				newQty = Math.Sign(tradeSignedQty) * remainingQty;
				newAvgPrice = tradePrice;
			}
		}

		// NormalizeMoney rounds to 4 dp away-from-zero (so sequential recomputations stay bit-identical to
		// the SQL, which reads each subsequent trade off the 4dp-stored avg_price) AND enforces the
		// DECIMAL(18,4) range, so a computed value that could not be stored fails deterministically.
		return new PositionRecalcResult
		{
			Quantity = NormalizeMoney(newQty, nameof(newQty)),
			AveragePrice = NormalizeMoney(newAvgPrice, nameof(newAvgPrice)),
			RealizedPnl = NormalizeMoney(newRealizedPnl, nameof(newRealizedPnl)),
		};
	}

	// Largest magnitude representable by the persisted DECIMAL(18,4) columns (18 digits, 4 after the point).
	private const decimal _moneyMax = 99_999_999_999_999.9999m;

	/// <summary>
	/// Normalizes a decimal to the persisted DECIMAL(18,4) contract: rounds to 4 decimal places
	/// away-from-zero and rejects any value outside the column's representable range with a
	/// deterministic <see cref="OverflowException"/>.
	/// </summary>
	private static decimal NormalizeMoney(decimal value, string name)
	{
		var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);

		if (Math.Abs(rounded) > _moneyMax)
			throw new OverflowException($"PositionRecalculationService: {name} value {value} is outside the DECIMAL(18,4) range.");

		return rounded;
	}

	private static SqlParameter BigIntParam(string name, long value)
		=> new(name, SqlDbType.BigInt) { Value = value };

	private static SqlParameter IntParam(string name, int value)
		=> new(name, SqlDbType.Int) { Value = value };

	private static SqlParameter DecimalParam(string name, decimal value)
		=> new(name, SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = value };

	/// <summary>
	/// Recomputes and persists the position for the portfolio/security that owns <paramref name="orderId"/>,
	/// enrolling in the caller's (gateway-owned) <paramref name="transaction"/> so the trade insertion and the
	/// position mutation commit or roll back together.
	/// <para>
	/// The position is recomputed <b>deterministically from the entire persisted trade set</b> of the
	/// portfolio/security (folding <see cref="Recalculate"/> over the trades in executed order) rather than
	/// incrementally mutating the stored row. This makes the operation <b>idempotent</b>: re-running it for the
	/// same persisted trades yields the same position, so no residual or repeated path can double-apply a trade
	/// (the historical trigger/standalone double-count hazard, LEGACY_LAYER.md:L74-L89, is structurally
	/// eliminated). Because the recompute reads the just-inserted trade through the shared transaction, the
	/// caller must INSERT the trade into <c>dbo.Trades</c> before calling this method within the same transaction.
	/// </para>
	/// <para>
	/// Concurrency: the position key is locked with <c>UPDLOCK, HOLDLOCK</c> for the duration of the caller's
	/// transaction, serializing concurrent recalculations of the same portfolio/security and blocking a racing
	/// first INSERT of the unique (portfolio_id, security_id) key (CWE-362).
	/// </para>
	/// </summary>
	/// <param name="connection">The gateway-owned open connection enrolled in <paramref name="transaction"/>.</param>
	/// <param name="transaction">The gateway-owned transaction that also covers the <c>dbo.Trades</c> INSERT.</param>
	/// <param name="orderId">The order whose portfolio/security identifies the position to recompute.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The recomputed position state that was persisted.</returns>
	public async Task<PositionRecalcResult> ApplyTradeAsync(SqlConnection connection, SqlTransaction transaction, long orderId, CancellationToken cancellationToken = default)
	{
		if (connection is null)
			throw new ArgumentNullException(nameof(connection));

		if (transaction is null)
			throw new ArgumentNullException(nameof(transaction));

		// Step 1 - resolve the portfolio/security this order (and therefore the trade) belongs to.
		int portfolioId;
		int securityId;

		await using (var orderCommand = new SqlCommand(
			"SELECT portfolio_id, security_id FROM dbo.Orders WHERE order_id = @order_id", connection, transaction))
		{
			orderCommand.Parameters.Add(BigIntParam("@order_id", orderId));

			await using var orderReader = await orderCommand.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

			if (!await orderReader.ReadAsync(cancellationToken))
				throw new InvalidOperationException($"PositionRecalculationService: order_id {orderId} not found");

			portfolioId = orderReader.GetInt32(0);
			securityId = orderReader.GetInt32(1);
		}

		// Step 2 - take an update/range lock on the position key (held to the end of the caller's
		// transaction). This serializes concurrent fills of the same instrument and blocks a racing first
		// INSERT of the unique key. It also tells us whether to UPDATE or INSERT below.
		bool positionExists;

		await using (var lockCommand = new SqlCommand(
			"SELECT qty FROM dbo.Positions WITH (UPDLOCK, HOLDLOCK) WHERE portfolio_id = @portfolio_id AND security_id = @security_id", connection, transaction))
		{
			lockCommand.Parameters.Add(IntParam("@portfolio_id", portfolioId));
			lockCommand.Parameters.Add(IntParam("@security_id", securityId));

			await using var lockReader = await lockCommand.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
			positionExists = await lockReader.ReadAsync(cancellationToken);
		}

		// Step 3 - deterministic recompute from the ENTIRE persisted trade set of this portfolio/security,
		// ordered exactly like the removed trigger's cursor (executed_date, then trade_id). Folding the pure
		// math from flat is idempotent: the same trades always produce the same position.
		var qty = 0m;
		var avgPrice = 0m;
		var realizedPnl = 0m;

		await using (var tradesCommand = new SqlCommand(
			"SELECT o.side, t.qty, t.price " +
			"FROM dbo.Trades t " +
			"INNER JOIN dbo.Orders o ON o.order_id = t.order_id " +
			"WHERE o.portfolio_id = @portfolio_id AND o.security_id = @security_id " +
			"ORDER BY t.executed_date ASC, t.trade_id ASC", connection, transaction))
		{
			tradesCommand.Parameters.Add(IntParam("@portfolio_id", portfolioId));
			tradesCommand.Parameters.Add(IntParam("@security_id", securityId));

			await using var tradesReader = await tradesCommand.ExecuteReaderAsync(cancellationToken);

			while (await tradesReader.ReadAsync(cancellationToken))
			{
				// dbo.Orders.side is CHAR(1) constrained to 'B'/'S'.
				var side = tradesReader.GetString(0) == "B" ? Sides.Buy : Sides.Sell;
				var tradeQty = tradesReader.GetDecimal(1);
				var tradePrice = tradesReader.GetDecimal(2);

				var step = Recalculate(qty, avgPrice, realizedPnl, side, tradeQty, tradePrice);

				qty = step.Quantity;
				avgPrice = step.AveragePrice;
				realizedPnl = step.RealizedPnl;
			}
		}

		var result = new PositionRecalcResult
		{
			Quantity = qty,
			AveragePrice = avgPrice,
			RealizedPnl = realizedPnl,
		};

		// Step 4 - persist. unrealized_pnl is set to 0 on insert and is never maintained here; it stays an
		// end-of-day mark-to-market concern (see dbo.Positions in Database/001_Schema.sql).
		var persistSql = positionExists
			? "UPDATE dbo.Positions SET qty = @q, avg_price = @ap, realized_pnl = @rp, updated_date = SYSUTCDATETIME() WHERE portfolio_id = @portfolio_id AND security_id = @security_id"
			: "INSERT INTO dbo.Positions (portfolio_id, security_id, qty, avg_price, realized_pnl, unrealized_pnl, updated_date) VALUES (@portfolio_id, @security_id, @q, @ap, @rp, 0, SYSUTCDATETIME())";

		await using (var persistCommand = new SqlCommand(persistSql, connection, transaction))
		{
			persistCommand.Parameters.Add(IntParam("@portfolio_id", portfolioId));
			persistCommand.Parameters.Add(IntParam("@security_id", securityId));
			persistCommand.Parameters.Add(DecimalParam("@q", result.Quantity));
			persistCommand.Parameters.Add(DecimalParam("@ap", result.AveragePrice));
			persistCommand.Parameters.Add(DecimalParam("@rp", result.RealizedPnl));

			await persistCommand.ExecuteNonQueryAsync(cancellationToken);
		}

		return result;
	}

	/// <summary>
	/// Self-contained convenience overload for standalone callers (e.g. an out-of-band reconciliation): opens
	/// its own connection and transaction, delegates to
	/// <see cref="ApplyTradeAsync(SqlConnection, SqlTransaction, long, CancellationToken)"/>, and commits. The
	/// gateway does NOT use this overload on the trade-recording path — it uses the connection/transaction
	/// overload so the trade insertion and position update are one atomic unit.
	/// </summary>
	/// <param name="orderId">The order whose portfolio/security identifies the position to recompute.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The recomputed position state that was persisted.</returns>
	public async Task<PositionRecalcResult> ApplyTradeAsync(long orderId, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

		var result = await ApplyTradeAsync(connection, transaction, orderId, cancellationToken);

		await transaction.CommitAsync(cancellationToken);

		return result;
	}
}
