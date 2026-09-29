using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.Api.IntegrationTests.Http;

/// <summary>
/// The ProblemDetails wire shape a client sees (09 §5.5): the five members every problem carries, the
/// optional <c>errors</c> group and whatever allow-listed metadata was flattened into the body.
/// </summary>
/// <remarks>
/// The test project declares its own DTO on purpose: <c>ProblemDetailsWriter</c>'s body type is
/// internal, and asserting the JSON keys here is what makes these the wire-contract tests.
/// </remarks>
internal sealed record ProblemResponse
{
    /// <summary>Web defaults: camelCase keys, like the writer's own options.</summary>
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string? Type { get; init; }

    public string? Title { get; init; }

    public int? Status { get; init; }

    public string? Code { get; init; }

    public string? TraceId { get; init; }

    public Dictionary<string, List<string>>? Errors { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}
