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
        await ResetAsync(TestContext.Current.CancellationToken);

        var reports = await RunAsync();

        reports.Single(report => report.Seeder == "voices").ShouldBe(new SeedReport("voices", 4, 0, 0));
        reports.Single(report => report.Seeder == "style_templates").ShouldBe(new SeedReport("style_templates", 7, 0, 0));
        reports.Single(report => report.Seeder == "feature_flags").ShouldBe(new SeedReport("feature_flags", 3, 0, 0));

        (await CountAsync(Voices, null, TestContext.Current.CancellationToken)).ShouldBe(4);
        (await CountAsync(StyleTemplates, null, TestContext.Current.CancellationToken)).ShouldBe(7);
        (await CountAsync(FeatureFlags, null, TestContext.Current.CancellationToken)).ShouldBe(3);
        (await CountAsync(FeatureFlags, "enabled", TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Seed_RunTwice_ProducesIdenticalRows()
    {
        await ResetAsync(TestContext.Current.CancellationToken);

        await RunAsync();
        var before = await SnapshotsAsync(TestContext.Current.CancellationToken);

        var second = await RunAsync();

        second.ShouldAllBe(report => report.Inserted == 0);
        string.Join('|', await SnapshotsAsync(TestContext.Current.CancellationToken)).ShouldBe(string.Join('|', before));
    }

    [Fact]
    public async Task Seed_ExistingFlagEnabled_IsNotOverwritten()
    {
        await ResetAsync(TestContext.Current.CancellationToken);

        await RunAsync();
        await Database.ExecuteAsync("UPDATE feature_flags SET enabled = true WHERE key = 'youtube_publish'", TestContext.Current.CancellationToken);

        await RunAsync();

        var enabled = await Database.QueryStringsAsync("SELECT enabled::text FROM feature_flags WHERE key = 'youtube_publish'", TestContext.Current.CancellationToken);
        enabled.Single().ShouldBe("true");
    }

    [Fact]
    public async Task Seed_RowNotInFile_IsNotDeleted()
    {
        await ResetAsync(TestContext.Current.CancellationToken);

        await RunAsync();
        await Database.ExecuteAsync(
            "INSERT INTO voices (id, display_name, language, gender, tags, active, created_at) "
            + "VALUES ('operator-voice', 'Operator voice', 'vi', 'female', '{}', true, now())",
            TestContext.Current.CancellationToken);

        await RunAsync();

        (await CountAsync(Voices, null, TestContext.Current.CancellationToken)).ShouldBe(5);
        (await Database.QueryStringsAsync("SELECT id FROM voices WHERE id = 'operator-voice'", TestContext.Current.CancellationToken)).Single()
            .ShouldBe("operator-voice");
    }

    private async Task<SeedReport[]> RunAsync()
    {
        await using var db = Database.CreateAppDbContext(clock: new FakeClock());
        var runner = SeedTestHarness.Runner(db, SeedTestHarness.DefaultOptions(), SeedTestHarness.Environment());

        return await runner.RunAsync(TestContext.Current.CancellationToken);
    }

    private async Task ResetAsync(CancellationToken ct) => await Database.ExecuteAsync(
        $"DELETE FROM {Voices}; DELETE FROM {StyleTemplates}; DELETE FROM {FeatureFlags}", ct);

    private async Task<int> CountAsync(string table, string? where, CancellationToken ct)
    {
        var sql = $"SELECT count(*)::text FROM {table}" + (where is null ? string.Empty : $" WHERE {where}");
        var rows = await Database.QueryStringsAsync(sql, ct);

        return int.Parse(rows.Single(), CultureInfo.InvariantCulture);
    }

    /// <summary>One content hash per catalog table, so two runs can be compared without reading every row.</summary>
    private async Task<IReadOnlyList<string>> SnapshotsAsync(CancellationToken ct)
    {
        var snapshots = new List<string>();
        foreach (var table in CatalogTables)
        {
            var rows = await Database.QueryStringsAsync(
                $"SELECT md5(coalesce(string_agg(to_jsonb(t)::text, '|' ORDER BY to_jsonb(t)::text), '')) FROM {table} t", ct);
            snapshots.Add(rows.Single());
        }

        return snapshots;
    }
}
