using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Sources.Commands.AddSource;
using NightTales.Domain.Common;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Sources.Commands.RetrySource;

/// <summary>
/// <c>POST /api/v1/episodes/{episodeId}/sources/{sourceId}/retry</c>: 404 → state gate 409 → only a
/// <c>failed</c> source (409 <c>episode_invalid_state</c>) → the blocklist again (400
/// <c>source_domain_not_allowed</c>, SEC-2) → the 24 h cap again (429) → <c>pending</c> and a new
/// <c>fetch_tasks</c> row with <c>attempts = 0</c>.
/// </summary>
/// <remarks>
/// The partial unique index on <c>fetch_tasks.source_id</c> guarantees at most one live task per source. The blocklist
/// is data that changes through a reviewed PR, so a source added before a domain was blocked must not be re-queued
/// after it: the stored registrable domain is checked exactly as the add path checks it, and a blocked source stays
/// <c>failed</c> with no new task and no creation record.
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="currentUser">The caller.</param>
/// <param name="clock">The command's clock reading.</param>
/// <param name="options">The caps.</param>
/// <param name="blocklist">The blocked domains.</param>
public sealed class RetrySourceHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    SourcesOptions options,
    DomainBlocklist blocklist)
    : ICommandHandler<RetrySourceCommand, ErrorOr<SourceDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<SourceDto>> Handle(RetrySourceCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return Errors.NotFound();
        }

        var loaded = await SourceGate.LoadEpisodeLockedAsync(db, command.EpisodeId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsError)
        {
            return loaded.Errors;
        }

        var episode = loaded.Value;
        var source = await db.StorySources
            .SingleOrDefaultAsync(
                candidate => candidate.Id == command.SourceId && candidate.EpisodeId == command.EpisodeId,
                cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return Errors.NotFound();
        }

        if (SourceGate.EnsureEditable(episode) is { IsError: true } gate)
        {
            return gate.Errors;
        }

        if (source.Kind != StorySourceKind.Url || source.Status != StorySourceStatus.Failed)
        {
            return Error.Conflict(ErrorCodes.EpisodeInvalidState, "Only a failed url source can be retried.");
        }

        // The kind/status gate above guarantees a url source, whose registrable domain is NOT NULL (ck_story_sources_url).
        if (blocklist.IsBlocked(source.RegistrableDomain!))
        {
            return SourceErrors.SourceDomainNotAllowed;
        }

        var now = clock.UtcNow;
        var cap = await SourceGate.CheckUrlCapAsync(db, userId, options, now, cancellationToken).ConfigureAwait(false);
        if (cap.IsError)
        {
            return cap.Errors;
        }

        var retried = source.Retry(now);
        if (retried.IsError)
        {
            return retried.Errors;
        }

        db.FetchTasks.Add(FetchTask.CreateFetchSource(
            Guid.CreateVersion7(),
            source.UserId,
            source.EpisodeId,
            source.Id,
            source.NormalizedUrl!,
            source.RegistrableDomain!,
            now));
        SourceGate.RecordCreation(db, userId, now);

        return SourceDto.From(source);
    }
}
