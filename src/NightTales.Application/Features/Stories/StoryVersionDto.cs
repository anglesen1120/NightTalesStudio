using System.Text.Json;
using NightTales.Application.Common;
using NightTales.Application.Features.Stories.Quality;

namespace NightTales.Application.Features.Stories;

/// <summary>
/// One story version as the edit command answers it (02.3.1-T1): the stored <c>story.v1</c> document, the numbers
/// derived from it and the editor hints of the local check.
/// </summary>
/// <remarks>
/// <see cref="Etag"/> is the story ETag <c>"sv-&lt;version id&gt;"</c> (02 README D9), carried in the body exactly as the
/// <c>ETag</c> header carries it. The interface member is implemented explicitly so the JSON body has one <c>etag</c>
/// member (the same shape as <c>SeriesDto</c>). <see cref="Content"/> is the stored document, so it keeps the
/// snake_case members of <c>story.v1</c> the client sent.
/// </remarks>
/// <param name="VersionId">The version's id; the ETag is derived from it.</param>
/// <param name="Version">The 1-based version number within the episode.</param>
/// <param name="Source">The wire value of how the version came to be (<c>generated</c>, <c>revised</c>, <c>edited</c>).</param>
/// <param name="ParentVersionId">The version it was edited or revised from, or <see langword="null"/>.</param>
/// <param name="FactSheetId">The approved fact sheet the story was written from, or <see langword="null"/>.</param>
/// <param name="Title">The derived title.</param>
/// <param name="WordCount">The derived D4 token count.</param>
/// <param name="Keywords">The derived keyword list, in story order.</param>
/// <param name="CreatedAt">When the version was saved.</param>
/// <param name="Content">The stored <c>story.v1</c> document, with <c>schema_version</c> and <c>fact_sources</c>.</param>
/// <param name="QualityHints">The editor hints of the local check (02.3.4-T1), objective first.</param>
/// <param name="Etag">The story ETag of this version.</param>
public sealed record StoryVersionDto(
    Guid VersionId,
    int Version,
    string Source,
    Guid? ParentVersionId,
    Guid? FactSheetId,
    string Title,
    int WordCount,
    IReadOnlyList<string> Keywords,
    DateTimeOffset CreatedAt,
    JsonElement Content,
    IReadOnlyList<QualityHintDto> QualityHints,
    string Etag) : IHasETag
{
    /// <inheritdoc />
    string IHasETag.ETag => Etag;
}
