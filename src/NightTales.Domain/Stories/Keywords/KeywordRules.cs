using NightTales.Domain.Stories.Content;

namespace NightTales.Domain.Stories.Keywords;

/// <summary>
/// 03 FR-03-3 as code: the machine-checkable rules a render keyword must pass (02 README D3, P5, 02 T76,
/// T78, T85). Built here and reused by 03.2.2 (CR-5), which adds the <c>fact_sources</c>-title check at
/// render; R4 (subject + place) and R8 stay model-only, with no machine check.
/// </summary>
/// <remarks>
/// The rules run most-specific first, so a street word reports R12 rather than the generic "not English":
/// R1/R3 word count, R5 person-name heuristic, R6 <see cref="GoreTerms"/>, R7 <see cref="BrandTerms"/>,
/// R12 <see cref="StreetWords"/> plus the caller's <see cref="StoryRules.KnownPlaceNames"/>, R13
/// <see cref="MetaTokens"/>, then R2's <c>[A-Za-z' -]</c> alphabet. R12 rejects a digit-plus-street-word
/// address ("12 baker street") or a known place name, not a street word alone ("rain on a village lane").
/// The R6/R7/R12/R13 lists match a token's hyphen-split parts and possessive stem as well as the whole
/// token, so "blood-soaked" and "child's" do not evade them.
/// </remarks>
public static class KeywordRules
{
    /// <summary>R1: fewer than <see cref="MinWords"/> words.</summary>
    public const string TooFewWords = "keyword_too_few_words";

    /// <summary>R3: more than <see cref="MaxWords"/> words.</summary>
    public const string TooManyWords = "keyword_too_many_words";

    /// <summary>R2: a character outside <c>[A-Za-z' -]</c> — digits, commas, periods and vi diacritics all fail.</summary>
    public const string NotEnglish = "keyword_not_english";

    /// <summary>R5: a title/honorific, a mid-phrase capitalised token, or a known person name.</summary>
    public const string PersonName = "keyword_person_name";

    /// <summary>R6: a gore, weapon, self-harm or nudity term.</summary>
    public const string Gore = "keyword_gore";

    /// <summary>R7: an object whose point is its text or face.</summary>
    public const string BrandObject = "keyword_brand_object";

    /// <summary>R12: a real place identifier — a street word or a known place name.</summary>
    public const string RealPlace = "keyword_real_place";

    /// <summary>R13: an instruction or words addressed to the model.</summary>
    public const string MetaInstruction = "keyword_meta_instruction";

    /// <summary>R1: the fewest words a keyword may have.</summary>
    public const int MinWords = 3;

    /// <summary>R3: the most words a keyword may have.</summary>
    public const int MaxWords = 12;

