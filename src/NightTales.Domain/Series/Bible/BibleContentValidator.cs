using System.Globalization;

namespace NightTales.Domain.Series.Bible;

/// <summary>
/// One content violation of a series bible (02.6.3 plan §3.2): the JSON path of the offending entry, the
/// closed code the client shows and an optional, content-free detail (a limit or the path of the entry the
/// value collides with).
/// </summary>
/// <param name="Field">The JSON path, e.g. <c>episode_summaries[3].summary</c> (root-level violations use <c>body</c>).</param>
/// <param name="Code">One of the code constants of <see cref="BibleContentValidator"/>.</param>
/// <param name="Detail">A limit or a colliding path; never content, a name or a summary (D19).</param>
public sealed record BibleFieldError(string Field, string Code, string? Detail);

/// <summary>
/// The pure content rules of the series bible (02.6.3 plan §3.2): the per-field limits, the non-empty persona,
/// the per-array uniqueness after <see cref="BibleOpNormalizer"/>, one summary per episode, the approved-episode
/// rule and the UTF-8 size budget of the whole document. No I/O and no clock.
/// </summary>
/// <remarks>
/// <para>
/// The client shows one message per field, so every violation is reported with its own path and code; the codes
/// are snake_case and language-neutral. The <c>&lt;</c>/<c>&gt;</c> rule is the D3/F36 metadata rule reused for
/// consistency; the character-class rule additionally refuses the C0/C1 controls, the zero-width/format/bidi
/// characters and the tag-plane characters, because a bible is a machine-read JSON document — none of them can
/// be stored or displayed faithfully, and a control character in a stored document is a defect no reader can
/// see (STATUS: the fact-sheet rules missed this class).
/// </para>
/// <para>
/// The two episode codes split on what this layer can observe from the approved id set it is given: an id that
/// names no episode at all (<see cref="Guid.Empty"/>) is <c>unknown_episode</c>, and an id that is not an
/// approved episode of this series — unknown, another series' or a known-but-unapproved one — is
/// <c>not_approved_episode</c> (the 400 the plan's acceptance checks require for a foreign episode). The
/// structure block and the approval instant of a summary are server-derived (plan §4 U2), so no rule here
/// constrains them and a placeholder value can never add a second error.
/// </para>
/// <para>
/// The <c>id</c> of a character, place or rule is checked too, because it travels into the writer's data object:
/// it must be <c>ch</c>/<c>pl</c>/<c>rl</c> followed by a number with no leading zero and at most nine digits
/// and it must be unique within its array (<c>invalid_structure</c> and <c>duplicate</c> respectively), while a
/// blank one is <c>empty</c>. The PUT handler fills a blank id before validation
/// (<see cref="SeriesBible.AssignMissingIds"/>), so a client can add an entry without knowing its id; a blank
/// reaching this validator is a caller that skipped that step.
/// </para>
/// </remarks>
public static class BibleContentValidator
{
    /// <summary>The value exceeds its field's limit.</summary>
    public const string TooLong = "too_long";

    /// <summary>The array holds more entries than its limit allows.</summary>
    public const string TooMany = "too_many";

    /// <summary>A required value is absent, blank, or the whole-document root is invalid.</summary>
    public const string Empty = "empty";

    /// <summary>The value repeats another entry of its array after <see cref="BibleOpNormalizer.Normalize"/>.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>A summary names no episode (<see cref="Guid.Empty"/>).</summary>
    public const string UnknownEpisode = "unknown_episode";

    /// <summary>A summary names an episode of this series that is not approved (or is not one of its episodes).</summary>
    public const string NotApprovedEpisode = "not_approved_episode";

    /// <summary>A value cannot be stored or displayed faithfully: markup, or a control/format/tag character.</summary>
    public const string InvalidStructure = "invalid_structure";

    /// <summary>
    /// A value names a field the backend owns. This validator never emits it — a manual PUT ignores
    /// <c>schema_version</c>, <c>structure</c> and <c>approved_at</c> by contract (plan §4 U2) — and it is part
    /// of the plan's vocabulary because the proposal path rejects an op that would rewrite such a field.
    /// </summary>
    public const string ReadOnly = "read_only";

    /// <summary>The field path of a violation of the document as a whole (the size budget).</summary>
    public const string RootField = "body";

    private const string CharacterIdPrefix = "ch";
    private const string PlaceIdPrefix = "pl";
    private const string RuleIdPrefix = "rl";

