using System.Text;

namespace NightTales.Application.Features.Stories.Generation;

/// <summary>
/// What may reach PostgreSQL (02.2.2-T3, Session 5 failure class): a <c>NUL</c> or a lone surrogate in a <c>jsonb</c> or
/// <c>text</c> value is a 500 at save time. Model output is cleaned here before it enters <c>audit_report</c>, and the
/// finished story document is checked here before a row is built from it.
/// </summary>
internal static class TextSafety
{
    private const char Replacement = '\uFFFD';

    /// <summary>The longest model id stored; a real id is far shorter, the cap only bounds a hostile provider reply.</summary>
    private const int MaxModelNameChars = 128;

    /// <summary>
    /// Replaces C0 control characters other than tab, line feed and carriage return with a space, and lone surrogates with
    /// U+FFFD.
    /// </summary>
    /// <param name="value">The text; null becomes the empty string.</param>
    /// <returns>The same instance when nothing needed replacing.</returns>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder? builder = null;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var replacement = character;

            if (character < ' ' && character is not ('\t' or '\n' or '\r'))
            {
                replacement = ' ';
            }
            else if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                builder?.Append(character).Append(value[index + 1]);
                index++;
                continue;
            }
            else if (char.IsSurrogate(character))
            {
                replacement = Replacement;
            }

            if (replacement != character && builder is null)
            {
                builder = new StringBuilder(value.Length).Append(value, 0, index);
            }

            builder?.Append(replacement);
        }

        return builder?.ToString() ?? value;
    }

    /// <summary>
    /// A provider-supplied model id made safe for <c>jsonb</c> and <c>text</c>: cleaned like any model text, trimmed, cut to
    /// <see cref="MaxModelNameChars"/> without splitting a surrogate pair, and replaced by <c>unknown</c> when nothing is left.
    /// </summary>
    /// <param name="model">The model id as the provider returned it.</param>
    /// <returns>A non-empty, bounded, storable name.</returns>
    public static string CleanModelName(string? model)
    {
        var cleaned = Clean(model).Trim();
        if (cleaned.Length > MaxModelNameChars)
        {
            var length = char.IsHighSurrogate(cleaned[MaxModelNameChars - 1]) ? MaxModelNameChars - 1 : MaxModelNameChars;
            cleaned = cleaned[..length];
        }

        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    /// <summary>
    /// Whether a JSON document can be stored: it holds no raw <c>NUL</c>, no <c>\u0000</c> escape (PostgreSQL <c>jsonb</c>
    /// refuses it) and no lone surrogate.
    /// </summary>
    /// <param name="json">The serialized document.</param>
    /// <returns><see langword="true"/> when the document is storable.</returns>
    public static bool IsPersistableJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];

            if (character == '\0')
            {
                return false;
            }

            if (char.IsHighSurrogate(character) && index + 1 < json.Length && char.IsLowSurrogate(json[index + 1]))
            {
                index++;
                continue;
            }

            if (char.IsSurrogate(character))
            {
                return false;
            }

            if (character == '\\' && IsNulEscape(json, index))
            {
                return false;
            }

            if (character == '\\')
            {
                // Skip the escaped character, so an escaped backslash followed by "u0000" is not read as an escape.
                index++;
            }
        }

        return true;
    }

    private static bool IsNulEscape(string json, int slashIndex) =>
        string.CompareOrdinal(json, slashIndex, "\\u0000", 0, 6) == 0;
}
