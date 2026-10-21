using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// The MPT client's contract (11 README D6, 05 C3/D15 P6): the submit body, the two headers, the two timeouts,
/// no redirect, and the split between a health signal and a task state.
/// </summary>
public sealed class MptClientTests
{
    private const string MptTaskId = "67b5ecfe-c283-47d1-a265-382224c1c207";

    private const string SubmitFixture = "render-submit.v1.landscape-vi.json";

    [Fact]
    public async Task Submit_SendsXTaskIdAndNThreads()
    {
        await using var harness = await AdapterHarness.StartAsync(options => options.MptThreads = 4);
        var submit = JsonSerializer.Deserialize<AdapterSubmitRequestV1>(
            FixtureFiles.Read(SubmitFixture),
            RenderNodeJson.Strict)!;
        var record = AdapterHarness.NewRecord(Guid.CreateVersion7(), submit.RenderJobId);
        await harness.SeedAsync(record);

        var accepted = await harness.Client.SubmitAsync(record, submit.Mpt, CancellationToken.None);

        accepted.IsError.ShouldBeFalse();
        accepted.Value.BackendTaskId.ShouldBe(harness.Mpt.SubmitTaskId);

        var request = harness.Mpt.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("POST");
        request.Path.ShouldBe("/api/v1/videos");
        request.ApiKey.ShouldBe(TestEnvironment.MptApiKey, "the node's MPT key travels in x-api-key (05 C2)");
        request.TaskId.ShouldBe(record.TaskId.ToString("D"), "the adapter's own id travels in x-task-id (05 C3)");

        var sent = JsonNode.Parse(request.Body)!.AsObject();
        sent["n_threads"]!.GetValue<int>().ShouldBe(4, "05 D15 P6: the adapter owns n_threads");
        var keys = sent.Select(property => property.Key).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var expected = JsonNode.Parse(FixtureFiles.Read(SubmitFixture))!.AsObject()["mpt"]!.AsObject()
            .Select(property => property.Key)
            .Append("n_threads")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        keys.ShouldBe(expected, "the D3 key set plus n_threads and nothing else");

        // The core's acceptance write: the two backend fields move, the row stays queued until the first poll.
        (await harness.Store.RecordBackendAcceptanceAsync(
            record.TaskId,
            accepted.Value.BackendTaskId,
            accepted.Value.StartedAt,
            CancellationToken.None)).ShouldBeTrue();

        var stored = await harness.RowAsync(record.TaskId);
        stored.BackendTaskId.ShouldBe(harness.Mpt.SubmitTaskId);
        stored.BackendStartedAt.ShouldBeNull();
        stored.State.ShouldBe(RenderStates.Queued);
        stored.Stage.ShouldBe(RenderStages.Queued);
        stored.Percent.ShouldBe(0);
    }

    [Fact]
    public async Task Submit_ThroughAdapterCore_MptPostAndStoredRow()
    {
        await using var harness = await AdapterHarness.StartAsync(options => options.MptThreads = 2, withHost: true);

        var response = await harness.PostAsync("/v1/renders", FixtureFiles.Read(SubmitFixture));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = JsonSerializer.Deserialize<AdapterAcceptedV1>(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            RenderNodeJson.Tolerant)!;
        accepted.State.ShouldBe(RenderStates.Queued);

        var request = harness.Mpt.Requests.ShouldHaveSingleItem();
        request.Path.ShouldBe("/api/v1/videos");
        request.TaskId.ShouldBe(accepted.TaskId.ToString("D"));

        var row = await harness.RowAsync(accepted.TaskId);
        row.BackendTaskId.ShouldBe(harness.Mpt.SubmitTaskId);
        row.State.ShouldBe(RenderStates.Queued);
        harness.PlanSink.Registered.ShouldContain(accepted.TaskId);
    }

    [Fact]
    public async Task Timeouts_Connect2sRead30s()
    {
        var defaults = new AdapterOptions();
        defaults.MptConnectTimeoutSeconds.ShouldBe(2, "D6: connect timeout 2 s");
        defaults.MptReadTimeoutSeconds.ShouldBe(30, "D6: read timeout 30 s");

        using var handler = MptHttpClient.CreateHandler(defaults);
        handler.ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(2));
        handler.AllowAutoRedirect.ShouldBeFalse("D6: the adapter never follows a redirect");

        // The read timeout is MptClient's own: a 1 s override against a 3 s answer must fail fast, not hang.
        await using var harness = await AdapterHarness.StartAsync(options => options.MptReadTimeoutSeconds = 1);
        harness.Mpt.Script(MptTaskId, 4, 20);
        harness.Mpt.ResponseDelay = TimeSpan.FromSeconds(3);

