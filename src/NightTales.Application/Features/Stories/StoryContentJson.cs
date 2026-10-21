using System.Text.Json;
using System.Text.Json.Nodes;
using NightTales.Application.Abstractions.Research;
using NightTales.Domain.Stories.Content;

namespace NightTales.Application.Features.Stories;

/// <summary>
/// The stored <c>story_versions.content</c> document (02 README D3): the <c>story.v1</c> draft in snake_case plus the
/// <c>schema_version</c> and <c>fact_sources</c> the backend adds. One place reads and writes it, so the edit command, the
/// job and the read paths agree on the shape. Reading is strict: a missing or null member is an error, never a default.
/// </summary>
public static class StoryContentJson
{
    private const string SchemaVersionMember = "schema_version";
    private const string FactSourcesMember = "fact_sources";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>The serializer options of the stored documents: snake_case, strict about missing and null members.</summary>
    public static JsonSerializerOptions SerializerOptions => Options;

    /// <summary>Reads a <c>story.v1</c> draft from a request body or a stored document; extra members are ignored.</summary>
    /// <param name="json">The JSON value.</param>
    /// <param name="content">The draft when the shape is right.</param>
    /// <returns><see langword="true"/> when the value has every required member with the right type.</returns>
    public static bool TryRead(JsonElement json, out StoryContentV1 content)
    {
        content = null!;

        if (json.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            var parsed = json.Deserialize<StoryContentV1>(Options);
            if (parsed is null)
            {
                return false;
            }

            content = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads the draft from a stored <c>content</c> document.</summary>
    /// <param name="storedJson">The stored text.</param>
    /// <param name="content">The draft when the document is a valid <c>story.v1</c>.</param>
    /// <returns><see langword="true"/> when the stored text parsed.</returns>
    public static bool TryReadStored(string storedJson, out StoryContentV1 content)
    {
        content = null!;

        try
        {
            using var document = JsonDocument.Parse(storedJson);
            return TryRead(document.RootElement, out content);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The <c>fact_sources</c> of a stored document, as the raw array; empty when absent or unreadable.</summary>
    /// <param name="storedJson">The stored text.</param>
    /// <returns>A copy of the array, or an empty one.</returns>
    public static JsonArray ReadFactSources(string storedJson)
    {
        try
        {
            if (JsonNode.Parse(storedJson) is JsonObject root
                && root[FactSourcesMember] is JsonArray array)
            {
                return (JsonArray)array.DeepClone();
            }
        }
        catch (JsonException)
        {
            // An unreadable stored document has no sources to carry over.
        }

        return [];
    }

    /// <summary>Builds the stored document: the draft plus <c>schema_version</c> and the backend-built <c>fact_sources</c>.</summary>
    /// <param name="content">The validated draft.</param>
    /// <param name="factSources">The sources of the facts the draft cites, never taken from the client.</param>
    /// <returns>The document text.</returns>
    public static string Write(StoryContentV1 content, IEnumerable<FactSourceRef> factSources)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(factSources);

        var array = new JsonArray();
        foreach (var source in factSources)
        {
            array.Add(new JsonObject
            {
                ["source_id"] = source.SourceId,
                ["title"] = source.Title,
                ["url"] = source.Url,
            });
        }

        return Write(content, array);
    }

    /// <summary>Builds the stored document with an already built <c>fact_sources</c> array.</summary>
    /// <param name="content">The validated draft.</param>
    /// <param name="factSources">The array to store.</param>
    /// <returns>The document text.</returns>
    public static string Write(StoryContentV1 content, JsonArray factSources)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(factSources);

        var root = (JsonObject)JsonSerializer.SerializeToNode(content, Options)!;
        root[SchemaVersionMember] = NightTales.Domain.Stories.StoryVersion.CurrentSchemaVersion;
        root[FactSourcesMember] = factSources;

        return root.ToJsonString();
    }
}
