using System.Text.Json;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// The published OpenAPI document must describe every bible route (02.6.3-T1 and -T2 acceptances): the required
/// <c>If-Match</c> header on <c>PUT</c>, the <c>SeriesBible</c> tag, the documented error statuses, and the
/// two content components — the write shape a request sends and the read shape a response answers — the
/// schemas the SPA is generated from, so a rename here breaks the web build rather than a runtime parse. The two
/// proposal routes of 02.6.3-T2 are asserted to be one <c>POST</c> each with no precondition and their own
/// documented statuses.
/// </summary>
/// <remarks>
/// Like <c>OpenApiDocumentTests</c> this reads the document the Api build writes rather than booting a host, so
/// it needs no database and carries no <c>Category=Integration</c> trait.
/// </remarks>
public sealed class SeriesBibleOpenApiTests
{
    private const string Path = "/api/v1/series/{id}/bible";

    /// <summary>The two proposal routes 02.6.3-T2 adds under the bible path (plan §5).</summary>
    private static readonly string[] ProposalPaths =
    [
        Path + "/proposal/accept",
        Path + "/proposal/dismiss",
    ];

    private static readonly Lazy<JsonDocument> Document = new(ReadDocument);

    private static JsonElement Root => Document.Value.RootElement;

    [Fact]
    public void Bible_path_documents_the_two_operations_under_a_plain_tag()
    {
        var operations = OperationNames();

        operations.ShouldBe(["get", "put"], "the proposal routes hang under their own sub-paths, not on this one");

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
    public void Proposal_routes_are_one_post_each_under_a_plain_tag()
    {
        foreach (var path in ProposalPaths)
        {
            var item = Root.GetProperty("paths").GetProperty(path);

            item.EnumerateObject().Select(member => member.Name).ShouldBe(
                ["post"],
                $"{path} is mapped as one POST (plan §5)");

            item.GetProperty("post").GetProperty("tags").EnumerateArray().Select(tag => tag.GetString())
                .ShouldContain("SeriesBible", "a plain tag: ng-openapi-gen cannot group a dotted name");
        }
    }

    [Fact]
    public void Proposal_routes_document_their_statuses_and_ask_for_no_precondition()
    {
        // The accept produces a content version, so it answers the very body the PUT answers — the frozen
        // { version, etag } a client reads its next If-Match from (plan §4 U4).
        var accept = Root.GetProperty("paths").GetProperty(ProposalPaths[0]).GetProperty("post");
        var acceptResponses = accept.GetProperty("responses");
        foreach (var status in new[] { "200", "401", "404", "409" })
        {
            acceptResponses.TryGetProperty(status, out _).ShouldBeTrue($"the accept documents {status}");
        }

        var success = ResolveSchema(acceptResponses.GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema"));
        var properties = success.GetProperty("properties");
        Types(properties.GetProperty("version")).ShouldContain("integer");
        Types(properties.GetProperty("etag")).ShouldBe(["string"]);

        // The dismiss writes no content version, so it answers 204 with no body at all.
        var dismiss = Root.GetProperty("paths").GetProperty(ProposalPaths[1]).GetProperty("post");
        var dismissResponses = dismiss.GetProperty("responses");
        foreach (var status in new[] { "204", "401", "404", "409" })
        {
            dismissResponses.TryGetProperty(status, out _).ShouldBeTrue($"the dismiss documents {status}");
        }

        dismissResponses.GetProperty("204").TryGetProperty("content", out _).ShouldBeFalse("a 204 carries no body");

        // Neither takes the write precondition: the ops are append-only and a dismissal cannot damage anything
        // (plan §4 U3/U4), so neither route may advertise an If-Match.
        foreach (var path in ProposalPaths)
        {
            var operation = Root.GetProperty("paths").GetProperty(path).GetProperty("post");
            if (operation.TryGetProperty("parameters", out var parameters)
                && parameters.ValueKind == JsonValueKind.Array)
            {
                var ifMatch = parameters.EnumerateArray()
                    .Where(parameter => parameter.GetProperty("name").GetString() == "If-Match")
                    .ToArray();

                ifMatch.ShouldBeEmpty($"{path} must not ask for an If-Match");
            }
        }
    }

    [Fact]
    public void Bible_content_is_published_per_direction()
    {
        // The write shape: the document a client sends, whose summaries carry only what the client owns, so the
        // request schema can never advertise the server-derived members as writable (02.6.3-T1, plan §4 U2).
        Schemas.TryGetProperty("BibleContentV1", out var request).ShouldBeTrue(
            "the acceptance names the component schema BibleContentV1 for the write");

        foreach (var member in new[] { "narrator", "recurring_characters", "places", "rules", "episode_summaries" })
        {
            request.GetProperty("properties").TryGetProperty(member, out _).ShouldBeTrue($"BibleContentV1 documents {member}");
        }

        SummaryMembers(request).ShouldBe(
            ["episode_id", "title", "summary"],
            "a request summary has no structure/approved_at member to advertise");

        // The read shape: the stored document plus the two members the server derives (plan §3.1, §4 U1).
        Schemas.TryGetProperty("BibleContentResponseV1", out var response).ShouldBeTrue(
            "the read answers its own component, so the derived members never reach the write schema");

        SummaryMembers(response).ShouldBe(
            ["episode_id", "title", "summary", "structure", "approved_at"],
            "the read publishes both server-derived members");

        StructureMembers(response).ShouldBe(
            ["sub_genre", "opening", "twist_type", "setting", "narrator_mode", "beats"],
            "the structure block is the snake_case case of plan §3.1");

        // The write body wraps the write shape...
        var body = ResolveSchema(Operation("put").GetProperty("requestBody")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        body.GetProperty("properties").GetProperty("content").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/BibleContentV1");

        // ...and the read answers the read shape.
        var read = ResolveSchema(Operation("get").GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        read.GetProperty("properties").GetProperty("content").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/BibleContentResponseV1");
    }

    /// <summary>The members of the <c>episode_summaries</c> item of a content component, in schema order.</summary>
    private static IReadOnlyList<string> SummaryMembers(JsonElement content) =>
        [.. SummaryItem(content).EnumerateObject().Select(member => member.Name)];

    /// <summary>
    /// The <c>structure</c> block of a read summary: the members of the component its item references.
    /// </summary>
    private static IReadOnlyList<string> StructureMembers(JsonElement content)
    {
        var structure = SummaryItem(content).GetProperty("structure");
        var resolved = ResolveSchema(structure);

        return [.. resolved.GetProperty("properties").EnumerateObject().Select(member => member.Name)];
    }

    /// <summary>The properties of the item schema of a content component's <c>episode_summaries</c> array.</summary>
    private static JsonElement SummaryItem(JsonElement content)
    {
        var items = content.GetProperty("properties").GetProperty("episode_summaries").GetProperty("items");

        return ResolveSchema(items).GetProperty("properties");
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