        var stopwatch = Stopwatch.StartNew();
        var status = await harness.Client.GetStatusAsync(MptTaskId, Guid.CreateVersion7(), CancellationToken.None);
        stopwatch.Stop();

        status.IsError.ShouldBeTrue();
        status.FirstError.Code.ShouldBe(MptErrorCodes.Unreachable);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public async Task NoRedirectFollowed()
    {
        await using var harness = await AdapterHarness.StartAsync();
        harness.Mpt.Script(MptTaskId, 4, 20);
        harness.Mpt.RedirectTo = "/ping";

        var status = await harness.Client.GetStatusAsync(MptTaskId, Guid.CreateVersion7(), CancellationToken.None);

        status.IsError.ShouldBeTrue("a 3xx is not the frozen task route");
        harness.Mpt.Requests.Count.ShouldBe(1, "the redirect must not be followed to /ping");
    }

    [Fact]
    public async Task MptUnauthorized_HealthNotStatus()
    {
        await using var harness = await AdapterHarness.StartAsync();
        harness.Mpt.Script(MptTaskId, 4, 20);
        harness.Mpt.RejectEveryKey = true;

        // /ping keeps working (it is the liveness probe); the authenticated probe is what reports the key.
        (await harness.Client.PingAsync(CancellationToken.None)).ShouldBeTrue();
        var probe = await harness.Client.CheckApiKeyAsync(CancellationToken.None);
        probe.IsError.ShouldBeTrue();
        probe.FirstError.Code.ShouldBe(MptErrorCodes.Unauthorized);

        // D7's split: a 401 is a health reason, never a task transition.
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId,
            state: RenderStates.Running,
            stage: RenderStages.Voice,
            percent: 20);
        await harness.SeedAsync(record);

        await harness.PollOnceAsync();

        (await harness.RowAsync(record.TaskId)).ShouldBe(record, "the poller must not change the row on a 401");
        harness.Logs.Contains("mpt_unauthorized").ShouldBeTrue();
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://127.5.6.7:8080")]
    [InlineData("http://[::1]:8080")]
    public void MptBaseUrl_LoopbackIpLiteralOverHttp_Accepted(string url)
    {
        var options = new AdapterOptions { MptBaseUrl = url };

        Should.NotThrow(() => NewClient(options));
        AdapterOptions.ParseLoopbackMptBaseUrl(url).ShouldBe(new Uri(url));
    }

    [Theory]
    [InlineData("https://remote.example")]
    [InlineData("http://remote.example:8080")]
    [InlineData("http://localhost:8080")]
    [InlineData("http://10.0.0.5:8080")]
    [InlineData("http://[2001:db8::1]:8080")]
    [InlineData("https://127.0.0.1:8080")]
    public void MptBaseUrl_NotLoopbackIpLiteralOverHttp_Rejected(string url)
    {
        var options = new AdapterOptions { MptBaseUrl = url };

        var failure = Should.Throw<ArgumentException>(() => NewClient(options));

        failure.Message.ShouldContain("Adapter:MptBaseUrl");
        failure.Message.ShouldNotContain("remote.example", Case.Insensitive, "the message must not echo the value");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("http://")]
    public void MptBaseUrl_Malformed_KeepsUriFormatException(string url)
    {
        var options = new AdapterOptions { MptBaseUrl = url };

        Should.Throw<UriFormatException>(() => NewClient(options));
    }

    [Fact]
    public void MptBaseUrl_Null_NamesTheKey()
    {
        var options = new AdapterOptions { MptBaseUrl = null! };

        var failure = Should.Throw<ArgumentNullException>(() => NewClient(options));

        failure.ParamName.ShouldBe("Adapter:MptBaseUrl");
    }

    private static MptClient NewClient(AdapterOptions options)
    {
        var bound = Options.Create(options);
        return new(
            MptHttpClient.Create(options),
            bound,
            Options.Create(new RenderNodeServerOptions { MaxConcurrentTasks = 1 }),
            new FakeProcessStartReader(),
            new DiskRuleEnforcer(new TaskDirCleaner(bound, NullLogger<TaskDirCleaner>.Instance), NullLogger<DiskRuleEnforcer>.Instance),
            new FileOutputSource(new InMemoryTaskStore(), bound, NullLogger<FileOutputSource>.Instance),
            NullLogger<MptClient>.Instance);
    }
}
