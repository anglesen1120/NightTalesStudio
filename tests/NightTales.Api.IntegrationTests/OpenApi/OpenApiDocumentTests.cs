using System.Text.Json;
using NightTales.Api.Http;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.OpenApi;

/// <summary>
/// The published OpenAPI document (09 README D14) must describe the error contract of 09.9.5-T1:
/// <c>components.schemas.ProblemDetails</c> with the five D7 members, referenced by every error
/// response, and an <c>info.version</c> of the <c>&lt;major&gt;.&lt;minor&gt;.&lt;build&gt;</c> shape the
/// contract rule compares.
/// </summary>
/// <remarks>
/// <para>
/// The document under test is the artifact the Api build writes
/// (<c>Microsoft.Extensions.ApiDescription.Server</c>, 09.1.5-T1) and the one the CI <c>contract</c>
/// job diffs against <c>main</c>, so the test reads that file instead of booting a host: no database,
/// no HTTP and no start-up configuration are needed. That is why the class does not derive from
/// <see cref="NightTales.TestKit.IntegrationTest"/> and carries no <c>Category=Integration</c> trait —
/// it runs in the unit job, right after the build that produced the document (09 W3 CR-35).
/// </para>
/// <para>
/// A missing document fails the test with the paths it looked at; it is never a silent pass.
/// </para>
/// </remarks>
public sealed class OpenApiDocumentTests
{
    /// <summary>The HTTP verbs an OpenAPI path item may carry; everything else is path metadata.</summary>
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "put", "post", "delete", "options", "head", "patch", "trace",
    };

    /// <summary>The five members 09 README D7 promises on every error body.</summary>
    private static readonly string[] RequiredProblemMembers = ["type", "title", "status", "code", "traceId"];

    private static readonly Lazy<JsonDocument> Document = new(ReadDocument);

    [Fact]
    public void ProblemDetails_component_exists()
    {
        Schemas.TryGetProperty(ProblemDetailsSchemaTransformer.ComponentName, out var schema)
            .ShouldBeTrue(
                $"the document must publish '{ProblemDetailsSchemaTransformer.ComponentName}' as a " +
                "component, so every error response can reference one typed error model (09 README D7)");

        schema.TryGetProperty("properties", out _).ShouldBeTrue("the component must describe its members");
    }

    [Fact]
    public void ProblemDetails_has_required_properties()
    {
        var properties = ProblemDetailsSchema.GetProperty("properties");

        foreach (var member in RequiredProblemMembers)
        {
            properties.TryGetProperty(member, out _).ShouldBeTrue(
                $"'{member}' is part of the D7 error body and must be in the published component");
        }

        // `code` and `traceId` are the two members the framework's ProblemDetails type cannot express,
        // so they are the ones 09.9.5-T1 adds: they must be typed strings and required, not optional
        // extras a generated client would treat as maybe-absent.
        foreach (var member in new[] { "code", "traceId" })
        {
            properties.GetProperty(member).GetProperty("type").GetString().ShouldBe(
                "string", $"'{member}' is a string on the wire");
        }

        Required.ShouldContain("code");
        Required.ShouldContain("traceId");
    }

    [Fact]
    public void Every_catalogued_error_response_references_problem_details()
    {
        var errorResponses = 0;

        foreach (var (path, method, operation) in Operations())
        {
            foreach (var response in operation.GetProperty("responses").EnumerateObject())
            {
                if (!IsErrorStatus(response.Name))
                {
                    continue;
                }

                // A body-less error response (a 401 answered by the framework) documents no content and
                // has nothing to reference; every error body the Api documents must be the problem one.
                if (!response.Value.TryGetProperty("content", out var content))
                {
                    continue;
                }

                foreach (var mediaType in content.EnumerateObject())
                {
                    var where = $"{method.ToUpperInvariant()} {path} answers {response.Name}";

                    mediaType.Name.ShouldBe(
                        "application/problem+json", $"{where} with '{mediaType.Name}'");

                    mediaType.Value.TryGetProperty("schema", out var schema).ShouldBeTrue(
                        $"{where} with '{mediaType.Name}' without a schema");

                    schema.TryGetProperty("$ref", out var reference).ShouldBeTrue(
                        $"{where} with an inline schema, not a reference to the shared error model");

                    reference.GetString().ShouldBe(
                        $"#/components/schemas/{ProblemDetailsSchemaTransformer.ComponentName}",
                        $"{where} with a schema that is not the shared error model");

                    errorResponses++;
                }
            }
        }

        errorResponses.ShouldBeGreaterThan(
            0, "the document must catalogue at least one error body, or this test asserts nothing");
    }

    [Fact]
    public void Info_version_is_major_minor_build()
    {
        var version = Root.GetProperty("info").GetProperty("version").GetString();
        version.ShouldNotBeNullOrWhiteSpace();

        // `<major>.<minor>.<build>` (09.9.5-T1): the rule compares the major of the head against the
        // base, so the shape is part of the gate's contract, not cosmetics.
        var parts = version!.Split('.');
        parts.Length.ShouldBe(3, $"'{version}' must be <major>.<minor>.<build>");
        foreach (var part in parts)
        {
            int.TryParse(part, out _).ShouldBeTrue($"'{version}' must be <major>.<minor>.<build>");
        }
    }

    [Fact]
    public void No_route_is_documented_without_a_response_schema()
    {
        var operations = 0;

        foreach (var (path, method, operation) in Operations())
        {
            operations++;

            operation.TryGetProperty("responses", out var responses).ShouldBeTrue(
                $"{method.ToUpperInvariant()} {path} documents no response");
            responses.EnumerateObject().ShouldNotBeEmpty(
                $"{method.ToUpperInvariant()} {path} documents no response");

            foreach (var response in responses.EnumerateObject())
            {
                // A 204, or a 200 the handler answers without a body, carries no content: there is no
                // schema to publish. Any content the document does publish must be typed.
                if (!response.Value.TryGetProperty("content", out var content))
                {
                    continue;
                }

                foreach (var mediaType in content.EnumerateObject())
                {
                    mediaType.Value.TryGetProperty("schema", out _).ShouldBeTrue(
                        $"{method.ToUpperInvariant()} {path} documents {response.Name} as " +
                        $"'{mediaType.Name}' without a schema");
                }
            }
        }

        operations.ShouldBeGreaterThan(0, "the document must describe at least one operation");
    }

    private static JsonElement Root => Document.Value.RootElement;

    private static JsonElement Schemas =>
        Root.GetProperty("components").GetProperty("schemas");

    private static JsonElement ProblemDetailsSchema =>
        Schemas.GetProperty(ProblemDetailsSchemaTransformer.ComponentName);

    private static string[] Required =>
    [
        .. ProblemDetailsSchema.GetProperty("required").EnumerateArray().Select(member => member.GetString()!),
    ];

    /// <summary>Every documented operation, as (path, method, operation object).</summary>
    private static IEnumerable<(string Path, string Method, JsonElement Operation)> Operations()
    {
        foreach (var path in Root.GetProperty("paths").EnumerateObject())
        {
            foreach (var member in path.Value.EnumerateObject())
            {
                if (HttpMethods.Contains(member.Name) && member.Value.ValueKind == JsonValueKind.Object)
                {
                    yield return (path.Name, member.Name, member.Value);
                }
            }
        }
    }

    /// <summary>Whether a response key is an error: a 4xx/5xx code, or a range such as <c>4XX</c>.</summary>
    private static bool IsErrorStatus(string key)
    {
        if (int.TryParse(key, out var status))
        {
            return status >= 400;
        }

        return key.Length == 3 && (key[0] == '4' || key[0] == '5');
    }

    /// <summary>
    /// The document the Api build wrote: <c>src/NightTales.Api/openapi.json</c> (09 README D14), or the
    /// copy the build placed next to the test assembly.
    /// </summary>
    private static JsonDocument ReadDocument()
    {
        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllBytes(candidate));
            }
        }

        throw new InvalidOperationException(
            "the Api's OpenAPI document was not found; build the solution first (the build writes " +
            "src/NightTales.Api/openapi.json, 09 README D14). Looked at: " +
            string.Join(", ", Candidates()));
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(RepositoryRoot(), "src", "NightTales.Api", "openapi.json");
        yield return Path.Combine(AppContext.BaseDirectory, "openapi.json");
    }

    /// <summary>The repository root: walks up from the test assembly to the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
