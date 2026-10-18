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
    /// <para>
    /// The method only records the rejection and moves the state: starting the re-render itself is the
    /// Worker's outbox handler, outside the rejecting transaction (WT3). The reason is trimmed and bounded
    /// here as well as in the request validator, because the column is what the next reader resolves the
    /// owner's words through. A reason carrying a NUL (U+0000) is refused here as well: PostgreSQL rejects a
    /// NUL in <c>varchar</c> (SQLSTATE 22021), which would surface as an unhandled 500 rather than the 400 the
    /// request validator answers. Only U+0000 is refused; every other character the owner typed is kept.
    /// </para>
    /// <para>
    /// A <see cref="RejectTarget.Story"/> rejection reaches <c>story_review</c>, the same state
    /// <see cref="ReopenStory"/> reaches, so it clears <see cref="ApprovedStoryVersionId"/> and
    /// <see cref="ApprovedAt"/> the way that method does: the approved pointer alone selects the version
    /// (<c>RenderEpisodeCommandHandler</c>), and a stale pointer on a story back in review would make
    /// consumers read a version that is under review again. <see cref="RejectTarget.ReRender"/> keeps both,
    /// because the approved story is unchanged.
    /// </para>
    /// </remarks>
    /// <param name="reason">The owner's reason, 1–500 characters after trimming, without U+0000.</param>
    /// <param name="target">Where the rejection sends the episode.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns>
    /// <c>Success</c>; the 400 of a blank, over-long or NUL-carrying reason, or of an undefined target; or the
    /// 409 of every state but <c>video_review</c>.
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

        // SEC-1: PostgreSQL rejects a NUL in the varchar column (SQLSTATE 22021), so storing one would be an
        // unhandled 500, not the coded 400 the request validator answers. The check runs on `trimmed`, the exact
        // string this method stores, which is equivalent to checking the raw reason: `string.Trim()` removes only
        // `char.IsWhiteSpace` characters and U+0000 is not one. Only U+0000 is refused here; the request validator
        // applies the same rule and refuses no other character.
        if (trimmed.Contains('\0'))
        {
            return Error.Validation(
                "video_reject_reason_invalid",
                "A rejection reason cannot contain a NUL character.");
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

        // SEC-2: `story` returns the episode to `story_review`, the state `ReopenStory` reaches, so it clears
        // the approval pointer and its stamp exactly as that method does (Episode.Story.cs) — the approved
        // pointer alone selects the version (RenderEpisodeCommandHandler, 02 D21/T28). `re_render` keeps them:
        // the approved story is unchanged and a re-render renders it again.
        if (target == RejectTarget.Story)
        {
            ApprovedStoryVersionId = null;
            ApprovedAt = null;
        }

        Raise(new VideoRejected(Id, SeriesId, UserId, target, now));

        return Result.Success;
    }
}
