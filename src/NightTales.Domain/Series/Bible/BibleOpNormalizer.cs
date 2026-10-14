using System.Text;

namespace NightTales.Domain.Series.Bible;

/// <summary>
/// The text normalisation the bible's uniqueness rules and its truncation rule share (02.6.3 plan §3.2, §4 U4):
/// NFC, case-folded, whitespace collapsed, punctuation dropped. It is deliberately the same shape as the story
/// text comparison, so "Không nói tên" and "không  nói tên" are one rule, while diacritics are <b>kept</b> —
/// they are letters, not punctuation, and dropping them would merge distinct Vietnamese names.
/// </summary>
/// <remarks>
/// Punctuation is dropped rather than kept: "Không nói tên," and "không nói tên" are one rule with a trailing
/// comma, and a second entry that differs only in punctuation is the duplicate the dedupe exists to catch
/// (plan §3.2). Whitespace is kept as a single separator, so "ch1" and "ch 1" stay distinct.
/// </remarks>
public static class BibleOpNormalizer
{
    /// <summary>
    /// Normalises one value for comparison: <see langword="null"/>, blank and whitespace-only values become the
    /// empty string.
    /// </summary>
    /// <param name="value">The value to normalise.</param>
    /// <returns>The comparison key; empty when there is nothing to compare.</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim().Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            // Every other non-letter, non-digit character goes: punctuation, symbols (an emoji, a math sign) and
            // the controls that reach here from stored documents. Two entries that differ only in punctuation are
            // the duplicate the dedupe exists to catch.
            if (!Rune.IsLetterOrDigit(rune))
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune.ToString().ToLowerInvariant());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Truncates one value to at most <paramref name="maxChars"/> characters on a word boundary (02.6.3 plan
    /// §4 U4). A value whose first word alone exceeds the limit is cut hard, so the result is never empty for a
    /// non-empty input, and a hard cut never ends on a lone high surrogate.
    /// </summary>
    /// <param name="value">The value to truncate.</param>
    /// <param name="maxChars">The limit (<c>Bible:Limits:SummaryChars</c>).</param>
    /// <returns>The truncated value.</returns>
    public static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || maxChars <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= maxChars)
        {
            return value;
        }

        var cut = value[..maxChars];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            return cut[..lastSpace].TrimEnd();
        }

        if (char.IsHighSurrogate(cut[^1]))
        {
            cut = cut[..^1];
        }

        return cut.TrimEnd();
    }
}
