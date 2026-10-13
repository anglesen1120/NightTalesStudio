using NightTales.Domain.Common;

namespace NightTales.Domain.Research.Events;

/// <summary>
/// A hand edit saved a new draft version (02.5.4-T1, plan §5). The outbox stores it as
/// <c>fact_sheet_saved.v1</c> so the UI hears which version is current and refetches it. Ids and the version
/// number only (D19): never a statement, a source text or a URL.
/// </summary>
/// <param name="FactSheetId">The saved version: the episode's <c>fs-&lt;id&gt;</c> ETag (D9).</param>
/// <param name="Version">The saved version number, <c>max(version) + 1</c> at creation.</param>
/// <param name="EpisodeId">The episode the version belongs to.</param>
/// <param name="SeriesId">Its series.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("fact_sheet_saved.v1")]
public sealed record FactSheetSaved(
    Guid FactSheetId,
    int Version,
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    DateTimeOffset OccurredAt) : IDomainEvent;
