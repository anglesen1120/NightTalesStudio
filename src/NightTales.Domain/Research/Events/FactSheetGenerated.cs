using NightTales.Domain.Common;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Domain.Research.Events;

/// <summary>
/// A fact-sheet run saved a draft version (02.5.3-T2, plan §5). The outbox stores it as
/// <c>fact_sheet_generated.v1</c> so the UI hears that the episode's fact sheet changed and refetches.
/// Ids and the version number only (D19): never a statement, a source text or a URL.
/// </summary>
/// <param name="FactSheetId">The saved version: the episode's <c>fs-&lt;id&gt;</c> ETag (D9).</param>
/// <param name="Version">The saved version number, <c>max(version) + 1</c> at creation.</param>
/// <param name="EpisodeId">The episode the version belongs to.</param>
/// <param name="SeriesId">Its series.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="GenerationRunId">The run that produced the version.</param>
/// <param name="Trigger">Who asked for the run.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the job's clock.</param>
[OutboxEvent("fact_sheet_generated.v1")]
public sealed record FactSheetGenerated(
    Guid FactSheetId,
    int Version,
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    Guid GenerationRunId,
    GenerationTrigger Trigger,
    DateTimeOffset OccurredAt) : IDomainEvent;
