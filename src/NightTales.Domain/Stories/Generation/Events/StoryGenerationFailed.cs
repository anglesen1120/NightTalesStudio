using NightTales.Domain.Common;

namespace NightTales.Domain.Stories.Generation.Events;

/// <summary>
/// A story generation ended without a version and the episode went back to the status it had before (02 D21,
/// F04). A cancel raises it too, with <see cref="GenerationErrorCode.StoryGenerationCancelled"/> (D8, TL T20).
/// The outbox stores it as <c>story_generation_failed.v1</c>; consumers: the 02.8.3 pause triggers and the UI
/// error state.
/// </summary>
/// <param name="EpisodeId">The episode that stopped generating.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="GenerationRunId">The run that ended.</param>
/// <param name="ErrorCode">The D8 job code (or platform pause code) the run ended with.</param>
/// <param name="Trigger">Who asked for the run.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the caller's clock.</param>
[OutboxEvent("story_generation_failed.v1")]
public sealed record StoryGenerationFailed(
    Guid EpisodeId,
    Guid UserId,
    Guid GenerationRunId,
    GenerationErrorCode ErrorCode,
    GenerationTrigger Trigger,
    DateTimeOffset OccurredAt) : IDomainEvent;
