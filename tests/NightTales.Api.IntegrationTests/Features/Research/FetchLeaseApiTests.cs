using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Storage;
using NightTales.Infrastructure.Storage;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// The lease, heartbeat and result routes of the fetcher's internal API against the real host and PostgreSQL 17
/// (02.5.2-T2 acceptance checks): the platform and per-domain caps, the lease columns, lease ownership and expiry, the
/// stored object and the <c>SourceFetched</c> outbox row, and the finder payload. Requests are sent as if they arrived on
/// port 8081 (<see cref="ApiFactory.InternalClient"/>); the blob store is the in-memory one, so the stored text is read back
/// byte for byte.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FetchLeaseApiTests(TestDatabase database) : IntegrationTest(database)
{
    private const string OtherWorker = "fetcher-2";

    private static CancellationToken Ct => TestContextToken.Value;

    private FetchTaskSeed NewSeed()
    {
        var seed = new FetchTaskSeed(Database);
        seed.ResetTasks();

        return seed;
    }

    private ApiFactory NewFactory(InMemoryBlobStore? blobs = null, int maxRequestsPerMinutePerWorker = 10_000)
    {
        var factory = new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start)).WithFetcherInternalApi(maxRequestsPerMinutePerWorker);
        if (blobs is not null)
        {
            factory.ConfigureServices(services =>
            {
                services.RemoveAll<IBlobStore>();
                services.AddSingleton<IBlobStore>(blobs);
            });
        }

        return factory;
    }

    // ------------------------------------------------------------------ lease: caps

    [Fact]
    public async Task Lease_SixQueued_ReturnsFour()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var tasks = Enumerable.Range(1, 6).Select(i => seed.AddQueued(world, $"six-{i}.example", ageSeconds: 100 - i)).ToList();
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        var items = await FetchTaskSeed.LeaseItemsAsync(client);

        items.Length.ShouldBe(4);
        items.Select(item => item.GetProperty("taskId").GetGuid())
            .ShouldBe(tasks.Take(4).Select(task => task.TaskId), ignoreOrder: true, "the four oldest eligible tasks");
        items.ShouldAllBe(item => item.GetProperty("kind").GetString() == "fetch_source" && item.GetProperty("attempt").GetInt32() == 1);
        seed.Count("SELECT count(*) FROM fetch_tasks WHERE status = 'leased' AND attempts = 1 AND leased_by = 'fetcher-1'").ShouldBe(4);
        seed.Count("SELECT count(*) FROM fetch_tasks WHERE status = 'queued' AND attempts = 0").ShouldBe(2);
    }

    [Fact]
    public async Task Lease_ReturnsTheWireShape_WithUrlAndUserAgent()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddQueued(world, "shape.example", path: "/a/b");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        var item = (await FetchTaskSeed.LeaseItemsAsync(client, max: 1)).ShouldHaveSingleItem();

        item.GetProperty("taskId").GetGuid().ShouldBe(task.TaskId);
        item.GetProperty("url").GetString().ShouldBe("https://shape.example/a/b");
        item.GetProperty("userAgent").GetString().ShouldNotBeNullOrWhiteSpace();
        item.TryGetProperty("finderRequest", out _).ShouldBeFalse("a fetch_source item has no finderRequest member");
        item.EnumerateObject().Select(property => property.Name).Order()
            .ShouldBe(["attempt", "kind", "leaseUntil", "taskId", "url", "userAgent"]);
    }

    [Fact]
    public async Task Lease_PlatformCap_ReturnsEmpty()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        for (var i = 1; i <= 4; i++)
        {
            seed.AddLeased(world, $"cap-{i}.example");
        }

        seed.AddQueued(world, "cap-queued.example");
        var before = seed.Text("SELECT string_agg(lease_until::text, ',' ORDER BY id) FROM fetch_tasks WHERE status = 'leased'");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        var items = await FetchTaskSeed.LeaseItemsAsync(client);

        items.ShouldBeEmpty();
        seed.LeasedCount().ShouldBe(4);
        seed.Text("SELECT string_agg(lease_until::text, ',' ORDER BY id) FROM fetch_tasks WHERE status = 'leased'")
            .ShouldBe(before, "the four leases keep the lease_until they had");
        seed.Count("SELECT count(*) FROM fetch_tasks WHERE status = 'queued'").ShouldBe(1);
    }

    [Fact]
    public async Task Lease_AskingForFourWhileThreeAreLeased_ReturnsOne()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        for (var i = 1; i <= 3; i++)
        {
            seed.AddLeased(world, $"clamp-{i}.example");
        }

        for (var i = 1; i <= 3; i++)
        {
            seed.AddQueued(world, $"clamp-q{i}.example");
        }

        using var factory = NewFactory();
        using var client = factory.InternalClient();

        (await FetchTaskSeed.LeaseItemsAsync(client, max: 4)).Length.ShouldBe(1, "min(max, cap - leased)");
        seed.LeasedCount().ShouldBe(4);
    }

    [Fact]
    public async Task Lease_SameDomain_ReturnsOne()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var first = seed.AddQueued(world, "same.example", ageSeconds: 100);
        var second = seed.AddQueued(world, "same.example", ageSeconds: 50);
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        var firstCall = await FetchTaskSeed.LeaseItemsAsync(client);
        var blockedCall = await FetchTaskSeed.LeaseItemsAsync(client);

        firstCall.ShouldHaveSingleItem().GetProperty("taskId").GetGuid().ShouldBe(first.TaskId);
        blockedCall.ShouldBeEmpty("the domain is busy while its lease is live");
        seed.TaskStatus(second.TaskId).ShouldBe("queued");

        // The lease ends (done here; the sweeper's requeue is covered by FetchLeaseSweeperTests): the other becomes leasable.
        seed.Execute($"UPDATE fetch_tasks SET status = 'done', leased_by = NULL, lease_until = NULL, finished_at = now() WHERE id = '{first.TaskId}'");
        (await FetchTaskSeed.LeaseItemsAsync(client)).ShouldHaveSingleItem().GetProperty("taskId").GetGuid().ShouldBe(second.TaskId);
    }

    [Fact]
    public async Task Lease_ThreeSameDomainInOneBatch_ReturnsExactlyOne()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var oldest = seed.AddQueued(world, "batch.example", ageSeconds: 300);
        var middle = seed.AddQueued(world, "batch.example", ageSeconds: 200);
        var newest = seed.AddQueued(world, "batch.example", ageSeconds: 100);
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        var items = await FetchTaskSeed.LeaseItemsAsync(client, max: 4);

        items.ShouldHaveSingleItem().GetProperty("taskId").GetGuid().ShouldBe(oldest.TaskId);
        seed.TaskStatus(middle.TaskId).ShouldBe("queued");
        seed.TaskStatus(newest.TaskId).ShouldBe("queued");
        seed.LeasedCount().ShouldBe(1);

        seed.Execute($"UPDATE fetch_tasks SET status = 'done', leased_by = NULL, lease_until = NULL, finished_at = now() WHERE id = '{oldest.TaskId}'");
        (await FetchTaskSeed.LeaseItemsAsync(client, max: 4)).ShouldHaveSingleItem().GetProperty("taskId").GetGuid()
            .ShouldBe(middle.TaskId, "the next call returns the next one of that domain, oldest first");
    }

    [Fact]
    public async Task Lease_MixedDomains_LeasesOnePerDomainAndTheRestOfTheBatch()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var a1 = seed.AddQueued(world, "a.example", ageSeconds: 500);
        seed.AddQueued(world, "a.example", ageSeconds: 400);
        var b1 = seed.AddQueued(world, "b.example", ageSeconds: 300);
        seed.AddQueued(world, "b.example", ageSeconds: 200);
        var c1 = seed.AddQueued(world, "c.example", ageSeconds: 100);
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        var items = await FetchTaskSeed.LeaseItemsAsync(client);

        items.Select(item => item.GetProperty("taskId").GetGuid()).ShouldBe([a1.TaskId, b1.TaskId, c1.TaskId], ignoreOrder: true);
        seed.Count("SELECT count(*) FROM (SELECT registrable_domain FROM fetch_tasks WHERE status = 'leased' GROUP BY 1 HAVING count(*) > 1) d").ShouldBe(0);
    }

    [Fact]
    public async Task Lease_TaskNotYetDue_IsSkipped()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var waiting = seed.AddQueued(world, "later.example");
        seed.Execute($"UPDATE fetch_tasks SET not_before = now() + interval '10 minutes' WHERE id = '{waiting.TaskId}'");
        var due = seed.AddQueued(world, "due.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        (await FetchTaskSeed.LeaseItemsAsync(client)).ShouldHaveSingleItem().GetProperty("taskId").GetGuid().ShouldBe(due.TaskId);
        seed.TaskStatus(waiting.TaskId).ShouldBe("queued");
    }

    [Fact]
    public async Task Lease_ConcurrentTwentyPairs_NeverExceedsCap()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        using var factory = NewFactory();
        using var first = factory.InternalClient();
        using var second = factory.InternalClient();
        var maxLeased = 0L;

        for (var iteration = 0; iteration < 20; iteration++)
        {
            seed.ResetTasks();
            for (var i = 1; i <= 3; i++)
            {
                seed.AddLeased(world, $"pair-held-{i}.example");
            }

            for (var i = 1; i <= 6; i++)
            {
                seed.AddQueued(world, $"pair-queued-{i}.example", ageSeconds: 100 - i);
            }

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new[] { LeaseWhenOpenAsync(first, "fetcher-1", gate.Task), LeaseWhenOpenAsync(second, OtherWorker, gate.Task) };
            gate.SetResult();
            var results = await Task.WhenAll(calls);

            var ids = results.SelectMany(items => items).Select(item => item.GetProperty("taskId").GetGuid()).ToList();
            ids.Distinct().Count().ShouldBe(ids.Count, $"iteration {iteration}: a task was leased to both callers");
            ids.Count.ShouldBe(1, $"iteration {iteration}: cap 4 - 3 already leased leaves exactly one lease for the pair");
            var leased = seed.LeasedCount();
            maxLeased = Math.Max(maxLeased, leased);
            leased.ShouldBeLessThanOrEqualTo(4, $"iteration {iteration}");
        }

        maxLeased.ShouldBe(4, "the pair reaches the cap exactly and never passes it");
    }

    [Fact]
    public async Task Lease_ConcurrentSameDomain_NeverLeasesTwo()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        using var factory = NewFactory();
        using var first = factory.InternalClient();
        using var second = factory.InternalClient();

        for (var iteration = 0; iteration < 10; iteration++)
        {
            seed.ResetTasks();
            seed.AddQueued(world, "race.example", ageSeconds: 100);
            seed.AddQueued(world, "race.example", ageSeconds: 50);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new[] { LeaseWhenOpenAsync(first, "fetcher-1", gate.Task), LeaseWhenOpenAsync(second, OtherWorker, gate.Task) };
            gate.SetResult();
            var results = await Task.WhenAll(calls);

            results.Sum(items => items.Length).ShouldBe(1, $"iteration {iteration}: one lease per registrable domain");
            seed.LeasedCount().ShouldBe(1);
        }
    }

    [Fact]
    public async Task Lease_SetsAttemptsAndLeaseUntilAndLeasedAt()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var fresh = seed.AddQueued(world, "attempts-1.example", ageSeconds: 100);
        var retried = seed.AddQueued(world, "attempts-2.example", ageSeconds: 90);
        seed.Execute($"UPDATE fetch_tasks SET attempts = 1 WHERE id = '{retried.TaskId}'");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        var items = await FetchTaskSeed.LeaseItemsAsync(client, "fetcher-7");

        items.Length.ShouldBe(2);
        seed.Text($"SELECT status || '/' || attempts::text || '/' || leased_by FROM fetch_tasks WHERE id = '{fresh.TaskId}'")
            .ShouldBe("leased/1/fetcher-7");
        seed.Text($"SELECT status || '/' || attempts::text || '/' || leased_by FROM fetch_tasks WHERE id = '{retried.TaskId}'")
            .ShouldBe("leased/2/fetcher-7", "attempts counts leases, so a requeued task is on its second");
        seed.Count(
                "SELECT count(*) FROM fetch_tasks WHERE status = 'leased' AND leased_at IS NOT NULL "
                + "AND leased_at BETWEEN now() - interval '1 minute' AND now() "
                + "AND lease_until = leased_at + interval '90 seconds'")
            .ShouldBe(2, "lease_until is leased_at + Fetcher:LeaseSeconds (90) from the one statement's now()");

        foreach (var item in items)
        {
            var taskId = item.GetProperty("taskId").GetGuid();
            var stored = seed.Text($"SELECT to_char(lease_until AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS') FROM fetch_tasks WHERE id = '{taskId}'");
            item.GetProperty("leaseUntil").GetDateTimeOffset().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                .ShouldBe(stored, "the response carries the stored lease end");
        }
    }

    [Fact]
    public async Task Lease_FindSources_ReturnsFinderRequestShape()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var run = await seed.AddFindSourcesRunAsync(world);
        var task = seed.AddFindSourcesTask(world, run);
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        var item = (await FetchTaskSeed.LeaseItemsAsync(client)).ShouldHaveSingleItem();

        item.GetProperty("taskId").GetGuid().ShouldBe(task);
        item.GetProperty("kind").GetString().ShouldBe("find_sources");
        item.TryGetProperty("url", out _).ShouldBeFalse("a find_sources item has no url");
        var finder = item.GetProperty("finderRequest");
        finder.EnumerateObject().Select(property => property.Name).Order()
            .ShouldBe(["allowList", "brief", "language", "maxCandidates", "seriesTitle", "topic"]);
        finder.GetProperty("topic").GetString().ShouldBe(seed.Text($"SELECT topic FROM series WHERE id = '{world.SeriesId}'"));
        finder.GetProperty("language").GetString().ShouldBe(seed.Text($"SELECT language FROM series WHERE id = '{world.SeriesId}'"));
        finder.GetProperty("seriesTitle").GetString().ShouldBe(seed.Text($"SELECT title FROM series WHERE id = '{world.SeriesId}'"));
        finder.GetProperty("brief").GetString().ShouldBe(seed.Text($"SELECT brief FROM episodes WHERE id = '{world.EpisodeId}'"));
        finder.GetProperty("brief").GetString().ShouldNotBeNullOrWhiteSpace();
        finder.GetProperty("allowList").GetArrayLength().ShouldBe(0, "no allow-list source exists before 02.7.2-T1");
        finder.GetProperty("maxCandidates").GetInt32().ShouldBe(10);
    }

    [Fact]
    public async Task Lease_TwoFindSourcesTasks_LeasesOnePerCall()
    {
        var seed = NewSeed();
        var first = await seed.SeedWorldAsync();
        var second = await seed.SeedWorldAsync();
        seed.AddFindSourcesTask(first, await seed.AddFindSourcesRunAsync(first));
        seed.AddFindSourcesTask(second, await seed.AddFindSourcesRunAsync(second));
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        (await FetchTaskSeed.LeaseItemsAsync(client)).Length.ShouldBe(1, "NULL domains share one partition: one browser task per call");
    }

    // ------------------------------------------------------------------ lease: request validation

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task Lease_MaxOutsideOneToFour_Returns400_AndLeasesNothing(int max)
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        seed.AddQueued(world, "max.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.LeaseAsync(client, max: max);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("validation_failed");
        seed.LeasedCount().ShouldBe(0);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"workerId\":\"fetcher-1\"}")]
    [InlineData("{\"workerId\":\"fetcher-1\",\"max\":4.5}")]
    [InlineData("{\"workerId\":\"fetcher-1\",\"max\":99999999999}")]
    [InlineData("{\"workerId\":null,\"max\":4}")]
    [InlineData("{\"workerId\":\"\",\"max\":4}")]
    [InlineData("{\"workerId\":12,\"max\":4}")]
    [InlineData("{\"workerId\":\"fetcher\\u0000-1\",\"max\":4}")]
    [InlineData("{\"workerId\":\"fetcher\\ud800-1\",\"max\":4}")]
    [InlineData("{\"workerId\":\"fetcher 1\",\"max\":4}")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task Lease_MalformedBody_Returns400_NeverA500(string json)
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        seed.AddQueued(world, "malformed.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Prefix + "/lease", json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("validation_failed");
        seed.LeasedCount().ShouldBe(0);
    }

    [Fact]
    public async Task Lease_WorkerIdOverSixtyFourCharacters_Returns400_AndSixtyFourIsAccepted()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        seed.AddQueued(world, "workerlen.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var tooLong = await FetchTaskSeed.LeaseAsync(client, new string('w', 65), 1);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        seed.LeasedCount().ShouldBe(0);

        using var longest = await FetchTaskSeed.LeaseAsync(client, new string('w', 64), 1);
        longest.StatusCode.ShouldBe(HttpStatusCode.OK);
        seed.Text("SELECT leased_by FROM fetch_tasks WHERE status = 'leased'").ShouldBe(new string('w', 64));
    }

    [Fact]
    public async Task Lease_BodyOverOneMebibyte_Returns413_AndLeasesNothing()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        seed.AddQueued(world, "bigbody.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();
        var json = "{\"workerId\":\"fetcher-1\",\"max\":4,\"pad\":\"" + new string('x', 1_048_576) + "\"}";

        using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Prefix + "/lease", json);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        seed.LeasedCount().ShouldBe(0);
    }

    [Fact]
    public async Task Lease_OverTheWorkerBudget_Returns429()
    {
        var seed = NewSeed();
        await seed.SeedWorldAsync();
        using var factory = NewFactory(maxRequestsPerMinutePerWorker: 3);
        using var client = factory.InternalClient();

        for (var i = 0; i < 3; i++)
        {
            using var allowed = await FetchTaskSeed.LeaseAsync(client);
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var limited = await FetchTaskSeed.LeaseAsync(client);
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await FetchTaskSeed.CodeOfAsync(limited)).ShouldBe("rate_limited");

        using var otherWorker = await FetchTaskSeed.LeaseAsync(client, OtherWorker);
        otherWorker.StatusCode.ShouldBe(HttpStatusCode.OK, "the budget is per worker id");
    }

    // ------------------------------------------------------------------ heartbeat

    [Fact]
    public async Task Heartbeat_OwnLease_Extends()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "hb.example", leaseUntil: "now() + interval '10 seconds'");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), new { workerId = FetchTaskSeed.Worker });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await FetchTaskSeed.ReadJsonAsync(response);
        body.EnumerateObject().Select(property => property.Name).ShouldBe(["leaseUntil"]);
        seed.Count(
                $"SELECT count(*) FROM fetch_tasks WHERE id = '{task.TaskId}' AND status = 'leased' "
                + "AND lease_until > now() + interval '55 seconds' AND lease_until <= now() + interval '60 seconds'")
            .ShouldBe(1, "a heartbeat moves lease_until to now() + Fetcher:HeartbeatSeconds (60)");
        var stored = seed.Text($"SELECT to_char(lease_until AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS') FROM fetch_tasks WHERE id = '{task.TaskId}'");
        body.GetProperty("leaseUntil").GetDateTimeOffset().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(stored);
    }

    [Fact]
    public async Task Heartbeat_OtherWorker_Returns409()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "hb-other.example", leaseUntil: "now() + interval '10 seconds'");
        var before = seed.Text($"SELECT lease_until::text FROM fetch_tasks WHERE id = '{task.TaskId}'");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), new { workerId = OtherWorker });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("fetch_lease_lost");
        seed.Text($"SELECT lease_until::text FROM fetch_tasks WHERE id = '{task.TaskId}'").ShouldBe(before, "a foreign heartbeat changes nothing");
    }

    [Fact]
    public async Task Heartbeat_Expired_Returns409()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "hb-expired.example", leaseUntil: "now() - interval '1 second'");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), new { workerId = FetchTaskSeed.Worker });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("fetch_lease_lost");
        seed.Count($"SELECT count(*) FROM fetch_tasks WHERE id = '{task.TaskId}' AND lease_until < now()").ShouldBe(1, "an expired lease is not revived");
    }

    [Fact]
    public async Task Heartbeat_QueuedOrDoneTask_Returns409_AndUnknownTask_Returns404()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var queued = seed.AddQueued(world, "hb-queued.example");
        var done = seed.AddLeased(world, "hb-done.example");
        seed.Execute($"UPDATE fetch_tasks SET status = 'done', leased_by = NULL, lease_until = NULL, finished_at = now() WHERE id = '{done.TaskId}'");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        foreach (var task in new[] { queued, done })
        {
            using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), new { workerId = FetchTaskSeed.Worker });
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using var unknown = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(Guid.NewGuid(), "heartbeat"), new { workerId = FetchTaskSeed.Worker });
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await FetchTaskSeed.CodeOfAsync(unknown)).ShouldBe("fetch_task_not_found");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"workerId\":\"\"}")]
    [InlineData("{\"workerId\":\"bad worker\"}")]
    [InlineData("{\"workerId\":7}")]
    [InlineData("nope")]
    public async Task Heartbeat_BadBody_Returns400(string json)
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "hb-bad.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("validation_failed");
    }

    // ------------------------------------------------------------------ result: fetch_source

    [Fact]
    public async Task Result_Done_StoresObjectAndMarksSource()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "store.example");
        var blobs = new InMemoryBlobStore();
        const string Text = "Một bài báo thử nghiệm.\nDòng thứ hai với emoji 😀 và tab\tgiữa.";
        var sha = FetchTaskSeed.Sha256(Text);
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody(Text));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var ack = await FetchTaskSeed.ReadJsonAsync(response);
        ack.GetProperty("taskId").GetGuid().ShouldBe(task.TaskId);
        ack.GetProperty("status").GetString().ShouldBe("done");

        var key = $"research/{world.UserId}/{world.EpisodeId}/{task.SourceId}/{sha}.txt";
        seed.Text($"SELECT status || '|' || content_sha256 || '|' || text_chars::text || '|' || extracted_text_key FROM story_sources WHERE id = '{task.SourceId}'")
            .ShouldBe($"fetched|{sha}|{Text.Length}|{key}");
        seed.Text($"SELECT coalesce(error_code, '-') || '|' || (fetched_at IS NOT NULL)::text || '|' || final_url || '|' || content_type || '|' || coalesce(http_status::text, '-') FROM story_sources WHERE id = '{task.SourceId}'")
            .ShouldBe("-|true|https://news.example.com/final-article|text/html|200");
        seed.Text($"SELECT title FROM story_sources WHERE id = '{task.SourceId}'").ShouldBe("Tiêu đề thử");
        seed.Text($"SELECT status || '|' || (finished_at IS NOT NULL)::text || '|' || coalesce(error_code, '-') FROM fetch_tasks WHERE id = '{task.TaskId}'")
            .ShouldBe("done|true|-");

        var bucket = factory.Services.GetRequiredService<StorageSettings>().Buckets.Media;
        blobs.Contains(bucket, key).ShouldBeTrue("the object lives in the media bucket under research/");
        var opened = await blobs.OpenReadAsync(bucket, key, Ct);
        opened.IsError.ShouldBeFalse();
        opened.Value.ContentType.ShouldBe(ExtractedTextStore.ContentType);
        using var reader = new StreamReader(opened.Value.Content, Encoding.UTF8);
        (await reader.ReadToEndAsync(Ct)).ShouldBe(Text, "the exact text, byte for byte");
        FetchTaskSeed.ResearchPuts(blobs).ShouldBe(1);
    }

    [Fact]
    public async Task Result_Done_AtTheTextCap_IsStored_AndSurrogatePairsCountAsTwoChars()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "boundary.example");
        var blobs = new InMemoryBlobStore();
        var text = string.Concat(Enumerable.Repeat("😀", 150_000));
        text.Length.ShouldBe(300_000);
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        // Raw UTF-8 on the wire: the default serializer would escape every emoji to 12 bytes and exceed the 1 MiB body cap first.
        var json = "{\"workerId\":\"fetcher-1\",\"status\":\"done\",\"finalUrl\":\"https://news.example.com/a\",\"contentType\":\"text/html\","
            + "\"text\":\"" + text + "\",\"textSha256\":\"" + FetchTaskSeed.Sha256(text) + "\"}";
        using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), json);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        seed.Text($"SELECT text_chars::text FROM story_sources WHERE id = '{task.SourceId}'").ShouldBe("300000");
    }

    [Fact]
    public async Task Result_Sha256Mismatch_DoesNotWriteObject()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "tamper.example");
        var blobs = new InMemoryBlobStore();
        var body = FetchTaskSeed.DoneBody();
        body["textSha256"] = FetchTaskSeed.Sha256("a different text");
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("source_no_text");
        FetchTaskSeed.ResearchCalls(blobs).ShouldBeEmpty("no object was written, not even a probe");
        seed.SourceStatus(task.SourceId).ShouldBe("pending");
        seed.TaskStatus(task.TaskId).ShouldBe("leased");
        seed.Count($"SELECT count(*) FROM story_sources WHERE id = '{task.SourceId}' AND content_sha256 IS NULL AND extracted_text_key IS NULL").ShouldBe(1);
        seed.Count("SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '" + world.UserId + "'").ShouldBe(0);
    }

    [Fact]
    public async Task Result_UppercaseHexSha_IsAccepted_AndStoredLowercase()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "upper.example");
        var body = FetchTaskSeed.DoneBody();
        body["textSha256"] = FetchTaskSeed.Sha256((string)body["text"]!).ToUpperInvariant();
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        seed.Count($"SELECT count(*) FROM story_sources WHERE id = '{task.SourceId}' AND content_sha256 = lower(content_sha256) AND extracted_text_key LIKE '%/' || content_sha256 || '.txt'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Result_LeaseLost_Returns409()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var foreign = seed.AddLeased(world, "lost-foreign.example", workerId: OtherWorker);
        var expired = seed.AddLeased(world, "lost-expired.example", leaseUntil: "now() - interval '1 second'");
        var requeued = seed.AddQueued(world, "lost-requeued.example");
        var blobs = new InMemoryBlobStore();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        foreach (var task in new[] { foreign, expired, requeued })
        {
            using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody());

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, task.Domain);
            (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("fetch_lease_lost", task.Domain);
            seed.SourceStatus(task.SourceId).ShouldBe("pending", task.Domain);
        }

        FetchTaskSeed.ResearchCalls(blobs).ShouldBeEmpty("a worker without the lease writes no object");
        seed.TaskStatus(foreign.TaskId).ShouldBe("leased");
    }

    [Fact]
    public async Task Result_LeaseLostIsCheckedBeforeTheBody()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var foreign = seed.AddLeased(world, "order.example", workerId: OtherWorker);
        var invalid = FetchTaskSeed.DoneBody();
        invalid.Remove("text");
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(foreign.TaskId, "result"), invalid);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, "ownership is item 1 of the validation order");
    }

    [Fact]
    public async Task Result_DuplicateAfterSuccess_Returns409_AndKeepsTheFirstResult()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "dup.example");
        var blobs = new InMemoryBlobStore();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();
        var route = FetchTaskSeed.Route(task.TaskId, "result");

        using var first = await FetchTaskSeed.PostAsync(client, route, FetchTaskSeed.DoneBody("first text"));
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var shaAfterFirst = seed.Text($"SELECT content_sha256 FROM story_sources WHERE id = '{task.SourceId}'");

        using var second = await FetchTaskSeed.PostAsync(client, route, FetchTaskSeed.DoneBody("second text"));

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await FetchTaskSeed.CodeOfAsync(second)).ShouldBe("fetch_lease_lost");
        seed.Text($"SELECT content_sha256 FROM story_sources WHERE id = '{task.SourceId}'").ShouldBe(shaAfterFirst);
        FetchTaskSeed.ResearchPuts(blobs).ShouldBe(1);
        seed.Count($"SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}'").ShouldBe(1);
    }

    [Fact]
    public async Task Result_SourceDeletedWhileLeased_Returns404()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "deleted.example");
        seed.Execute($"DELETE FROM story_sources WHERE id = '{task.SourceId}'");
        var blobs = new InMemoryBlobStore();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        seed.Count($"SELECT count(*) FROM fetch_tasks WHERE id = '{task.TaskId}'").ShouldBe(0, "the FK cascade removed the task");
        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("fetch_task_not_found");
        FetchTaskSeed.ResearchCalls(blobs).ShouldBeEmpty();
    }

    [Fact]
    public async Task Result_UnknownTask_Returns404()
    {
        NewSeed();
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(Guid.NewGuid(), "result"), FetchTaskSeed.DoneBody());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("fetch_task_not_found");
    }

    [Fact]
    public async Task Result_RaisesSourceFetchedOutboxRow()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var done = seed.AddLeased(world, "outbox-done.example");
        var failed = seed.AddLeased(world, "outbox-failed.example");
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();
        seed.Count($"SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}'").ShouldBe(0);

        using var ok = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(done.TaskId, "result"), FetchTaskSeed.DoneBody());
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);

        seed.Count($"SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}'").ShouldBe(1);
        var keys = SqlKeys(seed, world.UserId);
        keys.ShouldBe(["episodeid", "occurredat", "sourceid", "userid"], "ids and a timestamp only (D19)");
        var payload = seed.Text($"SELECT payload::text FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}'");
        payload.ShouldContain(done.SourceId.ToString());
        payload.ShouldContain(world.EpisodeId.ToString());
        payload.ShouldNotContain("news.example.com");
        payload.ShouldNotContain("Một bài báo");

        using var bad = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(failed.TaskId, "result"), FetchTaskSeed.FailedBody("source_unreachable"));
        bad.StatusCode.ShouldBe(HttpStatusCode.OK);
        seed.Count($"SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(2, "a failed source changes the episode's source list too, so the UI hears about it");
    }

    [Fact]
    public async Task Result_Failed_MarksSourceFailedWithCode()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "robots.example");
        var blobs = new InMemoryBlobStore();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.FailedBody("source_blocked_by_robots"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await FetchTaskSeed.ReadJsonAsync(response)).GetProperty("status").GetString().ShouldBe("done");
        seed.Text($"SELECT status || '|' || error_code || '|' || http_status::text || '|' || (fetched_at IS NOT NULL)::text FROM story_sources WHERE id = '{task.SourceId}'")
            .ShouldBe("failed|source_blocked_by_robots|403|true");
        seed.Text($"SELECT status || '|' || error_code || '|' || (finished_at IS NOT NULL)::text FROM fetch_tasks WHERE id = '{task.TaskId}'")
            .ShouldBe("failed|source_blocked_by_robots|true");
        seed.Count($"SELECT count(*) FROM story_sources WHERE id = '{task.SourceId}' AND extracted_text_key IS NULL AND content_sha256 IS NULL").ShouldBe(1);
        FetchTaskSeed.ResearchCalls(blobs).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("source_too_large", "source_too_large")]
    [InlineData("source_unsupported_type", "source_unsupported_type")]
    [InlineData("source_no_text", "source_no_text")]
    [InlineData("source_domain_not_allowed", "source_domain_not_allowed")]
    [InlineData("source_unreachable", "source_unreachable")]
    [InlineData("find_sources_generation_failed", "source_unreachable")]
    [InlineData("fetch_lease_lost", "source_unreachable")]
    [InlineData("totally_made_up", "source_unreachable")]
    [InlineData("", "source_unreachable")]
    [InlineData(null, "source_unreachable")]
    public async Task Result_Failed_StoresOnlyAKnownJobCode(string? reported, string stored)
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "codes.example");
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.FailedBody(reported));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        seed.Text($"SELECT error_code FROM story_sources WHERE id = '{task.SourceId}'").ShouldBe(stored);
        seed.Text($"SELECT error_code FROM fetch_tasks WHERE id = '{task.TaskId}'").ShouldBe(stored);
    }

    [Fact]
    public async Task Result_SourceNoLongerPending_Returns409_AndWritesNothing()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "settled.example");
        seed.Execute($"UPDATE story_sources SET status = 'rejected' WHERE id = '{task.SourceId}'");
        var blobs = new InMemoryBlobStore();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        FetchTaskSeed.ResearchPuts(blobs).ShouldBe(0);
        seed.SourceStatus(task.SourceId).ShouldBe("rejected");
    }

    [Fact]
    public async Task Result_ObjectStoreUnavailable_Returns503_LeavesSourcePendingAndTaskLeased()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "storedown.example");
        var blobs = new InMemoryBlobStore { AsyncFailure = ErrorOr.Error.Failure("service_unavailable", "storage down") };
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody());

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        seed.SourceStatus(task.SourceId).ShouldBe("pending");
        seed.TaskStatus(task.TaskId).ShouldBe("leased");
        seed.Count($"SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}'").ShouldBe(0);
    }

    // ------------------------------------------------------------------ result: find_sources

    [Fact]
    public async Task Result_FindSources_ValidCandidates_Returns400KindUnsupported_AndLeavesTaskLeased()
    {
        // [DECISION 02.5.2-T2] replaces Result_FindSources_StoresCandidatesOnGenerationRun: source_candidates ships with
        // 02.7.2-T3, so the result is validated and then refused, and the task is left to expire.
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var run = await seed.AddFindSourcesRunAsync(world);
        var task = seed.AddFindSourcesTask(world, run, leasedBy: FetchTaskSeed.Worker);
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();
        var body = new Dictionary<string, object?>
        {
            ["workerId"] = FetchTaskSeed.Worker,
            ["status"] = "done",
            ["candidates"] = new object[]
            {
                new { url = "https://vnexpress.net/a", title = "A", site = "VnExpress" },
                new { url = "not a url", title = "dropped" },
            },
        };

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task, "result"), body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("fetch_task_kind_unsupported");
        seed.TaskStatus(task).ShouldBe("leased");
        seed.Count("SELECT count(*) FROM generation_runs WHERE id = '" + run + "' AND status = 'queued'").ShouldBe(1, "nothing is stored on the run");
    }

    [Fact]
    public async Task Result_FindSources_OverFiftyCandidates_IsRejectedByValidation_BeforeAnyConsumer()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddFindSourcesTask(world, await seed.AddFindSourcesRunAsync(world), leasedBy: FetchTaskSeed.Worker);
        using var factory = NewFactory();
        using var client = factory.InternalClient();
        var body = new Dictionary<string, object?>
        {
            ["workerId"] = FetchTaskSeed.Worker,
            ["status"] = "done",
            ["candidates"] = Enumerable.Range(0, 51).Select(i => new { url = $"https://vnexpress.net/{i}" }).ToArray(),
        };

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task, "result"), body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("validation_failed", "the 50-entry cap, not the unsupported-kind refusal");
    }

    [Fact]
    public async Task Result_FindSources_MissingCandidates_Returns400Validation()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddFindSourcesTask(world, await seed.AddFindSourcesRunAsync(world), leasedBy: FetchTaskSeed.Worker);
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task, "result"), new { workerId = FetchTaskSeed.Worker, status = "done" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("validation_failed");
    }

    // ------------------------------------------------------------------ logging (D19)

    [Fact]
    public async Task Privacy_NoAuthorizationUrlPathOrTextInLogs_AcrossLeaseHeartbeatAndResults()
    {
        const string SecretPath = "qa-secret-path-5e1f90";
        const string SecretText = "qa-secret-text-4d2b81";
        const string SecretTitle = "qa-secret-title-9a77c3";
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var leasedTask = seed.AddQueued(world, "privacy-1.example", path: "/" + SecretPath);
        var doneTask = seed.AddLeased(world, "privacy-2.example", path: "/" + SecretPath + "-2");
        var failedTask = seed.AddLeased(world, "privacy-3.example", path: "/" + SecretPath + "-3");
        var badTask = seed.AddLeased(world, "privacy-4.example", path: "/" + SecretPath + "-4");
        var logs = new CapturingLoggerProvider();
        using var factory = NewFactory(new InMemoryBlobStore()).WithLoggerProvider(logs);
        using var client = factory.InternalClient();
        using var anonymous = factory.InternalClient(token: null);
        using var wrong = factory.InternalClient(token: "wrong-" + FetchTaskSeed.Sha256("x"));

        (await FetchTaskSeed.LeaseItemsAsync(client, max: 1)).Length.ShouldBe(1);
        (await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(leasedTask.TaskId, "heartbeat"), new { workerId = FetchTaskSeed.Worker })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var done = FetchTaskSeed.DoneBody(SecretText, finalUrl: $"https://news.example.com/{SecretPath}?token={SecretPath}");
        done["title"] = SecretTitle;
        (await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(doneTask.TaskId, "result"), done)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(failedTask.TaskId, "result"), FetchTaskSeed.FailedBody("source_unreachable"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var tampered = FetchTaskSeed.DoneBody(SecretText, finalUrl: $"https://privacy-4.example/{SecretPath}");
        tampered["textSha256"] = FetchTaskSeed.Sha256("other");
        (await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(badTask.TaskId, "result"), tampered)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FetchTaskSeed.LeaseAsync(anonymous)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await FetchTaskSeed.LeaseAsync(wrong)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Prefix + "/lease", "{\"workerId\":\"" + SecretText + " bad\",\"max\":4}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var lines = logs.Text;
        lines.ShouldNotBeEmpty("the host logs requests, so the capture must have seen something");
        foreach (var secret in new[] { SecretPath, SecretText, SecretTitle, ApiFactory.FetcherToken, "Bearer ", "wrong-" })
        {
            lines.ShouldNotContain(secret, Case.Insensitive, $"{secret} leaked into a log line");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static List<string> SqlKeys(FetchTaskSeed seed, Guid userId) =>
    [
        .. seed.Text(
                "SELECT string_agg(lower(k), ',' ORDER BY lower(k)) FROM outbox_messages m, jsonb_object_keys(m.payload) k "
                + $"WHERE m.type = 'source_fetched.v1' AND m.user_id = '{userId}' AND m.created_at = (SELECT min(created_at) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{userId}')")
            .Split(',', StringSplitOptions.RemoveEmptyEntries),
    ];

    private static async Task<JsonElement[]> LeaseWhenOpenAsync(HttpClient client, string workerId, Task gate)
    {
        await gate;

        return await FetchTaskSeed.LeaseItemsAsync(client, workerId);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public string Text
        {
            get
            {
                lock (_lines)
                {
                    return string.Join('\n', _lines);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

        public void Dispose()
        {
        }

        private void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }

        private sealed class Capture(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(' ', pairs.Select(pair => pair.Key + "=" + pair.Value))
                    : string.Empty;
                owner.Add($"{category} {logLevel} {formatter(state, exception)} | {properties} | {exception}");
            }
        }
    }
}
