namespace NightTales.RenderNode.Tests.Fixtures;

/// <summary>
/// Locates the frozen D14 fixtures (<c>worker-node/contract/fixtures</c>) by walking up from the test output
/// directory, and fails loudly when they are missing: a fixture test that silently skips is worse than no test
/// (05 D14 — the fixtures are the executable contract).
/// </summary>
internal static class FixtureFiles
{
    private static readonly string[] FixtureSegments = ["worker-node", "contract", "fixtures"];

    internal static string Root { get; } = Locate();

    /// <summary>Every fixture file name, in ordinal order.</summary>
    internal static IReadOnlyList<string> All() =>
    [
        .. Directory
            .EnumerateFiles(Root, "*.json")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal),
    ];

    internal static string Read(string fileName) => File.ReadAllText(Path.Combine(Root, fileName));

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. FixtureSegments]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"The contract fixtures were not found above '{AppContext.BaseDirectory}'.");
    }
}
