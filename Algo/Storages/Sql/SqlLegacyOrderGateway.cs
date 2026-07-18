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

		// Atomic upsert-or-select (F7): INSERT ... ON CONFLICT (LOWER(security_code), LOWER(board_code))
		// DO UPDATE ... RETURNING removes the former SELECT-then-INSERT race. The conflict target is the
		// case-insensitive expression list of the functional unique index UQ_Securities_code_board (M4),
		// declared NULLS NOT DISTINCT so a NULL board_code participates in the uniqueness check - reproducing
		// the old "@board IS NULL AND board_code IS NULL" match, preventing duplicate (code, NULL board) rows,
		// AND folding case so ensuring 'aapl' after 'AAPL' resolves to the SAME row (SQL Server CI parity).
		// DO UPDATE re-sets security_code to its EXISTING stored value (SET security_code = securities.security_code),
		// making the update a true no-op that PRESERVES the originally stored casing while still guaranteeing a
		// RETURNING row on an existing match (DO NOTHING would return none). This deliberately mirrors
		// EnsurePortfolioAsync above: using EXCLUDED.security_code here would rewrite the persisted display casing
		// on a live case-variant repeat (e.g. ensuring 'aapl' after 'AAPL' would overwrite the stored 'AAPL'),
		// which the QA Final-Acceptance reconciliation flagged - case-insensitive identity resolution must resolve
		// to the SAME row WITHOUT mutating the stored casing. T-SQL "OUTPUT INSERTED.security_id" -> Postgres
		// "RETURNING security_id".
		await using var command = new NpgsqlCommand(
			"INSERT INTO securities (security_code, board_code, security_type) VALUES (@code, @board, @type) " +
			"ON CONFLICT (LOWER(security_code), LOWER(board_code)) DO UPDATE SET security_code = securities.security_code " +
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
	/// explicit call is now the sole applier. Exactly-once for a REPLAY of the same persisted trade is
	/// enforced durably by the service's <c>trades.position_applied</c> guard (flipped in the same commit),
	/// so a retried or replayed apply of the SAME persisted trade is a safe idempotent no-op rather than a
	/// double count. To also dedup a RETRY that would otherwise insert a fresh trade id for a fill that was
	/// already persisted, use the <see cref="RecordTradeAsync(long, decimal, decimal, long, CancellationToken)"/>
	/// overload that carries a stable external fill key (C2).
	/// </summary>
	public Task RecordTradeAsync(long orderId, decimal qty, decimal price, CancellationToken cancellationToken = default)
		=> RecordTradeCoreAsync(orderId, qty, price, externalTradeId: null, cancellationToken);

	/// <summary>
	/// Records a fill against an order, tagged with a stable EXTERNAL fill key (e.g. a broker/venue fill id)
	/// so the operation is idempotent across a post-commit RETRY. If a trade with the same
	/// <paramref name="externalTradeId"/> was already persisted, the insert is a no-op (the position effect was
	/// applied by that earlier committed call) and this method returns without applying again - collapsing a
	/// retried fill to exactly one persisted trade and one position effect (C2). Preserves the public
	/// three-argument contract (AAP 0.7.1) as an additive overload; everything else matches
	/// <see cref="RecordTradeAsync(long, decimal, decimal, CancellationToken)"/> (single gateway-owned
	/// transaction, per-portfolio advisory lock, durable single-apply).
	/// </summary>
	/// <param name="orderId">Order the fill executed against (<c>orders.order_id</c>).</param>
	/// <param name="qty">Executed quantity (NUMERIC(18,4)).</param>
	/// <param name="price">Executed price (NUMERIC(18,4)).</param>
	/// <param name="externalTradeId">Stable external fill identifier used as the idempotency key (<c>trades.external_trade_id</c>).</param>
	/// <param name="cancellationToken">Token used to cancel the database operations.</param>
	public Task RecordTradeAsync(long orderId, decimal qty, decimal price, long externalTradeId, CancellationToken cancellationToken = default)
		=> RecordTradeCoreAsync(orderId, qty, price, externalTradeId, cancellationToken);

	/// <summary>
	/// Shared implementation of the <c>RecordTradeAsync</c> overloads. Runs the WHOLE unit - portfolio
	/// resolution, per-portfolio advisory lock, trade INSERT, single-apply claim and position recompute - in
	/// ONE gateway-owned transaction. C3: the advisory lock is acquired BEFORE the trade is inserted, so the
	/// insert + claim + position update all happen under the SAME lock that <see cref="SubmitOrderAsync"/>
	/// takes; a concurrent submit therefore cannot acquire the lock and validate against a snapshot that is
	/// missing this (still-uncommitted) fill. When <paramref name="externalTradeId"/> is supplied, the insert
	/// is idempotent via <c>ON CONFLICT ... DO NOTHING</c> on the partial-unique external-fill index (C2).
	/// </summary>
	private async Task RecordTradeCoreAsync(long orderId, decimal qty, decimal price, long? externalTradeId, CancellationToken cancellationToken)
	{
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken);

		// Atomicity (F2, AAP 0.6.5): the trade INSERT and the position recompute run in ONE gateway-owned
		// transaction, so a trade row can never be committed without its position effect (nor vice versa).
		// The recalculation service enrols in this transaction - it neither begins nor commits it - and the
		// gateway commits once at the end.
		await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

		// C3 (TOCTOU fix): resolve the order's portfolio and acquire the per-portfolio advisory lock BEFORE
		// inserting the trade, so the entire insert + claim + position update runs under the SAME single-key
		// pg_advisory_xact_lock(bigint) that SubmitOrderAsync uses. Previously the trade was inserted first and
		// the lock was taken inside ApplyAsync AFTER the insert; a concurrent submit could acquire the lock in
		// between and validate against state that could not yet see this uncommitted fill. Taking the lock here,
		// up front, closes that window (ApplyAsync re-takes the same key re-entrantly - a harmless no-op).
		//
		// MAJOR#1 / MINOR#1 denormalization: the SAME up-front read also fetches the order's security_id, so
		// both the parent order's portfolio_id AND security_id are carried onto the trade row below
		// (trades.portfolio_id / trades.security_id). That lets the pre-trade commission reads use the
		// IX_Trades_portfolio / IX_Trades_security_executed covering indexes instead of a Trades -> Orders join
		// that scanned the whole table at scale. Reading them here, on the same connection+transaction that
		// inserts the trade, guarantees the copies equal the parent order's values (the Trades FKs re-assert
		// it); a missing order yields no row and fails fast with InvalidOperationException before anything is
		// written (no partial state).
		int portfolioId;
		int securityId;
		using (var orderCommand = new NpgsqlCommand(
			"SELECT portfolio_id, security_id FROM orders WHERE order_id = @order_id", connection, transaction))
		{
			orderCommand.Parameters.Add(new NpgsqlParameter("order_id", NpgsqlDbType.Bigint) { Value = orderId });
			// Fully read and dispose this reader before the next command runs on the same connection (no MARS);
			// the enclosing using-block disposes both the reader and the command before the advisory-lock read.
			await using var orderReader = await orderCommand.ExecuteReaderAsync(cancellationToken);
			if (!await orderReader.ReadAsync(cancellationToken))
				throw new InvalidOperationException($"SqlLegacyOrderGateway.RecordTradeAsync: order_id {orderId} not found");
			portfolioId = orderReader.GetInt32(0);
			securityId = orderReader.GetInt32(1);
		}

		using (var lockCommand = new NpgsqlCommand(
			"SELECT pg_advisory_xact_lock(@portfolio_id)", connection, transaction))
		{
			lockCommand.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Bigint) { Value = (long)portfolioId });
			await lockCommand.ExecuteNonQueryAsync(cancellationToken);
		}

		// Insert the fill. executed_date via now() at time zone 'utc' (UTC time source, AAP 0.6.4). When an
		// external fill key is supplied, the insert is idempotent: ON CONFLICT (external_trade_id) WHERE
		// external_trade_id IS NOT NULL DO NOTHING infers the PARTIAL unique index UQ_Trades_external_trade_id -
		// the WHERE predicate MUST be repeated for a partial index or Postgres raises "no unique or exclusion
		// constraint matching the ON CONFLICT specification". A conflict means the fill was already persisted
		// (and applied) by an earlier committed call, so the insert affects 0 rows and RETURNING yields none.
		// C1: qty/price are NOT read back here for the recompute; ApplyAsync derives the applied values straight
		// from the persisted trade row it claims, so nothing downstream trusts caller-supplied values.
		// The trade row also carries the denormalized (portfolio_id, security_id) resolved from the parent
		// order above (MAJOR#1 / MINOR#1), so the pre-trade commission reads can use the covering indexes
		// instead of a Trades -> Orders join.
		var insertSql = externalTradeId is null
			? "INSERT INTO trades (order_id, portfolio_id, security_id, qty, price, executed_date) " +
			  "VALUES (@order_id, @portfolio_id, @security_id, @qty, @price, now() at time zone 'utc') RETURNING trade_id"
			: "INSERT INTO trades (order_id, portfolio_id, security_id, qty, price, external_trade_id, executed_date) " +
			  "VALUES (@order_id, @portfolio_id, @security_id, @qty, @price, @external_trade_id, now() at time zone 'utc') " +
			  "ON CONFLICT (external_trade_id) WHERE external_trade_id IS NOT NULL DO NOTHING RETURNING trade_id";

		long tradeId;
		bool inserted;
		await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
		{
			insert.Parameters.Add(new NpgsqlParameter("order_id", NpgsqlDbType.Bigint) { Value = orderId });
			// Denormalized copies resolved from the parent order above (MAJOR#1 / MINOR#1): bound as INT.
			insert.Parameters.Add(new NpgsqlParameter("portfolio_id", NpgsqlDbType.Integer) { Value = portfolioId });
			insert.Parameters.Add(new NpgsqlParameter("security_id", NpgsqlDbType.Integer) { Value = securityId });
			// qty / price are NUMERIC(18,4): bind as decimal (never double/float) - AAP 0.6.4.
			insert.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Numeric) { Value = qty });
			insert.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = price });
			if (externalTradeId is not null)
				insert.Parameters.Add(new NpgsqlParameter("external_trade_id", NpgsqlDbType.Bigint) { Value = externalTradeId.Value });

			// No MARS on a single Npgsql connection: fully read and dispose this reader before ApplyAsync runs
			// its own commands on the same connection + transaction.
			await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
			inserted = await reader.ReadAsync(cancellationToken);
			tradeId = inserted ? reader.GetInt64(0) : 0L;
		}

		// C2 idempotency: an external-key conflict means this fill was ALREADY recorded and its position effect
		// already applied in that prior committed transaction. There is nothing to insert or apply now; commit
		// the (empty) transaction and return - a safe no-op that makes a post-commit retry exactly-once.
		if (!inserted)
		{
			await transaction.CommitAsync(cancellationToken);
			return;
		}

		// Single-apply invariant (AAP 0.6.5): the trg_Trades_PositionRecalc trigger was removed, so the gateway
		// is the sole applier and calls the recalculation service exactly once for this freshly-inserted trade,
		// on THIS connection + transaction and under the advisory lock taken above. ApplyAsync (C1) claims the
		// trade by (trade_id AND order_id) and derives qty/price from the persisted row, so the trade row and
		// the position write form one atomic, correctly-bound unit.
		await _positionRecalc.ApplyAsync(connection, transaction, orderId, tradeId, cancellationToken);

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
