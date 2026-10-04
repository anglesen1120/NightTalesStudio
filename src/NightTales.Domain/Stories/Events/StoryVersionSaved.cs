using NightTales.Domain.Common;

namespace NightTales.Domain.Stories.Events;

/// <summary>
/// The owner saved an edited story version (02.3.1-T1); the outbox stores it as <c>story_version_saved.v1</c> and the
/// Worker turns it into <c>EpisodeUpdated</c> for the UI (02 CR-7). Ids and the version number only (D19): never text.
/// </summary>
/// <param name="EpisodeId">The edited episode.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="StoryVersionId">The new current version.</param>
/// <param name="Version">Its 1-based version number.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("story_version_saved.v1")]
public sealed record StoryVersionSaved(
    Guid EpisodeId,
    Guid UserId,
    Guid StoryVersionId,
    int Version,
    DateTimeOffset OccurredAt) : IDomainEvent;
