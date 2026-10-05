using System.Buffers;
using System.Globalization;
using System.Text;
using NightTales.Domain.Common;

namespace NightTales.Domain.Stories.Similarity;

/// <summary>Sanitizes untrusted UTF-16 and normalizes lexical text without removing diacritics.</summary>
public static class TextNormalizer
{
    private const int MaxConsecutiveMarks = 30;

    /// <summary>
    /// Controls and malformed surrogates become separators; format characters and variation
    /// selectors are deleted, preserving the surrounding word. At most 30 consecutive marks
    /// survive before NFC, bounding canonical reordering even for hostile input. Private-use
    /// and unassigned scalars remain token content, preserving the existing opaque-token policy.
    /// Invariant casing and punctuation/whitespace collapsing apply to all supported languages.
    /// </summary>
    public static string Normalize(string text, Language lang)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var buffer = ArrayPool<char>.Shared.Rent(text.Length);
        var written = 0;
        try
        {
            var marks = 0;
            var changed = false;
            var offset = 0;
            while (offset < text.Length)
            {
                var status = Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out var consumed);
                if (status != OperationStatus.Done)
                {
                    changed = true;
                    buffer[written++] = ' ';
                    offset++;
                    marks = 0;
                    continue;
                }

                offset += consumed;
                var category = Rune.GetUnicodeCategory(rune);
                if (Classify(rune, category) == CharacterKind.Deleted)
                {
                    changed = true;
                    continue;
                }

                if (IsMark(category))
                {
                    if (marks == MaxConsecutiveMarks)
                    {
                        changed = true;
                        continue;
                    }

                    marks++;
                }
                else
                {
                    marks = 0;
                }

                if (category == UnicodeCategory.Control)
                {
                    changed = true;
                    buffer[written++] = ' ';
                }
                else
                {
                    written += rune.EncodeToUtf16(buffer.AsSpan(written));
                }
            }

            var canonical = changed ? new string(buffer, 0, written) : text;
            if (!canonical.IsNormalized(NormalizationForm.FormC))
            {
                canonical = canonical.Normalize(NormalizationForm.FormC);
            }

            // NFC can expand composition-excluded characters (for example U+0344).
            if (buffer.Length < canonical.Length)
            {
                var larger = ArrayPool<char>.Shared.Rent(canonical.Length);
                buffer.AsSpan(0, written).Clear();
                ArrayPool<char>.Shared.Return(buffer);
                buffer = larger;
                written = 0;
            }

            var length = 0;
            var separator = false;
            foreach (var rune in canonical.EnumerateRunes())
            {
                var kind = Classify(rune, Rune.GetUnicodeCategory(rune));
                if (kind == CharacterKind.Deleted)
                {
                    continue;
                }

                if (kind == CharacterKind.Separator)
                {
                    separator = length > 0;
                    continue;
                }

                if (separator)
                {
                    buffer[length++] = ' ';
                    separator = false;
                }

                length += Rune.ToLowerInvariant(rune).EncodeToUtf16(buffer.AsSpan(length));
                written = Math.Max(written, length);
            }

            return new string(buffer, 0, length);
        }
        finally
        {
            buffer.AsSpan(0, written).Clear();
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    // Token presence needs neither a normalized copy nor NFC: composition and the mark cap
    // cannot remove the first retained content scalar. Malformed UTF-16 is only a separator.
    internal static bool HasTokenContent(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var offset = 0;
        while (offset < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out var consumed) != OperationStatus.Done)
            {
                offset++;
                continue;
            }

            if (Classify(rune, Rune.GetUnicodeCategory(rune)) == CharacterKind.Content)
            {
                return true;
            }

            offset += consumed;
        }

        return false;
    }

    private static CharacterKind Classify(Rune rune, UnicodeCategory category)
    {
        if (category == UnicodeCategory.Format || rune.Value is >= 0xFE00 and <= 0xFE0F
            or >= 0xE0100 and <= 0xE01EF)
        {
            return CharacterKind.Deleted;
        }

        return category == UnicodeCategory.Control || Rune.IsWhiteSpace(rune) || IsPunctuation(category)
            ? CharacterKind.Separator : CharacterKind.Content;
    }

    private static bool IsMark(UnicodeCategory category) => category is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static bool IsPunctuation(UnicodeCategory category) => category is
        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
        or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
        or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
        or UnicodeCategory.OtherPunctuation;

    private enum CharacterKind
    {
        Content,
        Separator,
        Deleted
    }
}
