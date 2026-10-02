using System.Text.Json;
using ErrorOr;
using NightTales.Domain.Catalog;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Loads and validates the tracked <c>seed/voices.v1.json</c> (04 README D9). It is the C# twin of the
/// proxy's <c>nts_vieneu_proxy.catalog.load_catalog</c>, which is normative: the JSON is walked in the
/// same order — file keys and types, file-level values, then each entry's structure and values before
/// the next entry, then the interleaved duplicate id/slug pass, then the active-per-language rule — so
/// a file with several faults reports the same <c>code</c> and <c>voiceId</c> on both sides (T14).
/// </summary>
public static class VoiceCatalogFileReader
{
    private static readonly string[] FileKeys = ["schema", "revision", "vieneuCommit", "voices"];

    private static readonly string[] EntryKeys =
    [
        "id",
        "slug",
        "displayName",
        "language",
        "gender",
        "tags",
        "active",
        "previewStorageKey",
    ];

    /// <summary>
    /// Parses and validates the UTF-8 document, returning the catalog or the first D9 failure.
    /// </summary>
    /// <param name="utf8Json">
    /// The file's bytes, as read from disk. <see cref="ReadOnlyMemory{T}"/> rather than
    /// <see cref="ReadOnlySpan{T}"/> because <see cref="JsonDocument.Parse(ReadOnlyMemory{byte}, JsonDocumentOptions)"/>
    /// is the strict byte-level entry point (it also rejects trailing content); callers pass the
    /// <c>byte[]</c> of <see cref="File.ReadAllBytes(string)"/> unchanged.
    /// </param>
    public static ErrorOr<VoiceCatalogFile> Load(ReadOnlyMemory<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json);
        }
        catch (JsonException)
        {
            return Invalid(null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasExactKeys(root, FileKeys))
            {
                return Invalid(null);
            }

            if (!root.TryGetProperty("voices", out var voices) || voices.ValueKind != JsonValueKind.Array)
            {
                return Invalid(null);
            }

            // File-level values first (D9 order: schema -> revision -> vieneuCommit), then the entries:
            // a file-level failure never carries an entry id, and an entry's own structure or value fault
            // is reported before the next entry is looked at.
            var header = VoiceCatalogRules.ValidateHeader(
                ReadString(root, "schema"),
                ReadInt64(root, "revision"),
                ReadString(root, "vieneuCommit"));

            if (header.IsError)
            {
                return header.FirstError;
            }

            var entries = new List<VoiceCatalogEntry>(voices.GetArrayLength());
            foreach (var element in voices.EnumerateArray())
            {
                var entry = ReadEntry(element);
                if (entry.IsError)
                {
                    return entry.FirstError;
                }

                var validation = VoiceCatalogRules.ValidateEntry(entry.Value);
                if (validation.IsError)
                {
                    return validation.FirstError;
                }

                entries.Add(entry.Value);
            }

            var duplicates = VoiceCatalogRules.ValidateCrossEntry(entries);
            if (duplicates.IsError)
            {
                return duplicates.FirstError;
            }

            var languages = VoiceCatalogRules.ValidateLanguages(ReadInt64(root, "revision")!.Value, entries);
            if (languages.IsError)
            {
                return languages.FirstError;
            }

            return new VoiceCatalogFile(
                ReadString(root, "schema")!,
                ReadInt64(root, "revision")!.Value,
                ReadString(root, "vieneuCommit")!,
                entries);
        }
    }

    /// <summary>
    /// Reads one entry's structure and declared types. The id is read first: it is the metadata of every
    /// entry-level failure, and it is absent or wrongly typed exactly when it cannot be reported.
    /// </summary>
    private static ErrorOr<VoiceCatalogEntry> ReadEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Invalid(null);
        }

        var id = ReadString(element, "id");
        if (id is null || !HasExactKeys(element, EntryKeys))
        {
            return Invalid(id);
        }

        var slug = ReadString(element, "slug");
        var displayName = ReadString(element, "displayName");
        var language = ReadString(element, "language");
        var gender = ReadString(element, "gender");
        if (slug is null
            || displayName is null
            || language is null
            || gender is null
            || !TryReadTags(element, out var tags)
            || !TryReadBool(element, "active", out var active)
            || !TryReadNullableString(element, "previewStorageKey", out var previewStorageKey))
        {
            return Invalid(id);
        }

        return new VoiceCatalogEntry(id, slug, displayName, language, gender, tags, active, previewStorageKey);
    }

    /// <summary>
    /// Whether the object carries exactly <paramref name="expected"/>: every key is known and every
    /// known key is present. A repeated key is not rejected (the last value wins, as in Python).
    /// </summary>
    private static bool HasExactKeys(JsonElement element, string[] expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal))
            {
                return false;
            }

            seen.Add(property.Name);
        }

        return seen.Count == expected.Length;
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The JSON integer as <see cref="long"/>, or <c>null</c> when it is absent, a float, a string, a
    /// boolean — or beyond <see cref="long"/>, which Python would accept but no catalog needs.
    /// </summary>
    private static long? ReadInt64(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
                ? number
                : null;

    private static bool TryReadBool(JsonElement element, string property, out bool result)
    {
        result = false;
        if (!element.TryGetProperty(property, out var value))
        {
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                result = true;
                return true;
            case JsonValueKind.False:
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadNullableString(JsonElement element, string property, out string? result)
    {
        result = null;
        if (!element.TryGetProperty(property, out var value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = value.GetString();
        return true;
    }

    private static bool TryReadTags(JsonElement element, out IReadOnlyList<string> tags)
    {
        tags = [];
        if (!element.TryGetProperty("tags", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = new List<string>(value.GetArrayLength());
        foreach (var tag in value.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            values.Add(tag.GetString()!);
        }

        tags = values;
        return true;
    }

    private static Error Invalid(string? voiceId) =>
        Error.Validation(
            VoiceCatalogRules.CatalogInvalid,
            "The voice catalog file is not a valid voices.v1 document.",
            new Dictionary<string, object> { [VoiceCatalogRules.VoiceIdMetadataKey] = voiceId! });
}
