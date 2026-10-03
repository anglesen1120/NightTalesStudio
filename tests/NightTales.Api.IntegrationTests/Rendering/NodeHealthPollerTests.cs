using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task SecondInstance_RefreshesItsOwnCacheButWritesNoRegistryState()
    {
        // CR-1 (11 README F10): the lock gates registry writes only. A non-leader still pulls into its own
        // process-local cache — its selector reads nothing else — but heartbeats and decides nothing.
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active);
        using var holder = CreateHarness(clock);
        using var second = CreateHarness(clock);
        holder.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));
        second.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));

        (await holder.Poller.TickAsync(Ct)).ShouldBeTrue("the first instance takes the advisory lock (Q5)");
        holder.Factory.Requests("alpha").Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(30));
        (await second.Poller.TickAsync(Ct)).ShouldBeFalse("the lock is held elsewhere, so this instance is not the leader");
        second.Factory.Requests("alpha").Count.ShouldBe(1, "a non-leader still pulls the node for its own cache");
        second.Cache.TryGet("alpha", out var cached).ShouldBeTrue();
        cached!.PulledAt.ShouldBe(clock.UtcNow);
        (await RowAsync("alpha")).LastHeartbeatAt.ShouldBe(Start, "only the lock holder heartbeats");

        await using var scope = second.Services.CreateAsyncScope();
        var picked = await scope.ServiceProvider.GetRequiredService<INodeSelector>().PickAsync(Ct);
        picked.IsError.ShouldBeFalse("the non-leader's selector dispatches from the cache its own poller filled");
        picked.Value.Name.ShouldBe("alpha");

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
    public async Task Up_RequiresConsecutiveAnswers_AcrossADrain()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Down, heartbeat: Start);
        using var harness = CreateHarness(clock);
        harness.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));

        // The first of the two consecutive answers the revival needs (F12).
        clock.Advance(TimeSpan.FromSeconds(200));
        await harness.Poller.PollOnceAsync(Ct);
        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Down);

        // The operator drains the node; while it drains it answers nothing, so the pull above is no
        // longer "consecutive" with whatever comes after the drain.
        await MarkDrainingAsync("alpha");
        harness.Factory.Script("alpha", new ScriptedHttpStep(Failure: new HttpRequestException("unreachable")));
        await harness.Poller.PollOnceAsync(Ct);
        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Draining);

        // The node leaves the configuration, so the seeder strand-marks the row down (UC10).
        harness.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));
        await StrandAllNodesAsync(clock);
        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Down);

        await harness.Poller.PollOnceAsync(Ct);
        (await RowAsync("alpha")).State.ShouldBe(
            WorkerNodeState.Down,
            "two consecutive answers revive a down node: the pre-drain pull is not one of them (F12)");

        await harness.Poller.PollOnceAsync(Ct);
        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Active);
    }

    [Fact]
    public async Task Http401WithParseableBody_RecordsReasonButIsNotAnAnswer()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active, heartbeat: Start);
        await SeedNodeAsync("beta", WorkerNodeState.Active, heartbeat: Start);
        using var harness = CreateHarness(clock);
        harness.Factory.Script(
            "alpha",
            new ScriptedHttpStep(
                HttpStatusCode.Unauthorized,
                RenderNodeHealthBodies.Degraded(NodeHealthReasons.MptUnauthorized)));
        harness.Factory.Script(
            "beta",
            new ScriptedHttpStep(HttpStatusCode.Unauthorized, RenderNodeHealthBodies.Unparseable));

        var reasons = new List<(string Node, string Reason)>();
        var up = new Dictionary<string, int>(StringComparer.Ordinal);
        using var listener = MetricsListener(harness.Metrics, reasons, up);

        clock.Advance(TimeSpan.FromSeconds(200));
        await harness.Poller.PollOnceAsync(Ct);
        listener.RecordObservableInstruments();

        // A 401 is not an answer: no heartbeat, no cache entry and no revival credit — the row goes down
        // like any silence (task §"Failure handling").
        var alpha = await RowAsync("alpha");
        alpha.LastHeartbeatAt.ShouldBe(Start, "a 401 is no answer, so nothing heartbeats");
        alpha.State.ShouldBe(WorkerNodeState.Down, "120 s without a valid answer sends an active node down");
        harness.Cache.TryGet("alpha", out _).ShouldBeFalse("a rejected body is never cached as an answer");
        up["alpha"].ShouldBe(0, "a 401 does not make the node 'up'");

        // Its body parsed, so the node's own reason is counted instead of the local node_unreachable.
        reasons.ShouldContain(("alpha", NodeHealthReasons.MptUnauthorized));
        reasons.ShouldNotContain(
            reason => reason.Node == "alpha" && reason.Reason == LocalNodeHealthReasons.NodeUnreachable,
            "a parseable 401 body supplies the reason");

        // An unparseable 401 body has no reason to read, so it keeps the local label.
        reasons.ShouldContain(("beta", LocalNodeHealthReasons.NodeUnreachable));
    }

    [Fact]
    public async Task Draining_WithNoAnswer_StaysDraining()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Draining, heartbeat: Start);
        using var harness = CreateHarness(clock);
        harness.Factory.Script("alpha", new ScriptedHttpStep(Failure: new HttpRequestException("unreachable")));

        clock.Advance(TimeSpan.FromSeconds(200));
        await harness.Poller.PollOnceAsync(Ct);

        var row = await RowAsync("alpha");
        row.State.ShouldBe(WorkerNodeState.Draining, "health never ends a drain, not even on silence (D12)");
        row.LastHeartbeatAt.ShouldBe(Start, "a node that does not answer does not heartbeat");
    }

    [Fact]
    public async Task SecondInstance_LogsLockNotHeldOnce()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active);
        var sink = new CapturingLoggerProvider();

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(sink));
        using var holder = CreateHarness(clock);
        using var second = CreateHarness(clock, logger: loggerFactory.CreateLogger<NodeHealthPoller>());
        holder.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));
        second.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));

        (await holder.Poller.TickAsync(Ct)).ShouldBeTrue();

        (await second.Poller.TickAsync(Ct)).ShouldBeFalse();
        (await second.Poller.TickAsync(Ct)).ShouldBeFalse();
        (await second.Poller.TickAsync(Ct)).ShouldBeFalse();

        sink.Messages.Count(message => message.Contains("lock-not-held", StringComparison.Ordinal))
            .ShouldBe(1, "a second Worker on the same database logs once, not on every tick (plan §7 step 1)");
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

    [Fact]
    public async Task StalledBody_IsNoAnswer_AtThePullDeadline()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active, heartbeat: Start);
        await SeedNodeAsync("beta", WorkerNodeState.Active, heartbeat: Start);
        using var harness = CreateHarness(clock);
        // The client's own timeout is far past the pull deadline, so only the pull's linked deadline can
        // end the body stall — the gap ResponseHeadersRead leaves (review F1/SEC-11.3.3-02).
        harness.Factory.Timeout = TimeSpan.FromSeconds(30);
        harness.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, StallBody: true));
        harness.Factory.Script("beta", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));

        clock.Advance(TimeSpan.FromSeconds(200));

        var tick = harness.Poller.PollOnceAsync(Ct);
        var completed = await Task.WhenAny(tick, Task.Delay(TimeSpan.FromSeconds(30), Ct));
        completed.ShouldBeSameAs(tick, "a body that never finishes must be cut by the pull deadline, not hang the tick");
        await tick;

        var alpha = await RowAsync("alpha");
        alpha.State.ShouldBe(WorkerNodeState.Down, "a stalled body is no valid answer");
        alpha.LastHeartbeatAt.ShouldBe(Start);

        var beta = await RowAsync("beta");
        beta.State.ShouldBe(WorkerNodeState.Active);
        beta.LastHeartbeatAt.ShouldBe(clock.UtcNow, "one stalled node must not hold the tick back");
    }

    [Fact]
    public async Task OversizedBody_IsNoAnswer_DeclaredOrChunked()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active, heartbeat: Start);
        await SeedNodeAsync("beta", WorkerNodeState.Active, heartbeat: Start);
        await SeedNodeAsync("gamma", WorkerNodeState.Active, heartbeat: Start);
        using var harness = CreateHarness(clock);

        // Bodies that parse but run past the 64 KiB cap: alpha declares it up front, beta hides it behind
        // a chunked answer, gamma stays inside the cap as the control (SEC-11.3.3-01).
        harness.Factory.Script(
            "alpha",
            new ScriptedHttpStep(HttpStatusCode.OK, PadBodyToSize(RenderNodeHealthBodies.Ok(), 70 * 1024)));
        harness.Factory.Script(
            "beta",
            new ScriptedHttpStep(
                HttpStatusCode.OK,
                PadBodyToSize(RenderNodeHealthBodies.Ok(), 70 * 1024),
                OmitContentLength: true));
        harness.Factory.Script("gamma", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));

        clock.Advance(TimeSpan.FromSeconds(200));
        await harness.Poller.PollOnceAsync(Ct);

        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Down, "a declared over-cap body is refused");
        (await RowAsync("beta")).State.ShouldBe(WorkerNodeState.Down, "an uncounted over-cap body is refused while reading");

        var gamma = await RowAsync("gamma");
        gamma.State.ShouldBe(WorkerNodeState.Active);
        gamma.LastHeartbeatAt.ShouldBe(clock.UtcNow, "a body inside the cap still answers");
    }

    [Fact]
    public async Task InvalidBodyFields_IsNoAnswer_ForThatNodeOnly()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active, heartbeat: Start);
        await SeedNodeAsync("beta", WorkerNodeState.Active, heartbeat: Start);
        await SeedNodeAsync("gamma", WorkerNodeState.Active, heartbeat: Start);
        using var harness = CreateHarness(clock);

        // Both bodies are parseable JSON shaped like render-node-health.v1 but with invalid known
        // fields: alpha's reason is blank (the reason counters throw on it — SEC-11.3.3-03), beta's
        // capacity block is null. Neither may kill the tick or poison gamma's update.
        harness.Factory.Script(
            "alpha",
            new ScriptedHttpStep(HttpStatusCode.ServiceUnavailable, RenderNodeHealthBodies.Degraded("")));
        harness.Factory.Script("beta", new ScriptedHttpStep(HttpStatusCode.OK, BodyWithNullBlock("capacity")));
        harness.Factory.Script("gamma", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));

        clock.Advance(TimeSpan.FromSeconds(200));
        await harness.Poller.PollOnceAsync(Ct);

        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Down, "a blank reason is no valid answer");
        (await RowAsync("beta")).State.ShouldBe(WorkerNodeState.Down, "a null required block is no valid answer");

        var gamma = await RowAsync("gamma");
        gamma.State.ShouldBe(WorkerNodeState.Active);
        gamma.LastHeartbeatAt.ShouldBe(clock.UtcNow, "one node's invalid body must not stop another's heartbeat");
    }

    [Fact]
    public async Task LostLock_SkipsTheTick_AndReacquiresOnTheNext()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active);
        var lockApplication = $"nts-qa-lock-{Guid.CreateVersion7():N}";
        using var harness = CreateHarness(clock, lockConnectionString: $"{Database.ConnectionString};Application Name={lockApplication}");
        harness.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok()));

        (await harness.Poller.TickAsync(Ct)).ShouldBeTrue("the first instance takes the advisory lock (Q5)");
        harness.Factory.Requests("alpha").Count.ShouldBe(1);

        // The server kills the lock session: the advisory lock is gone, but the client only learns it when
        // the connection is used (review F2). Until then the row must not be touched.
        await TerminateLockSessionAsync(lockApplication);

        (await harness.Poller.TickAsync(Ct)).ShouldBeFalse(
            "the tick that discovers the lost lock re-takes it but applies nothing from the gap");
        harness.Factory.Requests("alpha").Count.ShouldBe(1, "a lost lock must not drive a tick");

        (await harness.Poller.TickAsync(Ct)).ShouldBeTrue("the next tick polls on the re-taken lock");
        harness.Factory.Requests("alpha").Count.ShouldBe(2);
        (await RowAsync("alpha")).LastHeartbeatAt.ShouldBe(clock.UtcNow);
    }

    [Fact]
    public async Task FailedAcquisition_ReturnsTheCandidateToThePool()
    {
        // A one-slot pool makes an undisposed candidate observable: if a failure between the open and the
        // lock assignment leaks the connection, the next acquisition can never open one (review F4).
        await using var guard = new NodeHealthPollerLock(
            $"{Database.ConnectionString};Maximum Pool Size=1;Timeout=3")
        {
            OnCandidateOpened = async (candidate, _) =>
            {
                // A real post-open failure: kill the candidate's backend so the lock command runs on a
                // dead session and throws.
                await using var probe = candidate.CreateCommand();
                probe.CommandText = "SELECT pg_backend_pid()";
                var pid = (int)(await probe.ExecuteScalarAsync(Ct))!;
                await TerminateBackendAsync(pid);
            },
        };

        var failure = await Record.ExceptionAsync(() => guard.TryAcquireAsync(Ct));
        failure.ShouldNotBeNull("the lock command cannot run on a terminated session");
        guard.IsHeld.ShouldBeFalse("a failed acquisition holds nothing");

        guard.OnCandidateOpened = null;
        (await guard.TryAcquireAsync(Ct)).ShouldBe(
            NodeHealthPollerLock.LockAttempt.Held,
            "the first candidate's slot is back in the pool, so the re-acquisition can open one");
    }

    [Fact]
    public async Task OperatorDrainMidTick_WinsOverTheHealthDecision()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active, heartbeat: Start);
        using var harness = CreateHarness(clock);
        // The registry read sees `active` and the pull fails, so the tick decides `down`; but the
        // operator's drain lands mid-pull, so the tracked row is `draining` when the decision is
        // applied (review F3 — health must not stomp it).
        harness.Factory.Script(
            "alpha",
            new ScriptedHttpStep(
                Failure: new HttpRequestException("unreachable"),
                OnRequest: async _ => await MarkDrainingAsync("alpha")));

        clock.Advance(TimeSpan.FromSeconds(200));
        await harness.Poller.PollOnceAsync(Ct);

        (await RowAsync("alpha")).State.ShouldBe(
            WorkerNodeState.Draining,
            "the operator's write between the registry read and the apply wins over health (D12)");
    }

    [Fact]
    public async Task DriftAlert_RequiresThreeConsecutiveMismatches()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        await SeedNodeAsync("alpha", WorkerNodeState.Active);
        using var harness = CreateHarness(clock);

        var drifts = new List<string>();
        using var listener = MetricsListener(
            harness.Metrics, new List<(string, string)>(), new Dictionary<string, int>(), drifts);

        harness.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok(activeTasks: 2)));
        await harness.Poller.PollOnceAsync(Ct);
        await harness.Poller.PollOnceAsync(Ct);

        // A silent pull is not a mismatch: it breaks the streak instead of carrying it over (review F6).
        harness.Factory.Script("alpha", new ScriptedHttpStep(Failure: new HttpRequestException("unreachable")));
        await harness.Poller.PollOnceAsync(Ct);

        harness.Factory.Script("alpha", new ScriptedHttpStep(HttpStatusCode.OK, RenderNodeHealthBodies.Ok(activeTasks: 2)));
        await harness.Poller.PollOnceAsync(Ct);
        await harness.Poller.PollOnceAsync(Ct);
        drifts.ShouldBeEmpty("two mismatches around a silent pull are not three consecutive mismatches");

        await harness.Poller.PollOnceAsync(Ct);
        drifts.ShouldBe(["alpha"], "the run restarted after the silent pull, so this is its third member");
    }

    /// <summary>A valid body padded past the cap with an unknown member the tolerant reader skips (D4).</summary>
    private static string PadBodyToSize(string body, int minimumBytes)
    {
        var pad = ",\"padding\":\"" + new string('x', minimumBytes) + "\"}";
        return body[..^1] + pad;
    }

    /// <summary>The body's required block set to JSON <c>null</c> instead of its object.</summary>
    private static string BodyWithNullBlock(string member)
    {
        var document = JsonNode.Parse(RenderNodeHealthBodies.Ok())!.AsObject();
        document[member] = null;

        return document.ToJsonString();
    }

    /// <summary>Kills the lock session's backend by application name, then waits for it to be gone.</summary>
    private async Task TerminateLockSessionAsync(string applicationName)
    {
        var pids = await Database.QueryStringsAsync(
            "SELECT pid::text FROM pg_stat_activity "
            + $"WHERE application_name = '{applicationName}' AND pid <> pg_backend_pid()",
            Ct);
        pids.ShouldHaveSingleItem("the lock connection is the only session with that application name");

        await TerminateBackendAsync(int.Parse(pids[0], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Terminates one backend and waits until <c>pg_stat_activity</c> stops listing it: the advisory lock
    /// it owned is only free once the session is really dead.
    /// </summary>
    private async Task TerminateBackendAsync(int pid)
    {
        var terminated = await Database.QueryStringsAsync(
            $"SELECT pg_terminate_backend({pid})::text",
            Ct);
        terminated.ShouldContain("true", "the test must actually have killed the session");

        for (var attempt = 0; attempt < 250; attempt++)
        {
            var remaining = await Database.QueryStringsAsync(
                $"SELECT count(*)::text FROM pg_stat_activity WHERE pid = {pid}",
                Ct);
            if (remaining is ["0"])
            {
                return;
            }

            await Task.Delay(20, Ct);
        }

        throw new InvalidOperationException($"backend {pid} did not die within 5 s of its termination");
    }

    private Harness CreateHarness(
        FakeClock clock,
        RenderNodesOptions? options = null,
        ILogger<NodeHealthPoller>? logger = null,
        string? lockConnectionString = null)
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
        var lockGuard = new NodeHealthPollerLock(lockConnectionString ?? Database.ConnectionString);
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

    /// <summary>The operator's drain, applied directly to the row as <c>nodes set-state</c> would.</summary>
    private async Task MarkDrainingAsync(string name)
    {
        await using var db = Database.CreateAppDbContext();
        var node = await db.WorkerNodes.SingleAsync(row => row.Name == name, Ct);
        node.SetOperatorState(WorkerNodeState.Draining);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>What the seeder does to a row whose node left <c>RenderNodes:Nodes</c> (UC10).</summary>
    private async Task StrandAllNodesAsync(FakeClock clock)
    {
        await using var db = Database.CreateAppDbContext();
        var seeder = new WorkerNodeSeeder(
            db,
            Options.Create(new RenderNodesOptions()),
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            clock,
            NullLogger<WorkerNodeSeeder>.Instance);

        (await seeder.RegisterAsync(Ct)).IsError.ShouldBeFalse();
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

        public IServiceProvider Services => host.Services;

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
