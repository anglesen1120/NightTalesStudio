using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// The one JSON data object of an idea-generation call (02 README D6, 02.8.1-T1 plan §7.1): the control keys
/// first, then the series' material, the bible or the episode history, the structure steering and the research
/// notes.
/// </summary>
/// <remarks>
/// <para>
/// Every string here is material the model may read, never an instruction (the two-tier rule the idea prompt
/// states): the bible, the episode summaries and the research notes are values of a JSON document, so nothing
/// can break out of the object or the turn structure. The encoder is pinned to all Unicode ranges (D6/T68), so
/// Vietnamese text travels readably rather than escaped.
/// </para>
/// <para>
/// No id ever appears (D6's alias principle, D19): the bible block is rebuilt from <see cref="BibleSnapshot"/>
/// without its version or truncation flag, episode summaries carry no episode id, and a research note carries
/// only its kind and text — never a source id, URL or title. The top-level <c>recent_episodes</c> key exists
/// only when the bible is empty, so the same text is never paid for twice.
/// </para>
/// </remarks>
/// <param name="Task">Always <c>generate_ideas</c>.</param>
/// <param name="Language">The series' language, snake_case.</param>
/// <param name="Topic">The series' narrative topic, snake_case.</param>
/// <param name="Tone">The owner's tone, when the series carries one.</param>
/// <param name="Count">How many ideas to propose.</param>
/// <param name="BannedThemes">The themes to keep out, when any.</param>
/// <param name="Bible">The id-free bible, when the series has one.</param>
/// <param name="RecentEpisodes">The episode history, only when there is no bible.</param>
/// <param name="RequiredTags">The structure steering's required values, when any.</param>
/// <param name="ForbiddenTags">The structure steering's forbidden values, when any.</param>
/// <param name="ResearchNotes">The owner's notes, each truncated to 400 characters.</param>
public sealed record IdeaDataObject(
    string Task,
    string Language,
    string Topic,
    string? Tone,
    int Count,
    IReadOnlyList<string>? BannedThemes,
    IdeaDataBible? Bible,
    IReadOnlyList<IdeaDataEpisode>? RecentEpisodes,
    IdeaDataRequiredTags? RequiredTags,
    IdeaDataForbiddenTags? ForbiddenTags,
    IReadOnlyList<IdeaDataResearchNote>? ResearchNotes);

/// <summary>The bible block: the same id-free view the writer prompt carries (02.6.3-T3).</summary>
/// <param name="Narrator">The narrator, when one is defined.</param>
/// <param name="RecurringCharacters">The recurring characters.</param>
/// <param name="Places">The recurring places.</param>
/// <param name="Rules">The standing rules, as text.</param>
/// <param name="RecentEpisodes">The newest summaries, without episode ids.</param>
public sealed record IdeaDataBible(
    IdeaDataNarrator? Narrator,
    IReadOnlyList<IdeaDataNamed> RecurringCharacters,
    IReadOnlyList<IdeaDataNamed> Places,
    IReadOnlyList<string> Rules,
    IReadOnlyList<IdeaDataEpisode> RecentEpisodes);

/// <summary>The narrator of the data object.</summary>
/// <param name="Name">The narrator's name, if any.</param>
/// <param name="Persona">The persona text.</param>
public sealed record IdeaDataNarrator(string? Name, string Persona);

/// <summary>A character or a place of the data object: the name and the description, never an id.</summary>
/// <param name="Name">The name.</param>
/// <param name="Description">The description.</param>
public sealed record IdeaDataNamed(string Name, string Description);

/// <summary>One episode of the data object: the title and the summary, never an id.</summary>
/// <param name="Title">The title.</param>
/// <param name="Summary">The summary, or <see langword="null"/> when the budget kept only the title.</param>
public sealed record IdeaDataEpisode(string Title, string? Summary);

