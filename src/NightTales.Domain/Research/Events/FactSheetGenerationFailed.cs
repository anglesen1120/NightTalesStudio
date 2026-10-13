using NightTales.Domain.Common;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Domain.Research.Events;

/// <summary>
/// A fact-sheet run ended without a draft (02.5.3-T2, plan §5). The outbox stores it as
/// <c>fact_sheet_generation_failed.v1</c> so the UI stops showing a generating panel and can name the D8
/// outcome. The code and the ids only (D19): never a source text, a statement or a provider message.
/// </summary>
/// <param name="EpisodeId">The episode of the run.</param>
/// <param name="SeriesId">Its series.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="GenerationRunId">The run that failed.</param>
/// <param name="ErrorCode">The D8 job-outcome code (<c>fact_sheet_invalid_output</c>, <c>fact_sheet_moderated</c>, …).</param>
/// <param name="Trigger">Who asked for the run.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the job's clock.</param>
[OutboxEvent("fact_sheet_generation_failed.v1")]
public sealed record FactSheetGenerationFailed(
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    Guid GenerationRunId,
    string ErrorCode,
    GenerationTrigger Trigger,
    DateTimeOffset OccurredAt) : IDomainEvent;
