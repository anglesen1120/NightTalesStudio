using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Features.Assets.Jobs;
using NightTales.Domain.Assets;
using NightTales.Domain.Episodes;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Assets;

/// <summary>
/// Independent QA probes for 09.7.3-T1 against real PostgreSQL: the planner really uses the partial due-assets
/// index, the sweep boundary is strict at the database, a finished run does not leak its session advisory
/// lock, and a second run is a no-op.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RetentionSweepQaTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 3, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    private static string Bucket => new StorageSettings().Buckets.Media;

    [Fact]
    public async Task Qa_DueAssetsQuery_UsesThePartialIndex_On10kAssets()
    {
        var userId = await CreateUserAsync();
        var episodeId = await SeedEpisodeAsync(userId);
        var seed = await Episodes(userId).CreateAssetAsync(userId, episodeId, retentionUntil: Start.AddDays(30), ct: Ct);

        try
        {
            await Database.ExecuteAsync(
                "INSERT INTO assets (id, user_id, episode_id, kind, storage_key, content_type, size_bytes, duration_sec, sha256, "
                + "retention_until, deleted_at, created_at, updated_at) "
                + "SELECT gen_random_uuid(), user_id, episode_id, kind, 'media/qa/' || g, content_type, size_bytes, duration_sec, sha256, "
                + $"CASE WHEN g <= 20 THEN '{Start:O}'::timestamptz - interval '1 day' ELSE '{Start:O}'::timestamptz + interval '30 days' END, "
                + "CASE WHEN g % 3 = 0 AND g > 20 THEN created_at ELSE NULL END, created_at, updated_at "
                + $"FROM assets, generate_series(1, 10000) g WHERE id = '{seed.Id}'",
                Ct);
            await Database.ExecuteAsync("ANALYZE assets", Ct);

            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var explain = new NpgsqlCommand(
                "EXPLAIN SELECT id, user_id FROM assets WHERE deleted_at IS NULL AND retention_until IS NOT NULL "
                + $"AND retention_until < '{Start:O}'::timestamptz LIMIT 500",
                connection);
            var plan = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync(Ct))
            {
                while (await reader.ReadAsync(Ct))
                {
                    plan.Add(reader.GetString(0));
                }
            }

            string.Join('\n', plan).ShouldContain("ix_assets_retention_due");
        }
        finally
        {
            // The class shares one database: leave no due rows behind for the sweep tests.
            await Database.ExecuteAsync("DELETE FROM assets WHERE storage_key LIKE 'media/qa/%'", Ct);
            await Database.ExecuteAsync("DELETE FROM assets WHERE id = '" + seed.Id + "'", Ct);
        }
    }

    [Fact]
    public async Task Qa_AssetDueExactlyAtTheClock_IsKept_AtTheDatabase()
    {
        var userId = await CreateUserAsync();
        var episodeId = await SeedEpisodeAsync(userId);
        var atNow = await Episodes(userId).CreateAssetAsync(userId, episodeId, retentionUntil: Start, ct: Ct);
        var before = await Episodes(userId).CreateAssetAsync(userId, episodeId, retentionUntil: Start.AddTicks(-10), ct: Ct);
        var store = Seeded(atNow, before);

        await using var host = await StartAsync(store);
        var summary = await RunAsync(host);

        summary.Deleted.ShouldBe(1);
        (await Row(userId, atNow.Id)).DeletedAt.ShouldBeNull();
        (await Row(userId, before.Id)).DeletedAt.ShouldBe(Start);
        store.Contains(Bucket, atNow.StorageKey).ShouldBeTrue();
        store.Contains(Bucket, before.StorageKey).ShouldBeFalse();
    }

    [Fact]
    public async Task Qa_SecondRun_IsANoOp_AndDoesNotRestampDeletedAt()
    {
        var userId = await CreateUserAsync();
        var episodeId = await SeedEpisodeAsync(userId);
        var asset = await Episodes(userId).CreateAssetAsync(userId, episodeId, retentionUntil: Start.AddDays(-1), ct: Ct);
        var store = Seeded(asset);

        await using var host = await StartAsync(store);
        (await RunAsync(host)).Deleted.ShouldBe(1);
        var first = await Row(userId, asset.Id);
        var deletes = store.CountOf(BlobOperation.Delete);

        var second = await RunAsync(host);

        second.Deleted.ShouldBe(0);
        second.Failed.ShouldBe(0);
        store.CountOf(BlobOperation.Delete).ShouldBe(deletes, "the second run deletes no object");
        (await Row(userId, asset.Id)).DeletedAt.ShouldBe(first.DeletedAt);
        (await Row(userId, asset.Id)).UpdatedAt.ShouldBe(first.UpdatedAt);
    }

    [Fact]
    public async Task Qa_FinishedRun_DoesNotLeakItsSessionAdvisoryLock()
    {
        var userId = await CreateUserAsync();
        var asset = await Episodes(userId).CreateAssetAsync(
            userId,
            await SeedEpisodeAsync(userId),
            retentionUntil: Start.AddDays(-1),
            ct: Ct);
        var store = Seeded(asset);

        await using var host = await StartAsync(store);
        (await RunAsync(host)).Deleted.ShouldBe(1);

        await using var probe = new NpgsqlConnection(Database.ConnectionString);
        await probe.OpenAsync(Ct);
        await using var take = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext('retention_sweep'))", probe);
        (await take.ExecuteScalarAsync(Ct)).ShouldBe(true, "a pooled sweep connection must not still hold the lock");

        await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext('retention_sweep'))", probe);
        await release.ExecuteScalarAsync(Ct);
    }

    private static InMemoryBlobStore Seeded(params Asset[] assets)
    {
        var store = new InMemoryBlobStore();
        foreach (var asset in assets)
        {
            store.Seed(Bucket, asset.StorageKey, [1, 2, 3]);
        }

        return store;
    }

    private static async Task<RetentionSweepSummary> RunAsync(WorkerHost host)
    {
        using var scope = host.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<RetentionSweepJob>().SweepAsync(Ct);
    }

    private async Task<WorkerHost> StartAsync(InMemoryBlobStore store)
    {
        var host = new WorkerHost(Database, new FakeClock(Start)).ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(store);
        });
        await host.StartAsync(Ct);

        return host;
    }

    private async Task<Asset> Row(Guid userId, Guid assetId)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });

        return await context.Assets.AsNoTracking().SingleAsync(asset => asset.Id == assetId, Ct);
    }

    private async Task<Guid> CreateUserAsync() =>
        (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct)).Id;

    private EpisodeBuilder Episodes(Guid userId) =>
        new(Database, new TestCurrentUser { Id = userId }, new FakeClock(Start));

    private async Task<Guid> SeedEpisodeAsync(Guid userId)
    {
        var series = new SeriesBuilder(Database, new TestCurrentUser(), new FakeClock(Start));
        var voice = await series.EnsureVoiceAsync(ct: Ct);
        var template = await series.EnsureStyleTemplateAsync(ct: Ct);
        var created = await series.CreateAsync(userId, title: "Chuỗi thử nghiệm", voiceId: voice.Id, styleTemplateId: template.Id, ct: Ct);

        return (await Episodes(userId).CreateAsync(userId, created.Id, status: EpisodeStatus.VideoReview, ct: Ct)).Id;
    }
}
