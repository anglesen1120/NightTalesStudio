using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Common;
using NightTales.Domain.Audit;
using NightTales.Domain.Series.Bible;

namespace NightTales.Application.Features.SeriesBible.Commands.UpdateBible;

/// <summary>
/// <c>PUT /api/v1/series/{id}/bible</c> (02.6.3-T1, plan §4 U2): the owner's <c>bible.v1</c> document
/// replaces the stored one as a new content version under the <c>sb-</c> ETag it was read from.
/// </summary>
/// <remarks>
/// <para>
/// The route is naturally idempotent: an unchanged body still saves a new version, and a replay with the old
/// ETag is a 412, so it takes no <c>Idempotency-Key</c> — exactly as <c>PUT /episodes/{id}/story</c> and the
/// fact-sheet edit do. The <c>If-Match</c> value is the header as received, and this command is where the
/// rule lives: absence or blank is 428 <c>if_match_required</c>, a stale, weak or malformed tag is 412
/// <c>bible_conflict</c> (the route's <c>RequireIfMatch</c> filter answers the 428 case first when the
/// endpoint applies it, as the fact-sheet and story routes do). Both are checked after the series is known
/// to be the caller's own, so a foreign series still answers 404.
/// </para>
/// <para>
/// The summary members the client owns travel in <see cref="SeriesBibleContent"/>; the two the server owns
/// (<c>structure</c>, <c>approved_at</c>) are placeholders here and are filled by the handler from the
/// episode's approved story version. <c>schema_version</c> is not part of this command: the backend writes
/// <c>bible.v1</c> regardless of the client's value.
/// </para>
/// <para>
/// The audit row's <c>data</c> (the series id, the new version and the four counts, never a name, a rule or
/// a summary, 02 D19) is known only after the handler ran, so the handler stores it in
/// <see cref="Outcome"/> and <see cref="ToAuditData"/> projects it.
/// </para>
/// </remarks>
/// <param name="SeriesId">The series, from the route.</param>
/// <param name="Content">The <c>bible.v1</c> content to store; the server re-derives the summary members it owns.
/// <see langword="null"/> when the request carried no <c>content</c> object at all, which the handler answers as
/// 400 <c>bible_invalid_content</c> after the 404/428/412 order — the endpoint passes it through untouched.</param>
/// <param name="IfMatch">The <c>If-Match</c> header value: the <c>"sb-&lt;version&gt;"</c> ETag the client read. A <c>*</c>, a weak tag or any other value is not that ETag and answers 412 <c>bible_conflict</c>.</param>
[Audited("series.bible_updated", Category.Episodes, Visibility.User)]
public sealed record UpdateBibleCommand(Guid SeriesId, SeriesBibleContent? Content, string? IfMatch)
    : ICommand<ErrorOr<UpdateBibleResult>>, IAuditData
{
    /// <summary>The version the handler saved; stays <see langword="null"/> for a failure.</summary>
    [JsonIgnore]
    public UpdateBibleOutcome? Outcome { get; set; }

    /// <summary>The row's projection: the series is the entity; ids and counts only (02 D19).</summary>
    /// <returns>The projection, or <see langword="null"/> when no version was saved.</returns>
    public AuditData? ToAuditData() =>
        Outcome is null
            ? null
            : new AuditData(
                null,
                "series",
                SeriesId,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["series_id"] = SeriesId.ToString("D", CultureInfo.InvariantCulture),
                    ["version"] = Outcome.Version,
                    ["characters"] = Outcome.Characters,
                    ["places"] = Outcome.Places,
                    ["rules"] = Outcome.Rules,
                    ["summaries"] = Outcome.Summaries,
                });
}

/// <summary>The saved bible: the version the write produced and the ETag that names it.</summary>
/// <param name="Version">The <c>series_bibles.version</c> after the replace (1-based, incremented by one).</param>
/// <param name="ETag">The strong ETag of that version, quotes included (<c>"sb-2"</c>): the client's next <c>If-Match</c>.</param>
/// <remarks>
/// The body is <c>{ version, etag }</c> (plan §4 U2). <c>etag</c> is named explicitly because the camel-case
/// policy of the Api's JSON options would spell the member <c>eTag</c>.
/// </remarks>
public sealed record UpdateBibleResult(int Version, [property: JsonPropertyName("etag")] string ETag) : IHasETag
{
    /// <inheritdoc />
    string IHasETag.ETag => ETag;
}

/// <summary>The audit-relevant facts of a saved bible edit (02 D19: ids and counts, no content).</summary>
/// <param name="Version">The new content version.</param>
/// <param name="Characters">How many recurring characters the saved content holds.</param>
/// <param name="Places">How many places it holds.</param>
/// <param name="Rules">How many rules it holds.</param>
/// <param name="Summaries">How many episode summaries it holds.</param>
public sealed record UpdateBibleOutcome(int Version, int Characters, int Places, int Rules, int Summaries);
