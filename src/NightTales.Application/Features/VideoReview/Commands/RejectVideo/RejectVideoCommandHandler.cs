using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.VideoReview.Commands.RejectVideo;

/// <summary>
/// Records one rejection behind the episode row lock (09.7 plan §4 UC2/UC3, D5): the load, the state check,
/// the <c>If-Match</c> check and the transition run in one transaction, and the re-render the
/// <c>ReRender</c> target asks for is <em>not</em> part of it (WT3).
/// </summary>
/// <remarks>
/// <para>
/// The order is the contract. The tenant-filtered load answers 404 for an unknown episode and for another
/// user's (D10, never 403); the <em>state</em> check runs before the <c>If-Match</c> comparison, so a retry
/// after the state moved sees the 409 (<c>episode_invalid_state</c>, the "duplicate rejection" case of the
/// task's failure handling) rather than a stale-tag 412; a null <c>If-Match</c> skips only the comparison.
/// </para>
/// <para>
/// The answer carries the successor status and the target, because the route's status code depends on the
/// target (<c>story</c> → 200, <c>re_render</c> → 202) and that value has to survive the idempotency replay.
/// No <c>SaveChanges</c>, no transaction, no nested send: <c>UnitOfWorkBehavior</c> commits the moved episode,
/// the audit row and the <c>video_rejected.v1</c> outbox row together (D5, D11).
/// </para>
/// </remarks>
/// <param name="db">The tracked, tenant-filtered write model.</param>
/// <param name="clock">The command's clock reading; the domain never reads the clock itself.</param>
public sealed class RejectVideoCommandHandler(IAppDbContext db, IClock clock)
    : ICommandHandler<RejectVideoCommand, ErrorOr<VideoReviewResult>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<VideoReviewResult>> Handle(
        RejectVideoCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await db.LockEpisodeForUpdateAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == command.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        if (episode is null)
        {
            return Errors.NotFound();
        }

        // The state precedes If-Match (the ReopenStory precedent): rejecting twice is a conflict the client can
        // act on, not a stale-tag 412 — and a cleared tag must not move an episode that left video_review.
        if (episode.Status != EpisodeStatus.VideoReview)
        {
            return Error.Conflict(ErrorCodes.EpisodeInvalidState, "Only a video under review can be rejected.");
        }

        if (command.IfMatch is not null && !ETags.Matches(command.IfMatch, ETags.Episode(episode.Version)))
        {
            return Error.Conflict(ErrorCodes.EpisodeConflict, "The episode changed since it was read.");
        }

        var rejected = episode.RejectVideo(command.Reason, command.Target, clock.UtcNow);
        if (rejected.IsError)
        {
            return rejected.Errors;
        }

        return new VideoReviewResult(episode.Id, command.Target.SuccessorStatus(), command.Target);
    }
}
