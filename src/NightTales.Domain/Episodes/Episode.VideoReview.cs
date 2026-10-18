using ErrorOr;
using NightTales.Domain.Episodes.Events;

namespace NightTales.Domain.Episodes;

/// <summary>
/// The video-review part of the episode state machine (00 §7, 09.7.1-T1): <c>video_review → ready</c> when
/// the owner accepts the rendered cut (<see cref="ApproveVideo"/>), and <c>video_review → story_review</c> or
/// <c>video_review → story_approved</c> when they reject it (<see cref="RejectVideo"/>,
/// [RULED — README §B10 WT3]). It is a partial of <see cref="Episode"/> like the plan-02 files
/// (<c>Episode.Story.cs</c>, <c>Episode.Render.cs</c>), so the transitions of one area stay in one file.
/// </summary>
/// <remarks>
/// Both methods take the clock reading (never the clock itself) and neither is idempotent: <c>video_review</c>
/// is the only state that accepts them and each one leaves it, so a repeat answers 409
/// <see cref="InvalidStateCode"/> — a client retry after the state moved must see the conflict, not a silent
/// success. The Application handler locks the row (<c>LockEpisodeForUpdateAsync</c>) before calling, so the
/// state the check reads is the state it writes.
/// </remarks>
public sealed partial class Episode
{
    /// <summary>The longest rejection reason the <c>varchar(500)</c> column stores (09.7 plan §3, CR-48).</summary>
    private const int MaxVideoRejectReasonLength = 500;

    /// <summary>
    /// When the owner accepted the cut (<c>ready_at</c>, CR-48), or <see langword="null"/> until then.
    /// </summary>
    public DateTimeOffset? ReadyAt { get; private set; }

    /// <summary>
    /// The trimmed reason of the last rejection (<c>video_reject_reason</c>, CR-48), or <see langword="null"/>
    /// until the first one. A later approve does not clear it: the last rejection stays readable while the
    /// replacement cut is reviewed (09.7.1-T1 "Notes").
    /// </summary>
    public string? VideoRejectReason { get; private set; }

    /// <summary>
    /// Accepts the rendered cut (09.7 plan §4 UC1, 00 §7): <c>video_review → ready</c>, stamping
    /// <see cref="ReadyAt"/> and raising <see cref="VideoApproved"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no final-video guard: <c>final_video_asset_id</c> is the ingest's invariant (09.6.3-T2) and
    /// the event carries the pointer as-is, even when it is <see langword="null"/>, because a hand-inserted
    /// <c>video_review</c> row must still be approvable (09.7.1-T1 "Failure handling": the route that 404s
    /// without an asset is <c>video-url</c>, not this one).
    /// </para>
    /// <para>
    /// Re-stamping the episode's live assets for the approved retention window (WT56) is the handler's job,
    /// not this method's: the aggregate does not hold an asset collection.
    /// </para>
    /// </remarks>
    /// <param name="now">The command's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of every other state (including <c>ready</c> itself).</returns>
    public ErrorOr<Success> ApproveVideo(DateTimeOffset now)
    {
        if (Status != EpisodeStatus.VideoReview)
        {
            return Error.Conflict(InvalidStateCode, "Only a video in review can be approved.");
        }

        Status = EpisodeStatus.Ready;
        ReadyAt = now;
        UpdatedAt = now;

        Raise(new VideoApproved(Id, SeriesId, UserId, FinalVideoAssetId, now));

        return Result.Success;
    }

    /// <summary>
    /// Rejects the rendered cut (09.7 plan §4 UC2/UC3, WT3): <c>video_review → story_review</c> for
    /// <see cref="RejectTarget.Story"/>, or <c>video_review → story_approved</c> for
    /// <see cref="RejectTarget.ReRender"/>. The trimmed reason is stored in <see cref="VideoRejectReason"/>
    /// and <see cref="VideoRejected"/> is raised.
    /// </summary>
    /// <remarks>
    /// The method only records the rejection and moves the state: starting the re-render itself is the
    /// Worker's outbox handler, outside the rejecting transaction (WT3). The reason is trimmed and bounded
    /// here as well as in the request validator, because the column is what the next reader resolves the
    /// owner's words through.
    /// </remarks>
    /// <param name="reason">The owner's reason, 1–500 characters after trimming.</param>
    /// <param name="target">Where the rejection sends the episode.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns>
    /// <c>Success</c>; the 400 of a blank or over-long reason, or of an undefined target; or the 409 of every
    /// state but <c>video_review</c>.
    /// </returns>
    public ErrorOr<Success> RejectVideo(string reason, RejectTarget target, DateTimeOffset now)
    {
        if (Status != EpisodeStatus.VideoReview)
        {
            return Error.Conflict(InvalidStateCode, "Only a video in review can be rejected.");
        }

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxVideoRejectReasonLength)
        {
            return Error.Validation(
                "video_reject_reason_invalid",
                "A rejection carries a reason of 1 to 500 characters.");
        }

        var next = target switch
        {
            RejectTarget.Story => EpisodeStatus.StoryReview,
            RejectTarget.ReRender => EpisodeStatus.StoryApproved,
            _ => (EpisodeStatus?)null,
        };

        if (next is null)
        {
            return Error.Validation(
                "video_reject_target_invalid",
                "A rejection sends the episode to the story editor or to a re-render.");
        }

        VideoRejectReason = trimmed;
        Status = next.Value;
        UpdatedAt = now;

        Raise(new VideoRejected(Id, SeriesId, UserId, target, now));

        return Result.Success;
    }
}
