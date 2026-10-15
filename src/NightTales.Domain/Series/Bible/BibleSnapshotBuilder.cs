using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace NightTales.Domain.Series.Bible;

/// <summary>
/// The writer-budget knobs of the bible snapshot (02.6.3 plan §4 U4; configuration section <c>Bible:Prompt</c>).
/// </summary>
/// <param name="MaxSummaries">The newest episode summaries kept (step S0).</param>
/// <param name="MaxChars">The budget of the serialised <c>bible</c> object, in UTF-8 bytes.</param>
/// <param name="MaxDescriptionChars">The length a character or place description is cut to (step S2).</param>
/// <param name="MaxVoiceNotesChars">The length the narrator's voice notes are cut to (step S1).</param>
/// <param name="KeepFullSummaryCount">The newest summaries that keep their full text; older ones keep the title (step S3).</param>
/// <param name="MaxArrayEntries">The rules, places and characters kept when a list is capped (steps S4 to S6).</param>
public sealed record BibleSnapshotLimits(
    int MaxSummaries,
    int MaxChars,
    int MaxDescriptionChars,
    int MaxVoiceNotesChars,
    int KeepFullSummaryCount,
    int MaxArrayEntries);

/// <summary>
/// Builds the bounded writer view of a series bible (02.6.3 plan §3.4): the newest summaries first, then
/// progressively smaller forms, until the serialised <c>bible</c> object fits <see cref="BibleSnapshotLimits.MaxChars"/>.
/// </summary>
/// <remarks>
/// Pure: no I/O, no clock, no logging. The narrator is never dropped. When the budget is still not met after step
/// S7 the builder throws <see cref="BibleSnapshotTooLargeException"/> rather than send a partial bible. The size is
/// measured on the same serialisation the writer's data object uses (see <see cref="MeasureUtf8Bytes"/>), so a
/// bible that passes here is the exact number of bytes the prompt carries.
/// </remarks>
public static class BibleSnapshotBuilder
{
    /// <summary>The data object's serialiser options (02 T68), so the measured bytes are the bytes the prompt carries.</summary>
    private static readonly JsonSerializerOptions MeasureOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    /// Builds the writer snapshot of <paramref name="content"/>. Step S0 always keeps the newest
    /// <see cref="BibleSnapshotLimits.MaxSummaries"/> summaries. Steps S1 to S7 then run in order, each only while the
    /// serialised bible is still over budget.
    /// </summary>
    /// <param name="content">The approved bible content.</param>
    /// <param name="version">The bible's version; the content itself does not carry one.</param>
    /// <param name="limits">The writer-budget limits.</param>
    /// <returns>The bounded snapshot. <see cref="BibleSnapshot.Truncated"/> is true when any step changed it.</returns>
    /// <exception cref="BibleSnapshotTooLargeException">The bible is still over budget after step S7.</exception>
    public static BibleSnapshot Build(SeriesBibleContent content, int version, BibleSnapshotLimits limits)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxSummaries);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxArrayEntries);

        var summaries = OrderNewestFirst(content.EpisodeSummaries);
        var draft = new Draft(
            version,
            new BibleNarratorSnapshot(content.Narrator.Name, content.Narrator.Persona, NullIfBlank(content.Narrator.VoiceNotes)),
            content.RecurringCharacters.Select(character => new BibleCharacterSnapshot(character.Name, character.Description)).ToList(),
            content.Places.Select(place => new BiblePlaceSnapshot(place.Name, place.Description)).ToList(),
            content.Rules.Select(rule => rule.Text).ToList(),
            summaries.Select(summary => new BibleEpisodeSummarySnapshot(summary.Title, summary.Summary)).ToList());

        // S0: the newest MaxSummaries summaries. Always applied, because the count is a contract, not a size fallback.
        if (draft.Summaries.Count > limits.MaxSummaries)
        {
            draft.Summaries.RemoveRange(limits.MaxSummaries, draft.Summaries.Count - limits.MaxSummaries);
            draft.Truncated = true;
        }

        Func<Draft, bool>[] steps =
        [
            d => TruncateVoiceNotes(d, limits.MaxVoiceNotesChars),
            d => TruncateDescriptions(d, limits.MaxDescriptionChars),
            d => DropOlderSummaryText(d, limits.KeepFullSummaryCount),
            d => CapList(d.Rules, limits.MaxArrayEntries),
            d => CapList(d.Places, limits.MaxArrayEntries),
            d => CapList(d.Characters, limits.MaxArrayEntries),
            d => ClearRules(d),
        ];

        var measured = MeasureUtf8Bytes(draft.ToSnapshot());
        foreach (var step in steps)
        {
            if (measured <= limits.MaxChars)
            {
                break;
            }

            if (step(draft))
            {
                draft.Truncated = true;
            }

            measured = MeasureUtf8Bytes(draft.ToSnapshot());
        }

        if (measured > limits.MaxChars)
        {
            throw new BibleSnapshotTooLargeException(measured, limits.MaxChars);
        }

        return draft.ToSnapshot();
    }

    /// <summary>
    /// The UTF-8 byte count of the snapshot serialised exactly as the writer's data object serialises its
    /// <c>bible</c> property: snake_case names, nulls omitted, no indentation, Vietnamese written as-is.
    /// </summary>
    /// <param name="snapshot">The snapshot to measure.</param>
    /// <returns>The size in bytes.</returns>
    public static int MeasureUtf8Bytes(BibleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(snapshot, MeasureOptions));
    }

    /// <summary>S1: cuts the narrator's voice notes to <paramref name="maxChars"/>.</summary>
    private static bool TruncateVoiceNotes(Draft draft, int maxChars)
    {
        if (draft.Narrator.VoiceNotes is not { } notes || notes.Length <= maxChars)
        {
            return false;
        }

        draft.Narrator = draft.Narrator with { VoiceNotes = NullIfBlank(BibleOpNormalizer.Truncate(notes, maxChars)) };
        return true;
    }

    /// <summary>S2: cuts every character and place description to <paramref name="maxChars"/>.</summary>
    private static bool TruncateDescriptions(Draft draft, int maxChars)
    {
        var changed = false;
        for (var index = 0; index < draft.Characters.Count; index++)
        {
            var character = draft.Characters[index];
            if (character.Description.Length <= maxChars)
            {
                continue;
            }

            draft.Characters[index] = character with { Description = BibleOpNormalizer.Truncate(character.Description, maxChars) };
            changed = true;
        }

        for (var index = 0; index < draft.Places.Count; index++)
        {
            var place = draft.Places[index];
            if (place.Description.Length <= maxChars)
            {
                continue;
            }

            draft.Places[index] = place with { Description = BibleOpNormalizer.Truncate(place.Description, maxChars) };
            changed = true;
        }

        return changed;
    }

    /// <summary>S3: keeps the full text of the newest <paramref name="keepFull"/> summaries; older ones keep only the title.</summary>
    private static bool DropOlderSummaryText(Draft draft, int keepFull)
    {
        var changed = false;
        for (var index = Math.Max(keepFull, 0); index < draft.Summaries.Count; index++)
        {
            var summary = draft.Summaries[index];
            if (summary.Summary is null)
            {
                continue;
            }

            draft.Summaries[index] = summary with { Summary = null };
            changed = true;
        }

        return changed;
    }

    /// <summary>S4 to S6: keeps the first <paramref name="max"/> entries of a list.</summary>
    private static bool CapList<T>(List<T> items, int max)
    {
        if (items.Count <= max)
        {
            return false;
        }

        items.RemoveRange(max, items.Count - max);
        return true;
    }

    /// <summary>S7: drops every rule.</summary>
    private static bool ClearRules(Draft draft)
    {
        if (draft.Rules.Count == 0)
        {
            return false;
        }

        draft.Rules.Clear();
        return true;
    }

    /// <summary>Summaries newest first by approval; a tie keeps the input order, so the result is deterministic.</summary>
    private static List<BibleEpisodeSummary> OrderNewestFirst(IReadOnlyList<BibleEpisodeSummary> summaries) =>
        summaries
            .Select((summary, index) => (summary, index))
            .OrderByDescending(entry => entry.summary.ApprovedAt)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.summary)
            .ToList();

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The mutable working copy the steps edit; <see cref="ToSnapshot"/> freezes it into the record.</summary>
    private sealed class Draft(
        int version,
        BibleNarratorSnapshot narrator,
        List<BibleCharacterSnapshot> characters,
        List<BiblePlaceSnapshot> places,
        List<string> rules,
        List<BibleEpisodeSummarySnapshot> summaries)
    {
        public BibleNarratorSnapshot Narrator { get; set; } = narrator;

        public List<BibleCharacterSnapshot> Characters { get; } = characters;

        public List<BiblePlaceSnapshot> Places { get; } = places;

        public List<string> Rules { get; } = rules;

        public List<BibleEpisodeSummarySnapshot> Summaries { get; } = summaries;

        public bool Truncated { get; set; }

        public BibleSnapshot ToSnapshot() => new(version, Narrator, Characters, Places, Rules, Summaries, Truncated);
    }
}
