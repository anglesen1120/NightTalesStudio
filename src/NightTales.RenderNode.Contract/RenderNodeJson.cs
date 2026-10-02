using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.RenderNode.Contract;

/// <summary>
/// The two serialization modes of 11 README D4 for the frozen <c>render-node.v1</c> contract
/// (05.4.1-T2, <c>worker-node/contract/render-node.v1.md</c>). Every wire member carries an explicit
/// <c>[JsonPropertyName]</c>; no naming policy is applied, so a member added without one breaks the
/// fixture tests instead of silently changing the wire.
/// </summary>
public static class RenderNodeJson
{
    /// <summary>
    /// The reader/writer the node uses for submit bodies and every fixture test:
    /// unknown members are rejected (05.4.1-T2 §10 T69), duplicate properties are rejected
    /// (11 README E18/T1) and timestamps are read and written as ISO-8601 UTC with <c>Z</c>
    /// (05.4.1-T2 §1 transport).
    /// </summary>
    public static JsonSerializerOptions Strict { get; } = CreateStrict();

    /// <summary>
    /// The reader the Worker uses for status and health: unknown members are ignored, so an additive
    /// v1 change never breaks a running Worker (11 README D4, 05 CR-3). Duplicate members stay
    /// readable (last value wins) because this mode must never fail a poll.
    /// </summary>
    public static JsonSerializerOptions Tolerant { get; } = CreateTolerant();

    private static JsonSerializerOptions CreateStrict()
    {
        var options = CreateBase();
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.AllowDuplicateProperties = false;
        return options;
    }

    private static JsonSerializerOptions CreateTolerant()
    {
        var options = CreateBase();
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip;
        return options;
    }

    private static JsonSerializerOptions CreateBase()
    {
        var options = new JsonSerializerOptions
        {
            // The wire names are exact snake_case; a case-insensitive match would accept a body the
            // contract never defined.
            PropertyNameCaseInsensitive = false,
            // nulls are part of the contract (`video_transition_mode`, `finished_at`, `images`, …).
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.Strict,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 32,
        };
        options.Converters.Add(new UtcDateTimeOffsetJsonConverter());
        return options;
    }
}
