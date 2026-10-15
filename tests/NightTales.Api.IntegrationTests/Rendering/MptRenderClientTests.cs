using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Rendering;
using NightTales.RenderNode.Contract;
using NightTales.TestKit.Rendering;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// <c>MptRenderClient</c> against the scripted node and the frozen D14 bodies (09.6.2-T1 task §Tests): the
/// three calls' wire shapes, every <c>MOCK_FAIL_MODE</c> the mock serves, and the one rule that runs through
/// all of them — neither the node's key nor its base URL ever reaches a log line or a failure (BD16/S4).
/// </summary>
public sealed class MptRenderClientTests
{
    private const string TaskId = "3f7b9c2e-1a44-4d0f-9e21-6c5b8a7d0e11";

    private const string SubmitFixture = "render-submit.v1.landscape-vi.json";

    [Fact]
    public async Task Submit_MapsAdapterAcceptedV1()
    {
        var taskId = Guid.NewGuid();
        var handler = new ScriptedNodeHandler(NodeHttpStep.Json(202, new AdapterAcceptedV1(taskId, "queued")));
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), new ManualTimeProvider());
        var client = provider.GetRequiredService<IRenderClient>();

        var submitted = await client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None);

        submitted.IsError.ShouldBeFalse();
        submitted.Value.TaskId.ShouldBe(taskId);
        submitted.Value.State.ShouldBe("queued");
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Method.ShouldBe("POST");
        handler.Requests[0].Path.ShouldBe("/v1/renders");
    }

    [Fact]
    public async Task Submit_RetryReplaysSameRenderJobId_AdapterAccepts()
    {
        var taskId = Guid.NewGuid();
        var handler = new ScriptedNodeHandler(
            NodeHttpStep.Answer(500, Envelope(AdapterErrorCodes.InternalError)),
            NodeHttpStep.Json(202, new AdapterAcceptedV1(taskId, "queued")));

        var time = new ManualTimeProvider();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), time);
        var client = provider.GetRequiredService<IRenderClient>();

        var submitted = await DriveAsync(client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None), time);

        submitted.IsError.ShouldBeFalse();
        submitted.Value.TaskId.ShouldBe(taskId);
        handler.Requests.Count.ShouldBe(2);

        // The retry must replay the very same bytes, or the adapter's idempotency check sees a second job.
        var renderJobId = JsonNode.Parse(ContractFixtures.Read(SubmitFixture))!["render_job_id"]!.GetValue<string>();
        handler.Requests[0].Body.ShouldNotBeNull().ShouldContain(renderJobId);
        handler.Requests[1].Body.ShouldBe(handler.Requests[0].Body);
    }

    [Fact]
    public async Task GetStatus_MapsRenderStatusV1()
    {
        var handler = new ScriptedNodeHandler(
            NodeHttpStep.Answer(200, ContractFixtures.Read("render-status.v1.succeeded.json")));
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), new ManualTimeProvider());
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None);

        status.IsError.ShouldBeFalse();
        status.Value.Schema.ShouldBe("render-status.v1");
        status.Value.State.ShouldBe("succeeded");
        status.Value.Output.ShouldNotBeNull();
        handler.Requests[0].Method.ShouldBe("GET");
        handler.Requests[0].Path.ShouldBe($"/v1/renders/{TaskId}");
    }

    [Fact]
    public async Task GetStatus_404_OnSubmittedTask_IsRenderLost()
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Json(404, Envelope(AdapterErrorCodes.TaskUnknown)));
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), new ManualTimeProvider());
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None);

        status.IsError.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(1);

        RenderClientErrors.TryGetFailure(status.Errors[0], out var failure).ShouldBeTrue();
        failure!.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderLost);
        failure.Alert.ShouldBeFalse();
    }

    /// <summary>
    /// Every <c>MOCK_FAIL_MODE</c> the mock serves (11.1.2-T1 D5) as a node answer, mapped by the client:
    /// a failed status carries its outcome code, a timed-out render is still a running status, and a failing
    /// quality check is a <em>successful</em> status — the policy of 11.3.3-T1 judges the check block, never
    /// this client.
    /// </summary>
    /// <param name="mode">The mock's fail mode.</param>
    /// <param name="fixture">The frozen body the mock serves for it.</param>
    /// <param name="outcomeCode">The code the failed status must carry.</param>
    [Theory]
    [InlineData("tts_failed", "render-status.v1.failed-tts.json", RenderOutcomeCodes.TtsFailed)]
    [InlineData("assembly_failed", "render-status.v1.failed-assembly.json", RenderOutcomeCodes.RenderAssemblyFailed)]
    [InlineData("node_error", "render-status.v1.failed-tts.json", RenderOutcomeCodes.RenderNodeError)]
    [InlineData("timeout", "render-status.v1.running-voice.json", null)]
    [InlineData("quality:loudness", "render-status.v1.succeeded.json", null)]
    public async Task MockFailMode_ServesTheStatusTheClientMaps(string mode, string fixture, string? outcomeCode)
    {
        var json = ContractFixtures.Read(fixture);
        if (mode == "node_error")
        {
            var patched = JsonNode.Parse(json)!.AsObject();
            patched["outcome"]!["code"] = RenderOutcomeCodes.RenderNodeError;
            json = patched.ToJsonString();
        }

        var handler = new ScriptedNodeHandler(NodeHttpStep.Answer(200, json));
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), new ManualTimeProvider());
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None);

        status.IsError.ShouldBeFalse(
            $"MOCK_FAIL_MODE={mode} is a status the client reads, not a failure: "
            + string.Join(", ", status.Errors.Select(error => error.Code)));

        switch (mode)
        {
            case "timeout":
                status.Value.State.ShouldBe("running");
                break;
            case "quality:loudness":
                status.Value.State.ShouldBe("succeeded");
                status.Value.Check.ShouldNotBeNull();
                break;
            default:
                status.Value.State.ShouldBe("failed");
                status.Value.Outcome.ShouldNotBeNull();
                status.Value.Outcome!.Code.ShouldBe(outcomeCode);
                break;
        }
    }

    /// <summary>The sixth fail mode: the node has no such task at all, which is a lost render (05 D5).</summary>
    [Fact]
    public async Task MockFailMode_LostTask_IsRenderLost()
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Json(404, Envelope(AdapterErrorCodes.TaskUnknown)));
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), new ManualTimeProvider());
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None);

        status.IsError.ShouldBeTrue();
        RenderClientErrors.TryGetFailure(status.Errors[0], out var failure).ShouldBeTrue();
        failure!.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderLost);
    }

    /// <summary>
    /// The secret rule of BD16/S4 across every way a call can end: success, a rotated key (401), a node fault
    /// (500), a transport failure and an open breaker. The sentinel node's key and base URL are unique strings,
    /// and every log line, error code, error sentence and exception message is searched for them.
    /// </summary>
    [Fact]
    public async Task ApiKeyAndBaseUrl_NeverAppearInLogsOrFailures()
    {
        const string key = "sentinel-node-key-7f31c9d4";
        const string host = "sentinel-node-9f3a.invalid";
        var url = new Uri($"https://{host}:8443/");

        var handler = new ScriptedNodeHandler(NodeHttpStep.Transport(url));
        var logs = new CapturingLogger();
        var time = new ManualTimeProvider();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler, key, url), time, logs);
        var client = provider.GetRequiredService<IRenderClient>();
        var node = Node("sentinel");

        var reasoning = new List<ErrorOr.Error>();

        // A success answers with a body the client reads; it must log nothing that identifies the node.
        var accepted = await client.SubmitAsync(node, SubmitBody(), CancellationToken.None);
        accepted.IsError.ShouldBeFalse();

        // A rotated key: terminal and alerting.
        handler.Rescript(NodeHttpStep.Json(401, Envelope(AdapterErrorCodes.Unauthorized)));
        var unauthorized = await DriveAsync(client.GetStatusAsync(node, TaskId, CancellationToken.None), time);
        unauthorized.IsError.ShouldBeTrue();
        reasoning.AddRange(unauthorized.Errors);

        // A node fault: 500 for the whole retry budget.
        handler.Rescript(NodeHttpStep.Answer(500, Envelope(AdapterErrorCodes.InternalError)));
        var failed = await DriveAsync(client.GetStatusAsync(node, TaskId, CancellationToken.None), time);
        failed.IsError.ShouldBeTrue();
        reasoning.AddRange(failed.Errors);

        // A transport failure whose exception message names the URL, exactly as the real socket failure does.
        handler.Rescript(NodeHttpStep.Transport(url));
        var unreachable = await DriveAsync(client.GetStatusAsync(node, TaskId, CancellationToken.None), time);
        unreachable.IsError.ShouldBeTrue();
        reasoning.AddRange(unreachable.Errors);

        // The breaker opens on the fifth failure, so the last call never reaches the node.
        handler.Rescript(NodeHttpStep.Answer(500, Envelope(AdapterErrorCodes.InternalError)));
        await DriveAsync(client.GetStatusAsync(node, TaskId, CancellationToken.None), time);
        var openCircuit = await DriveAsync(client.GetStatusAsync(node, TaskId, CancellationToken.None), time);
        openCircuit.IsError.ShouldBeTrue();
        reasoning.AddRange(openCircuit.Errors);

        // The positive control: the client did log, so an empty capture cannot make this test pass.
        logs.Lines.ShouldNotBeEmpty();
        logs.Lines.Count(line => line.Contains("render node", StringComparison.OrdinalIgnoreCase)).ShouldBeGreaterThan(0);
        reasoning.Count.ShouldBeGreaterThan(0);

        foreach (var line in logs.Lines)
        {
            line.ShouldNotContain(key);
            line.ShouldNotContain(host);
            line.ShouldNotContain(url.ToString());
        }

        foreach (var error in reasoning)
        {
            error.Code.ShouldNotContain(key);
            error.Code.ShouldNotContain(host);
            error.Description.ShouldNotContain(key);
            error.Description.ShouldNotContain(host);
        }
    }

    /// <summary>The node a call is aimed at; the scripted factory supplies the transport.</summary>
    /// <param name="name">The node's name.</param>
    /// <returns>The node.</returns>
    private static WorkerNode Node(string name = "alpha") => WorkerNode.Create(
        name,
        new Uri("https://alpha.example/"),
        capacity: 2,
        tlsCertSha256: new string('A', WorkerNode.TlsCertSha256Length),
        gpuModel: null).Value;

    /// <summary>A real submit body from the frozen D14 fixtures.</summary>
    /// <returns>The body.</returns>
    private static AdapterSubmitRequestV1 SubmitBody() =>
        JsonSerializer.Deserialize<AdapterSubmitRequestV1>(
            ContractFixtures.Read(SubmitFixture), RenderNodeJson.Tolerant)!;

    /// <summary>The one error envelope of the contract, built from its own types.</summary>
    /// <param name="code">The adapter code.</param>
    /// <returns>The envelope as JSON.</returns>
    private static string Envelope(string code) =>
        JsonSerializer.Serialize(
            new AdapterErrorV1(new AdapterErrorBody(code, "The node refused the call.", null)),
            RenderNodeJson.Tolerant);

    /// <summary>The host the client is built in: the real registration over the scripted transport.</summary>
    /// <param name="factory">The factory that hands out the client, with the key and URL a test chooses.</param>
    /// <param name="time">The clock the pipelines run on.</param>
    /// <param name="logs">A capturing logger provider, when the test asserts on log lines.</param>
    /// <returns>The provider to resolve the client from.</returns>
    private static ServiceProvider Provider(
        IRenderNodeConnectionFactory factory,
        TimeProvider time,
        ILoggerProvider? logs = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        if (logs is not null)
        {
            services.AddSingleton(logs);
        }

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(time);
        services.AddSingleton(factory);
        services.AddOptions<RenderNodesOptions>();
        services.AddRenderClients(configuration);

        return services.BuildServiceProvider();
    }

    /// <summary>Settles one call by advancing the manual clock, so a retry budget costs no wall time.</summary>
    /// <typeparam name="T">The call's success type.</typeparam>
    /// <param name="call">The call to settle.</param>
    /// <param name="time">The clock to advance.</param>
    /// <returns>The call's result.</returns>
    private static async Task<T> DriveAsync<T>(Task<T> call, ManualTimeProvider time)
    {
        for (var step = 0; step < 400 && !call.IsCompleted; step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        call.IsCompleted.ShouldBeTrue("the call must finish within the advanced virtual time");
        return await call;
    }

    /// <summary>Captures every log line of the host, exception text included, so a leak cannot hide in one.</summary>
    private sealed class CapturingLogger : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        /// <summary>Every line the host logged, formatted.</summary>
        public IReadOnlyList<string> Lines => [.. _lines];

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _lines);

        /// <inheritdoc />
        public void Dispose()
        {
        }

        private sealed class Sink(string category, ConcurrentQueue<string> lines) : ILogger
        {
            /// <inheritdoc />
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <inheritdoc />
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)}");
                if (exception is not null)
                {
                    lines.Enqueue($"{logLevel} {category}: {exception}");
                }
            }
        }
    }
}
