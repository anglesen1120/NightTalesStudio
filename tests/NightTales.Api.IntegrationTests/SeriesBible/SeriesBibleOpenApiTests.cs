using System.Text.Json;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// The published OpenAPI document must describe both bible routes (02.6.3-T1 acceptance): the required
/// <c>If-Match</c> header on <c>PUT</c>, the <c>SeriesBible</c> tag, the documented error statuses, and the
/// <c>BibleContentV1</c> component both the request and the read reference — the schema the SPA is generated
/// from, so a rename here breaks the web build rather than a runtime parse.
/// </summary>
/// <remarks>
/// Like <c>OpenApiDocumentTests</c> this reads the document the Api build writes rather than booting a host, so
/// it needs no database and carries no <c>Category=Integration</c> trait.
/// </remarks>
public sealed class SeriesBibleOpenApiTests
{
    private const string Path = "/api/v1/series/{id}/bible";

    private static readonly Lazy<JsonDocument> Document = new(ReadDocument);

    private static JsonElement Root => Document.Value.RootElement;

    [Fact]
    public void Bible_path_documents_the_two_operations_under_a_plain_tag()
    {
        var operations = OperationNames();

        operations.ShouldBe(["get", "put"], "02.6.3-T1 maps exactly the read and the replace; accept/dismiss are T2");

        foreach (var operation in new[] { "get", "put" })
        {
            Operation(operation).GetProperty("tags").EnumerateArray().Select(tag => tag.GetString())
                .ShouldContain("SeriesBible", "a plain tag: ng-openapi-gen cannot group a dotted name");
        }
    }

    [Fact]
    public void Put_documents_the_required_if_match_header_and_the_d9_responses()
    {
        var operation = Operation("put");

        // The header is documented like every other If-Match route (a [FromHeader] string?); the 428 for a
        // missing header is the RequireIfMatch filter's, and is asserted below as a response.
        operation.GetProperty("parameters").EnumerateArray().Count(parameter =>
            parameter.GetProperty("in").GetString() == "header"
            && parameter.GetProperty("name").GetString() == "If-Match").ShouldBe(1);

        var responses = operation.GetProperty("responses");
        foreach (var status in new[] { "200", "400", "401", "404", "412", "428" })
        {
            responses.TryGetProperty(status, out _).ShouldBeTrue($"PUT bible documents {status}");
        }

        var success = ResolveSchema(responses.GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema"));
        var properties = success.GetProperty("properties");

        // The generator publishes an `int` as the JSON Schema 2020-12 array ["integer","string"] with a pattern
        // and a `string` as the plain "string", so a member is asserted by the type it carries, not its spelling.
        Types(properties.GetProperty("version")).ShouldContain("integer");
        Types(properties.GetProperty("etag")).ShouldBe(["string"]);
    }

    [Fact]
    public void Get_documents_the_401_404_and_the_drift_500()
    {
        var responses = Operation("get").GetProperty("responses");

        foreach (var status in new[] { "200", "401", "404", "500" })
        {
            responses.TryGetProperty(status, out _).ShouldBeTrue($"GET bible documents {status}");
        }
    }

    [Fact]
    public void BibleContentV1_is_published_and_referenced_by_both_routes()
    {
        Schemas.TryGetProperty("BibleContentV1", out var schema).ShouldBeTrue(
            "the acceptance names the component schema BibleContentV1 for the content of both routes");

        var members = schema.GetProperty("properties");
        foreach (var member in new[] { "narrator", "recurring_characters", "places", "rules", "episode_summaries" })
        {
            members.TryGetProperty(member, out _).ShouldBeTrue($"BibleContentV1 documents {member}");
        }

        // The write body wraps it under `content`...
        var request = ResolveSchema(Operation("put").GetProperty("requestBody")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        request.GetProperty("properties").GetProperty("content").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/BibleContentV1");

        // ...and so does the read.
        var read = ResolveSchema(Operation("get").GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        read.GetProperty("properties").GetProperty("content").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/BibleContentV1");
    }

    private static IReadOnlyList<string> OperationNames() =>
        [.. PathItem().EnumerateObject().Select(member => member.Name)];

    private static JsonElement Operation(string method) => PathItem().GetProperty(method);

    private static JsonElement PathItem() => Root.GetProperty("paths").GetProperty(Path);

    private static JsonElement Schemas => Root.GetProperty("components").GetProperty("schemas");

    /// <summary>
    /// The referenced component of a schema: a nullable body is published as <c>oneOf [null, $ref]</c> (the shape
    /// the fact-sheet edit's body has too), so the <c>oneOf</c>/<c>anyOf</c> wrapper is unwrapped to its single
    /// reference-holding branch before the <c>$ref</c> is followed.
    /// </summary>
    private static JsonElement ResolveSchema(JsonElement schema)
    {
        foreach (var keyword in new[] { "oneOf", "anyOf" })
        {
            if (schema.TryGetProperty(keyword, out var branches))
            {
                return ResolveSchema(branches.EnumerateArray().First(branch => branch.TryGetProperty("$ref", out _)));
            }
        }

        if (!schema.TryGetProperty("$ref", out var reference))
        {
            return schema;
        }

        var name = reference.GetString()!.Split('/')[^1];

        return Schemas.GetProperty(name);
    }

    /// <summary>The JSON Schema type name(s) of one schema: one name, or the 2020-12 array form.</summary>
    private static List<string> Types(JsonElement schema)
    {
        var type = schema.GetProperty("type");
        if (type.ValueKind == JsonValueKind.Array)
        {
            List<string> names = [];
            foreach (var item in type.EnumerateArray())
            {
                names.Add(item.GetString()!);
            }

            return names;
        }

        return [type.GetString()!];
    }

    /// <summary>
    /// The document the Api build wrote: <c>src/NightTales.Api/openapi.json</c> (09 README D14), or the copy the
    /// build placed next to the test assembly.
    /// </summary>
    private static JsonDocument ReadDocument()
    {
        var candidates = new[]
        {
            System.IO.Path.Combine(RepositoryRoot(), "src", "NightTales.Api", "openapi.json"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "openapi.json"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllBytes(candidate));
            }
        }

        throw new InvalidOperationException(
            "the Api's OpenAPI document was not found; build the solution first (the build writes " +
            "src/NightTales.Api/openapi.json, 09 README D14). Looked at: " + string.Join(", ", candidates));
    }

    /// <summary>The repository root: walks up from the test assembly to the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
