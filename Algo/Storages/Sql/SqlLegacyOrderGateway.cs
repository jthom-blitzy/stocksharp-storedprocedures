namespace StockSharp.Algo.Storages.Sql;

using Npgsql;
using NpgsqlTypes;

using StockSharp.Algo.Risk;

/// <summary>
/// ADO.NET (Npgsql) gateway onto the StockSharpLegacy PostgreSQL database (schema
/// lives under /Database at the repo root). Deliberately raw
/// <see cref="NpgsqlCommand"/> calls, not an ORM - this is meant to look like the
/// real production call pattern, not a clean data-access abstraction.
///
/// Consolidated model: the SQL Server stored procedures were retired and their
/// business logic moved to canonical C# services under Algo/Risk/, so this gateway
/// is now a PURE data-access layer that makes no accept/reject or P&amp;L decisions:
/// <list type="bullet">
///   <item>pre-trade accept/reject is delegated to <see cref="PreTradeRiskService"/>,
///   and the gateway merely relays its { IsValid, RejectReason } decision;</item>
///   <item>rows are written with plain parameterised <c>INSERT ... RETURNING</c>
///   (no stored procedures, no T-SQL <c>OUTPUT</c> parameters);</item>
///   <item>position recomputation is delegated to
///   <see cref="PositionRecalculationService"/> exactly once per inserted trade -
///   the position-recalc database trigger was removed in the consolidation
///   (see Database/003_Triggers.sql), so this single call is now the sole applier.</item>
/// </list>
///
/// This is an adapter that sits <i>alongside</i> <see cref="IEntityRegistry"/>,
/// not a replacement for it: Securities/Exchanges/subscriptions are still served by
/// <see cref="Csv.CsvEntityRegistry"/>. Only orders, trades and positions have been
/// moved to the database. That split - some entities in SQL, some still in CSV,
/// nothing keeping their identifiers in sync - is the same half-migrated state
/// described in LEGACY_LAYER.md.
/// </summary>
public class SqlLegacyOrderGateway
{
	private readonly string _connectionString;

	// Canonical per-order accept/reject gate (Algo/Risk). The gateway delegates the entire
	// pre-trade accept/reject decision to this service and makes none of its own; it is
	// constructed once and reused across calls (stateless apart from its injected UTC clock).
	// The logic that used to live in dbo.usp_ValidatePreTradeRisk now lives here (AAP 0.1/0.6).
	private readonly PreTradeRiskService _preTradeRisk = new();

	// The gateway owns a single PositionRecalculationService and calls ApplyAsync exactly
	// once per inserted trade (single-apply invariant, AAP 0.6.5). The service enforces that
	// invariant DURABLY at the database level (it claims each trade_id in the processedtrades
	// ledger inside its own transaction), so an accidental re-apply of the same trade is an
	// idempotent no-op even across service instances, process restarts, or concurrent callers -
	// not merely within one in-memory instance.
	private readonly PositionRecalculationService _positionRecalc = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="SqlLegacyOrderGateway"/>.
	/// </summary>
	/// <param name="connectionString">PostgreSQL (Npgsql) connection string for the StockSharpLegacy database.</param>
	public SqlLegacyOrderGateway(string connectionString)
	{
		if (connectionString.IsEmpty())
			throw new ArgumentNullException(nameof(connectionString));

		_connectionString = connectionString;
	}

	private NpgsqlConnection CreateConnection() => new(_connectionString);

	/// <summary>
	/// Finds the portfolio_id for a <see cref="Portfolio"/>, creating the row if it
	/// doesn't exist yet. Matched by name - there is no shared surrogate key between
	/// <see cref="Portfolio"/> and the portfolios table.
	/// </summary>
	public async Task<int> EnsurePortfolioAsync(Portfolio portfolio, CancellationToken cancellationToken = default)
	{
		if (portfolio is null)
			throw new ArgumentNullException(nameof(portfolio));

		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		// dbo. qualifier dropped: objects live in the public schema (unqualified).
		await using (var select = new NpgsqlCommand("SELECT portfolio_id FROM portfolios WHERE name = @name", connection))
		{
			select.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Varchar) { Value = portfolio.Name });

