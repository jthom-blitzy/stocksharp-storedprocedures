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
///   <see cref="PositionRecalculationService"/> exactly once per inserted trade, inside
///   the SAME transaction as the trade insert - the position-recalc database trigger was
///   removed in the consolidation (see Database/003_Triggers.sql), so this single call is
///   now the sole applier.</item>
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

	// The gateway owns a single PositionRecalculationService and calls ApplyAsync exactly once for each
	// freshly-inserted trade, inside the SAME gateway-owned transaction as the trade INSERT (single-apply
	// invariant, AAP 0.6.5). Exactly-once is enforced by TWO cooperating mechanisms: (1) one atomic
	// transaction per trade with a fresh unique trade_id, so each committed trade is applied once and a
	// rolled-back attempt persists nothing; and (2) a DURABLE database guard - ApplyAsync flips this trade's
	// trades.position_applied flag FALSE->TRUE in the SAME commit as the position write, so a restart, retry,
	// or second-instance replay of an already-committed trade_id sees the flag set and is an idempotent no-op
	// (C3). This replaces the former in-process HashSet, which could neither survive a restart nor coordinate
	// across instances. Constructed once and reused across calls.
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

		// Atomic upsert-or-select (F7): a single INSERT ... ON CONFLICT (LOWER(name)) DO UPDATE ... RETURNING
		// removes the former SELECT-then-INSERT race in which two concurrent callers could both miss the row
		// and both attempt the INSERT (the second hitting UQ_Portfolios_name). DO UPDATE (a no-op touch that
		// re-sets name to its EXISTING value) rather than DO NOTHING is used deliberately: DO NOTHING returns
		// NO row on conflict, whereas DO UPDATE always yields the existing row via RETURNING. The conflict
		// target LOWER(name) matches the case-insensitive functional unique index UQ_Portfolios_name (F2), so
		// ensuring 'demo' after 'DEMO' resolves to the SAME row; SET name = portfolios.name keeps the update a
		// true no-op that PRESERVES the originally stored casing. currency is not modeled on
		// BusinessEntities.Portfolio, so an auto-created row lands on the column default ('USD'); dbo. qualifier
		// dropped (objects live in the public schema).
		await using var command = new NpgsqlCommand(
			"INSERT INTO portfolios (name) VALUES (@name) " +
			"ON CONFLICT (LOWER(name)) DO UPDATE SET name = portfolios.name " +
			"RETURNING portfolio_id", connection);
		command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Varchar) { Value = portfolio.Name });

		return (int)await command.ExecuteScalarAsync(cancellationToken);
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

		// Atomic upsert-or-select (F7): INSERT ... ON CONFLICT (security_code, board_code) DO UPDATE ...
		// RETURNING removes the former SELECT-then-INSERT race. The conflict target matches
		// UQ_Securities_code_board, declared UNIQUE NULLS NOT DISTINCT (Phase-2 schema fix) so a NULL
		// board_code participates in the uniqueness check - reproducing the old
		// "@board IS NULL AND board_code IS NULL" match and preventing duplicate (code, NULL board) rows.
		// DO UPDATE (a no-op touch of security_code) guarantees a RETURNING row on an existing match, where
		// DO NOTHING would return none. T-SQL "OUTPUT INSERTED.security_id" -> Postgres "RETURNING security_id".
		await using var command = new NpgsqlCommand(
			"INSERT INTO securities (security_code, board_code, security_type) VALUES (@code, @board, @type) " +
			"ON CONFLICT (security_code, board_code) DO UPDATE SET security_code = EXCLUDED.security_code " +
			"RETURNING security_id", connection);
		command.Parameters.Add(new NpgsqlParameter("code", NpgsqlDbType.Varchar) { Value = security.Code });
		command.Parameters.Add(new NpgsqlParameter("board", NpgsqlDbType.Varchar) { Value = (object)boardCode ?? DBNull.Value });
		command.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Varchar) { Value = (object)security.Type?.ToString() ?? DBNull.Value });

		return (int)await command.ExecuteScalarAsync(cancellationToken);
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
	/// The validation reads and the order INSERT run in ONE transaction, serialized per-portfolio by a
	/// transaction-scoped advisory lock, so a concurrent burst cannot slip orders past the rolling
	/// frequency / daily-volume / position gate between the check and the INSERT (the read-then-write
	/// TOCTOU). Both accepted and rejected orders are still persisted (parity with the retired proc), so a
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
		// F23: exhaustive side mapping. An undefined Sides value is a programming error and must NOT silently
		// fall through to "S" (the retired ternary treated everything that was not Buy as a sell, so a bad
		// enum value would be recorded as a sell); reject it, mirroring MapOrderType's exhaustive switch.
		var sideCode = side switch
		{
			Sides.Buy => "B",
			Sides.Sell => "S",
			_ => throw new ArgumentOutOfRangeException(nameof(side), side, "Order side must be Buy or Sell."),
		};

		// M11/M12: normalize qty/price ONCE, up front, to the schema's NUMERIC(18,4) scale with the SAME
		// canonical quantizer the gate uses, then VALIDATE and PERSIST that one normalized value. Postgres
		// rounds a NUMERIC insert with the same round-half-away-from-zero rule, so binding the pre-quantized
		// value and letting the DB round agree exactly; this closes the former mismatch where the gate decided
		// on a quantized value while the INSERT bound the raw value (they could round differently and the stored
		// order would not match the decision).
		var qtyQ = CanonicalRiskRules.QuantizeToScale(volume);
		decimal? priceQ = price.HasValue ? CanonicalRiskRules.QuantizeToScale(price.Value) : (decimal?)null;

		// STRUCTURAL validity short-circuit (M11/M12): a qty that rounds to <= 0, or a non-null price that rounds
		// to <= 0, cannot be stored - it would violate CK_Orders_qty / CK_Orders_price - and is a malformed
		// request rather than a risk-limit breach, so it is NEITHER persisted NOR counted toward the rolling
		// order-frequency window (the retired proc could not persist it either, given the same CHECKs). Return
		// the rejection DTO directly (OrderId = 0, no row written), using the SAME reason text the pure gate
		// emits for these cases so the production path and the direct-gate parity tests agree.
		if (qtyQ <= 0m)
			return new() { OrderId = 0, IsValid = false, RejectReason = "Invalid qty" };

		if (priceQ is not null && priceQ.Value <= 0m)
			return new() { OrderId = 0, IsValid = false, RejectReason = $"Invalid price {priceQ.Value.To<string>()}" };

		// F1: run the DB-state-aware validation reads AND the order INSERT inside ONE gateway-owned
		// transaction, serialized per-portfolio by a transaction-scoped advisory lock, so a concurrent burst
		// cannot slip extra orders past the rolling frequency / daily-volume / position gate between a check
		// and the INSERT (the former read-then-write TOCTOU). A bounded retry re-runs the whole unit if
		// Postgres reports a transient serialization failure or deadlock; each failed attempt has fully rolled
		// back, so no partial order row can leak and the retry re-reads fresh state.
		const int maxAttempts = 3;

		for (var attempt = 1; ; attempt++)
		{
			try
			{
				await using var connection = CreateConnection();
				await connection.OpenAsync(cancellationToken);
				await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

				// Per-portfolio serialization (C4). Lock on the SINGLE-key pg_advisory_xact_lock(bigint) space,
				// keyed on portfolio_id - the SAME advisory-lock space PositionRecalculationService.ApplyAsync
				// takes. The former (portfolio_id, 0) two-key form lived in a DIFFERENT PostgreSQL advisory-lock
				// space from the recalc service's single-key lock, so an order submission and a position apply on
				// the same portfolio did NOT mutually exclude; unifying both services on the one-arg bigint
				// overload (portfolio_id widened to bigint) restores one coherent per-portfolio lock hierarchy.
				using (var lockCommand = new NpgsqlCommand(
					"SELECT pg_advisory_xact_lock(@portfolio_id)", connection, transaction))
				{
					lockCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Bigint) { Value = (long)portfolioId });
					await lockCommand.ExecuteNonQueryAsync(cancellationToken);
				}

				// Business decision delegated to the canonical PreTradeRiskService (Algo/Risk). It reads
				// risklimits/positions/orders/trades on THIS connection + transaction so its view is
				// consistent with, and serialized against, the INSERT below.
				var validation = await _preTradeRisk.ValidateAsync(
					connection, transaction, portfolioId, securityId, sideCode, qtyQ, priceQ, cancellationToken);
				var status = validation.IsValid ? "ACCEPTED" : "REJECTED";

				// Parameterized INSERT (StoredProcedure call retired; business logic moved to Algo/Risk). Both
				// ACCEPTED and REJECTED orders are persisted (parity with usp_SubmitOrder; a rejected order
				// still counts toward the frequency window). T-SQL "OUTPUT INSERTED.order_id" -> "RETURNING".
				await using var command = new NpgsqlCommand(
					"INSERT INTO orders " +
					"(portfolio_id, security_id, side, qty, price, order_type, status, reject_reason, external_transaction_id, submitted_date) " +
					"VALUES " +
					"(@portfolio_id, @security_id, @side, @qty, @price, @order_type, @status, @reject_reason, @external_transaction_id, now() at time zone 'utc') " +
					"RETURNING order_id", connection, transaction);

				command.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
				command.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });
				command.Parameters.Add(new NpgsqlParameter("side", NpgsqlDbType.Varchar) { Value = sideCode });
				// qty / price are NUMERIC(18,4): bind the NORMALIZED qtyQ/priceQ (M12) as decimal (never
				// double/float) so what is PERSISTED is byte-for-byte what the gate VALIDATED and no downstream
				// >= comparison can silently loosen (AAP 0.6.4).
				command.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = qtyQ });
				command.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = (object)priceQ ?? DBNull.Value });
				command.Parameters.Add(new NpgsqlParameter("order_type", NpgsqlDbType.Varchar) { Value = MapOrderType(orderType) });
				command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Varchar) { Value = status });
				command.Parameters.Add(new NpgsqlParameter("reject_reason", NpgsqlDbType.Varchar) { Value = (object)validation.RejectReason ?? DBNull.Value });
				command.Parameters.Add(new NpgsqlParameter("external_transaction_id", NpgsqlDbType.Bigint) { Value = (object)externalTransactionId ?? DBNull.Value });
				// submitted_date is written by now() at time zone 'utc' (== SYSUTCDATETIME(), the UTC time
				// source, AAP 0.6.4); last_updated is left to its column DEFAULT. requestedBy is intentionally
				// neither persisted nor forwarded to the risk gate (see the remarks above).

				var orderId = (long)await command.ExecuteScalarAsync(cancellationToken);

				await transaction.CommitAsync(cancellationToken);

				// Pure relay of the service decision: IsValid/RejectReason from the gate; OrderId from RETURNING.
				return new()
				{
					OrderId = orderId,
					IsValid = validation.IsValid,
					RejectReason = validation.RejectReason,
				};
			}
			catch (PostgresException ex) when (attempt < maxAttempts &&
				(ex.SqlState == PostgresErrorCodes.SerializationFailure || ex.SqlState == PostgresErrorCodes.DeadlockDetected))
			{
				// Transient serialization failure / deadlock: the failed transaction has already rolled back
				// (await using disposal), so no partial order row persists. Fall through to retry the whole
				// validate+insert unit against fresh state; the final attempt lets the exception propagate.
			}
		}
	}

	/// <summary>
	/// Records a fill against an order. Inserts into the trades table and then applies the
	/// trade's effect to the position exactly once via <see cref="PositionRecalculationService"/>,
	/// inside a single gateway-owned transaction so the trade row and the position update commit
	/// together or not at all (atomicity, AAP 0.6.5). The old <c>trg_Trades_PositionRecalc</c> trigger
	/// that used to recompute the position in the database was removed in the consolidation, so this
	/// explicit call is now the sole applier. Exactly-once is enforced durably by the service's
	/// <c>trades.position_applied</c> guard (flipped in the same commit), so a retried or replayed
	/// apply of the SAME persisted trade is a safe idempotent no-op rather than a double count.
	/// </summary>
	public async Task RecordTradeAsync(long orderId, decimal qty, decimal price, CancellationToken cancellationToken = default)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		// Atomicity (F2, AAP 0.6.5): the trade INSERT and the position recompute run in ONE gateway-owned
		// transaction, so a trade row can never be committed without its position effect (nor vice versa).
		// The recalculation service enrols in this transaction - it neither begins nor commits it and takes
		// its per-portfolio advisory lock on it (C4, the same lock space this gateway's SubmitOrderAsync uses)
		// - and the gateway commits once at the end.
		await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

		// Insert the fill, capturing the generated trade id AND the PERSISTED qty/price. executed_date via
		// now() at time zone 'utc' (UTC time source, AAP 0.6.4). C5 fix: bind the caller's qty/price but read
		// them BACK via RETURNING, so the position recompute below runs on the values ACTUALLY stored in the
		// NUMERIC(18,4) columns (Postgres rounds to 4dp on insert) rather than on a raw >4dp input that would
		// disagree with the persisted trade row and could loosen a downstream >= comparison. T-SQL
		// "OUTPUT INSERTED.*" -> Postgres "RETURNING" (was OUTPUT INSERTED / trigger-driven flow).
		long tradeId;
		decimal persistedQty;
		decimal persistedPrice;
		await using (var insert = new NpgsqlCommand(
			"INSERT INTO trades (order_id, qty, price, executed_date) VALUES (@order_id, @qty, @price, now() at time zone 'utc') RETURNING trade_id, qty, price", connection, transaction))
		{
			insert.Parameters.Add(new NpgsqlParameter("order_id", NpgsqlDbType.Bigint) { Value = orderId });
			// qty / price are NUMERIC(18,4): bind as decimal (never double/float) - AAP 0.6.4.
			insert.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = qty });
			insert.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = price });

			// No MARS on a single Npgsql connection: fully read and dispose this reader before ApplyAsync runs
			// its own commands on the same connection + transaction.
			await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
			await reader.ReadAsync(cancellationToken);
			tradeId = reader.GetInt64(0);
			// Persisted (4dp-rounded) qty/price -> decimal, so ApplyAsync computes from stored values (C5).
			persistedQty = reader.GetDecimal(1);
			persistedPrice = reader.GetDecimal(2);
		}

		// Single-apply invariant (AAP 0.6.5): the trg_Trades_PositionRecalc trigger was removed, so the
		// gateway is the sole applier and calls the recalculation service exactly once for this freshly-
		// inserted trade, on THIS connection + transaction. The service owns neither the connection nor the
		// transaction (the gateway does), so the trade row and the position write form one atomic unit.
		await _positionRecalc.ApplyAsync(connection, transaction, orderId, tradeId, persistedQty, persistedPrice, cancellationToken);

		await transaction.CommitAsync(cancellationToken);
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
