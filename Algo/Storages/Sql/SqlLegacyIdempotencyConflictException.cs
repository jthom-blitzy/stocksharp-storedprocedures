namespace StockSharp.Algo.Storages.Sql;

/// <summary>
/// Raised by <see cref="SqlLegacyOrderGateway"/> when an idempotent call is replayed with the SAME
/// business key but a DIFFERENT payload than the row already persisted under that key.
/// </summary>
/// <remarks>
/// <para>
/// The gateway makes both order submission (keyed by <c>external_transaction_id</c>) and fill recording
/// (keyed by <c>external_trade_id</c>) idempotent: replaying a call with a key that already exists is a
/// no-op that returns the original outcome, so a client retry can never double-record. That contract only
/// holds while the replay is genuinely the <i>same</i> logical operation. If a caller reuses a key for a
/// materially different order or fill (a different order, quantity or price), silently ignoring the second
/// call would hide a real programming error and let the two callers disagree about what was persisted.
/// This typed exception surfaces that mismatch explicitly (fail loud, not silent) so the caller can correct
/// the key reuse; it deliberately does <b>not</b> derive from a transient/retryable exception, because the
/// conflict is deterministic and retrying it unchanged will always fail.
/// </para>
/// <para>
/// It extends <see cref="InvalidOperationException"/> so existing broad handlers keep working while callers
/// that care can catch this specific type. <see cref="EntityKind"/> and <see cref="IdempotencyKey"/> carry
/// the conflicting context for diagnostics.
/// </para>
/// </remarks>
public class SqlLegacyIdempotencyConflictException : InvalidOperationException
{
	/// <summary>
	/// The kind of entity whose idempotency key was reused with a different payload
	/// (for example <c>"order"</c> or <c>"trade"</c>).
	/// </summary>
	public string EntityKind { get; }

	/// <summary>
	/// The idempotency key that was replayed with a conflicting payload
	/// (the <c>external_transaction_id</c> of an order or the <c>external_trade_id</c> of a fill).
	/// </summary>
	public string IdempotencyKey { get; }

	/// <summary>
	/// Initializes a new instance of the <see cref="SqlLegacyIdempotencyConflictException"/> class.
	/// </summary>
	public SqlLegacyIdempotencyConflictException()
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="SqlLegacyIdempotencyConflictException"/> class
	/// with a specified error message.
	/// </summary>
	/// <param name="message">The message that describes the error.</param>
	public SqlLegacyIdempotencyConflictException(string message)
		: base(message)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="SqlLegacyIdempotencyConflictException"/> class
	/// with a specified error message and a reference to the inner exception that is the cause.
	/// </summary>
	/// <param name="message">The message that describes the error.</param>
	/// <param name="innerException">The exception that is the cause of the current exception.</param>
	public SqlLegacyIdempotencyConflictException(string message, Exception innerException)
		: base(message, innerException)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="SqlLegacyIdempotencyConflictException"/> class
	/// with the conflicting entity kind and idempotency key.
	/// </summary>
	/// <param name="entityKind">The kind of entity (for example <c>"order"</c> or <c>"trade"</c>).</param>
	/// <param name="idempotencyKey">The idempotency key that was replayed with a conflicting payload.</param>
	/// <param name="message">The message that describes the conflict.</param>
	public SqlLegacyIdempotencyConflictException(string entityKind, string idempotencyKey, string message)
		: base(message)
	{
		EntityKind = entityKind;
		IdempotencyKey = idempotencyKey;
	}
}
