using NightTales.Domain.Common;

namespace NightTales.Domain.Stories.Generation.Events;

/// <summary>
/// The generation run entered a new stage (02 D21). The outbox stores it as
/// <c>story_generation_stage_changed.v1</c>; the Worker turns it into <c>EpisodeUpdated</c> for the UI
/// (CR-7). At most one per stage change, never per token.
/// </summary>
/// <param name="EpisodeId">The episode being generated.</param>
/// <param name="SeriesId">Its series.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="GenerationRunId">The run that changed stage.</param>
/// <param name="Stage">The stage the run is now in.</param>
/// <param name="Trigger">Who asked for the run.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the caller's clock.</param>
[OutboxEvent("story_generation_stage_changed.v1")]
public sealed record StoryGenerationStageChanged(
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    Guid GenerationRunId,
    GenerationStage Stage,
    GenerationTrigger Trigger,
    DateTimeOffset OccurredAt) : IDomainEvent;
