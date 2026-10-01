using ErrorOr;
using Mediator;
using NightTales.Application.Common.Paging;
using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.Episodes.Queries.ListEpisodes;

/// <summary>
/// <c>GET /api/v1/episodes?seriesId=&amp;status=&amp;cursor=&amp;limit=</c> (09.5.1 plan §4 UC2, 09 README
/// D14): one keyset page of the caller's episodes, newest first, with cancelled ones excluded whatever the
/// filter says.
/// </summary>
/// <remarks>
/// The page's own bounds are the validator's (<c>limit</c> 1–100, <c>cursor</c> decodable) so the failure
/// carries the field the client must fix; the handler repeats them because it must decode the cursor to
/// build the keyset anyway.
/// </remarks>
/// <param name="SeriesId">Only this series' episodes, or <see langword="null"/> for every series.</param>
/// <param name="Status">Only this status, or <see langword="null"/> for every non-cancelled status.</param>
/// <param name="Cursor">The opaque <c>nextCursor</c> of the previous page, or <see langword="null"/>.</param>
/// <param name="Limit">The page size; the endpoint defaults an absent value to <see cref="DefaultLimit"/>.</param>
public sealed record ListEpisodesQuery(
    Guid? SeriesId,
    EpisodeStatus? Status,
    string? Cursor,
    int Limit = 20) : IQuery<ErrorOr<Page<EpisodeListItemDto>>>
{
    /// <summary>The page size when <c>?limit=</c> is absent (09 README D14).</summary>
    public const int DefaultLimit = 20;

    /// <summary>The largest accepted page size; above it the request is a 400 (09 README D14).</summary>
    public const int MaxLimit = 100;
}
