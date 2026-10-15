using ErrorOr;

namespace NightTales.Domain.Episodes;

/// <summary>
/// The render part of the episode state machine (BD5; 00 §7): <c>story_approved</c> → <c>rendering</c> when a render is
/// requested, and from <c>rendering</c> either back to <c>story_approved</c> when the attempt fails or forwards to
/// <c>video_review</c> when its output has been stored. It is a partial of <see cref="Episode"/> like the plan-02 files
/// (<c>Episode.Story.cs</c>, <c>Episode.FactSheet.cs</c>), so the transitions of one area stay in one file (09.5.1
/// keeps <c>Episode.cs</c>, Part A D16).
/// </summary>
/// <remarks>
/// The two terminal transitions are idempotent for their own target state: the outbox and the Hangfire retries of
/// 09.6.3-T1/T2 are at least once, so a repeat of the same failure or the same ingest must answer <c>Success</c>
/// without a second write — the same convention <c>Episode.Story.cs</c> states for its own methods. Any other state is
/// 409 <see cref="InvalidStateCode"/>, which is what makes a render call on a <c>draft</c>, <c>story_review</c> or
/// <c>rendering</c> episode fail.
/// </remarks>
public sealed partial class Episode
{
    /// <summary>
    /// Starts a render of the approved story (BD5 step 1): <c>story_approved</c> → <c>rendering</c>.
    /// </summary>
    /// <param name="now">The command's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of any other state.</returns>
    public ErrorOr<Success> RequestRender(DateTimeOffset now)
    {
        if (Status != EpisodeStatus.StoryApproved)
        {
            return Error.Conflict(InvalidStateCode, "The episode cannot start a render in its current state.");
        }

        Status = EpisodeStatus.Rendering;
        UpdatedAt = now;

        return Result.Success;
    }

    /// <summary>
    /// Returns the episode to <c>story_approved</c> after a failed render (BD5, WT50): <c>rendering</c> →
    /// <c>story_approved</c>, so the creator can fix the story or retry without a re-approval. It is idempotent: an
    /// episode already back in <c>story_approved</c> answers <c>Success</c> without a write, because the failure path of
    /// 09.6.3-T1/T2 can be delivered more than once.
    /// </summary>
    /// <param name="now">The Worker's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of every other state.</returns>
    public ErrorOr<Success> RenderFailed(DateTimeOffset now) => MoveOutOfRendering(EpisodeStatus.StoryApproved, now);

    /// <summary>
    /// Moves the episode to <c>video_review</c> after a successful render (BD5, 00 §7): <c>rendering</c> →
    /// <c>video_review</c>, where the owner watches the output and approves or re-renders it. It is idempotent for the
    /// <c>video_review</c> target: the ingest of 09.6.3-T2 may resume after a crash and re-run its final steps (WT45),
    /// and the second call must not fail the job.
    /// </summary>
    /// <param name="now">The Worker's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of every other state.</returns>
    public ErrorOr<Success> RenderSucceeded(DateTimeOffset now) => MoveOutOfRendering(EpisodeStatus.VideoReview, now);

    /// <summary>
    /// The shared body of the two terminal render transitions: it writes the target status and the stamp, and it
    /// answers <c>Success</c> for a repeat of the target state.
    /// </summary>
    /// <param name="target">The status the transition moves to.</param>
    /// <param name="now">The Worker's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of every other state.</returns>
    private ErrorOr<Success> MoveOutOfRendering(EpisodeStatus target, DateTimeOffset now)
    {
        if (Status == target)
        {
            return Result.Success;
        }

        if (Status != EpisodeStatus.Rendering)
        {
            return Error.Conflict(InvalidStateCode, "The episode is not rendering, so its render cannot end.");
        }

        Status = target;
        UpdatedAt = now;

        return Result.Success;
    }
}
