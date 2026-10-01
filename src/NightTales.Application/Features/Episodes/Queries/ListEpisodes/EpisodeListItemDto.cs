using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.Episodes.Queries.ListEpisodes;

/// <summary>
/// One row of <c>GET /api/v1/episodes</c> (09.5.1 plan §5): the episode without its <c>brief</c>, which
/// the list never needs — picking an episode to continue does not read the whole brief.
/// </summary>
/// <remarks>
/// The <c>etag</c> member is carried per row even though the collection response has no <c>ETag</c>
/// header: a client that lists, then deletes one row, must be able to send the tag it just read
/// (09 README D15).
/// </remarks>
/// <param name="Id">The episode id.</param>
/// <param name="SeriesId">The series the episode belongs to.</param>
/// <param name="Title">The optional title, ≤ 120 characters.</param>
/// <param name="Status">The lifecycle state; never <c>cancelled</c>, which the list excludes.</param>
/// <param name="LengthTier">The story length tier.</param>
/// <param name="CreatedAt">When the episode was created; the list's sort key.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="Etag">The quoted strong ETag clients send back in <c>If-Match</c> (09 README D15).</param>
public sealed record EpisodeListItemDto(
    Guid Id,
    Guid SeriesId,
    string? Title,
    EpisodeStatus Status,
    LengthTier LengthTier,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Etag);