/// <summary>The structure steering's required values; an unconstrained dimension is omitted.</summary>
/// <param name="SubGenre">The required sub-genre id, or <see langword="null"/>.</param>
/// <param name="Setting">The required setting id, or <see langword="null"/>.</param>
/// <param name="Opening">The required opening id, or <see langword="null"/>.</param>
/// <param name="TwistType">The required twist-type id, or <see langword="null"/>.</param>
public sealed record IdeaDataRequiredTags(string? SubGenre, string? Setting, string? Opening, string? TwistType);

/// <summary>The structure steering's forbidden values; an empty dimension is omitted.</summary>
/// <param name="SubGenre">The forbidden sub-genre ids, or <see langword="null"/>.</param>
/// <param name="Setting">The forbidden setting ids, or <see langword="null"/>.</param>
/// <param name="Opening">The forbidden opening ids, or <see langword="null"/>.</param>
/// <param name="TwistType">The forbidden twist-type ids, or <see langword="null"/>.</param>
public sealed record IdeaDataForbiddenTags(
    IReadOnlyList<string>? SubGenre,
    IReadOnlyList<string>? Setting,
    IReadOnlyList<string>? Opening,
    IReadOnlyList<string>? TwistType);

/// <summary>One research note of the data object: the kind and the text, never a source id, URL or title.</summary>
/// <param name="Kind">The note kind, for example <c>user_note</c>.</param>
/// <param name="Text">The note text, at most 400 characters.</param>
public sealed record IdeaDataResearchNote(string Kind, string Text);

/// <summary>
/// Builds and serialises the one data object of an idea-generation call (02 README D6, 02.8.1-T1 plan §7.1).
/// </summary>
/// <remarks>
/// The property order is the declaration order — control keys, then the material — so the material block starts
/// at a stable offset and the riskiest text sits last. Null and empty optional keys are omitted rather than sent
/// as empty values, so the prompt's rule "absent means unconstrained" holds. Nothing here truncates the
/// serialised object: the generator refuses an object over <c>Story:Prompt:MaxDataObjectBytes</c> instead of
/// silently dropping content (the bible builder's own budget keeps that unreachable in practice).
/// </remarks>
public static class IdeaDataObjectJson
{
    /// <summary>The <c>task</c> control value of an idea-generation call.</summary>
    public const string GenerateIdeasTask = "generate_ideas";

    /// <summary>The most characters of one research note the data object carries (plan §8, D19).</summary>
    public const int MaxResearchNoteChars = 400;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Builds and serialises the data object of one call.</summary>
    /// <param name="request">The series context and the call's parameters.</param>
    /// <returns>The one data object, the exact string the client sends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="request"/> names no candidate.</exception>
    public static string Build(IdeaGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Count);

        var bible = BibleBlock(request.Bible);
        var dataObject = new IdeaDataObject(
            GenerateIdeasTask,
            request.Language,
            request.Topic,
            NullIfBlank(request.Tone),
            request.Count,
            EmptyToNull(request.BannedThemes),
            bible,
            // Plan §7.1: the bible's own summaries are the continuity memory; the top-level list exists only for
            // a series whose bible is empty, so the same text is never sent twice (cost and cache prefix).
            bible is null ? Episodes(request.RecentEpisodes) : null,
            RequiredTags(request.RequiredTags),
            ForbiddenTags(request.ForbiddenTags),
            ResearchNotes(request.ResearchNotes));

