using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Sources.Commands.AddSource;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Sources;

/// <summary>
/// The shared <see cref="ISourceAdder"/> (02.7.2-T2 R7): the add-source core of 02.5.1-T1, unchanged in order,
/// codes and writes, so <c>POST /sources</c> and the candidate add command run one write path.
/// </summary>
/// <remarks>
/// Moved out of <c>AddSourceHandler</c> without a change of behaviour; the existing <c>AddSourceHandlerTests</c> are
/// the oracle. Registered scoped, so both handlers resolve it over the same request's tracked context.
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="currentUser">The caller.</param>
/// <param name="clock">The command's clock reading.</param>
/// <param name="psl">The public-suffix list.</param>
/// <param name="blocklist">The blocked domains.</param>
/// <param name="options">The caps.</param>
public sealed class SourceAdder(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    IPublicSuffixList psl,
    DomainBlocklist blocklist,
    SourcesOptions options) : ISourceAdder
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<SourceDto>> AddAsync(
        Guid episodeId,
        string? url,
        string? noteText,
        CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return Errors.NotFound();
        }

        var locked = await LockEditableEpisodeAsync(episodeId, cancellationToken).ConfigureAwait(false);
        if (locked.IsError)
        {
            return locked.Errors;
        }

        return await AddLockedAsync(userId, locked.Value, url, noteText, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Episode>> LockEditableEpisodeAsync(
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        var loaded = await SourceGate.LoadEpisodeLockedAsync(db, episodeId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsError)
        {
            return loaded.Errors;
        }

        if (SourceGate.EnsureEditable(loaded.Value) is { IsError: true } gate)
        {
            return gate.Errors;
        }

        return loaded.Value;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<SourceDto>> AddLockedAsync(
        Guid userId,
        Episode episode,
        string? url,
        string? noteText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var now = clock.UtcNow;
        var isUrl = !string.IsNullOrWhiteSpace(url);

        SourceUrl? sourceUrl = null;
        if (isUrl)
        {
            var parsed = SourceUrl.Parse(url, psl);
            if (parsed.IsError)
            {
                return parsed.Errors;
            }

            sourceUrl = parsed.Value;
            if (blocklist.IsBlocked(sourceUrl.RegistrableDomain))
            {
                return SourceErrors.SourceDomainNotAllowed;
            }
        }

        var onEpisode = await db.StorySources
            .CountAsync(source => source.EpisodeId == episode.Id, cancellationToken)
            .ConfigureAwait(false);
        if (onEpisode >= options.MaxPerEpisode)
        {
            return Error.Conflict(
                ErrorCodes.SourceLimitReached,
                $"An episode holds at most {options.MaxPerEpisode} sources.",
                new Dictionary<string, object> { [ErrorMetadata.Limit] = options.MaxPerEpisode });
        }

        if (sourceUrl is not null)
        {
            var cap = await SourceGate.CheckUrlCapAsync(db, userId, options, now, cancellationToken).ConfigureAwait(false);
            if (cap.IsError)
            {
                return cap.Errors;
            }

            var duplicate = await db.StorySources
                .AnyAsync(
                    source => source.EpisodeId == episode.Id && source.NormalizedUrl == sourceUrl.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicate)
            {
                return Error.Conflict(ErrorCodes.SourceDuplicate, "This URL is already a source of the episode.");
            }
        }

        var sourceId = Guid.CreateVersion7();
        var created = sourceUrl is not null
            ? StorySource.CreateUrl(sourceId, episode.UserId, episode.Id, sourceUrl, now)
            : StorySource.CreateNote(sourceId, episode.UserId, episode.Id, noteText, now);
        if (created.IsError)
        {
            return created.Errors;
        }

        var source = created.Value;
        db.StorySources.Add(source);

        if (sourceUrl is not null)
        {
            db.FetchTasks.Add(FetchTask.CreateFetchSource(
                Guid.CreateVersion7(),
                episode.UserId,
                episode.Id,
                source.Id,
                sourceUrl.Value,
                sourceUrl.RegistrableDomain,
                now));

            SourceGate.RecordCreation(db, userId, now);
        }

        return SourceDto.From(source);
    }
}
