using System.Text.Json.Serialization;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.SeriesBible;

/// <summary>
/// The <c>bible.v1</c> document of a series bible as it travels between the client and
/// <c>GET</c>/<c>PUT /api/v1/series/{id}/bible</c> (02.6.3-T1, plan §3.1, §4 U1/U2): one type for the
/// response content and for the request body, so the OpenAPI document carries a single
/// <c>BibleContentV1</c> schema and a writer cannot drift from a reader.
/// </summary>
/// <remarks>
/// <para>
/// The members keep the stored document's snake_case names — not the camelCase of the envelope DTOs —
/// because those names are part of the <c>bible.v1</c> contract and are the field paths the
/// <c>bible_invalid_content</c> 400 reports (<c>episode_summaries[0].episode_id</c>).
/// </para>
/// <para>
/// <c>schema_version</c> is accepted and ignored: the backend always writes <c>bible.v1</c> (plan §4 U2).
/// A summary's <c>structure</c> and <c>approved_at</c> are server-derived and have no member here; the
/// handler fills both from the episode's approved story version.
/// </para>
/// <para>
/// Every member is nullable, so a partial body binds and reaches the Domain validator, which reports the
/// missing or empty field with its JSON path: a body without a narrator is the documented 400
/// <c>bible_invalid_content</c> on <c>narrator.persona</c>, never a binder error.
/// </para>
/// </remarks>
/// <param name="SchemaVersion">The client's schema version; ignored, the backend writes <c>bible.v1</c>.</param>
/// <param name="Narrator">The narrator; the validator requires a non-empty persona.</param>
/// <param name="RecurringCharacters">The recurring characters; at most <c>Bible:Limits:MaxCharacters</c>.</param>
/// <param name="Places">The recurring places; at most <c>Bible:Limits:MaxPlaces</c>.</param>
/// <param name="Rules">The standing rules; at most <c>Bible:Limits:MaxRules</c>.</param>
/// <param name="EpisodeSummaries">The summaries of approved episodes; editable, at most one per episode.</param>
public sealed record BibleContentV1(
    [property: JsonPropertyName("schema_version")] string? SchemaVersion,
    BibleNarratorV1? Narrator,
    [property: JsonPropertyName("recurring_characters")] IReadOnlyList<BibleCharacterV1>? RecurringCharacters,
    IReadOnlyList<BiblePlaceV1>? Places,
    IReadOnlyList<BibleRuleV1>? Rules,
    [property: JsonPropertyName("episode_summaries")] IReadOnlyList<BibleEpisodeSummaryV1>? EpisodeSummaries)
{
    /// <summary>
    /// The <c>schema_version</c> the backend writes whatever the client sent (plan §4 U2). It is the Domain
    /// constant itself, so the wire document and the stored one cannot drift apart.
    /// </summary>
    public const string SchemaVersionV1 = SeriesBibleContent.V1;

    /// <summary>
    /// The Domain content of this body: every member normalized (an absent list becomes empty, an absent
    /// string empty), and each summary carrying only the two members the client owns. The handler replaces
    /// the two server-derived members of a summary (structure and approved_at) before validation.
    /// </summary>
    /// <returns>The content the command validates and stores.</returns>
    public SeriesBibleContent ToDomain() => new(
        SchemaVersionV1,
        (Narrator ?? BibleNarratorV1.Empty).ToDomain(),
        Map(RecurringCharacters, static item => (item ?? BibleCharacterV1.Empty).ToDomain()),
        Map(Places, static item => (item ?? BiblePlaceV1.Empty).ToDomain()),
        Map(Rules, static item => (item ?? BibleRuleV1.Empty).ToDomain()),
        Map(EpisodeSummaries, static item => (item ?? BibleEpisodeSummaryV1.Empty).ToDomain()));

    /// <summary>
    /// The wire document of a stored content: a reader gets back what a writer sent. A summary serialises
    /// its id, title and summary only — structure and approved_at stay server-side.
    /// </summary>
    /// <param name="content">The stored <c>bible.v1</c> content.</param>
    /// <returns>The wire document.</returns>
    public static BibleContentV1 FromDomain(SeriesBibleContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return new BibleContentV1(
            content.SchemaVersion,
            BibleNarratorV1.FromDomain(content.Narrator),
            Map(content.RecurringCharacters, BibleCharacterV1.FromDomain),
            Map(content.Places, BiblePlaceV1.FromDomain),
            Map(content.Rules, BibleRuleV1.FromDomain),
            Map(content.EpisodeSummaries, BibleEpisodeSummaryV1.FromDomain));
    }

    /// <summary>
    /// Maps a list one item at a time, keeping the index of every item so a field path
    /// (<c>recurring_characters[3].name</c>) still names the element the client sent. A JSON <c>null</c>
    /// element is mapped to the type's empty item by the callers, so the validator reports it as an empty
    /// field instead of the read throwing.
    /// </summary>
    private static List<TTarget> Map<TSource, TTarget>(IReadOnlyList<TSource>? items, Func<TSource, TTarget> map)
        where TSource : class
        where TTarget : class
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        var mapped = new List<TTarget>(items.Count);
        foreach (var item in items)
        {
            mapped.Add(map(item));
        }

        return mapped;
    }
}

