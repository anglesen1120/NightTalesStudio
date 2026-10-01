using System.Text.Json;
using NightTales.Domain.Catalog;
using NightTales.Domain.Series;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Reads and validates the committed <c>seed/</c> catalogs before any write (09.2.5 plan §3.2). Every
/// failure is a <see cref="SeedValidationException"/> naming the offending key, so a seed aborts with a
/// readable code instead of a half-written table.
/// </summary>
/// <remarks>
/// Only the dev <c>catalog.dev.json</c> (feature flags) and the plan 03 style-template catalog still
/// pass through here; the plan 04 voice catalog moved to its own reader and rules with 04.2.2-T2
/// (<see cref="VoiceCatalogFileReader"/> + <see cref="VoiceCatalogRules"/>, 04 README D9), so every
/// voice key in the file is validated exactly once, against the rules the proxy shares. The plan 03 row
/// rules (<see cref="StyleTemplateRules"/> plus the <c>negativeClauseVersion</c> check) arrive with
/// 03.1.1-T2 and are applied by <see cref="StyleTemplateSeeder"/> and <c>ReadNegativeClauseVersion</c>.
/// This parser validates only the structural JSON it can own: the enum values, duplicate keys and
/// non-blank required fields. Every style-template <em>value</em> rule is
/// <see cref="StyleTemplateRules"/>', applied per row by <see cref="StyleTemplateSeeder"/> before any
/// write, so a bad value is reported with the code of the rule that owns it (09.4.1-T1 F4).
/// </remarks>
public static class SeedCatalogFile
{
    /// <summary>Reads the <c>templates</c> array; checks the structural JSON (a name, a unique natural key, the topic enum).</summary>
    /// <param name="json">The style-template document text.</param>
    public static IReadOnlyList<StyleTemplateSeedRow> ReadStyleTemplates(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("templates", out var templates) || templates.ValueKind != JsonValueKind.Array)
        {
            throw new SeedValidationException("seed_style_template_invalid:-:missing_templates");
        }

        var rows = new List<StyleTemplateSeedRow>(templates.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in templates.EnumerateArray())
        {
            var name = ReadRequiredString(element, "name")
                ?? throw new SeedValidationException("seed_style_template_invalid:-:missing_name");

            if (!seen.Add(name))
            {
                throw new SeedValidationException($"seed_style_template_invalid:{name}:duplicate_name");
            }

            // The value rules — exactly one {term}, 60–600 characters, no line break, no quote, no
            // negative clause — are StyleTemplateRules': the seeder runs them per row, in file order,
            // before any write, so a bad value is reported with the code of the rule that owns it
            // instead of a second, divergent opinion here (09.4.1-T1 F4). A row with no `template`
            // member reaches those rules as the empty string and fails the term count, which is the
            // same code a row whose template has no placeholder gets.
            var template = ReadRequiredString(element, "template") ?? string.Empty;

            Topic? topicHint = null;
            var hint = ReadOptionalString(element, "topicHint");
            if (hint is not null)
            {
                if (!TryParseEnum<Topic>(hint, out var topic))
                {
                    throw new SeedValidationException($"seed_style_template_invalid:{name}:invalid_topic_hint");
                }

                topicHint = topic;
            }

            rows.Add(new StyleTemplateSeedRow(
                name,
                template,
                topicHint,
                ReadOptionalString(element, "previewStorageKey"),
                ReadActive(element)));
        }

        return rows;
    }

    /// <summary>
    /// Reads the file's <c>negativeClauseVersion</c> (03 README D2/CR-1). The seeder compares it with
    /// <see cref="NegativeClause.Version"/>: the seed file and the code must ship together, because the
    /// clause text the relay checks and the text the builder appends come from the two of them.
    /// </summary>
    /// <param name="json">The style-template document text.</param>
    /// <returns>The declared version, or <c>null</c> when the property is absent or not a string.</returns>
    public static string? ReadNegativeClauseVersion(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("negativeClauseVersion", out var version)
            && version.ValueKind == JsonValueKind.String
            ? version.GetString()
            : null;
    }

    /// <summary>Reads the <c>featureFlags</c> key array; rejects a blank or duplicate key.</summary>
    /// <param name="json">The catalog document text.</param>
    public static IReadOnlyList<string> ReadFeatureFlags(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("featureFlags", out var flags))
        {
            return [];
        }

        if (flags.ValueKind != JsonValueKind.Array)
        {
            throw new SeedValidationException("seed_feature_flag_invalid:-:invalid_list");
        }

        var keys = new List<string>(flags.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in flags.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            {
                throw new SeedValidationException("seed_feature_flag_invalid:-:invalid_key");
            }

            var key = element.GetString()!;
            if (!seen.Add(key))
            {
                throw new SeedValidationException($"seed_feature_flag_invalid:{key}:duplicate_key");
            }

            keys.Add(key);
        }

        return keys;
    }

    private static string? ReadRequiredString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string? ReadOptionalString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadActive(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("active", out var active))
        {
            return true;
        }

        return active.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => true,
        };
    }

    /// <summary>
    /// Parses an enum stored as snake_case (<c>true_crime</c> → <see cref="Topic.TrueCrime"/>), case
    /// insensitively. A numeric string is refused before the parse, because
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would take <c>'+1'</c>, <c>'-1'</c>
    /// or <c>' 1'</c> as the enum's numeric value; every catalog value is instead an alphabetic name,
    /// so the underscores are dropped and every remaining character must be an ASCII letter. The parsed
    /// value must then still be a defined member.
    /// </summary>
    private static bool TryParseEnum<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var name = value.Replace("_", string.Empty, StringComparison.Ordinal);
        if (name.Length == 0 || !name.All(char.IsAsciiLetter))
        {
            return false;
        }

        return Enum.TryParse(name, ignoreCase: true, out result) && Enum.IsDefined(result);
    }
}

/// <summary>One validated <c>style_templates</c> row.</summary>
/// <param name="Name">The natural key the seeder upserts by.</param>
/// <param name="Template">The template text; <see cref="StyleTemplateRules"/> requires exactly one <c>{term}</c> in it.</param>
/// <param name="TopicHint">The parsed topic, or <c>null</c> for every topic.</param>
/// <param name="PreviewStorageKey">The preview object key, or <c>null</c>.</param>
/// <param name="Active">Whether the template is offered.</param>
public sealed record StyleTemplateSeedRow(
    string Name,
    string Template,
    Topic? TopicHint,
    string? PreviewStorageKey,
    bool Active);
