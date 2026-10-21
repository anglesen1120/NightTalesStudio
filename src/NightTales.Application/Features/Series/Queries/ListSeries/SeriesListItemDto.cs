using NightTales.Domain.Common;
using NightTales.Domain.Series;

namespace NightTales.Application.Features.Series.Queries.ListSeries;

/// <summary>
/// One row of <c>GET /api/v1/series</c> (09.4.1 plan §5): the series DTO without <c>StoryPreset</c>,
/// which the list never needs — picking a series to continue in does not read its preset.
/// </summary>
/// <remarks>
/// The <c>etag</c> member is carried per row even though the collection response has no <c>ETag</c>
/// header: a client that lists, then patches one row, must be able to send the tag it just read
/// (09 README D15).
/// </remarks>
/// <param name="Id">The series id.</param>
/// <param name="Title">The title, 1–120 characters.</param>
/// <param name="Topic">The narrative topic.</param>
/// <param name="Language">The content language.</param>
/// <param name="Aspect">The video aspect.</param>
/// <param name="VoiceId">The catalog voice the series reads its text with.</param>
/// <param name="StyleTemplateId">The catalog style template.</param>
/// <param name="AutopilotEnabled">Whether autopilot is on (02.8.3 writes it).</param>
/// <param name="ArchivedAt">When the series was archived, or <see langword="null"/>.</param>
/// <param name="CreatedAt">When the series was created; the list's sort key.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="Etag">The quoted strong ETag clients send back in <c>If-Match</c> (09 README D15).</param>
public sealed record SeriesListItemDto(
    Guid Id,
    string Title,
    Topic Topic,
    Language Language,
    Aspect Aspect,
    string VoiceId,
    Guid StyleTemplateId,
    bool AutopilotEnabled,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Etag);
