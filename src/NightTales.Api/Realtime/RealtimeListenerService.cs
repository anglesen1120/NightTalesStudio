using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Realtime;

namespace NightTales.Api.Realtime;

/// <summary>
/// The Api instance's one <c>LISTEN</c> session (09 README BD11, 09.8.1 plan §5): it reads what the
/// producers wrote with <c>pg_notify</c> and forwards each message to this instance's own sockets.
/// </summary>
/// <remarks>
/// <para>
/// One connection per Api instance serves both channels — <c>realtime</c> and <c>security</c> — because
/// PostgreSQL lets one session listen on many; a <c>realtime</c> message goes to the group of the user it
/// names, a <c>security</c> message aborts that user's connections on <em>this</em> instance
/// (09-backend-api L218; every other instance closes its own copy of the socket, open item 7).
/// </para>
/// <para>
/// The loop never busy-waits: the wait itself is the pause
/// (<see cref="INpgsqlListenerConnection.WaitAsync"/> returns <c>null</c> on the heartbeat), and the
/// backstop sweep runs after a wait <em>and</em> at the top of every reconnect attempt, every
/// <c>Realtime:RevalidateSeconds</c> — its deadline is never pushed back by a reconnect, so a session
/// that keeps flapping cannot postpone it. Every failure of a session is retried on the BD11 schedule
/// (1, 2, 4, 8, 16, 30 s, then 30 s forever) and <b>nothing is replayed</b> on reconnect: clients
/// refetch, which is why a dropped connection is a recoverable event and never a reason to stop the
/// host. The schedule restarts from its first entry only after a wait has returned, so an outage that
/// keeps a session from ever reaching its first wait ramps instead of retrying once a second.
/// </para>
/// <para>
/// A <c>realtime</c> event is never written from the reading loop: it is handed to a bounded queue whose
/// single consumer owns the group send, so a socket under back-pressure holds neither the <c>LISTEN</c>
/// pump nor the <c>security</c> abort nor the backstop (<c>Realtime:DeliveryQueueCapacity</c> bounds the
/// queue; <c>Realtime:DeliveryTimeoutSeconds</c> bounds one send).
/// </para>
/// <para>
/// The listener holds no stamp, status or token cache ([RULED — README §B10 WT37]): its only state is
/// the registry of the sockets this instance accepted, and the sweep closes them purely on elapsed
/// time against the <em>role's</em> cap.
/// </para>
/// </remarks>
/// <param name="connections">Creates the long-lived session (a real Npgsql connection in production).</param>
/// <param name="sender">Forwards one <c>realtime</c> event to its owner's local group.</param>
/// <param name="registry">This instance's live connections, for the <c>security</c> abort and the backstop.</param>
/// <param name="options">The heartbeat, the sweep interval and the reconnect schedule.</param>
/// <param name="delay">The pause between reconnect attempts; a test walks the schedule without waiting.</param>
/// <param name="metrics">The listener counters.</param>
/// <param name="clock">The instance's clock; a test moves it instead of waiting.</param>
/// <param name="logger">The listener's own lines — never a payload, an id or a connection string.</param>
public sealed partial class RealtimeListenerService(
    INpgsqlListenerConnectionFactory connections,
    IRealtimeGroupSender sender,
    RealtimeConnectionRegistry registry,
    IOptions<RealtimeOptions> options,
    IRealtimeListenerDelay delay,
    RealtimeMetrics metrics,
    IClock clock,
    ILogger<RealtimeListenerService> logger) : BackgroundService
{
    private readonly Channel<Delivery> _deliveries = Channel.CreateBounded<Delivery>(
        new BoundedChannelOptions(options.Value.DeliveryQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        },
        _ =>
        {
            // The channel evicts the oldest entry itself, in the same atomic operation as the enqueue, so a
            // full queue never races the consumer.
            metrics.ListenerDeliveryDropped();
            LogDeliveryDropped(logger);
        });

    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset? _nextSweep;

    /// <summary>
    /// Completes once the loop holds an open session — the barrier a test awaits instead of sleeping,
    /// and the only public state of this service. A cancelled wait throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="cancellationToken">The test's own bound on how long to wait.</param>
    public Task ListeningAsync(CancellationToken cancellationToken) =>
        _listening.Task.WaitAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// No exception escapes on purpose: a database that is down when the Api starts must not stop the
    /// host (the default <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>), so every failure
    /// inside the loop is a reconnect attempt with a warning and a metric.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delivery = DeliverAsync(stoppingToken);

        try
        {
            await ListenAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            // No producer is left, so the one consumer drains what was queued and then ends.
            _deliveries.Writer.TryComplete();
            await delivery.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Holds the one <c>LISTEN</c> session, reconnecting on the BD11 schedule until the host stops.
    /// </summary>
    /// <remarks>
    /// The backstop sweep runs at the top of every attempt, so a session that never becomes healthy — a
    /// database down at start-up, a flapping connection, a failing wait — still cannot postpone it.
    /// </remarks>
    /// <param name="stoppingToken">The loop's shutdown token.</param>
    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            SweepIfDue();

            await using var connection = connections.Create();

            try
            {
                await connection.ConnectAsync(stoppingToken).ConfigureAwait(false);

                _listening.TrySetResult();

                // The schedule restarts only once the session has proved itself with a completed wait:
                // a connection that drops before its first wait — a flapping database — keeps ramping
                // instead of holding the listener in a one-second retry loop (BD11).
                await PumpAsync(connection, () => attempt = 0, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // One warning and one metric per attempt (BD11). The exception's *type* is logged, never
                // the exception itself: a provider message can carry the connection string (D17).
                var backoff = options.Value.BackoffFor(attempt);
                metrics.ListenerReconnect();
                LogListenerDropped(attempt, (int)backoff.TotalSeconds, exception.GetType().Name);

                try
                {
                    await delay.DelayAsync(backoff, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                attempt++;
            }
        }
    }

    /// <summary>
    /// Reads the session until it breaks or the host shuts down: each iteration is one heartbeat-sized
    /// wait, then the sweep if it is due, then the message if one arrived.
    /// </summary>
    /// <param name="connection">The session created for this attempt.</param>
    /// <param name="sessionAlive">
    /// Run once a wait has returned: a returned wait — a message or the heartbeat — is the proof the
    /// session is live, so it is the only thing that resets the reconnect schedule (BD11).
    /// </param>
    /// <param name="stoppingToken">The loop's shutdown token.</param>
    private async Task PumpAsync(
        INpgsqlListenerConnection connection,
        Action sessionAlive,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var message = await connection.WaitAsync(options.Value.Heartbeat, stoppingToken)
                .ConfigureAwait(false);

            sessionAlive();
            SweepIfDue();

            if (message is { } received)
            {
                Dispatch(received);
            }
        }
    }

    /// <summary>
    /// Runs the backstop sweep when <c>Realtime:RevalidateSeconds</c> has elapsed, and closes every
    /// connection whose role's <c>Hubs:MaxConnectionMinutes</c> is up
    /// ([RULED — README §B10 WT37]). Reads no cache: the registry compares the clock with the entry.
    /// </summary>
    private void SweepIfDue()
    {
        var now = clock.UtcNow;

        if (_nextSweep is { } due && now < due)
        {
            return;
        }

        _nextSweep = now + options.Value.Revalidate;
        registry.AbortExpired(now);
    }

    /// <summary>
    /// Routes one message on the reading loop: <c>security</c> aborts here and now, <c>realtime</c> is
    /// queued for the one sender, and anything else is logged. Nothing on this path waits on a socket.
    /// </summary>
    private void Dispatch(NpgsqlNotifyMessage message)
    {
        if (!RealtimeNotification.TryParse(message.Channel, message.Payload, out var notification)
            || notification is null)
        {
            // An unreadable message (an unknown channel, invalid JSON, no user id) is dropped with a
            // line: the loop must survive a producer of a later wave, and the channel is all that can be
            // logged without touching the payload.
            LogUnreadableMessage(message.Channel);

            return;
        }

        metrics.ListenerMessage(notification.Channel);

        if (string.Equals(notification.Channel, RealtimeNotification.SecurityChannel, StringComparison.Ordinal))
        {
            var aborted = registry.AbortUser(notification.UserId);
            LogSecurityAbort(aborted);

            return;
        }

        if (!RealtimeEventNames.TryParseWireName(notification.EventName, out var name))
        {
            // An event a later wave introduced: forward nothing (the client never receives a method it
            // did not subscribe to) and keep the loop alive (plan §5 [DECISION]).
            LogUnknownEvent(notification.EventName);

            return;
        }

        if (notification.Body is not { } body || !NamesSameUser(body, notification.UserId))
        {
            // [DECISION] Defence in depth: the id the forwarded body carries must be the id the message
            // is routed by, so a malformed body can never reach a group it did not name.
            LogUserIdMismatch();

            return;
        }

        Enqueue(new Delivery(notification.UserId, name, body));
    }

    /// <summary>
    /// The delivery queue's one consumer: it owns every group send, so the reading loop never waits on a
    /// client and a slow socket cannot hold the <c>LISTEN</c> session, the <c>security</c> abort or the
    /// backstop.
    /// </summary>
    /// <param name="stoppingToken">The loop's shutdown token.</param>
    private async Task DeliverAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var delivery in _deliveries.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await DeliverOneAsync(delivery, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Sends one queued event, bounded by <c>Realtime:DeliveryTimeoutSeconds</c>.</summary>
    private async Task DeliverOneAsync(Delivery delivery, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(options.Value.DeliveryTimeout);

        try
        {
            await sender.SendAsync(delivery.UserId, delivery.Name, delivery.Body, timeout.Token)
                .ConfigureAwait(false);

            // SignalR's write path cancels the flush, aborts the socket and then completes the write task
            // *successfully*, so a timeout can come back as a normal return; the deadline is what says the
            // event was not delivered in time.
            if (timeout.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                metrics.ListenerDeliveryFailed();
                LogDeliveryTimedOut();
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // A socket that cannot take the write within Realtime:DeliveryTimeoutSeconds must not hold
            // this consumer — and every event behind it — any longer; its client refetches on reconnect.
            metrics.ListenerDeliveryFailed();
            LogDeliveryTimedOut();
        }
        catch (Exception exception)
        {
            metrics.ListenerDeliveryFailed();
            LogDeliveryFailed(exception.GetType().Name);
        }
    }

    /// <summary>
    /// Queues one event for the sender without ever blocking the reading loop; a full queue evicts its
    /// oldest entry and counts it (the channel's drop callback), never the newest.
    /// </summary>
    /// <param name="delivery">The event the pump routed.</param>
    private void Enqueue(Delivery delivery) => _deliveries.Writer.TryWrite(delivery);

    /// <summary>Whether the forwarded body still names <paramref name="userId"/> as its subject.</summary>
    private static bool NamesSameUser(JsonElement body, Guid userId) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("userId", out var element)
        && element.ValueKind == JsonValueKind.String
        && Guid.TryParse(element.GetString(), out var fromBody)
        && fromBody == userId;

    [LoggerMessage(
        EventId = 2403,
        Level = LogLevel.Warning,
        Message = "realtime.listener.dropped: attempt={Attempt} backoff={BackoffSeconds}s error={ErrorType}")]
    private partial void LogListenerDropped(int attempt, int backoffSeconds, string errorType);

    [LoggerMessage(
        EventId = 2404,
        Level = LogLevel.Warning,
        Message = "realtime.listener.unreadable: channel={Channel}")]
    private partial void LogUnreadableMessage(string channel);

    [LoggerMessage(
        EventId = 2405,
        Level = LogLevel.Warning,
        Message = "realtime.listener.unknown-event: event={WireEvent}")]
    private partial void LogUnknownEvent(string? wireEvent);

    [LoggerMessage(
        EventId = 2406,
        Level = LogLevel.Warning,
        Message = "realtime.listener.user-mismatch: the body does not name the user it is routed by")]
    private partial void LogUserIdMismatch();

    [LoggerMessage(
        EventId = 2407,
        Level = LogLevel.Information,
        Message = "realtime.listener.security: aborted={Aborted}")]
    private partial void LogSecurityAbort(int aborted);

    [LoggerMessage(
        EventId = 2408,
        Level = LogLevel.Warning,
        Message = "realtime.listener.delivery-dropped: the send queue was full; the oldest event was dropped")]
    private static partial void LogDeliveryDropped(ILogger logger);

    [LoggerMessage(
        EventId = 2409,
        Level = LogLevel.Warning,
        Message = "realtime.listener.delivery-timeout: a socket did not take the event within the delivery timeout")]
    private partial void LogDeliveryTimedOut();

    [LoggerMessage(
        EventId = 2410,
        Level = LogLevel.Warning,
        Message = "realtime.listener.delivery-failed: error={ErrorType}")]
    private partial void LogDeliveryFailed(string errorType);

    /// <summary>One event waiting for the delivery consumer.</summary>
    private readonly record struct Delivery(Guid UserId, RealtimeEventName Name, JsonElement Body);
}
