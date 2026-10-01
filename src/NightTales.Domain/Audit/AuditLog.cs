using System.Net;
using NightTales.Domain.Common;

namespace NightTales.Domain.Audit;

/// <summary>
/// One append-only <c>audit_logs</c> row (00 overview §6, 09 README BD15, plan §3): the record that a
/// command, an auth handler, a job or an operator verb changed something. Written by the pipeline's
/// <c>AuditBehavior</c> inside the command's transaction or by <c>IAuditWriter</c>.
/// </summary>
/// <remarks>
/// <para>
/// The type is deliberately <em>not</em> <see cref="IUserOwned"/>: a row carries a
/// <c>subject_user_id</c> <em>and</em> an <c>actor_user_id</c>, which one tenant filter cannot express,
/// and the retention sweep reads across users. Readers filter explicitly — the only P HTTP reader
/// (09.3.6-T2) filters <c>subject_user_id = me AND visibility = user</c>.
/// </para>
/// <para>
/// There is no foreign key to <c>users</c>: a row outlives the account (13 §5.4 hashes the stored id).
/// There is also no update path and no cascade — the table only grows until 09.7.3-T1's retention sweep
/// deletes through <see cref="AuditRetentionRules"/>.
/// </para>
/// <para>
/// The <c>data</c> payload is safe text only: ids, codes and short labels, never story text, an e-mail
/// address, a token or a secret (09 README BD16).
/// </para>
/// </remarks>
public sealed class AuditLog : Entity, ICreatedAt
{
    private AuditLog()
    {
    }

    /// <summary>
    /// Who caused the change, or <c>null</c> for a pure system row (a webhook, a refund, a scheduled
    /// sweep) — the id is kept without a foreign key so it survives the account.
    /// </summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>
    /// How to read <see cref="ActorUserId"/> (09 README D10): <see cref="ActorType.User"/> when the
    /// actor is the subject, <see cref="ActorType.Admin"/> for a staff action on someone else's account,
    /// <see cref="ActorType.System"/> otherwise.
    /// </summary>
    public ActorType ActorType { get; private set; }

    /// <summary>
    /// Whose account the row is about, or <c>null</c> for a system row and for a failed login or CAPTCHA
    /// challenge on an unknown address (the attempted address is stored hashed, never plain).
    /// </summary>
    public Guid? SubjectUserId { get; private set; }

    /// <summary>
    /// The frozen wire <c>messageKey</c> of the change (<c>episode.story_approved</c>), always the value
    /// of the command's <c>[Audited]</c> attribute or the literal a hand-written writer passes.
    /// </summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>The area of the change; also the 24-month retention rule's key.</summary>
    public Category Category { get; private set; }

    /// <summary>Who may read the row: the owner's feed (<see cref="Visibility.User"/>) or staff only.</summary>
    public Visibility Visibility { get; private set; }

    /// <summary>The kind of entity the change concerns (<c>series</c>, <c>episode</c>), or <c>null</c>.</summary>
    public string? EntityType { get; private set; }

    /// <summary>The changed entity's id, or <c>null</c> when the change names no single row.</summary>
    public Guid? EntityId { get; private set; }

    /// <summary>
    /// The <c>data</c> payload as JSON text (column <c>data</c>, <c>jsonb</c>): the command's
    /// <c>IAuditData</c> summary, plus <c>actor</c> for an operator verb (<c>"system:operator"</c>).
    /// </summary>
    public string DataJson { get; private set; } = "{}";

    /// <summary>The caller's address for a security event; <c>null</c> for an ordinary command.</summary>
    public IPAddress? Ip { get; private set; }

    /// <summary>The caller's <c>User-Agent</c> for a security event; <c>null</c> for an ordinary command.</summary>
    public string? UserAgent { get; private set; }

    /// <summary>
    /// When the change happened, from the scope's clock. The factory receives it, so the audit
    /// interceptor never re-stamps a row whose instant was already fixed.
    /// </summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Records one row. The id is a UUIDv7 produced here (09 README D3) and the caller adds the row to
    /// its unit of work, which commits it inside the ambient transaction.
    /// </summary>
    /// <param name="actorUserId">The actor, or <c>null</c> for a system row.</param>
    /// <param name="actorType">How to read the actor (see <see cref="ActorType"/>).</param>
    /// <param name="subjectUserId">The affected account, or <c>null</c> when there is none.</param>
    /// <param name="action">The frozen wire <c>messageKey</c>.</param>
    /// <param name="category">The area of the change.</param>
    /// <param name="visibility">Who may read the row.</param>
    /// <param name="entityType">The changed entity's kind, or <c>null</c>.</param>
    /// <param name="entityId">The changed entity's id, or <c>null</c>.</param>
    /// <param name="dataJson">The <c>data</c> payload as JSON text; safe text only (09 README BD16).</param>
    /// <param name="ip">The caller's address for a security event, or <c>null</c>.</param>
    /// <param name="userAgent">The caller's <c>User-Agent</c> for a security event, or <c>null</c>.</param>
    /// <param name="occurredAt">The instant of the change, from the scope's clock.</param>
    /// <returns>The new row, tracked by the caller's context.</returns>
    public static AuditLog Record(
        Guid? actorUserId,
        ActorType actorType,
        Guid? subjectUserId,
        string action,
        Category category,
        Visibility visibility,
        string? entityType,
        Guid? entityId,
        string dataJson,
        IPAddress? ip,
        string? userAgent,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(dataJson);

        return new AuditLog
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = actorUserId,
            ActorType = actorType,
            SubjectUserId = subjectUserId,
            Action = action,
            Category = category,
            Visibility = visibility,
            EntityType = entityType,
            EntityId = entityId,
            DataJson = dataJson,
            Ip = ip,
            UserAgent = userAgent,
            CreatedAt = occurredAt,
        };
    }
}
