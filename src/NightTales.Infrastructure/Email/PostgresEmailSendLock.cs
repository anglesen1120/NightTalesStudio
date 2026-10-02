using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Persistence;

namespace NightTales.Infrastructure.Email;

/// <summary>
/// The per-message send lock of 09.2.6-T3, taken as a PostgreSQL session advisory lock on the job's own
/// connection.
/// </summary>
/// <remarks>
/// <para>
/// A session advisory lock is released when the session ends, so a Worker that dies mid-send cannot leave
/// the message locked forever — which a claimed status or a claimed column could. The connection is opened
/// by this type and closed by the handle, so EF keeps reusing it for the attempt's own writes and the
/// advisory lock stays held from the status read to the result commit.
/// </para>
/// <para>
/// The lock is taken with the blocking <c>pg_advisory_lock</c>: a job that finds the message already being
/// sent waits for that send to commit and then leaves at the idempotency guard. A non-blocking variant would
/// return a successful job for a message nobody sent, so a wait that outlives the connection's command
/// timeout is preferred and is retried by Hangfire.
/// </para>
/// <para>
/// The key is derived from all sixteen bytes of the message's UUID: <c>pg_advisory_lock(int4, int4)</c> takes
/// two keys, and both halves of the UUID enter each of them. Hashing only the leading bytes would be a bug,
/// because a UUIDv7 leads with its creation timestamp: every message queued in the same millisecond would
/// share that prefix and two unrelated sends would serialise — and, under a non-blocking lock, one of them
/// would be dropped.
/// </para>
/// <para>
/// One in-flight send holds one pooled connection (the job's own) for as long as it holds the lock, and the
/// job releases it before writing a compensating failure through another scope — so the Worker's
/// <c>Jobs:Workers:&lt;queue&gt;</c> count must stay below the Npgsql pool size.
/// </para>
/// </remarks>
internal sealed class PostgresEmailSendLock : IEmailSendLock
{
    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAsync(
        IAppDbContext database,
        Guid emailMessageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        var (first, second) = LockKey(emailMessageId);
        var connection = database.Database.GetDbConnection();

        await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var locked = false;
        try
        {
            await LockAsync(connection, first, second, cancellationToken).ConfigureAwait(false);
            locked = true;
        }
        finally
        {
            if (!locked)
            {
                // The connection was opened for a lock this call does not hold: give the reference back so
                // the job's own DbContext is not left holding an open connection on a path that is about to
                // fail. (The command timeout of the wait is what ends in this branch.)
                await database.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        return new Handle(database, connection, first, second);
    }

    /// <summary>Takes the advisory lock on the connection's session, waiting until it is available.</summary>
    /// <param name="connection">The open connection; the lock lives on its session.</param>
    /// <param name="first">The first key of the pair.</param>
    /// <param name="second">The second key of the pair.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    private static async Task LockAsync(
        DbConnection connection,
        int first,
        int second,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_lock(@key1, @key2)";
        AddKeys(command, first, second);

        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases the advisory lock; best effort, because a released session releases its locks too.</summary>
    /// <param name="connection">The connection the lock was taken on.</param>
    /// <param name="first">The first key of the pair.</param>
    /// <param name="second">The second key of the pair.</param>
    private static async Task UnlockAsync(DbConnection connection, int first, int second)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(@key1, @key2)";
            AddKeys(command, first, second);

            _ = await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // Best effort only. If the session is already gone its locks went with it; if the physical
            // connection goes back to the pool, Npgsql resets it with DISCARD ALL, which runs
            // `pg_advisory_unlock_all` — so nothing stays locked either way.
        }
    }

    /// <summary>Adds the two keys the <c>pg_advisory_lock(int4, int4)</c> form takes.</summary>
    /// <param name="command">The command being built.</param>
    /// <param name="first">The first key of the pair.</param>
    /// <param name="second">The second key of the pair.</param>
    private static void AddKeys(DbCommand command, int first, int second)
    {
        var firstParameter = command.CreateParameter();
        firstParameter.ParameterName = "key1";
        firstParameter.Value = first;
        command.Parameters.Add(firstParameter);

        var secondParameter = command.CreateParameter();
        secondParameter.ParameterName = "key2";
        secondParameter.Value = second;
        command.Parameters.Add(secondParameter);
    }

    /// <summary>The lock-key pair of one message: both halves of its UUID folded into each key.</summary>
    /// <param name="emailMessageId">The <c>email_messages</c> row id.</param>
    /// <returns>
    /// Two 32-bit keys over all sixteen bytes, so two messages that share a UUIDv7 timestamp prefix —
    /// everything queued in the same millisecond — do not share a lock.
    /// </returns>
    private static (int First, int Second) LockKey(Guid emailMessageId)
    {
        var bytes = emailMessageId.ToByteArray();

        return (BitConverter.ToInt32(bytes, 0) ^ BitConverter.ToInt32(bytes, 8),
                BitConverter.ToInt32(bytes, 4) ^ BitConverter.ToInt32(bytes, 12));
    }

    /// <summary>Owns the connection the lock lives on; disposing releases the lock and the connection.</summary>
    /// <remarks>
    /// Disposal is idempotent: the job releases the handle early on the one path that writes a compensating
    /// failure through another connection, and the scope-exit disposal of the same handle must then be a
    /// no-op instead of a second unlock on a closed connection.
    /// </remarks>
    /// <param name="database">The job's write model, whose connection was opened for the lock.</param>
    /// <param name="connection">The connection whose session holds the lock.</param>
    /// <param name="first">The first key of the pair.</param>
    /// <param name="second">The second key of the pair.</param>
    private sealed class Handle(
        IAppDbContext database,
        DbConnection connection,
        int first,
        int second) : IAsyncDisposable
    {
        private bool _released;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;

            try
            {
                await UnlockAsync(connection, first, second).ConfigureAwait(false);
            }
            finally
            {
                // The connection is closed on every path, so an unlock that failed cannot leave the job's
                // DbContext holding an open connection.
                await database.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }
}
