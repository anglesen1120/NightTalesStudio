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
/// <see cref="MetaTokens"/>, then R2's <c>[A-Za-z' -]</c> alphabet.
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

        var words = StoryText.Tokenize(text);
        if (words.Count < MinWords)
        {
            return KeywordViolation.Invalid(TooFewWords, text);
        }

        if (words.Count > MaxWords)
        {
            return KeywordViolation.Invalid(TooManyWords, text);
        }

        if (ContainsPhrase(words, Honorifics)
            || ContainsPhrase(words, rules.KnownPersonNames)
            || words.Skip(1).Any(word => char.IsUpper(word[0])))
        {
            return KeywordViolation.Invalid(PersonName, text);
        }

        if (ContainsPhrase(words, GoreTerms.All))
        {
            return KeywordViolation.Invalid(Gore, text);
        }

        if (ContainsPhrase(words, BrandTerms.All))
        {
            return KeywordViolation.Invalid(BrandObject, text);
        }

        if (ContainsPhrase(words, StreetWords.All) || ContainsPhrase(words, rules.KnownPlaceNames))
        {
            return KeywordViolation.Invalid(RealPlace, text);
        }

        if (ContainsPhrase(words, MetaTokens.All))
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

    /// <summary>
    /// Whether the keyword's tokens hold any phrase of <paramref name="phrases"/>, compared whole-token and
    /// case-insensitively; a multi-word entry matches a consecutive run of tokens.
    /// </summary>
    private static bool ContainsPhrase(IReadOnlyList<string> words, IEnumerable<string> phrases)
    {
        foreach (var phrase in phrases)
        {
            var parts = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            for (var start = 0; start + parts.Length <= words.Count; start++)
            {
                var matched = true;
                for (var offset = 0; offset < parts.Length; offset++)
                {
                    if (!words[start + offset].Equals(parts[offset], StringComparison.OrdinalIgnoreCase))
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
