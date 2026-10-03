using NightTales.Domain.Common;

namespace NightTales.Domain.Research.Events;

/// <summary>
/// A source was removed from its episode; the outbox stores it as <c>source_removed.v1</c>. Ids only (D19).
/// </summary>
/// <param name="SourceId">The removed source.</param>
/// <param name="EpisodeId">The episode it belonged to.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("source_removed.v1")]
public sealed record SourceRemoved(Guid SourceId, Guid EpisodeId, Guid UserId, DateTimeOffset OccurredAt) : IDomainEvent;
