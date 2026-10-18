using NightTales.Domain.Common;

namespace NightTales.Domain.Publishing;

/// <summary>
/// A channel connection's state changed (06.1.2-T2, 06 README D13): the outbox stores it as
/// <c>channel_connection_changed.v1</c> with ids and the state only — never a token, the channel title, the
/// Google channel id or the e-mail address.
/// </summary>
/// <param name="ConnectionId">The connection the change belongs to.</param>
/// <param name="UserId">The owner the message runs as (the outbox row's <c>user_id</c> as well).</param>
/// <param name="Status">The state the row is in after the change; stored as its snake_case wire value.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("channel_connection_changed.v1")]
public sealed record ChannelConnectionChanged(
    Guid ConnectionId,
    Guid UserId,
    ChannelConnectionStatus Status,
    DateTimeOffset OccurredAt) : IDomainEvent;
