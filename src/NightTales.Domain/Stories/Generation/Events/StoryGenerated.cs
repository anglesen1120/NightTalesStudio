using NightTales.Domain.Common;

namespace NightTales.Domain.Stories.Generation.Events;

/// <summary>
/// A story generation saved its version(s) and the episode is now <c>story_review</c> (02 D21). The outbox
/// stores it as <c>story_generated.v1</c> in the same transaction as the <c>story_versions</c> insert, so it is
/// never raised for a partial save. Consumers: the 02.6.1 bible proposal, the 02.8.2 gates and the 02.8.3
/// orchestration.
/// </summary>
/// <param name="EpisodeId">The generated episode.</param>
/// <param name="SeriesId">Its series.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="GenerationRunId">The run that produced the version.</param>
/// <param name="StoryVersionId">The version that is now the episode's current one.</param>
/// <param name="Revised">Whether the run saved more than one version (a revision happened).</param>
/// <param name="Trigger">Who asked for the run.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the caller's clock.</param>
[OutboxEvent("story_generated.v1")]
public sealed record StoryGenerated(
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    Guid GenerationRunId,
    Guid StoryVersionId,
    bool Revised,
    GenerationTrigger Trigger,
    DateTimeOffset OccurredAt) : IDomainEvent;
