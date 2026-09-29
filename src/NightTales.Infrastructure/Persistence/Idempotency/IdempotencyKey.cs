namespace NightTales.Infrastructure.Persistence.Idempotency;

/// <summary>
/// One idempotency key of a paid POST (09 README D6, 00 overview §6). Infrastructure-only: the
/// <c>IdempotencyBehavior</c> (09.1.3-T2) owns every read and write of it, inside the command's
/// transaction.
/// </summary>
public sealed class IdempotencyKey
{
    /// <summary>The printable-ASCII key length limit (D6: 1–128 characters).</summary>
    public const int KeyMaxLength = 128;

    /// <summary>The length of the SHA-256 hex digest stored in <see cref="RequestHash"/>.</summary>
    public const int HashLength = 64;

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>SHA-256 hex of the message type name plus the canonical command JSON (D6).</summary>
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>CLR full name of the stored success value; null while the command is in flight.</summary>
    public string? ResponseType { get; set; }

    /// <summary>
    /// The stored success value as JSON text in a <c>jsonb</c> column. Opaque like the outbox payload:
    /// the behaviour serializes and deserializes it from <see cref="ResponseType"/>.
    /// </summary>
    public string? Response { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary><see cref="CreatedAt"/> plus 24 hours (D6).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
