using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Features.Platform.Retention;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// The single-flight guard of <c>RetentionSweepJob</c> (09.7.3-T1): a non-blocking PostgreSQL <em>session</em>
/// advisory lock on the job's own connection, <c>pg_try_advisory_lock(hashtext('retention_sweep'))</c>.
/// </summary>
/// <remarks>
/// A session lock, not a transaction one: the sweep commits many short transactions, and the lock has to
/// span all of them. It is released with the session, so a Worker that dies mid-sweep cannot leave it held.
/// The connection is opened here and closed by the handle, so EF keeps reusing it for the audit phase and
/// the lock stays on one session for the whole run (the pattern of <c>PostgresEmailSendLock</c>).
/// </remarks>
/// <param name="database">The job scope's context, whose connection carries the lock.</param>
public sealed class PostgresRetentionSweepLock(AppDbContext database) : IRetentionSweepLock
{
    private const string TryLockSql = "SELECT pg_try_advisory_lock(hashtext('retention_sweep'))";
    private const string UnlockSql = "SELECT pg_advisory_unlock(hashtext('retention_sweep'))";

    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
        var connection = database.Database.GetDbConnection();

        await database.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

        var locked = false;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = TryLockSql;

            locked = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
        }
        finally
        {
            if (!locked)
            {
                await database.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        return locked ? new Handle(database, connection) : null;
    }

    private sealed class Handle(AppDbContext database, DbConnection connection) : IAsyncDisposable
    {
        private bool _released;

        public async ValueTask DisposeAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = UnlockSql;

                _ = await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (DbException)
            {
                // Best effort: a released session releases its locks, and a pooled connection is reset with
                // DISCARD ALL, which runs pg_advisory_unlock_all.
            }
            finally
            {
                await database.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }
}
