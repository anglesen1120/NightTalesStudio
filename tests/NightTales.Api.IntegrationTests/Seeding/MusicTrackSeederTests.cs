using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Seeding;

/// <summary>
/// The 05.3.2-T3 music-track seeder against a real migrated database (05 README D8): it upserts the catalog
/// by <c>track_id</c>, a second run changes nothing, a track the file no longer lists is left alone (never
/// destructive), an absent catalog leaves the table untouched without failing, and a catalog that breaks a
/// D8 rule writes nothing. The shared fixtures of <c>seed/fixtures/music</c> are the input.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MusicTrackSeederTests(TestDatabase database) : IntegrationTest(database)
{
    private static string FixtureFile(string name) =>
        Path.Combine(SeedTestHarness.RepoRoot, "seed", "fixtures", "music", name);

    [Fact]
    public async Task Seed_InsertsTheCatalog_ThenASecondRunIsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        await using var db = Database.CreateAppDbContext(clock: new FakeClock());
        var seeder = CreateSeeder(db, FixtureFile("valid-catalog.json"));

        var first = await seeder.SeedAsync(ct);
        var second = await seeder.SeedAsync(ct);

        first.ShouldBe(new SeedReport("music_tracks", 3, 0, 0));
        second.ShouldBe(new SeedReport("music_tracks", 0, 0, 3));
        (await CountAsync(ct)).ShouldBe(3);

        await using var read = Database.CreateAppDbContext(clock: new FakeClock());
        var row = await read.MusicTracks.AsNoTracking().SingleAsync(track => track.TrackId == "fixture-noir-suspense", ct);
        row.Moods.ShouldBe(["noir", "suspense"]);
        row.Active.ShouldBeTrue();
        row.StorageKey.ShouldBe("music/fixture-noir-suspense.mp3");
        row.CatalogRevision.ShouldBe(1);
        row.Attribution.ShouldNotBeNull();
    }

    [Fact]
    public async Task Seed_UpdatesAChangedTrackInPlace_AndNeverDeletesAnUnlistedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        await using var db = Database.CreateAppDbContext(clock: new FakeClock());
        await CreateSeeder(db, FixtureFile("valid-catalog.json")).SeedAsync(ct);
        var path = WriteCatalog(
            File.ReadAllText(FixtureFile("valid-catalog.json"))
                .Replace("\"title\": \"Fixture fixture-temple-calm\"", "\"title\": \"Renamed\"", StringComparison.Ordinal)
                .Replace("\"revision\": 1", "\"revision\": 2", StringComparison.Ordinal));
        try
        {
            await using var second = Database.CreateAppDbContext(clock: new FakeClock());
            var report = await CreateSeeder(second, path).SeedAsync(ct);

            report.Inserted.ShouldBe(0);
            report.Updated.ShouldBe(3, "the catalog revision moved, so every row is rewritten");
        }
        finally
        {
            File.Delete(path);
        }

        // A file that lists fewer tracks leaves the others untouched.
        var trimmed = WriteCatalog(TrimToFirstTrack(File.ReadAllText(FixtureFile("valid-catalog.json"))));
        try
        {
            await using var third = Database.CreateAppDbContext(clock: new FakeClock());
            await CreateSeeder(third, trimmed).SeedAsync(ct);
        }
        finally
        {
            File.Delete(trimmed);
        }

        (await CountAsync(ct)).ShouldBe(3, "the seeder never deletes");
        (await Database.QueryStringsAsync(
            "SELECT title FROM music_tracks WHERE track_id = 'fixture-temple-calm'", ct)).ShouldBe(["Renamed"]);
    }

    [Fact]
    public async Task Seed_AbsentCatalog_LeavesTheTableUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        await using var db = Database.CreateAppDbContext(clock: new FakeClock());
        await CreateSeeder(db, FixtureFile("valid-catalog.json")).SeedAsync(ct);

        var missing = Path.Combine(Path.GetTempPath(), $"music-{Guid.NewGuid():N}.json");
        await using var other = Database.CreateAppDbContext(clock: new FakeClock());
        var report = await CreateSeeder(other, missing, environment: "Production").SeedAsync(ct);

        report.ShouldBe(new SeedReport("music_tracks", 0, 0, 0));
        (await CountAsync(ct)).ShouldBe(3);
    }

    [Theory]
    [InlineData("invalid-license.json", "license_unknown")]
    [InlineData("invalid-attribution.json", "attribution_required")]
    // The fixture trips both track_id_duplicate and file_duplicate; the seeder throws the first
    // violation, which is the id duplicate (the unit fixture test asserts the full code list).
    [InlineData("invalid-duplicate-file.json", "track_id_duplicate")]
    [InlineData("invalid-revision.json", "revision_not_positive")]
    [InlineData("invalid-duplicate-key.json", "music_catalog_invalid")]
    [InlineData("invalid-empty-tracks.json", "music_catalog_invalid")]
    [InlineData("invalid-loudness-scale.json", "loudness_scale")]
    [InlineData("invalid-duration-max.json", "duration_above_maximum")]
    [InlineData("invalid-title-blank.json", "title_blank")]
    [InlineData("invalid-artist-blank.json", "artist_blank")]
    [InlineData("invalid-source-url.json", "source_url_invalid")]
    public async Task Seed_InvalidCatalog_WritesNothing(string fixture, string code)
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        await using var db = Database.CreateAppDbContext(clock: new FakeClock());

        var exception = await Should.ThrowAsync<SeedValidationException>(
            () => CreateSeeder(db, FixtureFile(fixture), environment: "Production").SeedAsync(ct).AsTask());

        exception.Code.ShouldContain(code);
        (await CountAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Seed_GainWithExtraPrecision_IsStoredAtTheColumnScale_AndAFreshContextReseedIsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FixtureFile("valid-catalog.json")))!.AsObject();
        var track = document["tracks"]!.AsArray()[0]!.AsObject();
        track["loudnessLufs"] = -20.0m;
        track["bgmVolume"] = 0.2004m;
        var path = WriteCatalog(document.ToJsonString());

        await using (var first = Database.CreateAppDbContext(clock: new FakeClock()))
        {
            (await CreateSeeder(first, path).SeedAsync(ct)).Inserted.ShouldBe(3);
        }

        await using var second = Database.CreateAppDbContext(clock: new FakeClock());
        var report = await CreateSeeder(second, path).SeedAsync(ct);

        report.ShouldBe(new SeedReport("music_tracks", 0, 0, 3));
        (await Database.QueryStringsAsync(
            "SELECT bgm_volume::text FROM music_tracks WHERE bgm_volume = 0.200", ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Seed_CatalogOverOneMiB_IsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        await using var db = Database.CreateAppDbContext(clock: new FakeClock());
        var path = WriteCatalog(File.ReadAllText(FixtureFile("valid-catalog.json")) + new string(' ', 1024 * 1024));

        var report = await CreateSeeder(db, path).SeedAsync(ct);

        report.ShouldBe(new SeedReport("music_tracks", 0, 0, 0));
        (await CountAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Seed_Revision0_IsAllowedInDevelopmentOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        await using var db = Database.CreateAppDbContext(clock: new FakeClock());

        var report = await CreateSeeder(db, FixtureFile("invalid-revision.json"), environment: "Development").SeedAsync(ct);

        report.Inserted.ShouldBe(1);
    }

    [Fact]
    public async Task Seed_CommittedCatalog_LoadsAll30Tracks()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        await using var db = Database.CreateAppDbContext(clock: new FakeClock());

        var report = await CreateSeeder(db, Path.Combine(SeedTestHarness.RepoRoot, "seed", "music-tracks.v1.json"))
            .SeedAsync(ct);

        report.Inserted.ShouldBe(30);
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM music_tracks WHERE active", ct)).ShouldBe(["30"]);
    }

    [Fact]
    public async Task Seed_CommittedCatalogTwice_IsIdempotentAndLeavesRowsByteIdentical()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var path = Path.Combine(SeedTestHarness.RepoRoot, "seed", "music-tracks.v1.json");
        const string Snapshot = """
            SELECT md5(string_agg(concat_ws('|', id, track_id, file_name, title, artist, source_url, license,
                coalesce(attribution, '~'), platform_scope, array_to_string(moods, ','), duration_sec, loudness_lufs,
                bgm_volume, sha256, storage_key, catalog_revision, active, created_at, updated_at), E'\n' ORDER BY track_id))
            FROM music_tracks
            """;

        await using var first = Database.CreateAppDbContext(clock: new FakeClock());
        var one = await CreateSeeder(first, path).SeedAsync(ct);
        var before = (await Database.QueryStringsAsync(Snapshot, ct)).Single();

        await using var second = Database.CreateAppDbContext(clock: new FakeClock());
        var two = await CreateSeeder(second, path).SeedAsync(ct);
        var after = (await Database.QueryStringsAsync(Snapshot, ct)).Single();

        one.Inserted.ShouldBe(30);
        two.ShouldBe(new SeedReport("music_tracks", 0, 0, 30));
        after.ShouldBe(before, "a second run must not touch a single column, including updated_at");
        (await CountAsync(ct)).ShouldBe(30);
    }

    [Fact]
    public async Task Seed_TrackNoLongerListed_IsDeactivatedNotDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        await using var db = Database.CreateAppDbContext(clock: new FakeClock());
        await CreateSeeder(db, FixtureFile("valid-catalog.json")).SeedAsync(ct);
        var trimmed = WriteCatalog(TrimToFirstTrack(File.ReadAllText(FixtureFile("valid-catalog.json"))));
        try
        {
            await using var second = Database.CreateAppDbContext(clock: new FakeClock());
            var report = await CreateSeeder(second, trimmed).SeedAsync(ct);

            report.Inserted.ShouldBe(0);
        }
        finally
        {
            File.Delete(trimmed);
        }

        (await CountAsync(ct)).ShouldBe(3);
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM music_tracks WHERE active", ct)).ShouldBe(["1"]);
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM music_tracks WHERE NOT active", ct)).ShouldBe(["2"]);
    }

    [Fact]
    public async Task Seed_InvalidCatalog_AfterAGoodSeed_LeavesTheExistingRowsUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        await using var db = Database.CreateAppDbContext(clock: new FakeClock());
        await CreateSeeder(db, FixtureFile("valid-catalog.json")).SeedAsync(ct);

        await using var bad = Database.CreateAppDbContext(clock: new FakeClock());
        await Should.ThrowAsync<SeedValidationException>(
            () => CreateSeeder(bad, FixtureFile("invalid-sha256.json"), environment: "Production").SeedAsync(ct).AsTask());

        (await CountAsync(ct)).ShouldBe(3);
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM music_tracks WHERE active", ct)).ShouldBe(["3"]);
    }

    private static MusicTrackSeeder CreateSeeder(AppDbContext db, string catalogFile, string environment = "Development") =>
        new(
            db,
            Options.Create(new SeedOptions { MusicTracksFile = catalogFile }),
            SeedTestHarness.Environment(environment),
            NullLogger<MusicTrackSeeder>.Instance);

    private static string WriteCatalog(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"music-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        return path;
    }

    private static string TrimToFirstTrack(string json)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var tracks = document["tracks"]!.AsArray();
        while (tracks.Count > 1)
        {
            tracks.RemoveAt(tracks.Count - 1);
        }

        return document.ToJsonString();
    }

    private async Task ResetAsync(CancellationToken ct) =>
        await Database.ExecuteAsync("DELETE FROM music_tracks", ct);

    private async Task<int> CountAsync(CancellationToken ct) =>
        int.Parse(
            (await Database.QueryStringsAsync("SELECT count(*)::text FROM music_tracks", ct)).Single(),
            CultureInfo.InvariantCulture);
}
