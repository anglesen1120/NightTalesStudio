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
/// The key is the first eight bytes of the message's UUID: 64 bits make a collision between two live
/// messages so unlikely that it is not a correctness dependency (a collision would only serialise two
/// unrelated sends).
/// </para>
/// </remarks>
internal sealed class PostgresEmailSendLock : IEmailSendLock
{
    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryAcquireAsync(
        IAppDbContext database,
        Guid emailMessageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        var key = LockKey(emailMessageId);
        var connection = database.Database.GetDbConnection();

        await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (!await TryLockAsync(connection, key, cancellationToken).ConfigureAwait(false))
        {
            await database.Database.CloseConnectionAsync().ConfigureAwait(false);

            return null;
        }

        return new Handle(database, connection, key);
    }

    /// <summary>Takes the advisory lock, answering <see langword="false"/> when another session holds it.</summary>
    /// <param name="connection">The open connection; the lock lives on its session.</param>
    /// <param name="key">The 64-bit lock key.</param>
    /// <param name="cancellationToken">Cancels the round trip.</param>
    private static async Task<bool> TryLockAsync(
        DbConnection connection,
        long key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@key)";
        command.Parameters.Add(Parameter(command, key));

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>Releases the advisory lock; a failure here is harmless because closing the session also releases it.</summary>
    /// <param name="connection">The connection the lock was taken on.</param>
    /// <param name="key">The 64-bit lock key.</param>
    private static async Task UnlockAsync(DbConnection connection, long key)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(@key)";
            command.Parameters.Add(Parameter(command, key));

            await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // The session is already gone (a cancelled or dead connection), and closing it releases every
            // advisory lock it held, so nothing is leaked and there is nothing to report.
        }
    }

    /// <summary>The command's <c>@key</c> parameter, built through the provider so the type stays the connection's.</summary>
    /// <param name="command">The command being built.</param>
    /// <param name="key">The 64-bit lock key.</param>
    private static DbParameter Parameter(DbCommand command, long key)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = key;

        return parameter;
    }

    /// <summary>The 64-bit lock key of one message: the first eight bytes of its UUID.</summary>
    /// <param name="emailMessageId">The <c>email_messages</c> row id.</param>
    private static long LockKey(Guid emailMessageId) =>
        BitConverter.ToInt64(emailMessageId.ToByteArray(), 0);

    /// <summary>Owns the connection the lock lives on; disposing releases the lock and the connection.</summary>
    /// <param name="database">The job's write model, whose connection was opened for the lock.</param>
    /// <param name="connection">The connection whose session holds the lock.</param>
    /// <param name="key">The 64-bit lock key.</param>
    private sealed class Handle(IAppDbContext database, DbConnection connection, long key) : IAsyncDisposable
    {
        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await UnlockAsync(connection, key).ConfigureAwait(false);
            await database.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
