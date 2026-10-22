using NightTales.Domain.Common;

namespace NightTales.Domain.Publishing;

/// <summary>
/// A platform incident was raised (06 README D13/D14): the first <c>invalid_client</c>/<c>unauthorized_client</c> of
/// one OAuth client, stored as <c>platform_incident_raised.v1</c> in the same unit of work as the
/// <c>platform_incidents</c> row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Platform-wide, hence <see cref="PlatformEventAttribute"/>.</b> An incident has no user root: the row is not
/// <see cref="IUserOwned"/> and the message carries no <c>user_id</c>, so the outbox would refuse it as a bug in the
/// raising command without the attribute (09 README D10/D11).
/// </para>
/// <para>
/// <b>Ids and codes only.</b> The payload is D13's <c>{ incidentId, platform, oauthClientId, affectedConnections }</c>
/// plus the record's own <see cref="OccurredAt"/>; it never carries a token, a session URI or a channel title
/// (06 README D17).
/// </para>
/// <para>
/// <b>Written by the store, not by an aggregate.</b> <c>IPlatformIncidentStore.RaiseAsync</c> writes the
/// <c>outbox_messages</c> row directly, built to <c>OutboxSaveChangesInterceptor.ToRow</c>'s own contract: the raw
/// <c>ON CONFLICT DO NOTHING</c> insert is what decides "first raise", so there is no tracked <see cref="AggregateRoot"/>
/// holding an event for the interceptor to collect. The attribute still names the event in the
/// <c>OutboxEventRegistry</c>, which is what makes the name resolvable when the row is written and dispatched.
/// </para>
/// </remarks>
/// <param name="IncidentId">The <c>platform_incidents</c> row this raise produced (the open one on a repeat raise).</param>
/// <param name="Platform">The platform's wire value (<c>youtube</c>).</param>
/// <param name="OAuthClientId">The OAuth client whose grant was refused.</param>
/// <param name="AffectedConnections">How many <c>active</c> connections of this app are pinned to that client.</param>
/// <param name="OccurredAt">When the incident was first raised, from the caller's clock.</param>
[OutboxEvent("platform_incident_raised.v1")]
[PlatformEvent]
public sealed record PlatformIncidentRaised(
    Guid IncidentId,
    string Platform,
    string OAuthClientId,
    int AffectedConnections,
    DateTimeOffset OccurredAt) : IDomainEvent;
