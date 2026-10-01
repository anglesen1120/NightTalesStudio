using NightTales.Domain.Common;

namespace NightTales.Domain.Episodes.Events;

/// <summary>
/// An episode was created (09 README D11/D16). The outbox interceptor stores it as
/// <c>episode_created.v1</c> in the same command transaction as the row, and the Worker dispatcher
/// delivers it once a handler exists — this wave registers none (09.5.1 plan §5), so a message of this
/// type is marked processed with nothing to run. It is the trigger 02's autopilot (02.8.3) and the
/// real-time hub (09.8.1) will subscribe to.
/// </summary>
/// <param name="EpisodeId">The created episode.</param>
/// <param name="SeriesId">The series it belongs to.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("episode_created.v1")]
public sealed record EpisodeCreated(Guid EpisodeId, Guid SeriesId, Guid UserId, DateTimeOffset OccurredAt) : IDomainEvent;
