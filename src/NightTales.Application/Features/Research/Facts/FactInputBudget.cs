using System.Globalization;
using System.Text;
using NightTales.Domain.Common;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Facts;

/// <summary>The kind of a fact-extraction source (<c>story_sources.kind</c>, 02.5.2).</summary>
public enum FactSourceKind
{
    /// <summary>A fetched URL source; its text is <c>extracted_text</c>, and it may be truncated.</summary>
    Url = 0,

    /// <summary>The user's own note; its text is <c>note_text</c>, and it is never truncated (README CR-4).</summary>
    UserNote = 1,
}

/// <summary>
/// One candidate source handed to <see cref="FactInputBudget.Plan"/>: what the alias assignment needs and
/// nothing else. The caller fills <see cref="Text"/> from the row's own column of its kind —
/// <c>extracted_text</c> for a url, <c>note_text</c> for a user note.
/// </summary>
/// <param name="SourceId">The <c>story_sources.id</c>; also the tie-break of the creation-order sort.</param>
/// <param name="Title">The source title.</param>
/// <param name="Kind">Url or user note; a user note is never truncated.</param>
/// <param name="Text">The source text, or <see langword="null"/> when the row carries none.</param>
/// <param name="CreatedAt">The row's <c>created_at</c>; sources are ordered by it, ascending.</param>
public sealed record FactSourceCandidate(
    Guid SourceId,
    string Title,
    FactSourceKind Kind,
    string? Text,
    DateTimeOffset CreatedAt);

/// <summary>The input bounds of a fact-extraction call, bound from the <c>Facts</c> section (02.5.3-T1).</summary>
/// <param name="MaxSources">How many sources may be sent; the alias scheme allows at most <see cref="MaxSourcesCeiling"/>.</param>
/// <param name="PerSourceCharCap">The per-source character cap applied before the total budget.</param>
/// <param name="MaxInputTokens">The estimated-token ceiling of the serialised data object.</param>
public sealed record FactInputLimits(int MaxSources, int PerSourceCharCap, int MaxInputTokens)
{
    /// <summary>The largest source count the alias scheme allows (<c>s1…s10</c>); never alias <c>s11</c>.</summary>
    public const int MaxSourcesCeiling = 10;

    /// <summary>The default per-source character cap (<c>Facts:PerSourceCharCap</c>).</summary>
    public const int DefaultPerSourceCharCap = 20_000;

    /// <summary>The default total input budget in estimated tokens (<c>Facts:MaxInputTokens</c>).</summary>
    public const int DefaultMaxInputTokens = 60_000;

    /// <summary>The floor a per-source cap is halved to before the total-budget loop gives up.</summary>
    public const int MinimumPerSourceCharCap = 1_000;
}

/// <summary>Whether a <see cref="FactInputPlan"/> fits its total budget.</summary>
public enum FactInputBudgetStatus
{
    /// <summary>The estimated input is at or under <c>Facts:MaxInputTokens</c>; the plan may be sent.</summary>
    WithinBudget = 0,

    /// <summary>
    /// The plan cannot fit, or cannot be trusted to: either the estimated input is over the budget even at the
    /// minimum per-source floor (only user notes or floor-truncated sources remain), or a <c>user_note</c> is
    /// beyond its own <see cref="StorySource.NoteMaxChars"/> contract and is never truncated or scanned. The
    /// plan MUST NOT be sent: the caller fails the run instead of silently exceeding the cap (02.5.3-T1
    /// failure handling).
    /// </summary>
    OverBudget = 1,
}

/// <summary>
/// The deterministic result of <see cref="FactInputBudget.Plan"/>: the sources the model may read, what was
/// dropped, what was truncated and the estimated input size.
/// </summary>
/// <param name="Sources">The sources, alias-assigned <c>s1…sN</c>, in creation order; never empty.</param>
/// <param name="ExcludedSourceIds">The ids of the sources the plan dropped, whether for empty text, for the <c>MaxSources</c> ceiling or as a surplus of user notes; reported, not an error.</param>
/// <param name="TruncatedAliases">The aliases whose text was cut; the <c>input_truncated</c> signal of README CR-4 for 02.5.3-T2 and the UI.</param>
/// <param name="EstimatedTokens">The estimated token count of the serialised data object of <paramref name="Sources"/>, or <see cref="int.MaxValue"/> when the plan is returned <see cref="FactInputBudgetStatus.OverBudget"/> because a <c>user_note</c> is beyond <see cref="StorySource.NoteMaxChars"/>: such a plan is never sent, so the oversized note text is deliberately never scanned or serialised.</param>
/// <param name="Status">Whether the estimate fits <c>Facts:MaxInputTokens</c> and the plan may be sent.</param>
public sealed record FactInputPlan(
    IReadOnlyList<FactSourceInput> Sources,
    IReadOnlyList<Guid> ExcludedSourceIds,
    IReadOnlyList<string> TruncatedAliases,
    int EstimatedTokens,
    FactInputBudgetStatus Status)
{
    /// <summary>Whether the plan may be sent (<c>input_truncated</c> may still be true — a cut source fits).</summary>
    public bool WithinBudget => Status == FactInputBudgetStatus.WithinBudget;

    /// <summary>The README CR-4 <c>input_truncated</c> signal: at least one source text was cut.</summary>
    public bool InputTruncated => TruncatedAliases.Count > 0;
}

