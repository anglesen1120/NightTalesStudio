using System.Text.Json.Serialization;
using NightTales.Application.Common;

namespace NightTales.Application.Features.SeriesBible.Queries.GetBible;

/// <summary>
/// <c>GET /api/v1/series/{id}/bible</c> (02.6.3-T1, plan §4 U1): the series bible — its version, its
/// <c>bible.v1</c> content and the <c>"sb-&lt;version&gt;"</c> ETag the next save must send back — with
/// the pending proposal when one exists.
/// </summary>
/// <remarks>
/// The read model is read-only and untracked, so this route never creates the row it does not find (the
/// CQRS rule of plan §4 U1): a series whose bible row is missing is a data-integrity error, answered 500
/// <c>series_bible_missing</c> and counted, while an unknown or another user's series is the usual 404.
/// No episode state changes (plan §7).
/// </remarks>
/// <param name="SeriesId">The series whose bible is read; another user's id answers 404, like a missing one.</param>
/// <param name="Version">The <c>series_bibles.version</c> the content was read at; the ETag names it.</param>
/// <param name="ETag">The strong ETag of that version, quotes included (<c>"sb-2"</c>): what the client sends as <c>If-Match</c>. Wire name <c>etag</c> (the camel-case policy would spell the member <c>eTag</c>).</param>
/// <param name="Content">The stored <c>bible.v1</c> document, with each summary's derived <c>structure</c> and <c>approved_at</c>.</param>
/// <param name="Proposal">The pending proposal, or <see langword="null"/> when none is pending.</param>
public sealed record BibleDto(
    Guid SeriesId,
    int Version,
    [property: JsonPropertyName("etag")] string ETag,
    BibleContentResponseV1 Content,
    BibleProposalDto? Proposal) : IHasETag
{
    /// <inheritdoc />
    string IHasETag.ETag => ETag;
}
