using System.Data;
using Npgsql;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// The single-instance guard of the health poller (11 README D13, Q5): a PostgreSQL session advisory lock,
/// <c>pg_try_advisory_lock(hashtext('node_health_poller'))</c>, held on a dedicated connection for as long
/// as this Worker polls.
/// </summary>
/// <remarks>
/// <para>
/// A second Worker on the same database therefore polls nothing and only logs once per start, while both
/// instances keep serving <c>nodes list</c> (plan §7 step 1, Q5). The lock is session-scoped, so it dies
/// with the connection: <see cref="IsHeld"/> reports a broken connection as "not held" and the next tick
/// re-acquires instead of silently polling on a lost lock.
/// </para>
/// <para>
/// It is deliberately not a transaction lock: the poller's writes run in their own scope and must commit
/// between ticks, while the guard has to outlive each tick.
/// </para>
/// </remarks>
/// <param name="connectionString">The database the Worker's persistence layer uses.</param>
public sealed class NodeHealthPollerLock(string connectionString) : IAsyncDisposable
{
    /// <summary>The advisory-lock key; <c>hashtext</c> turns it into the 32-bit lock id (D13).</summary>
    public const string LockKey = "node_health_poller";

    private readonly string _connectionString = connectionString;
    private NpgsqlConnection? _connection;

    /// <summary>Whether this instance currently holds the lock on a live connection.</summary>
    public bool IsHeld => _connection is { State: ConnectionState.Open };

    /// <summary>
    /// Tries to take the lock on a connection of its own. Idempotent: an instance that already holds the
    /// lock answers <c>true</c> without a second round trip.
    /// </summary>
    /// <param name="cancellationToken">The poller's stopping token.</param>
    /// <returns><c>true</c> when this instance holds the lock; <c>false</c> when another instance does.</returns>
    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (IsHeld)
        {
            return true;
        }

        await DropDeadConnectionAsync();

        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(hashtext(@key))";
        command.Parameters.AddWithValue("key", LockKey);

        if (await command.ExecuteScalarAsync(cancellationToken) is true)
        {
            _connection = connection;

            return true;
        }

        await connection.DisposeAsync();

        return false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is null)
        {
            return;
        }

        var connection = _connection;
        _connection = null;

        try
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(hashtext(@key))";
                command.Parameters.AddWithValue("key", LockKey);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // Releasing on a broken connection is a no-op server-side: the session ended with it, so the
            // lock is already gone (plan §"Failure handling": a lost lock never flips a node state).
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>Drops a connection the server or the network already closed, so the lock can be re-taken.</summary>
    private async Task DropDeadConnectionAsync()
    {
        if (_connection is not { } dead || dead.State == ConnectionState.Open)
        {
            return;
        }

        _connection = null;
        await dead.DisposeAsync();
    }
}