/// <summary>
/// The deterministic input budget of a fact-extraction call (02 README D6/D21, 02.5.3-T1): it selects and
/// alias-assigns the sources, truncates each one over its per-source cap, and shrinks the largest truncatable
/// sources until the serialised data object fits <c>Facts:MaxInputTokens</c>.
/// </summary>
/// <remarks>
/// <para>
/// It is pure and clock-free: the same candidates, limits and language always yield the same
/// <see cref="FactInputPlan"/>, so the provider prompt cache and the retry stay stable. The estimate is the
/// UTF-8 byte count of the very object <see cref="FactDataObjectJson.Serialize"/> produces, divided by four
/// and rounded up, so the estimate equals what the extractor sends.
/// </para>
/// <para>
/// Truncation keeps the head 60 % and the tail 40 % of a cut source around a
/// <c>\n[…truncated…]\n</c> marker, cut on text elements so a surrogate pair or a combining sequence is
/// never split; the marker is reserved <em>inside</em> <c>Facts:PerSourceCharCap</c>, so a cut source is never
/// longer than the cap (a text at or under the cap is returned untouched). A text-element boundary that lies
/// further than <see cref="MaxClusterWindowChars"/> past a cut target — or a text that is one huge grapheme
/// cluster — falls back to a surrogate-safe index cut, so the walk stays bounded and the result is always
/// well-formed. A cut is a real cut: a source is reported in <see cref="FactInputPlan.TruncatedAliases"/>
/// only when its text actually shrank. A <c>user_note</c> source is never truncated. When only such sources
/// (or floor-truncated ones) are left and the budget still does not fit, the plan is returned with
/// <see cref="FactInputBudgetStatus.OverBudget"/> rather than silently exceeding the cap.
/// </para>
/// <para>
/// Three ceilings bound the work before it starts. A url text above <see cref="HardTextCeiling"/> — sixteen
/// times <c>Facts:PerSourceCharCap</c> — is first reduced by a cheap, deterministic, surrogate-safe head 60 % /
/// tail 40 % index cut (<see cref="CeilingCut"/>), so the sanitise, the normalise, the element walk and the
/// estimate of a hostile input are all bounded; the source is still truncated by the normal cut below, which is
/// what reports it. A title longer than <see cref="MaxTitleChars"/> is cut to that length before the estimate
/// and the serialisation (a title is a label, not fact content), which closes the one uncapped string of the
/// data object. A <c>user_note</c> is capped by contract at <see cref="StorySource.NoteMaxChars"/> (3,000) and
/// is never truncated, so a note beyond that contract cannot be brought under the budget: the plan is returned
/// as <see cref="FactInputBudgetStatus.OverBudget"/> with
/// <see cref="FactInputPlan.EstimatedTokens"/> at its sentinel, its texts left exactly as the caller supplied
/// them, and no scan, sanitise or serialisation of the oversized text — it never throws. Such a plan is never
/// sent (the run fails on the status), and the extractor re-checks the budget over the bytes it serialises.
/// </para>
/// <para>
/// The <c>MaxSources</c> ceiling is filled in two passes, so a late user note is never pushed out by a url
/// that merely came first: the eligible <c>user_note</c> sources take slots first, ordered
/// <c>created_at</c> ascending then id, at most <c>MaxSources</c> of them — a surplus of notes above
/// <c>MaxSources</c> is excluded and reported, oldest notes winning. The remaining slots then go to the
/// eligible url sources, <c>created_at</c> ascending then id, so the newest urls are the ones the ceiling
/// excludes. The kept set is then re-ordered by <c>created_at</c> ascending then id and assigned the aliases
/// <c>s1…sN</c> in that creation order. Every source left out — empty text, the ceiling, or a surplus of
/// notes — is listed in <see cref="FactInputPlan.ExcludedSourceIds"/>.
/// </para>
/// </remarks>
public static class FactInputBudget
{
    /// <summary>The marker inserted where a source text was cut.</summary>
    private const string TruncationMarker = "\n[…truncated…]\n";

