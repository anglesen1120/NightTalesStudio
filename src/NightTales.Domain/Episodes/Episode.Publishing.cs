using ErrorOr;

namespace NightTales.Domain.Episodes;

/// <summary>
/// The publishing part of the episode state machine (00 §7, F15): <c>ready → scheduled</c> when the owner consents
/// to a publication of the episode (<see cref="MarkScheduled"/>), and the transitions named by 06.2.5-T1 alongside
/// it. It is a partial of <see cref="Episode"/> like the plan-02 files (<c>Episode.Story.cs</c>,
/// <c>Episode.VideoReview.cs</c>), so the transitions of one area stay in one file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives here and not in the publication.</b> F15 records that 00 §7 moves the episode
/// <c>ready → scheduled → published</c> "via publications" without naming an owner for the call; 06 README D12
/// resolves it as <c>06.2.2-T1</c> (consent, the primary caller) and <c>06.2.5-T1</c> (the idempotent safety net
/// while a processed upload waits for its go-live). Both call it inside the transaction that changes the
/// publication row, so the two aggregates are written or rolled back together.
/// </para>
/// <para>
/// <b>Idempotent for its own target state.</b> A second publication of the same episode — or a replayed
/// <c>publication_updated.v1</c> delivered at least once — must not fail the command that triggered it, so an
/// episode already <c>scheduled</c> answers <c>Success</c> without a write: no status move, no <c>updated_at</c>
/// bump and no event. Every other state but <c>ready</c> is 409 <see cref="InvalidStateCode"/>, the same
/// convention <c>Episode.Render.cs</c> states for its own methods.
/// </para>
/// <para>
/// <b>No outbox event.</b> Episode status moves raise no event of their own here: 06 README D13 lists
/// <c>publication_updated.v1</c> as the one event of this flow, 06.2.1's <c>plan.md:228</c> says "No new event
/// names", and that payload already carries both <c>publicationId</c> and <c>episodeId</c>, so the realtime
/// fan-out (06.2.5-T1's <c>PublicationUpdated</c>, ids only) tells the open tab to refetch
/// <c>GET /episodes/{id}</c> and see the new status. Adding an <c>episode_scheduled.v1</c> beside it would be a
/// second writer of the same fact.
/// </para>
/// </remarks>
public sealed partial class Episode
{
    /// <summary>
    /// Marks the episode as published-to-be (06 README D12, F15): <c>ready → scheduled</c>, in the same unit of
    /// work as the consent that scheduled it.
    /// </summary>
    /// <remarks>
    /// The method does not check that the publication exists, is the one this episode's current consent names, or
    /// belongs to the same owner: the caller loads both rows inside the tenant filter of 09 README D10 and has just
    /// written the consent, so re-reading the world here would only add round trips to a pure state move.
    /// </remarks>
    /// <param name="publicationId">
    /// The publication that is scheduling this episode (06.2.5-T1 declares the same parameter). The episode stores
    /// no reference to it — D12/F15 make the publication row the owner of that link, and the column set of
    /// <c>episodes</c> has no field for it — so the id is carried for the caller's correlation only and is not
    /// written.
    /// </param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns>
    /// <c>Success</c>; a no-op <c>Success</c> when the episode is already <c>scheduled</c>; or the 409 of every
    /// other state.
    /// </returns>
    public ErrorOr<Success> MarkScheduled(Guid publicationId, DateTimeOffset now)
    {
        _ = publicationId;

        // D12: a second publication of the same episode is a legitimate command (the first may have been
        // cancelled), so `scheduled` is the target state and not a failure.
        if (Status == EpisodeStatus.Scheduled)
        {
            return Result.Success;
        }

        if (Status != EpisodeStatus.Ready)
        {
            return Error.Conflict(InvalidStateCode, "The episode cannot be scheduled in its current state.");
        }

        Status = EpisodeStatus.Scheduled;
        UpdatedAt = now;

        return Result.Success;
    }
}
