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
/// Pure and deterministic: no clock, no IO and no randomness. Matching is NFC-normalised, diacritic- and
/// case-insensitive over <em>our own</em> draft only — the owner's brief, notes and fact statements never reach it —
/// and every quoted fragment is at most <see cref="ValidationIssue.MaxEvidenceChars"/> characters of our text
/// (D22), with the lexicon phrase or the incident word as the matched phrase (never the review model's wording).
/// </para>
/// <para>
/// Hostile input cannot cost unbounded time: phrases and incident words are found with an ordinal search (no
/// pattern is built from configuration), the date patterns are source-generated with
/// <see cref="RegexOptions.NonBacktracking"/> so they are linear in the segment length, and every scan is a single
/// left-to-right pass. Noncharacters and unpaired surrogates are dropped before the NFC step, because ICU refuses
/// to normalise them on Linux and the detector must report on hostile text, not throw on it.
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

    /// <summary>The leftmost specific date of the segment (day/month/year or month and year), across all patterns.</summary>
    private static bool TryFirstSpecificDate(string text, out int index, out int length)
    {
        index = int.MaxValue;
        length = 0;

        Consider(ViNumericDate().Match(text), ref index, ref length);
        Consider(ViLongDate().Match(text), ref index, ref length);
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
    /// The text as the matcher sees it: NFC-composed, diacritic-folded and lower-cased, with a map back to the raw
    /// segment so evidence is always quoted from the draft and not from this derived form. Folding is one character
    /// to one character, and only the fold map's Vietnamese letters are rewritten; every other character is only
    /// lower-cased, so the map stays exact.
    /// </summary>
    private static PreparedText Prepare(string text)
    {
        if (text.Length == 0)
        {
            return new PreparedText(string.Empty, null);
        }

        if (!HasRefusedCodePoint(text.AsSpan()) && IsComposed(text))
        {
            // The common case: nothing to drop and nothing to compose, so prepared index == raw index.
            return new PreparedText(Fold(text), null);
        }

        var filtered = new char[text.Length];
        var rawIndex = new int[text.Length];
        var count = Filter(text, filtered, rawIndex);
        if (count == 0)
        {
            return new PreparedText(string.Empty, null);
        }

        var kept = new string(filtered, 0, count);
        var source = new int[count];
        Array.Copy(rawIndex, source, count);

        if (IsComposed(kept))
        {
            var fold = new StringBuilder(count);
            for (var index = 0; index < count; index++)
            {
                fold.Append(Fold(kept[index]));
            }

            return new PreparedText(fold.ToString(), source);
        }

        return Compose(kept, source);
    }

    /// <summary>
    /// The slow path: text that is not NFC yet, composed one canonical unit at a time (a starter and the combining
    /// marks that follow it), which is exactly where NFC composition happens. Each prepared character maps to the
    /// raw index the unit started at.
    /// </summary>
    private static PreparedText Compose(string text, int[] rawIndex)
    {
        var folded = new StringBuilder(text.Length);
        var source = new List<int>(text.Length);
        var position = 0;

        while (position < text.Length)
        {
            var unitStart = position;
            var unitRawStart = rawIndex[position];
            Rune.DecodeFromUtf16(text.AsSpan(position), out _, out var consumed);
            position += consumed;

            while (position < text.Length)
            {
                Rune.DecodeFromUtf16(text.AsSpan(position), out var mark, out var markConsumed);
                if (!IsCombiningMark(mark))
                {
                    break;
                }

                position += markConsumed;
            }

            foreach (var character in Normalise(new string(text.AsSpan(unitStart, position - unitStart))))
            {
                folded.Append(Fold(character));
                source.Add(unitRawStart);
            }
        }

        return new PreparedText(folded.ToString(), [.. source]);
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
    /// The text with every unpaired surrogate and every noncharacter removed, plus the raw index of each kept
    /// character. Returns the number of kept UTF-16 units. The filter is what makes the NFC step safe on hostile
    /// input (see <see cref="BibleOpNormalizer.IsNoncharacter"/>, the set the bible and keyword rules share).
    /// </summary>
    private static int Filter(string text, char[] destination, int[] rawIndex)
    {
        var position = 0;
        var count = 0;

        while (position < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(position), out var rune, out var consumed);

            if (status != OperationStatus.Done || BibleOpNormalizer.IsNoncharacter(rune.Value))
            {
                position += status == OperationStatus.Done ? consumed : 1;
                continue;
            }

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

    /// <summary>Whether a refused code point (an unpaired surrogate or a noncharacter) is present anywhere.</summary>
    private static bool HasRefusedCodePoint(ReadOnlySpan<char> text)
    {
        var position = 0;

        while (position < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text[position..], out var rune, out var consumed);
            if (status != OperationStatus.Done || BibleOpNormalizer.IsNoncharacter(rune.Value))
            {
                return true;
            }

            position += consumed;
        }

        return false;
    }

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

    /// <summary>The bare vi year, "năm 2020": never a hit alone, only together with an incident word.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\bnam\s+(?:19|20)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex YearOnly();

    /// <summary>The en month date, "March 12, 2019".</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"\b(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+\d{1,2},?\s+(?:19|20)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
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
