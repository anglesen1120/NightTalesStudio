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
/// with the connection: every tick probes the session itself with a cheap round trip, because a backend
/// terminated server-side can leave the client reporting <see cref="ConnectionState.Open"/> until the
/// connection is used. A session that turns out dead is dropped, the lock re-taken, and that tick skipped —
/// a lost lock never decides a node state.
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

    /// <summary>Whether this instance holds the lock on a connection it believes open; the per-tick probe is the liveness check.</summary>
    public bool IsHeld => _connection is { State: ConnectionState.Open };

    /// <summary>
    /// Runs with the freshly opened candidate connection, before the advisory-lock command. Production
    /// leaves it <c>null</c>; the harness sets it to make an acquisition fail after the connection is open,
    /// so it can prove the candidate is disposed on every path that does not take ownership.
    /// </summary>
    public Func<NpgsqlConnection, CancellationToken, Task>? OnCandidateOpened { get; set; }

    /// <summary>
    /// Tries to take the lock on a connection of its own. When the lock is already held the holding
    /// session is probed first: a backend the server terminated between ticks releases the lock without
    /// this client noticing until the connection is used.
    /// </summary>
    /// <param name="cancellationToken">The poller's stopping token.</param>
    /// <returns>The acquisition outcome the tick acts on.</returns>
    public async Task<LockAttempt> TryAcquireAsync(CancellationToken cancellationToken)
    {
        // The caller skips the tick whenever a previous session was lost (probe failure, or a connection
        // already known dead): a re-taken lock makes this instance the sole holder again, but this tick's
        // decisions could have raced another Worker's during the gap (plan §"Failure handling").
        var recovered = false;

        if (_connection is { State: ConnectionState.Open } held)
        {
            if (await IsLockSessionAliveAsync(held, cancellationToken))
            {
                return LockAttempt.Held;
            }

            recovered = true;
            await DropConnectionAsync();
        }
        else if (_connection is not null)
        {
            recovered = true;
            await DropConnectionAsync();
        }

        var connection = new NpgsqlConnection(_connectionString);
        var acquired = false;

        try
        {
            await connection.OpenAsync(cancellationToken);

            if (OnCandidateOpened is { } onOpened)
            {
                await onOpened(connection, cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(hashtext(@key))";
            command.Parameters.AddWithValue("key", LockKey);

            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                acquired = true;
                _connection = connection;

                return recovered ? LockAttempt.Recovered : LockAttempt.Held;
            }

            return LockAttempt.HeldElsewhere;
        }
        finally
        {
            // The candidate is owned by this method until it lands in _connection: every failure between
            // the open and the assignment — including a fault injected by OnCandidateOpened — must return
            // it to the pool rather than leak a busy connection.
            if (!acquired)
            {
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// A cheap round trip on the lock connection: a session advisory lock is held for the session's life,
    /// so the session answering means the lock is still ours.
    /// </summary>
    private static async Task<bool> IsLockSessionAliveAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var probe = connection.CreateCommand();
            probe.CommandText = "SELECT 1";
            await probe.ExecuteScalarAsync(cancellationToken);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A dead session (terminated backend, dropped socket, hung query) reads exactly like the
            // client-side flag: report the lock as lost and let the caller re-take it.
            return false;
        }
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

    /// <summary>Drops the held connection — dead or merely suspect — so the lock can be re-taken.</summary>
    private async Task DropConnectionAsync()
    {
        if (_connection is not { } dropped)
        {
            return;
        }

        _connection = null;
        await dropped.DisposeAsync();
    }

    /// <summary>What one <see cref="TryAcquireAsync"/> call resolved to.</summary>
    public enum LockAttempt
    {
        /// <summary>This instance holds the lock; the tick may poll.</summary>
        Held,

        /// <summary>Another instance holds the lock; the tick does nothing.</summary>
        HeldElsewhere,

        /// <summary>
        /// The previous session lost the lock and this call took it back; the tick is skipped anyway, so
        /// the gap in which another Worker could have polled applies no decisions.
        /// </summary>
        Recovered,
    }
}
