using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Flags;

namespace NightTales.Infrastructure.Persistence.Flags;

/// <summary>
/// The cross-instance half of the flag eviction (09 README BD13, 09.9.2-T2 §Eviction): one
/// <c>LISTEN feature_flags_changed</c> session per instance whose notifications drop the named key from
/// this process's cache, so a flag an operator flips is believed everywhere in milliseconds instead of
/// waiting out the 30 s window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing but the eviction is fatal.</b> The loop never escapes with an exception (the default
/// <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>): a database that is down when the host
/// starts, a flapping connection and a failed wait are all reconnect attempts on the BD11 schedule
/// (1, 2, 4, 8, 16, 30 s, then 30 s forever). The 30 s cache window is the bound when a notification is
/// lost, so a deaf instance is stale, never wrong forever.
/// </para>
/// <para>
/// The schedule restarts from its first entry only after a wait has returned — the proof the session is
/// live — so an outage that keeps the session from ever reaching its first wait ramps instead of retrying
/// once a second.
/// </para>
/// <para>
/// The payload is the flag key. Anything else is dropped unread and logged: the writer is the only
/// publisher, so a payload that is not a key is a bug this line names, not a cache key to guess at.
/// </para>
/// </remarks>
/// <param name="connections">Creates the long-lived session (a real Npgsql connection in production).</param>
/// <param name="cache">The process-wide cache the reader fills and this service evicts from.</param>
/// <param name="options">The heartbeat and the reconnect schedule.</param>
/// <param name="delay">The pause between reconnect attempts; a test walks the schedule without waiting.</param>
/// <param name="logger">The service's own lines — a key, a failure type, never a payload body.</param>
public sealed partial class FeatureFlagsCacheInvalidator(
    IFeatureFlagsListenerConnectionFactory connections,
    IMemoryCache cache,
    IOptions<FeatureFlagsOptions> options,
    IFeatureFlagsListenerDelay delay,
    ILogger<FeatureFlagsCacheInvalidator> logger) : BackgroundService
{
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the loop holds an open session — the barrier a test awaits instead of sleeping, and
    /// the only public state of this service.
    /// </summary>
    /// <param name="ct">The test's own bound on how long to wait.</param>
    public Task ListeningAsync(CancellationToken ct) => _listening.Task.WaitAsync(ct);

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => ListenAsync(stoppingToken);

    /// <summary>
    /// Holds the one <c>LISTEN</c> session, reconnecting on the BD11 schedule until the host stops.
    /// </summary>
    /// <param name="stoppingToken">The loop's shutdown token.</param>
    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var connection = connections.Create();

            try
            {
                await connection.ConnectAsync(stoppingToken).ConfigureAwait(false);

                _listening.TrySetResult();

                await PumpAsync(connection, () => attempt = 0, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // One warning and one reconnect per attempt. The exception's *type* is logged, never the
                // exception itself: a provider message can carry the connection string (09 README D17).
                var backoff = options.Value.BackoffFor(attempt);
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
    /// wait, which is also the loop's pause.
    /// </summary>
    /// <param name="connection">The session created for this attempt.</param>
    /// <param name="sessionAlive">
    /// Run once a wait has returned: a returned wait — a message or the heartbeat — is the proof the
    /// session is live, so it is the only thing that resets the reconnect schedule.
    /// </param>
    /// <param name="stoppingToken">The loop's shutdown token.</param>
    private async Task PumpAsync(
        IFeatureFlagsListenerConnection connection,
        Action sessionAlive,
        CancellationToken stoppingToken)
    {
        var heartbeat = TimeSpan.FromSeconds(options.Value.HeartbeatSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var payload = await connection.WaitAsync(heartbeat, stoppingToken).ConfigureAwait(false);

            sessionAlive();

            if (payload is not null)
            {
                Evict(payload);
            }
        }
    }

    /// <summary>Drops the named key from this instance's cache, or names a payload that is not a key.</summary>
    /// <param name="payload">The notification's payload, as PostgreSQL delivered it.</param>
    private void Evict(string payload)
    {
        if (!FeatureFlagKey.IsValid(payload))
        {
            LogUnreadablePayload(payload.Length);

            return;
        }

        cache.Remove(FeatureFlagsCache.Key(payload));
        LogEvicted(payload);
    }

    [LoggerMessage(
        EventId = 2324,
        Level = LogLevel.Warning,
        Message = "flags.invalidator.dropped attempt={Attempt} backoffSeconds={BackoffSeconds} type={FailureType}")]
    private partial void LogListenerDropped(int attempt, int backoffSeconds, string failureType);

    [LoggerMessage(EventId = 2325, Level = LogLevel.Information, Message = "flags.invalidator.evicted key={Key}")]
    private partial void LogEvicted(string key);

    [LoggerMessage(EventId = 2326, Level = LogLevel.Warning, Message = "flags.invalidator.unreadable-payload bytes={Bytes}")]
    private partial void LogUnreadablePayload(int bytes);
}
