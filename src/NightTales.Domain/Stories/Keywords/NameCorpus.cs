using System.Globalization;
using System.Text;
using NightTales.Domain.Series.Bible;

namespace NightTales.Domain.Stories.Keywords;

/// <summary>
/// The real-person name corpus of one episode (03 README D11, F10, G4; 03.2.2-T1): the names its own research
/// carries — the titles of its fetched <c>url</c> sources and the statements of its approved fact sheet — folded
/// to plain ASCII tokens and stored as the consecutive token pairs of every candidate run.
/// <see cref="RenderKeywordLinter"/> flags a keyword whose folded tokens repeat such a pair, so a scene that
/// names a real victim or suspect is stopped before any image is paid for.
/// </summary>
/// <remarks>
/// <para>
/// Pure and deterministic: no I/O, no clock, no randomness. Every step is linear in the length of the input
/// text, so a hostile or oversized title degrades the run's cost linearly and never quadratically, and a null
/// text, a null sequence, a null or empty keyword and a whitespace-only text are all safe (no throw, no match).
/// </para>
/// <para>
/// Folding (<see cref="Fold"/>, D11): NFD, so a precomposed vi letter becomes its base letter plus combining
/// marks. Every <see cref="UnicodeCategory.NonSpacingMark"/> (<c>Mn</c>) char is dropped, <c>đ</c>/<c>Đ</c>
/// (U+0111/U+0110, which NFD does not decompose) map to <c>d</c>/<c>D</c>, and any char that is not a letter or
/// a digit separates tokens — which keeps the original casing available for run detection while the stored pairs
/// are lower-case. NFC and NFD spellings of the same text therefore fold identically, and <c>đ</c> and a <c>d</c>
/// carrying a combining mark both fold to <c>d</c>.
/// </para>
/// <para>
/// Two code points ICU refuses to normalise — an unpaired UTF-16 surrogate unit and every noncharacter
/// (<see cref="BibleOpNormalizer.IsNoncharacter"/>; verified: the backend throws
/// <see cref="ArgumentException"/> on <c>U+FFFE</c>) — are replaced with a space <em>before</em> the NFD step, so
/// folding never throws on a hostile source title (02 D2: the print path reports, it does not fail). A space,
/// not a deletion, for the same reason a zero-width char separates: deleting would merge the parts of a name
/// into one token the corpus could no longer match. The fold is per UTF-16 unit, so a supplementary-plane letter
/// (not used by vi or en) separates tokens rather than forming one — never the reverse.
/// </para>
/// <para>
/// A format char (ZWSP, ZWNJ, ZWJ, BOM, soft hyphen) is not a letter or a digit, so it separates tokens rather
/// than being deleted: a zero-width char between the parts of a name neither merges them into a token the corpus
/// cannot match nor hides them, and one inside a keyword cannot break a pair the corpus stores. A zero-width char
/// inside the keyword is additionally rejected by <see cref="KeywordRules"/> R2 (the R2 alphabet is
/// <c>[A-Za-z' -]</c>), so that keyword is blocked whatever this corpus would say.
/// </para>
/// <para>
/// A run is a maximal stretch of consecutive tokens whose first char is upper case (D11). Leading stop words
/// (<see cref="StopWords"/>) are skipped from the head of a run — the rest must still hold at least
/// <see cref="MinimumRunTokens"/> tokens — so <c>"The Old Mill"</c> yields <c>old mill</c> while
/// <c>"Police Station"</c> yields nothing. <c>an</c> is deliberately not in the list: it is both an English
/// article and a common Vietnamese given name, and folded <c>án</c> ("case") would otherwise drop a real first
/// name. A run-start that is a real surname after a sentence-initial word that is not in the list still yields
/// its run: more false positives over missed names (D11 [DECISION]). A place name (<c>Da Lat</c>) is caught too,
/// which is intentional (G4) — a place plus a real case is identifying, and the hint copy asks for a generic
/// description instead.
/// </para>
/// <para>
/// The Title Case guard skips a whole text whose tokens of three or more letters are at least 60 % capitalised,
/// the shape of an English headline (<c>"The Night The Lights Went Out"</c>) where every word pair would become
/// a "name". The share is compared in integer arithmetic (<c>capitalised × 10 ≥ longTokens × 6</c>), and the guard
/// only fires from <see cref="TitleCaseMinimumTokens"/> such tokens up: with fewer long tokens there is not enough
/// evidence of a headline, which is what keeps a short sentence-case vi title such as <c>"Vụ án Nguyễn Văn An"</c>
/// (two long tokens, both capitalised) in the corpus [DECISION, gap: D11 states the share alone, and the share
/// alone would skip that corpus text and contradict the 03.2.2-T1 acceptance check].
/// </para>
/// </remarks>
public sealed class NameCorpus
{
    /// <summary>The fewest tokens a candidate run may hold (D11: runs of two or more).</summary>
    public const int MinimumRunTokens = 2;

