using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NightTales.Api.Realtime;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Realtime;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Realtime;

/// <summary>
/// <c>RealtimeListenerService</c> (09.8.1-T1 Tests table, 09 README BD11): one <c>LISTEN</c> session per
/// instance over both channels, the fan-out to this instance's own sockets, the reconnect schedule of
/// 1-2-4-8-16-30 s, the <c>security</c> abort and the per-role backstop that reads no cache (WT37).
/// </summary>
/// <remarks>
/// <para>
/// Delivery is proven end to end — the production host, a real PostgreSQL, a real <c>pg_notify</c> and a
/// real socket — because that chain is what "committed delivers, rolled back does not" means. The loop's
/// own behaviour (reconnect, backstop, unknown event) is driven directly with hand-written fakes
/// (09 README D13), and every assertion there is ordered: a message that follows the one under test is
/// what proves the negative half, so no test sleeps.
/// </para>
/// </remarks>
public sealed class RealtimeListenerTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task Listener_Forwards_Notify_To_Local_Connections_Only()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();
        var stranger = Guid.CreateVersion7();

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);
        await using var client = await harness.ConnectAsync(owner, PlatformRole.User, ct);

        using var bound = RealtimeHarness.Bounded(ct);
        await harness.ListenerReadyAsync(bound.Token);

        var episodeId = Guid.CreateVersion7();
        await using var context = Database.CreateAppDbContext();
        var notifier = harness.NotifierOn(context);

        // No socket of this instance belongs to `stranger`, so the message is a no-op here...
        await notifier.NotifyAsync(
            stranger,
            new RealtimeEvent(RealtimeEventName.PublicationUpdated, new RealtimeIds()),
            bound.Token);

        // ...and the owner's event is the first thing the owner's socket sees.
        await notifier.NotifyAsync(
            owner,
            new RealtimeEvent(RealtimeEventName.EpisodeUpdated, new RealtimeIds(EpisodeId: episodeId)),
            bound.Token);

        var received = await client.ReceiveAsync(bound.Token);

        received.Target.ShouldBe(nameof(RealtimeEventName.EpisodeUpdated));
        received.Event.ShouldBe("episode_updated");
        received.IdOf("episodeId").ShouldBe(episodeId);
    }

    [Fact]
    public async Task Notify_Inside_A_Rolled_Back_Transaction_Is_Not_Delivered()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);
        await using var client = await harness.ConnectAsync(owner, PlatformRole.User, ct);

        using var bound = RealtimeHarness.Bounded(ct);
        await harness.ListenerReadyAsync(bound.Token);

        await using var context = Database.CreateAppDbContext();
        var notifier = harness.NotifierOn(context);

        await using (var transaction = await context.Database.BeginTransactionAsync(bound.Token))
        {
            await notifier.NotifyAsync(
                owner,
                new RealtimeEvent(RealtimeEventName.PublicationUpdated, new RealtimeIds()),
                bound.Token);

            await transaction.RollbackAsync(bound.Token);
        }

        // The sentinel is a later, committed event: it can only be the first message on the socket if the
        // rolled-back one never left the database (D5 — a change is never announced unless it committed).
        var episodeId = Guid.CreateVersion7();
        await notifier.NotifyAsync(
            owner,
            new RealtimeEvent(RealtimeEventName.EpisodeUpdated, new RealtimeIds(EpisodeId: episodeId)),
            bound.Token);

        var received = await client.ReceiveAsync(bound.Token);

        received.Target.ShouldBe(nameof(RealtimeEventName.EpisodeUpdated));
        received.IdOf("episodeId").ShouldBe(episodeId);
    }

    [Fact]
    public async Task Notify_Inside_A_Committed_Transaction_Is_Delivered_Once()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);
        await using var client = await harness.ConnectAsync(owner, PlatformRole.User, ct);

        using var bound = RealtimeHarness.Bounded(ct);
        await harness.ListenerReadyAsync(bound.Token);

        await using var context = Database.CreateAppDbContext();
        var notifier = harness.NotifierOn(context);

        var episodeId = Guid.CreateVersion7();

        await using (var transaction = await context.Database.BeginTransactionAsync(bound.Token))
        {
            await notifier.NotifyAsync(
                owner,
                new RealtimeEvent(RealtimeEventName.EpisodeUpdated, new RealtimeIds(EpisodeId: episodeId)),
                bound.Token);

            await transaction.CommitAsync(bound.Token);
        }

        await notifier.NotifyAsync(
            owner,
            new RealtimeEvent(RealtimeEventName.CreditBalanceChanged, new RealtimeIds()),
            bound.Token);

        var committed = await client.ReceiveAsync(bound.Token);
        committed.Target.ShouldBe(nameof(RealtimeEventName.EpisodeUpdated));
        committed.IdOf("episodeId").ShouldBe(episodeId);

        // Exactly once: the sentinel is next, so the committed event was not delivered a second time.
        var sentinel = await client.ReceiveAsync(bound.Token);
        sentinel.Target.ShouldBe(nameof(RealtimeEventName.CreditBalanceChanged));
    }

    [Fact]
    public async Task Security_Channel_Aborts_That_Users_Connections_Only()
    {
        var ct = TestContext.Current.CancellationToken;
        var revoked = Guid.CreateVersion7();
        var untouched = Guid.CreateVersion7();

        await using var harness = RealtimeHarness.Start(Database.ConnectionString);
        await using var revokedClient = await harness.ConnectAsync(revoked, PlatformRole.User, ct);
        await using var untouchedClient = await harness.ConnectAsync(untouched, PlatformRole.User, ct);

        using var bound = RealtimeHarness.Bounded(ct);
        await harness.ListenerReadyAsync(bound.Token);
        await harness.WaitForConnectionsAsync(2, bound.Token);

        // 09-backend-api L218: a revocation NOTIFYs `security` with the user id alone.
        await Database.ExecuteAsync(
            $"SELECT pg_notify('{RealtimeNotification.SecurityChannel}', '{revoked:D}')",
            bound.Token);

        (await revokedClient.ClosedAsync(bound.Token)).ShouldBeTrue(
            "the revoked user's connection is aborted on the receiving instance");

        // The other user's socket is untouched: its own event still arrives, so the abort was per user.
        await harness.SendAsync(
            untouched,
            RealtimeEventName.CreditBalanceChanged,
            RealtimeHarness.Body(untouched, RealtimeEventName.CreditBalanceChanged),
            bound.Token);

        var received = await untouchedClient.ReceiveAsync(bound.Token);
        received.Target.ShouldBe(nameof(RealtimeEventName.CreditBalanceChanged));

        harness.Resolve<RealtimeConnectionRegistry>().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Slow_Socket_Does_Not_Hold_The_Security_Abort_Or_The_Next_Dispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var metrics = new RealtimeMetrics();
        using var sender = new BlockingGroupSender();
        var connection = new ScriptedListenerConnection();
        var revoked = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var registry = NewRegistry(clock, metrics);
        var revokedSocket = new RecordingHubCallerContext("blocked-socket");
        registry.Add(revoked, PlatformRole.User, revokedSocket);

        var service = BuildListener(
            new ScriptedListenerConnectionFactory(_ => connection),
            sender,
            registry,
            metrics,
            clock,
            new RecordingListenerDelay(completeAfter: int.MaxValue),
            NullLogger<RealtimeListenerService>.Instance);

        using var bound = RealtimeHarness.Bounded(ct);

        await service.StartAsync(ct);
        await service.ListeningAsync(bound.Token);

        // The one delivery consumer picks the first event up and its socket never accepts the write —
        // the back-pressure the loop must not feel (F2)...
        connection.Deliver(Event(revoked));
        await sender.WaitForAsync(1, bound.Token);

        // ...so the revocation that arrives next is dispatched at once, and the event after it is queued
        // rather than lost behind the blocked socket.
        connection.Deliver(new NpgsqlNotifyMessage(
            RealtimeNotification.SecurityChannel,
            revoked.ToString("D")));
        connection.Deliver(Event(other));

        await WaitUntilAsync(() => revokedSocket.Aborted, bound.Token);

        sender.Received.Count.ShouldBe(1, "the blocked socket still owns the one in-flight send");
        registry.Count.ShouldBe(0, "the revocation aborted the socket without waiting for the send");

        // Releasing the socket drains what the pump queued while it was blocked.
        sender.Release();

        await sender.WaitForAsync(2, bound.Token);
        sender.Received[1].UserId.ShouldBe(other);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Full_Queue_Drops_The_Oldest_Event_And_Counts_It()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var metrics = new RealtimeMetrics();
        using var dropped = new CounterRecorder(metrics, RealtimeMetrics.ListenerDeliveriesDroppedCounterName);
        using var sender = new BlockingGroupSender();
        var connection = new ScriptedListenerConnection();
        var userId = Guid.CreateVersion7();

        var service = BuildListener(
            new ScriptedListenerConnectionFactory(_ => connection),
            sender,
            NewRegistry(clock, metrics),
            metrics,
            clock,
            new RecordingListenerDelay(completeAfter: int.MaxValue),
            NullLogger<RealtimeListenerService>.Instance,
            new RealtimeOptions { DeliveryQueueCapacity = 1 });

        using var bound = RealtimeHarness.Bounded(ct);

        await service.StartAsync(ct);
        await service.ListeningAsync(bound.Token);

        // The consumer takes the first event and blocks on its socket, so the one-slot queue stays full.
        connection.Deliver(Event(userId));
        await sender.WaitForAsync(1, bound.Token);

        // Three more, each with its own event name: the first fills the slot, each of the other two evicts
        // the oldest — atomically, so the eviction can never race the blocked consumer.
        connection.Deliver(Event(userId, RealtimeEventName.PublicationUpdated));
        connection.Deliver(Event(userId, RealtimeEventName.EpisodeUpdated));
        connection.Deliver(Event(userId, RealtimeEventName.CreditBalanceChanged));

        await WaitUntilAsync(() => dropped.Count == 2, bound.Token);
        dropped.Count.ShouldBe(2, "the queue counts every evicted oldest event");

        // The one event left is the newest, so drop-oldest (never drop-newest) is what the queue does.
        sender.Release();

        await sender.WaitForAsync(2, bound.Token);
        sender.Received[1].Name.ShouldBe(RealtimeEventName.CreditBalanceChanged);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Delivery_Whose_Timeout_Is_Swallowed_By_The_Sender_Is_Still_Counted()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var metrics = new RealtimeMetrics();
        using var failed = new CounterRecorder(metrics, RealtimeMetrics.ListenerDeliveriesFailedCounterName);
        using var sender = new SwallowingGroupSender();
        var connection = new ScriptedListenerConnection();
        var userId = Guid.CreateVersion7();

        var service = BuildListener(
            new ScriptedListenerConnectionFactory(_ => connection),
            sender,
            NewRegistry(clock, metrics),
            metrics,
            clock,
            new RecordingListenerDelay(completeAfter: int.MaxValue),
            NullLogger<RealtimeListenerService>.Instance,
            new RealtimeOptions { DeliveryTimeoutSeconds = 1 });

        using var bound = RealtimeHarness.Bounded(ct);

        await service.StartAsync(ct);
        await service.ListeningAsync(bound.Token);

        connection.Deliver(Event(userId));
        await sender.WaitForAsync(1, bound.Token);

        // The send outlives the one-second bound and then returns normally — exactly what SignalR's write
        // path does when it cancels the flush, aborts the socket and completes the write task.
        await WaitUntilAsync(() => failed.Count == 1, bound.Token);
        failed.Count.ShouldBe(
            1,
            "a send that outlives the delivery timeout must be counted even when it returns");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Listener_Reconnects_With_Backoff_After_A_Dropped_Connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var metrics = new RealtimeMetrics();
        using var reconnectCounter = new CounterRecorder(metrics, RealtimeMetrics.ListenerReconnectsCounterName);
        using var sender = new RecordingGroupSender();
        var delay = new RecordingListenerDelay(completeAfter: 7);
        var connections = new ScriptedListenerConnectionFactory(_ => new DroppingListenerConnection());

        var service = BuildListener(
            connections,
            sender,
            NewRegistry(clock, metrics),
            metrics,
            clock,
            delay,
            NullLogger<RealtimeListenerService>.Instance);

        using var bound = RealtimeHarness.Bounded(ct);

        await service.StartAsync(ct);
        await delay.EnoughAsync(bound.Token);
        await service.StopAsync(CancellationToken.None);

        // BD11: 1, 2, 4, 8, 16, 30 s and then the last entry repeats.
        string.Join(",", delay.Recorded.Select(seconds => (int)seconds.TotalSeconds))
            .ShouldBe("1,2,4,8,16,30,30");

        // One session per attempt, one metric per attempt, and nothing was forwarded during the outage.
        connections.CreatedCount.ShouldBeGreaterThanOrEqualTo(7);
        reconnectCounter.Count.ShouldBeGreaterThanOrEqualTo(7);
        sender.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Backstop_Closes_AnOwnerConnection_AfterFiveMinutes_AndAUserConnection_AfterFifteen()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var metrics = new RealtimeMetrics();
        using var sender = new RecordingGroupSender();

        // The ruled defaults of Hubs:MaxConnectionMinutes (WT37): owner/admin 5, user 15 — no map is
        // configured here on purpose, so the defaults are what this test measures.
        var registry = NewRegistry(clock, metrics);
        var owner = new RecordingHubCallerContext("owner-connection");
        var user = new RecordingHubCallerContext("user-connection");
        var ownerId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        registry.Add(ownerId, PlatformRole.Owner, owner);
        registry.Add(userId, PlatformRole.User, user);

        var connection = new ScriptedListenerConnection();
        var service = BuildListener(
            new ScriptedListenerConnectionFactory(_ => connection),
            sender,
            registry,
            metrics,
            clock,
            new RecordingListenerDelay(completeAfter: int.MaxValue),
            NullLogger<RealtimeListenerService>.Instance);

        using var bound = RealtimeHarness.Bounded(ct);

        await service.StartAsync(ct);
        await service.ListeningAsync(bound.Token);

        // Five minutes: the owner's cap is up, the user's is not. The sweep runs after a wait returns, and
        // the message that follows proves the sweep of the previous iteration has already run.
        clock.Advance(TimeSpan.FromMinutes(5));
        connection.Pulse();
        connection.Deliver(Event(ownerId));

        await sender.WaitForAsync(1, bound.Token);

        owner.Aborted.ShouldBeTrue("an owner connection lives at most five minutes (WT37)");
        user.Aborted.ShouldBeFalse("a user connection lives fifteen minutes");
        registry.Count.ShouldBe(1);

        // Ten minutes later the user's cap is up too.
        clock.Advance(TimeSpan.FromMinutes(10));
        connection.Pulse();
        connection.Deliver(Event(userId));

        await sender.WaitForAsync(2, bound.Token);

        user.Aborted.ShouldBeTrue("a user connection lives at most fifteen minutes (WT37)");
        registry.Count.ShouldBe(0, "an aborted connection is forgotten, so the registry cannot leak");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Backstop_Aborts_AnExpired_Socket_While_The_Listen_Session_Is_Down()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var metrics = new RealtimeMetrics();
        using var sender = new RecordingGroupSender();
        var registry = NewRegistry(clock, metrics);
        var expired = new RecordingHubCallerContext("expired-socket");
        registry.Add(Guid.CreateVersion7(), PlatformRole.Owner, expired);

        // The owner's five-minute cap is already up when the loop starts, and every attempt drops its
        // wait: the sweep must run anyway, on its own schedule (F1).
        clock.Advance(TimeSpan.FromMinutes(5));

        var delay = new RecordingListenerDelay(completeAfter: 3);
        var service = BuildListener(
            new ScriptedListenerConnectionFactory(_ => new DroppingListenerConnection()),
            sender,
            registry,
            metrics,
            clock,
            delay,
            NullLogger<RealtimeListenerService>.Instance);

        using var bound = RealtimeHarness.Bounded(ct);

        await service.StartAsync(ct);

        // Three failed attempts prove the outage outlasted several reconnects; the sweep already ran.
        await delay.EnoughAsync(bound.Token);
        await service.StopAsync(CancellationToken.None);

        expired.Aborted.ShouldBeTrue("the backstop must not depend on the LISTEN session's health");
        registry.Count.ShouldBe(0);
    }

    [Fact]
    public void Backstop_Reads_NoStampCache()
    {
        // [RULED — README §B10 WT37]: no stamp or status cache is read anywhere on the hub path. The
        // listener's collaborators are exactly these, so a cache cannot be reached from the loop at all
        // — and a later change that injects one (a stamp reader, a user-status cache) fails here.
        DependencyNames(typeof(RealtimeListenerService)).ShouldBe(
            "IClock,ILogger`1,INpgsqlListenerConnectionFactory,IOptions`1,IRealtimeGroupSender,"
            + "IRealtimeListenerDelay,RealtimeConnectionRegistry,RealtimeMetrics");

        DependencyNames(typeof(RealtimeConnectionRegistry)).ShouldBe("IClock,IOptions`1,RealtimeMetrics");
        DependencyNames(typeof(EpisodeHub)).ShouldBe("RealtimeConnectionRegistry");

        // And nothing reachable from that closed graph is a stamp or a cache.
        var reachable = Reachable(typeof(RealtimeListenerService))
            .Concat(Reachable(typeof(RealtimeConnectionRegistry)))
            .Concat(Reachable(typeof(EpisodeHub)));

        reachable
            .Where(type => type.Namespace?.StartsWith("NightTales", StringComparison.Ordinal) == true)
            .Select(type => type.FullName!)
            .Where(name => name.Contains("Cache", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Stamp", StringComparison.OrdinalIgnoreCase))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Backstop_Closes_A_Connection_Only_Once_Its_Role_Cap_Has_Elapsed()
    {
        var clock = new FakeClock();
        using var metrics = new RealtimeMetrics();
        var registry = NewRegistry(clock, metrics);
        var owner = new RecordingHubCallerContext("owner-connection");
        registry.Add(Guid.CreateVersion7(), PlatformRole.Owner, owner);

        // One millisecond short of the owner's five minutes (WT37): the socket is still open...
        registry.AbortExpired(clock.UtcNow + TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1))
            .ShouldBe(0);
        owner.Aborted.ShouldBeFalse();

        // ...and the cap itself closes it.
        registry.AbortExpired(clock.UtcNow + TimeSpan.FromMinutes(5)).ShouldBe(1);
        owner.Aborted.ShouldBeTrue();
        registry.Count.ShouldBe(0);
    }

    [Fact]
    public void Backstop_Uses_The_Configured_Role_Cap_When_The_Map_Names_One()
    {
        var clock = new FakeClock();
        using var metrics = new RealtimeMetrics();
        var options = new HubsOptions();
        options.MaxConnectionMinutes["admin"] = 7; // not the ruled five-minute default
        var registry = new RealtimeConnectionRegistry(Options.Create(options), clock, metrics);
        var admin = new RecordingHubCallerContext("admin-connection");
        registry.Add(Guid.CreateVersion7(), PlatformRole.Admin, admin);

        // The default would have closed it at five minutes; the configured cap does not.
        registry.AbortExpired(clock.UtcNow + TimeSpan.FromMinutes(5)).ShouldBe(0);
        admin.Aborted.ShouldBeFalse();

        registry.AbortExpired(clock.UtcNow + TimeSpan.FromMinutes(7)).ShouldBe(1);
        admin.Aborted.ShouldBeTrue();
    }

    [Fact]
    public void ConnectionsActive_Gauge_Reports_This_Instances_Registry()
    {
        var clock = new FakeClock();
        using var metrics = new RealtimeMetrics();
        using var gauge = new GaugeRecorder(metrics);
        var registry = NewRegistry(clock, metrics);

        registry.Add(Guid.CreateVersion7(), PlatformRole.User, new RecordingHubCallerContext("first"));
        registry.Add(Guid.CreateVersion7(), PlatformRole.User, new RecordingHubCallerContext("second"));
        gauge.Record();
        gauge.Last.ShouldBe(2);

        registry.Remove("first");
        gauge.Record();
        gauge.Last.ShouldBe(1);
    }

    [Fact]
    public async Task Unreadable_Security_Message_Aborts_Nothing_And_Is_Logged()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var metrics = new RealtimeMetrics();
        using var sender = new RecordingGroupSender();
        var logger = new CapturingListenerLogger();
        var connection = new ScriptedListenerConnection();
        var registry = NewRegistry(clock, metrics);
        var live = new RecordingHubCallerContext("live-connection");
        registry.Add(Guid.CreateVersion7(), PlatformRole.User, live);

        var service = BuildListener(
            new ScriptedListenerConnectionFactory(_ => connection),
            sender,
            registry,
            metrics,
            clock,
            new RecordingListenerDelay(completeAfter: int.MaxValue),
            logger);

        using var bound = RealtimeHarness.Bounded(ct);

        await service.StartAsync(ct);
        await service.ListeningAsync(bound.Token);

        // A security payload that is not a user id must not close every live connection...
        connection.Deliver(new NpgsqlNotifyMessage(RealtimeNotification.SecurityChannel, "not-a-user-id"));

        // ...and the event that follows proves the previous message was already dispatched.
        connection.Deliver(Event(Guid.CreateVersion7()));

        await sender.WaitForAsync(1, bound.Token);

        live.Aborted.ShouldBeFalse("an unparsable security payload aborts nobody");
        registry.Count.ShouldBe(1);
        logger.Lines.ShouldContain(
            line => line.Contains("realtime.listener.unreadable", StringComparison.Ordinal));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Unknown_Event_Name_Is_Ignored_With_A_Warning()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var metrics = new RealtimeMetrics();
        using var sender = new RecordingGroupSender();
        var logger = new CapturingListenerLogger();
        var connection = new ScriptedListenerConnection();
        var userId = Guid.CreateVersion7();

        var service = BuildListener(
            new ScriptedListenerConnectionFactory(_ => connection),
            sender,
            NewRegistry(clock, metrics),
            metrics,
            clock,
            new RecordingListenerDelay(completeAfter: int.MaxValue),
            logger);

        using var bound = RealtimeHarness.Bounded(ct);

        await service.StartAsync(ct);
        await service.ListeningAsync(bound.Token);

        // A fifth event of a later wave (02.8.2 L227-228): ignored, never forwarded...
        connection.Deliver(new NpgsqlNotifyMessage(
            RealtimeNotification.RealtimeChannel,
            $$$"""{"userId":"{{{userId:D}}}","event":"autopilot_run_updated","ids":{}}"""));

        // ...and the known event that follows arrives alone, which is what proves the first was dropped.
        connection.Deliver(Event(userId));

        await sender.WaitForAsync(1, bound.Token);

        sender.Count.ShouldBe(1);
        sender.Received[0].Name.ShouldBe(RealtimeEventName.EpisodeUpdated);
        sender.Received[0].UserId.ShouldBe(userId);

        logger.Lines.ShouldContain(
            line => line.Contains("realtime.listener.unknown-event", StringComparison.Ordinal)
                && line.Contains("autopilot_run_updated", StringComparison.Ordinal));

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>The one event shape these tests deliver: an <c>episode_updated</c> for one user.</summary>
    private static NpgsqlNotifyMessage Event(Guid userId) =>
        new(
            RealtimeNotification.RealtimeChannel,
            RealtimePayload.Build(
                userId,
                new RealtimeEvent(RealtimeEventName.EpisodeUpdated, new RealtimeIds())));

    /// <summary>The same shape, carrying <paramref name="name"/> so a test can tell two events apart.</summary>
    private static NpgsqlNotifyMessage Event(Guid userId, RealtimeEventName name) =>
        new(
            RealtimeNotification.RealtimeChannel,
            RealtimePayload.Build(userId, new RealtimeEvent(name, new RealtimeIds())));

    private static RealtimeConnectionRegistry NewRegistry(IClock clock, RealtimeMetrics metrics) =>
        new(Options.Create(new HubsOptions()), clock, metrics);

    /// <summary>
    /// Polls a condition 10 ms apart until the caller's token bounds the wait — the same technique the
    /// harness uses for a registry transition, never an unbounded sleep.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private static RealtimeListenerService BuildListener(
        INpgsqlListenerConnectionFactory connections,
        IRealtimeGroupSender sender,
        RealtimeConnectionRegistry registry,
        RealtimeMetrics metrics,
        IClock clock,
        IRealtimeListenerDelay delay,
        ILogger<RealtimeListenerService> logger,
        RealtimeOptions? options = null) =>
        new(
            connections,
            sender,
            registry,
            Options.Create(options ?? new RealtimeOptions()),
            delay,
            metrics,
            clock,
            logger);

    private static string DependencyNames(Type type) =>
        string.Join(
            ",",
            ConstructorDependencies(type)
                .Select(dependency => dependency.Name)
                .OrderBy(name => name, StringComparer.Ordinal));

    private static IEnumerable<Type> ConstructorDependencies(Type type) =>
        type.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Distinct();

    /// <summary>Everything transitively injectable into <paramref name="root"/>, within the solution.</summary>
    private static IEnumerable<Type> Reachable(Type root)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([root]);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;

            foreach (var dependency in ConstructorDependencies(current).Concat(current.GetGenericArguments()))
            {
                if (dependency.Namespace?.StartsWith("NightTales", StringComparison.Ordinal) == true)
                {
                    queue.Enqueue(dependency);
                }
            }
        }
    }

    /// <summary>One message PostgreSQL delivered on a channel, from a script the test drives.</summary>
    private sealed class ScriptedListenerConnection : INpgsqlListenerConnection
    {
        private readonly Channel<object> _inbox = Channel.CreateUnbounded<object>();

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async ValueTask<NpgsqlNotifyMessage?> WaitAsync(
            TimeSpan heartbeat,
            CancellationToken cancellationToken)
        {
            var pulse = await _inbox.Reader.ReadAsync(cancellationToken);

            return pulse is NpgsqlNotifyMessage message ? message : null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>Releases one wait with a heartbeat, which is what makes the loop sweep.</summary>
        public void Pulse() => _inbox.Writer.TryWrite(new object());

        /// <summary>Releases one wait with a message.</summary>
        public void Deliver(NpgsqlNotifyMessage message) => _inbox.Writer.TryWrite(message);
    }

    /// <summary>A session that drops: <c>ConnectAsync</c> succeeds and the wait fails.</summary>
    private sealed class DroppingListenerConnection : INpgsqlListenerConnection
    {
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask<NpgsqlNotifyMessage?> WaitAsync(TimeSpan heartbeat, CancellationToken cancellationToken) =>
            ValueTask.FromException<NpgsqlNotifyMessage?>(
                new NpgsqlException("The LISTEN session was dropped."));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedListenerConnectionFactory(Func<int, INpgsqlListenerConnection> create)
        : INpgsqlListenerConnectionFactory
    {
        private int _created;

        public int CreatedCount => Volatile.Read(ref _created);

        public INpgsqlListenerConnection Create() => create(Interlocked.Increment(ref _created));
    }

    /// <summary>
    /// The loop's pause, recorded instead of spent: it returns at once until <c>completeAfter</c> entries
    /// are recorded, then holds the loop so the schedule of a test is exactly what it observed.
    /// </summary>
    private sealed class RecordingListenerDelay(int completeAfter) : IRealtimeListenerDelay
    {
        private readonly TaskCompletionSource _enough = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private readonly List<TimeSpan> _recorded = [];

        public IReadOnlyList<TimeSpan> Recorded
        {
            get
            {
                lock (_gate)
                {
                    return [.. _recorded];
                }
            }
        }

        public Task EnoughAsync(CancellationToken cancellationToken) => _enough.Task.WaitAsync(cancellationToken);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            int recorded;
            lock (_gate)
            {
                _recorded.Add(delay);
                recorded = _recorded.Count;
            }

            if (recorded < completeAfter)
            {
                return;
            }

            _enough.TrySetResult();

            // Hold the loop until the test stops the service, so no further attempt is recorded.
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>The fan-out, recorded: what the loop tried to deliver to this instance's sockets.</summary>
    private sealed class RecordingGroupSender : IRealtimeGroupSender, IDisposable
    {
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly object _gate = new();
        private readonly List<(Guid UserId, RealtimeEventName Name, JsonElement Body)> _received = [];

        public IReadOnlyList<(Guid UserId, RealtimeEventName Name, JsonElement Body)> Received
        {
            get
            {
                lock (_gate)
                {
                    return [.. _received];
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _received.Count;
                }
            }
        }

        public Task SendAsync(
            Guid userId,
            RealtimeEventName name,
            JsonElement body,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _received.Add((userId, name, body));
            }

            _arrived.Release();

            return Task.CompletedTask;
        }

        /// <summary>Waits until at least <paramref name="count"/> messages were forwarded.</summary>
        public async Task WaitForAsync(int count, CancellationToken cancellationToken)
        {
            while (Count < count)
            {
                await _arrived.WaitAsync(cancellationToken);
            }
        }

        public void Dispose() => _arrived.Dispose();
    }

    /// <summary>
    /// A sender whose sends block until the test releases them, so the delivery queue's decoupling is the
    /// observable: the pump keeps dispatching while one socket never accepts its write.
    /// </summary>
    private sealed class BlockingGroupSender : IRealtimeGroupSender, IDisposable
    {
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private readonly List<(Guid UserId, RealtimeEventName Name)> _received = [];

        public IReadOnlyList<(Guid UserId, RealtimeEventName Name)> Received
        {
            get
            {
                lock (_gate)
                {
                    return [.. _received];
                }
            }
        }

        public async Task SendAsync(
            Guid userId,
            RealtimeEventName name,
            JsonElement body,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _received.Add((userId, name));
            }

            _arrived.Release();

            await _release.Task.WaitAsync(cancellationToken);
        }

        /// <summary>Waits until at least <paramref name="count"/> sends have started.</summary>
        public async Task WaitForAsync(int count, CancellationToken cancellationToken)
        {
            while (Received.Count < count)
            {
                await _arrived.WaitAsync(cancellationToken);
            }
        }

        /// <summary>Lets every blocked send finish.</summary>
        public void Release() => _release.TrySetResult();

        public void Dispose() => _arrived.Dispose();
    }

    /// <summary>
    /// A sender whose send outlives its timeout and then returns normally, like SignalR's write path when it
    /// cancels the flush, aborts the socket and completes the write task successfully.
    /// </summary>
    private sealed class SwallowingGroupSender : IRealtimeGroupSender, IDisposable
    {
        private readonly SemaphoreSlim _arrived = new(0);
        private int _started;

        public async Task SendAsync(
            Guid userId,
            RealtimeEventName name,
            JsonElement body,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _started);
            _arrived.Release();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Swallowed on purpose: the production sender's write task completes successfully too.
            }
        }

        /// <summary>Waits until at least <paramref name="count"/> sends have started.</summary>
        public async Task WaitForAsync(int count, CancellationToken cancellationToken)
        {
            while (Volatile.Read(ref _started) < count)
            {
                await _arrived.WaitAsync(cancellationToken);
            }
        }

        public void Dispose() => _arrived.Dispose();
    }

    /// <summary>A socket the registry can abort; it is the observable of the <c>security</c> channel.</summary>
    private sealed class RecordingHubCallerContext(string connectionId) : HubCallerContext
    {
        private int _aborted;

        public bool Aborted => Volatile.Read(ref _aborted) != 0;

        public override string ConnectionId { get; } = connectionId;

        public override string? UserIdentifier => null;

        public override ClaimsPrincipal? User => null;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() => Volatile.Write(ref _aborted, 1);
    }

    /// <summary>Keeps the listener's rendered lines for the warning assertions (D13: hand-written).</summary>
    private sealed class CapturingListenerLogger : ILogger<RealtimeListenerService>
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

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
            lock (_gate)
            {
                _lines.Add($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }

    /// <summary>Reads the observable <c>realtime_connections_active</c> gauge of one meter instance.</summary>
    private sealed class GaugeRecorder : IDisposable
    {
        private readonly MeterListener _listener;

        public GaugeRecorder(RealtimeMetrics metrics)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, owner) =>
                {
                    if (ReferenceEquals(instrument.Meter, metrics.Meter))
                    {
                        owner.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
            {
                if (string.Equals(
                    instrument.Name,
                    RealtimeMetrics.ConnectionsActiveGaugeName,
                    StringComparison.Ordinal))
                {
                    Last = value;
                }
            });

            _listener.Start();
        }

        public int Last { get; private set; } = -1;

        public void Record() => _listener.RecordObservableInstruments();

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Counts the measurements of one instrument, like 09.9.3-T1's exporter would.</summary>
    private sealed class CounterRecorder : IDisposable
    {
        private readonly MeterListener _listener;
        private int _count;

        public CounterRecorder(RealtimeMetrics metrics, string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, owner) =>
                {
                    if (ReferenceEquals(instrument.Meter, metrics.Meter)
                        && string.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
                    {
                        owner.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref _count));
            _listener.Start();
        }

        public int Count => Volatile.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }
}
