namespace StockSharp.Algo.Storages.Sql;

using System.Data;

using Microsoft.Data.SqlClient;

using StockSharp.Algo.Risk;

/// <summary>
/// Pure ADO.NET data-access gateway onto the StockSharpLegacy database (schema
/// lives under /Database at the repo root). Deliberately raw
/// <see cref="SqlCommand"/> calls, not an ORM. The gateway holds no risk
/// thresholds or accept/reject/P&amp;L logic: every decision is delegated to the
/// canonical C# services - <see cref="PreTradeRiskService"/> for pre-trade
/// validation and <see cref="PositionRecalculationService"/> for average-cost /
/// realized-P&amp;L recompute - while the gateway performs only the create/read
/// operations against dbo.Orders, dbo.Trades and dbo.Positions. The SQL side is
/// therefore pure data storage (tables, constraints, indexes); the retired
/// procedures (usp_SubmitOrder, usp_ValidatePreTradeRisk,
/// usp_RecalculatePositionOnTrade) and the trg_Trades_PositionRecalc trigger are
/// no longer used or installed.
///
/// This is an adapter that sits <i>alongside</i> <see cref="IEntityRegistry"/>,
/// not a replacement for it: Securities/Exchanges/subscriptions are still
/// served by <see cref="Csv.CsvEntityRegistry"/>. Only orders, trades and
/// positions live in SQL Server - that entity-storage split (nothing keeping the
/// SQL and CSV identifiers in sync) is described in LEGACY_LAYER.md and is
/// unrelated to the now-consolidated risk logic.
/// </summary>
public class SqlLegacyOrderGateway
{
	private readonly string _connectionString;
	private readonly PreTradeRiskService _preTradeRisk;
	private readonly PositionRecalculationService _positionRecalc;

	/// <summary>
	/// Initializes a new instance of the <see cref="SqlLegacyOrderGateway"/>.
	/// </summary>
	/// <param name="connectionString">SQL Server connection string for the StockSharpLegacy database.</param>
	public SqlLegacyOrderGateway(string connectionString)
	{
		if (connectionString.IsEmpty())
			throw new ArgumentNullException(nameof(connectionString));

		_connectionString = connectionString;
		_preTradeRisk = new PreTradeRiskService(connectionString);
		_positionRecalc = new PositionRecalculationService(connectionString);
	}

	private SqlConnection CreateConnection() => new(_connectionString);

	/// <summary>
	/// Finds the SQL-side portfolio_id for a <see cref="Portfolio"/>, creating the row
	/// if it doesn't exist yet. Matched by name - there is no shared surrogate key
	/// between <see cref="Portfolio"/> and dbo.Portfolios.
	/// </summary>
	public async Task<int> EnsurePortfolioAsync(Portfolio portfolio, CancellationToken cancellationToken = default)
	{
		if (portfolio is null)
			throw new ArgumentNullException(nameof(portfolio));

		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using (var select = new SqlCommand("SELECT portfolio_id FROM dbo.Portfolios WHERE name = @name", connection))
		{
			select.Parameters.AddWithValue("@name", portfolio.Name);

			if (await select.ExecuteScalarAsync(cancellationToken) is int existingId)
				return existingId;
		}

		// currency isn't modeled on BusinessEntities.Portfolio the way it is on
		// dbo.Portfolios, so newly auto-created rows always land on the column
		// default ('USD') regardless of what the security/portfolio actually trades in
		await using var insert = new SqlCommand(
			"INSERT INTO dbo.Portfolios (name) OUTPUT INSERTED.portfolio_id VALUES (@name)", connection);
		insert.Parameters.AddWithValue("@name", portfolio.Name);

		return (int)await insert.ExecuteScalarAsync(cancellationToken);
	}

	/// <summary>
	/// Finds the SQL-side security_id for a <see cref="Security"/>, creating the row
	/// if it doesn't exist yet. Matched by Code + Board.Code.
	/// </summary>
	public async Task<int> EnsureSecurityAsync(Security security, CancellationToken cancellationToken = default)
	{
		if (security is null)
			throw new ArgumentNullException(nameof(security));

		var boardCode = security.Board?.Code;

		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using (var select = new SqlCommand(
			"""
			SELECT security_id FROM dbo.Securities
			WHERE security_code = @code AND (board_code = @board OR (@board IS NULL AND board_code IS NULL))
			""", connection))
		{
			select.Parameters.AddWithValue("@code", security.Code);
			select.Parameters.AddWithValue("@board", (object)boardCode ?? DBNull.Value);

			if (await select.ExecuteScalarAsync(cancellationToken) is int existingId)
				return existingId;
		}

		await using var insert = new SqlCommand(
			"INSERT INTO dbo.Securities (security_code, board_code, security_type) OUTPUT INSERTED.security_id VALUES (@code, @board, @type)", connection);
		insert.Parameters.AddWithValue("@code", security.Code);
		insert.Parameters.AddWithValue("@board", (object)boardCode ?? DBNull.Value);
		insert.Parameters.AddWithValue("@type", (object)security.Type?.ToString() ?? DBNull.Value);

		return (int)await insert.ExecuteScalarAsync(cancellationToken);
	}

