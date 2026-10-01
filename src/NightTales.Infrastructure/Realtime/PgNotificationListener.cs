using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Realtime;

/// <summary>
/// The production <see cref="INpgsqlListenerConnectionFactory"/>: it hands out
/// <see cref="PgNotificationListener"/> connections over the platform's own database, using the same
/// connection string the persistence layer reads (<c>ConnectionStrings:Postgres</c>, 09 README D8).
/// </summary>
/// <param name="configuration">The host configuration carrying <c>ConnectionStrings:Postgres</c>.</param>
public sealed class PgNotificationListenerFactory(IConfiguration configuration) : INpgsqlListenerConnectionFactory
{
    /// <inheritdoc />
    public INpgsqlListenerConnection Create() => new PgNotificationListener(ConnectionString());

    /// <summary>
    /// The connection string of the listener's own session, validated here rather than at the first
    /// reconnect: a host that cannot reach its database must say so with the missing key's name.
    /// </summary>
    private string ConnectionString()
    {
        var connectionString = configuration.GetConnectionString(PersistenceOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Configuration value 'ConnectionStrings:{PersistenceOptions.ConnectionStringName}' is required by the "
                + "real-time listener and must not be blank.");
        }

        return connectionString;
    }
}

/// <summary>
/// The Api instance's <c>LISTEN</c> session (09 README BD11): one Npgsql connection that listens on
/// <c>realtime</c> and <c>security</c> and queues what PostgreSQL delivers.
/// </summary>
/// <remarks>
/// <para>
/// <c>LISTEN</c> belongs to the session, so this connection is deliberately long-lived and is never an
/// EF <c>DbContext</c>: a pooled context returns its session to the pool after every command and would
/// silently stop receiving. Npgsql's asynchronous notification queue is pumped by
/// <see cref="WaitAsync"/>; the handler only enqueues, so a message is never processed on the provider's
/// callback thread.
/// </para>
/// <para>
/// The two <c>LISTEN</c> statements are literals built from the channel constants of
/// <see cref="RealtimeNotification"/>, never from input, so they cannot be parameterized (PostgreSQL does
/// not parameterize identifiers) and cannot be injected either.
/// </para>
/// </remarks>
/// <param name="connectionString">The database session's connection string; never logged.</param>
public sealed class PgNotificationListener(string connectionString) : INpgsqlListenerConnection
{
    private readonly PgNotificationInbox _inbox = new();
    private NpgsqlConnection? _connection;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Notification += OnNotification;

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ListenAsync(connection, RealtimeNotification.RealtimeChannel, cancellationToken).ConfigureAwait(false);
            await ListenAsync(connection, RealtimeNotification.SecurityChannel, cancellationToken).ConfigureAwait(false);
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
    public ValueTask<NpgsqlNotifyMessage?> WaitAsync(TimeSpan heartbeat, CancellationToken cancellationToken)
    {
        var connection = _connection
            ?? throw new InvalidOperationException("The listener connection is not open; ConnectAsync must run first.");

        return _inbox.NextAsync(heartbeat, (timeout, token) => connection.WaitAsync(timeout, token), cancellationToken);
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
        _inbox.Enqueue(new NpgsqlNotifyMessage(args.Channel, args.Payload));

    private static async Task ListenAsync(NpgsqlConnection connection, string channel, CancellationToken cancellationToken)
    {
        // A constant, so the statement is built by concatenation on purpose: LISTEN takes an identifier
        // and PostgreSQL does not accept a parameter there.
        var statement = string.Equals(channel, RealtimeNotification.RealtimeChannel, StringComparison.Ordinal)
            || string.Equals(channel, RealtimeNotification.SecurityChannel, StringComparison.Ordinal)
                ? "LISTEN " + channel
                : throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown real-time channel.");

        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The inbound queue of one <c>LISTEN</c> session (09 README BD11) and its drain policy: a message the
/// provider's notification callback already queued is handed back <em>before</em> the session waits.
/// </summary>
/// <remarks>
/// <para>
/// Npgsql runs the notification callback while it processes a command or a keepalive — not only while a
/// wait is pending — so the queue can hold a message while the provider has nothing left to read. Waiting
/// first would hold it for a whole heartbeat, the <c>security</c> signal that closes a revoked socket
/// included; the drain therefore comes first and the wait runs only on an empty queue (F3).
/// </para>
/// <para>
/// It holds no provider of its own, so its contract is unit-tested without a database; production reaches
/// it only through <see cref="PgNotificationListener"/>.
/// </para>
/// </remarks>
public sealed class PgNotificationInbox
{
    private readonly ConcurrentQueue<NpgsqlNotifyMessage> _pending = new();

    /// <summary>How many messages are waiting to be read.</summary>
    public int Count => _pending.Count;

    /// <summary>Queues one message the provider's notification callback delivered.</summary>
    /// <param name="message">The message, exactly as PostgreSQL delivered it.</param>
    public void Enqueue(NpgsqlNotifyMessage message) => _pending.Enqueue(message);

    /// <summary>
    /// Returns the next queued message without waiting; waits through <paramref name="wait"/> only when
    /// nothing is queued, then returns what that wait queued — or <c>null</c> on a heartbeat.
    /// </summary>
    /// <param name="heartbeat">How long the wait may take.</param>
    /// <param name="wait">The provider's wait; it pumps the callbacks that enqueue here.</param>
    /// <param name="cancellationToken">The loop's shutdown token.</param>
    public async ValueTask<NpgsqlNotifyMessage?> NextAsync(
        TimeSpan heartbeat,
        Func<TimeSpan, CancellationToken, Task> wait,
        CancellationToken cancellationToken)
    {
        if (_pending.TryDequeue(out var queued))
        {
            return queued;
        }

        await wait(heartbeat, cancellationToken).ConfigureAwait(false);

        return _pending.TryDequeue(out var message) ? message : null;
    }
}
