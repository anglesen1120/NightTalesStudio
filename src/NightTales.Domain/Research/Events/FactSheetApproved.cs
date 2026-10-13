using NightTales.Domain.Common;

namespace NightTales.Domain.Research.Events;

/// <summary>
/// A draft version was approved (02.5.4-T1, plan §5): the version the story writer now reads for a true-crime
/// episode. The outbox stores it as <c>fact_sheet_approved.v1</c> so the UI hears the sheet changed state and
/// refetches it. Ids and the version number only (D19): never a statement, a source text or a URL.
/// </summary>
/// <param name="FactSheetId">The approved version: the episode's <c>fs-&lt;id&gt;</c> ETag (D9).</param>
/// <param name="Version">The approved version number.</param>
/// <param name="EpisodeId">The episode the version belongs to.</param>
/// <param name="SeriesId">Its series.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("fact_sheet_approved.v1")]
public sealed record FactSheetApproved(
    Guid FactSheetId,
    int Version,
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    DateTimeOffset OccurredAt) : IDomainEvent;
