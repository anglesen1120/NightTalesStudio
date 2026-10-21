using NightTales.Domain.Common;

namespace NightTales.Domain.Stories.Generation.Events;

/// <summary>
/// A paid story generation was requested and the episode is now <c>story_generating</c> (02 D21). The outbox
/// stores it as <c>story_generation_requested.v1</c> in the command's transaction; the Worker dispatcher turns
/// it into <c>GenerateStoryJob(generationRunId)</c> (09.2.4).
/// </summary>
/// <param name="EpisodeId">The episode being generated.</param>
/// <param name="SeriesId">Its series.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="GenerationRunId">The <c>generation_runs</c> row the job runs.</param>
/// <param name="Trigger">Who asked: the user, the autopilot or the system.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("story_generation_requested.v1")]
public sealed record StoryGenerationRequested(
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    Guid GenerationRunId,
    GenerationTrigger Trigger,
    DateTimeOffset OccurredAt) : IDomainEvent;
