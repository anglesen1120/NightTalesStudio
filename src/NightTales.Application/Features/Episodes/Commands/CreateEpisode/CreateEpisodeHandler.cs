using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

// The feature namespace `…Features.Episodes` contains the aggregate's namespace segment, so the entity
// is aliased the way the series handlers alias theirs (09.4.1-T1).
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Application.Features.Episodes.Commands.CreateEpisode;

/// <summary>
/// <c>POST /api/v1/series/{seriesId}/episodes</c> (09.5.1 plan §4 UC1/UC5, Design): load the series
/// through the tenant filter, refuse an archived one, build the aggregate and let the D4 pipeline commit
/// it together with its <c>episode_created.v1</c> outbox row.
/// </summary>
/// <remarks>
/// <para>
/// The load is the whole authorization: the D10 filter narrows <c>series</c> to the caller, so a series
/// that does not exist and another tenant's series answer the same 404, never a 403. The owner is read
/// from the loaded series (<c>series.UserId</c>) and not from <see cref="ICurrentUser"/>, which is what
/// makes UC5 — the autopilot run of 02.8.3, whose scope is pinned with
/// <see cref="ISystemContext.For"/> — take the run's user and nothing else.
/// </para>
/// <para>
/// An archived series is refused with 409 <c>series_archived</c> (F16): archiving stops new episodes but
/// never touches existing ones (09.4.1 plan §S2). The check is at load, so a series archived between the
/// load and the commit still accepts the episode (09.5.1 plan §Failure handling).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="clock">The command's clock reading.</param>
public sealed class CreateEpisodeHandler(IAppDbContext db, IClock clock)
    : ICommandHandler<CreateEpisodeCommand, ErrorOr<Guid>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Guid>> Handle(
        CreateEpisodeCommand command,
        CancellationToken cancellationToken)
    {
        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == command.SeriesId, cancellationToken)
            .ConfigureAwait(false);

        if (series is null)
        {
            // D10: another user's series and a series that does not exist are the same answer.
            return Errors.NotFound();
        }

        if (series.ArchivedAt is not null)
        {
            return Error.Conflict(
                ErrorCodes.SeriesArchived,
                "The series is archived; a new episode cannot be added to it.");
        }

        var episode = EpisodeAggregate.Create(
            series.UserId,
            series.Id,
            string.IsNullOrWhiteSpace(command.Title) ? null : command.Title.Trim(),
            command.Brief.Trim(),
            command.LengthTier,
            clock.UtcNow);

        db.Episodes.Add(episode);

        return episode.Id;
    }
}