    /// <summary>
    /// The longest title carried into the data object. A title is a label, never fact material, so an
    /// over-long one is cut on a surrogate-pair boundary instead of being serialised whole — the title was
    /// the one string of the data object with no ceiling at all.
    /// </summary>
    private const int MaxTitleChars = 200;

    /// <summary>
    /// The multiple of <c>Facts:PerSourceCharCap</c> above which a url text is reduced before any other work:
    /// the hard ceiling is <see cref="HardTextCeiling"/>, so sanitising, normalising and cutting stay bounded
    /// even for a text a hundred times the cap.
    /// </summary>
    private const int HardTextCeilingMultiplier = 16;

    /// <summary>
    /// How far past a cut target a text-element boundary may lie before the cut falls back to a surrogate-safe
    /// UTF-16 index. A text that is one huge grapheme cluster — one base character with hundreds of thousands
    /// of combining marks or ZWJ extenders — has no boundary anywhere near the target, and neither walking to
    /// one nor cutting on one is affordable.
    /// </summary>
    private const int MaxClusterWindowChars = 32;

    /// <summary>
    /// The smallest cap a truncation may be asked for: a head and a tail of at least one character each have
    /// to fit beside the marker, so a cut below this keeps the head alone and still removes at least one
    /// character. The total-budget loop never reduces a source below
    /// <see cref="FactInputLimits.MinimumPerSourceCharCap"/> or this bound, whichever is larger.
    /// </summary>
    private static readonly int MinimumTruncationLength = TruncationMarker.Length + 2;

