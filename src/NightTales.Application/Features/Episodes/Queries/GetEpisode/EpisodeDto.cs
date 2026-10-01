using NightTales.Application.Common;
using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.Episodes.Queries.GetEpisode;

/// <summary>
/// One episode as <c>GET /api/v1/episodes/{id}</c> returns it (09.5.1 plan §5). The list item of
/// <c>GET /episodes</c> is the same shape without <c>brief</c>.
/// </summary>
/// <remarks>
/// <see cref="Etag"/> is the frozen D15 value <c>"ep-&lt;xmin&gt;"</c>, carried in the body exactly as the
/// <c>ETag</c> header carries it. The interface member is implemented explicitly so the JSON body has one
/// <c>etag</c> member and not a second, differently-cased copy from <see cref="IHasETag"/>.
/// </remarks>
/// <param name="Id">The episode id.</param>
/// <param name="SeriesId">The series the episode belongs to.</param>
/// <param name="Title">The optional title, ≤ 120 characters.</param>
/// <param name="Brief">The brief, 1,000 characters at most (00 §6).</param>
/// <param name="Status">The lifecycle state (00 §7, 09 README D16); never <c>cancelled</c> on this route.</param>
/// <param name="LengthTier">The story length tier.</param>
/// <param name="CreatedAt">When the episode was created, from the server's clock; the list's sort key.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="Etag">The quoted strong ETag clients send back in <c>If-Match</c> (09 README D15).</param>
public sealed record EpisodeDto(
    Guid Id,
    Guid SeriesId,
    string? Title,
    string Brief,
    EpisodeStatus Status,
    LengthTier LengthTier,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Etag) : IHasETag
{
    /// <inheritdoc />
    string IHasETag.ETag => Etag;
}
