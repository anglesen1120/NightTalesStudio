using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace NightTales.Infrastructure.Persistence.Flags;

/// <summary>
/// Creates the one long-lived <c>LISTEN</c> session an Api or Worker instance keeps for flag eviction
/// (09 README BD13, 09.9.2-T2 §Eviction): one connection per instance, never one per request and never a
/// pooled EF <c>DbContext</c>.
/// </summary>
/// <remarks>
/// The factory is the seam the invalidator's loop — and its tests — create a connection through:
/// production hands out <see cref="PgFeatureFlagsListener"/>, while a test drives the reconnect schedule
/// and the eviction with a hand-written connection and no server.
/// </remarks>
public interface IFeatureFlagsListenerConnectionFactory
{
    /// <summary>Creates an unopened session to the platform's PostgreSQL database.</summary>
    IFeatureFlagsListenerConnection Create();
}

/// <summary>
/// The invalidator's <c>LISTEN feature_flags_changed</c> session: it hands back one notification payload
/// at a time, or <see langword="null"/> when the wait was only a heartbeat.
/// </summary>
public interface IFeatureFlagsListenerConnection : IAsyncDisposable
{
    /// <summary>Opens the connection and issues <c>LISTEN feature_flags_changed</c>, or throws.</summary>
    /// <param name="ct">The loop's shutdown token.</param>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// Returns the next payload a callback already queued without waiting, or waits up to
    /// <paramref name="heartbeat"/> for one and returns <see langword="null"/> when none arrived — the
    /// loop's pause, never a busy wait.
    /// </summary>
    /// <param name="heartbeat">How long to wait before treating the wait as a heartbeat.</param>
    /// <param name="ct">The loop's shutdown token.</param>
    ValueTask<string?> WaitAsync(TimeSpan heartbeat, CancellationToken ct);
}

/// <summary>
/// The production factory (09.9.2 plan §7): it hands out <see cref="PgFeatureFlagsListener"/> sessions
/// over the same connection string the persistence layer reads
/// (<c>ConnectionStrings:Postgres</c>, 09 README D8).
/// </summary>
/// <param name="configuration">The host configuration carrying the connection string.</param>
public sealed class PgFeatureFlagsListenerFactory(IConfiguration configuration) : IFeatureFlagsListenerConnectionFactory
{
    /// <inheritdoc />
    public IFeatureFlagsListenerConnection Create() => new PgFeatureFlagsListener(ConnectionString());

    /// <summary>
    /// The connection string of the invalidator's own session, validated here rather than at the first
    /// reconnect: a host that cannot reach its database must say so with the missing key's name.
    /// </summary>
    private string ConnectionString()
    {
        var connectionString = configuration.GetConnectionString(PersistenceOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Configuration value 'ConnectionStrings:{PersistenceOptions.ConnectionStringName}' is required by the "
                + "feature-flag cache invalidator and must not be blank.");
        }

        return connectionString;
    }
}

/// <summary>
/// The instance's <c>LISTEN feature_flags_changed</c> session (09 README BD13): one Npgsql connection
/// that queues what PostgreSQL delivers and never blocks on it.
/// </summary>
/// <remarks>
/// <para>
/// <c>LISTEN</c> belongs to the session, so this connection is deliberately long-lived and is never an
/// EF <c>DbContext</c>: a pooled context returns its session to the pool after every command and would
/// silently stop receiving. Npgsql's notification callback only enqueues, so nothing is processed on the
/// provider's thread.
/// </para>
/// <para>
/// The drain comes first and the wait runs only on an empty queue: Npgsql runs the callback while it
/// processes a command or a keepalive, so a message can already be queued when the loop comes back —
/// waiting first would hold it for a whole heartbeat (the same policy as BD11's listener).
/// </para>
/// <para>
/// The <c>LISTEN</c> statement is a literal built from the one channel constant, never from input:
/// PostgreSQL does not parameterize an identifier, so concatenation is the only form and it cannot be
/// injected.
/// </para>
/// </remarks>
/// <param name="connectionString">The session's connection string; never logged.</param>
public sealed class PgFeatureFlagsListener(string connectionString) : IFeatureFlagsListenerConnection
{
    private readonly ConcurrentQueue<string> _pending = new();
    private NpgsqlConnection? _connection;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct)
    {
        // A pooled backend can still carry unread NOTIFY frames from its previous LISTEN session.
        // Reset runs when the next command is sent, after our callback is attached. Own the backend
        // instead: this instance already keeps one long-lived connection and disposal closes it.
        var listenerConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
        }.ConnectionString;
        var connection = new NpgsqlConnection(listenerConnectionString);
        connection.Notification += OnNotification;

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "LISTEN " + FeatureFlagsCache.ChangedChannel;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            connection.Notification -= OnNotification;
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _connection = connection;
    }

    /// <inheritdoc />
    public async ValueTask<string?> WaitAsync(TimeSpan heartbeat, CancellationToken ct)
    {
        var connection = _connection
            ?? throw new InvalidOperationException("The listener connection is not open; ConnectAsync must run first.");

        if (_pending.TryDequeue(out var queued))
        {
            return queued;
        }

        await connection.WaitAsync(heartbeat, ct).ConfigureAwait(false);

        return _pending.TryDequeue(out var received) ? received : null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var connection = _connection;
        _connection = null;

        if (connection is null)
        {
            return;
        }

        connection.Notification -= OnNotification;
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs args) =>
        _pending.Enqueue(args.Payload);
}

/// <summary>
/// The reconnect pause of the invalidator's loop (09.9.2 plan §9): the schedule is asserted without
/// waiting, exactly as BD11's listener tests assert theirs.
/// </summary>
public interface IFeatureFlagsListenerDelay
{
    /// <summary>Waits <paramref name="delay"/>, or throws <see cref="OperationCanceledException"/> on shutdown.</summary>
    /// <param name="delay">How long to wait.</param>
    /// <param name="ct">The loop's shutdown token.</param>
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

/// <summary>The production pause: a plain <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
public sealed class SystemFeatureFlagsListenerDelay : IFeatureFlagsListenerDelay
{
    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        Task.Delay(delay, ct);
}
