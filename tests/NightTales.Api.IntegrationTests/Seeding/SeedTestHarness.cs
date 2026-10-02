using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Domain.Catalog;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Seeding;

namespace NightTales.Api.IntegrationTests.Seeding;

/// <summary>
/// Builds the 09.2.5-T1 seeder graph over the class database without booting a host, so a seed test
/// drives <see cref="SeedRunner"/> directly and asserts the rows. The paths are absolute into the
/// repository, because the committed catalogs are the contract the tests exercise.
/// </summary>
internal static class SeedTestHarness
{
    /// <summary>The repository root, found by walking up from the test output to <c>NightTales.sln</c>.</summary>
    internal static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>The dev catalog options, with paths that resolve to the committed files.</summary>
    internal static SeedOptions DefaultOptions() => new()
    {
        CatalogFile = Path.Combine(RepoRoot, "seed", "catalog.dev.json"),
        StyleTemplatesFile = Path.Combine(RepoRoot, "seed", "style-templates.v1.json"),
        VoiceCatalogFile = Path.Combine(RepoRoot, "seed", "voices.v1.json"),
    };

    /// <summary>
    /// The entries of the committed <c>seed/voices.v1.json</c>, parsed with the production reader, so a
    /// test asserts "the rows the seeder wrote are the file's entries" instead of a hard-coded revision
    /// size (04 CR-9).
    /// </summary>
    internal static IReadOnlyList<VoiceCatalogEntry> SeedVoices { get; } = ParseSeedVoices();

    /// <summary>A host environment for the seeders; Development unless a test says otherwise.</summary>
    internal static IHostEnvironment Environment(string name = "Development")
        => new TestHostEnvironment { EnvironmentName = name };

    /// <summary>The production seeder set sharing <paramref name="db"/>, like one DI scope.</summary>
    internal static SeedRunner Runner(AppDbContext db, SeedOptions options, IHostEnvironment environment)
    {
        var bound = Options.Create(options);

        ISeeder[] seeders =
        [
            new VoiceSeeder(db, bound, environment, NullLogger<VoiceSeeder>.Instance),
            new StyleTemplateSeeder(db, bound, environment, NullLogger<StyleTemplateSeeder>.Instance),
            new FeatureFlagSeeder(db, bound, environment, NullLogger<FeatureFlagSeeder>.Instance),
        ];

        return new SeedRunner(seeders, db, bound, NullLogger<SeedRunner>.Instance);
    }

    private static IReadOnlyList<VoiceCatalogEntry> ParseSeedVoices()
    {
        var path = Path.Combine(RepoRoot, "seed", "voices.v1.json");
        var loaded = VoiceCatalogFileReader.Load(File.ReadAllBytes(path));
        if (loaded.IsError)
        {
            throw new InvalidOperationException($"seed/voices.v1.json must load: {loaded.FirstError.Code}");
        }

        return loaded.Value.Voices;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (NightTales.sln) above the test output.");
    }
}

/// <summary>A minimal <see cref="IHostEnvironment"/>: only the name is read by the seeders.</summary>
internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;

    public string ApplicationName { get; set; } = "NightTales.Api.IntegrationTests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
