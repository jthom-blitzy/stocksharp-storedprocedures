namespace StockSharp.Algo.Storages.Sql;

using System.Data;

using Microsoft.Data.SqlClient;

/// <summary>
/// ADO.NET gateway onto the StockSharpLegacy stored-proc layer (schema and
/// procs live under /Database at the repo root). Deliberately raw
/// <see cref="SqlCommand"/> calls, not an ORM - this is meant to look like
/// the real production call pattern (CommandType.StoredProcedure + output
/// params), not a clean data-access abstraction.
///
/// This is an adapter that sits <i>alongside</i> <see cref="IEntityRegistry"/>,
/// not a replacement for it: Securities/Exchanges/subscriptions are still
/// served by <see cref="Csv.CsvEntityRegistry"/>. Only orders, trades and
/// positions have been moved to SQL Server so far. That split - some entities
/// in SQL, some still in CSV, nothing keeping their identifiers in sync - is
/// the same half-migrated state described in LEGACY_LAYER.md.
/// </summary>
public class SqlLegacyOrderGateway
{
	private readonly string _connectionString;

	/// <summary>
	/// Initializes a new instance of the <see cref="SqlLegacyOrderGateway"/>.
	/// </summary>
	/// <param name="connectionString">SQL Server connection string for the StockSharpLegacy database.</param>
	public SqlLegacyOrderGateway(string connectionString)
	{
		if (connectionString.IsEmpty())
			throw new ArgumentNullException(nameof(connectionString));

		_connectionString = connectionString;
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
	/// Submits an order through usp_SubmitOrder, which runs usp_ValidatePreTradeRisk
	/// internally before inserting the row (accepted or rejected).
	/// </summary>
	public async Task<SqlOrderSubmitResult> SubmitOrderAsync(
		int portfolioId, int securityId, Sides side, decimal volume, decimal? price, OrderTypes orderType,
		long? externalTransactionId = null, string requestedBy = null, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using var command = new SqlCommand("dbo.usp_SubmitOrder", connection)
		{
			CommandType = CommandType.StoredProcedure,
		};

		command.Parameters.AddWithValue("@portfolio_id", portfolioId);
		command.Parameters.AddWithValue("@security_id", securityId);
		command.Parameters.AddWithValue("@side", side == Sides.Buy ? "B" : "S");
		command.Parameters.AddWithValue("@qty", volume);
		command.Parameters.AddWithValue("@price", (object)price ?? DBNull.Value);
		command.Parameters.AddWithValue("@order_type", MapOrderType(orderType));
		command.Parameters.AddWithValue("@external_transaction_id", (object)externalTransactionId ?? DBNull.Value);
		command.Parameters.AddWithValue("@requested_by", (object)requestedBy ?? DBNull.Value);

		var orderIdParam = new SqlParameter("@order_id", SqlDbType.BigInt) { Direction = ParameterDirection.Output };
		var isValidParam = new SqlParameter("@is_valid", SqlDbType.Bit) { Direction = ParameterDirection.Output };
		var rejectReasonParam = new SqlParameter("@reject_reason", SqlDbType.NVarChar, 200) { Direction = ParameterDirection.Output };

		command.Parameters.Add(orderIdParam);
		command.Parameters.Add(isValidParam);
		command.Parameters.Add(rejectReasonParam);

		await command.ExecuteNonQueryAsync(cancellationToken);

		return new()
		{
			OrderId = (long)orderIdParam.Value,
			IsValid = (bool)isValidParam.Value,
			RejectReason = rejectReasonParam.Value as string,
		};
	}

	/// <summary>
	/// Records a fill against a SQL-side order. Inserting into dbo.Trades fires
	/// trg_Trades_PositionRecalc automatically, which recomputes the position -
	/// callers must NOT also invoke usp_RecalculatePositionOnTrade for the same
	/// trade (see the warning in Database/002_StoredProcedures.sql).
	/// </summary>
	public async Task RecordTradeAsync(long orderId, decimal qty, decimal price, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using var command = new SqlCommand(
			"INSERT INTO dbo.Trades (order_id, qty, price) VALUES (@order_id, @qty, @price)", connection);
		command.Parameters.AddWithValue("@order_id", orderId);
		command.Parameters.AddWithValue("@qty", qty);
		command.Parameters.AddWithValue("@price", price);

		await command.ExecuteNonQueryAsync(cancellationToken);
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
}
