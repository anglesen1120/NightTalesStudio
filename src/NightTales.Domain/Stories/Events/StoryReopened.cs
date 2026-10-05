using NightTales.Domain.Common;

namespace NightTales.Domain.Stories.Events;

/// <summary>
/// An approved story was reopened for review (02.3.2-T1); the outbox stores it as <c>story_reopened.v1</c> and the
/// Worker turns it into <c>EpisodeUpdated</c> for the UI (02 CR-7). Ids only (D19): never text.
/// </summary>
/// <param name="EpisodeId">The reopened episode.</param>
/// <param name="SeriesId">The series containing the episode.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="StoryVersionId">The story version whose approval was cleared.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("story_reopened.v1")]
public sealed record StoryReopened(
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    Guid StoryVersionId,
    DateTimeOffset OccurredAt) : IDomainEvent;
