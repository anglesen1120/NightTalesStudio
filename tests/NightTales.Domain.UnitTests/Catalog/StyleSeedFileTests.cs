using NightTales.Domain.Catalog;
using NightTales.Domain.Series;
using NightTales.Infrastructure.Persistence.Seeding;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Catalog;

/// <summary>
/// The committed catalog <c>seed/style-templates.v1.json</c> (03.1.1-T2 Acceptance checks, 03 README
/// D2): every row passes <c>StyleTemplateRules</c>, the file carries the seven D2 templates with their
/// topic hints, and it declares the <c>NegativeClause</c> version the code ships. The file is read
/// through the seeder's own parser, so a parse-level regression fails here too.
/// </summary>
public sealed class StyleSeedFileTests
{
    private static readonly string[] ExpectedNames =
    [
        "dark-folklore-ink",
        "foggy-cinematic",
        "vintage-photo-grain",
        "watercolor-night",
        "temple-candlelight",
        "noir-sketch",
        "case-file-illustration",
    ];

    private static readonly string SeedFile = Path.Combine(RepoRoot(), "seed", "style-templates.v1.json");

    [Fact]
    public void AllTemplatesPassRules()
    {
        var rows = SeedCatalogFile.ReadStyleTemplates(File.ReadAllText(SeedFile));

        rows.ShouldNotBeEmpty();
        foreach (var row in rows)
        {
            StyleTemplateRules.Validate(row.Name, row.Template)
                .IsError.ShouldBeFalse($"the '{row.Name}' template must pass the 03 README D1 rules");
        }
    }

    [Fact]
    public void HasSevenTemplates_TwoTrueCrime()
    {
        var rows = SeedCatalogFile.ReadStyleTemplates(File.ReadAllText(SeedFile));

        rows.Count.ShouldBe(7);
        rows.Select(row => row.Name).ShouldBe(ExpectedNames, ignoreOrder: true);
        rows.Count(row => row.TopicHint == Topic.TrueCrime).ShouldBe(2);
        rows.Count(row => row.TopicHint == Topic.Ghost).ShouldBe(3);
        rows.Count(row => row.TopicHint == Topic.Spiritual).ShouldBe(2);
        rows.ShouldAllBe(row => row.Active);
    }

    [Fact]
    public void NegativeClauseVersionMatchesDomain()
    {
        var version = SeedCatalogFile.ReadNegativeClauseVersion(File.ReadAllText(SeedFile));

        version.ShouldBe(NegativeClause.Version);
    }

    /// <summary>The repository root, found by walking up from the test output to <c>NightTales.sln</c>.</summary>
    private static string RepoRoot()
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
