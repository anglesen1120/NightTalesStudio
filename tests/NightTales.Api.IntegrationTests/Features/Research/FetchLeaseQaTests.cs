using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Features.Research.Fetcher;
using NightTales.Application.Features.Research.Jobs;
using NightTales.Application.Telemetry;
using NightTales.Infrastructure.Storage;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// Independent QA for 02.5.2-T2 (negative, boundary and exploratory cases written by a tester who did not write the
/// feature): hostile bodies, races with the sweeper and between two results, the per-worker budget, the metrics, the
/// logs on the failure paths and the isolation of the public port. Tests marked <c>Skip</c> are open defects: each one
/// asserts the behaviour the plan promises and was run un-skipped to confirm it fails today.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FetchLeaseQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Hello = "hello";

    private static CancellationToken Ct => TestContextToken.Value;

    private FetchTaskSeed NewSeed()
    {
        var seed = new FetchTaskSeed(Database);
        seed.ResetTasks();

        return seed;
    }

    private ApiFactory NewFactory(
        InMemoryBlobStore? blobs = null,
        int maxRequestsPerMinutePerWorker = 10_000,
        DateTimeOffset? clock = null,
        ILoggerProvider? logs = null)
    {
        var factory = new ApiFactory(Database, new FakeClock(clock ?? FetchTaskSeed.Start))
            .WithFetcherInternalApi(maxRequestsPerMinutePerWorker);
        if (blobs is not null)
        {
            factory.ConfigureServices(services =>
            {
                services.RemoveAll<IBlobStore>();
                services.AddSingleton<IBlobStore>(blobs);
            });
        }

        if (logs is not null)
        {
            factory.WithLoggerProvider(logs);
        }

        return factory;
    }

    private static string DoneJson(string finalUrlJson, string contentTypeJson = "\"text/html\"", string titleJson = "\"t\"", string shaJson = "")
    {
        var sha = shaJson.Length > 0 ? shaJson : "\"" + FetchTaskSeed.Sha256(Hello) + "\"";

        return "{\"workerId\":\"fetcher-1\",\"status\":\"done\",\"finalUrl\":" + finalUrlJson + ",\"title\":" + titleJson
            + ",\"contentType\":" + contentTypeJson + ",\"text\":\"" + Hello + "\",\"textSha256\":" + sha + "}";
    }

    private async Task<WorkerHost> StartWorkerAsync()
    {
        var host = new WorkerHost(Database, new FakeClock(FetchTaskSeed.Start));
        await host.StartAsync(Ct);

        return host;
    }

    private static async Task<FetchLeaseSweepSummary> SweepAsync(WorkerHost host)
    {
        using var scope = host.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<FetchLeaseSweeperJob>().SweepAsync(Ct);
    }

    // ------------------------------------------------------------------ hostile request shapes

    [Theory]
    [InlineData("{\"workerId\":5,\"max\":1}")]
    [InlineData("{\"workerId\":\"fetcher-1\",\"max\":true}")]
    [InlineData("{\"workerId\":\"fetcher-1\",\"max\":null}")]
    [InlineData("{\"workerId\":\"fetcher-1\",\"max\":[1]}")]
    [InlineData("{\"workerId\":\"fetcher-1\",\"max\":1.5}")]
    [InlineData("{\"workerId\":\"fetcher-1\",\"max\":1e0}")]
    [InlineData("{\"workerId\":\"fetcher-1\\u0000\",\"max\":1}")]
    [InlineData("{\"workerId\":\"fetcher-1\\u0001\",\"max\":1}")]
    [InlineData("{\"workerId\":\"fetcher-1\\u007f\",\"max\":1}")]
    [InlineData("{\"workerId\":\"fetcher 1\",\"max\":1}")]
    [InlineData("{\"workerId\":\"fetcher/1\",\"max\":1}")]
    [InlineData("{\"workerId\":\"fetcher-\\u00e9\",\"max\":1}")]
    [InlineData("{\"workerId\":\"\\ud800\",\"max\":1}")]
    [InlineData("{\"workerId\":\"\\udc00x\",\"max\":1}")]
    [InlineData("{\"workerId\":\"   \",\"max\":1}")]
    [InlineData("{\"workerId\":\"\",\"max\":1}")]
    [InlineData("{\"max\":1}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("")]
    [InlineData("{\"workerId\":\"fetcher-1\",\"max\":1,}")]
    [InlineData("{\"workerId\":\"fetcher-1\",\"max\":1} {}")]
    [InlineData("{\"workerId\":\"fetcher-1\",\"max\":1 /* c */}")]
    public async Task Lease_HostileBodies_Return400_NeverA500_AndLeaseNothing(string json)
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        seed.AddQueued(world, "hostile.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Prefix + "/lease", json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("validation_failed");
        seed.LeasedCount().ShouldBe(0);
    }

    [Fact]
    public async Task Lease_ByteOrderMarkBody_Returns400_NeverA500()
    {
        NewSeed();
        using var factory = NewFactory();
        using var client = factory.InternalClient();
        using var content = new ByteArrayContent([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"workerId\":\"fetcher-1\",\"max\":1}")]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.PostAsync(FetchTaskSeed.Prefix + "/lease", content, Ct);

        ((int)response.StatusCode).ShouldBeLessThan(500);
    }

    [Theory]
    [InlineData("{\"workerId\":\"fetcher-1\\u0000\"}")]
    [InlineData("{\"workerId\":\"\\ud800\"}")]
    [InlineData("{\"workerId\":\"fetcher\\u0009-1\"}")]
    [InlineData("{\"workerId\":7}")]
    [InlineData("{}")]
    public async Task Heartbeat_HostileWorkerId_Returns400_AndTheLeaseIsUntouched(string json)
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "hb-hostile.example");
        var before = seed.Text($"SELECT lease_until::text FROM fetch_tasks WHERE id = '{task.TaskId}'");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        seed.Text($"SELECT lease_until::text FROM fetch_tasks WHERE id = '{task.TaskId}'").ShouldBe(before);
    }

    [Fact]
    public async Task Lease_DeeplyNestedBodies_Return400_OnEveryRoute()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "nest.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();
        var nested = "{\"workerId\":\"fetcher-1\",\"max\":1,\"x\":" + new string('[', 5_000) + new string(']', 5_000) + "}";

        foreach (var path in new[] { FetchTaskSeed.Prefix + "/lease", FetchTaskSeed.Route(task.TaskId, "heartbeat"), FetchTaskSeed.Route(task.TaskId, "result") })
        {
            using var response = await FetchTaskSeed.PostRawAsync(client, path, nested);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, path);
        }
    }

    [Fact(Skip = "QA-3 open: Web serializer defaults accept a quoted number for max")]
    public async Task Lease_QuotedMax_IsRejected_AsTheContractCallsForAnInteger()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        seed.AddQueued(world, "quoted.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Prefix + "/lease", "{\"workerId\":\"fetcher-1\",\"max\":\"1\"}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "QA-3: JsonSerializerDefaults.Web accepts \"1\" for an int, so a quoted max is leased");
    }

    [Theory]
    [InlineData("\"DONE\"")]
    [InlineData("\"Done\"")]
    [InlineData("\" done\"")]
    [InlineData("\"done \"")]
    [InlineData("\"done\\u0000\"")]
    [InlineData("\"\\ud800\"")]
    [InlineData("\"1\"")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[\"done\"]")]
    [InlineData("{}")]
    public async Task Result_StatusThatIsNotExactlyDoneOrFailed_Returns400_AndLeavesTheTaskLeased(string statusJson)
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "status.example");
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostRawAsync(
            client,
            FetchTaskSeed.Route(task.TaskId, "result"),
            "{\"workerId\":\"fetcher-1\",\"status\":" + statusJson + "}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("validation_failed");
        seed.TaskStatus(task.TaskId).ShouldBe("leased");
        seed.SourceStatus(task.SourceId).ShouldBe("pending");
    }

    [Theory]
    [InlineData("\"https://news.example.com/\\u0000\"")]
    [InlineData("\"https://news.example.com/\\u0001x\"")]
    [InlineData("\"https://news.example.com/\\u007f\"")]
    [InlineData("\"https://news.example.com/x\\u0000\"")]
    [InlineData("\"javascript:alert(1)\"")]
    [InlineData("\"file:///etc/passwd\"")]
    [InlineData("\"ftp://news.example.com/x\"")]
    [InlineData("\"https://127.0.0.1/x\"")]
    [InlineData("\"https://localhost/x\"")]
    [InlineData("\"https://[::1]/x\"")]
    [InlineData("\"//news.example.com/x\"")]
    [InlineData("\"   \"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public async Task Result_HostileFinalUrl_Returns400SourceDomainNotAllowed_AndNothingIsStored(string finalUrlJson)
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "finalurl.example");
        var blobs = new InMemoryBlobStore();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), DoneJson(finalUrlJson));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("source_domain_not_allowed");
        FetchTaskSeed.ResearchPuts(blobs).ShouldBe(0);
        seed.SourceStatus(task.SourceId).ShouldBe("pending");
        seed.TaskStatus(task.TaskId).ShouldBe("leased");
    }

    [Fact]
    public async Task Result_FinalUrlOverTheUrlCap_IsRefused_NeverA500()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "longurl.example");
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();
        var url = "\"https://news.example.com/" + new string('a', 5_000) + "\"";

        using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), DoneJson(url));

        ((int)response.StatusCode).ShouldBeLessThan(500);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        seed.SourceStatus(task.SourceId).ShouldBe("pending");
    }

    [Fact]
    public async Task Result_FinalUrlWithUserInfo_NeverStoresTheCredentials()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "userinfo.example");
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostRawAsync(
            client,
            FetchTaskSeed.Route(task.TaskId, "result"),
            DoneJson("\"https://admin:hunter2@news.example.com/story\""));

        ((int)response.StatusCode).ShouldBeLessThan(500);
        seed.Text($"SELECT coalesce(final_url, '') FROM story_sources WHERE id = '{task.SourceId}'")
            .ShouldNotContain("hunter2");
    }

    [Theory]
    [InlineData("\"text/html\\u0000\"")]
    [InlineData("\"\\ud800\"")]
    [InlineData("\"application/pdf\"")]
    [InlineData("\"text/htmlx\"")]
    [InlineData("\"image/png; charset=utf-8\"")]
    [InlineData("\";\"")]
    [InlineData("\";text/html\"")]
    [InlineData("\"text/html,application/pdf\"")]
    [InlineData("\"\"")]
    [InlineData("12")]
    public async Task Result_HostileContentType_Returns400_NeverA500(string contentTypeJson)
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "ctype.example");
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostRawAsync(
            client,
            FetchTaskSeed.Route(task.TaskId, "result"),
            DoneJson("\"https://news.example.com/x\"", contentTypeJson));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        seed.TaskStatus(task.TaskId).ShouldBe("leased");
    }

    [Theory]
    [InlineData("\"\\u0000\"")]
    [InlineData("\"\\ud800\"")]
    [InlineData("\"abc\\u0000def\"")]
    [InlineData("12")]
    [InlineData("null")]
    [InlineData("\"\"")]
    public async Task Result_HostileTextSha256_Returns400SourceNoText_NeverA500(string shaJson)
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "sha.example");
        var blobs = new InMemoryBlobStore();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostRawAsync(
            client,
            FetchTaskSeed.Route(task.TaskId, "result"),
            DoneJson("\"https://news.example.com/x\"", shaJson: shaJson));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        FetchTaskSeed.ResearchPuts(blobs).ShouldBe(0);
    }

    [Fact]
    public async Task Result_TitleOfLoneSurrogateOrControlsOnly_IsStoredAsNull_AndTheResultIsAccepted()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var first = seed.AddLeased(world, "title-1.example");
        var second = seed.AddLeased(world, "title-2.example");
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();

        using var withSurrogate = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Route(first.TaskId, "result"), DoneJson("\"https://news.example.com/x\"", titleJson: "\"bad\\ud800title\""));
        using var controlsOnly = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Route(second.TaskId, "result"), DoneJson("\"https://news.example.com/y\"", titleJson: "\"\\u0000\\u0001\\u0007\""));

        // A lone surrogate escape cannot even be parsed, so that body is a 400; a controls-only title is cleaned away.
        withSurrogate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        controlsOnly.StatusCode.ShouldBe(HttpStatusCode.OK);
        seed.Text($"SELECT coalesce(title, '<null>') FROM story_sources WHERE id = '{second.SourceId}'").ShouldBe("<null>");
    }

    [Fact]
    public async Task Result_LongEmojiTitle_IsCappedWithoutSplittingASurrogatePair()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "emoji-title.example");
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();
        var title = string.Concat(Enumerable.Repeat("😀", 400));
        var body = FetchTaskSeed.DoneBody();
        body["title"] = title;

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = seed.Text($"SELECT title FROM story_sources WHERE id = '{task.SourceId}'");
        stored.Length.ShouldBeLessThanOrEqualTo(300);
        stored.Length.ShouldBeGreaterThan(0);
        stored.Length.ShouldBe(stored.EnumerateRunes().Count() * 2, "every rune is a full surrogate pair");
    }

    [Theory]
    [InlineData("\"\\u0000\"")]
    [InlineData("\"source_unreachable\\u0000\"")]
    [InlineData("\"\\ud800\"")]
    [InlineData("\"SOURCE_BLOCKED_BY_ROBOTS\"")]
    [InlineData("\" source_blocked_by_robots\"")]
    [InlineData("12")]
    [InlineData("null")]
    [InlineData("\"find_sources_generation_failed\"")]
    [InlineData("\"fetch_lease_lost\"")]
    public async Task Result_FailedWithAnythingButAnExactJobCode_StoresSourceUnreachable_OrRefusesTheBody(string errorCodeJson)
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "errcode.example");
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostRawAsync(
            client,
            FetchTaskSeed.Route(task.TaskId, "result"),
            "{\"workerId\":\"fetcher-1\",\"status\":\"failed\",\"errorCode\":" + errorCodeJson + "}");

        ((int)response.StatusCode).ShouldBeLessThan(500);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            seed.Text($"SELECT error_code FROM story_sources WHERE id = '{task.SourceId}'").ShouldBe("source_unreachable");
            seed.Text($"SELECT error_code FROM fetch_tasks WHERE id = '{task.TaskId}'").ShouldBe("source_unreachable");
        }
        else
        {
            seed.TaskStatus(task.TaskId).ShouldBe("leased");
        }
    }

    [Fact]
    public async Task Result_TextEdgeCharacters_AreAcceptedAndRoundTripExactly()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "edge-text.example");
        var blobs = new InMemoryBlobStore();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        // C1 control, line/paragraph separators, BOM, zero-width space, replacement char, a noncharacter, CRLF.
        const string Text = "a\u0085b\u2028c\u2029d\uFEFFe\u200Bf\uFFFDg\uFFFFh\r\ni\tj";
        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody(Text));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var key = seed.Text($"SELECT extracted_text_key FROM story_sources WHERE id = '{task.SourceId}'");
        var opened = await blobs.OpenReadAsync(factory.Services.GetRequiredService<StorageSettings>().Buckets.Media, key, Ct);
        opened.IsError.ShouldBeFalse();
        using var memory = new MemoryStream();
        await opened.Value.Content.CopyToAsync(memory, Ct);
        memory.ToArray().ShouldBe(Encoding.UTF8.GetBytes(Text), "the stored object is the exact UTF-8 of the posted text");
    }

    [Fact]
    public async Task Result_WorkerIdIsMatchedExactly_AWorkerIdOfAnotherCaseGets409()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "case.example");
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();

        using var result = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody(workerId: "Fetcher-1"));
        using var heartbeat = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), new { workerId = "FETCHER-1" });

        result.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        heartbeat.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ------------------------------------------------------------------ body size

    [Fact]
    public async Task Lease_ChunkedBodyOverTheCap_Returns413_AndUnderTheCapIsServed()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        seed.AddQueued(world, "chunked.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();
        var over = Encoding.UTF8.GetBytes("{\"workerId\":\"fetcher-1\",\"max\":4,\"pad\":\"" + new string('x', 1_048_576) + "\"}");
        var under = Encoding.UTF8.GetBytes("{\"workerId\":\"fetcher-1\",\"max\":4}");

        using var tooBig = await client.PostAsync(FetchTaskSeed.Prefix + "/lease", new UnknownLengthContent(over), Ct);
        seed.LeasedCount().ShouldBe(0);
        using var fine = await client.PostAsync(FetchTaskSeed.Prefix + "/lease", new UnknownLengthContent(under), Ct);

        tooBig.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        fine.StatusCode.ShouldBe(HttpStatusCode.OK);
        seed.LeasedCount().ShouldBe(1);
    }

    [Fact]
    public async Task Result_ArrayOfHundredsOfThousandsOfEntries_IsAnsweredFastWith400()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var runId = await seed.AddFindSourcesRunAsync(world);
        var task = seed.AddFindSourcesTask(world, runId, leasedBy: FetchTaskSeed.Worker);
        using var factory = NewFactory();
        using var client = factory.InternalClient();
        var json = "{\"workerId\":\"fetcher-1\",\"status\":\"done\",\"candidates\":[" + string.Join(',', Enumerable.Repeat("{}", 300_000)) + "]}";

        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Route(task, "result"), json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe("validation_failed");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        seed.TaskStatus(task).ShouldBe("leased");
    }

    // ------------------------------------------------------------------ races

    [Fact]
    public async Task Result_TwoConcurrentResultsForOneTask_ExactlyOneWins()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var blobs = new InMemoryBlobStore();
        using var factory = NewFactory(blobs);
        using var first = factory.InternalClient();
        using var second = factory.InternalClient();

        for (var iteration = 0; iteration < 10; iteration++)
        {
            var task = seed.AddLeased(world, $"race-{iteration}.example");
            var route = FetchTaskSeed.Route(task.TaskId, "result");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new[]
            {
                PostWhenOpenAsync(first, route, FetchTaskSeed.DoneBody($"first text {iteration}"), gate.Task),
                PostWhenOpenAsync(second, route, FetchTaskSeed.DoneBody($"second text {iteration}"), gate.Task),
            };
            gate.SetResult();
            var codes = (await Task.WhenAll(calls)).Order().ToArray();

            codes.ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict], $"iteration {iteration}");
            seed.TaskStatus(task.TaskId).ShouldBe("done");
            seed.Count($"SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}' AND payload::text LIKE '%{task.SourceId}%'")
                .ShouldBe(1, $"iteration {iteration}: exactly one SourceFetched row");
            seed.Count($"SELECT count(*) FROM story_sources WHERE id = '{task.SourceId}' AND status = 'fetched'").ShouldBe(1);
        }

        FetchTaskSeed.ResearchPuts(blobs).ShouldBe(10, "only the winner of each race writes an object");
    }

    [Fact]
    public async Task Result_AfterTheSweeperRequeuedAndAnotherWorkerLeased_OnlyTheNewHolderCanFinish()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "requeue.example", leaseUntil: "now() - interval '1 second'", attempts: 1);
        var blobs = new InMemoryBlobStore();
        await using var worker = await StartWorkerAsync();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        (await SweepAsync(worker)).Requeued.ShouldBe(1);
        using var heartbeat = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), new { workerId = "fetcher-1" });
        heartbeat.StatusCode.ShouldBe(HttpStatusCode.Conflict, "a heartbeat cannot resurrect a requeued task");
        seed.TaskStatus(task.TaskId).ShouldBe("queued");

        seed.Execute($"UPDATE fetch_tasks SET not_before = now() - interval '1 second' WHERE id = '{task.TaskId}'");
        var leased = await FetchTaskSeed.LeaseItemsAsync(client, "fetcher-2");
        leased.Length.ShouldBe(1);
        leased[0].GetProperty("attempt").GetInt32().ShouldBe(2);

        using var stale = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody(workerId: "fetcher-1"));
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        FetchTaskSeed.ResearchPuts(blobs).ShouldBe(0);

        using var current = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody(workerId: "fetcher-2"));
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
        seed.TaskStatus(task.TaskId).ShouldBe("done");
    }

    [Fact]
    public async Task Result_RacingTheSweeperOnAnExpiredLease_Is409_AndNothingIsStored()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var blobs = new InMemoryBlobStore();
        await using var worker = await StartWorkerAsync();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        for (var iteration = 0; iteration < 5; iteration++)
        {
            var task = seed.AddLeased(world, $"expired-{iteration}.example", leaseUntil: "now() - interval '1 second'", attempts: 1);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = PostWhenOpenAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), FetchTaskSeed.DoneBody(), gate.Task);
            var sweep = Task.Run(async () =>
            {
                await gate.Task;

                return await SweepAsync(worker);
            });
            gate.SetResult();

            (await result).ShouldBe(HttpStatusCode.Conflict, $"iteration {iteration}: an expired lease never accepts a result");
            (await sweep).Requeued.ShouldBeGreaterThanOrEqualTo(1);
            seed.TaskStatus(task.TaskId).ShouldBe("queued");
            seed.SourceStatus(task.SourceId).ShouldBe("pending");
            seed.ResetTasks();
        }

        FetchTaskSeed.ResearchPuts(blobs).ShouldBe(0);
    }

    [Fact]
    public async Task Sweeper_TwoConcurrentRuns_HandleEachExpiredRowExactlyOnce()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var requeue = Enumerable.Range(0, 6).Select(i => seed.AddLeased(world, $"requeue-{i}.example", leaseUntil: "now() - interval '1 minute'", attempts: 1)).ToList();
        var fail = Enumerable.Range(0, 4).Select(i => seed.AddLeased(world, $"fail-{i}.example", leaseUntil: "now() - interval '1 minute'", attempts: 2)).ToList();
        await using var worker = await StartWorkerAsync();

        var runs = await Task.WhenAll(SweepAsync(worker), SweepAsync(worker), SweepAsync(worker));

        runs.Sum(run => run.Requeued).ShouldBe(requeue.Count);
        runs.Sum(run => run.Failed).ShouldBe(fail.Count);
        runs.Sum(run => run.SourcesFailed).ShouldBe(fail.Count);
        seed.Count("SELECT count(*) FROM fetch_tasks WHERE status = 'queued' AND attempts = 1").ShouldBe(requeue.Count);
        seed.Count("SELECT count(*) FROM fetch_tasks WHERE status = 'failed' AND error_code = 'source_unreachable'").ShouldBe(fail.Count);
        seed.Count("SELECT count(*) FROM story_sources WHERE status = 'failed' AND error_code = 'source_unreachable'").ShouldBe(fail.Count);
    }

    [Fact(Skip = "QA-2 open: a heartbeat sets lease_until = now()+60 s, which shortens a fresh 90 s lease")]
    public async Task Heartbeat_OnAFreshLease_DoesNotShortenIt()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "shorten.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();
        var beforeSeconds = double.Parse(seed.Text($"SELECT extract(epoch FROM lease_until - now())::text FROM fetch_tasks WHERE id = '{task.TaskId}'"), System.Globalization.CultureInfo.InvariantCulture);

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), new { workerId = "fetcher-1" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var afterSeconds = double.Parse(seed.Text($"SELECT extract(epoch FROM lease_until - now())::text FROM fetch_tasks WHERE id = '{task.TaskId}'"), System.Globalization.CultureInfo.InvariantCulture);
        afterSeconds.ShouldBeGreaterThanOrEqualTo(beforeSeconds - 2, $"QA-2: the plan says a heartbeat extends the lease; a fresh 90 s lease became {afterSeconds:F0} s");
    }

    // ------------------------------------------------------------------ rate limit

    [Fact]
    public async Task RateLimit_EveryRouteSharesOneBudget_RefusalCarriesRetryAfter_AndUnauthorisedCallsAreFree()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddLeased(world, "budget.example");
        using var factory = NewFactory(maxRequestsPerMinutePerWorker: 4);
        using var client = factory.InternalClient();
        using var anonymous = factory.InternalClient(token: null);

        for (var i = 0; i < 20; i++)
        {
            (await FetchTaskSeed.LeaseAsync(anonymous, "fetcher-1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await FetchTaskSeed.LeaseAsync(client, "fetcher-1")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), new { workerId = "fetcher-1" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(Guid.NewGuid(), "result"), FetchTaskSeed.DoneBody())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Prefix + "/lease", "{\"workerId\":\"fetcher-1\",\"max\":0}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var limited = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "heartbeat"), new { workerId = "fetcher-1" });

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull("a 429 names when to retry");
        limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeInRange(1, 60);
        (await FetchTaskSeed.CodeOfAsync(limited)).ShouldBe("rate_limited");
        (await FetchTaskSeed.LeaseAsync(client, "fetcher-2")).StatusCode.ShouldBe(HttpStatusCode.OK, "another worker id has its own budget");
    }

    [Fact(Skip = "QA-4 open: the per-worker limiter keys on any <=64-char workerId before validation")]
    public async Task RateLimit_MalformedWorkerIds_DoNotEachGetAFreshBudget()
    {
        NewSeed();
        using var factory = NewFactory(maxRequestsPerMinutePerWorker: 3);
        using var client = factory.InternalClient();
        var limited = 0;

        for (var i = 0; i < 12; i++)
        {
            using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Prefix + "/lease", "{\"workerId\":\"bad id " + i + "\",\"max\":1}");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited++;
            }
        }

        limited.ShouldBeGreaterThan(0, "QA-4: the limiter keys on any 64-character string before the validator runs, so every malformed id is a new partition");
    }

    // ------------------------------------------------------------------ isolation

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task PublicPort_EveryVerbOnEveryInternalRoute_Is404_WithOrWithoutTheToken(string method)
    {
        using var factory = NewFactory();
        var task = "00000000-0000-0000-0000-000000000001";
        foreach (var tail in new[] { "lease", task + "/heartbeat", task + "/result", task + "/result/", "LEASE", "lease/" })
        {
            foreach (var withToken in new[] { true, false })
            {
                using var client = factory.PublicPortClient();
                if (withToken)
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.FetcherToken);
                }

                using var request = new HttpRequestMessage(new HttpMethod(method), FetchTaskSeed.Prefix + "/" + tail);
                if (method is "POST" or "PUT" or "PATCH")
                {
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                }

                using var response = await client.SendAsync(request, Ct);

                response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {tail} token={withToken}");
            }
        }
    }

    [Fact(Skip = "QA-5 open: FETCHER_API_TOKEN is hashed untrimmed but the presented value is trimmed")]
    public async Task Token_ConfiguredWithTrailingWhitespace_StillAuthenticatesTheTrimmedValue()
    {
        NewSeed();
        using var factory = new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start))
            .WithFetcherInternalApi()
            .WithSetting("FETCHER_API_TOKEN", ApiFactory.FetcherToken + "\n");
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.LeaseAsync(client);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "QA-5: the stored digest is over the untrimmed token while the presented value is trimmed, so a secret delivered with a trailing newline can never authenticate");
    }

    [Fact(Skip = "QA-6 open: a find_sources task without a generation_runs row is leased with no finderRequest")]
    public async Task Lease_FindSourcesTaskWithoutAGenerationRun_IsNotHandedOutWithoutItsPayload()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var task = Guid.NewGuid();
        seed.Execute(
            "INSERT INTO fetch_tasks (id, user_id, episode_id, source_id, generation_run_id, kind, url, registrable_domain, status, attempts, not_before, created_at) "
            + $"VALUES ('{task}', '{world.UserId}', '{world.EpisodeId}', NULL, NULL, 'find_sources', NULL, NULL, 'queued', 0, now() - interval '1 minute', now() - interval '1 minute')");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        var items = await FetchTaskSeed.LeaseItemsAsync(client);

        items.Count(item => item.GetProperty("kind").GetString() == "find_sources" && !item.GetRawText().Contains("finderRequest", StringComparison.Ordinal))
            .ShouldBe(0, "QA-6: a find_sources lease without its generation_runs row is handed out with no finderRequest (the fetcher cannot act on it)");
    }

    // ------------------------------------------------------------------ metrics and logs

    [Fact]
    public async Task Metrics_TerminalResultsAreCounted_AndTheDurationRunsFromLeaseNotFromCreation()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var done = seed.AddLeased(world, "metric-done.example");
        var failed = seed.AddLeased(world, "metric-failed.example");
        seed.Execute($"UPDATE fetch_tasks SET created_at = now() - interval '1 hour', not_before = now() - interval '1 hour', leased_at = now() - interval '20 seconds' WHERE id IN ('{done.TaskId}', '{failed.TaskId}')");
        using var factory = NewFactory(new InMemoryBlobStore(), clock: DateTimeOffset.UtcNow);
        using var client = factory.InternalClient();
        var metrics = factory.Services.GetRequiredService<NightTalesMetrics>();
        _ = factory.Services.GetRequiredService<FetchLeaseGauge>();
        using var probe = new MeterProbe(metrics.Meter);

        (await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(done.TaskId, "result"), FetchTaskSeed.DoneBody())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(failed.TaskId, "result"), FetchTaskSeed.FailedBody("source_blocked_by_robots"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var counted = probe.Measures.Where(m => m.Name == NightTalesMetrics.FetchTasksTotalName).ToList();
        counted.ShouldContain(m => m.Tag("status") == "done" && m.Tag("error_code") == null && m.Value == 1);
        counted.ShouldContain(m => m.Tag("status") == "failed" && m.Tag("error_code") == "source_blocked_by_robots" && m.Value == 1);
        var durations = probe.Measures.Where(m => m.Name == NightTalesMetrics.FetchDurationSecondsName).Select(m => m.Value).ToList();
        durations.Count.ShouldBe(2);
        durations.ShouldAllBe(seconds => seconds >= 15 && seconds <= 120, "lease to result is ~20 s; created to result would be ~3600 s");

        await FetchTaskSeed.LeaseAsync(client);
        probe.Observe();
        probe.Measures.Where(m => m.Name == FetchLeaseGauge.Name).Select(m => m.Value).Last().ShouldBe(seed.LeasedCount());
    }

    [Fact]
    public async Task Metrics_SweeperCountsRequeuedAndFailedTransitions()
    {
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        seed.AddLeased(world, "sweep-metric-1.example", leaseUntil: "now() - interval '1 minute'", attempts: 1);
        seed.AddLeased(world, "sweep-metric-2.example", leaseUntil: "now() - interval '1 minute'", attempts: 2);
        await using var worker = await StartWorkerAsync();
        using var probe = new MeterProbe(worker.Services.GetRequiredService<NightTalesMetrics>().Meter);

        await SweepAsync(worker);

        var counted = probe.Measures.Where(m => m.Name == NightTalesMetrics.FetchTasksTotalName).ToList();
        counted.ShouldContain(m => m.Tag("status") == "requeued" && m.Tag("error_code") == null);
        counted.ShouldContain(m => m.Tag("status") == "failed" && m.Tag("error_code") == "source_unreachable");
    }

    [Fact]
    public async Task Logs_FailurePaths_NeverCarryTheBodyTheTokenOrAnUrl()
    {
        const string Secret = "qa-failure-marker-7c1d"; // gitleaks:allow (test canary, not a credential)
        var seed = NewSeed();
        var world = await seed.SeedWorldAsync();
        var leased = seed.AddLeased(world, "failure-log.example", path: "/" + Secret);
        var logs = new CapturingLoggerProvider();
        using var factory = NewFactory(new InMemoryBlobStore(), maxRequestsPerMinutePerWorker: 1_000, logs: logs);
        using var client = factory.InternalClient();
        using var wrong = factory.InternalClient(token: "wrong-" + Secret);

        var tampered = FetchTaskSeed.DoneBody(Secret, finalUrl: "https://news.example.com/" + Secret);
        tampered["textSha256"] = FetchTaskSeed.Sha256("other");
        (await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(leased.TaskId, "result"), tampered)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(leased.TaskId, "result"), FetchTaskSeed.DoneBody(Secret, workerId: "fetcher-9"))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(Guid.NewGuid(), "result"), FetchTaskSeed.DoneBody(Secret))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Route(leased.TaskId, "result"), "{\"workerId\":\"fetcher-1\",\"status\":\"done\",\"text\":\"" + Secret + "\\ud800\"}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var oversized = await client.PostAsync(FetchTaskSeed.Prefix + "/lease", new UnknownLengthContent(Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('x', 1_048_600) + Secret + "\"}")), Ct);
        oversized.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await FetchTaskSeed.LeaseAsync(wrong)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var text = logs.Text;
        text.ShouldNotBeEmpty();
        foreach (var forbidden in new[] { Secret, ApiFactory.FetcherToken, "Bearer", "failure-log.example", "news.example.com" })
        {
            text.ShouldNotContain(forbidden, Case.Insensitive);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<HttpStatusCode> PostWhenOpenAsync(HttpClient client, string route, object body, Task gate)
    {
        await gate;
        using var response = await FetchTaskSeed.PostAsync(client, route, body);

        return response.StatusCode;
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes.AsMemory()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }

    private sealed record Measure(string Name, double Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public string? Tag(string key) => Tags.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private sealed class MeterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Measure> _measures = [];

        public MeterProbe(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
            _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
            _listener.Start();
        }

        public IReadOnlyList<Measure> Measures
        {
            get
            {
                lock (_measures)
                {
                    return [.. _measures];
                }
            }
        }

        public void Observe() => _listener.RecordObservableInstruments();

        public void Dispose() => _listener.Dispose();

        private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            lock (_measures)
            {
                _measures.Add(new Measure(name, value, copy));
            }
        }
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
