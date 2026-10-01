using NightTales.Application.Abstractions.Realtime;
using NightTales.Infrastructure.Realtime;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Realtime;

/// <summary>
/// The drain policy of the <c>LISTEN</c> session's inbox (F3): a message the provider's callback already
/// queued is handed back before the session waits, so a burst that arrived while the provider had nothing
/// left to read is never held for a heartbeat.
/// </summary>
public sealed class PgNotificationInboxTests
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task NextAsync_Drains_A_Queued_Burst_Without_Waiting_And_Waits_Only_When_Empty()
    {
        var ct = TestContext.Current.CancellationToken;
        var inbox = new PgNotificationInbox();
        var waits = 0;

        Task Wait(TimeSpan heartbeat, CancellationToken token)
        {
            waits++;

            // The provider's callback runs while it waits: this is the message it queues.
            if (waits == 1)
            {
                inbox.Enqueue(new NpgsqlNotifyMessage(RealtimeNotification.SecurityChannel, "during-wait"));
            }

            return Task.CompletedTask;
        }

        // Two messages the callback queued while the provider had nothing left to read.
        inbox.Enqueue(new NpgsqlNotifyMessage(RealtimeNotification.RealtimeChannel, "burst-1"));
        inbox.Enqueue(new NpgsqlNotifyMessage(RealtimeNotification.RealtimeChannel, "burst-2"));

        (await inbox.NextAsync(Heartbeat, Wait, ct))!.Value.Payload.ShouldBe("burst-1");
        (await inbox.NextAsync(Heartbeat, Wait, ct))!.Value.Payload.ShouldBe("burst-2");
        waits.ShouldBe(0, "a queued message must be drained without touching the provider first (F3)");

        // The wait runs only on an empty queue, and it hands back what the callback queued during it.
        (await inbox.NextAsync(Heartbeat, Wait, ct))!.Value.Payload.ShouldBe("during-wait");
        waits.ShouldBe(1);

        // A wait that queued nothing is the heartbeat.
        (await inbox.NextAsync(Heartbeat, Wait, ct)).ShouldBeNull();
        waits.ShouldBe(2);
        inbox.Count.ShouldBe(0);
    }
}