    /// <summary>
    /// Validates one bible document against <paramref name="limits"/>, without the approved-episode rule
    /// (02.6.3 plan §3.2). This is the overload an aggregate re-checks defensively, because it holds no
    /// episode scope.
    /// </summary>
    /// <param name="content">The document to validate.</param>
    /// <param name="limits">The configured limits.</param>
    /// <returns>Every violation, in field order; empty when the document is valid.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public static IReadOnlyList<BibleFieldError> Validate(SeriesBibleContent content, BibleLimits limits) =>
        Validate(content, limits, null);

    /// <summary>
    /// Validates one bible document against <paramref name="limits"/> and the approved-episode rule the PUT
    /// needs (02.6.3 plan §3.2): every <c>episode_summaries[].episode_id</c> must be an approved episode of the
    /// same series.
    /// </summary>
    /// <param name="content">The document to validate.</param>
    /// <param name="limits">The configured limits.</param>
    /// <param name="approvedEpisodeIds">The ids of the series' episodes with an approved story version.</param>
    /// <returns>Every violation, in field order; empty when the document is valid.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public static IReadOnlyList<BibleFieldError> Validate(
        SeriesBibleContent content,
        BibleLimits limits,
        IReadOnlySet<Guid>? approvedEpisodeIds)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        var errors = new List<BibleFieldError>();

        ValidateNarrator(errors, content, limits);
        ValidateCharacters(errors, content, limits);
        ValidatePlaces(errors, content, limits);
        ValidateRules(errors, content, limits);
        ValidateSummaries(errors, content, limits, approvedEpisodeIds);

        // The D6 data-object budget: the canonical document, in UTF-8 bytes, exactly as it will be stored.
        if (BibleJson.Utf8ByteCount(content) > limits.MaxContentBytes)
        {
            errors.Add(new BibleFieldError(RootField, TooLong, Limit(limits.MaxContentBytes)));
        }

        return errors;
    }

    /// <summary>Validates <c>narrator</c>: the persona is required and non-empty, the two other texts are capped.</summary>
    private static void ValidateNarrator(List<BibleFieldError> errors, SeriesBibleContent content, BibleLimits limits)
    {
        var narrator = content.Narrator;
        if (narrator is null)
        {
            errors.Add(new BibleFieldError("narrator", Empty, null));
            return;
        }

        AddText(errors, "narrator.name", narrator.Name, limits.NarratorNameChars);
        if (string.IsNullOrWhiteSpace(narrator.Persona))
        {
            errors.Add(new BibleFieldError("narrator.persona", Empty, null));
        }

        AddText(errors, "narrator.persona", narrator.Persona, limits.NarratorPersonaChars);
        AddText(errors, "narrator.voice_notes", narrator.VoiceNotes, limits.VoiceNotesChars);
    }

    /// <summary>Validates <c>recurring_characters</c>: the count, each name/description, and unique normalised names.</summary>
    private static void ValidateCharacters(List<BibleFieldError> errors, SeriesBibleContent content, BibleLimits limits)
    {
        IReadOnlyList<BibleCharacter>? characters = content.RecurringCharacters;
        if (characters is null)
        {
            return;
        }

        AddCount(errors, "recurring_characters", characters.Count, limits.MaxCharacters);

        var seenIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < characters.Count; index++)
        {
            var field = Field("recurring_characters", index);
            var character = characters[index];
            if (character is null)
            {
                errors.Add(new BibleFieldError(field, InvalidStructure, null));
                continue;
            }

            AddId(errors, field, character.Id, CharacterIdPrefix, seenIds, "recurring_characters", index);
            AddText(errors, string.Concat(field, ".name"), character.Name, limits.NameChars);
            AddText(errors, string.Concat(field, ".description"), character.Description, limits.DescriptionChars);
            AddDuplicate(errors, string.Concat(field, ".name"), character.Name, seen, "recurring_characters", index);
        }
    }

    /// <summary>Validates <c>places</c>: the count, each name/description, and unique normalised names.</summary>
    private static void ValidatePlaces(List<BibleFieldError> errors, SeriesBibleContent content, BibleLimits limits)
    {
        IReadOnlyList<BiblePlace>? places = content.Places;
        if (places is null)
        {
            return;
        }

        AddCount(errors, "places", places.Count, limits.MaxPlaces);

        var seenIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < places.Count; index++)
        {
            var field = Field("places", index);
            var place = places[index];
            if (place is null)
            {
                errors.Add(new BibleFieldError(field, InvalidStructure, null));
                continue;
            }

            AddId(errors, field, place.Id, PlaceIdPrefix, seenIds, "places", index);
            AddText(errors, string.Concat(field, ".name"), place.Name, limits.NameChars);
            AddText(errors, string.Concat(field, ".description"), place.Description, limits.DescriptionChars);
            AddDuplicate(errors, string.Concat(field, ".name"), place.Name, seen, "places", index);
        }
    }

    /// <summary>Validates <c>rules</c>: the count, each text, and unique normalised texts.</summary>
    private static void ValidateRules(List<BibleFieldError> errors, SeriesBibleContent content, BibleLimits limits)
    {
        IReadOnlyList<BibleRule>? rules = content.Rules;
        if (rules is null)
        {
            return;
        }

        AddCount(errors, "rules", rules.Count, limits.MaxRules);

        var seenIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < rules.Count; index++)
        {
            var field = Field("rules", index);
            var rule = rules[index];
            if (rule is null)
            {
                errors.Add(new BibleFieldError(field, InvalidStructure, null));
                continue;
            }

            AddId(errors, field, rule.Id, RuleIdPrefix, seenIds, "rules", index);
            AddText(errors, string.Concat(field, ".text"), rule.Text, limits.RuleChars);
            AddDuplicate(errors, string.Concat(field, ".text"), rule.Text, seen, "rules", index);
        }
    }

    /// <summary>
    /// Validates <c>episode_summaries</c>: one entry per episode, each episode approved, and the two capped
    /// texts. The derived <c>structure</c> and <c>approved_at</c> are not validated (plan §4 U2).
    /// </summary>
    private static void ValidateSummaries(
        List<BibleFieldError> errors,
        SeriesBibleContent content,
        BibleLimits limits,
        IReadOnlySet<Guid>? approvedEpisodeIds)
    {
        IReadOnlyList<BibleEpisodeSummary>? summaries = content.EpisodeSummaries;
        if (summaries is null)
        {
            return;
        }

        var seen = new Dictionary<Guid, int>();
        for (var index = 0; index < summaries.Count; index++)
        {
            var field = Field("episode_summaries", index);
            var summary = summaries[index];
            if (summary is null)
            {
                errors.Add(new BibleFieldError(field, InvalidStructure, null));
                continue;
            }

            var episodeField = string.Concat(field, ".episode_id");
            if (summary.EpisodeId == Guid.Empty)
            {
                errors.Add(new BibleFieldError(episodeField, UnknownEpisode, null));
            }
            else if (approvedEpisodeIds is not null && !approvedEpisodeIds.Contains(summary.EpisodeId))
            {
                errors.Add(new BibleFieldError(episodeField, NotApprovedEpisode, null));
            }

            if (seen.TryGetValue(summary.EpisodeId, out var first))
            {
                errors.Add(new BibleFieldError(
                    episodeField,
                    Duplicate,
                    string.Concat(Field("episode_summaries", first), ".episode_id")));
            }
            else
            {
                seen[summary.EpisodeId] = index;
            }

            AddText(errors, string.Concat(field, ".title"), summary.Title, limits.SummaryTitleChars);
            AddText(errors, string.Concat(field, ".summary"), summary.Summary, limits.SummaryChars);
        }
    }

    /// <summary>Adds <c>too_many</c> when the array holds more entries than its limit.</summary>
    private static void AddCount(List<BibleFieldError> errors, string field, int count, int max)
    {
        if (count > max)
        {
            errors.Add(new BibleFieldError(field, TooMany, Limit(max)));
        }
    }

    /// <summary>Appends <c>duplicate</c> when the normalised value already occurred in the array.</summary>
    private static void AddDuplicate(
        List<BibleFieldError> errors,
        string field,
        string? value,
        Dictionary<string, int> seen,
        string arrayField,
        int index)
    {
        var normalised = BibleOpNormalizer.Normalize(value);
        if (normalised.Length == 0)
        {
            return;
        }

        if (seen.TryGetValue(normalised, out var first))
        {
            errors.Add(new BibleFieldError(field, Duplicate, Field(arrayField, first)));
            return;
        }

        seen[normalised] = index;
    }

    /// <summary>
    /// Appends the id violations of one entry: <c>empty</c> for an absent or blank id, <c>invalid_structure</c>
    /// when it is not <c>&lt;prefix&gt;&lt;n&gt;</c>, and <c>duplicate</c> when the same id already occurred in
    /// the array. At most one per field.
    /// </summary>
    /// <remarks>
    /// A bible id is placed in the writer's data object, so a client-sent id is checked like a stored one: the
    /// prefix pins it to its own array, and the array must not carry the same id twice. A blank id is refused
    /// rather than repaired here — the PUT handler fills it before validation
    /// (<see cref="SeriesBible.AssignMissingIds"/>), so this layer stays a pure rule.
    /// </remarks>
    /// <param name="errors">The error list to append to.</param>
    /// <param name="field">The JSON path of the entry, e.g. <c>recurring_characters[1]</c>.</param>
    /// <param name="id">The id as it arrived or was stored.</param>
    /// <param name="prefix">The prefix the entry's array owns (<c>ch</c>, <c>pl</c> or <c>rl</c>).</param>
    /// <param name="seen">The ids seen so far in this array, with the index of the first entry that carried one.</param>
    /// <param name="arrayField">The array's JSON path, for the detail of a duplicate.</param>
    /// <param name="index">The index of this entry.</param>
    private static void AddId(
        List<BibleFieldError> errors,
        string field,
        string? id,
        string prefix,
        Dictionary<string, int> seen,
        string arrayField,
        int index)
    {
        var idField = string.Concat(field, ".id");
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add(new BibleFieldError(idField, Empty, null));
            return;
        }

        if (!IsIdOfPrefix(id, prefix))
        {
            errors.Add(new BibleFieldError(idField, InvalidStructure, null));
            return;
        }

        if (seen.TryGetValue(id, out var first))
        {
            errors.Add(new BibleFieldError(idField, Duplicate, string.Concat(Field(arrayField, first), ".id")));
            return;
        }

        seen[id] = index;
    }

    /// <summary>
    /// Whether <paramref name="id"/> is <paramref name="prefix"/> followed by a number of one to nine digits
    /// whose first digit is not zero (<c>ch1</c>, <c>pl12</c>, <c>rl999999999</c>).
    /// </summary>
    /// <param name="id">The id to check.</param>
    /// <param name="prefix">The prefix the array owns.</param>
    /// <returns>Whether the id has the shape of its array.</returns>
    private static bool IsIdOfPrefix(string id, string prefix)
    {
        if (!id.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = id.AsSpan(prefix.Length);
        if (digits.Length is 0 or > 9 || digits[0] == '0')
        {
            return false;
        }

        foreach (var character in digits)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Appends the character-class, markup and length violations of one optional text, at most one per field.</summary>
    private static void AddText(List<BibleFieldError> errors, string field, string? value, int maxChars)
    {
        if (value is null)
        {
            return;
        }

        if (FindUnsafeCharacter(value) is { } unsafeCharacter)
        {
            errors.Add(new BibleFieldError(
                field,
                InvalidStructure,
                string.Create(CultureInfo.InvariantCulture, $"U+{(int)unsafeCharacter:X4}")));
            return;
        }

        if (value.Contains('<') || value.Contains('>'))
        {
            errors.Add(new BibleFieldError(field, InvalidStructure, "angle_bracket"));
            return;
        }

        if (value.Length > maxChars)
        {
            errors.Add(new BibleFieldError(field, TooLong, Limit(maxChars)));
        }
    }

    /// <summary>The JSON path of one array entry.</summary>
    private static string Field(string arrayField, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{arrayField}[{index}]");

    /// <summary>The <c>Detail</c> of a limit violation: the limit itself, never the value.</summary>
    private static string Limit(int max) => max.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The first character that cannot be stored or displayed faithfully, or <see langword="null"/>: any C0
    /// control (including a line feed, a tab and NUL, which Postgres refuses), DEL and the C1 range, the
    /// zero-width and bidirectional format characters, the BOM/zero-width space, the tag-plane characters
    /// (U+E0000–U+E007F) and an unpaired surrogate.
    /// </summary>
    /// <param name="text">The text to scan; null and empty carry none.</param>
    /// <returns>The offending <see cref="char"/>, or <see langword="null"/>.</returns>
    private static char? FindUnsafeCharacter(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    return character;
                }

                var codePoint = char.ConvertToUtf32(character, text[index + 1]);
                if (codePoint is >= 0xE0000 and <= 0xE007F)
                {
                    return character;
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(character) || IsUnsafeBmpCharacter(character))
            {
                return character;
            }
        }

        return null;
    }

    /// <summary>The unsafe characters of the basic multilingual plane.</summary>
    private static bool IsUnsafeBmpCharacter(char character) =>
        character < ' '
        || character is '\u007F'
        || (character >= '\u0080' && character <= '\u009F')
        || (character >= '\u200B' && character <= '\u200F')
        || (character >= '\u202A' && character <= '\u202E')
        || (character >= '\u2060' && character <= '\u206F')
        || character is '\uFEFF';
}
