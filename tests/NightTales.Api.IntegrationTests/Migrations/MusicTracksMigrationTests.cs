using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Rendering.Music;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_05_MusicTracks</c> migration (05.3.2-T3, 05 README D8/CR-2): the global catalog table reaches a
/// fresh database with the agreed columns, the unique <c>track_id</c> index and the D8 CHECKs, carries no
/// <c>user_id</c>, matches the EF model, and its <c>Down</c> drops it. Its <c>Down</c> check finds the migration by
/// name; the newest migration's test (02.5.1-T1, <c>SourcesAndFetchTasksMigrationTests</c>) owns <c>migrations[^1]</c>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class MusicTracksMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "05_MusicTracks";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Applies_WithTheAgreedShape()
    {
        (await Database.QueryStringsAsync(
            "SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns "
            + "WHERE table_schema = 'public' AND table_name = 'music_tracks' ORDER BY ordinal_position",
            Ct))
            .ShouldBe(
            [
                "id:uuid:NO",
                "track_id:text:NO",
                "file_name:text:NO",
                "title:text:NO",
                "artist:text:NO",
                "source_url:text:NO",
                "license:text:NO",
                "attribution:text:YES",
                "platform_scope:text:NO",
                "moods:ARRAY:NO",
                "duration_sec:numeric:NO",
                "loudness_lufs:numeric:NO",
                "bgm_volume:numeric:NO",
                "sha256:character:NO",
                "storage_key:text:NO",
                "catalog_revision:integer:NO",
                "active:boolean:NO",
                "created_at:timestamp with time zone:NO",
                "updated_at:timestamp with time zone:NO",
            ],
            ignoreOrder: false);

        (await Database.QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'music_tracks' AND indexname = 'ux_music_tracks_track_id'",
            Ct))
            .ShouldHaveSingleItem().ShouldContain("UNIQUE INDEX");
    }

    [Fact]
    public async Task GlobalCatalog_HasNoTenantColumn()
    {
        (await Database.QueryStringsAsync(
            "SELECT count(*)::text FROM information_schema.columns "
            + "WHERE table_schema = 'public' AND table_name = 'music_tracks' AND column_name = 'user_id'",
            Ct))
            .ShouldBe(["0"]);
    }

    [Theory]
    [InlineData("license = 'MIT'", "ck_music_tracks_license")]
    [InlineData("platform_scope = 'tiktok'", "ck_music_tracks_platform_scope")]
    [InlineData("license = 'CC-BY-4.0', attribution = NULL", "ck_music_tracks_attribution")]
    [InlineData("license = 'YouTube-Audio-Library', platform_scope = 'any'", "ck_music_tracks_platform_scope")]
    [InlineData("moods = '{battle}'", "ck_music_tracks_moods")]
    [InlineData("moods = '{}'", "ck_music_tracks_moods")]
    [InlineData("bgm_volume = 0.5", "ck_music_tracks_bgm_volume")]
    [InlineData("duration_sec = 59.9", "ck_music_tracks_duration_sec")]
    [InlineData("sha256 = 'XYZ'", "ck_music_tracks_sha256")]
    [InlineData("track_id = 'Bad_Id'", "ck_music_tracks_track_id")]
    public void CheckConstraints_RefuseARowThatBreaksAD8Rule(string set, string constraint)
    {
        var trackId = $"mig-{Guid.NewGuid():N}"[..20];
        var insert = $"""
            INSERT INTO music_tracks (id, track_id, file_name, title, artist, source_url, license, attribution,
                platform_scope, moods, duration_sec, loudness_lufs, bgm_volume, sha256, storage_key,
                catalog_revision, active, created_at, updated_at)
            VALUES ('{Guid.NewGuid()}', '{trackId}', '{trackId}.mp3', 't', 'a', 'https://example.invalid/x', 'CC0-1.0', NULL,
                'any', ARRAY['calm'], 120.0, -20.00, 0.200, '{new string('a', 64)}', 'music/{trackId}.mp3', 1, true, now(), now())
            """;
        SqlProbe.Execute(Database, insert);
        var update = $"UPDATE music_tracks SET {set} WHERE track_id = '{trackId}'";

        var state = SqlProbe.SqlState(Database, update);

        state.ShouldBe("23514", $"{constraint}: {set}");
    }

    // QA: the SQLSTATE-only theory above cannot tell WHICH CHECK fired (it also passes when a different one
    // does), so each violation is repeated here asserting the constraint name Postgres reports.
    [Theory]
    [InlineData("license = 'MIT'", "ck_music_tracks_license")]
    [InlineData("platform_scope = 'tiktok'", "ck_music_tracks_platform_scope")]
    [InlineData("license = 'CC-BY-4.0', attribution = NULL", "ck_music_tracks_attribution")]
    [InlineData("license = 'CC0-1.0', attribution = 'credit'", "ck_music_tracks_attribution")]
    [InlineData("license = 'YouTube-Audio-Library', platform_scope = 'any'", "ck_music_tracks_platform_scope")]
    [InlineData("platform_scope = 'youtube'", "ck_music_tracks_platform_scope")]
    [InlineData("license = 'CC-BY-4.0', attribution = E' \\t\\r\\n'", "ck_music_tracks_attribution")]
    [InlineData("moods = '{battle}'", "ck_music_tracks_moods")]
    [InlineData("moods = '{}'", "ck_music_tracks_moods")]
    [InlineData("moods = '{calm,noir,suspense,temple}'", "ck_music_tracks_moods")]
    [InlineData("bgm_volume = 0.5", "ck_music_tracks_bgm_volume")]
    [InlineData("bgm_volume = 0.201", "ck_music_tracks_bgm_volume")]
    [InlineData("duration_sec = 59.9", "ck_music_tracks_duration_sec")]
    [InlineData("duration_sec = 3600.1", "ck_music_tracks_duration_sec")]
    [InlineData("sha256 = 'XYZ'", "ck_music_tracks_sha256")]
    [InlineData("track_id = 'Bad_Id', file_name = 'Bad_Id.mp3'", "ck_music_tracks_track_id")]
    [InlineData("track_id = 'Bad_Id'", "ck_music_tracks_file_name")]
    [InlineData("file_name = replace(file_name, '.mp3', '.wav')", "ck_music_tracks_file_name")]
    public void CheckConstraints_ReportTheExactConstraintThatWasBroken(string set, string constraint)
    {
        var trackId = $"mig-{Guid.NewGuid():N}"[..20];
        SqlProbe.Execute(Database, $"""
            INSERT INTO music_tracks (id, track_id, file_name, title, artist, source_url, license, attribution,
                platform_scope, moods, duration_sec, loudness_lufs, bgm_volume, sha256, storage_key,
                catalog_revision, active, created_at, updated_at)
            VALUES ('{Guid.NewGuid()}', '{trackId}', '{trackId}.mp3', 't', 'a', 'https://example.invalid/x', 'CC0-1.0', NULL,
                'any', ARRAY['calm'], 120.0, -20.00, 0.200, '{new string('a', 64)}', 'music/{trackId}.mp3', 1, true, now(), now())
            """);

        var exception = Should.Throw<Npgsql.PostgresException>(
            () => SqlProbe.Execute(Database, $"UPDATE music_tracks SET {set} WHERE track_id = '{trackId}'"));

        exception.SqlState.ShouldBe("23514");
        exception.ConstraintName.ShouldBe(constraint, set);
    }

    [Fact]
    public void UniqueIndex_RefusesADuplicateTrackId()
    {
        var trackId = $"dup-{Guid.NewGuid():N}"[..20];
        string Insert() => $"""
            INSERT INTO music_tracks (id, track_id, file_name, title, artist, source_url, license, attribution,
                platform_scope, moods, duration_sec, loudness_lufs, bgm_volume, sha256, storage_key,
                catalog_revision, active, created_at, updated_at)
            VALUES ('{Guid.NewGuid()}', '{trackId}', '{trackId}.mp3', 't', 'a', 'https://example.invalid/x', 'CC0-1.0', NULL,
                'any', ARRAY['calm'], 120.0, -20.00, 0.200, '{Guid.NewGuid():N}{Guid.NewGuid():N}', 'music/{trackId}.mp3', 1, true, now(), now())
            """;
        SqlProbe.Execute(Database, Insert());

        SqlProbe.SqlState(Database, Insert()).ShouldBe("23505");
    }

    [Fact]
    public async Task Model_MatchesTheMigration()
    {
        await using var context = Database.CreateAppDbContext();
        var entity = context.Model.FindEntityType(typeof(MusicTrack));
        entity.ShouldNotBeNull("the model must map the music_tracks row (D8)");
        entity!.GetTableName().ShouldBe("music_tracks");
        entity.GetDeclaredQueryFilters().ShouldBeEmpty("the catalog is global: no tenant filter (09.2.3)");
        entity.GetProperties()
            .Select(property => property.GetColumnName())
            .Order(StringComparer.Ordinal)
            .ShouldBe(
            [
                "active",
                "artist",
                "attribution",
                "bgm_volume",
                "catalog_revision",
                "created_at",
                "duration_sec",
                "file_name",
                "id",
                "license",
                "loudness_lufs",
                "moods",
                "platform_scope",
                "sha256",
                "source_url",
                "storage_key",
                "title",
                "track_id",
                "updated_at",
            ]);

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
    }

    [Fact]
    public async Task DownMigration_DropsTable()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");

        try
        {
            // Reverting to the migration before this one also reverts every later migration.
            await context.Database.GetService<IMigrator>().MigrateAsync(migrations[own - 1], Ct);

            (await Database.QueryStringsAsync("SELECT coalesce(to_regclass('public.music_tracks')::text, '')", Ct))
                .ShouldBe([string.Empty], ignoreOrder: false, customMessage: "the Down drops the table");
        }
        finally
        {
            await context.Database.MigrateAsync(CancellationToken.None);
        }

        (await Database.QueryStringsAsync("SELECT count(*)::text FROM music_tracks", Ct))
            .ShouldBe(["0"], ignoreOrder: false, customMessage: "the re-created table is empty");
    }
}
