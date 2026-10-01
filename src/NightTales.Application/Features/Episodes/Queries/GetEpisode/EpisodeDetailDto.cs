using NightTales.Application.Common;
using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.Episodes.Queries.GetEpisode;

/// <summary>
/// One episode as <c>GET /api/v1/episodes/{id}</c> returns it (09.5.3 plan §5): the episode's own
/// columns, the actions the domain allows next and the three page blocks.
/// </summary>
/// <remarks>
/// <para>
/// Renamed from 09.5.1-T1's <c>EpisodeDto</c> when the route grew the blocks: one route, one DTO, no
/// second shape for the same resource. The list item of <c>GET /episodes</c>
/// (<c>EpisodeListItemDto</c>) stays the thinner shape.
/// </para>
/// <para>
/// <see cref="Etag"/> is the frozen D15 value <c>"ep-&lt;xmin&gt;"</c>, carried in the body exactly as
/// the <c>ETag</c> header carries it. The interface member is implemented explicitly so the JSON body
/// has one <c>etag</c> member and not a second, differently-cased copy from <see cref="IHasETag"/>.
/// </para>
/// <para>
/// Extension points, in this one DTO and this one projection — never a second route (09.5.3 plan §Steps
/// 6): <see cref="RenderJob"/> gains <c>images</c> from 03.4.2-T1, and 02.3.4-T2 adds
/// <c>currentStory</c>/<c>generation</c>. 06.2.1-T1 fills <see cref="Publications"/> from its own
/// sequential query; until then the member is <see langword="null"/> in P and carries no placeholder
/// value.
/// </para>
/// </remarks>
/// <param name="Id">The episode id.</param>
/// <param name="SeriesId">The series the episode belongs to.</param>
/// <param name="Title">The optional title, ≤ 120 characters.</param>
/// <param name="Brief">The brief, 1,000 characters at most (00 §6).</param>
/// <param name="LengthTier">The story length tier, snake_case on the wire (<c>short</c>, <c>standard</c>, <c>long</c>).</param>
/// <param name="Status">The lifecycle state (00 §7, 09 README D16), snake_case on the wire; never <c>cancelled</c> on this route.</param>
/// <param name="NextActions">
/// The actions the domain allows next (<see cref="EpisodeNextActions.For"/>), snake_case strings in the
/// array. A hint for the UI, never an authorization: every command re-checks its own transition.
/// </param>
/// <param name="Etag">The quoted strong ETag clients send back in <c>If-Match</c> (09 README D15).</param>
/// <param name="RenderJob">
/// The episode's latest render job, or <see langword="null"/> when it never rendered (09 README §B10 WT2:
/// the block stays <see langword="null"/> until 09.6.1-T1 lands <c>render_jobs</c>).
/// </param>
/// <param name="FinalVideo">The metadata of the video asset <c>episodes.final_video_asset_id</c> points at, or null; never a media URL.</param>
/// <param name="Publications">The publication summary, or <see langword="null"/> in P (06 owns the table).</param>
public sealed record EpisodeDetailDto(
    Guid Id,
    Guid SeriesId,
    string? Title,
    string Brief,
    LengthTier LengthTier,
    EpisodeStatus Status,
    IReadOnlyList<EpisodeNextAction> NextActions,
    string Etag,
    RenderJobSummaryDto? RenderJob,
    FinalVideoAssetDto? FinalVideo,
    PublicationsSummaryDto? Publications) : IHasETag
{
    /// <inheritdoc />
    string IHasETag.ETag => Etag;
}
