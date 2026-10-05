using System.Text;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Features.Research.Jobs;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Fetcher;
using NightTales.Fetcher.InternalApi;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Fetching;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// The fetcher host (02.5.2-T3) against the real internal API (02.5.2-T2) and a real PostgreSQL 17 database: the real
/// <c>FetcherLoop</c>, <c>FetcherApiClient</c> and <c>SafeHttpSourceFetcher</c> run in-process through <c>AddFetcherHost</c>.
/// The seams are the ones D20 allows: the API client's primary handler is the TestServer handler of the internal port
/// (see <see cref="FetcherE2EHarness"/>), and DNS, the address validator, the connector endpoint and the rate limiter are
/// swapped for loopback fakes. Every wait is bounded polling; the host is stopped in <c>finally</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FetcherHostEndToEndTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContextToken.Value;

    private static string ArticleHtml()
    {
        var paragraphs = Enumerable.Range(1, 9).Select(i =>
            $"<p>Paragraph {i} of the quokka chronicle describes how the travelling surveyors crossed the salt flats at dawn, "
            + "counted the old stone markers one by one, wrote their measurements in a worn leather book, and argued quietly "
            + "about whether the river had moved since the last map was drawn by their predecessors.</p>");

        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Zebra Quokka Chronicle</title></head><body>"
            + "<nav><a href=\"/\">Home</a></nav><article><h1>Zebra Quokka Chronicle</h1>"
            + string.Concat(paragraphs)
            + "</article><footer>Copyright the surveyors</footer></body></html>";
    }

    private static TestResponse Article() => TestResponse.Text(200, "text/html; charset=utf-8", ArticleHtml());

    private static TestResponse Page(TestRequest request, TimeSpan? delay = null) =>
        request.Target.StartsWith("/story/", StringComparison.Ordinal)
            ? Article() with { Delay = delay ?? TimeSpan.Zero }
            : new TestResponse(404);

    /// <summary>
    /// Proves: lease -> fetch -> extract -> result POST on the real stack; the source row is <c>fetched</c> with hash,
    /// length and blob key; the task is <c>done</c>; D19 (no URL path/query or title in any log line).
    /// </summary>
    [Fact]
    public async Task EndToEnd_LeaseFetchExtractPost_SourceFetched()
    {
        await using var h = new FetcherE2EHarness(Database, request => Page(request));
        var world = await h.Seed.SeedWorldAsync();
        var task = h.QueueHttpTask(world, "alpha-e2e.com", "/story/zebra-quokka-chronicle?ref=abc");

        await h.StartFetcherAsync(Ct);
        try
        {
            (await FetcherE2EHarness.WaitAsync(() => h.Seed.SourceStatus(task.SourceId) == "fetched", Bound, Ct))
                .ShouldBeTrue($"source stayed {h.Seed.SourceStatus(task.SourceId)}; logs: {string.Join(" // ", h.Logs.Lines.Where(l => !l.StartsWith("Trace", StringComparison.Ordinal)).Take(20))}");
            (await h.WaitForPostedResultAsync(task.TaskId, Bound, Ct))
                .ShouldBeTrue("the result POST was not acknowledged by the fetcher");
        }
        finally
        {
            await h.StopFetcherAsync();
        }

        h.Seed.Count(
                "SELECT count(*) FROM story_sources "
                + $"WHERE id = '{task.SourceId}' AND content_sha256 ~ '^[0-9a-f]{{64}}$' AND text_chars >= 1275 AND extracted_text_key IS NOT NULL")
            .ShouldBe(1);
        h.Seed.TaskStatus(task.TaskId).ShouldBe("done");
        h.Seed.Count($"SELECT count(*) FROM fetch_tasks WHERE id = '{task.TaskId}' AND attempts = 1 AND finished_at IS NOT NULL")
            .ShouldBe(1);
        h.Traffic.Results.ShouldHaveSingleItem().ShouldBe(new ResultPostRecord(task.TaskId, 200, string.Empty));
        h.Logs.Lines.ShouldContain(line => line.Contains(task.TaskId.ToString(), StringComparison.Ordinal) && line.Contains("(posted)", StringComparison.Ordinal));
        foreach (var forbidden in new[] { "zebra-quokka-chronicle", "ref=abc", "Zebra Quokka", "Quokka" })
        {
            h.Logs.Lines.ShouldNotContain(line => line.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"D19: a log line leaked '{forbidden}'");
        }
    }

    /// <summary>
    /// Proves: a redirect to a domain on <c>Fetcher:BlockedDomains</c> fails the fetch with D8 <c>source_domain_not_allowed</c>
    /// and the Api stores it on the source and the task (the page server never sees the blocked host).
    /// </summary>
    [Fact]
    public async Task EndToEnd_BlockedDomain_ResultIsFailedWithCode()
    {
        await using var h = new FetcherE2EHarness(Database, request => request.Target.StartsWith("/start", StringComparison.Ordinal)
            ? TestResponse.Redirect("http://youtube.com/watch?v=1")
            : new TestResponse(404));
        var world = await h.Seed.SeedWorldAsync();
        var task = h.QueueHttpTask(world, "beta-e2e.com", "/start");

        await h.StartFetcherAsync(Ct);
        try
        {
            (await FetcherE2EHarness.WaitAsync(() => h.Seed.SourceStatus(task.SourceId) == "failed", Bound, Ct))
                .ShouldBeTrue($"source stayed {h.Seed.SourceStatus(task.SourceId)}");
        }
        finally
        {
            await h.StopFetcherAsync();
        }

        h.Seed.Text($"SELECT error_code FROM story_sources WHERE id = '{task.SourceId}'").ShouldBe("source_domain_not_allowed");
        h.Seed.TaskStatus(task.TaskId).ShouldBe("failed");
        h.Seed.Text($"SELECT error_code FROM fetch_tasks WHERE id = '{task.TaskId}'").ShouldBe("source_domain_not_allowed");
        h.Pages.Requests.ShouldNotContain(request => request.Host.Contains("youtube", StringComparison.Ordinal));
        h.Dns.Lookups.ShouldNotContain("youtube.com", "the blocklist is checked before any DNS lookup of the redirect target");
    }

    /// <summary>
    /// Proves: an Api that is unreachable while the result is posted loses nothing. Mechanism: a fault-injecting handler in
    /// front of the in-process API makes the three result POST attempts fail in transport (the client gives up and the lease is
    /// left to expire); the lease is then expired with SQL and <c>FetchLeaseSweeperJob</c> (through <c>WorkerHost</c>) requeues
    /// it; the same host leases it again as attempt 2 and completes it. Not an actual Api restart: the TestServer keeps its
    /// state, which is what a restart preserves too (all state is in PostgreSQL).
    /// </summary>
    [Fact]
    public async Task EndToEnd_ApiRestartMidTask_LeaseRequeuedAndCompleted()
    {
        await using var h = new FetcherE2EHarness(Database, request => Page(request));
        var world = await h.Seed.SeedWorldAsync();
        var task = h.QueueHttpTask(world, "gamma-e2e.com", "/story/restart");
        h.Traffic.FailResults = true;

        await h.StartFetcherAsync(Ct);
        try
        {
            (await FetcherE2EHarness.WaitAsync(
                    () => h.Logs.Lines.Any(line => line.Contains("(gave_up)", StringComparison.Ordinal)),
                    Bound,
                    Ct))
                .ShouldBeTrue("the client never gave up on the result POST");
            h.Traffic.SimulatedFailures.ShouldBe(3, "three result POST attempts in all");
            h.Seed.TaskStatus(task.TaskId).ShouldBe("leased");
            h.Seed.SourceStatus(task.SourceId).ShouldBe("pending");

            h.Seed.Execute($"UPDATE fetch_tasks SET lease_until = now() - interval '1 minute' WHERE id = '{task.TaskId}'");
            await using (var worker = new WorkerHost(Database, new FakeClock(DateTimeOffset.UtcNow)))
            {
                await worker.StartAsync(Ct);
                using var scope = worker.CreateScope();
                var summary = await scope.ServiceProvider.GetRequiredService<FetchLeaseSweeperJob>().SweepAsync(Ct);
                summary.ShouldBe(new FetchLeaseSweepSummary(Requeued: 1, Failed: 0, SourcesFailed: 0));
            }

            h.Seed.TaskStatus(task.TaskId).ShouldBe("queued");
            h.Traffic.FailResults = false;
            h.Seed.Execute($"UPDATE fetch_tasks SET not_before = now() - interval '1 second' WHERE id = '{task.TaskId}'");

            (await FetcherE2EHarness.WaitAsync(() => h.Seed.SourceStatus(task.SourceId) == "fetched", Bound, Ct))
                .ShouldBeTrue($"attempt 2 did not complete; source {h.Seed.SourceStatus(task.SourceId)}");
            (await h.WaitForPostedResultAsync(task.TaskId, Bound, Ct))
                .ShouldBeTrue("attempt 2's result POST was not acknowledged by the fetcher");
        }
        finally
        {
            await h.StopFetcherAsync();
        }

        h.Seed.TaskStatus(task.TaskId).ShouldBe("done");
        h.Seed.Count($"SELECT count(*) FROM fetch_tasks WHERE id = '{task.TaskId}' AND attempts = 2").ShouldBe(1);
        h.Traffic.Results.ShouldHaveSingleItem().ShouldBe(new ResultPostRecord(task.TaskId, 200, string.Empty));
    }

    /// <summary>
    /// Proves the as-built find_sources contract (02.5.2-T2 hand-off): with a runner registered the host POSTs its candidates and
    /// the Api answers 400 <c>fetch_task_kind_unsupported</c>, leaving the task <c>leased</c>.
    /// HAND-OVER to 02.7.2-T3: when <c>source_candidates</c> ships, this test must change to expect 200 and a stored candidate
    /// list; the name is kept because the task file names it.
    /// </summary>
    [Fact]
    public async Task EndToEnd_FindSourcesStub_ReturnsCandidates()
    {
        var finder = new StubFinder();
        await using var h = new FetcherE2EHarness(Database, request => Page(request), services => services.AddSingleton<ISourceFinderRunner>(finder));
        var world = await h.Seed.SeedWorldAsync();
        var run = await h.Seed.AddFindSourcesRunAsync(world);
        var taskId = h.Seed.AddFindSourcesTask(world, run);

        await h.StartFetcherAsync(Ct);
        try
        {
            (await FetcherE2EHarness.WaitAsync(() => h.Traffic.Results.Count > 0, Bound, Ct)).ShouldBeTrue("no result was posted");
        }
        finally
        {
            await h.StopFetcherAsync();
        }

        finder.Calls.ShouldBe(1);
        h.Traffic.Results.ShouldHaveSingleItem().ShouldBe(new ResultPostRecord(taskId, 400, "fetch_task_kind_unsupported"));
        h.Seed.TaskStatus(taskId).ShouldBe("leased");
    }

    /// <summary>
    /// Proves: with no <c>ISourceFinderRunner</c> a leased find_sources task is abandoned without a POST (it stays leased), and
    /// the loop keeps running: a fetch_source task queued alongside is still fetched.
    /// </summary>
    [Fact]
    public async Task EndToEnd_FindSourcesWithoutRunner_NoPostAndLoopContinues()
    {
        await using var h = new FetcherE2EHarness(Database, request => Page(request));
        var world = await h.Seed.SeedWorldAsync();
        var run = await h.Seed.AddFindSourcesRunAsync(world);
        var findId = h.Seed.AddFindSourcesTask(world, run);
        var fetch = h.QueueHttpTask(world, "delta-e2e.com", "/story/after-find");

        await h.StartFetcherAsync(Ct);
        try
        {
            (await FetcherE2EHarness.WaitAsync(() => h.Seed.SourceStatus(fetch.SourceId) == "fetched", Bound, Ct))
                .ShouldBeTrue("the loop stopped after the find_sources task");
            (await h.WaitForPostedResultAsync(fetch.TaskId, Bound, Ct))
                .ShouldBeTrue("the fetch result POST was not acknowledged by the fetcher");
        }
        finally
        {
            await h.StopFetcherAsync();
        }

        h.Traffic.Results.ShouldHaveSingleItem().TaskId.ShouldBe(fetch.TaskId);
        h.Seed.TaskStatus(findId).ShouldBe("leased");
        h.Logs.Lines.ShouldContain(line => line.Contains("no source finder runner registered", StringComparison.Ordinal));
    }

    /// <summary>
    /// Proves "deleting the source mid-fetch": the result POST answers 404 <c>fetch_task_not_found</c>, the loop logs it and
    /// continues (a second task queued afterwards is fetched).
    /// </summary>
    [Fact]
    public async Task EndToEnd_SourceDeletedMidFetch_ResultIs404AndLoopContinues()
    {
        await using var h = new FetcherE2EHarness(Database, request => Page(request, TimeSpan.FromMilliseconds(600)));
        var world = await h.Seed.SeedWorldAsync();
        var doomed = h.QueueHttpTask(world, "epsilon-e2e.com", "/story/doomed");

        await h.StartFetcherAsync(Ct);
        FetchTaskSeed.SeededTask? next = null;
        try
        {
            (await FetcherE2EHarness.WaitAsync(
                    () => h.Pages.Requests.Any(request => request.Target.StartsWith("/story/doomed", StringComparison.Ordinal)),
                    Bound,
                    Ct))
                .ShouldBeTrue("the page was never requested");
            h.Seed.Execute($"DELETE FROM story_sources WHERE id = '{doomed.SourceId}'");

            (await FetcherE2EHarness.WaitAsync(() => h.Traffic.Results.Count > 0, Bound, Ct)).ShouldBeTrue("no result was posted");
            next = h.QueueHttpTask(world, "zeta-e2e.com", "/story/survivor");
            (await FetcherE2EHarness.WaitAsync(() => h.Seed.SourceStatus(next.SourceId) == "fetched", Bound, Ct))
                .ShouldBeTrue("the loop did not continue after the 404");
            (await h.WaitForPostedResultAsync(next.TaskId, Bound, Ct))
                .ShouldBeTrue("the survivor's result POST was not acknowledged by the fetcher");
        }
        finally
        {
            await h.StopFetcherAsync();
        }

        h.Traffic.Results[0].ShouldBe(new ResultPostRecord(doomed.TaskId, 404, "fetch_task_not_found"));
        h.Logs.Lines.ShouldContain(line => line.Contains(doomed.TaskId.ToString(), StringComparison.Ordinal) && line.Contains("(fetch_task_not_found)", StringComparison.Ordinal));
        h.Traffic.Results.ShouldContain(new ResultPostRecord(next!.TaskId, 200, string.Empty));
    }

    /// <summary>
    /// Proves "6 queued tasks, at most 4 in flight": six tasks on six domains with pages that stall; a probe of
    /// <c>status = 'leased'</c> never sees more than 4, sees more than 1, and all six sources end up fetched.
    /// </summary>
    [Fact]
    public async Task EndToEnd_SixQueuedTasks_AtMostFourInFlight()
    {
        await using var h = new FetcherE2EHarness(Database, request => Page(request, TimeSpan.FromMilliseconds(400)));
        var world = await h.Seed.SeedWorldAsync();
        var tasks = Enumerable.Range(1, 6).Select(i => h.QueueHttpTask(world, $"six{i}-e2e.com", $"/story/{i}")).ToList();
        long peak = 0;
        var done = false;
        var probe = Task.Run(
            async () =>
            {
                while (!Volatile.Read(ref done))
                {
                    peak = Math.Max(peak, h.Seed.LeasedCount());
                    await Task.Delay(10, CancellationToken.None);
                }
            },
            CancellationToken.None);

        await h.StartFetcherAsync(Ct);
        try
        {
            (await FetcherE2EHarness.WaitAsync(
                    () => tasks.All(task => h.Seed.SourceStatus(task.SourceId) == "fetched"),
                    TimeSpan.FromSeconds(40),
                    Ct))
                .ShouldBeTrue("not every source was fetched");
        }
        finally
        {
            await h.StopFetcherAsync();
            Volatile.Write(ref done, true);
            await probe;
        }

        peak.ShouldBeLessThanOrEqualTo(4, "Fetcher:MaxLeasesPlatformWide is 4");
        peak.ShouldBeGreaterThan(1, "the loop works tasks concurrently");
    }

    private sealed class StubFinder : ISourceFinderRunner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<ErrorOr<IReadOnlyList<ResultCandidate>>> FindAsync(LeasedTask task, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            IReadOnlyList<ResultCandidate> candidates =
            [
                new("https://news.example/a", "A", "News"),
                new("https://news.example/b", "B", "News"),
            ];

            return ValueTask.FromResult<ErrorOr<IReadOnlyList<ResultCandidate>>>(ErrorOrFactory.From(candidates));
        }
    }
}
