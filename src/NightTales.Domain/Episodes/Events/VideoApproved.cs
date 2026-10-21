using NightTales.Domain.Common;

namespace NightTales.Domain.Episodes.Events;

/// <summary>
/// The owner accepted the rendered cut (09.7 plan §4 UC1, 09.7.1-T1). The outbox interceptor stores it as
/// <c>video_approved.v1</c> in the same command transaction as the status change, and the Worker's
/// real-time handler (09.8.1-T1) publishes <c>EpisodeUpdated</c> for it.
/// </summary>
/// <param name="EpisodeId">The episode that is now <c>ready</c>.</param>
/// <param name="SeriesId">The series it belongs to.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="AssetId">
/// The accepted <c>assets</c> row (<c>final_video_asset_id</c>), or <see langword="null"/> for an episode
/// whose ingest never recorded one: approve deliberately adds no second guard for it (that invariant is
/// 09.6.3-T2's), so the payload carries whatever the aggregate holds.
/// </param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("video_approved.v1")]
public sealed record VideoApproved(
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    Guid? AssetId,
    DateTimeOffset OccurredAt) : IDomainEvent;
