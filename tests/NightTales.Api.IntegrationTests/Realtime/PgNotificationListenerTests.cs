using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Infrastructure.Realtime;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Realtime;

/// <summary>
/// The production <c>LISTEN</c> session (<see cref="PgNotificationListener"/>) against a real PostgreSQL
/// 17: it listens on both channels of 09 README BD11, its wait is the loop's pause, a committed burst is
/// drained at once, and a session the server drops surfaces as an error — the one thing the reconnect
/// schedule of <c>RealtimeListenerService</c> reacts to.
/// </summary>
/// <remarks>
/// The loop-level tests drive a hand-written fake connection (09 README D13); these exercise the real
/// Npgsql path that the fakes deliberately hide.
/// </remarks>
public sealed class PgNotificationListenerTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task Listener_Reads_Real_Notifies_On_Both_Channels_And_Pauses_On_The_Heartbeat()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var listener = new PgNotificationListener(Database.ConnectionString);
        await listener.ConnectAsync(ct);

        // Nothing was sent: the wait itself is the pause, and it returns with no message.
        (await listener.WaitAsync(TimeSpan.FromMilliseconds(250), ct)).ShouldBeNull();

        var userId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            $"SELECT pg_notify('{RealtimeNotification.RealtimeChannel}', '{{\"userId\":\"{userId:D}\"}}')",
            ct);

        var realtime = await listener.WaitAsync(TimeSpan.FromSeconds(10), ct);
        realtime.ShouldNotBeNull();
        realtime!.Value.Channel.ShouldBe(RealtimeNotification.RealtimeChannel);

        // The second channel shares the one session (BD11: one connection per Api instance).
        await Database.ExecuteAsync(
            $"SELECT pg_notify('{RealtimeNotification.SecurityChannel}', '{userId:D}')",
            ct);

        var security = await listener.WaitAsync(TimeSpan.FromSeconds(10), ct);
        security.ShouldNotBeNull();
        security!.Value.Channel.ShouldBe(RealtimeNotification.SecurityChannel);
        security.Value.Payload.ShouldBe(userId.ToString("D"));
    }

    [Theory]
    [InlineData("realtime")]
    [InlineData("security")]
    public async Task Listener_DoesNotReceiveAPreviousSessionsUnreadNotice(string channel)
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = WithApplicationName("nts-qa-listener-reused-" + Guid.CreateVersion7().ToString("N"));
        await using (var previous = new NpgsqlConnection(connectionString))
        {
            await previous.OpenAsync(ct);
            await using var command = previous.CreateCommand();
            command.CommandText = "LISTEN " + channel;
            await command.ExecuteNonQueryAsync(ct);
            await Database.ExecuteAsync($"SELECT pg_notify('{channel}', 'previous-session')", ct);
        }

        await using var listener = new PgNotificationListener(connectionString);
        await listener.ConnectAsync(ct);
        (await listener.WaitAsync(TimeSpan.FromMilliseconds(250), ct)).ShouldBeNull(
            "a new listener must not inherit the previous session's unread notification");
    }

    [Fact]
    public async Task Listener_Drains_A_Committed_Burst_Without_Waiting_A_Heartbeat_Per_Message()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var listener = new PgNotificationListener(Database.ConnectionString);
        await listener.ConnectAsync(ct);

        // One statement commits three notifications together, so PostgreSQL delivers them as one batch.
        await Database.ExecuteAsync(
            "SELECT pg_notify('realtime', 'burst-1'), pg_notify('realtime', 'burst-2'), "
            + "pg_notify('realtime', 'burst-3')",
            ct);

        var started = Stopwatch.StartNew();
        var payloads = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var message = await listener.WaitAsync(TimeSpan.FromSeconds(5), ct);

            message.ShouldNotBeNull("every message of the batch is handed back");
            payloads.Add(message!.Value.Payload);
        }

        started.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(2),
            "a batch already in the read buffer is drained immediately; one heartbeat per message would show here");
        payloads.ShouldBe(["burst-1", "burst-2", "burst-3"], ignoreOrder: true);
    }

    [Fact]
    public async Task Listener_Surfaces_A_Dropped_Session_So_The_Loop_Reconnects()
    {
        var ct = TestContext.Current.CancellationToken;
        var applicationName = "nts-qa-listener-" + Guid.CreateVersion7().ToString("N");

        await using var listener = new PgNotificationListener(WithApplicationName(applicationName));
        await listener.ConnectAsync(ct);

        (await listener.WaitAsync(TimeSpan.FromMilliseconds(250), ct)).ShouldBeNull();

        var terminated = await Database.QueryStringsAsync(
            "SELECT pg_terminate_backend(pid)::text FROM pg_stat_activity "
            + $"WHERE application_name = '{applicationName}' AND pid <> pg_backend_pid()",
            ct);

        terminated.ShouldContain("true", "the test must actually have killed the LISTEN session");

        // RealtimeListenerService catches this and reconnects on the 1-2-4-8-16-30 s schedule; a wait that
        // kept returning null would leave the instance silently deaf instead.
        var thrown = await Record.ExceptionAsync(
            () => listener.WaitAsync(TimeSpan.FromSeconds(10), ct).AsTask());

        thrown.ShouldNotBeNull("a dropped session must surface as an error");
        thrown.ShouldNotBeOfType<OperationCanceledException>();
    }

    [Fact]
    public void ListenerFactory_RefusesToBuild_Without_A_ConnectionString()
    {
        var factory = new PgNotificationListenerFactory(new ConfigurationBuilder().Build());

        var thrown = Should.Throw<InvalidOperationException>(() => factory.Create());

        thrown.Message.ShouldContain("ConnectionStrings:Postgres");
    }

    private string WithApplicationName(string applicationName) =>
        new NpgsqlConnectionStringBuilder(Database.ConnectionString)
        {
            ApplicationName = applicationName,
        }.ConnectionString;
}
