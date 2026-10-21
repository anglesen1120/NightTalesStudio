using System.Text.Json;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Series;

namespace NightTales.Application.Features.Catalog;

/// <summary>
/// The snake_case wire spelling of the two catalog enums (09 README D14) and the parse of the two
/// catalog filters, in one place so the validators and the handlers cannot disagree about what
/// <c>vi</c>/<c>true_crime</c> means.
/// </summary>
/// <remarks>
/// The comparison is ordinal and the accepted spelling is the exact snake_case member name — the same
/// rule the episode list's <c>?status=</c> parser follows (09.5.1): a filter is a machine value, so
/// <c>VI</c>, <c>TRUE_CRIME</c> and <c>true-crime</c> are unknown rather than silently accepted, and a
/// typo is a 400 under the field the client must fix (09 README D14).
/// </remarks>
internal static class CatalogWireNames
{
    /// <summary>The snake_case name of a language, for example <c>vi</c>.</summary>
    internal static string Spelling(Language language) => Spell(language.ToString());

    /// <summary>The snake_case name of a voice gender, for example <c>female</c>.</summary>
    internal static string Spelling(VoiceGender gender) => Spell(gender.ToString());

    /// <summary>The snake_case name of a topic hint, or <see langword="null"/> when the row has none.</summary>
    internal static string? Spelling(Topic? topic) => topic is null ? null : Spell(topic.Value.ToString());

    /// <summary>Parses a <c>?language=</c> value; false for <see langword="null"/>, blank or unknown text.</summary>
    internal static bool TryParseLanguage(string? value, out Language language) => TryParse(value, out language);

    /// <summary>Parses a <c>?topic=</c> value; false for <see langword="null"/>, blank or unknown text.</summary>
    internal static bool TryParseTopic(string? value, out Topic topic) => TryParse(value, out topic);

    private static string Spell(string memberName) => JsonNamingPolicy.SnakeCaseLower.ConvertName(memberName);

    private static bool TryParse<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        parsed = default;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(Spell(name), value, StringComparison.Ordinal))
            {
                parsed = Enum.Parse<TEnum>(name);

                return true;
            }
        }

        return false;
    }
}
