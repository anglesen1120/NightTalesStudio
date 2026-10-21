using System.Data;
using System.Net.Sockets;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The raw-SQL plumbing the security stores share (09.10.4-T1): a command over the scope's connection and
/// its open transaction, plus the transient-failure mapping every one of them answers with.
/// </summary>
/// <remarks>
/// The same shape 09.3.2-T1's <c>EfRefreshTokenService</c> uses privately, extracted here because three
/// of this task's types need it: the secret store, the recovery-code service and the TOTP verifier's
/// compare-and-set all take a row lock or write a column no aggregate owns, inside the caller's
/// transaction (09 README D5).
/// </remarks>
internal static class SecuritySql
{
    /// <summary>
    /// Opens the scope's connection when a statement runs outside a transaction; inside one it is
    /// already open.
    /// </summary>
    /// <param name="database">The scope's write model.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static async Task EnsureOpenAsync(AppDbContext database, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A command over the scope's connection and, when required, its open transaction, carrying the
    /// configured <c>Persistence:CommandTimeoutSeconds</c> (09 README D8).
    /// </summary>
    /// <param name="database">The scope's write model.</param>
    /// <param name="sql">The statement to run.</param>
    /// <param name="requireTransaction">
    /// Whether the statement must join a transaction — every write and every row lock does, so a caller
    /// outside the command's transaction fails loudly instead of taking a lock that dies with the
    /// statement (09 README D5).
    /// </param>
    /// <exception cref="InvalidOperationException">The statement requires a transaction and none is open.</exception>
    internal static NpgsqlCommand Command(AppDbContext database, string sql, bool requireTransaction)
    {
        var transaction = database.Database.CurrentTransaction;
        if (requireTransaction && transaction is null)
        {
            throw new InvalidOperationException(
                "The MFA stores run inside the command's transaction (09 README D5); the statement was "
                + "issued outside one, where a row lock would not survive it.");
        }

        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction?.GetDbTransaction() as NpgsqlTransaction;
        command.CommandTimeout = database.Database.GetCommandTimeout() ?? PersistenceOptions.DefaultCommandTimeoutSeconds;

        return command;
    }

    /// <summary>Whether the failure is a transient database problem rather than a bug or bad input.</summary>
    /// <param name="exception">The failure to classify.</param>
    internal static bool IsTransient(Exception exception) =>
        exception is NpgsqlException { IsTransient: true }
        || exception.InnerException is NpgsqlException { IsTransient: true }
        || exception is NpgsqlException { InnerException: SocketException }
        || exception is SocketException;

    /// <summary>
    /// Takes a transaction-scoped advisory lock on one user, so the MFA lifecycle's setup, verification
    /// and disable cannot interleave for the same account.
    /// </summary>
    /// <remarks>
    /// The pending secret is read, verified, promoted and deleted across several statements; without one
    /// lock over all of them a concurrent setup could replace the pending payload between the read and the
    /// promotion, so an unverified secret would be activated. The lock is taken <em>first</em> by every
    /// path of the lifecycle and released at commit (any whole-transaction quiescence), like the refresh
    /// families' <c>LockFamilyAsync</c>. A different advisory-lock seed keeps a user lock from colliding
    /// with a family lock.
    /// </remarks>
    /// <param name="database">The scope's write model; its transaction holds the lock.</param>
    /// <param name="userId">The user serialized by the lock.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static async Task LockUserAsync(AppDbContext database, Guid userId, CancellationToken ct)
    {
        await using var command = Command(
            database,
            "SELECT pg_advisory_xact_lock(hashtextextended(@user_id::text, 1))",
            requireTransaction: true);
        command.Parameters.Add(new NpgsqlParameter("user_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = userId });

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes a transaction-scoped advisory lock on one action-token scope — <c>(user, kind, subject)</c> —
    /// so two issues of the same scope cannot both leave a live row.
    /// </summary>
    /// <remarks>
    /// The issue is an <c>UPDATE … INSERT</c>: under <c>ReadCommitted</c> two callers that each see no live
    /// row for the scope both update zero rows and both insert, leaving two live links where the design
    /// promises at most one (09 README BD3/WF11). The lock is taken <em>before</em> the update and held
    /// through the insert, and the same lock key covers the plain flow (<c>subject_id</c> NULL) and a
    /// subject-scoped one. A different advisory-lock seed (<c>2</c>) keeps it from colliding with a user
    /// lock (<c>1</c>) or a refresh family lock (<c>0</c>).
    /// </remarks>
    /// <param name="database">The scope's write model; its transaction holds the lock.</param>
    /// <param name="userId">The user the token authorizes.</param>
    /// <param name="kind">The flow's snake_case wire value.</param>
    /// <param name="subjectId">The row a subject-scoped token is about, or null for a plain flow.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static async Task LockActionTokenScopeAsync(
        AppDbContext database,
        Guid userId,
        string kind,
        Guid? subjectId,
        CancellationToken ct)
    {
        await using var command = Command(
            database,
            "SELECT pg_advisory_xact_lock(hashtextextended(@scope::text, 2))",
            requireTransaction: true);

        // The key is unambiguous: the fixed-width user id, then the kind (never contains ':'), then the
        // fixed-width subject or '-' for a plain flow.
        command.Parameters.AddWithValue(
            "scope",
            $"{userId:N}:{kind}:{subjectId?.ToString("N") ?? "-"}");

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The 503 of a transient failure, mapped exactly as the unit of work maps a refused commit.</summary>
    /// <typeparam name="T">The response type of the caller.</typeparam>
    internal static ErrorOr<T> Unavailable<T>() =>
        Error.Failure(
            code: ErrorCodes.ServiceUnavailable,
            description: "The database is temporarily unavailable; retry.");
}
