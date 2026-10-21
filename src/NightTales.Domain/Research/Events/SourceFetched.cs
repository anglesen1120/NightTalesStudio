using NightTales.Domain.Common;

namespace NightTales.Domain.Research.Events;

/// <summary>
/// A url source's text was fetched and stored (02 D10, 02.5.2-T2 raises it from the result route): the outbox stores it
/// as <c>source_fetched.v1</c> so the UI hears about the episode's new source state. Ids only (D19).
/// </summary>
/// <param name="SourceId">The fetched source.</param>
/// <param name="EpisodeId">The episode it belongs to.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("source_fetched.v1")]
public sealed record SourceFetched(Guid SourceId, Guid EpisodeId, Guid UserId, DateTimeOffset OccurredAt) : IDomainEvent;
