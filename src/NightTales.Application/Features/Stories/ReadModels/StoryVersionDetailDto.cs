using System.Text.Json;
using NightTales.Application.Features.Stories.Quality;
using NightTales.Application.Common;

namespace NightTales.Application.Features.Stories.ReadModels;

/// <summary>The stored story read model; never recomputes content or quality checks.</summary>
/// <param name="VersionId">The immutable version id.</param>
/// <param name="Version">The episode-local version number.</param>
/// <param name="Source">The source actor action, snake_case.</param>
/// <param name="CreatedAt">When the version was saved.</param>
/// <param name="CreatedBy">The actor: user for edited, system for generated or revised.</param>
/// <param name="Etag">The D9 strong story ETag.</param>
/// <param name="Content">The stored story object verbatim, or null when unreadable.</param>
/// <param name="Body">The stored derived body.</param>
/// <param name="Keywords">The stored keywords in story order.</param>
/// <param name="WordCount">The stored derived word count.</param>
/// <param name="QualityHints">Stored local issues, objective first (R7).</param>
/// <param name="AuditReport">The stored audit object verbatim, or null when unreadable.</param>
/// <param name="SimilarityScore">The stored similarity score, if measured.</param>
/// <param name="FactSheetId">The source fact sheet id, if any.</param>
public sealed record StoryVersionDetailDto(
    Guid VersionId,
    int Version,
    string Source,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string Etag,
    JsonElement? Content,
    string Body,
    IReadOnlyList<string> Keywords,
    int WordCount,
    IReadOnlyList<QualityHintDto> QualityHints,
    JsonElement? AuditReport,
    decimal? SimilarityScore,
    Guid? FactSheetId) : IHasETag
{
    /// <inheritdoc />
    string IHasETag.ETag => Etag;
}