    /// <summary>The estimated token count of a serialised data object: its UTF-8 byte count divided by four, rounded up.</summary>
    /// <param name="utf8Json">The serialised data object.</param>
    /// <returns>The estimate the total-budget loop compares to <c>Facts:MaxInputTokens</c>.</returns>
    public static int EstimateTokens(string utf8Json)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);

        return (Encoding.UTF8.GetByteCount(utf8Json) + 3) / 4;
    }

    /// <summary>
    /// Builds the plan of one fact-extraction call: ordered, alias-assigned, per-source-truncated sources
    /// brought under the total budget.
    /// </summary>
    /// <param name="language">The run's language; part of the serialised data object, so it counts towards the estimate.</param>
    /// <param name="candidates">The episode's sources; ordered by <c>created_at</c> ascending, tie-broken by id.</param>
    /// <param name="limits">The <c>Facts</c> bounds in force.</param>
    /// <returns>The plan; its <see cref="FactInputPlan.Status"/> decides whether it may be sent.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No candidate is supplied, every candidate carries no text, or <paramref name="limits"/> is out of range (a programming error; the caller rejects an empty sheet with 409 <c>fact_sheet_sources_insufficient</c> first).</exception>
    public static FactInputPlan Plan(Language language, IReadOnlyList<FactSourceCandidate> candidates, FactInputLimits limits)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(limits);
        GuardLimits(limits);

        if (candidates.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidates), candidates, "A fact-extraction call needs at least one source.");
        }

        // Creation order is the one order the whole method reads: the two-pass slot selection, the alias
        // assignment and the exclusion report all walk it ascending, tie-broken by id.
        var ordered = candidates.OrderBy(item => item.CreatedAt).ThenBy(item => item.SourceId).ToList();

        // A user_note is never truncated and never left out while a url holds a slot (F18, task Design), so the
        // MaxSources ceiling is filled in two passes. Pass one: the eligible notes take slots first, oldest
        // first, up to MaxSources; a surplus of notes above MaxSources is reported and the oldest notes win.
        // Pass two: the remaining slots go to the eligible urls, oldest first, so the NEWEST urls are the ones
        // the ceiling excludes. A url with no extracted_text, or a user note with no note_text, carries nothing
        // and takes no slot: it is dropped before alias assignment and reported, never an error. A user note is
        // never dropped merely for having no extracted_text — it has none by design (README CR-4).
        var notes = new List<FactSourceCandidate>();
        var urls = new List<FactSourceCandidate>();
        foreach (var candidate in ordered)
        {
            if (string.IsNullOrWhiteSpace(candidate.Text))
            {
                continue;
            }

            (candidate.Kind == FactSourceKind.UserNote ? notes : urls).Add(candidate);
        }

        var keptNoteCount = Math.Min(notes.Count, limits.MaxSources);
        var keptUrlCount = Math.Min(urls.Count, limits.MaxSources - keptNoteCount);

        var keptIds = new HashSet<Guid>(keptNoteCount + keptUrlCount);
        for (var i = 0; i < keptNoteCount; i++)
        {
            keptIds.Add(notes[i].SourceId);
        }

        for (var i = 0; i < keptUrlCount; i++)
        {
            keptIds.Add(urls[i].SourceId);
        }

        // Aliases follow creation order rather than the order the two passes filled the slots: the kept set is
        // re-read from the creation-ordered list, so s1…sN stays the order the model sees. A source missing from
        // it was dropped for empty text or lost the ceiling — both are reported, neither is an error, and the
        // excess is never alias s11.
        var kept = new List<FactSourceCandidate>(keptIds.Count);
        var excluded = new List<Guid>();
        foreach (var candidate in ordered)
        {
            if (keptIds.Contains(candidate.SourceId))
            {
                kept.Add(candidate);
            }
            else
            {
                excluded.Add(candidate.SourceId);
            }
        }

        if (kept.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidates), candidates, "Every source carries no text.");
        }

        var ceiling = HardTextCeiling(limits.PerSourceCharCap);
        var floor = Math.Max(MinimumTruncationLength, Math.Min(FactInputLimits.MinimumPerSourceCharCap, limits.PerSourceCharCap));
        var count = kept.Count;
        var titles = new string[count];
        var full = new string[count];
        var texts = new string[count];

        for (var i = 0; i < count; i++)
        {
            // A title is a label, never fact material, and it was the one string of the data object with no
            // ceiling at all: it is cut to MaxTitleChars on a surrogate-pair boundary, then sanitised, so an
            // over-long or hostile title is never copied whole.
            titles[i] = FactDataObjectJson.Sanitize(CapTitle(kept[i].Title));
        }

        // A user_note is never truncated (README CR-4) and the store caps it at StorySource.NoteMaxChars
        // (3,000), so a note beyond that contract — or beyond the hard ceiling — could never be brought under
        // the budget. The plan reports OverBudget without scanning, sanitising or serialising the oversized
        // text and never throws: its texts stay exactly as the caller supplied them and EstimatedTokens is the
        // sentinel no ceiling accepts, so such a plan is never sent.
        foreach (var candidate in kept)
        {
            if (candidate.Kind != FactSourceKind.UserNote)
            {
                continue;
            }

            var noteText = candidate.Text!;
            if (noteText.Length > StorySource.NoteMaxChars || noteText.Length > ceiling)
            {
                return new FactInputPlan(
                    BuildInputs(kept, titles, [.. kept.Select(item => item.Text!)]),
                    excluded,
                    [],
                    int.MaxValue,
                    FactInputBudgetStatus.OverBudget);
            }
        }

        for (var i = 0; i < count; i++)
        {
            var raw = kept[i].Text!;

            // The hard ceiling comes before every other step: a url text beyond it is reduced by a cheap,
            // deterministic, surrogate-safe head 60 % / tail 40 % index cut, so the sanitise, the normalise,
            // the element walk and the estimate stay bounded. The cut below still truncates the source, which
            // is what reports it in TruncatedAliases — the ceiling cut carries no marker of its own.
            if (kept[i].Kind == FactSourceKind.Url && raw.Length > ceiling)
            {
                raw = CeilingCut(raw, ceiling);
            }

            // NFC, so the cap decision, the cut and the estimate all read the same text; and sanitised, so no
            // unpaired surrogate or C0 control character reaches the object or the estimate. The plan's own
            // sources are therefore already sanitised, which is what lets the estimate serialise them without
            // sanitising them again (SerializeSanitised) and still take the exact bytes the extractor sends.
            full[i] = FactDataObjectJson.Sanitize(raw).Normalize(NormalizationForm.FormC);

            if (kept[i].Kind == FactSourceKind.UserNote)
            {
                texts[i] = full[i];
                continue;
            }

            texts[i] = Truncate(full[i], limits.PerSourceCharCap);
        }

        var json = Serialize(language, kept, titles, texts);
        var tokens = EstimateTokens(json);

        while (tokens > limits.MaxInputTokens)
        {
            var index = LargestTruncatable(kept, texts, floor);
            if (index < 0)
            {
                break;
            }

            // Halve the picked source's own truncated length: it is longer than the floor, so the new cap is
            // strictly smaller than the length that source just produced, and a source whose text did not
            // shrink is never picked again. Every pass therefore makes progress and the loop stops at the floor.
            texts[index] = Truncate(full[index], Math.Max(floor, texts[index].Length / 2));

            json = Serialize(language, kept, titles, texts);
            tokens = EstimateTokens(json);
        }

        var sources = BuildInputs(kept, titles, texts);
        var truncated = new List<string>();
        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(texts[i], full[i], StringComparison.Ordinal))
            {
                truncated.Add(sources[i].Alias);
            }
        }

        return new FactInputPlan(
            sources,
            excluded,
            truncated,
            tokens,
            tokens <= limits.MaxInputTokens ? FactInputBudgetStatus.WithinBudget : FactInputBudgetStatus.OverBudget);
    }

    /// <summary>
    /// The index of the truncatable source whose truncated text is the longest, or <c>-1</c> when none can
    /// shrink further. The size is the length the source's text <em>actually</em> has after its cut — never a
    /// cap it did not reach — so a source whose text did not shrink is not picked over and over.
    /// </summary>
    private static int LargestTruncatable(List<FactSourceCandidate> kept, string[] texts, int floor)
    {
        var best = -1;
        var bestSize = 0;
        for (var i = 0; i < kept.Count; i++)
        {
            if (kept[i].Kind != FactSourceKind.Url)
            {
                continue;
            }

            var size = texts[i].Length;
            if (size <= floor || size <= bestSize)
            {
                continue;
            }

            best = i;
            bestSize = size;
        }

        return best;
    }

    /// <summary>
    /// Serialises the object the estimate is taken from. The plan's sources are already sanitised — the titles
    /// by <see cref="CapTitle"/> then <see cref="FactDataObjectJson.Sanitize"/>, the texts by the sanitise in
    /// <see cref="Plan"/> and by cuts that only ever remove whole text elements or surrogate pairs, so no
    /// control character and no lone surrogate can appear in them — so sanitising them again on every pass of
    /// the total-budget loop would only repeat work over the same characters.
    /// <see cref="FactDataObjectJson.SerializeSanitised"/> writes the same bytes
    /// <see cref="FactDataObjectJson.Serialize"/> writes for the returned sources, because sanitising an
    /// already-sanitised string changes nothing.
    /// </summary>
    private static string Serialize(Language language, List<FactSourceCandidate> kept, string[] titles, string[] texts) =>
        FactDataObjectJson.SerializeSanitised(language, BuildInputs(kept, titles, texts));

    private static FactSourceInput[] BuildInputs(List<FactSourceCandidate> kept, string[] titles, string[] texts)
    {
        var inputs = new FactSourceInput[kept.Count];
        for (var i = 0; i < kept.Count; i++)
        {
            inputs[i] = new FactSourceInput(
                string.Create(CultureInfo.InvariantCulture, $"s{i + 1}"),
                kept[i].SourceId,
                titles[i],
                texts[i],
                kept[i].Kind == FactSourceKind.UserNote);
        }

        return inputs;
    }

    /// <summary>
    /// Cuts <paramref name="text"/> to at most <paramref name="cap"/> characters as head 60 % + marker + tail
    /// 40 % <em>of the characters the marker leaves</em>, on text-element boundaries so a surrogate pair or a
    /// combining sequence is never split (02.5.3-T1). The marker is reserved inside the cap, so a cut source is
    /// never longer than <paramref name="cap"/> — without that, a source a few characters over the cap would
    /// come back longer than it went in. A text at or under the cap is returned untouched, and a text over it
    /// always loses characters, so a reported truncation is always a real cut.
    /// <para>
    /// The walk is bounded: it stops at the head target, and the tail boundary is found on the same pass, which
    /// never reaches the end of a long text. When the boundary at or after a target is further than
    /// <see cref="MaxClusterWindowChars"/> past it — a text that is one huge grapheme cluster, a base character
    /// with hundreds of thousands of combining marks or ZWJ extenders — the cut falls back to a surrogate-safe
    /// UTF-16 index at the target, so neither the walk nor the scan is unbounded and the result is always
    /// well-formed. Deterministic: the same text and cap always yield the same cut.
    /// </para>
    /// </summary>
    private static string Truncate(string text, int cap)
    {
        if (text.Length <= cap)
        {
            return text;
        }

        var span = text.AsSpan();

        // Below the marker plus a character of head and tail there is no room for a marked cut: the head alone
        // is kept, which is still a real cut and still never longer than the cap.
        if (cap < MinimumTruncationLength)
        {
            return text[..SafeHeadIndex(span, cap)];
        }

        var kept = cap - TruncationMarker.Length;
        var headTarget = (int)(kept * 6L / 10);

        // Pass one: the head boundary, walking no further than the window past the target.
        var position = 0;
        while (position < headTarget)
        {
            position += StringInfo.GetNextTextElementLength(span[position..]);
        }

        var headEnd = position - headTarget <= MaxClusterWindowChars ? position : headTarget;
        if (headEnd > kept)
        {
            headEnd = kept;
        }

        headEnd = SafeHeadIndex(span, headEnd);

        var tailBudget = kept - headEnd;
        if (tailBudget == 0)
        {
            return string.Concat(span[..headEnd], TruncationMarker.AsSpan());
        }

        // Pass two: the tail boundary. It continues the same walk, so every position it visits is a real
        // boundary and no text before the head target is examined twice.
        var tailIndex = Math.Max(headEnd, text.Length - tailBudget);
        var tailStart = text.Length;
        while (position < text.Length)
        {
            if (position >= tailIndex)
            {
                tailStart = position - tailIndex <= MaxClusterWindowChars ? position : tailIndex;
                break;
            }

            position += StringInfo.GetNextTextElementLength(span[position..]);
        }

        tailStart = Math.Max(headEnd, SafeTailIndex(span, tailStart));

        return string.Concat(span[..headEnd], TruncationMarker.AsSpan(), span[tailStart..]);
    }

    /// <summary>
    /// The cheap, deterministic reduction of a url text above the hard ceiling: the head 60 % and the tail 40 %
    /// of the ceiling, cut on surrogate-pair boundaries. It carries no marker — the cut in
    /// <see cref="Truncate"/> is what marks and reports the truncation — and its result is never longer than
    /// the ceiling.
    /// </summary>
    private static string CeilingCut(string text, int ceiling)
    {
        var head = (int)(ceiling * 6L / 10);
        var span = text.AsSpan();
        var headEnd = SafeHeadIndex(span, head);
        var tailStart = Math.Max(headEnd, text.Length - (ceiling - head));

        return string.Concat(span[..headEnd], span[SafeTailIndex(span, tailStart)..]);
    }

    /// <summary>The hard ceiling of a url text: a multiple of the per-source cap, never overflowing.</summary>
    private static int HardTextCeiling(int perSourceCharCap) =>
        perSourceCharCap > int.MaxValue / HardTextCeilingMultiplier
            ? int.MaxValue
            : perSourceCharCap * HardTextCeilingMultiplier;

    /// <summary>Cuts a title to <see cref="MaxTitleChars"/>, on a surrogate-pair boundary; shorter titles are returned as they are.</summary>
    private static string CapTitle(string title) =>
        title.Length <= MaxTitleChars ? title : title[..SafeHeadIndex(title.AsSpan(), MaxTitleChars)];

    /// <summary>The largest index at or before <paramref name="index"/> that does not sit between a surrogate pair.</summary>
    private static int SafeHeadIndex(ReadOnlySpan<char> span, int index)
    {
        if (index >= span.Length)
        {
            index = span.Length;
        }

        return index > 0 && index < span.Length && char.IsHighSurrogate(span[index - 1]) && char.IsLowSurrogate(span[index])
            ? index - 1
            : Math.Max(index, 0);
    }

    /// <summary>The smallest index at or after <paramref name="index"/> that does not start on an unpaired low surrogate.</summary>
    private static int SafeTailIndex(ReadOnlySpan<char> span, int index)
    {
        if (index <= 0)
        {
            return 0;
        }

        if (index >= span.Length)
        {
            return span.Length;
        }

        return char.IsLowSurrogate(span[index]) && char.IsHighSurrogate(span[index - 1]) ? index + 1 : index;
    }

    private static void GuardLimits(FactInputLimits limits)
    {
        if (limits.MaxSources < 1 || limits.MaxSources > FactInputLimits.MaxSourcesCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), limits.MaxSources, $"MaxSources must be 1..{FactInputLimits.MaxSourcesCeiling}.");
        }

        if (limits.PerSourceCharCap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), limits.PerSourceCharCap, "PerSourceCharCap must be positive.");
        }

        if (limits.MaxInputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), limits.MaxInputTokens, "MaxInputTokens must be positive.");
        }
    }
}
