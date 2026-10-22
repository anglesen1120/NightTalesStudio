using NightTales.Domain.Common;

namespace NightTales.Domain.Publishing;

/// <summary>
/// A platform incident was cleared by an operator (06 README D13/D14): stored as
/// <c>platform_incident_cleared.v1</c> in the same unit of work as the row's <c>cleared_at</c>/<c>cleared_by</c>/<c>note</c>
/// and the held publications' reschedule.
/// </summary>
/// <remarks>
/// <para>
/// <b>Platform-wide, hence <see cref="PlatformEventAttribute"/></b>: like <see cref="PlatformIncidentRaised"/>, the
/// message has no user root and no <c>user_id</c>.
/// </para>
/// <para>
/// <b>Ids and codes only.</b> D13's payload is <c>{ incidentId, platform, oauthClientId }</c> plus the record's own
/// <see cref="OccurredAt"/>; the operator's name and note stay in the audit row
/// <c>publishing.incident_cleared</c>, category <c>support</c>, visibility <c>internal</c> — written by 06.2.3-T3's
/// clear command after its reschedule sweep, the only commit that knows the count), never here.
/// </para>
/// <para>
/// <b>Written by the store, not by an aggregate.</b> <c>IPlatformIncidentStore.ClearAsync</c> writes the
/// <c>outbox_messages</c> row directly, built to <c>OutboxSaveChangesInterceptor.ToRow</c>'s own contract: the clear is
/// one set-based <c>UPDATE … RETURNING</c>, so no tracked aggregate holds the event for the interceptor to collect.
/// </para>
/// </remarks>
/// <param name="IncidentId">The incident the clear closed.</param>
/// <param name="Platform">The platform's wire value (<c>youtube</c>).</param>
/// <param name="OAuthClientId">The OAuth client whose publications may be retried again.</param>
/// <param name="OccurredAt">When the clear was committed, from the caller's clock.</param>
[OutboxEvent("platform_incident_cleared.v1")]
[PlatformEvent]
public sealed record PlatformIncidentCleared(
    Guid IncidentId,
    string Platform,
    string OAuthClientId,
    DateTimeOffset OccurredAt) : IDomainEvent;
