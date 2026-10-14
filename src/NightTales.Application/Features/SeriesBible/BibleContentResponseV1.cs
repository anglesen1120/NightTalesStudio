using System.Text.Json.Serialization;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.SeriesBible;

/// <summary>
/// The <c>bible.v1</c> document of a <c>GET /api/v1/series/{id}/bible</c> response (02.6.3-T1, plan §3.1,
/// §4 U1): the stored document a client reads, with the two server-derived members of every summary
/// (<c>structure</c>, <c>approved_at</c>) spelled out. A client that wants to write the document back edits
/// it into the request shape <see cref="BibleContentV1"/>.
/// </summary>
/// <remarks>
/// <para>
/// The read and the write are two types on purpose: a summary of a request carries only what the client
/// owns, so the OpenAPI request component cannot advertise <c>structure</c> or <c>approved_at</c> as
/// writable, while the response component publishes both. Every other member is shared with
/// <see cref="BibleContentV1"/>, because a reader gets back exactly what a writer sent for them.
/// </para>
/// <para>
/// The members keep the stored document's snake_case names — not the camelCase of the envelope DTOs —
/// because those names are part of the <c>bible.v1</c> contract (plan §3.1).
/// </para>
/// </remarks>
/// <param name="SchemaVersion">Always <c>bible.v1</c>: the backend writes it, never a client or a model.</param>
/// <param name="Narrator">The narrator; a fresh bible's persona is empty until a first save.</param>
/// <param name="RecurringCharacters">The recurring characters, with their server-assigned <c>ch&lt;n&gt;</c> ids.</param>
/// <param name="Places">The recurring places, with their server-assigned <c>pl&lt;n&gt;</c> ids.</param>
/// <param name="Rules">The standing rules, with their server-assigned <c>rl&lt;n&gt;</c> ids.</param>
/// <param name="EpisodeSummaries">The summaries of earlier approved episodes, each with its derived structure and approval instant.</param>
public sealed record BibleContentResponseV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    BibleNarratorV1 Narrator,
    [property: JsonPropertyName("recurring_characters")] IReadOnlyList<BibleCharacterV1> RecurringCharacters,
    IReadOnlyList<BiblePlaceV1> Places,
    IReadOnlyList<BibleRuleV1> Rules,
    [property: JsonPropertyName("episode_summaries")] IReadOnlyList<BibleEpisodeSummaryResponseV1> EpisodeSummaries)
{
    /// <summary>
    /// The wire document of a stored content: every member of <c>bible.v1</c>, the summaries included.
    /// </summary>
    /// <param name="content">The stored <c>bible.v1</c> content.</param>
    /// <returns>The wire document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static BibleContentResponseV1 FromDomain(SeriesBibleContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return new BibleContentResponseV1(
            content.SchemaVersion,
            BibleNarratorV1.FromDomain(content.Narrator),
            Map(content.RecurringCharacters, BibleCharacterV1.FromDomain),
            Map(content.Places, BiblePlaceV1.FromDomain),
            Map(content.Rules, BibleRuleV1.FromDomain),
            Map(content.EpisodeSummaries, BibleEpisodeSummaryResponseV1.FromDomain));
    }

    /// <summary>Maps a stored list one item at a time, keeping the order the document stores.</summary>
    private static List<TTarget> Map<TSource, TTarget>(IReadOnlyList<TSource> items, Func<TSource, TTarget> map)
        where TSource : class
        where TTarget : class
    {
        var mapped = new List<TTarget>(items.Count);
        foreach (var item in items)
        {
            mapped.Add(map(item));
        }

        return mapped;
    }
}

/// <summary>
/// One summary of an approved episode as the read answers it (<c>bible.v1</c>
/// <c>episode_summaries[]</c>, plan §3.1): the title and text the client owns, plus the two members the
/// server derived from the episode's approved story version — <c>structure</c> and <c>approved_at</c> —
/// which a request never carries.
/// </summary>
/// <param name="EpisodeId">The approved episode the summary is about.</param>
/// <param name="Title">The episode title, ≤ <c>Bible:Limits:SummaryTitleChars</c> characters.</param>
/// <param name="Summary">The summary text, ≤ <c>Bible:Limits:SummaryChars</c> characters.</param>
/// <param name="Structure">The D3 structure tags of the episode's approved story version, plus its beats.</param>
/// <param name="ApprovedAt">The instant the episode was approved, ISO 8601 UTC.</param>
public sealed record BibleEpisodeSummaryResponseV1(
    [property: JsonPropertyName("episode_id")] Guid EpisodeId,
    string Title,
    string Summary,
    BibleStructureV1 Structure,
    [property: JsonPropertyName("approved_at")] DateTimeOffset ApprovedAt)
{
    /// <summary>The wire summary of a stored one, derived members included.</summary>
    /// <param name="summary">The stored summary.</param>
    /// <returns>The wire summary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="summary"/> is <see langword="null"/>.</exception>
    public static BibleEpisodeSummaryResponseV1 FromDomain(BibleEpisodeSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new BibleEpisodeSummaryResponseV1(
            summary.EpisodeId,
            summary.Title,
            summary.Summary,
            BibleStructureV1.FromDomain(summary.Structure),
            summary.ApprovedAt);
    }
}

/// <summary>
/// The <c>structure</c> block of a stored summary (<c>bible.v1</c>
/// <c>episode_summaries[].structure</c>, plan §3.1): the D3 tags of the episode's approved story version and
/// its body beats, as the series bible keeps them (<see cref="StoryStructureTags"/>).
/// </summary>
/// <remarks>
/// The members are spelled here rather than taken from the Domain record's own names: the document is
/// snake_case, and the Domain type carries no wire attribute (its stored shape comes from
/// <c>BibleJson.Options</c>, whose policy the DTO layer cannot rely on).
/// </remarks>
/// <param name="SubGenre">The <c>sub_genre</c> value, as stored by the D3 mapper.</param>
/// <param name="Opening">The <c>opening</c> value.</param>
/// <param name="TwistType">The <c>twist_type</c> value.</param>
/// <param name="Setting">The <c>setting</c> value.</param>
/// <param name="NarratorMode">The <c>narrator_mode</c> value.</param>
/// <param name="Beats">The body paragraphs' beats, in paragraph order.</param>
public sealed record BibleStructureV1(
    [property: JsonPropertyName("sub_genre")] string SubGenre,
    [property: JsonPropertyName("opening")] string Opening,
    [property: JsonPropertyName("twist_type")] string TwistType,
    [property: JsonPropertyName("setting")] string Setting,
    [property: JsonPropertyName("narrator_mode")] string NarratorMode,
    IReadOnlyList<string> Beats)
{
    /// <summary>The wire block of the tags a summary stores.</summary>
    /// <param name="tags">The stored structure tags.</param>
    /// <returns>The wire block.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tags"/> is <see langword="null"/>.</exception>
    public static BibleStructureV1 FromDomain(StoryStructureTags tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        return new BibleStructureV1(
            tags.SubGenre,
            tags.Opening,
            tags.TwistType,
            tags.Setting,
            tags.NarratorMode,
            tags.Beats);
    }
}
