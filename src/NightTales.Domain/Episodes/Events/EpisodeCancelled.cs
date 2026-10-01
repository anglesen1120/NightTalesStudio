using NightTales.Domain.Common;

namespace NightTales.Domain.Episodes.Events;

/// <summary>
/// An episode was cancelled (09 README D11/D16, F14). The outbox interceptor stores it as
/// <c>episode_cancelled.v1</c> in the same command transaction as the status change, and the Worker's
/// <c>EpisodeCancelledAssetRetentionHandler</c> marks the episode's assets for the 09.7.3 retention
/// sweep.
/// </summary>
/// <param name="EpisodeId">The cancelled episode, whose assets are scheduled for removal.</param>
/// <param name="SeriesId">The series it belongs to.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="PreviousStatus">
/// The status the episode was in before the cancel, so a consumer can tell "cancelled a draft" from
/// "cancelled an approved story" (02 §7: the credit refund depends on how far the flow got).
/// </param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("episode_cancelled.v1")]
public sealed record EpisodeCancelled(
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    EpisodeStatus PreviousStatus,
    DateTimeOffset OccurredAt) : IDomainEvent;
