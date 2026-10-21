using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

// The feature namespace `…Features.Episodes` contains the aggregate's namespace segment, so the entity
// is aliased the way the series handlers alias theirs (09.4.1-T1).
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Application.Features.Episodes.Commands.DeleteEpisode;

/// <summary>
/// <c>DELETE /api/v1/episodes/{id}</c> (09.5.1 plan §4 UC4, Design): load through the tenant filter,
/// compare <c>If-Match</c>, then let the aggregate cancel the episode.
/// </summary>
/// <remarks>
/// <para>
/// The order is the contract's: 404 before 412 before 409. A caller who did not read the row must not
/// learn anything about it, a stale tag must not be told why the episode refused the cancel, and the
/// aggregate's own <c>episode_invalid_state</c> is the last word.
/// </para>
/// <para>
/// An already cancelled episode is a 404, not a second 204 (09.5.1 plan §Failure handling, F14): the
/// cancel is not idempotent, so the route answers the same 404 a missing row does.
/// </para>
/// <para>
/// The load goes through the tracked context on purpose: <c>Cancel</c> changes <c>status</c> and
/// <c>updated_at</c>, so a concurrent generate/render command (02's
/// <c>LockEpisodeForUpdateAsync</c>) makes the commit fail on <c>xmin</c> and the loser is answered
/// 409 <c>concurrency_conflict</c> by the unit of work.
/// </para>
/// <para>
/// <c>If-Match</c> is optional on this route, and "absent" means exactly <see langword="null"/>: the
/// endpoint binds a missing header to <see langword="null"/> and a present one — empty or whitespace
/// included — to its raw value. Every present value is compared, so a present-but-empty header is a 412
/// <c>episode_conflict</c>, while a request without the header cancels (CR-1, [DECISION] 09.5.1 plan
/// §Notes).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="clock">The command's clock reading.</param>
public sealed class DeleteEpisodeHandler(IAppDbContext db, IClock clock)
    : ICommandHandler<DeleteEpisodeCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        DeleteEpisodeCommand command,
        CancellationToken cancellationToken)
    {
        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == command.Id, cancellationToken)
            .ConfigureAwait(false);

        if (episode is null || episode.Status == EpisodeStatus.Cancelled)
        {
            return Errors.NotFound();
        }

        // Only an absent header skips the precondition (CR-1): every present value — including the empty
        // one a client sends to mean "any version" — goes through ETags.Matches, which refuses `*`, a weak
        // tag, a comma-separated list and a stale tag alike. A present-but-empty `If-Match` therefore
        // answers 412 episode_conflict instead of silently cancelling the episode.
        if (command.IfMatch is not null
            && !ETags.Matches(command.IfMatch, ETags.Episode(episode.Version)))
        {
            // 412 through the code catalogue (09 README D7/D15, D17). Unlike PATCH /series/{id} there is
            // no route filter: the header is optional here, so absent is not a 428.
            return Error.Failure(
                ErrorCodes.EpisodeConflict,
                "The episode changed since it was read; read it again and retry.");
        }

        return episode.Cancel(clock.UtcNow);
    }
}
