using NightTales.Domain.Common;

namespace NightTales.Domain.Series.Events;

/// <summary>
/// A series accepted a new autopilot authorization version (02.8.3 plan §3.7): the outbox stores it as
/// <c>autopilot_authorization_accepted.v1</c> in the same transaction as the row, so the acceptance is
/// observable to the platform (09 README D11).
/// </summary>
/// <remarks>
/// <para>
/// Raised by <see cref="Series"/>'s accepting method and not by the authorization row itself:
/// <c>SeriesAutopilotAuthorization</c> is an <see cref="Entity"/>, and the outbox interceptor collects the
/// events of an <see cref="AggregateRoot"/> only.
/// </para>
/// <para>
/// Ids and the version number only (D-8, 09 README BD16, 02 D19): never the rendered text, never its hash,
/// never the settings snapshot. The handler resolves everything it needs from the rows this payload names.
/// Nothing consumes it in this wave — the authorization is already current by the time it is stored — exactly
/// as <c>SeriesBibleUpdated</c> is registered without a handler.
/// </para>
/// </remarks>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="SeriesId">The series the accepted authorization belongs to.</param>
/// <param name="AuthorizationId">The <c>series_autopilot_authorizations</c> row that was accepted.</param>
/// <param name="Version">The acceptance's 1-based version number.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("autopilot_authorization_accepted.v1")]
public sealed record AutopilotAuthorizationAccepted(
    Guid UserId,
    Guid SeriesId,
    Guid AuthorizationId,
    int Version,
    DateTimeOffset OccurredAt) : IDomainEvent;
