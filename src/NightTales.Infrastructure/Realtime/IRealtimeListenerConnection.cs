namespace NightTales.Infrastructure.Realtime;

/// <summary>
/// Creates the one long-lived <c>LISTEN</c> connection an Api instance keeps (09 README BD11, WF10: one
/// connection per instance, never one per request and never a pooled EF <c>DbContext</c>).
/// </summary>
/// <remarks>
/// The factory is the seam the listener's loop (and its tests) create a connection through: production
/// hands out <see cref="PgNotificationListener"/>, while a test counts the attempts of the reconnect
/// schedule without a server (09.8.1-T1 plan §9).
/// </remarks>
public interface INpgsqlListenerConnectionFactory
{
    /// <summary>Creates an unopened connection to the platform's PostgreSQL database.</summary>
    INpgsqlListenerConnection Create();
}

/// <summary>One message PostgreSQL delivered on a channel this host listens on.</summary>
/// <param name="Channel">The channel name (<c>realtime</c> or <c>security</c>).</param>
/// <param name="Payload">The payload, exactly as PostgreSQL delivered it.</param>
public readonly record struct NpgsqlNotifyMessage(string Channel, string Payload);

/// <summary>
/// The listener connection: it <c>LISTEN</c>s on both channels of this feature and hands the messages
/// back one at a time.
/// </summary>
/// <remarks>
/// <para>
/// It owns a plain Npgsql connection rather than an EF <c>DbContext</c>: a <c>LISTEN</c> registration
/// lives as long as the session, which is exactly what a pooled, per-request context must not be. The
/// two channels share the one connection, because PostgreSQL lets a session listen on many channels
/// and BD11 wants one connection per Api instance.
/// </para>
/// <para>
/// The message is returned unparsed: <c>RealtimeNotification.TryParse</c> is what decides whether it is
/// one this listener serves, and a test can therefore drive the loop with the same raw payloads
/// PostgreSQL delivers.
/// </para>
/// </remarks>
public interface INpgsqlListenerConnection : IAsyncDisposable
{
    /// <summary>
    /// Opens the connection and issues <c>LISTEN realtime; LISTEN security;</c>, or throws.
    /// </summary>
    /// <param name="cancellationToken">The loop's shutdown token.</param>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the next notification a callback already queued without waiting, or waits up to
    /// <paramref name="heartbeat"/> for one and returns <c>null</c> when none arrived — the loop's pause,
    /// not a busy wait.
    /// </summary>
    /// <param name="heartbeat">How long to wait before treating the wait as a heartbeat.</param>
    /// <param name="cancellationToken">The loop's shutdown token.</param>
    ValueTask<NpgsqlNotifyMessage?> WaitAsync(TimeSpan heartbeat, CancellationToken cancellationToken);
}