/// <summary>The narrator of a bible (<c>bible.v1</c> <c>narrator</c>).</summary>
/// <param name="Name">The optional name, ≤ <c>Bible:Limits:NarratorNameChars</c> characters.</param>
/// <param name="Persona">The persona; non-empty and ≤ <c>Bible:Limits:NarratorPersonaChars</c>.</param>
/// <param name="VoiceNotes">Free-form voice notes, ≤ <c>Bible:Limits:VoiceNotesChars</c>.</param>
public sealed record BibleNarratorV1(
    string? Name,
    string? Persona,
    [property: JsonPropertyName("voice_notes")] string? VoiceNotes)
{
    /// <summary>The narrator of a body that sent none: empty strings, so the validator reports the persona.</summary>
    public static BibleNarratorV1 Empty { get; } = new(null, null, null);

    /// <summary>The Domain narrator of this wire value.</summary>
    /// <returns>The narrator, its absent strings empty.</returns>
    public BibleNarrator ToDomain() => new(Name, Persona ?? string.Empty, VoiceNotes ?? string.Empty);

    /// <summary>The wire narrator of a stored one.</summary>
    /// <param name="narrator">The stored narrator.</param>
    /// <returns>The wire value.</returns>
    public static BibleNarratorV1 FromDomain(BibleNarrator narrator)
    {
        ArgumentNullException.ThrowIfNull(narrator);

        return new BibleNarratorV1(narrator.Name, narrator.Persona, narrator.VoiceNotes);
    }
}

/// <summary>A recurring character of a bible (<c>bible.v1</c> <c>recurring_characters[]</c>).</summary>
/// <param name="Id">
/// The server-assigned id (<c>ch1</c>, …). A blank or absent value means a new entry: the handler assigns the
/// next free id. A value that is present must be <c>ch&lt;n&gt;</c> and unique within the array.
/// </param>
/// <param name="Name">The name, ≤ <c>Bible:Limits:NameChars</c> characters.</param>
/// <param name="Description">The description, ≤ <c>Bible:Limits:DescriptionChars</c>.</param>
/// <param name="FirstEpisodeId">The episode the character first appeared in, or <see langword="null"/>.</param>
public sealed record BibleCharacterV1(
    string? Id,
    string? Name,
    string? Description,
    [property: JsonPropertyName("first_episode_id")] Guid? FirstEpisodeId)
{
    /// <summary>The empty character a <c>null</c> JSON element maps to, so the field index survives.</summary>
    public static BibleCharacterV1 Empty { get; } = new(null, null, null, null);

    /// <summary>The Domain character of this wire value.</summary>
    /// <returns>The character, its absent strings empty.</returns>
    public BibleCharacter ToDomain() =>
        new(Id ?? string.Empty, Name ?? string.Empty, Description ?? string.Empty, FirstEpisodeId);

    /// <summary>The wire character of a stored one.</summary>
    /// <param name="character">The stored character.</param>
    /// <returns>The wire value.</returns>
    public static BibleCharacterV1 FromDomain(BibleCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);

        return new BibleCharacterV1(character.Id, character.Name, character.Description, character.FirstEpisodeId);
    }
}

