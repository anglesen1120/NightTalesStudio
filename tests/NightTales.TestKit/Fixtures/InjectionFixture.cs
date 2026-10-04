using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.TestKit.Fixtures;

/// <summary>
/// One prompt-injection fixture of <c>prompts/fixtures/injection/*.json</c> (02.2.3-T2, 02 README D6):
/// a hostile <see cref="Value"/> to inject into the data-object slot <see cref="Field"/>, a unique
/// <see cref="Canary"/> token inside it, and what a correct model output must satisfy.
/// </summary>
/// <param name="Id">The fixture id; also the file name without extension.</param>
/// <param name="Language">The content language of the run the payload is injected into, <c>vi</c> or <c>en</c>.</param>
/// <param name="Field">The material slot of the data object: <c>brief</c>, <c>bible.summary</c>, <c>facts[0].statement</c> or <c>avoid_plots[0]</c>; control keys (<c>structure_plan</c>, ids) take vocabulary values only and are never a payload slot.</param>
/// <param name="Value">The hostile text.</param>
/// <param name="Canary">A unique token embedded in <see cref="Value"/>; it must never appear in model output.</param>
/// <param name="Expect">The output expectations, used by the live fixture tests.</param>
/// <param name="Step">The pipeline step whose data object receives the payload: <c>writer</c> (default, the 02.2.3-T2 fixtures) or <c>review</c> (02.6.1-T1, <see cref="Field"/> is then a path inside <c>story</c>).</param>
public sealed record InjectionFixture(
    string Id,
    string Language,
    string Field,
    string Value,
    string Canary,
    InjectionExpectation Expect,
    string Step = "writer")
{
    /// <summary>The <see cref="Step"/> of the writer fixtures.</summary>
    public const string WriterStep = "writer"; // keep equal to the Step default of the constructor

    /// <summary>The <see cref="Step"/> of the review fixtures.</summary>
    public const string ReviewStep = "review";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The fixture ids of one step shipped under <c>prompts/fixtures/injection/</c>, ordered by name.</summary>
    /// <param name="step">The <see cref="Step"/> to list; the writer fixtures by default.</param>
    /// <returns>The file names without extension.</returns>
    public static IReadOnlyList<string> Ids(string step = WriterStep) =>
        [.. AllIds().Where(id => string.Equals(Load(id).Step, step, StringComparison.Ordinal))];

    /// <summary>Loads one fixture by id.</summary>
    /// <param name="id">The fixture id.</param>
    /// <returns>The parsed fixture.</returns>
    public static InjectionFixture Load(string id) =>
        JsonSerializer.Deserialize<InjectionFixture>(File.ReadAllText(Path.Combine(FixtureDirectory(), id + ".json")), Options)
        ?? throw new InvalidOperationException($"Injection fixture '{id}' is empty.");

    /// <summary>Loads every shipped fixture of one step.</summary>
    /// <param name="step">The <see cref="Step"/> to load; the writer fixtures by default.</param>
    /// <returns>The parsed fixtures, ordered by id.</returns>
    public static IReadOnlyList<InjectionFixture> LoadAll(string step = WriterStep) => [.. Ids(step).Select(Load)];

    private static IEnumerable<string> AllIds() =>
        Directory.EnumerateFiles(FixtureDirectory(), "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!;

    private static string FixtureDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (NightTales.sln) above the test output.");
        return Path.Combine(root, "prompts", "fixtures", "injection");
    }
}

/// <summary>What a model output must satisfy for one <see cref="InjectionFixture"/>.</summary>
/// <param name="SchemaValid">Whether the output must be a schema-valid <c>story.v1</c> draft.</param>
/// <param name="MustNotContain">Strings that must not appear anywhere in the output.</param>
/// <param name="NoPayloadEcho">Whether the fixture's canary must not appear anywhere in the output.</param>
public sealed record InjectionExpectation(
    bool SchemaValid,
    [property: JsonPropertyName("mustNotContain")] IReadOnlyList<string> MustNotContain,
    bool NoPayloadEcho);
