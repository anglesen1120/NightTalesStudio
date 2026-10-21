using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Domain.Series.Bible;

/// <summary>
/// The <c>bible.v1</c> content of one series (02.6.3 plan §3.1): the narrator, the recurring characters and
/// places, the standing rules and the summaries of earlier approved episodes.
/// </summary>
/// <remarks>
/// The document is stored verbatim in <c>series_bibles.content</c>, so its wire shape is a contract: snake_case
/// property names (the plan's example) and no member outside it. <c>schema_version</c> is written by the
/// backend only — a client that sends one is ignored (plan §4 U2).
/// </remarks>
/// <param name="SchemaVersion">Always <see cref="V1"/>; written by the backend, never by a client or a model.</param>
/// <param name="Narrator">The narrator; required by the validator, and never changed by a proposal (plan §4 U4).</param>
/// <param name="RecurringCharacters">The recurring characters, at most <c>Bible:Limits:MaxCharacters</c>.</param>
/// <param name="Places">The recurring places, at most <c>Bible:Limits:MaxPlaces</c>.</param>
/// <param name="Rules">The series' standing rules, at most <c>Bible:Limits:MaxRules</c>.</param>
/// <param name="EpisodeSummaries">The summaries of earlier approved episodes; unbounded (the prompt keeps the newest 50).</param>
public sealed record SeriesBibleContent(
    string SchemaVersion,
    BibleNarrator Narrator,
    IReadOnlyList<BibleCharacter> RecurringCharacters,
    IReadOnlyList<BiblePlace> Places,
    IReadOnlyList<BibleRule> Rules,
    IReadOnlyList<BibleEpisodeSummary> EpisodeSummaries)
{
    /// <summary>The one schema version the backend writes (<c>bible.v1</c>, plan §3.1).</summary>
    public const string V1 = "bible.v1";

    /// <summary>
    /// The empty bible a series is created with (<c>SeriesBible.CreateDefault</c>): no narrator name, an empty
    /// persona and no entries. It is the starting point of every series, not a valid PUT body — the validator
    /// refuses an empty persona — so nothing validates it on the create path.
    /// </summary>
    public static SeriesBibleContent Empty { get; } =
        new(V1, new BibleNarrator(null, string.Empty, string.Empty), [], [], [], []);
}

/// <summary>The narrator of a series.</summary>
/// <param name="Name">The narrator's name, if any (≤ <c>Bible:Limits:NarratorNameChars</c>).</param>
/// <param name="Persona">The persona text; required and non-empty (≤ <c>Bible:Limits:NarratorPersonaChars</c>).</param>
/// <param name="VoiceNotes">Free-form voice notes (≤ <c>Bible:Limits:VoiceNotesChars</c>).</param>
public sealed record BibleNarrator(string? Name, string Persona, string VoiceNotes);

/// <summary>
/// A recurring character. The id is server-assigned and stable (<c>ch1</c>, <c>ch2</c>, …): it is never
/// renumbered and never reused (02.6.3 plan §3.1).
/// </summary>
/// <param name="Id">The server-assigned id (<c>ch&lt;n&gt;</c>).</param>
/// <param name="Name">The character's name (≤ <c>Bible:Limits:NameChars</c>).</param>
/// <param name="Description">The description (≤ <c>Bible:Limits:DescriptionChars</c>).</param>
/// <param name="FirstEpisodeId">The episode the character first appeared in, if known.</param>
public sealed record BibleCharacter(string Id, string Name, string Description, Guid? FirstEpisodeId);

/// <summary>A recurring place; the id is server-assigned (<c>pl1</c>, …) and never reused.</summary>
/// <param name="Id">The server-assigned id (<c>pl&lt;n&gt;</c>).</param>
/// <param name="Name">The place's name (≤ <c>Bible:Limits:NameChars</c>).</param>
/// <param name="Description">The description (≤ <c>Bible:Limits:DescriptionChars</c>).</param>
public sealed record BiblePlace(string Id, string Name, string Description);

