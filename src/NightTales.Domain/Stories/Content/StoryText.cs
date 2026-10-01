using System.Text;

namespace NightTales.Domain.Stories.Content;

/// <summary>
/// The token, sentence and markup rules the validator and the derived fields share (02 README D4, 02
/// T72). A token is a whitespace-separated run after NFC normalisation — the vi token is a syllable
/// (D4/V1) — and the type is deliberately dependency-free so 02.5.6 (shingles) and 03.2.2 can reuse it
/// without pulling in EF.
/// </summary>
public static class StoryText
{
    /// <summary>The characters 02 T72 rejects anywhere in a segment text, title or description.</summary>
    private const string MarkupChars = "*_#`><[]()";

    /// <summary>The URL prefixes the markup rule rejects; a bare <c>www.</c> counts too.</summary>
    private static readonly string[] UrlPrefixes = ["http://", "https://", "www."];

    /// <summary>
    /// NFC-normalises <paramref name="text"/>; a null or empty value yields the empty string. An unpaired
    /// surrogate (invalid Unicode) is replaced with U+FFFD before normalising, so the call never throws —
    /// the validator's contract is to report hostile input, not to fail on it.
    /// </summary>
    /// <param name="text">The raw text.</param>
    /// <returns>The text in Unicode normal form C.</returns>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return HasUnpairedSurrogate(text.AsSpan())
            ? ReplaceUnpairedSurrogates(text).Normalize(NormalizationForm.FormC)
            : text.Normalize(NormalizationForm.FormC);
    }

    /// <summary>Whether <paramref name="text"/> holds a surrogate that is not part of a valid pair.</summary>
    private static bool HasUnpairedSurrogate(ReadOnlySpan<char> text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character)
                && (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1])))
            {
                return true;
            }

            if (char.IsLowSurrogate(character)
                && (index == 0 || !char.IsHighSurrogate(text[index - 1])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Replaces every unpaired surrogate with U+FFFD so <see cref="Normalize"/> can never throw.</summary>
    private static string ReplaceUnpairedSurrogates(string text) =>
        string.Create(text.Length, text, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                var unpairedHigh = char.IsHighSurrogate(character)
                    && (index + 1 >= source.Length || !char.IsLowSurrogate(source[index + 1]));
                var unpairedLow = char.IsLowSurrogate(character)
                    && (index == 0 || !char.IsHighSurrogate(source[index - 1]));
                destination[index] = unpairedHigh || unpairedLow ? '\uFFFD' : character;
            }
        });

    /// <summary>
    /// The D4 token rule: NFC-normalise, split on Unicode whitespace, count the non-empty runs. Every
    /// returned token is a slice of the normalised text.
    /// </summary>
    /// <param name="text">The text to count; null and empty have no tokens.</param>
    /// <returns>The non-empty whitespace-separated runs.</returns>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        var normalised = Normalize(text);
        if (normalised.Length == 0)
        {
            return [];
        }

        var tokens = new List<string>();
        var start = -1;
        for (var index = 0; index < normalised.Length; index++)
        {
            if (char.IsWhiteSpace(normalised[index]))
            {
                if (start >= 0)
                {
                    tokens.Add(normalised[start..index]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = index;
            }
        }

        if (start >= 0)
        {
            tokens.Add(normalised[start..]);
        }

        return tokens;
    }

    /// <summary>
    /// The sentence count of the hook rule: a maximal run of <c>.</c> <c>!</c> <c>?</c> <c>…</c> ends one
    /// sentence, and a trailing stretch of text with no terminator counts as one more sentence. Null,
    /// empty and whitespace-only text have none.
    /// </summary>
    /// <param name="text">The text to count.</param>
    /// <returns>The number of sentences.</returns>
    public static int CountSentences(string? text)
    {
        var normalised = Normalize(text);
        var count = 0;
        var afterLastTerminator = 0;
        var index = 0;
        while (index < normalised.Length)
        {
            if (!IsTerminator(normalised[index]))
            {
                index++;
                continue;
            }

            count++;
            while (index < normalised.Length && IsTerminator(normalised[index]))
            {
                index++;
            }

            afterLastTerminator = index;
        }

        if (count == 0)
        {
            return normalised.Trim().Length == 0 ? 0 : 1;
        }

        return normalised[afterLastTerminator..].Trim().Length == 0 ? count : count + 1;
    }

    /// <summary>Whether <paramref name="text"/> carries any markup the D3 validator rejects.</summary>
    /// <param name="text">The text to inspect.</param>
    /// <returns>True when <see cref="FindMarkup"/> finds something.</returns>
    public static bool ContainsMarkup(string? text) => FindMarkup(text) is not null;

    /// <summary>
    /// The first markup occurrence in <paramref name="text"/>, or null. The set is 02 T72's: the
    /// characters <c>* _ # ` &gt; &lt; [ ] ( )</c>, a line starting with <c>- </c> or <c>N. </c>, and a
    /// URL (<c>http://</c>, <c>https://</c>, <c>www.</c>). The returned fragment is exactly what the
    /// draft contains, so it can be quoted as the issue's evidence.
    /// </summary>
    /// <param name="text">The text to inspect.</param>
    /// <returns>The offending fragment, or null.</returns>
    public static string? FindMarkup(string? text)
    {
        var normalised = Normalize(text);
        if (normalised.Length == 0)
        {
            return null;
        }

        foreach (var prefix in UrlPrefixes)
        {
            if (normalised.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return prefix;
            }
        }

        foreach (var character in normalised)
        {
            if (MarkupChars.Contains(character))
            {
                return character.ToString();
            }
        }

        var lineStart = 0;
        while (lineStart < normalised.Length)
        {
            var lineEnd = normalised.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = normalised.Length;
            }

            if (ListMarker(normalised[lineStart..lineEnd]) is { } marker)
            {
                return marker;
            }

            lineStart = lineEnd + 1;
        }

        return null;
    }

    /// <summary>The sentence terminators of D4 (the Vietnamese texts use the same set).</summary>
    private static bool IsTerminator(char character) => character is '.' or '!' or '?' or '\u2026';

    /// <summary>The bullet/ordered-list marker of one line, or null (a segment is prose, never a list).</summary>
    private static string? ListMarker(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            return "- ";
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        return digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] == '.' && trimmed[digits + 1] == ' '
            ? trimmed[..(digits + 2)].ToString()
            : null;
    }
}
