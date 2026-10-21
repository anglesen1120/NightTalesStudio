using System.Text.Json;

namespace NightTales.TestKit.Review;

/// <summary>
/// The frozen seeded-defect manifest of <c>tests/NightTales.TestKit/Fixtures/seeded-defects-expected.json</c>
/// (02.6.1-T2, T27): the hand-computed counts and the per-draft <c>[type, segment]</c> pairs the set froze to. The
/// live harness reads only <see cref="Ids"/> to prove the loaded fixtures are exactly the frozen set before the
/// first paid call; the unit tests compare the whole shape.
/// </summary>
/// <remarks>
/// There is no <c>Content</c>/<c>CopyToOutputDirectory</c> rule in any test project, so the file is located the way
/// every other TestKit fixture is: by walking up from the test output to <c>NightTales.sln</c>.
/// </remarks>
/// <param name="Drafts">The frozen number of drafts (22).</param>
/// <param name="CleanDrafts">The frozen number of clean drafts (5).</param>
/// <param name="SeededDrafts">The frozen number of seeded drafts (17).</param>
/// <param name="SeededDefects">The frozen number of planted defects (26).</param>
/// <param name="DefectsByType">The frozen planted-defect count per type.</param>
/// <param name="PerDraft">One entry per frozen draft, in manifest order.</param>
public sealed record SeededDefectManifest(
    int Drafts,
    int CleanDrafts,
    int SeededDrafts,
    int SeededDefects,
    IReadOnlyDictionary<string, int> DefectsByType,
    IReadOnlyList<SeededDefectManifestDraft> PerDraft)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>The frozen fixture ids, in manifest order.</summary>
    public IReadOnlyList<string> Ids => [.. PerDraft.Select(draft => draft.Id)];

    /// <summary>The absolute path of the frozen manifest, below the repository root.</summary>
    /// <returns>The manifest path.</returns>
    /// <exception cref="InvalidOperationException">The repository root (NightTales.sln) is not above the test output.</exception>
    public static string ManifestPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (NightTales.sln) above the test output.");

        return Path.Combine(root, "tests", "NightTales.TestKit", "Fixtures", "seeded-defects-expected.json");
    }

    /// <summary>Reads the frozen manifest as shipped.</summary>
    /// <returns>The parsed manifest.</returns>
    /// <exception cref="InvalidOperationException">The manifest is empty.</exception>
    public static SeededDefectManifest Load() =>
        JsonSerializer.Deserialize<SeededDefectManifest>(File.ReadAllText(ManifestPath()), Options)
        ?? throw new InvalidOperationException("The frozen seeded-defect manifest is empty.");
}

/// <summary>One frozen draft of the manifest: its id, its clean flag and its <c>[type, segment]</c> pairs.</summary>
/// <param name="Id">The fixture id; also the file name without extension.</param>
/// <param name="Clean">Whether the draft must review with no blocking issue.</param>
/// <param name="Defects">The planted defects as <c>[type, segment]</c> pairs.</param>
public sealed record SeededDefectManifestDraft(string Id, bool Clean, IReadOnlyList<string[]> Defects);