/// <summary>A standing rule of the series; the id is server-assigned (<c>rl1</c>, …) and never reused.</summary>
/// <param name="Id">The server-assigned id (<c>rl&lt;n&gt;</c>).</param>
/// <param name="Text">The rule text (≤ <c>Bible:Limits:RuleChars</c>).</param>
public sealed record BibleRule(string Id, string Text);

/// <summary>
/// The summary of one earlier approved episode (02.6.3 plan §3.1). <see cref="Structure"/> and
/// <see cref="ApprovedAt"/> are server-derived: the client edits only the title and the summary text, and any
/// value it sends for the other two is ignored (plan §4 U2).
/// </summary>
/// <param name="EpisodeId">The episode the summary belongs to; one summary per episode (plan §3.2).</param>
/// <param name="Title">The episode title (≤ <c>Bible:Limits:SummaryTitleChars</c>).</param>
/// <param name="Summary">The summary text (≤ <c>Bible:Limits:SummaryChars</c>).</param>
/// <param name="Structure">The D3 structure tags of the episode's approved story version, plus its beats.</param>
/// <param name="ApprovedAt">The instant the episode was approved.</param>
public sealed record BibleEpisodeSummary(
    Guid EpisodeId,
    string Title,
    string Summary,
    StoryStructureTags Structure,
    DateTimeOffset ApprovedAt);

/// <summary>
/// The single canonical JSON shape of the two <c>jsonb</c> columns of <c>series_bibles</c> (02.6.3 plan §3.3):
/// snake_case property names, no indentation, every property written and the default (non-relaxed) encoder, so
/// the byte count <c>Bible:Limits:MaxContentBytes</c> is measured against is the same document the database
/// stores. <see cref="BibleContentValidator"/> counts those bytes and the EF value converters persist them.
/// </summary>
/// <remarks>
/// The counterpart in Infrastructure is the source-generated <c>BibleJsonContext</c>, which supplies the
/// serialiser's type metadata; both take their naming policy and encoder from <see cref="Options"/>, so the
/// validated size and the stored bytes cannot drift.
/// </remarks>
public static class BibleJson
{
    /// <summary>
    /// The canonical serialiser settings (see the type remarks): snake_case, no indentation, no ignored
    /// properties and no relaxed escaping.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.Default,
    };

    /// <summary>Serialises a bible document in its canonical form.</summary>
    /// <param name="content">The content to serialise.</param>
    /// <returns>The canonical JSON of <c>bible.v1</c>.</returns>
    public static string Serialize(SeriesBibleContent content) => JsonSerializer.Serialize(content, Options);

    /// <summary>Serialises a pending proposal in its canonical form (<c>proposed_content</c>).</summary>
    /// <param name="proposal">The proposal to serialise.</param>
    /// <returns>The canonical JSON of the proposal document.</returns>
    public static string SerializeProposal(BibleProposal proposal) => JsonSerializer.Serialize(proposal, Options);

    /// <summary>Reads a stored bible document.</summary>
    /// <param name="json">The stored JSON.</param>
    /// <returns>The document.</returns>
    /// <exception cref="InvalidOperationException">The JSON deserialised to <see langword="null"/>.</exception>
    public static SeriesBibleContent DeserializeContent(string json) =>
        JsonSerializer.Deserialize<SeriesBibleContent>(json, Options)
        ?? throw new InvalidOperationException("A persisted bible document is never null: the column is NOT NULL.");

    /// <summary>Reads a stored proposal document.</summary>
    /// <param name="json">The stored JSON, or <see langword="null"/> when no proposal is pending.</param>
    /// <returns>The proposal, or <see langword="null"/>.</returns>
    public static BibleProposal? DeserializeProposal(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<BibleProposal>(json, Options);

    /// <summary>The UTF-8 size of a bible document's canonical JSON.</summary>
    /// <param name="content">The content to measure.</param>
    /// <returns>The byte count <c>Bible:Limits:MaxContentBytes</c> caps.</returns>
    public static int Utf8ByteCount(SeriesBibleContent content) =>
        Encoding.UTF8.GetByteCount(Serialize(content));
}
