using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Content;

namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// The real-world-framing detector of 02.8.2-T2 (02 README F31, task §"Real-world detection"): it turns one story
/// version's text and the review model's claims into the hits that fail gate check 8.
/// </summary>
/// <remarks>
/// <para>
/// Three signals and no fourth: a framing phrase from the lexicon (<see cref="RealWorldSignalKind.FramingLexicon"/>),
/// a date together with an incident word in the same segment (<see cref="RealWorldSignalKind.DatedIncident"/>), and
/// a non-empty <c>model_review.real_world_claims[]</c> (<see cref="RealWorldSignalKind.ModelClaim"/>). There is
/// deliberately no named-entity signal: a detector cannot separate an invented name from a real one, and a name
/// list would both miss real people and hold every ordinary story.
/// </para>
/// <para>
/// Pure and deterministic: no clock, no IO and no randomness. Matching is case- and diacritic-insensitive over
/// <em>our own</em> draft only — the owner's brief, notes and fact statements never reach it — and every quoted
/// fragment is at most <see cref="ValidationIssue.MaxEvidenceChars"/> characters of our text (D22), with the lexicon
/// phrase or the incident word as the matched phrase (never the review model's wording).
/// </para>
/// <para>
/// Text is prepared before matching, and the segment, every lexicon phrase and every incident word go through the
/// same preparation: invisible code points (the format category and variation selectors) are dropped, every
/// whitespace code point becomes one ASCII space and whitespace runs collapse to one, combining marks left after
/// NFC are dropped, and letters are invariant-lower-cased. So an NBSP, a double space, a newline or tab inside a
/// phrase, a zero-width space inside a word and a fully decomposed (NFD) Vietnamese word all match the release
/// lexicon, and decomposed input folds to the same string as composed input. Evidence is still quoted from the raw
/// segment through an index map (prepared position to raw position), so it carries our spelling and our invisible
/// characters, and an empty map means the two forms are the same length and index.
/// </para>
/// <para>
/// Hostile input cannot cost unbounded time: phrases and incident words are found with an ordinal search (no
/// pattern is built from configuration), the date patterns are source-generated with
/// <see cref="RegexOptions.NonBacktracking"/> so they are linear in the segment length, and every scan is a single
/// left-to-right pass. Noncharacters, unpaired surrogates and invisible characters are dropped before the NFC step,
/// because ICU refuses some input on Linux and the detector must report on hostile text, not throw on it.
/// </para>
/// <para>
/// Known limit: Cyrillic and Greek homoglyphs (for example <c>а</c>, <c>е</c>, <c>о</c>, <c>р</c>, <c>с</c>) and other
/// mixed-script confusables do not fold to their Latin lookalikes, so a phrase spelled with them is not matched.
/// </para>
/// </remarks>
public static partial class RealWorldSignalDetector
{
    /// <summary>
    /// How many characters of our draft a hit may quote (02 README D22, the same cap the local issues carry).
    /// </summary>
    private const int EvidenceChars = ValidationIssue.MaxEvidenceChars;

    /// <summary>How long the matched phrase we echo may be, so a hostile lexicon line cannot travel whole.</summary>
    private const int MatchedPhraseChars = 100;

