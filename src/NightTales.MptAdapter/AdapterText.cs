using System.Text;

namespace NightTales.MptAdapter;

/// <summary>
/// The boundary between MPT's free-form text and the adapter's journal (S4/S7, SEC-11.3.5-T2-02). MPT's
/// <c>error</c>, <c>failed_stage</c> and any parser reason are the only untrusted strings that reach a log
/// message; CR, LF, ESC and the other control characters are replaced so a value MPT controls can neither
/// forge a second journal line nor move the operator's cursor, and the length is bounded.
/// </summary>
/// <remarks>
/// The replacement is a visible <c>?</c> rather than a space: a stripped newline must not silently join two
/// words into another plausible message. The character is deliberately not <see cref="char.ReplacementChar"/>,
/// which is unprintable in some terminals. Diagnostic value is kept — the escaped text still carries every
/// printable character MPT sent, so S4 ("the raw error text stays in the journal") still holds.
/// </remarks>
public static class AdapterText
{
    /// <summary>The longest MPT-supplied string the adapter puts in one log message.</summary>
    public const int MaxSanitizedLength = 256;

    /// <summary>What each control character becomes.</summary>
    public const char ControlReplacement = '?';

    /// <summary>
    /// Returns <paramref name="value"/> with every control character (C0, DEL and C1 — CR, LF, TAB and ESC
    /// included) replaced, truncated to <paramref name="maxLength"/> characters. Null and empty answer
    /// <see cref="string.Empty"/>.
    /// </summary>
    public static string Sanitize(string? value, int maxLength = MaxSanitizedLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var max = Math.Max(0, maxLength);
        var builder = new StringBuilder(Math.Min(value.Length, max));

        // By scalar, not by UTF-16 unit: a supplementary-plane format character (U+E0001, U+1D173) is two
        // surrogate chars that no per-char category test can see (SEC-2). The bound is still in UTF-16 units
        // and never splits a pair.
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > max)
            {
                break;
            }

            if (NeedsReplacing(rune))
            {
                builder.Append(ControlReplacement);
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    /// <summary>Control characters plus the Unicode line/paragraph separators and format characters (SEC-2).</summary>
    private static bool NeedsReplacing(Rune value) =>
        Rune.IsControl(value)
        || Rune.GetUnicodeCategory(value) is System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator
            or System.Globalization.UnicodeCategory.Format;
}
