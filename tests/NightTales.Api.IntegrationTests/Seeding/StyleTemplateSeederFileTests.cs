using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Domain.Series;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Seeding;

/// <summary>
/// The 03.1.1-T2 seed hook against a real migrated database: the committed file seeds the seven active
/// D2 templates, and a bad row or a clause-version skew aborts the seed with a code and writes nothing
/// (03 README CR-1, 09.2.5-T1).
/// </summary>
public sealed class StyleTemplateSeederFileTests(TestDatabase database) : IntegrationTest(database)
{
    private const string InvalidRowFile = """
        {
          "schema": "style-templates.v1",
          "negativeClauseVersion": "v1",
          "templates": [
            {
              "name": "bad-name",
              "topicHint": "ghost",
              "active": true,
              "previewStorageKey": null,
              "template": "{term} too short"
            }
          ]
        }
        """;

    private const string MismatchedVersionFile = """
        {
          "schema": "style-templates.v1",
          "negativeClauseVersion": "v2",
          "templates": [
            {
              "name": "dark-folklore-ink",
              "topicHint": "ghost",
              "active": true,
              "previewStorageKey": null,
              "template": "Dark folklore ink illustration of {term}. Black ink wash on aged rice paper, muted grey tones."
            }
          ]
        }
        """;

    [Fact]
    public async Task Seeder_SeedsSevenActiveTemplates()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);

        await using var db = Database.CreateAppDbContext(clock: new FakeClock());
        var runner = SeedTestHarness.Runner(db, SeedTestHarness.DefaultOptions(), SeedTestHarness.Environment());

        var reports = await runner.RunAsync(ct);

        reports.Single(report => report.Seeder == "style_templates")
            .ShouldBe(new SeedReport("style_templates", 7, 0, 0));

        var rows = await db.StyleTemplates.AsNoTracking().ToListAsync(ct);
        rows.Count.ShouldBe(7);
        rows.ShouldAllBe(row => row.Active);
        rows.Count(row => row.TopicHint == Topic.TrueCrime).ShouldBe(2);
    }

    [Fact]
    public async Task Seeder_InvalidTemplateRow_AbortsWithNameAndCode()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var path = WriteSeedFile(InvalidRowFile);

        try
        {
            await using var db = Database.CreateAppDbContext(clock: new FakeClock());

            var exception = await Should.ThrowAsync<SeedValidationException>(
                () => CreateSeeder(db, path).SeedAsync(ct).AsTask());

            exception.Code.ShouldBe("seed_style_template_invalid:bad-name:style_length");
            exception.Message.ShouldBe(exception.Code);
            (await CountStyleTemplatesAsync(ct)).ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Seeder_NegativeClauseVersionMismatch_Aborts()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var path = WriteSeedFile(MismatchedVersionFile);

        try
        {
            await using var db = Database.CreateAppDbContext(clock: new FakeClock());

            var exception = await Should.ThrowAsync<SeedValidationException>(
                () => CreateSeeder(db, path).SeedAsync(ct).AsTask());

            exception.Code.ShouldBe("seed_negative_clause_mismatch");
            (await CountStyleTemplatesAsync(ct)).ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Seeder_MissingNegativeClauseVersion_Aborts()
    {
        // The other leg of the version check: a file that names no version is a mismatch, not a
        // silently accepted catalog (03.1.1-T2 §Failure handling: file and code ship together).
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        const string noVersion = """
            {
              "schema": "style-templates.v1",
              "templates": [
                {
                  "name": "dark-folklore-ink",
                  "topicHint": "ghost",
                  "active": true,
                  "previewStorageKey": null,
                  "template": "Dark folklore ink illustration of {term}. Black ink wash on aged rice paper, muted grey tones."
                }
              ]
            }
            """;
        var path = WriteSeedFile(noVersion);

        try
        {
            await using var db = Database.CreateAppDbContext(clock: new FakeClock());

            var exception = await Should.ThrowAsync<SeedValidationException>(
                () => CreateSeeder(db, path).SeedAsync(ct).AsTask());

            exception.Code.ShouldBe("seed_negative_clause_mismatch");
            (await CountStyleTemplatesAsync(ct)).ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static StyleTemplateSeeder CreateSeeder(AppDbContext db, string styleTemplatesFile) => new(
        db,
        Options.Create(new SeedOptions
        {
            CatalogFile = SeedTestHarness.DefaultOptions().CatalogFile,
            StyleTemplatesFile = styleTemplatesFile,
        }),
        SeedTestHarness.Environment(),
        NullLogger<StyleTemplateSeeder>.Instance);

    private static string WriteSeedFile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nts-style-seed-{Guid.CreateVersion7():N}.json");
        File.WriteAllText(path, json);

        return path;
    }

    private async Task ResetAsync(CancellationToken ct) =>
        await Database.ExecuteAsync("DELETE FROM style_templates", ct);

    private async Task<int> CountStyleTemplatesAsync(CancellationToken ct)
    {
        var rows = await Database.QueryStringsAsync("SELECT count(*)::text FROM style_templates", ct);

        return int.Parse(rows.Single(), CultureInfo.InvariantCulture);
    }
}
