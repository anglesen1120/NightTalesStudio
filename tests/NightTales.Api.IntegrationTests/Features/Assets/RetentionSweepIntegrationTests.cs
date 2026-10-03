using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Jobs;
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
using NightTales.Worker.Jobs;
using Shouldly;
using Xunit;
using Error = ErrorOr.Error;

namespace NightTales.Api.IntegrationTests.Features.Assets;

/// <summary>
/// <c>RetentionSweepJob</c> through the Worker's own graph (09.7.3-T1): the real <c>ISystemWorkQuery</c>, the
/// per-row pinned scope, the tracked <c>MarkDeleted</c> save and the PostgreSQL advisory lock, with the object
/// store replaced by <see cref="InMemoryBlobStore"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RetentionSweepIntegrationTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 3, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    private static string Bucket => new StorageSettings().Buckets.Media;

    [Fact]
    public async Task RetentionSweep_ThroughWorkerHost_DeletesObjectAndMarksRow()
    {
        var userId = await CreateUserAsync();
        var episodeId = await SeedEpisodeAsync(userId);
        var due = await Episodes(userId).CreateAssetAsync(userId, episodeId, retentionUntil: Start.AddDays(-1), ct: Ct);
        var future = await Episodes(userId).CreateAssetAsync(userId, episodeId, retentionUntil: Start.AddDays(5), ct: Ct);
        var store = Seeded(due, future);

        await using var host = await StartAsync(store);
        var summary = await RunAsync(host);

        summary.Deleted.ShouldBe(1);
        store.Contains(Bucket, due.StorageKey).ShouldBeFalse();
        store.Contains(Bucket, future.StorageKey).ShouldBeTrue();
        (await Row(userId, due.Id)).DeletedAt.ShouldBe(Start);
        (await Row(userId, future.Id)).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task RetentionSweep_OfAnotherUsersAssets_DoesNotThrowAndUsesSystemScope()
    {
        var first = await CreateUserAsync();
        var second = await CreateUserAsync();
        var a = await Episodes(first).CreateAssetAsync(first, await SeedEpisodeAsync(first), retentionUntil: Start.AddDays(-2), ct: Ct);
        var b = await Episodes(second).CreateAssetAsync(second, await SeedEpisodeAsync(second), retentionUntil: Start.AddDays(-2), ct: Ct);
        var store = Seeded(a, b);

        await using var host = await StartAsync(store);
        var summary = await RunAsync(host);

        summary.Failed.ShouldBe(0);
        (await Row(first, a.Id)).DeletedAt.ShouldBe(Start);
        (await Row(second, b.Id)).DeletedAt.ShouldBe(Start);
    }

    [Fact]
    public async Task RetentionSweep_SecondRun_AfterAPartialFailure_RetriesOnlyTheFailedRows()
    {
        var userId = await CreateUserAsync();
        var episodeId = await SeedEpisodeAsync(userId);
        var asset = await Episodes(userId).CreateAssetAsync(userId, episodeId, retentionUntil: Start.AddDays(-1), ct: Ct);
        var store = Seeded(asset);
        store.AsyncFailure = Error.Failure("service_unavailable", "down");

        await using var host = await StartAsync(store);
        var failed = await RunAsync(host);

        failed.Failed.ShouldBeGreaterThanOrEqualTo(1);
        (await Row(userId, asset.Id)).DeletedAt.ShouldBeNull("nothing is marked while the object is still there");

        store.AsyncFailure = null;
        var retried = await RunAsync(host);

        retried.Deleted.ShouldBeGreaterThanOrEqualTo(1);
        (await Row(userId, asset.Id)).DeletedAt.ShouldBe(Start);
        store.Contains(Bucket, asset.StorageKey).ShouldBeFalse();
    }

    [Fact]
    public async Task RetentionSweep_ExitsWhenTheRealAdvisoryLockIsHeld()
    {
        var userId = await CreateUserAsync();
        var asset = await Episodes(userId).CreateAssetAsync(
            userId,
            await SeedEpisodeAsync(userId),
            retentionUntil: Start.AddDays(-1),
            ct: Ct);
        var store = Seeded(asset);

        await using var holder = new NpgsqlConnection(Database.ConnectionString);
        await holder.OpenAsync(Ct);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock(hashtext('retention_sweep'))", holder))
        {
            await take.ExecuteScalarAsync(Ct);
        }

        await using var host = await StartAsync(store);
        var skipped = await RunAsync(host);

        skipped.Deleted.ShouldBe(0);
        store.Contains(Bucket, asset.StorageKey).ShouldBeTrue();

        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext('retention_sweep'))", holder))
        {
            await release.ExecuteScalarAsync(Ct);
        }

        (await RunAsync(host)).Deleted.ShouldBe(1);
    }

    [Fact]
    public async Task RetentionSweep_Registration_IsDailyAt03Utc_AndRemovedWhenDisabled()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scheduler = new InlineJobScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        new RetentionSweepRegistration(Options.Create(new RetentionSweepOptions())).Register(scheduler);

        scheduler.RecurringUpserts.ShouldHaveSingleItem().ShouldBe(new RecordedRecurringCall(
            "retention_sweep",
            nameof(RetentionSweepJob),
            JobQueues.Name(JobQueue.Default),
            "0 3 * * *"));

        var disabled = new InlineJobScheduler(provider.GetRequiredService<IServiceScopeFactory>());
        new RetentionSweepRegistration(Options.Create(new RetentionSweepOptions { Enabled = false })).Register(disabled);

        disabled.RecurringUpserts.ShouldBeEmpty();
        disabled.RecurringRemovals.ShouldBe(["retention_sweep"]);
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
