using NightTales.Domain.Common;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Domain.Research.Events;

/// <summary>
/// A fact-sheet generation was requested (02.5.3-T2). The outbox stores it as
/// <c>fact_sheet_generation_requested.v1</c> in the command's transaction; the Worker dispatcher turns it into
/// <c>GenerateFactSheetJob(generationRunId)</c>, the run id being the only data argument (D19/D2).
/// </summary>
/// <remarks>
/// A fact-sheet run does not move its episode: <c>EpisodeStatus</c> has no fact-sheet value, D2's
/// "at most one active run per (episode, kind)" partial unique index is the state, and only a <c>story</c> run
/// changes an episode's status. This event is therefore the only thing the request writes beyond the
/// <c>generation_runs</c> row itself.
/// </remarks>
/// <param name="EpisodeId">The episode whose fetched sources the run reads.</param>
/// <param name="SeriesId">Its series.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="GenerationRunId">The <c>generation_runs</c> row the job runs.</param>
/// <param name="Trigger">Who asked: the user, the autopilot or the system.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("fact_sheet_generation_requested.v1")]
public sealed record FactSheetGenerationRequested(
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    Guid GenerationRunId,
    GenerationTrigger Trigger,
    DateTimeOffset OccurredAt) : IDomainEvent;
