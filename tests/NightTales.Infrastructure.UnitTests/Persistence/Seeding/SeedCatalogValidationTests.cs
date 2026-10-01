using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Domain.Catalog;
using NightTales.Domain.Series;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence.Seeding;

/// <summary>
/// The pre-write validation of the style-template catalog (09.2.5 plan §3.2, 03.1.1-T2, 09.4.1-T1 F4):
/// <see cref="SeedCatalogFile"/> owns the JSON shape, <see cref="StyleTemplateRules"/> owns every value
/// rule, and the seeder applies those rules per row before any write — so a bad row aborts the seed with
/// the code of the rule that failed, not with a second opinion from the parser.
/// </summary>
public sealed class SeedCatalogValidationTests
{
    /// <summary>A connection string that is never dialled: the failure these tests assert happens before any query.</summary>
    private const string NeverConnected =
        "Host=127.0.0.1;Port=5432;Database=nts_seed_never_connected;Username=nts;Password=nts";

    [Fact]
    public void Seed_InvalidCatalog_FailsWithKeyNamed()
    {
        // A template without the {term} placeholder the writer substitutes. The reader keeps the row —
        // the placeholder is not a JSON-shape rule — and the first rule to reject it is the term count,
        // which is what the seeder reports (F4).
        const string missingTerm = """
            { "templates": [ { "name": "no-term", "template": "A story without the placeholder." } ] }
            """;

        var kept = SeedCatalogFile.ReadStyleTemplates(missingTerm).Single();
        kept.Name.ShouldBe("no-term");
        kept.Template.ShouldBe("A story without the placeholder.");
        StyleTemplateRules.Validate(kept.Name, kept.Template).FirstError.Code.ShouldBe(StyleTemplateRules.TermCount);

        // Two templates with the same natural key: still the reader's own structural rule.
        const string duplicateName = """
            { "templates": [
                { "name": "dup", "template": "A {term} in ink." },
                { "name": "dup", "template": "Another {term} in paint." } ] }
            """;

        var duplicateFailure = Should.Throw<SeedValidationException>(
            () => SeedCatalogFile.ReadStyleTemplates(duplicateName));
        duplicateFailure.Code.ShouldBe("seed_style_template_invalid:dup:duplicate_name");

        // A valid template parses, and its topic hint is the snake_case enum value.
        const string valid = """
            { "templates": [ { "name": "ok", "template": "A {term} at night.", "topicHint": "true_crime" } ] }
            """;

        var row = SeedCatalogFile.ReadStyleTemplates(valid).Single();
        row.Name.ShouldBe("ok");
        row.TopicHint.ShouldBe(Topic.TrueCrime);
    }

    [Fact]
    public async Task Seeder_NoTermRow_ReportsTheRuleCode()
    {
        // The end-to-end code of F4: the parser no longer throws for a missing placeholder, so the seed
        // fails in the seeder's rule pass and names both the row and the rule that rejected it.
        var path = Path.Combine(Path.GetTempPath(), $"nts-seed-{Guid.CreateVersion7():N}.json");
        await File.WriteAllTextAsync(
            path,
            $$"""
            { "negativeClauseVersion": "{{NegativeClause.Version}}",
              "templates": [ { "name": "no-term", "template": "A story without the placeholder." } ] }
            """,
            TestContext.Current.CancellationToken);

        try
        {
            var seeder = new StyleTemplateSeeder(
                TestDbContextFactory.CreateAppContext(NeverConnected, null, null),
                Options.Create(new SeedOptions { StyleTemplatesFile = path }),
                StubEnvironment(),
                NullLogger<StyleTemplateSeeder>.Instance);

            var failure = await Should.ThrowAsync<SeedValidationException>(
                async () => await seeder.SeedAsync(TestContext.Current.CancellationToken));

            failure.Code.ShouldBe("seed_style_template_invalid:no-term:style_term_count");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The host environment the seeder reads its file through; Development, like a dev run.</summary>
    private static StubHostEnvironment StubEnvironment() => new();

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "NightTales.Infrastructure.UnitTests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
