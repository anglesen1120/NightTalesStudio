using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Research.Sources.Commands.RemoveSource;

/// <summary>
/// <c>DELETE /api/v1/episodes/{episodeId}/sources/{sourceId}</c>: load the episode locked, apply the state gate,
/// find the source on the episode (404 when absent or another user's), delete it and raise
/// <c>SourceRemoved</c> on the episode, so the outbox row commits with the delete (plan §5.5).
/// </summary>
/// <remarks>
/// A <c>leased</c> task is cancelled by the foreign key's cascade deletion rather than a status update, so a fetcher
/// that posts a result for it gets 404 and drops the work.
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="clock">The command's clock reading.</param>
public sealed class RemoveSourceHandler(IAppDbContext db, IClock clock) : ICommandHandler<RemoveSourceCommand, ErrorOr<Deleted>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Deleted>> Handle(RemoveSourceCommand command, CancellationToken cancellationToken)
    {
        var loaded = await SourceGate.LoadEpisodeLockedAsync(db, command.EpisodeId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsError)
        {
            return loaded.Errors;
        }

        if (SourceGate.EnsureEditable(loaded.Value) is { IsError: true } gate)
        {
            return gate.Errors;
        }

        var source = await db.StorySources
            .SingleOrDefaultAsync(
                candidate => candidate.Id == command.SourceId && candidate.EpisodeId == command.EpisodeId,
                cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return Errors.NotFound();
        }

        db.StorySources.Remove(source);
        loaded.Value.RaiseSourceRemoved(source.Id, clock.UtcNow);

        return Result.Deleted;
    }
}
