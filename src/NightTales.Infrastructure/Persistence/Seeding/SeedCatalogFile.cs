using System.Text.Json;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Series;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Reads and validates the committed <c>seed/</c> catalogs before any write (09.2.5 plan §3.2). Every
/// failure is a <see cref="SeedValidationException"/> naming the offending key, so a seed aborts with a
/// readable code instead of a half-written table.
/// </summary>
/// <remarks>
/// The full rules of plan 04 (<c>VoiceCatalogRules</c>) and plan 03 (<c>StyleTemplateRules</c> plus the
/// <c>negativeClauseVersion</c> check) arrive with 04.2.2-T2 and 03.1.1-T2; this task validates only
/// what it can own: the <c>{term}</c> placeholder, the enum values, duplicate keys and non-blank
/// required fields.
/// </remarks>
public static class SeedCatalogFile
{
    /// <summary>The placeholder every style template must keep.</summary>
    public const string TermPlaceholder = "{term}";

    /// <summary>Reads the <c>voices</c> array; validates ids, enums, tags and duplicate ids.</summary>
    /// <param name="json">The catalog document text.</param>
    public static IReadOnlyList<VoiceSeedRow> ReadVoices(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("voices", out var voices) || voices.ValueKind != JsonValueKind.Array)
        {
            throw new SeedValidationException("seed_catalog_invalid:voices:missing");
        }

        var rows = new List<VoiceSeedRow>(voices.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in voices.EnumerateArray())
        {
            var id = ReadRequiredString(element, "id") ?? throw new SeedValidationException("seed_voice_invalid:-:missing_id");
            if (!seen.Add(id))
            {
                throw new SeedValidationException($"seed_voice_invalid:{id}:duplicate_id");
            }

            var displayName = ReadRequiredString(element, "displayName")
                ?? throw new SeedValidationException($"seed_voice_invalid:{id}:missing_display_name");

            if (!TryParseEnum<Language>(ReadOptionalString(element, "language"), out var language))
            {
                throw new SeedValidationException($"seed_voice_invalid:{id}:invalid_language");
            }

            if (!TryParseEnum<VoiceGender>(ReadOptionalString(element, "gender"), out var gender))
            {
                throw new SeedValidationException($"seed_voice_invalid:{id}:invalid_gender");
            }

            rows.Add(new VoiceSeedRow(id, displayName, language, gender, ReadTags(element, id), ReadActive(element)));
        }

        return rows;
    }

    /// <summary>Reads the <c>templates</c> array; validates the placeholder, the topic enum and duplicates.</summary>
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

            var template = ReadRequiredString(element, "template");
            if (template is null || !template.Contains(TermPlaceholder, StringComparison.Ordinal))
            {
                throw new SeedValidationException($"seed_style_template_invalid:{name}:missing_term");
            }

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

    private static List<string> ReadTags(JsonElement element, string id)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("tags", out var tags))
        {
            return [];
        }

        if (tags.ValueKind != JsonValueKind.Array)
        {
            throw new SeedValidationException($"seed_voice_invalid:{id}:invalid_tags");
        }

        var values = new List<string>(tags.GetArrayLength());
        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tag.GetString()))
            {
                throw new SeedValidationException($"seed_voice_invalid:{id}:invalid_tags");
            }

            values.Add(tag.GetString()!);
        }

        return values;
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

/// <summary>One validated <c>voices</c> row of the dev catalog.</summary>
/// <param name="Id">The canonical voice id (the natural key).</param>
/// <param name="DisplayName">The shown name.</param>
/// <param name="Language">The parsed language.</param>
/// <param name="Gender">The parsed gender.</param>
/// <param name="Tags">The voice tags, in file order.</param>
/// <param name="Active">Whether the voice is offered.</param>
public sealed record VoiceSeedRow(
    string Id,
    string DisplayName,
    Language Language,
    VoiceGender Gender,
    IReadOnlyList<string> Tags,
    bool Active);

/// <summary>One validated <c>style_templates</c> row.</summary>
/// <param name="Name">The natural key the seeder upserts by.</param>
/// <param name="Template">The template text; it contains <see cref="SeedCatalogFile.TermPlaceholder"/>.</param>
/// <param name="TopicHint">The parsed topic, or <c>null</c> for every topic.</param>
/// <param name="PreviewStorageKey">The preview object key, or <c>null</c>.</param>
/// <param name="Active">Whether the template is offered.</param>
public sealed record StyleTemplateSeedRow(
    string Name,
    string Template,
    Topic? TopicHint,
    string? PreviewStorageKey,
    bool Active);