    /// <summary>
    /// Detects the real-world signals of one story version. Hits are ordered by segment order, then by
    /// <see cref="RealWorldSignalKind"/> (framing, dated incident, claim), then by position in the segment, so the
    /// same input always yields the same list and the owner reads it in the order the story reads.
    /// </summary>
    /// <param name="segments">The version's text in D22 addressing; blank texts are already absent.</param>
    /// <param name="framingPhrases">
    /// The lexicon phrases of the series language (<c>prompts/shared/real-world-framing.{vi|en}.v1.txt</c>). They
    /// match case- and diacritic-insensitively on a word boundary; blank entries are ignored.
    /// </param>
    /// <param name="modelClaims">The review model's claims, reduced to their segment addresses.</param>
    /// <param name="options">Whether a date needs an incident word, and which words count.</param>
    /// <returns><see cref="RealWorldSignal.Hit"/> with every hit, or no hit at all.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static RealWorldSignal Detect(
        StoryTextSegments segments,
        IReadOnlyList<string> framingPhrases,
        IReadOnlyList<RealWorldClaim> modelClaims,
        RealWorldOptions options)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(framingPhrases);
        ArgumentNullException.ThrowIfNull(modelClaims);
        ArgumentNullException.ThrowIfNull(options);

        var phrases = FoldedPhrases(framingPhrases);
        var incidents = FoldedPhrases(options.IncidentWords);
        var candidates = new List<Candidate>();
        var sequence = 0;

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var prepared = Prepare(segment.Text);

            foreach (var (offset, hit) in FramingHits(segment, prepared, phrases))
            {
                candidates.Add(new Candidate(index, RealWorldSignalKind.FramingLexicon, offset, sequence++, hit));
            }

            foreach (var (offset, hit) in DatedIncidentHits(segment, prepared, incidents, options.RequireIncidentCoOccurrence))
            {
                candidates.Add(new Candidate(index, RealWorldSignalKind.DatedIncident, offset, sequence++, hit));
            }
        }

        AddClaimHits(segments, modelClaims, candidates, ref sequence);

        if (candidates.Count == 0)
        {
            return new RealWorldSignal(false, []);
        }

        candidates.Sort(static (left, right) =>
        {
            var order = left.SegmentIndex.CompareTo(right.SegmentIndex);
            if (order != 0)
            {
                return order;
            }

            order = ((int)left.Kind).CompareTo((int)right.Kind);
            if (order != 0)
            {
                return order;
            }

            order = left.Offset.CompareTo(right.Offset);
            return order != 0 ? order : left.Sequence.CompareTo(right.Sequence);
        });

        var hits = new RealWorldSignalHit[candidates.Count];
        for (var index = 0; index < hits.Length; index++)
        {
            hits[index] = candidates[index].Hit;
        }

        return new RealWorldSignal(true, hits);
    }

    /// <summary>
    /// One hit per claim, in claim order. The claim's own wording is never carried: the hit names the claim's D22
    /// address and quotes the leading characters of that segment of our draft, or nothing when the address does not
    /// resolve (a reviewer's claim still counts even when its address does not name a segment of this version).
    /// </summary>
    private static void AddClaimHits(
        StoryTextSegments segments,
        IReadOnlyList<RealWorldClaim> claims,
        List<Candidate> candidates,
        ref int sequence)
    {
        if (claims.Count == 0)
        {
            return;
        }

        var byAddress = new Dictionary<string, int>(segments.Count, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < segments.Count; index++)
        {
            byAddress[segments[index].Id] = index;
        }

        for (var index = 0; index < claims.Count; index++)
        {
            var address = claims[index]?.Segment?.Trim() ?? string.Empty;
            var resolved = byAddress.TryGetValue(address, out var segmentIndex);
            candidates.Add(new Candidate(
                resolved ? segmentIndex : int.MaxValue,
                RealWorldSignalKind.ModelClaim,
                index,
                sequence++,
                new RealWorldSignalHit(
                    address,
                    RealWorldSignalKind.ModelClaim,
                    MatchedPhrase: string.Empty,
                    Evidence: resolved ? ValidationIssue.Snip(segments[segmentIndex].Text) : string.Empty)));
        }
    }

    /// <summary>
    /// The framing hits of one segment: the first whole-word occurrence of each phrase, longest phrase first, so
    /// "câu chuyện có thật" is reported as itself rather than also as the "chuyện có thật" it contains.
    /// </summary>
    private static List<(int Offset, RealWorldSignalHit Hit)> FramingHits(
        StoryTextSegment segment,
        PreparedText prepared,
        IReadOnlyList<Phrase> phrases)
    {
        var hits = new List<(int Offset, RealWorldSignalHit Hit)>();
        if (phrases.Count == 0)
        {
            return hits;
        }

        var taken = new List<(int Start, int End)>(phrases.Count);

        foreach (var phrase in phrases)
        {
            foreach (var at in BoundedOccurrences(prepared.Folded, phrase.Folded))
            {
                var end = at + phrase.Folded.Length;
                if (Overlaps(taken, at, end))
                {
                    continue;
                }

                taken.Add((at, end));
                hits.Add((at, new RealWorldSignalHit(
                    segment.Id,
                    RealWorldSignalKind.FramingLexicon,
                    phrase.Display,
                    EvidenceAround(segment.Text, prepared, at, phrase.Folded.Length))));
                break;
            }
        }

        return hits;
    }

    /// <summary>
    /// The dated-incident hits of one segment. With co-occurrence required (the default) the segment needs a date
    /// pattern and an incident word, and each distinct incident word yields one hit; with it switched off a
    /// specific date alone yields one hit. A bare year ("năm 2020") is never a hit on its own in either mode — it
    /// is the main false-positive control (task §"Incident words").
    /// </summary>
    private static List<(int Offset, RealWorldSignalHit Hit)> DatedIncidentHits(
        StoryTextSegment segment,
        PreparedText prepared,
        IReadOnlyList<Phrase> incidents,
        bool requireCoOccurrence)
    {
        var hits = new List<(int Offset, RealWorldSignalHit Hit)>();
        var text = prepared.Folded;

        if (requireCoOccurrence)
        {
            if (incidents.Count == 0)
            {
                return hits;
            }

            var found = new List<(int Offset, Phrase Word)>(incidents.Count);
            foreach (var word in incidents)
            {
                foreach (var at in BoundedOccurrences(text, word.Folded))
                {
                    found.Add((at, word));
                    break;
                }
            }

            // The date patterns cost more than the word scan, so a segment without an incident word never runs one.
            if (found.Count == 0 || !HasDatePattern(text))
            {
                return hits;
            }

            foreach (var (at, word) in found)
            {
                hits.Add((at, new RealWorldSignalHit(
                    segment.Id,
                    RealWorldSignalKind.DatedIncident,
                    word.Display,
                    EvidenceAround(segment.Text, prepared, at, word.Folded.Length))));
            }

            return hits;
        }

        if (!TryFirstSpecificDate(text, out var start, out var length))
        {
            return hits;
        }

        hits.Add((start, new RealWorldSignalHit(
            segment.Id,
            RealWorldSignalKind.DatedIncident,
            DateText(segment.Text, prepared, start, length),
            EvidenceAround(segment.Text, prepared, start, length))));
        return hits;
    }

    /// <summary>Whether a segment carries a date pattern at all: a specific date, or a bare year.</summary>
    private static bool HasDatePattern(string text) =>
        TryFirstSpecificDate(text, out _, out _) || YearOnly().IsMatch(text);

    /// <summary>
    /// The leftmost specific date of the segment across all patterns: a day and a month with a year, a month and a
    /// year, or a numeric date. A bare year and a bare month/year number are not specific dates.
    /// </summary>
    private static bool TryFirstSpecificDate(string text, out int index, out int length)
    {
        index = int.MaxValue;
        length = 0;

        Consider(ViNumericDate().Match(text), ref index, ref length);
        Consider(ViLongDate().Match(text), ref index, ref length);
        Consider(ViMonthYear().Match(text), ref index, ref length);
        Consider(EnMonthDate().Match(text), ref index, ref length);
        Consider(EnDayMonth().Match(text), ref index, ref length);
        Consider(EnNumericDate().Match(text), ref index, ref length);

        return length > 0;
    }

    private static void Consider(Match match, ref int index, ref int length)
    {
        if (match.Success && match.Index < index)
        {
            index = match.Index;
            length = match.Length;
        }
    }

    /// <summary>The matched date as our draft spells it, so a switch-off hit still says what it matched.</summary>
    private static string DateText(string raw, PreparedText prepared, int preparedIndex, int preparedLength)
    {
        var start = prepared.RawStart(preparedIndex);
        var end = Math.Min(raw.Length, prepared.RawStart(preparedIndex + preparedLength - 1) + 1);
        return end > start ? Clip(raw[start..end], MatchedPhraseChars) : string.Empty;
    }

    /// <summary>
    /// At most <see cref="EvidenceChars"/> characters of <paramref name="raw"/> around the matched span: the whole
    /// segment when it already fits, otherwise a window centred on the match that never splits a surrogate pair at
    /// either edge.
    /// </summary>
    private static string EvidenceAround(string raw, PreparedText prepared, int preparedIndex, int preparedLength)
    {
        if (raw.Length <= EvidenceChars)
        {
            return raw;
        }

        var start = prepared.RawStart(preparedIndex);
        var end = Math.Min(raw.Length, prepared.RawStart(preparedIndex + preparedLength - 1) + 1);
        var matchLength = Math.Max(1, end - start);
        var pad = Math.Max(0, (EvidenceChars - matchLength) / 2);
        var from = Math.Clamp(start - pad, 0, raw.Length - EvidenceChars);
        var to = Math.Min(from + EvidenceChars, raw.Length);

        if (from > 0 && char.IsLowSurrogate(raw[from]) && char.IsHighSurrogate(raw[from - 1]))
        {
            from++;
        }

        if (to < raw.Length && char.IsHighSurrogate(raw[to - 1]) && char.IsLowSurrogate(raw[to]))
        {
            to--;
        }

        return raw[from..to];
    }

    /// <summary>The bounded, folded form of each configured phrase, first spelling wins, blanks dropped, longest first.</summary>
    private static List<Phrase> FoldedPhrases(IReadOnlyList<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var phrases = new List<Phrase>(values.Count);

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var display = Clip(value.Trim(), MatchedPhraseChars);
            var folded = Prepare(display).Folded;
            if (folded.Length > 0 && seen.Add(folded))
            {
                phrases.Add(new Phrase(display, folded));
            }
        }

        phrases.Sort(static (left, right) =>
        {
            var order = right.Folded.Length.CompareTo(left.Folded.Length);
            return order != 0 ? order : string.CompareOrdinal(left.Folded, right.Folded);
        });

        return phrases;
    }

    /// <summary>Every whole-word occurrence of <paramref name="needle"/>, left to right; both sides are folded already.</summary>
    private static IEnumerable<int> BoundedOccurrences(string text, string needle)
    {
        var start = 0;

        while (start <= text.Length - needle.Length)
        {
            var found = text.AsSpan(start).IndexOf(needle.AsSpan(), StringComparison.Ordinal);
            if (found < 0)
            {
                yield break;
            }

            var at = start + found;
            if (IsWholeWord(text, at, needle.Length))
            {
                yield return at;
            }

            start = at + 1;
        }
    }

    /// <summary>
    /// Whether the match at <paramref name="start"/> is a whole word: no letter or digit directly before or after
    /// it. Digits count, so a phrase cannot match inside <c>chuyện1</c>, and a match inside <c>untrue story</c> is
    /// not the phrase <c>true story</c>.
    /// </summary>
    private static bool IsWholeWord(string text, int start, int length)
    {
        var end = start + length;
        return (start == 0 || !IsWordCharacterBefore(text, start))
            && (end >= text.Length || !IsWordCharacterAt(text, end));
    }

    private static bool IsWordCharacterAt(string text, int index) =>
        char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
            ? Rune.IsLetterOrDigit(new Rune(text[index], text[index + 1]))
            : char.IsLetterOrDigit(text[index]);

    private static bool IsWordCharacterBefore(string text, int index)
    {
        var previous = index - 1;
        return char.IsLowSurrogate(text[previous]) && previous > 0 && char.IsHighSurrogate(text[previous - 1])
            ? Rune.IsLetterOrDigit(new Rune(text[previous - 1], text[previous]))
            : char.IsLetterOrDigit(text[previous]);
    }

    private static bool Overlaps(List<(int Start, int End)> taken, int start, int end)
    {
        foreach (var span in taken)
        {
            if (start < span.End && span.Start < end)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The text as the matcher sees it: invisible code points dropped, whitespace collapsed to one ASCII space,
    /// NFC-composed, diacritic-folded and lower-cased, with a map back to the raw segment so evidence is always
    /// quoted from the draft and not from this derived form. <c>null</c> means prepared index == raw index, which is
    /// the common case: already composed, plainly spaced text with no invisible or mark code point in it.
    /// </summary>
    private static PreparedText Prepare(string text)
    {
        if (text.Length == 0)
        {
            return new PreparedText(string.Empty, null);
        }

        if (IsPlain(text) && IsComposed(text))
        {
            return new PreparedText(Fold(text), null);
        }

        var filtered = new char[text.Length];
        var rawIndex = new int[text.Length];
        var count = Filter(text, filtered, rawIndex);
        if (count == 0)
        {
            return new PreparedText(string.Empty, null);
        }

        return ComposeAndFold(filtered.AsSpan(0, count), rawIndex);
    }

    /// <summary>
    /// Whether the text needs nothing beyond the case and diacritic fold: every code point is a BMP scalar that is
    /// neither a noncharacter nor invisible nor a combining mark, and the only whitespace is a single ASCII space.
    /// This is what keeps the common path free of an index map, and it is one linear pass like every other step.
    /// </summary>
    private static bool IsPlain(string text)
    {
        var previousWasSpace = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (character == ' ')
            {
                if (previousWasSpace)
                {
                    return false;
                }

                previousWasSpace = true;
                continue;
            }

            previousWasSpace = false;

            if (character < '\u0080')
            {
                // Only a printable ASCII code point is plain: a tab, a line break or a form feed is whitespace and
                // has to be collapsed like any other whitespace run.
                if (char.IsWhiteSpace(character))
                {
                    return false;
                }

                continue;
            }

            // A surrogate pair takes the slow path as well: folding a supplementary letter can move it between
            // planes, and the map back to the raw segment must stay exact.
            var status = Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
            if (status != OperationStatus.Done || consumed > 1 || NeedsPreparation(rune))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a code point changes under preparation: a noncharacter, an invisible one (format category or
    /// variation selector), a combining mark, or whitespace other than the ASCII space.
    /// </summary>
    private static bool NeedsPreparation(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return BibleOpNormalizer.IsNoncharacter(rune.Value)
            || category is UnicodeCategory.Format
                or UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark
            || IsVariationSelector(rune)
            || Rune.IsWhiteSpace(rune);
    }

    /// <summary>
    /// The slow path: one canonical unit at a time (a starter and the combining marks that follow it), composed to
    /// NFC, then folded, with each prepared UTF-16 unit mapped to the raw index its unit started at.
    /// </summary>
    private static PreparedText ComposeAndFold(ReadOnlySpan<char> text, int[] rawIndex)
    {
        var folded = new StringBuilder(text.Length);
        var source = new List<int>(text.Length);
        var position = 0;

        while (position < text.Length)
        {
            var unitStart = position;
            var unitRawStart = rawIndex[position];
            var unitStatus = Rune.DecodeFromUtf16(text[position..], out var first, out var consumed);
            if (unitStatus != OperationStatus.Done || consumed == 0)
            {
                // Filter only keeps whole scalars, so this is unreachable; it keeps a bad span from looping forever.
                position++;
                continue;
            }

            position += consumed;

            var marks = 0;
            while (position < text.Length)
            {
                var status = Rune.DecodeFromUtf16(text[position..], out var mark, out var markConsumed);
                if (status != OperationStatus.Done || !IsCombiningMark(mark))
                {
                    break;
                }

                position += markConsumed;
                marks++;
            }

            if (marks == 0)
            {
                AppendFolded(folded, source, unitRawStart, first);
                continue;
            }

            // Exactly where NFC composition happens, and the one place a mark can lose its base: whatever mark is
            // still a separate code point after composition cannot compose, so it is dropped and the composed and
            // decomposed spellings of the same word fold alike.
            var composed = Normalise(new string(text[unitStart..position]));
            var at = 0;
            while (at < composed.Length)
            {
                var status = Rune.DecodeFromUtf16(composed.AsSpan(at), out var composedRune, out var composedConsumed);
                at += status == OperationStatus.Done ? composedConsumed : 1;
                if (status == OperationStatus.Done)
                {
                    AppendFolded(folded, source, unitRawStart, composedRune);
                }
            }
        }

        return new PreparedText(folded.ToString(), [.. source]);
    }

    /// <summary>
    /// Appends one prepared code point and its raw index: a mark that composition left behind has no base to carry
    /// and is dropped, whitespace becomes one ASCII space (a run collapses to the first one), and every other code
    /// point takes the fold map's base letter or its invariant lower case.
    /// </summary>
    private static void AppendFolded(StringBuilder folded, List<int> source, int rawIndex, Rune rune)
    {
        if (IsCombiningMark(rune))
        {
            return;
        }

        if (Rune.IsWhiteSpace(rune))
        {
            if (folded.Length > 0 && folded[^1] == ' ')
            {
                return;
            }

            folded.Append(' ');
            source.Add(rawIndex);
            return;
        }

        Span<char> buffer = stackalloc char[2];
        var written = Fold(rune).EncodeToUtf16(buffer);

        for (var index = 0; index < written; index++)
        {
            folded.Append(buffer[index]);
            source.Add(rawIndex);
        }
    }

    private static string Normalise(string unit)
    {
        try
        {
            return unit.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // The ICU backend refuses some input (verified: noncharacters, 02.6.3 gate). Fold the unit as it is
            // instead of failing a gate the owner must read.
            return unit;
        }
    }

    /// <summary>
    /// The text with every unpaired surrogate, noncharacter, invisible code point (format category, variation
    /// selector) removed, every whitespace code point mapped to one ASCII space and whitespace runs collapsed, plus
    /// the raw index of each kept UTF-16 unit. Returns the number of kept UTF-16 units. The filter is what makes the
    /// NFC step safe on hostile input (see <see cref="BibleOpNormalizer.IsNoncharacter"/>, the set the bible and
    /// keyword rules share) and what keeps a pasted NBSP, a stray tab or a zero-width space from breaking a match.
    /// </summary>
    private static int Filter(string text, char[] destination, int[] rawIndex)
    {
        var position = 0;
        var count = 0;
        var previousWasSpace = false;

        while (position < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(position), out var rune, out var consumed);

            if (status != OperationStatus.Done
                || BibleOpNormalizer.IsNoncharacter(rune.Value)
                || IsInvisible(rune))
            {
                position += status == OperationStatus.Done ? consumed : 1;
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (!previousWasSpace)
                {
                    destination[count] = ' ';
                    rawIndex[count] = position;
                    count++;
                    previousWasSpace = true;
                }

                position += consumed;
                continue;
            }

            previousWasSpace = false;

            for (var offset = 0; offset < consumed; offset++)
            {
                destination[count] = text[position + offset];
                rawIndex[count] = position;
                count++;
            }

            position += consumed;
        }

        return count;
    }

    /// <summary>
    /// An invisible code point: the format category (soft hyphen, zero-width space and joiners, bidi controls, BOM,
    /// Mongolian vowel separator) and the variation selectors, which are marks but scalar-value modifiers rather
    /// than diacritics.
    /// </summary>
    private static bool IsInvisible(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format || IsVariationSelector(rune);

    /// <summary>A variation selector, BMP or supplementary: it selects a glyph variant and carries no letter.</summary>
    private static bool IsVariationSelector(Rune rune) =>
        rune.Value is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF;

    /// <summary>
    /// Whether the NFC step can be skipped: the text is already in form C, or the ICU backend refuses it, in which
    /// case folding the text as it is beats throwing on a story the gate has to judge.
    /// </summary>
    private static bool IsComposed(string text)
    {
        try
        {
            return text.IsNormalized(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool IsCombiningMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    /// <summary>
    /// The fast path's fold, one UTF-16 unit at a time. That path only ever sees BMP scalars that are not marks
    /// (<see cref="IsPlain"/>), so a unit-to-unit fold is the same as <see cref="Fold(Rune)"/> and the identity map
    /// stays exact.
    /// </summary>
    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(Fold(character));
        }

        return builder.ToString();
    }

    /// <summary>One character to its comparison form: the fold map's base letter, else its invariant lower case.</summary>
    private static char Fold(char character) =>
        FoldMap.TryGetValue(character, out var baseLetter) ? baseLetter : char.ToLowerInvariant(character);

    /// <summary>
    /// One code point to its comparison form: the fold map's base letter (a precomposed Vietnamese letter), else its
    /// invariant lower case, so no machine culture can change a fold.
    /// </summary>
    private static Rune Fold(Rune rune) =>
        rune.IsBmp && FoldMap.TryGetValue((char)rune.Value, out var baseLetter)
            ? new Rune(baseLetter)
            : Rune.ToLowerInvariant(rune);

    private static string Clip(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[maxChars - 1]) && char.IsLowSurrogate(text[maxChars])
            ? maxChars - 1
            : maxChars;
        return text[..length];
    }

    /// <summary>
    /// The diacritic fold used by <em>this detector only</em> (task §Notes): every Vietnamese letter, either case,
    /// to its base letter. It exists because owners write briefs without diacritics, and it must never leak into
    /// search or keyword rules where precision matters.
    /// </summary>
    private static readonly Dictionary<char, char> FoldMap = BuildFoldMap();

    private static Dictionary<char, char> BuildFoldMap()
    {
        (char Base, string Accented)[] groups =
        [
            ('a', "àáảãạăằắẳẵặâầấẩẫậ"),
            ('e', "èéẻẽẹêềếểễệ"),
            ('i', "ìíỉĩị"),
            ('o', "òóỏõọôồốổỗộơờớởỡợ"),
            ('u', "ùúủũụưừứửữự"),
            ('y', "ỳýỷỹỵ"),
            ('d', "đ"),
        ];

        var map = new Dictionary<char, char>(148);
        foreach (var (baseLetter, accented) in groups)
        {
            foreach (var character in accented)
            {
                map[character] = baseLetter;
                map[char.ToUpperInvariant(character)] = baseLetter;
            }
        }

        return map;
    }

    /// <summary>A configured phrase: the spelling we echo and the folded form we search for.</summary>
    private readonly record struct Phrase(string Display, string Folded);

    /// <summary>The matched text with a map back to the raw segment; <c>null</c> means prepared index == raw index.</summary>
    private readonly record struct PreparedText(string Folded, int[]? Source)
    {
        public int RawStart(int preparedIndex) => Source is null ? preparedIndex : Source[preparedIndex];
    }

    /// <summary>One hit with the ordering keys of <see cref="Detect"/>.</summary>
    private readonly record struct Candidate(
        int SegmentIndex,
        RealWorldSignalKind Kind,
        int Offset,
        int Sequence,
        RealWorldSignalHit Hit);

    /// <summary>The vi numeric date, optionally prefixed with "ngày" (folded spelling: the matcher folds first).</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\b(?:ngay\s+)?\d{1,2}[/-]\d{1,2}[/-](?:19|20)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ViNumericDate();

    /// <summary>The vi long date, "ngày 12 tháng 3" or "ngày 12 tháng 3 năm 2019".</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\bngay\s+\d{1,2}\s+thang\s+\d{1,2}(?:\s+nam\s+(?:19|20)\d{2})?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ViLongDate();

    /// <summary>
    /// The vi month and year without a day, "tháng 3 năm 2019" or "tháng 3/2019". A bare month/year number ("3/2019")
    /// is deliberately absent: it is as likely a chapter count as a date.
    /// </summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\bthang\s+\d{1,2}(?:\s+nam\s+(?:19|20)\d{2}|\s*/\s*(?:19|20)\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ViMonthYear();

    /// <summary>The bare vi year, "năm 2020": never a hit alone, only together with an incident word.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\bnam\s+(?:19|20)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex YearOnly();

    /// <summary>
    /// The en month date with the day optional on either side of the month ("March 12, 2019", "March, 2019",
    /// "March 2019", "Mar 2019"); the task file's own example hit is the month-and-year form.
    /// </summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\b(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)[,\s]+(?:\d{1,2},?\s+)?(?:19|20)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EnMonthDate();

    /// <summary>The en day-month date, "12 March 2019".</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\b\d{1,2}\s+(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+(?:19|20)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EnDayMonth();

    /// <summary>The en numeric date, "12/3/2019"; the same shape as the vi one without the prefix.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\b\d{1,2}[/-]\d{1,2}[/-](?:19|20)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EnNumericDate();
}
