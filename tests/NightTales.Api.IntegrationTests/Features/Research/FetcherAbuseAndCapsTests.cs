using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Features.Research.Jobs;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Fetching;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using NightTales.Worker.Jobs;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// 02.5.2-T4 cases 21-34 plus 23b, 23c, 23d (threat rows 7-11, 17, 18; F15): payload, type, robots, cookies, rate and
/// timeout against the real fetcher and loopback server, then the url-source and lease caps and the sweeper against the real
/// Api and PostgreSQL. Also holds the suite's count check, the leak scan and the code-set check.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FetcherAbuseAndCapsTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The hosts the leak test fetches; only their spans are scanned (co-running test classes share the process).</summary>
    private static readonly string[] LeakTraceHosts = ["page-e2e.com", "denied-e2e.com", "youtube.com"];

    private static CancellationToken Ct => TestContextToken.Value;

    private static FetchStack NewStack(
        Action<NightTales.Fetcher.Core.Fetching.FetcherOptions>? configure = null,
        bool realRateLimiter = false,
        TimeProvider? robotsTime = null) =>
        FetchStack.Create(HttpTestRoutes.Standard, configure: configure, realRateLimiter: realRateLimiter, robotsTime: robotsTime);

    // ------------------------------------------------------------------ payload and type (rows 7-8)

    [Fact]
    public async Task Payload_GzipBomb_TooLarge()
    {
        // threat row 7, case 21 (F14: decompression bomb).
        await using var stack = NewStack();

        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com/gzip-bomb", Ct)).ShouldBe(SourceErrors.SourceTooLargeCode);

        HttpTestRoutes.GzipBombWireBytes.ShouldBeLessThan(stack.Options.MaxResponseBytes, "the bomb is small on the wire");
        stack.PageRequests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("/pdf")]
    [InlineData("/binary-as-html")]
    public async Task Payload_WrongOrDisguisedContentType_Unsupported(string path)
    {
        // threat row 8, case 22.
        await using var stack = NewStack();

        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com" + path, Ct)).ShouldBe(SourceErrors.SourceUnsupportedTypeCode, path);
    }

    [Theory]
    [InlineData("/spa")]
    [InlineData("/short-text")]
    public async Task Payload_SpaAndShortText_NoText(string path)
    {
        // threat row 8, case 23.
        await using var stack = NewStack();

        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com" + path, Ct)).ShouldBe(SourceErrors.SourceNoTextCode, path);
    }

    [Fact]
    public async Task Payload_MinTextCharsBoundary()
    {
        // threat row 8, case 23b (02 T104): MinTextChars = 1,275; 1,274 chars of text is no text, 1,275 is success.
        await using var stack = NewStack(options => options.MinTextChars = 1_275);

        var below = Calibrate(HttpTestRoutes.SecretTitle, 1_274);
        var at = Calibrate(HttpTestRoutes.SecretTitle, 1_275);

        FetchStack.Code(await stack.FetchAsync($"http://page-e2e.com/article?chars={below}", Ct)).ShouldBe(SourceErrors.SourceNoTextCode);
        var accepted = await stack.FetchAsync($"http://page-e2e.com/article?chars={at}", Ct);
        accepted.IsError.ShouldBeFalse();
        accepted.Value.TextChars.ShouldBeGreaterThanOrEqualTo(1_275);
    }

    /// <summary>The paragraph length whose extracted text is exactly <paramref name="target"/> characters (calibrated once, no guessing).</summary>
    private static int Calibrate(string title, int target)
    {
        var extractor = new NightTales.Fetcher.Core.Research.Extraction.SmartReaderArticleExtractor(
            Microsoft.Extensions.Options.Options.Create(new NightTales.Fetcher.Core.Fetching.FetcherOptions { MinTextChars = 1 }));
        for (var chars = target - 40; chars <= target + 40; chars++)
        {
            var html = HttpTestRoutes.ArticleHtml(title, chars);
            if (extractor.Extract(html, new Uri("http://page-e2e.com/a"), "text/html", CancellationToken.None).Text.Length == target)
            {
                return chars;
            }
        }

        throw new InvalidOperationException($"no paragraph length yields {target} extracted chars");
    }

    [Fact]
    public async Task Payload_DeepNestedHtml_DepthGuard_TooLarge_NoCrash()
    {
        // threat row 7, case 23c (02 T105, SEC-0928-1): 100,000-deep nesting, inside 2 MiB, rejected before a DOM is built.
        await using var stack = NewStack();
        var watch = Stopwatch.StartNew();

        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com/deep-nested", Ct)).ShouldBe(SourceErrors.SourceTooLargeCode);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(stack.Options.TotalTimeoutSeconds + stack.Options.ExtractTimeoutSeconds));
    }

    [Fact]
    public async Task Payload_ManyElements_ElementGuard_TooLarge()
    {
        // threat row 7, case 23d (02 T105): more than ExtractMaxElements elements inside 2 MiB.
        await using var stack = NewStack();
        var watch = Stopwatch.StartNew();

        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com/many-elements", Ct)).ShouldBe(SourceErrors.SourceTooLargeCode);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(stack.Options.TotalTimeoutSeconds + stack.Options.ExtractTimeoutSeconds));
    }

    [Fact]
    public async Task Payload_OverMaxChars_TooLarge()
    {
        // threat row 7, case 24.
        await using var stack = NewStack();

        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com/long-text", Ct)).ShouldBe(SourceErrors.SourceTooLargeCode);
    }

    // ------------------------------------------------------------------ robots, cookies, timeout, rate (rows 9-10, 12)

    [Fact]
    public async Task Robots_Disallow_BlockedByRobots()
    {
        // threat row 9, case 25 (F14: robots failure semantics).
        await using var stack = NewStack();
        stack.Robots = _ => HttpTestRoutes.RobotsDisallowAll();

        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com/ok", Ct)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode);

        stack.RobotsRequests.Count.ShouldBe(1);
        stack.PageRequests.ShouldBeEmpty("a disallowed page is never requested");
    }

    [Fact]
    public async Task Robots_ServerErrorOrTimeout_BlockedByRobots()
    {
        // threat row 9, case 26 (D11: 5xx / unreachable -> disallow).
        await using var serverError = NewStack();
        serverError.Robots = _ => HttpTestRoutes.RobotsServerError();
        FetchStack.Code(await serverError.FetchAsync("http://page-e2e.com/ok", Ct)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode);
        serverError.PageRequests.ShouldBeEmpty();

        await using var timeout = NewStack(options => options.RobotsTimeoutSeconds = 1);
        timeout.Robots = _ => HttpTestRoutes.RobotsSlow(TimeSpan.FromSeconds(4));
        FetchStack.Code(await timeout.FetchAsync("http://page-e2e.com/ok", Ct)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode);
        timeout.PageRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Robots_NotFound_AllowsAll()
    {
        // threat row 9, case 27 (D11: 4xx -> allow).
        await using var stack = NewStack();
        stack.Robots = _ => HttpTestRoutes.RobotsNotFound();

        (await stack.FetchAsync("http://page-e2e.com/ok", Ct)).IsError.ShouldBeFalse();

        stack.PageRequests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Robots_Cached24h_OneFetchPerOrigin()
    {
        // threat row 9, case 28.
        var time = new AdjustableTimeProvider(Start);
        await using var stack = NewStack(robotsTime: time);
        stack.Robots = _ => NightTales.TestKit.Fetching.TestResponse.Text(200, "text/plain", "User-agent: *\nAllow: /\n");

        (await stack.FetchAsync("http://page-e2e.com/ok", Ct)).IsError.ShouldBeFalse();
        (await stack.FetchAsync("http://page-e2e.com/ok", Ct)).IsError.ShouldBeFalse();
        stack.RobotsRequests.Count.ShouldBe(1, "two fetches inside 24 h share one robots request");

        time.Advance(TimeSpan.FromHours(25));
        (await stack.FetchAsync("http://page-e2e.com/ok", Ct)).IsError.ShouldBeFalse();
        stack.RobotsRequests.Count.ShouldBe(2, "after 25 h the robots file is fetched again");
    }

    [Fact]
    public async Task Cookies_SetByServer_NotReplayed()
    {
        // threat row 10, case 29 (F14: cookies; UseCookies = false).
        await using var stack = NewStack();

        (await stack.FetchAsync("http://page-e2e.com/cookie-setter", Ct)).IsError.ShouldBeFalse();
        (await stack.FetchAsync("http://page-e2e.com/set-cookie-then-check", Ct)).IsError.ShouldBeFalse();

        stack.Server.Requests.Count.ShouldBeGreaterThanOrEqualTo(2);
        stack.Server.Requests.ShouldAllBe(request => !request.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task Timeout_SharedBudget_Unreachable()
    {
        // threat row 12, case 30: robots (slow, but inside its own limit) and the page share one 10 s budget.
        await using var stack = NewStack(options => options.RobotsTimeoutSeconds = 5);
        stack.Robots = _ => HttpTestRoutes.RobotsSlow(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();

        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com/slow?ms=12000", Ct)).ShouldBe(SourceErrors.SourceUnreachableCode);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(11), "robots + page stayed inside the 10 s budget (+ margin)");
    }

    [Fact]
    public async Task RateLimit_OnePerSecondPerDomain_NotAcrossDomains()
    {
        // threat row 12, case 31: the production limiter.
        await using var stack = NewStack(realRateLimiter: true);

        (await stack.FetchAsync("http://same-e2e.com/ok", Ct)).IsError.ShouldBeFalse();
        (await stack.FetchAsync("http://same-e2e.com/ok", Ct)).IsError.ShouldBeFalse();
        var same = stack.Server.Requests.Select((request, index) => (request, time: stack.Server.RequestTimes[index]))
            .Where(entry => entry.request.Host.StartsWith("same-e2e.com", StringComparison.Ordinal) && entry.request.Target == "/ok")
            .Select(entry => entry.time)
            .ToList();
        same.Count.ShouldBe(2);
        (same[1] - same[0]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(0.9), "same registrable domain: serialised 1/s");

        var first = stack.FetchAsync("http://one-e2e.com/ok", Ct).AsTask();
        var second = stack.FetchAsync("http://two-e2e.com/ok", Ct).AsTask();
        await Task.WhenAll(first, second);
        var times = stack.Server.Requests.Select((request, index) => (request, time: stack.Server.RequestTimes[index]))
            .Where(entry => entry.request.Target == "/ok"
                && (entry.request.Host.StartsWith("one-e2e.com", StringComparison.Ordinal) || entry.request.Host.StartsWith("two-e2e.com", StringComparison.Ordinal)))
            .Select(entry => entry.time)
            .OrderBy(time => time)
            .ToList();
        times.Count.ShouldBe(2);
        (times[1] - times[0]).ShouldBeLessThan(TimeSpan.FromSeconds(0.9), "different registrable domains are not serialised");
    }

    // ------------------------------------------------------------------ volume and lease caps (rows 11, 17, 18; F15)

    [Fact]
    public async Task Caps_ThirtyFirstUrlSource_429_AndEleventhSourceOnEpisode_409()
    {
        // threat rows 11 and 17, case 32 (02.5.1-T1's rules from the abuse angle).
        var actor = new TestCurrentUser();
        var clock = new FakeClock(Start);
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(user.Id, ct: Ct);
        var dailyEpisode = (await new EpisodeBuilder(Database, actor, clock).CreateAsync(user.Id, series.Id, status: EpisodeStatus.Draft, ct: Ct)).Id;
        var cappedEpisode = (await new EpisodeBuilder(Database, actor, clock).CreateAsync(user.Id, series.Id, status: EpisodeStatus.Draft, ct: Ct)).Id;
        SqlProbe.Execute(
            Database,
            "insert into source_creations (id, user_id, created_at) "
            + $"select gen_random_uuid(), '{user.Id}', '{Start:O}' from generate_series(1, 30)");
        SqlProbe.Execute(
            Database,
            "insert into story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, created_at) "
            + $"select gen_random_uuid(), '{user.Id}', '{cappedEpisode}', 'url', 'https://cap-e2e.com/' || n, 'https://cap-e2e.com/' || n, 'cap-e2e.com', 'pending', '{Start:O}' "
            + "from generate_series(1, 10) n");

        using var factory = new ApiFactory(Database, clock);
        using var client = factory.ClientFor(user.Id);

        using var thirtyFirst = await client.PostAsJsonAsync($"/api/v1/episodes/{dailyEpisode}/sources", new { url = "https://example.com/31" }, Ct);
        thirtyFirst.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CodeOfAsync(thirtyFirst)).ShouldBe("source_fetch_limit_reached");
        thirtyFirst.Headers.RetryAfter.ShouldNotBeNull();
        thirtyFirst.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeGreaterThan(0);

        using var eleventh = await client.PostAsJsonAsync($"/api/v1/episodes/{cappedEpisode}/sources", new { url = "https://example.com/11" }, Ct);
        eleventh.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(eleventh)).ShouldBe("source_limit_reached");
        SqlProbe.Count(Database, $"select count(*) from story_sources where episode_id = '{cappedEpisode}'").ShouldBe(10);
    }

    [Fact]
    public async Task Lease_ConcurrentCalls_PlatformAndDomainCapsHold()
    {
        // threat row 18 (F15), case 33: 6 queued tasks (two share a domain), lease twice concurrently, repeated.
        var seed = new FetchTaskSeed(Database);
        seed.ResetTasks();
        var world = await seed.SeedWorldAsync();
        using var factory = new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start)).WithFetcherInternalApi();
        using var first = factory.InternalClient();
        using var second = factory.InternalClient();

        for (var iteration = 0; iteration < 20; iteration++)
        {
            seed.ResetTasks();
            seed.AddQueued(world, "dup-e2e.com", ageSeconds: 100);
            seed.AddQueued(world, "dup-e2e.com", ageSeconds: 99);
            for (var i = 1; i <= 4; i++)
            {
                seed.AddQueued(world, $"lease-{i}-e2e.com", ageSeconds: 90 - i);
            }

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new[] { LeaseWhenOpenAsync(first, "fetcher-1", gate.Task), LeaseWhenOpenAsync(second, "fetcher-2", gate.Task) };
            gate.SetResult();
            var results = await Task.WhenAll(calls);

            var ids = results.SelectMany(items => items).Select(item => item.GetProperty("taskId").GetGuid()).ToList();
            ids.Distinct().Count().ShouldBe(ids.Count, $"iteration {iteration}: a task was handed out twice");
            seed.LeasedCount().ShouldBeLessThanOrEqualTo(4, $"iteration {iteration}: platform cap");
            seed.Count("SELECT count(*) FROM (SELECT registrable_domain FROM fetch_tasks WHERE status = 'leased' GROUP BY 1 HAVING count(*) > 1) d")
                .ShouldBe(0, $"iteration {iteration}: one task per registrable domain");
        }
    }

    private static async Task<JsonElement[]> LeaseWhenOpenAsync(HttpClient client, string worker, Task gate)
    {
        await gate;

        return await FetchTaskSeed.LeaseItemsAsync(client, worker);
    }

    [Fact]
    public async Task Lease_AbandonedTask_SweeperRequeuesThenFails()
    {
        // threat row 17, case 34: lease through the real API, abandon, force the sweeper (no sleeping), twice.
        var seed = new FetchTaskSeed(Database);
        seed.ResetTasks();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddQueued(world, "abandon-e2e.com");
        using var factory = new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start)).WithFetcherInternalApi();
        using var client = factory.InternalClient();
        await using var host = new WorkerHost(Database, new FakeClock(FetchTaskSeed.Start));
        await host.StartAsync(Ct);

        (await FetchTaskSeed.LeaseItemsAsync(client)).ShouldHaveSingleItem().GetProperty("taskId").GetGuid().ShouldBe(task.TaskId);
        Abandon(task.TaskId);
        (await SweepAsync(host)).ShouldBe(new FetchLeaseSweepSummary(Requeued: 1, Failed: 0, SourcesFailed: 0));
        seed.TaskStatus(task.TaskId).ShouldBe("queued");
        seed.SourceStatus(task.SourceId).ShouldBe("pending");

        seed.Execute($"UPDATE fetch_tasks SET not_before = now() - interval '1 minute' WHERE id = '{task.TaskId}'");
        (await FetchTaskSeed.LeaseItemsAsync(client)).ShouldHaveSingleItem().GetProperty("taskId").GetGuid().ShouldBe(task.TaskId);
        Abandon(task.TaskId);
        (await SweepAsync(host)).ShouldBe(new FetchLeaseSweepSummary(Requeued: 0, Failed: 1, SourcesFailed: 1));

        seed.Text($"SELECT status || '/' || error_code FROM fetch_tasks WHERE id = '{task.TaskId}'").ShouldBe("failed/source_unreachable");
        seed.Text($"SELECT status || '/' || error_code FROM story_sources WHERE id = '{task.SourceId}'").ShouldBe("failed/source_unreachable");

        void Abandon(Guid id) =>
            seed.Execute($"UPDATE fetch_tasks SET lease_until = now() - interval '1 second', leased_at = now() - interval '91 seconds' WHERE id = '{id}'");
    }

    private static async Task<FetchLeaseSweepSummary> SweepAsync(WorkerHost host)
    {
        using var scope = host.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<FetchLeaseSweeperJob>().SweepAsync(Ct);
    }

    private static async Task<string> CodeOfAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("code").GetString()!;

    // ------------------------------------------------------------------ cross-cutting

    [Fact]
    public async Task Leak_NoUrlTitleOrTextInLogsOrTraces()
    {
        // D19: the listener is registered before the first fetch, so the failure-path spans are scanned too. The fetcher
        // registers no tracing source of its own (FetcherTelemetry is metrics-only, D19), so the only span the stack can
        // produce is the SDK's System.Net.Http one; scoping ShouldListenTo to that name keeps the scan on this stack's
        // own surface (any other source is out of contract for the fetcher).
        // ActivityStopped can fire concurrently from several threads, so the sink must be thread-safe.
        var traces = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                // Classes run in parallel in one process: scan only the spans of the hosts this test fetches.
                if (activity.GetTagItem("server.address") is string address && LeakTraceHosts.Contains(address, StringComparer.Ordinal))
                {
                    traces.Enqueue(TraceText(activity));
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        // Success and every failure shape on one stack; the log and the traces must carry no path, query, title or text.
        await using var stack = NewStack(options => options.RobotsTimeoutSeconds = 1);
        (await stack.FetchAsync("http://page-e2e.com/ok?q=secret-q", Ct)).IsError.ShouldBeFalse();
        foreach (var path in new[] { "/gzip-bomb", "/pdf", "/spa", "/long-text", "/chain?n=4" })
        {
            FetchStack.Code(await stack.FetchAsync($"http://page-e2e.com{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}q=secret-q", Ct));
        }

        // Blocked by the blocklist and a rejected redirect target: their secret path is never dialled, so no span may carry it.
        FetchStack.Code(await stack.FetchAsync("http://youtube.com/secret-path/article?q=secret-q", Ct))
            .ShouldBe(SourceErrors.SourceDomainNotAllowedCode);
        var rejected = Uri.EscapeDataString("http://169.254.169.254/secret-path?q=secret-q");
        FetchStack.Code(await stack.FetchAsync($"http://page-e2e.com/redirect?to={rejected}", Ct))
            .ShouldBe(SourceErrors.SourceUrlInvalidCode);

        // A robots denial is a path that logs (the registrable domain and reason); the scan needs at least one line to scan.
        stack.Robots = _ => HttpTestRoutes.RobotsDisallowAll();
        FetchStack.Code(await stack.FetchAsync("http://denied-e2e.com/secret-path/article?q=secret-q", Ct)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode);
        stack.Robots = _ => HttpTestRoutes.RobotsNotFound();

        stack.Logs.Lines.ShouldNotBeEmpty("the scan must have something to scan");
        stack.AssertNoLeak();
        traces.ShouldNotBeEmpty("the trace scan must have something to scan (no fetcher span means the scan is vacuous)");
        FetcherSuiteAsserts.NoSecrets(traces, FetchStack.Secrets, "trace");
    }

    /// <summary>Every surface of a span the scan must read: source, display name, tags and event payloads.</summary>
    private static string TraceText(Activity activity) =>
        activity.Source.Name + " " + activity.DisplayName
        + " " + string.Join(' ', activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))
        + " " + string.Join(' ', activity.Events.SelectMany(item => item.Tags).Select(tag => $"{tag.Key}={tag.Value}"));

    [Fact]
    public async Task Persistence_ExtractedTextObjectHasNoHtml_FailedCaseWritesNone_ApiLogsDoNotLeak()
    {
        // Cross-cutting (task "No raw HTML persisted" + "No leakage", Api half), through the real loop and the real Api: a fetched
        // source's object holds the extracted text only (no tag, length equals text_chars); a failed fetch writes no object; the
        // Api's own log output carries neither the URL path/query nor the title nor the text.
        await using var harness = new FetcherE2EHarness(Database, request => request.Target switch
        {
            var target when target.StartsWith("/story/", StringComparison.Ordinal) => HttpTestRoutes.Standard(request with { Target = "/article?chars=3000" }),
            var target when target.StartsWith("/pdf-secret", StringComparison.Ordinal) => HttpTestRoutes.Standard(request with { Target = "/pdf" }),
            _ => new TestResponse(404),
        });
        var world = await harness.Seed.SeedWorldAsync();
        var good = harness.QueueHttpTask(world, "good-e2e.com", "/story/secret-article-path?secret=qq");
        var bad = harness.QueueHttpTask(world, "bad-e2e.com", "/pdf-secret-path?secret=qq");

        await harness.StartFetcherAsync(Ct);
        try
        {
            (await FetcherE2EHarness.WaitAsync(
                    () => harness.Seed.SourceStatus(good.SourceId) == "fetched" && harness.Seed.SourceStatus(bad.SourceId) == "failed",
                    TimeSpan.FromSeconds(30),
                    Ct))
                .ShouldBeTrue($"good={harness.Seed.SourceStatus(good.SourceId)} bad={harness.Seed.SourceStatus(bad.SourceId)}");
        }
        finally
        {
            await harness.StopFetcherAsync();
        }

        var key = harness.Seed.Text($"SELECT extracted_text_key FROM story_sources WHERE id = '{good.SourceId}'");
        key.ShouldNotBeNullOrEmpty();
        var put = harness.Blobs.Calls.Single(call => call.Operation == BlobOperation.Put && call.Key == key);
        var opened = await harness.Blobs.OpenReadAsync(put.Bucket, key, Ct);
        opened.IsError.ShouldBeFalse();
        string stored;
        using (var reader = new StreamReader(opened.Value.Content))
        {
            stored = await reader.ReadToEndAsync(Ct);
        }

        stored.ShouldNotContain("<", Case.Sensitive, "extracted text must not contain a tag");
        stored.ShouldNotContain(">", Case.Sensitive, "extracted text must not contain a tag");
        stored.Length.ShouldBe((int)harness.Seed.Count($"SELECT text_chars FROM story_sources WHERE id = '{good.SourceId}'"), "the object is exactly the text counted on the row");

        harness.Seed.Text($"SELECT coalesce(extracted_text_key, '') FROM story_sources WHERE id = '{bad.SourceId}'").ShouldBeEmpty("a failed case has no object key");
        harness.Blobs.Calls.Where(call => call.Operation == BlobOperation.Put && call.Key.StartsWith("research/", StringComparison.Ordinal)).Select(call => call.Key).Distinct().ShouldBe([key], "only the successful source wrote an object");

        harness.ApiLogs.Lines.ShouldNotBeEmpty("the Api scan must have something to scan");
        foreach (var forbidden in new[] { "secret-article-path", "pdf-secret-path", "secret=qq", HttpTestRoutes.SecretTitle, HttpTestRoutes.SecretText })
        {
            harness.ApiLogs.Lines.ShouldNotContain(line => line.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"Api log leaked '{forbidden}'");
            harness.Logs.Lines.ShouldNotContain(line => line.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"fetcher log leaked '{forbidden}'");
        }
    }

    [Fact]
    public async Task ErrorCodes_AreInCatalogue()
    {
        // Every D8 job code is a catalogue member, and every code any fetch of this process produced is a D8 job code.
        var catalogue = FetcherSuiteAsserts.Catalogue();
        foreach (var code in FetcherSuiteAsserts.SourceErrorCodes())
        {
            catalogue.ShouldContain(code, "SourceErrors code missing from the ErrorCodes catalogue");
        }

        // The observed-code half is only meaningful when this process actually produced codes: run a representative
        // failing set here, so the loop cannot pass on an empty set when this test runs alone.
        await using var stack = NewStack();
        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com/gzip-bomb", Ct)).ShouldBe(SourceErrors.SourceTooLargeCode);
        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com/pdf", Ct)).ShouldBe(SourceErrors.SourceUnsupportedTypeCode);
        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com/spa", Ct)).ShouldBe(SourceErrors.SourceNoTextCode);
        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com/chain?n=4", Ct)).ShouldBe(SourceErrors.SourceUnreachableCode);
        FetchStack.Code(await stack.FetchAsync("http://youtube.com/article", Ct)).ShouldBe(SourceErrors.SourceDomainNotAllowedCode);

        var observed = FetchStack.ObservedCodes.Distinct().ToList();
        observed.ShouldNotBeEmpty("the observed-code loop must not be vacuous");
        foreach (var code in observed)
        {
            FetcherSuiteAsserts.JobCodes.ShouldContain(code, "the suite observed a code outside the D8 job-code set");
        }
    }

    [Fact]
    public void Suite_HasAll37Cases()
    {
        // A deleted case, or a deleted case row, fails here (task acceptance: the count is asserted).
        var cases = SuiteCases.All().ToList();
        cases.Select(row => row.Case).Distinct().Count().ShouldBe(SuiteCases.Expected);
        foreach (var (caseId, owner, member) in cases)
        {
            owner.GetMethod(member, BindingFlags.Public | BindingFlags.Instance).ShouldNotBeNull($"case {caseId}: {owner.Name}.{member} is missing");
        }

        foreach (var (owner, member) in SuiteCases.ExtraMembers())
        {
            owner.GetMethod(member, BindingFlags.Public | BindingFlags.Instance).ShouldNotBeNull($"{owner.Name}.{member} is missing");
        }
    }
}
