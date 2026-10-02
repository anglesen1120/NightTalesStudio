using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Seeding;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Rendering;
using NightTales.RenderNode.Contract;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Rendering;
using NightTales.TestKit.Time;
using NightTales.Worker.Rendering;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The Worker's health poller of 11 README D13/F10/F12 (11.3.3-T1 plan §7/§9): driven one tick at a time
/// with a <see cref="FakeClock"/> and a scripted node, it heartbeats and caches on any valid answer (200 or
/// 503 with a body), sends an <c>active</c> node <c>down</c> after <c>DownAfterSeconds</c>, revives a
/// <c>down</c> node after exactly two good pulls, never touches <c>draining</c>, counts its reasons, warns
/// once per drift run, polls only while it holds the advisory lock, and never logs a URL or a key.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NodeHealthPollerTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
    private const string Pin = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string ApiKeyValue = "poller-key-value";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Http200_StoresHeartbeatAndCache()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active);
        using var harness = CreateHarness(clock);
        harness.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok(activeTasks: 1)));

        await harness.Poller.PollOnceAsync(Ct);

        var row = await RowAsync("alpha");
        row.LastHeartbeatAt.ShouldBe(Start, "a valid answer is the heartbeat (D13)");
        row.State.ShouldBe(WorkerNodeState.Active);

        harness.Cache.TryGet("alpha", out var snapshot).ShouldBeTrue("the answer is cached for the selector (Q1)");
        snapshot!.HttpOk.ShouldBeTrue();
        snapshot.Status.ShouldBe("ok");
        snapshot.ActiveTasks.ShouldBe(1);
        harness.Factory.Requests("alpha").ShouldHaveSingleItem().Path.ShouldBe("/v1/health");
    }

    [Fact]
    public async Task Http503WithValidBody_CountsAsAnswer()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active);
        using var harness = CreateHarness(clock);
        harness.Factory.Script(
            "alpha",
            new ScriptedHttpStep(
                HttpStatusCode.ServiceUnavailable,
                RenderNodeHealthBodies.Degraded(NodeHealthReasons.DiskLow)));

        await harness.Poller.PollOnceAsync(Ct);

        (await RowAsync("alpha")).LastHeartbeatAt.ShouldBe(Start, "503 with a parseable body is still an answer");

        harness.Cache.TryGet("alpha", out var snapshot).ShouldBeTrue();
        snapshot!.HttpOk.ShouldBeFalse("the node answered but is not dispatchable");
        snapshot.Reasons.ShouldBe([NodeHealthReasons.DiskLow]);
    }

    [Fact]
    public async Task Unparseable200_CountsAsNoAnswer()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active, heartbeat: Start);
        using var harness = CreateHarness(clock);
        harness.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Unparseable));

        clock.Advance(TimeSpan.FromSeconds(200));
        await harness.Poller.PollOnceAsync(Ct);

        var row = await RowAsync("alpha");
        row.LastHeartbeatAt.ShouldBe(Start, "an unparseable body is not an answer");
        row.State.ShouldBe(WorkerNodeState.Down, "after DownAfterSeconds without an answer an active node goes down");
    }

    [Fact]
    public async Task Timeout_CountsAsNoAnswer()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active, heartbeat: Start);
        using var harness = CreateHarness(clock);
        harness.Factory.Timeout = TimeSpan.FromMilliseconds(150);
        harness.Factory.Script(
            "alpha",
            new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok(), Delay: TimeSpan.FromSeconds(30)));

        clock.Advance(TimeSpan.FromSeconds(200));
        await harness.Poller.PollOnceAsync(Ct);

        var row = await RowAsync("alpha");
        row.LastHeartbeatAt.ShouldBe(Start);
        row.State.ShouldBe(WorkerNodeState.Down, "a per-pull timeout is not an answer (D13)");
    }

    [Fact]
    public async Task Down_After120sWithoutAnswer()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active, heartbeat: Start);
        using var harness = CreateHarness(clock);
        harness.Factory.Script("alpha", new ScriptedHttpStep(Failure: new HttpRequestException("node unreachable")));

        await harness.Poller.PollOnceAsync(Ct);
        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Active, "0 s since the last answer is inside the window");

        clock.Advance(TimeSpan.FromSeconds(119));
        await harness.Poller.PollOnceAsync(Ct);
        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Active, "119 s is still inside DownAfterSeconds");

        clock.Advance(TimeSpan.FromSeconds(1));
        await harness.Poller.PollOnceAsync(Ct);
        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Down, "120 s without a valid answer (D13/F12)");
    }

    [Fact]
    public async Task Up_AfterTwoConsecutiveGoodPulls()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Down, heartbeat: Start);
        using var harness = CreateHarness(clock);
        harness.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));

        clock.Advance(TimeSpan.FromSeconds(200));
        await harness.Poller.PollOnceAsync(Ct);
        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Down, "one good pull is not enough (F12 hysteresis)");

        await harness.Poller.PollOnceAsync(Ct);
        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Active, "exactly two consecutive good pulls revive it");
    }

    [Fact]
    public async Task Draining_NeverChangedByPoller()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Draining, heartbeat: Start);
        using var harness = CreateHarness(clock);
        harness.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));

        clock.Advance(TimeSpan.FromSeconds(200));
        await harness.Poller.PollOnceAsync(Ct);

        var row = await RowAsync("alpha");
        row.State.ShouldBe(WorkerNodeState.Draining, "a drain is operator-only; health never cancels it (D12)");
        row.LastHeartbeatAt.ShouldBe(clock.UtcNow, "a draining node still heartbeats");
    }

    [Fact]
    public async Task SecondInstance_DoesNotPoll()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active);
        using var holder = CreateHarness(clock);
        using var second = CreateHarness(clock);
        holder.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));
        second.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));

        (await holder.Poller.TickAsync(Ct)).ShouldBeTrue("the first instance takes the advisory lock (Q5)");
        holder.Factory.Requests("alpha").Count.ShouldBe(1);

        (await second.Poller.TickAsync(Ct)).ShouldBeFalse("the lock is held, so the second instance does not poll");
        second.Factory.Requests("alpha").ShouldBeEmpty("a non-holder polls nothing and must not error");
        (await holder.Poller.TickAsync(Ct)).ShouldBeTrue("the holder keeps polling on the next tick");
    }

    [Fact]
    public async Task EmitsHealthReasonCounters()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active);
        await SeedNodeAsync("beta", WorkerNodeState.Active);
        await SeedNodeAsync("gamma", WorkerNodeState.Active);
        using var harness = CreateHarness(clock);
        harness.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));
        harness.Factory.Script(
            "beta",
            new ScriptedHttpStep(
                HttpStatusCode.ServiceUnavailable,
                RenderNodeHealthBodies.Degraded(NodeHealthReasons.DiskLow, NodeHealthReasons.GpuUnavailable)));
        harness.Factory.Script("gamma", new ScriptedHttpStep(Failure: new HttpRequestException("unreachable")));

        var reasons = new List<(string Node, string Reason)>();
        var up = new Dictionary<string, int>(StringComparer.Ordinal);
        using var listener = MetricsListener(harness.Metrics, reasons, up);

        await harness.Poller.PollOnceAsync(Ct);
        listener.RecordObservableInstruments();

        reasons.ShouldContain(("beta", NodeHealthReasons.DiskLow));
        reasons.ShouldContain(("beta", NodeHealthReasons.GpuUnavailable));
        reasons.ShouldContain(("gamma", LocalNodeHealthReasons.NodeUnreachable));
        reasons.ShouldNotContain(reason => reason.Node == "alpha", "a clean answer records no reason");

        up["alpha"].ShouldBe(1);
        up["beta"].ShouldBe(1, "a 503 with a valid body is an answer, so the node is 'up' (dispatchability is T2's predicate)");
        up["gamma"].ShouldBe(0, "a node that never answered is not up");
    }

    [Fact]
    public async Task DriftAlert_AfterThreePulls()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active);
        using var harness = CreateHarness(clock);
        harness.Factory.Script(
            "alpha",
            new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok(activeTasks: 2)));

        var drifts = new List<string>();
        using var listener = MetricsListener(harness.Metrics, new List<(string, string)>(), new Dictionary<string, int>(), drifts);

        await harness.Poller.PollOnceAsync(Ct);
        await harness.Poller.PollOnceAsync(Ct);
        drifts.ShouldBeEmpty("two mismatches are below the DriftAlertPulls threshold (3)");

        await harness.Poller.PollOnceAsync(Ct);
        drifts.ShouldBe(["alpha"], "three consecutive mismatches warn once for the node");

        await harness.Poller.PollOnceAsync(Ct);
        drifts.Count.ShouldBe(1, "a drift run is counted once, not on every later pull");
        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Active, "drift never changes a state");
    }

    [Fact]
    public async Task PullFailure_LogsNameAndClassOnly()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active);
        var sink = new CapturingLoggerProvider();

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(sink));
        using var harness = CreateHarness(clock, logger: loggerFactory.CreateLogger<NodeHealthPoller>());
        harness.Factory.Script(
            "alpha",
            new ScriptedHttpStep(
                Failure: new HttpRequestException(
                    $"connect to https://alpha.example/v1/health failed (x-api-key {ApiKeyValue})")));

        await harness.Poller.PollOnceAsync(Ct);

        sink.Messages.ShouldNotBeEmpty("the failure is logged so an operator sees the node");
        sink.Messages.ShouldAllBe(message => !message.Contains(ApiKeyValue, StringComparison.Ordinal));
        sink.Messages.ShouldAllBe(message => !message.Contains("https://", StringComparison.Ordinal));
        sink.Messages.ShouldContain(message => message.Contains("alpha", StringComparison.Ordinal));
    }

    private Harness CreateHarness(
        FakeClock clock,
        RenderNodesOptions? options = null,
        ILogger<NodeHealthPoller>? logger = null)
    {
        var host = RenderNodeTestHost.Build(
            Database,
            clock,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"ConnectionStrings:{NightTales.Infrastructure.Persistence.PersistenceOptions.ConnectionStringName}"] =
                    Database.ConnectionString,
            });

        var factory = new FakeRenderNodeConnectionFactory();
        var lockGuard = new NodeHealthPollerLock(Database.ConnectionString);
        var poller = new NodeHealthPoller(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options ?? new RenderNodesOptions()),
            factory,
            host.Services.GetRequiredService<INodeHealthCache>(),
            host.Services.GetRequiredService<NodeMetrics>(),
            clock,
            lockGuard,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NodeHealthPoller>.Instance);

        return new Harness(
            host,
            factory,
            host.Services.GetRequiredService<INodeHealthCache>(),
            host.Services.GetRequiredService<NodeMetrics>(),
            lockGuard,
            poller);
    }

    private async Task SeedNodeAsync(string name, WorkerNodeState state, DateTimeOffset? heartbeat = null)
    {
        await using var db = Database.CreateAppDbContext();
        var node = WorkerNode.Create(name, new Uri($"https://{name}.example/"), capacity: 2, tlsCertSha256: Pin, gpuModel: null).Value;

        if (heartbeat is { } at)
        {
            node.RecordHeartbeat(at);
        }

        if (state == WorkerNodeState.Down)
        {
            node.MarkDown(Start);
        }
        else if (state == WorkerNodeState.Draining)
        {
            node.SetOperatorState(WorkerNodeState.Draining);
        }

        db.WorkerNodes.Add(node);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<WorkerNode> RowAsync(string name)
    {
        await using var db = Database.CreateAppDbContext();

        return await db.WorkerNodes.SingleAsync(node => node.Name == name, Ct);
    }

    private static MeterListener MetricsListener(
        NodeMetrics metrics,
        List<(string Node, string Reason)> reasons,
        Dictionary<string, int> up,
        List<string>? drifts = null)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter))
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (string.Equals(instrument.Name, NodeMetrics.HealthReasonsInstrumentName, StringComparison.Ordinal))
            {
                reasons.Add((Tag(tags, NodeMetrics.NodeTagName), Tag(tags, NodeMetrics.ReasonTagName)));
            }
            else if (string.Equals(instrument.Name, NodeMetrics.ActiveTasksDriftInstrumentName, StringComparison.Ordinal))
            {
                drifts?.Add(Tag(tags, NodeMetrics.NodeTagName));
            }
        });

        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, _) =>
        {
            if (string.Equals(instrument.Name, NodeMetrics.UpInstrumentName, StringComparison.Ordinal))
            {
                up[Tag(tags, NodeMetrics.NodeTagName)] = measurement;
            }
        });

        listener.Start();

        return listener;
    }

    private static string Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
            {
                return tag.Value as string ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>The poller and everything it needs, disposed together (11.3.3-T1 plan §7).</summary>
    private sealed class Harness(
        RenderNodeTestHost host,
        FakeRenderNodeConnectionFactory factory,
        INodeHealthCache cache,
        NodeMetrics metrics,
        NodeHealthPollerLock lockGuard,
        NodeHealthPoller poller) : IDisposable
    {
        public FakeRenderNodeConnectionFactory Factory { get; } = factory;

        public INodeHealthCache Cache { get; } = cache;

        public NodeMetrics Metrics { get; } = metrics;

        public NodeHealthPoller Poller { get; } = poller;

        public void Dispose()
        {
            Poller.Dispose();
            lockGuard.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Factory.Dispose();
            host.Dispose();
        }
    }

    /// <summary>Keeps every log line the poller rendered, so a scan can prove what it never contains (S4).</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_gate, _messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(Lock gate, List<string> messages) : ILogger
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
                var line = formatter(state, exception);

                lock (gate)
                {
                    messages.Add(line);
                }
            }
        }
    }
}