	/// <summary>
	/// Submits an order: runs the C# <see cref="PreTradeRiskService"/> pre-trade gate
	/// and inserts the resulting dbo.Orders row (ACCEPTED, or REJECTED with the reason
	/// preserved for the audit trail - rejected orders are recorded, not dropped, as
	/// the retired usp_SubmitOrder did). Validation and the insert run in one
	/// serializable transaction so a concurrent submission cannot change the
	/// frequency/volume/position/commission state between the check and the insert.
	/// </summary>
	public async Task<SqlOrderSubmitResult> SubmitOrderAsync(
		int portfolioId, int securityId, Sides side, decimal volume, decimal? price, OrderTypes orderType,
		long? externalTransactionId = null, string requestedBy = null, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

		// Canonical C# pre-trade gate; runs on the same connection/transaction as the
		// insert below so the read-decide-insert sequence is one atomic unit.
		var validation = await _preTradeRisk.ValidateAsync(
			connection, transaction, portfolioId, securityId, side, volume, price,
			orderType, requestedBy, cancellationToken);

		var status = validation.IsValid ? "ACCEPTED" : "REJECTED";

		long orderId;

		await using (var insert = new SqlCommand(
			"""
			INSERT INTO dbo.Orders (portfolio_id, security_id, side, qty, price, order_type, status, reject_reason, external_transaction_id)
				OUTPUT INSERTED.order_id
				VALUES (@portfolio_id, @security_id, @side, @qty, @price, @order_type, @status, @reject_reason, @external_transaction_id)
			""", connection)
		{
			Transaction = transaction,
		})
		{
			insert.Parameters.AddWithValue("@portfolio_id", portfolioId);
			insert.Parameters.AddWithValue("@security_id", securityId);
			insert.Parameters.AddWithValue("@side", MapSide(side));
			insert.Parameters.Add(new SqlParameter("@qty", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = volume });
			insert.Parameters.Add(new SqlParameter("@price", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = (object)price ?? DBNull.Value });
			insert.Parameters.AddWithValue("@order_type", MapOrderType(orderType));
			insert.Parameters.AddWithValue("@status", status);
			insert.Parameters.AddWithValue("@reject_reason", (object)validation.RejectReason ?? DBNull.Value);
			insert.Parameters.AddWithValue("@external_transaction_id", (object)externalTransactionId ?? DBNull.Value);

			orderId = (long)await insert.ExecuteScalarAsync(cancellationToken);
		}

		await transaction.CommitAsync(cancellationToken);

		return new()
		{
			OrderId = orderId,
			IsValid = validation.IsValid,
			RejectReason = validation.RejectReason,
		};
	}

	/// <summary>
	/// Records a fill against a SQL-side order: inserts the dbo.Trades row and then
	/// recomputes the position through the C# <see cref="PositionRecalculationService"/>
	/// EXACTLY ONCE, both inside one serializable transaction so the trade and its
	/// position effect commit or roll back together. The old trg_Trades_PositionRecalc
	/// trigger is gone, so there is a single, unambiguous recompute with no
	/// double-count hazard.
	/// </summary>
	public async Task RecordTradeAsync(long orderId, decimal qty, decimal price, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

		await using (var insert = new SqlCommand(
			"INSERT INTO dbo.Trades (order_id, qty, price) VALUES (@order_id, @qty, @price)", connection)
		{
			Transaction = transaction,
		})
		{
			insert.Parameters.AddWithValue("@order_id", orderId);
			insert.Parameters.Add(new SqlParameter("@qty", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = qty });
			insert.Parameters.Add(new SqlParameter("@price", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = price });

			var affected = await insert.ExecuteNonQueryAsync(cancellationToken);

			if (affected != 1)
				throw new InvalidOperationException(FormattableString.Invariant(
					$"Trade insert for order {orderId} affected {affected} rows (expected 1)."));
		}

		// Single recompute per trade, in the same transaction as the insert above.
		await _positionRecalc.RecalculateAsync(connection, transaction, orderId, qty, price, cancellationToken);

		await transaction.CommitAsync(cancellationToken);
	}

	/// <summary>
	/// Reads the current SQL-side position for a portfolio/security pair, or
	/// <see langword="null"/> if no trades have been recorded against it yet.
	/// </summary>
	public async Task<SqlPosition> GetPositionAsync(int portfolioId, int securityId, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using var command = new SqlCommand(
			"SELECT qty, avg_price, realized_pnl, unrealized_pnl, updated_date FROM dbo.Positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id",
			connection);
		command.Parameters.AddWithValue("@portfolio_id", portfolioId);
		command.Parameters.AddWithValue("@security_id", securityId);

		await using var reader = await command.ExecuteReaderAsync(cancellationToken);

		if (!await reader.ReadAsync(cancellationToken))
			return null;

		return new()
		{
			PortfolioId = portfolioId,
			SecurityId = securityId,
			Quantity = reader.GetDecimal(0),
			AveragePrice = reader.GetDecimal(1),
			RealizedPnL = reader.GetDecimal(2),
			UnrealizedPnL = reader.GetDecimal(3),
			UpdatedDate = reader.GetDateTime(4),
		};
	}

	private static string MapOrderType(OrderTypes type) => type switch
	{
		OrderTypes.Limit => "LIMIT",
		OrderTypes.Market => "MARKET",
		_ => throw new NotSupportedException($"Order type '{type}' has no dbo.Orders.order_type mapping (LIMIT/MARKET only)."),
	};

	private static string MapSide(Sides side) => side switch
	{
		Sides.Buy => "B",
		Sides.Sell => "S",
		_ => throw new NotSupportedException($"Order side '{side}' has no dbo.Orders.side mapping (Buy/Sell only)."),
	};
}
