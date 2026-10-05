using System.Globalization;
using System.Text;
using NightTales.Domain.Common;

namespace NightTales.Domain.Stories.Similarity;

/// <summary>Sanitizes untrusted UTF-16 and normalizes lexical text without removing diacritics.</summary>
public static class TextNormalizer
{
    /// <summary>
    /// Controls and malformed surrogates become separators before NFC normalization. Unicode
    /// punctuation becomes a space (so <c>a.b</c> becomes <c>a b</c>), not concatenated words.
    /// Invariant casing and whitespace collapsing apply identically to all supported languages.
    /// </summary>
    public static string Normalize(string text, Language lang)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sanitized = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsControl(c))
            {
                sanitized.Append(' ');
            }
            else if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sanitized.Append(c).Append(text[++i]);
                }
                else
                {
                    sanitized.Append(' ');
                }
            }
            else
            {
                sanitized.Append(char.IsLowSurrogate(c) ? ' ' : c);
            }
        }

        var canonical = sanitized.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var result = new StringBuilder(canonical.Length);
        var separator = false;
        Span<char> characters = stackalloc char[2];
        foreach (var rune in canonical.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || IsPunctuation(Rune.GetUnicodeCategory(rune)))
            {
                separator = result.Length > 0;
                continue;
            }

            if (separator)
            {
                result.Append(' ');
                separator = false;
            }

            var length = rune.EncodeToUtf16(characters);
            result.Append(characters[..length]);
        }

        return result.ToString();
    }

    private static bool IsPunctuation(UnicodeCategory category) => category is
        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
        or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
        or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
        or UnicodeCategory.OtherPunctuation;
}
