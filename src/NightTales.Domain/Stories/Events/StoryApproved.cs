using NightTales.Domain.Common;

namespace NightTales.Domain.Stories.Events;

/// <summary>
/// A story version was approved (02.3.2-T1); the outbox stores it as <c>story_approved.v1</c> and the
/// Worker turns it into <c>EpisodeUpdated</c> for the UI (02 CR-7). Ids and approval source only (D19): never text.
/// </summary>
/// <param name="EpisodeId">The approved episode.</param>
/// <param name="SeriesId">The series containing the episode.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="StoryVersionId">The approved story version.</param>
/// <param name="ApprovalSource">Who approved the story.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("story_approved.v1")]
public sealed record StoryApproved(
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    Guid StoryVersionId,
    ApprovalSource ApprovalSource,
    DateTimeOffset OccurredAt) : IDomainEvent;