    /// <summary>D11's Title Case share: at or above it, a text is a headline and is skipped.</summary>
    private const int TitleCaseSharePercent = 60;

    /// <summary>The fewest long tokens a text needs before the Title Case guard may fire (see the type remarks).</summary>
    private const int TitleCaseMinimumTokens = 4;

    /// <summary>The shortest token the Title Case guard counts, in folded characters (D11: tokens of ≥ 3 letters).</summary>
    private const int TitleCaseMinimumLetters = 3;

    /// <summary>
    /// The D11 run-start stop list (folded, matched case-insensitively): the English articles, prepositions and
    /// legal/crime words and their Vietnamese counterparts that open a sentence or a headline but are not the
    /// first half of a name. <c>an</c> is not here, on purpose (see the type remarks).
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "this", "that", "in", "on", "at", "of", "police", "court", "case", "vu", "toa", "cong",
    };

    /// <summary>
    /// The consecutive lower-case token pairs of every stored run, each mapped to the token count of the longest
    /// run that carries it (the <c>run:&lt;n&gt;</c> Evidence of the linter's issue). A dictionary rather than a
    /// bare set, so the evidence needs no second lookup; the key's string comparison is the default ordinal one.
    /// </summary>
    private readonly Dictionary<(string, string), int> _bigrams;

    private NameCorpus(Dictionary<(string, string), int> bigrams) => _bigrams = bigrams;

    /// <summary>
    /// The corpus of no text at all: fiction, an episode whose series topic is not true crime, an episode with no
    /// fetched source title and no approved fact, and the state in which <c>Images:Linter:NameCorpusEnabled</c> is
    /// off (D11 [DECISION]). <see cref="Matches(string)"/> is always false on it.
    /// </summary>
    public static NameCorpus Empty { get; } = new(new Dictionary<(string, string), int>());

    /// <summary>Folds the episode's texts and collects the token pairs of every candidate run.</summary>
    /// <param name="texts">The fetched source titles and approved fact statements; a null sequence or a null
    /// element yields no pairs for it.</param>
    /// <returns><see cref="Empty"/> when no pair was collected, the corpus otherwise.</returns>
    public static NameCorpus From(IEnumerable<string> texts)
    {
        if (texts is null)
        {
            return Empty;
        }

        var bigrams = new Dictionary<(string, string), int>();
        foreach (var text in texts)
        {
            AddText(bigrams, text);
        }

        return bigrams.Count == 0 ? Empty : new NameCorpus(bigrams);
    }

    /// <summary>
    /// Folds <paramref name="text"/> to its lower-case token form joined by single spaces — the form the corpus
    /// stores and matches, and the form tests and logs use (<c>"Nguyễn Văn An"</c> → <c>nguyen van an</c>).
    /// </summary>
    /// <param name="text">The raw text; null, empty and whitespace-only yield the empty string.</param>
    /// <returns>The folded, lower-case, single-space-joined tokens.</returns>
    public static string Fold(string? text)
    {
        var tokens = FoldTokensCore(text);
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var folded = new StringBuilder(tokens.Count * 8);
        for (var index = 0; index < tokens.Count; index++)
        {
            if (index > 0)
            {
                folded.Append(' ');
            }

            folded.Append(tokens[index].ToLowerInvariant());
        }

        return folded.ToString();
    }

    /// <summary>
    /// Folds <paramref name="text"/> and returns its tokens with the original casing, which run detection needs
    /// (D11 step 3: keep the casing, lower-case only for storage).
    /// </summary>
    /// <param name="text">The raw text; null, empty and whitespace-only yield no tokens.</param>
    /// <returns>The folded tokens in text order, no empty token.</returns>
    public static IReadOnlyList<string> FoldTokens(string? text) => FoldTokensCore(text);

    /// <summary>
    /// The token list behind <see cref="FoldTokens"/> and <see cref="From"/>: the concrete type keeps the private
    /// callers allocation- and lookup-cheap, while the public member exposes the read-only view.
    /// </summary>
    /// <param name="text">The raw text; null, empty and whitespace-only yield no tokens.</param>
    /// <returns>The folded tokens in text order, no empty token.</returns>
    private static List<string> FoldTokensCore(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var decomposed = ReplaceRefusedCodePoints(text).Normalize(NormalizationForm.FormD);
        var current = new StringBuilder(16);

        foreach (var character in decomposed)
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var folded = character switch
            {
                'đ' => 'd',
                'Đ' => 'D',
                _ => character,
            };

            if (char.IsLetterOrDigit(folded))
            {
                current.Append(folded);
            }
            else
            {
                Flush(tokens, current);
            }
        }

        Flush(tokens, current);
        return tokens;
    }

    /// <summary>Whether the keyword repeats a stored pair — the D11 check.</summary>
    /// <param name="keyword">The keyword as the writer returned it; null, empty and whitespace-only never match.</param>
    /// <returns>True when two consecutive tokens of the folded keyword are a stored pair.</returns>
    public bool Matches(string? keyword) => Matches(keyword, out _);

    /// <summary>
    /// Whether the keyword repeats a stored pair, and the token count of the run behind it for the issue's
    /// Evidence (D10/D11: the issue carries the rule, the address and <c>run:&lt;n tokens&gt;</c>, never the
    /// keyword or the matched name — 02 D19 and 03 §8 keep personal names out of logs and params).
    /// </summary>
    /// <param name="keyword">The keyword as the writer returned it; null, empty and whitespace-only never match.</param>
    /// <param name="matchedRunTokens">The token count of the longest stored run carrying the repeated pair; 0
    /// when there is no match.</param>
    /// <returns>True when two consecutive tokens of the folded keyword are a stored pair.</returns>
    /// <remarks>
    /// The plan §3.1 signature carries the matched run's text instead (<c>out string matchedRun</c>); this
    /// overload carries its token count, because a run's text is a person's name and must not travel with the
    /// issue (03 §8, 02 D19). The D10 error shape is unaffected: it carries <c>segment</c>, <c>index</c> and
    /// <c>rule</c> only.
    /// </remarks>
    public bool Matches(string? keyword, out int matchedRunTokens)
    {
        matchedRunTokens = 0;
        if (_bigrams.Count == 0 || string.IsNullOrWhiteSpace(keyword))
        {
            return false;
        }

        var tokens = FoldTokens(keyword);
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            var pair = (tokens[index].ToLowerInvariant(), tokens[index + 1].ToLowerInvariant());
            if (_bigrams.TryGetValue(pair, out matchedRunTokens))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Collects the pairs of one text, unless the text is a Title Case headline (D11).</summary>
    private static void AddText(Dictionary<(string, string), int> bigrams, string text)
    {
        var tokens = FoldTokensCore(text);
        if (tokens.Count < MinimumRunTokens || IsTitleCased(tokens))
        {
            return;
        }

        var index = 0;
        while (index < tokens.Count)
        {
            if (!StartsUpper(tokens[index]))
            {
                index++;
                continue;
            }

            var end = index;
            while (end < tokens.Count && StartsUpper(tokens[end]))
            {
                end++;
            }

            AddRun(bigrams, tokens, index, end);
            index = end;
        }
    }

    /// <summary>
    /// Adds the pairs of the run <c>tokens[start..end)</c> after skipping its leading stop words; a remainder
    /// shorter than <see cref="MinimumRunTokens"/> contributes nothing (D11).
    /// </summary>
    private static void AddRun(Dictionary<(string, string), int> bigrams, List<string> tokens, int start, int end)
    {
        var head = start;
        while (head < end && StopWords.Contains(tokens[head].ToLowerInvariant()))
        {
            head++;
        }

        var runTokens = end - head;
        if (runTokens < MinimumRunTokens)
        {
            return;
        }

        for (var index = head; index + 1 < end; index++)
        {
            var pair = (tokens[index].ToLowerInvariant(), tokens[index + 1].ToLowerInvariant());

            // The longest run carrying the pair wins, so the Evidence does not depend on the order the texts
            // arrive in.
            if (!bigrams.TryGetValue(pair, out var known) || runTokens > known)
            {
                bigrams[pair] = runTokens;
            }
        }
    }

    /// <summary>Whether D11's Title Case guard skips the text, given its folded tokens (see the type remarks).</summary>
    private static bool IsTitleCased(List<string> tokens)
    {
        var longTokens = 0;
        var capitalised = 0;
        foreach (var token in tokens)
        {
            if (token.Length < TitleCaseMinimumLetters)
            {
                continue;
            }

            longTokens++;
            if (char.IsUpper(token[0]))
            {
                capitalised++;
            }
        }

        // Integer arithmetic for the share, exactly like the planner's: capitalised / longTokens ≥ 60 %.
        return longTokens >= TitleCaseMinimumTokens && capitalised * 100 >= longTokens * TitleCaseSharePercent;
    }

    /// <summary>Whether a folded token opens with an upper-case letter (a digit never does).</summary>
    private static bool StartsUpper(string token) => char.IsUpper(token[0]);

    /// <summary>
    /// <paramref name="text"/> with every unit ICU refuses to normalise replaced by a space (see the type
    /// remarks), so <see cref="string.Normalize(NormalizationForm)"/> can never throw. Returns the input itself
    /// when it carries none, so the common path allocates nothing. A well-formed surrogate pair that is not a
    /// noncharacter is kept and stepped over as one unit, so its low surrogate is never re-tested on its own and
    /// mistaken for an unpaired one; only a genuinely unpaired surrogate unit and a noncharacter are replaced.
    /// </summary>
    /// <param name="text">The raw text, non-empty and not whitespace-only.</param>
    /// <returns>The text, safe to normalise.</returns>
    private static string ReplaceRefusedCodePoints(string text)
    {
        var first = 0;
        int width;
        while (first < text.Length && !IsRefused(text, first, out width))
        {
            first += width;
        }

        if (first == text.Length)
        {
            return text;
        }

        var sanitised = new StringBuilder(text.Length);
        sanitised.Append(text, 0, first);
        for (var index = first; index < text.Length;)
        {
            if (IsRefused(text, index, out width))
            {
                sanitised.Append(' ');
                index += width;
                continue;
            }

            // A well-formed pair is appended and stepped over as one unit: its low surrogate, tested on its own,
            // would look unpaired and be replaced. A plain kept char has width 1.
            sanitised.Append(text, index, width);
            index += width;
        }

        return sanitised.ToString();
    }

    /// <summary>
    /// Whether the unit at <paramref name="index"/> is one ICU refuses to normalise: an unpaired surrogate unit
    /// (width 1) or a noncharacter (<see cref="BibleOpNormalizer.IsNoncharacter"/>), BMP or supplementary
    /// (width 2 for a well-formed pair), which <see cref="ReplaceRefusedCodePoints"/> replaces with a space. A
    /// well-formed pair that is not a noncharacter is not refused, but still reports width 2: the caller MUST
    /// advance by <paramref name="width"/> so the pair's low surrogate is never examined on its own.
    /// </summary>
    /// <param name="text">The text holding the unit.</param>
    /// <param name="index">The unit's index.</param>
    /// <param name="width">The unit's length in UTF-16 code units.</param>
    /// <returns>Whether it must be replaced before normalising.</returns>
    private static bool IsRefused(string text, int index, out int width)
    {
        width = 1;
        var character = text[index];
        if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            width = 2;
            return BibleOpNormalizer.IsNoncharacter(char.ConvertToUtf32(character, text[index + 1]));
        }

        return char.IsSurrogate(character) || BibleOpNormalizer.IsNoncharacter(character);
    }

    /// <summary>Ends the token being built, if any; a separator or a dropped mark leaves the caller with none.</summary>
    private static void Flush(List<string> tokens, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }
}