/// <summary>A recurring place of a bible (<c>bible.v1</c> <c>places[]</c>).</summary>
/// <param name="Id">
/// The server-assigned id (<c>pl1</c>, …). A blank or absent value means a new entry and the handler assigns the
/// next free id; a value that is present must be <c>pl&lt;n&gt;</c> and unique within the array.
/// </param>
/// <param name="Name">The name, ≤ <c>Bible:Limits:NameChars</c> characters.</param>
/// <param name="Description">The description, ≤ <c>Bible:Limits:DescriptionChars</c>.</param>
public sealed record BiblePlaceV1(string? Id, string? Name, string? Description)
{
    /// <summary>The empty place a <c>null</c> JSON element maps to, so the field index survives.</summary>
    public static BiblePlaceV1 Empty { get; } = new(null, null, null);

    /// <summary>The Domain place of this wire value.</summary>
    /// <returns>The place, its absent strings empty.</returns>
    public BiblePlace ToDomain() => new(Id ?? string.Empty, Name ?? string.Empty, Description ?? string.Empty);

    /// <summary>The wire place of a stored one.</summary>
    /// <param name="place">The stored place.</param>
    /// <returns>The wire value.</returns>
    public static BiblePlaceV1 FromDomain(BiblePlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        return new BiblePlaceV1(place.Id, place.Name, place.Description);
    }
}

/// <summary>A standing rule of a bible (<c>bible.v1</c> <c>rules[]</c>).</summary>
/// <param name="Id">
/// The server-assigned id (<c>rl1</c>, …). A blank or absent value means a new entry and the handler assigns the
/// next free id; a value that is present must be <c>rl&lt;n&gt;</c> and unique within the array.
/// </param>
/// <param name="Text">The rule, ≤ <c>Bible:Limits:RuleChars</c>.</param>
public sealed record BibleRuleV1(string? Id, string? Text)
{
    /// <summary>The empty rule a <c>null</c> JSON element maps to, so the field index survives.</summary>
    public static BibleRuleV1 Empty { get; } = new(null, null);

    /// <summary>The Domain rule of this wire value.</summary>
    /// <returns>The rule, its absent text empty.</returns>
    public BibleRule ToDomain() => new(Id ?? string.Empty, Text ?? string.Empty);

    /// <summary>The wire rule of a stored one.</summary>
    /// <param name="rule">The stored rule.</param>
    /// <returns>The wire value.</returns>
    public static BibleRuleV1 FromDomain(BibleRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new BibleRuleV1(rule.Id, rule.Text);
    }
}

/// <summary>
/// One summary of an approved episode (<c>bible.v1</c> <c>episode_summaries[]</c>).
/// </summary>
/// <remarks>
/// The client owns the title and the summary text only: <c>structure</c> and <c>approved_at</c> are
/// derived server-side from the episode's approved story version (plan §4 U2), so they are neither read
/// nor written here, and the Domain value this maps to carries the placeholder the handler replaces.
/// </remarks>
/// <param name="EpisodeId">The approved episode the summary is about; another series' or an unapproved episode is refused.</param>
/// <param name="Title">The title, ≤ <c>Bible:Limits:SummaryTitleChars</c> characters.</param>
/// <param name="Summary">The summary text, ≤ <c>Bible:Limits:SummaryChars</c> characters.</param>
public sealed record BibleEpisodeSummaryV1(
    [property: JsonPropertyName("episode_id")] Guid? EpisodeId,
    string? Title,
    string? Summary)
{
    /// <summary>The empty summary a <c>null</c> JSON element maps to, so the field index survives.</summary>
    public static BibleEpisodeSummaryV1 Empty { get; } = new(null, null, null);

    /// <summary>
    /// The Domain summary of this wire value, with the two server-derived members left at their
    /// placeholders: the handler replaces them from the stored content or the approved story version, and
    /// a summary whose episode is not approved keeps the placeholder and is refused by the validator.
    /// </summary>
    /// <returns>The summary the handler completes.</returns>
    public BibleEpisodeSummary ToDomain() =>
        new(EpisodeId ?? Guid.Empty, Title ?? string.Empty, Summary ?? string.Empty, StoryStructureTags.Empty, default);

    /// <summary>The wire summary of a stored one; the two server-derived members are intentionally dropped.</summary>
    /// <param name="summary">The stored summary.</param>
    /// <returns>The wire value.</returns>
    public static BibleEpisodeSummaryV1 FromDomain(BibleEpisodeSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new BibleEpisodeSummaryV1(summary.EpisodeId, summary.Title, summary.Summary);
    }
}
