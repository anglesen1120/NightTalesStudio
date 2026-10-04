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

    /// <summary>
    /// The first character in <paramref name="text"/> that story text may not carry, or null: a C0 control character, or a
    /// surrogate that is not part of a valid pair. A line feed, a carriage return and a tab are prose (the editor
    /// canonicalises <c>\r\n</c> and <c>\r</c> to <c>\n</c>); every other character below U+0020 — above all NUL, which
    /// Postgres refuses in <c>text</c> and <c>jsonb</c> — is not, and neither is a lone surrogate (invalid Unicode that
    /// Npgsql would store as U+FFFD or refuse). The scan is over the raw text: NFC never adds or removes either.
    /// </summary>
    /// <param name="text">The raw field text; null and empty carry none.</param>
    /// <returns>The offending character, or null.</returns>
    public static char? FindControlCharacter(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (character < ' ' && character is not ('\n' or '\r' or '\t'))
            {
                return character;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    index++;
                    continue;
                }

                return character;
            }

            if (char.IsLowSurrogate(character))
            {
                return character;
            }
        }

        return null;
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
    public static IReadOnlyList<string> Tokenize(string? text) => TokenizeNormalized(Normalize(text));

    /// <summary>The D4 token count of <paramref name="text"/>, computed without materialising the tokens.</summary>
    /// <param name="text">The text to count; null and empty have no tokens.</param>
    /// <returns>The number of non-empty whitespace-separated runs.</returns>
    public static int CountTokens(string? text) => CountTokensNormalized(Normalize(text));

    /// <summary>
    /// The D4 token rule for already-NFC-normalised text: split on Unicode whitespace and count the
    /// non-empty runs. A hot path that already normalised a text calls this instead of normalising again.
    /// </summary>
    /// <param name="normalised">The text in Unicode normal form C.</param>
    /// <returns>The non-empty whitespace-separated runs.</returns>
    internal static IReadOnlyList<string> TokenizeNormalized(string normalised)
    {
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

    /// <summary>The D4 token count of already-NFC-normalised text, without materialising the tokens.</summary>
    /// <param name="normalised">The text in Unicode normal form C.</param>
    /// <returns>The number of non-empty whitespace-separated runs.</returns>
    internal static int CountTokensNormalized(string normalised)
    {
        var count = 0;
        var inToken = false;
        foreach (var character in normalised)
        {
            if (char.IsWhiteSpace(character))
            {
                inToken = false;
            }
            else if (!inToken)
            {
                inToken = true;
                count++;
            }
        }

        return count;
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

    /// <summary>
    /// The sentences of <paramref name="text"/> under the same rule as <see cref="CountSentences"/>: a maximal run of
    /// <c>.</c> <c>!</c> <c>?</c> <c>…</c> ends a sentence (and stays with it), a trailing run without a terminator is
    /// one more, and a line break also ends a sentence. Blank pieces are dropped; each piece is trimmed.
    /// </summary>
    /// <param name="text">The text to split.</param>
    /// <returns>The sentences in order; empty for null, empty or whitespace-only text.</returns>
    public static IReadOnlyList<string> SplitSentences(string? text)
    {
        var normalised = Normalize(text);
        var sentences = new List<string>();
        var start = 0;
        var index = 0;
        while (index < normalised.Length)
        {
            var character = normalised[index];
            if (character is '\n' or '\r')
            {
                Flush(normalised, start, index, sentences);
                index++;
                start = index;
            }
            else if (IsTerminator(character))
            {
                while (index < normalised.Length && IsTerminator(normalised[index]))
                {
                    index++;
                }

                Flush(normalised, start, index, sentences);
                start = index;
            }
            else
            {
                index++;
            }
        }

        Flush(normalised, start, normalised.Length, sentences);
        return sentences;
    }

    private static void Flush(string text, int start, int end, List<string> sentences)
    {
        var piece = text.AsSpan(start, end - start).Trim();
        if (piece.Length > 0)
        {
            sentences.Add(piece.ToString());
        }
    }

    /// <summary>Whether <paramref name="text"/> carries any markup the D3 validator rejects.</summary>
    /// <param name="text">The text to inspect.</param>
    /// <returns>True when <see cref="FindMarkup"/> finds something.</returns>
    public static bool ContainsMarkup(string? text) => FindMarkup(text) is not null;

    /// <summary>
    /// The first markup occurrence in <paramref name="text"/>, or null. The set is 02 T72's: the
    /// characters <c>* _ # ` &gt; &lt; [ ] ( )</c>, a line starting with <c>- </c> or <c>N. </c>, and a
    /// URL (<c>http://</c>, <c>https://</c>, <c>www.</c>). A line break is LF, CRLF or a lone CR. The
    /// returned fragment is a prefix of what the draft contains (bounded to the D22 evidence cap), so it
    /// can be quoted as the issue's evidence.
    /// </summary>
    /// <param name="text">The text to inspect.</param>
    /// <returns>The offending fragment, or null.</returns>
    public static string? FindMarkup(string? text) => FindMarkupNormalized(Normalize(text));

    /// <summary>The <see cref="FindMarkup"/> scan over already-NFC-normalised text.</summary>
    /// <param name="normalised">The text in Unicode normal form C.</param>
    /// <returns>The offending fragment, or null.</returns>
    internal static string? FindMarkupNormalized(string normalised)
    {
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
            var lineEnd = lineStart;
            while (lineEnd < normalised.Length && normalised[lineEnd] is not ('\n' or '\r'))
            {
                lineEnd++;
            }

            if (ListMarker(normalised[lineStart..lineEnd]) is { } marker)
            {
                return marker;
            }

            if (lineEnd >= normalised.Length)
            {
                break;
            }

            // A CRLF pair is one line break; a lone CR or LF is its own.
            lineStart = normalised[lineEnd] == '\r'
                && lineEnd + 1 < normalised.Length
                && normalised[lineEnd + 1] == '\n'
                    ? lineEnd + 2
                    : lineEnd + 1;
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
            ? ValidationIssue.Snip(trimmed[..(digits + 2)].ToString())
            : null;
    }
}