        return JsonSerializer.Serialize(dataObject, Options);
    }

    /// <summary>
    /// Cuts a research note to the contract's 400 characters, never splitting a surrogate pair (half a pair is
    /// not a character and <c>System.Text.Json</c> would rewrite it).
    /// </summary>
    /// <param name="text">The note text.</param>
    /// <param name="maxChars">The ceiling; <see cref="MaxResearchNoteChars"/> at the call site.</param>
    /// <returns>The text to send.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxChars"/> is not positive.</exception>
    public static string TruncateNote(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;

        return text[..cut];
    }

    /// <summary>The bible block, ids stripped and version/truncation flags dropped; <see langword="null"/> when empty.</summary>
    private static IdeaDataBible? BibleBlock(BibleSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var narrator = snapshot.Narrator is { } source
            ? new IdeaDataNarrator(NullIfBlank(source.Name), source.Persona)
            : null;
        var characters = snapshot.RecurringCharacters
            .Select(character => new IdeaDataNamed(character.Name, character.Description))
            .ToArray();
        var places = snapshot.Places
            .Select(place => new IdeaDataNamed(place.Name, place.Description))
            .ToArray();
        var rules = snapshot.Rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Select(rule => rule.Trim())
            .ToArray();
        var episodes = snapshot.EpisodeSummaries
            .Select(summary => new IdeaDataEpisode(summary.Title, NullIfBlank(summary.Summary)))
            .ToArray();

        if (narrator is null && characters.Length == 0 && places.Length == 0 && rules.Length == 0 && episodes.Length == 0)
        {
            return null;
        }

        return new IdeaDataBible(narrator, characters, places, rules, episodes);
    }

    /// <summary>The episode history of an empty bible; <see langword="null"/> when there is none.</summary>
    private static IdeaDataEpisode[]? Episodes(IReadOnlyList<EpisodeSummaryRef>? summaries)
    {
        if (summaries is null || summaries.Count == 0)
        {
            return null;
        }

        var episodes = summaries
            .Where(summary => !string.IsNullOrWhiteSpace(summary.Title))
            .Select(summary => new IdeaDataEpisode(summary.Title.Trim(), NullIfBlank(summary.Summary)))
            .ToArray();

        return episodes.Length == 0 ? null : episodes;
    }

    /// <summary>The required tags, dropping unconstrained dimensions; <see langword="null"/> when nothing is required.</summary>
    private static IdeaDataRequiredTags? RequiredTags(StructureRequirements? required)
    {
        if (required is null)
        {
            return null;
        }

        var subGenre = NullIfBlank(required.SubGenre);
        var setting = NullIfBlank(required.Setting);
        var opening = NullIfBlank(required.Opening);
        var twistType = NullIfBlank(required.TwistType);

        return subGenre is null && setting is null && opening is null && twistType is null
            ? null
            : new IdeaDataRequiredTags(subGenre, setting, opening, twistType);
    }

    /// <summary>The forbidden tags, dropping empty dimensions; <see langword="null"/> when nothing is forbidden.</summary>
    private static IdeaDataForbiddenTags? ForbiddenTags(StructureForbidden? forbidden)
    {
        if (forbidden is null)
        {
            return null;
        }

        var subGenre = EmptyToNull(forbidden.SubGenre);
        var setting = EmptyToNull(forbidden.Setting);
        var opening = EmptyToNull(forbidden.Opening);
        var twistType = EmptyToNull(forbidden.TwistType);

        return subGenre is null && setting is null && opening is null && twistType is null
            ? null
            : new IdeaDataForbiddenTags(subGenre, setting, opening, twistType);
    }

    /// <summary>The owner's notes, each truncated to 400 characters; <see langword="null"/> when there are none.</summary>
    private static IdeaDataResearchNote[]? ResearchNotes(IReadOnlyList<ResearchNoteRef>? notes)
    {
        if (notes is null || notes.Count == 0)
        {
            return null;
        }

        var kept = notes
            .Where(note => !string.IsNullOrWhiteSpace(note.Text))
            .Select(note => new IdeaDataResearchNote(note.Kind, TruncateNote(note.Text, MaxResearchNoteChars)))
            .ToArray();

        return kept.Length == 0 ? null : kept;
    }

    /// <summary>Drops an empty list, so an unconstrained key is omitted rather than sent empty.</summary>
    private static string[]? EmptyToNull(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var kept = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray();

        return kept.Length == 0 ? null : kept;
    }

    /// <summary>A blank nullable string is no value at all.</summary>
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
