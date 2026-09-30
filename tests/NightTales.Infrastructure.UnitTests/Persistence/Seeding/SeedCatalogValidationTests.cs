using NightTales.Domain.Series;
using NightTales.Infrastructure.Persistence.Seeding;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence.Seeding;

/// <summary>
/// The pre-write validation of <see cref="SeedCatalogFile"/> (09.2.5 plan §3.2, task acceptance): a bad
/// catalog aborts the seed with a key-named code, so a <c>nts-seed</c> run names the template it could
/// not accept instead of writing half a table.
/// </summary>
public sealed class SeedCatalogValidationTests
{
    [Fact]
    public void Seed_InvalidCatalog_FailsWithKeyNamed()
    {
        // A template without the {term} placeholder the writer substitutes.
        const string missingTerm = """
            { "templates": [ { "name": "no-term", "template": "A story without the placeholder." } ] }
            """;

        var missingTermFailure = Should.Throw<SeedValidationException>(
            () => SeedCatalogFile.ReadStyleTemplates(missingTerm));
        missingTermFailure.Code.ShouldBe("seed_style_template_invalid:no-term:missing_term");

        // Two templates with the same natural key.
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
}
