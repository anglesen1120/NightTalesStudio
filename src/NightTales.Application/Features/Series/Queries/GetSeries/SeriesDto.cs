using NightTales.Application.Common;
using NightTales.Domain.Common;
using NightTales.Domain.Series;

namespace NightTales.Application.Features.Series.Queries.GetSeries;

/// <summary>
/// One series as <c>GET /api/v1/series/{id}</c> returns it (09.4.1 plan §5). The list item of
/// <c>GET /series</c> is the same shape without <c>StoryPreset</c>.
/// </summary>
/// <remarks>
/// <see cref="Etag"/> is the frozen D15 value <c>"se-&lt;xmin&gt;"</c>, carried in the body exactly as
/// the <c>ETag</c> header carries it. The interface member is implemented explicitly so the JSON body
/// has one <c>etag</c> member and not a second, differently-cased copy from
/// <see cref="IHasETag"/>.
/// </remarks>
/// <param name="Id">The series id.</param>
/// <param name="Title">The title, 1–120 characters.</param>
/// <param name="Topic">The narrative topic.</param>
/// <param name="Language">The immutable content language.</param>
/// <param name="Aspect">The video aspect.</param>
/// <param name="VoiceId">The catalog voice the series reads its text with.</param>
/// <param name="StyleTemplateId">The catalog style template.</param>
/// <param name="StoryPreset">The 02 preset key, or <see langword="null"/>.</param>
/// <param name="AutopilotEnabled">Whether autopilot is on (02.8.3 writes it).</param>
/// <param name="ArchivedAt">When the series was archived, or <see langword="null"/>.</param>
/// <param name="CreatedAt">When the series was created, from the server's clock.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="Etag">The quoted strong ETag clients send back in <c>If-Match</c> (09 README D15).</param>
public sealed record SeriesDto(
    Guid Id,
    string Title,
    Topic Topic,
    Language Language,
    Aspect Aspect,
    string VoiceId,
    Guid StyleTemplateId,
    string? StoryPreset,
    bool AutopilotEnabled,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Etag) : IHasETag
{
    /// <inheritdoc />
    string IHasETag.ETag => Etag;
}
