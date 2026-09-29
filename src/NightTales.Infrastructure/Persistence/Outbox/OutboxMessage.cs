namespace NightTales.Infrastructure.Persistence.Outbox;

/// <summary>
/// One pending domain event (00 overview §6 <c>outbox_messages</c>, 09 README D11). Infrastructure-only:
/// it is not a Domain type. The row is written by <c>OutboxSaveChangesInterceptor</c> (09.2.4-T1) inside
/// the same <c>SaveChanges</c> as its aggregate, and consumed by the Worker's <c>OutboxDispatcher</c>.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>The longest stored failure text (D11: safe text, never content).</summary>
    public const int ErrorMaxLength = 2_000;

    public Guid Id { get; set; }

    /// <summary>The stable event name (<c>story_approved.v1</c>), from <c>[OutboxEvent]</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// The opaque event payload as JSON text, stored in a <c>jsonb</c> column. Opaque on purpose: the
    /// dispatcher deserializes it by <see cref="Type"/>, so the mapping stays <c>string</c> (D8: the
    /// string/jsonb fallback is for opaque payloads).
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Null only for platform events; no FK, so the row survives user deletion (D9).</summary>
    public Guid? UserId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    public string? Error { get; set; }

    public string? TraceParent { get; set; }

    /// <summary>
    /// Stored-generation timestamp: the row is added *inside* <c>SaveChanges</c> by the outbox
    /// interceptor, i.e. after <c>AuditableSaveChangesInterceptor</c> has already run, so the column
    /// default (<c>now()</c>) fills it instead of a hand-written value (plan §3 maps <c>created_at</c>
    /// for every table).
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