			if (await select.ExecuteScalarAsync(cancellationToken) is int existingId)
				return existingId;
		}

		// currency isn't modeled on BusinessEntities.Portfolio the way it is on the
		// portfolios table, so newly auto-created rows always land on the column
		// default ('USD') regardless of what the security/portfolio actually trades in.
		// T-SQL "OUTPUT INSERTED.portfolio_id" -> Postgres "RETURNING portfolio_id".
		await using var insert = new NpgsqlCommand(
			"INSERT INTO portfolios (name) VALUES (@name) RETURNING portfolio_id", connection);
		insert.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Varchar) { Value = portfolio.Name });

		return (int)await insert.ExecuteScalarAsync(cancellationToken);
	}

	/// <summary>
	/// Finds the security_id for a <see cref="Security"/>, creating the row if it
	/// doesn't exist yet. Matched by Code + Board.Code.
	/// </summary>
	public async Task<int> EnsureSecurityAsync(Security security, CancellationToken cancellationToken = default)
	{
		if (security is null)
			throw new ArgumentNullException(nameof(security));

		var boardCode = security.Board?.Code;

		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using (var select = new NpgsqlCommand(
			"""
			SELECT security_id FROM securities
			WHERE security_code = @code AND (board_code = @board OR (@board IS NULL AND board_code IS NULL))
			""", connection))
		{
			select.Parameters.Add(new NpgsqlParameter("code", NpgsqlDbType.Varchar) { Value = security.Code });
			select.Parameters.Add(new NpgsqlParameter("board", NpgsqlDbType.Varchar) { Value = (object)boardCode ?? DBNull.Value });

			if (await select.ExecuteScalarAsync(cancellationToken) is int existingId)
				return existingId;
		}

		// T-SQL "OUTPUT INSERTED.security_id" -> Postgres "RETURNING security_id".
		await using var insert = new NpgsqlCommand(
			"INSERT INTO securities (security_code, board_code, security_type) VALUES (@code, @board, @type) RETURNING security_id", connection);
		insert.Parameters.Add(new NpgsqlParameter("code", NpgsqlDbType.Varchar) { Value = security.Code });
		insert.Parameters.Add(new NpgsqlParameter("board", NpgsqlDbType.Varchar) { Value = (object)boardCode ?? DBNull.Value });
		insert.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Varchar) { Value = (object)security.Type?.ToString() ?? DBNull.Value });

		return (int)await insert.ExecuteScalarAsync(cancellationToken);
	}

	/// <summary>
	/// Submits an order into the orders table and returns the persisted order id together with the
	/// canonical pre-trade accept/reject decision.
	/// </summary>
	/// <remarks>
	/// Consolidation note (AAP 0.1/0.6): the retired <c>dbo.usp_SubmitOrder</c> ran
	/// <c>usp_ValidatePreTradeRisk</c> before inserting the row. That pre-trade accept/reject decision
	/// now lives in the canonical C# <see cref="PreTradeRiskService"/> under Algo/Risk/, so this gateway
	/// makes NO decision of its own: it delegates to the service and is a pure relay of the returned
	/// { <see cref="SqlOrderSubmitResult.IsValid"/>, <see cref="SqlOrderSubmitResult.RejectReason"/> }.
	/// Both accepted and rejected orders are still persisted (parity with the retired proc), so a
	/// rejected order continues to count toward the rolling order-frequency window. The public
	/// signature is preserved for the demo and other callers; <paramref name="requestedBy"/> is
	/// retained on the signature but is not persisted (the orders table has no such column - it was
	/// only ever a presently-unused argument of the retired proc) and is not passed to the risk gate
	/// (the service does not consume it).
	/// </remarks>
	public async Task<SqlOrderSubmitResult> SubmitOrderAsync(
		int portfolioId, int securityId, Sides side, decimal volume, decimal? price, OrderTypes orderType,
		long? externalTransactionId = null, string requestedBy = null, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		// 'B'/'S' side sign carries over unchanged from the SQL side.
		var sideCode = side == Sides.Buy ? "B" : "S";

		// Business decision delegated to the canonical PreTradeRiskService (Algo/Risk). The old
		// dbo.usp_SubmitOrder StoredProcedure call (which ran usp_ValidatePreTradeRisk internally) is
		// retired; the gateway is now a pure relay of the service's decision. The already-open
		// connection + token are passed through so the DB-state-aware gate can read
		// risklimits/positions/orders/trades for the position, frequency and daily-volume checks.
		var validation = await _preTradeRisk.ValidateAsync(connection, portfolioId, securityId, sideCode, volume, price, cancellationToken);
		var status = validation.IsValid ? "ACCEPTED" : "REJECTED";

		// StoredProcedure call replaced by service delegation + parameterized INSERT (business logic
		// moved to Algo/Risk; usp_SubmitOrder/usp_ValidatePreTradeRisk retired). The LIMIT/MARKET
		// order_type carries over unchanged. T-SQL "OUTPUT INSERTED.order_id" -> Postgres
		// "RETURNING order_id".
		await using var command = new NpgsqlCommand(
			"""
			INSERT INTO orders
				(portfolio_id, security_id, side, qty, price, order_type, status, reject_reason, external_transaction_id, submitted_date)
			VALUES
				(@portfolio_id, @security_id, @side, @qty, @price, @order_type, @status, @reject_reason, @external_transaction_id, now() at time zone 'utc')
			RETURNING order_id
			""", connection);

		command.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
		command.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });
		command.Parameters.Add(new NpgsqlParameter("side", NpgsqlDbType.Varchar) { Value = sideCode });
		// qty / price are NUMERIC(18,4): bind as decimal (never double/float) so the schema's
		// scale is preserved and no downstream >= comparison can silently loosen (AAP 0.6.4).
		command.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = volume });
		command.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = (object)price ?? DBNull.Value });
		command.Parameters.Add(new NpgsqlParameter("order_type", NpgsqlDbType.Varchar) { Value = MapOrderType(orderType) });
		// both ACCEPTED and REJECTED orders are persisted (parity with usp_SubmitOrder; rejected
		// orders still count toward the frequency window).
		command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Varchar) { Value = status });
		command.Parameters.Add(new NpgsqlParameter("reject_reason", NpgsqlDbType.Varchar) { Value = (object)validation.RejectReason ?? DBNull.Value });
		command.Parameters.Add(new NpgsqlParameter("external_transaction_id", NpgsqlDbType.Bigint) { Value = (object)externalTransactionId ?? DBNull.Value });
		// submitted_date is written by the SQL expression now() at time zone 'utc' (== SYSUTCDATETIME(),
		// the UTC time source, AAP 0.6.4); last_updated is left to its column DEFAULT. requestedBy is
		// intentionally neither persisted nor forwarded to the risk gate (see the remarks above).

		var orderId = (long)await command.ExecuteScalarAsync(cancellationToken);

		// Pure relay of the service decision: IsValid/RejectReason come straight from the gate;
		// OrderId from RETURNING.
		return new()
		{
			OrderId = orderId,
			IsValid = validation.IsValid,
			RejectReason = validation.RejectReason,
		};
	}

	/// <summary>
	/// Records a fill against an order. Inserts into the trades table and then applies the
	/// trade's effect to the position exactly once via <see cref="PositionRecalculationService"/>.
	/// The old <c>trg_Trades_PositionRecalc</c> trigger that used to recompute the position in the
	/// database was removed in the consolidation, so this explicit single call is now the sole
	/// applier - callers must NOT apply the same trade a second time (AAP 0.6.5).
	/// </summary>
	public async Task RecordTradeAsync(long orderId, decimal qty, decimal price, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		// Insert the fill, capturing the generated trade id. executed_date via now() at time zone
		// 'utc' (UTC time source, AAP 0.6.4). T-SQL "OUTPUT INSERTED.trade_id" -> Postgres
		// "RETURNING trade_id" (was OUTPUT INSERTED / trigger-driven flow).
		long tradeId;
		await using (var insert = new NpgsqlCommand(
			"INSERT INTO trades (order_id, qty, price, executed_date) VALUES (@order_id, @qty, @price, now() at time zone 'utc') RETURNING trade_id", connection))
		{
			insert.Parameters.Add(new NpgsqlParameter("order_id", NpgsqlDbType.Bigint) { Value = orderId });
			// qty / price are NUMERIC(18,4): bind as decimal (never double/float) - AAP 0.6.4.
			insert.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = qty });
			insert.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = price });

			tradeId = (long)await insert.ExecuteScalarAsync(cancellationToken);
		}

		// Single-apply invariant (AAP 0.6.5): the trg_Trades_PositionRecalc trigger was removed, so the
		// gateway is the sole applier and calls the recalculation service exactly once per inserted trade.
		// The service owns neither the connection nor a transaction (the gateway does), matching the
		// transactional context the retired procedure ran in.
		await _positionRecalc.ApplyAsync(connection, orderId, tradeId, qty, price, cancellationToken);
	}

	/// <summary>
	/// Reads the current position for a portfolio/security pair, or <see langword="null"/>
	/// if no trades have been recorded against it yet.
	/// </summary>
	public async Task<SqlPosition> GetPositionAsync(int portfolioId, int securityId, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		await using var command = new NpgsqlCommand(
			"SELECT qty, avg_price, realized_pnl, unrealized_pnl, updated_date FROM positions WHERE portfolio_id = @portfolio_id AND security_id = @security_id",
			connection);
		command.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
		command.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });

		await using var reader = await command.ExecuteReaderAsync(cancellationToken);

		if (!await reader.ReadAsync(cancellationToken))
			return null;

		return new()
		{
			PortfolioId = portfolioId,
			SecurityId = securityId,
			// NUMERIC(18,4) columns -> decimal (never double) to preserve scale.
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
		_ => throw new NotSupportedException($"Order type '{type}' has no orders.order_type mapping (LIMIT/MARKET only)."),
	};
}
