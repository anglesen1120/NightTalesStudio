using NightTales.Domain.Common;

namespace NightTales.Domain.Episodes.Events;

/// <summary>
/// The owner rejected the rendered cut (09.7 plan §4 UC2/UC3, [RULED — README §B10 WT3]). The outbox
/// interceptor stores it as <c>video_rejected.v1</c> in the same command transaction as the status change;
/// in the Worker, <c>VideoReRenderHandler</c> starts a new render when <see cref="Target"/> is
/// <see cref="RejectTarget.ReRender"/> (never inside the rejecting transaction) and the real-time handler
/// publishes <c>EpisodeUpdated</c>.
/// </summary>
/// <remarks>
/// The reason text is deliberately absent: the owner's own words stay in
/// <c>episodes.video_reject_reason</c> (and the audit row), so the message the platform stores and fans out
/// carries ids and the target only.
/// </remarks>
/// <param name="EpisodeId">The rejected episode.</param>
/// <param name="SeriesId">The series it belongs to.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="Target">Where the rejection sent the episode: <c>story_review</c> or a re-render.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("video_rejected.v1")]
public sealed record VideoRejected(
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    RejectTarget Target,
    DateTimeOffset OccurredAt) : IDomainEvent;
