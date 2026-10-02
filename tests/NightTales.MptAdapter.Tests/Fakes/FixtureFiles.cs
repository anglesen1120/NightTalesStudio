namespace NightTales.MptAdapter.Tests.Fakes;

/// <summary>
/// Locates the frozen D14 fixtures (<c>worker-node/contract/fixtures</c>) by walking up from the test output and
/// fails loudly when they are missing: the fixtures are the contract (05 D14), and a fixture test that silently
/// skips is worse than no test.
/// </summary>
internal static class FixtureFiles
{
    private static readonly string[] FixtureSegments = ["worker-node", "contract", "fixtures"];

    internal static string Root { get; } = Locate();

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
