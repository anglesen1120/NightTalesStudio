using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Storage;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Health;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Storage;

/// <summary>
/// The storage half of <c>/health/ready</c> and the prober behind it (09 README BD12/BD16, 09.7.2-T1
/// acceptance WT38/WT15): a storage fault reports <em>degraded</em> with the probe still answering 200,
/// a stale cache does the same, and readiness never touches S3.
/// </summary>
/// <remarks>
/// The readiness tests run the Worker's real health pipeline through <see cref="WorkerHealthProbe"/> and
/// freeze the cache, so what they assert is the check's own decision rather than the prober's timing.
/// The prober's three cases drive <c>ProbeAsync</c> directly over the in-memory store — no container,
/// no sleep (09 README D13).
/// </remarks>
[Trait("Category", "Integration")]
public sealed class StorageHealthTests(TestDatabase database) : IntegrationTest(database)
{
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=nts_unreachable;Username=nts;Password=nts-not-a-real-password";

    private const string MarkerKey = "_health/storage-ready.txt";

    // The check's name and its three constant descriptions (09.7.2-T1). They are repeated here because
    // the Worker assembly is referenced by TestKit under an alias, so a test cannot name
    // NightTales.Worker.Health.StorageHealthCheck; the probe's own tests assert the descriptions, so a
    // rename there fails them.
    private const string CheckName = "storage";
    private const string ReadyDescription = "storage ready";
    private const string UnavailableDescription = "storage unavailable";
    private const string StaleDescription = "storage health stale";

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task HealthReady_Returns200WithDegradedStatus_WhenStorageIsUnhealthy()
    {
        await using var probe = await StartProbeAsync(Database.ConnectionString, WithoutProber);

        Publish(probe, new StorageHealth(false, FakeClock.DefaultStart, StorageHealth.UnavailableErrorCode));

        var (response, report) = await ReadReadyAsync(probe);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "a storage fault never turns readiness into a 503");
        report.GetProperty("status").GetString().ShouldBe("degraded");
        StorageCheck(report).GetProperty("status").GetString().ShouldBe("degraded");
        StorageCheck(report).GetProperty("description").GetString().ShouldBe(UnavailableDescription);
    }

    [Fact]
    public async Task HealthReady_Returns200WithDegradedStatus_WhenTheCacheIsStale()
    {
        await using var probe = await StartProbeAsync(Database.ConnectionString, WithoutProber);

        // The last observation was healthy, but it is an hour old: 3 × the 30-second interval has passed,
        // which is the prober-died signal (WT38).
        Publish(probe, new StorageHealth(true, FakeClock.DefaultStart.AddHours(-1), ErrorCode: null));

        var (response, report) = await ReadReadyAsync(probe);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        report.GetProperty("status").GetString().ShouldBe("degraded");
        StorageCheck(report).GetProperty("status").GetString().ShouldBe("degraded");
        StorageCheck(report).GetProperty("description").GetString().ShouldBe(StaleDescription);
    }

    [Fact]
    public async Task HealthReady_ReportsHealthyStorage_WhenTheCacheIsFresh()
    {
        await using var probe = await StartProbeAsync(Database.ConnectionString, WithoutProber);

        Publish(probe, new StorageHealth(true, FakeClock.DefaultStart, ErrorCode: null));

        var (response, report) = await ReadReadyAsync(probe);

        // The control of the two degraded cases: a fresh, healthy observation really does report healthy,
        // so those cases are about the state and not about a check that always degrades.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        report.GetProperty("status").GetString().ShouldBe("healthy");
        StorageCheck(report).GetProperty("status").GetString().ShouldBe("healthy");
        StorageCheck(report).GetProperty("description").GetString().ShouldBe(ReadyDescription);
    }

    [Fact]
    public async Task HealthReady_DatabaseFailure_Is503()
    {
        await using var probe = await StartProbeAsync(UnreachableDatabase, WithoutProber);

        Publish(probe, new StorageHealth(true, FakeClock.DefaultStart, ErrorCode: null));

        var (response, report) = await ReadReadyAsync(probe);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        report.GetProperty("status").GetString().ShouldBe("unhealthy");
    }

    [Fact]
    public async Task HealthReady_DoesNotCallTheBlobStore()
    {
        var store = new InMemoryBlobStore();
        await using var probe = await StartProbeAsync(Database.ConnectionString, services =>
        {
            WithoutProber(services);
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(store);
        });

        Publish(probe, new StorageHealth(true, FakeClock.DefaultStart, ErrorCode: null));

        await ReadReadyAsync(probe);
        await ReadReadyAsync(probe);

        // BD16: "health endpoints read caches only". An LB that probes every second must not become an
        // S3 request storm.
        store.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task HealthReady_ReportsDegradedStorage_BeforeTheFirstProbe()
    {
        // A host that just started has StorageHealth.NotChecked in the cache — never probed, so
        // "unknown" must read as degraded, not healthy (the initial CheckedAt is stale by design).
        await using var probe = await StartProbeAsync(Database.ConnectionString, WithoutProber);

        var (response, report) = await ReadReadyAsync(probe);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        report.GetProperty("status").GetString().ShouldBe("degraded");
        StorageCheck(report).GetProperty("status").GetString().ShouldBe("degraded");
        StorageCheck(report).GetProperty("description").GetString().ShouldBe(StaleDescription);
    }

    [Fact]
    public async Task StorageHealthProbe_WritesMarkerOnce_WhenAbsent()
    {
        var store = new InMemoryBlobStore();
        var cache = new StorageHealthCache();
        var clock = new FakeClock();
        var probe = Prober(store, cache, clock);

        var first = await probe.ProbeAsync(Ct);

        first.IsHealthy.ShouldBeTrue();
        cache.Current.ShouldBe(first);
        first.CheckedAt.ShouldBe(clock.UtcNow);
        store.CountOf(BlobOperation.Exists).ShouldBe(1);
        store.CountOf(BlobOperation.Put).ShouldBe(1);
        store.Contains("nt-media-dev", MarkerKey).ShouldBeTrue("the marker is the source of truth");

        var second = await probe.ProbeAsync(Ct);

        second.IsHealthy.ShouldBeTrue();
        store.CountOf(BlobOperation.Put).ShouldBe(1, "a present marker is never rewritten");
    }

    [Fact]
    public async Task StorageHealthProbe_DoesNotRewriteMarker_WhenPresent()
    {
        var store = new InMemoryBlobStore();
        store.Seed("nt-media-dev", MarkerKey, [], "text/plain");

        var probe = Prober(store, new StorageHealthCache(), new FakeClock());

        var health = await probe.ProbeAsync(Ct);

        health.IsHealthy.ShouldBeTrue();
        store.CountOf(BlobOperation.Put).ShouldBe(0);
    }

    [Fact]
    public async Task StorageHealthProbe_OnStorageFailure_SetsUnhealthyCache()
    {
        var store = new InMemoryBlobStore
        {
            AsyncFailure = Error.Failure(ErrorCodes.ServiceUnavailable, "storage unreachable (test)"),
        };

        var cache = new StorageHealthCache();
        var clock = new FakeClock();
        var probe = Prober(store, cache, clock);

        var health = await probe.ProbeAsync(Ct);

        health.IsHealthy.ShouldBeFalse();
        health.ErrorCode.ShouldBe(StorageHealth.UnavailableErrorCode);
        health.CheckedAt.ShouldBe(clock.UtcNow);
        cache.Current.ShouldBe(health, "the failure is published, not thrown");
    }

    private static StorageHealthProbe Prober(
        InMemoryBlobStore store,
        IStorageHealthCache cache,
        FakeClock clock) =>
        new(
            Options.Create(new StorageSettings
            {
                ServiceUrl = StorageTestEndpoint.Unreachable,
                AccessKey = "test-access-key",
                SecretKey = "test-secret-key",
                Buckets = new StorageBuckets { Media = "nt-media-dev" },
            }),
            store,
            cache,
            clock,
            NullLogger<StorageHealthProbe>.Instance);

    private static void WithoutProber(IServiceCollection services) => services.WithoutStorageHealthProbe();

    private static async Task<WorkerHealthProbe> StartProbeAsync(
        string connectionString,
        Action<IServiceCollection> configureServices) =>
        await WorkerHealthProbe.StartAsync(connectionString, configureServices, Ct);

    private static void Publish(WorkerHealthProbe probe, StorageHealth health) =>
        probe.Services.GetRequiredService<IStorageHealthCache>().Publish(health);

    private static async Task<(HttpResponseMessage Response, JsonElement Report)> ReadReadyAsync(WorkerHealthProbe probe)
    {
        using var client = probe.CreateClient();

        var response = await client.GetAsync(WorkerHealthProbe.ReadyPath, Ct);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        return (response, report);
    }

    private static JsonElement StorageCheck(JsonElement report) =>
        report.GetProperty("checks")
            .EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == CheckName);
}
