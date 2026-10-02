using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Seeding;

/// <summary>
/// The 04.2.2-T2 voice seeder against a real migrated database: it upserts the entries of the tracked
/// <c>seed/voices.v1.json</c> — including <c>preview_storage_key</c> — in place, leaves rows the file no
/// longer lists alone, refuses <c>revision 0</c> outside Development, and writes nothing at all when the
/// file breaks a D9 rule (04 README D9, CR-9).
/// </summary>
public sealed class VoiceSeederTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// A minimal valid revision-1 catalog: two vi and one en active entry, one of them with a preview
    /// key. It carries an extra vi entry so a later file can drop it while the catalog stays valid
    /// (revision 1 needs at least one active voice per language).
    /// </summary>
    private const string RevisionOneFile = """
        {
          "schema": "voices.v1",
          "revision": 1,
          "vieneuCommit": "c1390abbdb2eedcdf58eafb546966c06ce27af71",
          "voices": [
            {
              "id": "Hải Đăng",
              "slug": "hai-dang",
              "displayName": "Hải Đăng",
              "language": "vi",
              "gender": "male",
              "tags": ["storyteller", "deep"],
              "active": true,
              "previewStorageKey": "voice-previews/hai-dang.v1.mp3"
            },
            {
              "id": "Ngọc Huyền",
              "slug": "ngoc-huyen",
              "displayName": "Ngọc Huyền",
              "language": "en",
              "gender": "female",
              "tags": [],
              "active": true,
              "previewStorageKey": null
            },
            {
              "id": "Mai Anh",
              "slug": "mai-anh",
              "displayName": "Mai Anh",
              "language": "vi",
              "gender": "female",
              "tags": ["news"],
              "active": true,
              "previewStorageKey": null
            }
          ]
        }
        """;

    /// <summary>
    /// The same catalog plus a new vi entry, with every catalog-owned field of <c>Hải Đăng</c> changed
    /// (display name, language, gender, tags, active, preview key) and <c>Ngọc Huyền</c> untouched. A
    /// new active vi entry is required because the changed one becomes inactive (revision 1 needs one
    /// active voice per language).
    /// </summary>
    private const string RevisionOneChangedFile = """
        {
          "schema": "voices.v1",
          "revision": 1,
          "vieneuCommit": "c1390abbdb2eedcdf58eafb546966c06ce27af71",
          "voices": [
            {
              "id": "Mai Anh",
              "slug": "mai-anh",
              "displayName": "Mai Anh",
              "language": "vi",
              "gender": "female",
              "tags": ["news"],
              "active": true,
              "previewStorageKey": null
            },
            {
              "id": "Hải Đăng",
              "slug": "hai-dang",
              "displayName": "Hải Đăng mới",
              "language": "en",
              "gender": "neutral",
              "tags": ["calm"],
              "active": false,
              "previewStorageKey": "voice-previews/hai-dang.v12.mp3"
            },
            {
              "id": "Ngọc Huyền",
              "slug": "ngoc-huyen",
              "displayName": "Ngọc Huyền",
              "language": "en",
              "gender": "female",
              "tags": [],
              "active": true,
              "previewStorageKey": null
            }
          ]
        }
        """;

    /// <summary>The first catalog with <c>Mai Anh</c> removed: the file still satisfies revision 1.</summary>
    private const string RevisionOneReducedFile = """
        {
          "schema": "voices.v1",
          "revision": 1,
          "vieneuCommit": "c1390abbdb2eedcdf58eafb546966c06ce27af71",
          "voices": [
            {
              "id": "Hải Đăng",
              "slug": "hai-dang",
              "displayName": "Hải Đăng",
              "language": "vi",
              "gender": "male",
              "tags": ["storyteller", "deep"],
              "active": true,
              "previewStorageKey": "voice-previews/hai-dang.v1.mp3"
            },
            {
              "id": "Ngọc Huyền",
              "slug": "ngoc-huyen",
              "displayName": "Ngọc Huyền",
              "language": "en",
              "gender": "female",
              "tags": [],
              "active": true,
              "previewStorageKey": null
            }
          ]
        }
        """;

    /// <summary>A valid revision-0 placeholder: the en entry is inactive, which only revision 1 forbids.</summary>
    private const string RevisionZeroFile = """
        {
          "schema": "voices.v1",
          "revision": 0,
          "vieneuCommit": "c1390abbdb2eedcdf58eafb546966c06ce27af71",
          "voices": [
            {
              "id": "Hải Đăng",
              "slug": "hai-dang",
              "displayName": "Hải Đăng",
              "language": "vi",
              "gender": "male",
              "tags": [],
              "active": true,
              "previewStorageKey": null
            },
            {
              "id": "Ngọc Huyền",
              "slug": "ngoc-huyen",
              "displayName": "Ngọc Huyền",
              "language": "en",
              "gender": "female",
              "tags": [],
              "active": false,
              "previewStorageKey": null
            }
          ]
        }
        """;

    /// <summary>The invalid catalog: one entry's slug does not match its id, and nothing else is wrong.</summary>
    private const string SlugMismatchFile = """
        {
          "schema": "voices.v1",
          "revision": 1,
          "vieneuCommit": "c1390abbdb2eedcdf58eafb546966c06ce27af71",
          "voices": [
            {
              "id": "Hải Đăng",
              "slug": "hai-dang-1",
              "displayName": "Hải Đăng",
              "language": "vi",
              "gender": "male",
              "tags": [],
              "active": true,
              "previewStorageKey": null
            },
            {
              "id": "Ngọc Huyền",
              "slug": "ngoc-huyen",
              "displayName": "Ngọc Huyền",
              "language": "en",
              "gender": "female",
              "tags": [],
              "active": true,
              "previewStorageKey": null
            }
          ]
        }
        """;

    [Fact]
    public async Task Seed_Revision1_UpsertsAllEntriesIncludingPreviewKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var path = WriteSeedFile(RevisionOneFile);

        try
        {
            await using (var db = Database.CreateAppDbContext(clock: new FakeClock()))
            {
                var report = await CreateSeeder(db, path).SeedAsync(ct);

                report.ShouldBe(new SeedReport("voices", 3, 0, 0));
            }
        }
        finally
        {
            File.Delete(path);
        }

        var rows = await ReadVoicesAsync(ct);

        rows.Count.ShouldBe(3);
        rows["Hải Đăng"].ShouldBe(new VoiceRow("Hải Đăng", Language.Vi, VoiceGender.Male, "storyteller,deep", "voice-previews/hai-dang.v1.mp3", true));
        rows["Ngọc Huyền"].ShouldBe(new VoiceRow("Ngọc Huyền", Language.En, VoiceGender.Female, string.Empty, null, true));
        rows["Mai Anh"].ShouldBe(new VoiceRow("Mai Anh", Language.Vi, VoiceGender.Female, "news", null, true));
    }

    [Fact]
    public async Task Seed_Rerun_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var path = WriteSeedFile(RevisionOneFile);

        try
        {
            await using (var db = Database.CreateAppDbContext(clock: new FakeClock()))
            {
                await CreateSeeder(db, path).SeedAsync(ct);
            }

            await using (var db = Database.CreateAppDbContext(clock: new FakeClock()))
            {
                var second = await CreateSeeder(db, path).SeedAsync(ct);

                second.ShouldBe(new SeedReport("voices", 0, 0, 3));
            }
        }
        finally
        {
            File.Delete(path);
        }

        (await CountVoicesAsync(ct)).ShouldBe(3);
    }

    [Fact]
    public async Task Seed_ChangedEntry_UpdatesRow_KeepsSeriesReference()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var path = WriteSeedFile(RevisionOneFile);
        var changedPath = WriteSeedFile(RevisionOneChangedFile);

        try
        {
            await using (var db = Database.CreateAppDbContext(clock: new FakeClock()))
            {
                await CreateSeeder(db, path).SeedAsync(ct);
            }

            var actor = new TestCurrentUser();
            var user = await new UserBuilder(Database, actor).CreateAsync(ct: ct);
            var series = await new SeriesBuilder(Database, actor)
                .CreateAsync(user.Id, voiceId: "Hải Đăng", ct: ct);

            await using (var db = Database.CreateAppDbContext(clock: new FakeClock()))
            {
                var report = await CreateSeeder(db, changedPath).SeedAsync(ct);

                report.ShouldBe(new SeedReport("voices", 0, 1, 2));
            }

            var rows = await ReadVoicesAsync(ct);

            rows.Count.ShouldBe(3);
            rows["Hải Đăng"].ShouldBe(new VoiceRow("Hải Đăng mới", Language.En, VoiceGender.Neutral, "calm", "voice-previews/hai-dang.v12.mp3", false));

            var referenced = await Database.QueryStringsAsync($"SELECT voice_id FROM series WHERE id = '{series.Id:D}'", ct);

            referenced.Single().ShouldBe("Hải Đăng");
        }
        finally
        {
            File.Delete(path);
            File.Delete(changedPath);
        }
    }

    [Fact]
    public async Task Seed_InvalidFile_WritesNothing_ReportsSeedVoiceInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var path = WriteSeedFile(SlugMismatchFile);

        try
        {
            await using var db = Database.CreateAppDbContext(clock: new FakeClock());

            var exception = await Should.ThrowAsync<SeedValidationException>(
                () => CreateSeeder(db, path).SeedAsync(ct).AsTask());

            exception.Code.ShouldBe("seed_voice_invalid:Hải Đăng:voice_slug_mismatch");
            exception.Message.ShouldBe(exception.Code);
        }
        finally
        {
            File.Delete(path);
        }

        (await CountVoicesAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Seed_Revision0_NonDevelopment_Fails()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var path = WriteSeedFile(RevisionZeroFile);

        try
        {
            await using var db = Database.CreateAppDbContext(clock: new FakeClock());

            var exception = await Should.ThrowAsync<SeedValidationException>(
                () => CreateSeeder(db, path, "Production").SeedAsync(ct).AsTask());

            exception.Code.ShouldBe("seed_voice_invalid:-:catalog_invalid");
        }
        finally
        {
            File.Delete(path);
        }

        (await CountVoicesAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Seed_Revision0_Development_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var path = WriteSeedFile(RevisionZeroFile);

        try
        {
            await using var db = Database.CreateAppDbContext(clock: new FakeClock());

            var report = await CreateSeeder(db, path).SeedAsync(ct);

            report.ShouldBe(new SeedReport("voices", 2, 0, 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Seed_EntryRemovedFromFile_RowLeftUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var path = WriteSeedFile(RevisionOneFile);
        var reducedPath = WriteSeedFile(RevisionOneReducedFile);

        try
        {
            await using (var db = Database.CreateAppDbContext(clock: new FakeClock()))
            {
                await CreateSeeder(db, path).SeedAsync(ct);
            }

            await using (var db = Database.CreateAppDbContext(clock: new FakeClock()))
            {
                await CreateSeeder(db, reducedPath).SeedAsync(ct);
            }

            var rows = await ReadVoicesAsync(ct);

            // The reduced file no longer lists Mai Anh, and the row it seeded keeps its values.
            rows.Count.ShouldBe(3);
            rows["Hải Đăng"].ShouldBe(new VoiceRow("Hải Đăng", Language.Vi, VoiceGender.Male, "storyteller,deep", "voice-previews/hai-dang.v1.mp3", true));
            rows["Mai Anh"].ShouldBe(new VoiceRow("Mai Anh", Language.Vi, VoiceGender.Female, "news", null, true));
            rows.Keys.ShouldBe(["Hải Đăng", "Ngọc Huyền", "Mai Anh"], ignoreOrder: true);
        }
        finally
        {
            File.Delete(path);
            File.Delete(reducedPath);
        }

        (await CountVoicesAsync(ct)).ShouldBe(3);
    }

    private static VoiceSeeder CreateSeeder(AppDbContext db, string voiceCatalogFile, string environment = "Development") =>
        new(
            db,
            Options.Create(new SeedOptions { VoiceCatalogFile = voiceCatalogFile }),
            SeedTestHarness.Environment(environment),
            NullLogger<VoiceSeeder>.Instance);

    private static string WriteSeedFile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"voices-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        return path;
    }

    private async Task ResetAsync(CancellationToken ct) =>
        await Database.ExecuteAsync("DELETE FROM series; DELETE FROM voices", ct);

    private async Task<int> CountVoicesAsync(CancellationToken ct) =>
        int.Parse(
            (await Database.QueryStringsAsync("SELECT count(*)::text FROM voices", ct)).Single(),
            CultureInfo.InvariantCulture);

    private async Task<IReadOnlyDictionary<string, VoiceRow>> ReadVoicesAsync(CancellationToken ct)
    {
        await using var db = Database.CreateAppDbContext(clock: new FakeClock());

        var rows = await db.Voices.AsNoTracking().ToListAsync(ct);

        return rows.ToDictionary(
            voice => voice.Id,
            voice => new VoiceRow(
                voice.DisplayName,
                voice.Language,
                voice.Gender,
                string.Join(',', voice.Tags),
                voice.PreviewStorageKey,
                voice.Active),
            StringComparer.Ordinal);
    }

    /// <summary>The catalog-owned columns of one <c>voices</c> row, for a whole-row comparison.</summary>
    private sealed record VoiceRow(
        string DisplayName,
        Language Language,
        VoiceGender Gender,
        string Tags,
        string? PreviewStorageKey,
        bool Active);
}
