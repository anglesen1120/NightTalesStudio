using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Sources.Queries.ListSources;

/// <summary>
/// <c>GET /api/v1/episodes/{episodeId}/sources</c>: a no-tracking projection through the tenant-filtered read model,
/// plus the two counts of the <c>limits</c> block. Another user's episode, an unknown id and a cancelled episode all
/// answer 404.
/// </summary>
/// <param name="readDb">The no-tracking read model; the D10 tenant filter applies.</param>
/// <param name="currentUser">The caller, for the rolling count.</param>
/// <param name="clock">The window's "now".</param>
/// <param name="options">The caps.</param>
public sealed class ListSourcesHandler(IReadDb readDb, ICurrentUser currentUser, IClock clock, SourcesOptions options)
    : IQueryHandler<ListSourcesQuery, ErrorOr<SourceListDto>>
{
    /// <summary>The UI copy-key prefix of a source error code.</summary>
    public const string ErrorMessageKeyPrefix = "sources.error.";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<SourceListDto>> Handle(ListSourcesQuery query, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return Errors.NotFound();
        }

        var exists = await readDb.Query<Episode>()
            .AnyAsync(episode => episode.Id == query.EpisodeId && episode.Status != EpisodeStatus.Cancelled, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            return Errors.NotFound();
        }

        var rows = await readDb.Query<StorySource>()
            .Where(source => source.EpisodeId == query.EpisodeId)
            .OrderBy(source => source.CreatedAt)
            .ThenBy(source => source.Id)
            .Select(source => new SourceListItemDto(
                source.Id,
                source.Kind,
                source.Url,
                source.RegistrableDomain,
                source.NoteText,
                source.Status,
                source.ErrorCode,
                null,
                source.HttpStatus,
                source.Title,
                source.FinalUrl,
                source.ContentType,
                source.TextChars,
                false,
                source.CreatedAt,
                source.FetchedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var windowStart = clock.UtcNow - SourcesOptions.UrlWindow;
        var recent = await readDb.Query<SourceCreation>()
            .CountAsync(creation => creation.UserId == userId && creation.CreatedAt > windowStart, cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(row => row.ErrorCode is null ? row : row with { ErrorMessageKey = ErrorMessageKeyPrefix + row.ErrorCode })
            .ToList();

        return new SourceListDto(items, new SourceLimitsDto(options.MaxPerEpisode, recent, options.MaxUrlSourcesPerUserPerDay));
    }
}
