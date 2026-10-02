namespace NightTales.MockMpt.Tests.TestHost;

/// <summary>
/// Locates the frozen D14 fixtures (<c>worker-node/contract/fixtures</c>) by walking up from the test output
/// directory and fails loudly when they are missing: the mock's fixture replay tests are worthless without them
/// (05 D14 — the fixtures are the executable contract).
/// </summary>
internal static class FixtureFiles
{
    private static readonly string[] FixtureSegments = ["worker-node", "contract", "fixtures"];

    internal static string Root { get; } = Locate();

    internal static string PathOf(string id) => System.IO.Path.Combine(Root, id + ".json");

    internal static string Read(string id) => File.ReadAllText(PathOf(id));

    internal static IReadOnlyList<string> All()
    {
        var names = new List<string>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Root, "*.json"))
        {
            names.Add(System.IO.Path.GetFileNameWithoutExtension(file));
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine([directory.FullName, .. FixtureSegments]);
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"The contract fixtures were not found above '{AppContext.BaseDirectory}'.");
    }
}