    /// <summary>The R5 title/honorific list, matched as whole tokens, case-insensitively (02 T85).</summary>
    private static readonly IReadOnlySet<string> Honorifics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "sir", "lady", "captain", "detective", "officer", "professor", "saint",
    };

    /// <summary>
    /// R6's permitted minor formation (02 T78, keyword-rules.v1.md): a minor appears only through an
    /// object they left, and the contract's approved example is "child's drawing". A possessive of a
    /// <see cref="GoreTerms.MinorSubjects"/> term is a violation unless the possessed noun is here.
    /// </summary>
    private static readonly HashSet<string> R6PermittedChildObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "drawing",
    };

    /// <summary>Whether <paramref name="keyword"/> passes every machine check.</summary>
    /// <param name="keyword">The keyword as the writer returned it.</param>
    /// <param name="rules">The run's rules; <see cref="StoryRules.KnownPersonNames"/> and
    /// <see cref="StoryRules.KnownPlaceNames"/> feed R5 and R12.</param>
    /// <returns>True when <see cref="Check"/> returns a valid violation.</returns>
    public static bool IsValid(string? keyword, StoryRules rules) => Check(keyword, rules).IsValid;

    /// <summary>Checks one keyword; the first rule that fires wins.</summary>
    /// <param name="keyword">The keyword as the writer returned it; null and empty fail R1.</param>
    /// <param name="rules">The run's rules.</param>
    /// <returns>The violation, or <see cref="KeywordViolation.Valid"/>.</returns>
    public static KeywordViolation Check(string? keyword, StoryRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var text = StoryText.Normalize(keyword).Trim();
        if (text.Length == 0)
        {
            return KeywordViolation.Invalid(TooFewWords, "(empty)");
        }

        var words = StoryText.TokenizeNormalized(text);
        if (words.Count < MinWords)
        {
            return KeywordViolation.Invalid(TooFewWords, text);
        }

        if (words.Count > MaxWords)
        {
            return KeywordViolation.Invalid(TooManyWords, text);
        }

        // One lexical representation for the input and for every list entry (SEC-02.2.1-KEYWORD-BOUNDARY):
        // per word, leading/trailing apostrophes are trimmed, runs of hyphens split the word into parts,
        // and a possessive ("'s" or a trailing "'") is stripped from each part. No substring matching.
        var lexical = LexicalTokens(words);

        if (ContainsPhrase(lexical, Honorifics)
            || ContainsPhrase(lexical, rules.KnownPersonNames)
            || words.Skip(1).Any(word => char.IsUpper(word[0])))
        {
            return KeywordViolation.Invalid(PersonName, text);
        }

        if (ContainsPhrase(lexical, GoreTerms.All))
        {
            return KeywordViolation.Invalid(Gore, text);
        }

        if (ContainsPhrase(lexical, BrandTerms.All))
        {
            return KeywordViolation.Invalid(BrandObject, text);
        }

        // R12 (02 T80): an address/street identifier is a digit plus a street word; a street word alone
        // ("rain on a village lane") is a scene and stays valid. Known place names always fail.
        if ((ContainsPhrase(lexical, StreetWords.All) && HasAsciiDigit(words))
            || ContainsPhrase(lexical, rules.KnownPlaceNames))
        {
            return KeywordViolation.Invalid(RealPlace, text);
        }

        if (ContainsPhrase(lexical, MetaTokens.All))
        {
            return KeywordViolation.Invalid(MetaInstruction, text);
        }

        return text.Any(character => !IsKeywordCharacter(character))
            ? KeywordViolation.Invalid(NotEnglish, text)
            : KeywordViolation.Valid;
    }

    /// <summary>The R2 alphabet: ASCII letters, apostrophe, hyphen and space (digits and vi diacritics fail).</summary>
    private static bool IsKeywordCharacter(char character) =>
        char.IsAsciiLetter(character) || character is '\'' or '-' or ' ';

    /// <summary>R12 (02 T80) rejects an address: a street word together with a digit ("12 baker street").</summary>
    private static bool HasAsciiDigit(IReadOnlyList<string> words)
    {
        foreach (var word in words)
        {
            foreach (var character in word)
            {
                if (char.IsAsciiDigit(character))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The canonical lexical form of a word or a list entry, shared by both sides of every comparison: a
    /// leading/trailing apostrophe is trimmed, runs of one or more hyphens split the word into parts, and
    /// a possessive ("<c>'s</c>" or a bare trailing "<c>'</c>") is stripped from each part
    /// (SEC-02.2.1-KEYWORD-BOUNDARY). No substring matching is used.
    /// </summary>
    /// <param name="value">A whitespace-separated word or list entry.</param>
    /// <returns>The canonical parts.</returns>
    private static List<string> CanonicalParts(string value)
    {
        var parts = new List<string>();
        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var raw in WordParts(word))
            {
                var stem = StripPossessive(raw);
                if (stem.Length > 0)
                {
                    parts.Add(stem);
                }
            }
        }

        return parts;
    }

    /// <summary>One whitespace word split on runs of hyphens, with its leading/trailing apostrophes trimmed.</summary>
    /// <param name="word">The whitespace token.</param>
    /// <returns>The parts still carrying any possessive.</returns>
    private static string[] WordParts(string word)
    {
        var trimmed = word.Trim('\'');
        return trimmed.Length == 0 ? [] : trimmed.Split('-', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Strips a possessive ("<c>'s</c>" or a trailing "<c>'</c>") from one part; unchanged otherwise.</summary>
    /// <param name="part">One hyphen-split part.</param>
    /// <returns>The part without the possessive.</returns>
    private static string StripPossessive(string part)
    {
        if (part.Length >= 2 && part[^2] == '\'' && part[^1] is 's' or 'S')
        {
            return part[..^2];
        }

        return part.Length >= 1 && part[^1] == '\'' ? part[..^1] : part;
    }

    /// <summary>The first canonical part of one word, or the empty string (the R6 permitted-object check).</summary>
    /// <param name="word">The whitespace token.</param>
    /// <returns>The first canonical part.</returns>
    private static string FirstCanonicalPart(string word)
    {
        var parts = CanonicalParts(word);
        return parts.Count > 0 ? parts[0] : string.Empty;
    }

    /// <summary>
    /// The canonical parts of the keyword's whitespace tokens, keeping R6's permission: a possessive of a
    /// minor over an object they left ("child's drawing", keyword-rules.v1.md) is not emitted, while a
    /// bare minor or a possessive over anything else is.
    /// </summary>
    /// <param name="words">The keyword's whitespace tokens.</param>
    /// <returns>The canonical parts the lists match against.</returns>
    private static List<string> LexicalTokens(IReadOnlyList<string> words)
    {
        var tokens = new List<string>(words.Count);
        for (var index = 0; index < words.Count; index++)
        {
            var permitted = index + 1 < words.Count
                && R6PermittedChildObjects.Contains(FirstCanonicalPart(words[index + 1]));

            foreach (var raw in WordParts(words[index]))
            {
                var stem = StripPossessive(raw);
                if (stem.Length == 0)
                {
                    continue;
                }

                if (permitted && stem != raw && GoreTerms.MinorSubjects.Contains(stem))
                {
                    continue;
                }

                tokens.Add(stem);
            }
        }

        return tokens;
    }

    /// <summary>
    /// Whether the canonical parts hold any phrase of <paramref name="phrases"/>; each list entry is
    /// canonicalised with the same rule as the keyword, so a multi-part entry (e.g. <c>self-harm</c>)
    /// matches a consecutive part run — <c>self-harm</c>, <c>self--harm</c> and <c>self harm</c> all do.
    /// </summary>
    /// <param name="parts">The keyword's canonical parts.</param>
    /// <param name="phrases">The list entries.</param>
    /// <returns>True when a consecutive part run equals a phrase.</returns>
    private static bool ContainsPhrase(List<string> parts, IEnumerable<string> phrases)
    {
        foreach (var phrase in phrases)
        {
            var phraseParts = CanonicalParts(phrase);
            if (phraseParts.Count == 0)
            {
                continue;
            }

            for (var start = 0; start + phraseParts.Count <= parts.Count; start++)
            {
                var matched = true;
                for (var offset = 0; offset < phraseParts.Count; offset++)
                {
                    if (!parts[start + offset].Equals(phraseParts[offset], StringComparison.OrdinalIgnoreCase))
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>
/// The R7 lexical list (02 T79, 02 T85): an object whose point is its text or its face is not a scene, so
/// the keyword must describe what is in the frame instead.
/// </summary>
public static class BrandTerms
{
    /// <summary>The rejected terms, matched case-insensitively as whole tokens.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "logo", "brand", "trademark", "sign", "poster", "headline", "label", "caption", "reading", "saying", "written",
    };
}

/// <summary>
/// The R12 street words (02 T80, 02 T85): an address, house or plate number, street, village, school or
/// shop name is a real place identifier — the keyword must describe the kind of place instead.
/// </summary>
public static class StreetWords
{
    /// <summary>The rejected terms, matched case-insensitively as whole tokens.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "street", "road", "lane", "alley", "ward", "district",
    };
}

/// <summary>
/// The R13 meta-token list (02 T81, 02 T85): a keyword is a noun phrase describing what is in the frame,
/// never an instruction or words addressed to a model.
/// </summary>
public static class MetaTokens
{
    /// <summary>The rejected terms, matched case-insensitively as whole tokens.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ignore", "instead", "draw", "render", "generate", "style", "prompt", "caption", "text",
    };
}
