using System.Globalization;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Seeding;

/// <summary>
/// The catalog seeders against a real migrated database (09.2.5 plan §9): a fresh database gets the 4
/// dev voices, the 7 style templates and the 3 disabled flags; a second run changes nothing and deletes
/// nothing; and a reseed never flips a flag.
/// </summary>
public sealed class SeedTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Voices = "voices";
    private const string StyleTemplates = "style_templates";
    private const string FeatureFlags = "feature_flags";

    /// <summary>The three catalog tables, in the order a snapshot compares them.</summary>
    private static readonly string[] CatalogTables = [Voices, StyleTemplates, FeatureFlags];

    [Fact]
    public async Task Seed_FreshDatabase_InsertsCatalogAndFlagsOff()
    {
        await ResetAsync();

        var reports = await RunAsync();

        reports.Single(report => report.Seeder == "voices").ShouldBe(new SeedReport("voices", 4, 0, 0));
        reports.Single(report => report.Seeder == "style_templates").ShouldBe(new SeedReport("style_templates", 7, 0, 0));
        reports.Single(report => report.Seeder == "feature_flags").ShouldBe(new SeedReport("feature_flags", 3, 0, 0));

        (await CountAsync(Voices)).ShouldBe(4);
        (await CountAsync(StyleTemplates)).ShouldBe(7);
        (await CountAsync(FeatureFlags)).ShouldBe(3);
        (await CountAsync(FeatureFlags, "enabled")).ShouldBe(0);
    }

    [Fact]
    public async Task Seed_RunTwice_ProducesIdenticalRows()
    {
        await ResetAsync();

        await RunAsync();
        var before = await SnapshotsAsync();

        var second = await RunAsync();

        second.ShouldAllBe(report => report.Inserted == 0);
        string.Join('|', await SnapshotsAsync()).ShouldBe(string.Join('|', before));
    }

    [Fact]
    public async Task Seed_ExistingFlagEnabled_IsNotOverwritten()
    {
        await ResetAsync();

        await RunAsync();
        await Database.ExecuteAsync("UPDATE feature_flags SET enabled = true WHERE key = 'youtube_publish'", TestContext.Current.CancellationToken);

        await RunAsync();

        var enabled = await Database.QueryStringsAsync("SELECT enabled::text FROM feature_flags WHERE key = 'youtube_publish'", TestContext.Current.CancellationToken);
        enabled.Single().ShouldBe("true");
    }

    [Fact]
    public async Task Seed_RowNotInFile_IsNotDeleted()
    {
        await ResetAsync();

        await RunAsync();
        await Database.ExecuteAsync(
            "INSERT INTO voices (id, display_name, language, gender, tags, active, created_at) "
            + "VALUES ('operator-voice', 'Operator voice', 'vi', 'female', '{}', true, now())",
            TestContext.Current.CancellationToken);

        await RunAsync();

        (await CountAsync(Voices)).ShouldBe(5);
        (await Database.QueryStringsAsync("SELECT id FROM voices WHERE id = 'operator-voice'", TestContext.Current.CancellationToken)).Single()
            .ShouldBe("operator-voice");
    }

    private async Task<SeedReport[]> RunAsync()
    {
        await using var db = Database.CreateAppDbContext(clock: new FakeClock());
        var runner = SeedTestHarness.Runner(db, SeedTestHarness.DefaultOptions(), SeedTestHarness.Environment());

        return await runner.RunAsync(TestContext.Current.CancellationToken);
    }

    private async Task ResetAsync() => await Database.ExecuteAsync(
        $"DELETE FROM {Voices}; DELETE FROM {StyleTemplates}; DELETE FROM {FeatureFlags}");

    private async Task<int> CountAsync(string table, string? where = null)
    {
        var sql = $"SELECT count(*)::text FROM {table}" + (where is null ? string.Empty : $" WHERE {where}");
        var rows = await Database.QueryStringsAsync(sql);

        return int.Parse(rows.Single(), CultureInfo.InvariantCulture);
    }

    /// <summary>One content hash per catalog table, so two runs can be compared without reading every row.</summary>
    private async Task<IReadOnlyList<string>> SnapshotsAsync()
    {
        var snapshots = new List<string>();
        foreach (var table in CatalogTables)
        {
            var rows = await Database.QueryStringsAsync(
                $"SELECT md5(coalesce(string_agg(to_jsonb(t)::text, '|' ORDER BY to_jsonb(t)::text), '')) FROM {table} t");
            snapshots.Add(rows.Single());
        }

        return snapshots;
    }
}
