using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using Npgsql;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Common.Errors;
using NightTales.Fetcher.Core.Fetching;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.Fetcher.Core.Research;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// 02.5.2-T2 fix pass for the lease store: a heartbeat never shortens a lease and stops at the total ceiling (QA-2,
/// SEC-1), a full platform answers without scanning (PERF-2), a contended platform lock answers 503 within its bound
/// (SEC-2.3), and the read members map a transient failure to <c>service_unavailable</c> (CR-1).
/// </summary>
[Trait("Category", "Integration")]
public sealed class FetchLeaseStoreHardeningTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The platform lease lock, "NTSFETCH" in ASCII (<c>FetchTaskLeaseStore.LeaseLockKey</c>).</summary>
    private const long LeaseLockKey = 0x4E54_5346_4554_4348;

    private static CancellationToken Ct => TestContextToken.Value;

    private FetchTaskSeed NewSeed()
    {
        var seed = new FetchTaskSeed(Database);
        seed.ResetTasks();

        return seed;
    }

    private ApiFactory NewFactory() =>
        new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start)).WithFetcherInternalApi();

    private static double Seconds(FetchTaskSeed seed, Guid task, string expression) =>
        double.Parse(
            seed.Text($"SELECT extract(epoch FROM {expression})::text FROM fetch_tasks WHERE id = '{task}'"),
            CultureInfo.InvariantCulture);

    [Fact]
    public async Task Heartbeat_NeverShortensALongerLease()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "hb-keep.example", leaseUntil: "now() + interval '85 seconds'");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), new { workerId = FetchTaskSeed.Worker });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        Seconds(seed, task.TaskId, "lease_until - now()").ShouldBeGreaterThan(80, "now()+60 s is earlier than the current end, so the end stays");
    }

    [Fact]
    public async Task Heartbeat_NearTheTotalCeiling_IsClampedToLeasedAtPlusMaxTotal()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "hb-clamp.example", leaseUntil: "now() + interval '5 seconds'");
        seed.Execute($"UPDATE fetch_tasks SET leased_at = now() - interval '280 seconds' WHERE id = '{task.TaskId}'");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), new { workerId = FetchTaskSeed.Worker });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var remaining = Seconds(seed, task.TaskId, "lease_until - now()");
        remaining.ShouldBeGreaterThan(15);
        remaining.ShouldBeLessThanOrEqualTo(21, "the end is leased_at + 300 s (about 20 s from now), not now() + 60 s");
        Seconds(seed, task.TaskId, "lease_until - leased_at").ShouldBe(300, 1, "the ceiling is exactly MaxLeaseTotalSeconds from leased_at");
    }

    [Fact]
    public async Task Heartbeat_PastTheTotalCeiling_Returns409_AndLeavesTheLeaseToTheSweeper()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "hb-over.example", leaseUntil: "now() + interval '30 seconds'");
        seed.Execute($"UPDATE fetch_tasks SET leased_at = now() - interval '301 seconds' WHERE id = '{task.TaskId}'");
        var before = seed.Text($"SELECT lease_until::text FROM fetch_tasks WHERE id = '{task.TaskId}'");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), new { workerId = FetchTaskSeed.Worker });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("fetch_lease_lost");
        seed.Text($"SELECT lease_until::text FROM fetch_tasks WHERE id = '{task.TaskId}'").ShouldBe(before, "the row is untouched");
        seed.TaskStatus(task.TaskId).ShouldBe("leased", "the sweeper, not the heartbeat, reclaims it");
    }

    [Fact]
    public async Task Lease_AtThePlatformCap_ReturnsEmpty_AndTouchesNothing()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        for (var i = 1; i <= 4; i++)
        {
            seed.AddLeased(world, $"full-{i}.example");
        }

        var queued = seed.AddQueued(world, "full-queued.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        var items = await FetchTaskSeed.LeaseItemsAsync(client);

        items.ShouldBeEmpty();
        seed.TaskStatus(queued.TaskId).ShouldBe("queued");
        seed.LeasedCount().ShouldBe(4);
    }

    [Fact]
    public async Task Lease_WhileTheLeaseLockIsHeld_Answers503WithinTheBound_AndLeasesNothing()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var queued = seed.AddQueued(world, "contended.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        await using var holder = new NpgsqlConnection(Database.ConnectionString);
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", holder, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", LeaseLockKey);
            await lockCommand.ExecuteNonQueryAsync(Ct);
        }

        var clock = Stopwatch.StartNew();
        using var contended = await FetchTaskSeed.LeaseAsync(client);
        clock.Stop();

        contended.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await contended.Content.ReadAsStringAsync(Ct));
        (await FetchTaskSeed.CodeOfAsync(contended)).ShouldBe("service_unavailable");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "the wait is bounded at about 2 s, not the command timeout");
        seed.TaskStatus(queued.TaskId).ShouldBe("queued");

        await transaction.RollbackAsync(Ct);
        var after = await FetchTaskSeed.LeaseItemsAsync(client);
        after.Length.ShouldBe(1, "once the lock is free the same call leases normally (T42 exactness is unchanged)");
    }

    [Fact]
    public async Task FindOwnerAsync_OnAnUnreachableDatabase_MapsToServiceUnavailable_AndDoesNotThrow()
    {
        var builder = new NpgsqlConnectionStringBuilder(Database.ConnectionString)
        {
            Host = "127.0.0.1",
            Port = 1,
            Timeout = 2,
            Pooling = false,
        };
        await using var context = TestDbContextFactory.CreateAppContext(builder.ConnectionString);
        var store = new FetchTaskLeaseStore(context, Options.Create(new FetcherOptions()));

        var result = await store.FindOwnerAsync(Guid.NewGuid(), Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task FindOwnerAsync_ForAnUnknownTask_IsNotFound_AndAKnownTaskYieldsItsOwner()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddQueued(world, "owner.example");
        await using var context = TestDbContextFactory.CreateAppContext(Database.ConnectionString);
        var store = new FetchTaskLeaseStore(context, Options.Create(new FetcherOptions()));

        var known = await store.FindOwnerAsync(task.TaskId, Ct);
        var unknown = await store.FindOwnerAsync(Guid.NewGuid(), Ct);

        known.IsError.ShouldBeFalse();
        known.Value.ShouldBe(world.UserId);
        unknown.IsError.ShouldBeTrue();
        unknown.FirstError.Code.ShouldBe(ErrorCodes.FetchTaskNotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(86_401)]
    public void Options_MaxLeaseTotalSeconds_MustExceedLeaseSecondsAndStayBounded(int value)
    {
        var options = new FetcherOptions { BlockedDomains = ["youtube.com"], MaxLeaseTotalSeconds = value };

        var result = new FetcherOptionsValidator(new NightTales.TestKit.Research.FakePublicSuffixList()).Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("MaxLeaseTotalSeconds");
    }
}
