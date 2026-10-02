using System.Text.Json;
using System.Text.Json.Nodes;
using NightTales.RenderNode.Contract;

namespace NightTales.MockMpt.Fixtures;

/// <summary>
/// Loads the 24 D14 golden fixtures once at start (11.1.2-T1 Design) and exposes the three the mock uses: the
/// <c>render-status.v1</c> bodies a task can replay, the <c>render-node-health.v1</c> bodies the health source
/// can replay, and the <c>render-check.v1</c> blocks a terminal task reports.
/// </summary>
/// <remarks>
/// A missing or empty directory is a named start-up failure, never a silent empty set; a fixture that does not
/// parse with <see cref="RenderNodeJson.Strict"/> fails the same way, so a hand-edited fixture can never make
/// the mock serve a body the contract would reject. Nothing here writes to the directory (05 D14).
/// </remarks>
public sealed class FixtureSet
{
    /// <summary>The id of the check block a successful render reports (05 D14).</summary>
    public const string OkCheckId = "render-check.v1.ok";

    /// <summary>The id of the check block a render without subtitles reports (05 D14).</summary>
    public const string NoSubtitlesCheckId = "render-check.v1.no-subtitles";

    /// <summary>The id of the health body the in-process health source is built from (05 D14).</summary>
    public const string OkHealthId = "render-node-health.v1.ok";

    private const string StatusPrefix = "render-status.v1.";
    private const string HealthPrefix = "render-node-health.v1.";
    private const string CheckPrefix = "render-check.v1.";

    private readonly Dictionary<string, StatusFixture> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RenderNodeHealthV1> _healths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _checks = new(StringComparer.Ordinal);

    private FixtureSet(string directory)
    {
        Directory = directory;
    }

    /// <summary>The fixtures directory the set was read from.</summary>
    public string Directory { get; }

    /// <summary>Every fixture id that can be replayed as a task, in ordinal order.</summary>
    public IReadOnlyList<string> StatusIds => [.. _statuses.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Every fixture id that can be replayed as a health body, in ordinal order.</summary>
    public IReadOnlyList<string> HealthIds => [.. _healths.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Reads every <c>*.json</c> fixture under <paramref name="directory"/>.</summary>
    public static FixtureSet Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!System.IO.Directory.Exists(directory))
        {
            throw new MockStartupException($"fixtures directory not found: '{directory}' (05 D14)");
        }

        var files = System.IO.Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0)
        {
            throw new MockStartupException($"fixtures directory is empty: '{directory}' (05 D14, 24 files)");
        }

        var set = new FixtureSet(directory);
        foreach (var file in files)
        {
            set.LoadOne(file);
        }

        set.Require(OkCheckId, set._checks.ContainsKey(OkCheckId));
        set.Require(NoSubtitlesCheckId, set._checks.ContainsKey(NoSubtitlesCheckId));
        set.Require(OkHealthId, set._healths.ContainsKey(OkHealthId));
        return set;
    }

    /// <summary>The replayable status fixture <paramref name="id"/> names, or null.</summary>
    public StatusFixture? Status(string? id) =>
        id is not null && _statuses.TryGetValue(id, out var fixture) ? fixture : null;

    /// <summary>The replayable health body <paramref name="id"/> names, or null.</summary>
    public RenderNodeHealthV1? Health(string? id) =>
        id is not null && _healths.TryGetValue(id, out var health) ? health : null;

    /// <summary>A <c>render-check.v1</c> block by id; the loader guaranteed the two the mock uses.</summary>
    public JsonElement Check(string id) =>
        _checks.TryGetValue(id, out var check)
            ? check
            : throw new MockStartupException($"fixture '{id}' is not present in '{Directory}'");

    /// <summary>True when <paramref name="id"/> names a fixture the mock can replay (a task or a health body).</summary>
    public bool CanReplay(string id) => _statuses.ContainsKey(id) || _healths.ContainsKey(id);

    private void Require(string id, bool present)
    {
        if (!present)
        {
            throw new MockStartupException($"fixture '{id}.json' is missing from '{Directory}' (05 D14)");
        }
    }

    private void LoadOne(string file)
    {
        var id = Path.GetFileNameWithoutExtension(file);
        var text = File.ReadAllText(file);

        if (id.StartsWith(StatusPrefix, StringComparison.Ordinal))
        {
            _statuses[id] = new StatusFixture(id, Parse<RenderStatusV1>(text, id));
            return;
        }

        if (id.StartsWith(HealthPrefix, StringComparison.Ordinal))
        {
            _healths[id] = Parse<RenderNodeHealthV1>(text, id);
            return;
        }

        if (id.StartsWith(CheckPrefix, StringComparison.Ordinal))
        {
            // Validate the block as a render-check.v1 before keeping it verbatim; the mock re-serves the exact
            // fixture bytes (with the served file's facts substituted) rather than a re-serialized DTO.
            _ = Parse<RenderCheckV1>(text, id);
            using var document = JsonDocument.Parse(text);
            _checks[id] = document.RootElement.Clone();
            return;
        }

        // render-submit.v1.* and render-quality.v1.* are not the mock's to serve; parse them with Strict so a
        // broken fixture still fails at start, but keep nothing.
        _ = Parse<JsonNode>(text, id);
    }

    private static T Parse<T>(string text, string id)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, RenderNodeJson.Strict)
                ?? throw new MockStartupException($"fixture '{id}' is null");
        }
        catch (JsonException exception)
        {
            throw new MockStartupException($"fixture '{id}' is not a valid {typeof(T).Name}: {exception.Message}", exception);
        }
    }
}
