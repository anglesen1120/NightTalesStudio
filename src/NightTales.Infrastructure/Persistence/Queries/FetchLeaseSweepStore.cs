using System.Data.Common;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Jobs;

namespace NightTales.Infrastructure.Persistence.Queries;

/// <summary>
/// The sweeper's one statement over <c>fetch_tasks</c> (02.5.2-T2 plan, <c>RequeueExpiredLeases</c>): raw SQL on the scope's
/// connection, no transaction of its own — the statement is atomic and a re-run finds nothing it already changed.
/// </summary>
/// <remarks>
/// <para>
/// It is a cross-user write by design (the fetcher has no user), like the outbox and idempotency cleanups: it names no user
/// data and returns ids only. The caller then fails each source in its owner's pinned scope.
/// </para>
/// <para>
/// The guard <c>status = 'leased' AND lease_until &lt; now()</c> is the whole concurrency story: the first run flips a row's
/// status, and a concurrent run's <c>UPDATE</c> re-evaluates the guard after the first commits and skips the row, so no
/// advisory lock is needed. The clock is the database's <c>now()</c>, the same clock the lease statement wrote
/// <c>lease_until</c> with.
/// </para>
/// </remarks>
public sealed class FetchLeaseSweepStore(AppDbContext database) : IFetchLeaseSweepStore
{
    /// <inheritdoc />
    public async Task<ErrorOr<IReadOnlyList<ExpiredFetchLease>>> RequeueExpiredLeasesAsync(
        int maxAttempts,
        int backoffSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backoffSeconds);

        var opened = false;

        try
        {
            await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            opened = true;

            await using var command = database.Database.GetDbConnection().CreateCommand();
            command.CommandText = Sql;
            command.Parameters.Add(new NpgsqlParameter("maxAttempts", maxAttempts));
            command.Parameters.Add(new NpgsqlParameter("backoffSeconds", backoffSeconds));

            var rows = new List<ExpiredFetchLease>();
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ExpiredFetchLease(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(2),
                    reader.GetGuid(3),
                    reader.GetInt32(4),
                    string.Equals(reader.GetString(5), FailedStatus, StringComparison.Ordinal)));
            }

            return rows;
        }
        catch (Exception exception) when (IsTransient(exception) || IsDeadline(exception, cancellationToken))
        {
            // A statement deadline (the command timeout surfaces as an OperationCanceledException that the caller's
            // token did not raise) and a transient provider failure both mean "nothing changed, try again".
            return Error.Failure(
                code: ErrorCodes.ServiceUnavailable,
                description: "The database is temporarily unavailable; retry.");
        }
        finally
        {
            if (opened)
            {
                await database.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception exception) =>
        exception is NpgsqlException { IsTransient: true }
        || exception.InnerException is NpgsqlException { IsTransient: true };

    private static bool IsDeadline(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private const string FailedStatus = "failed";

    // The task's SQL verbatim; RETURNING also names user_id, which the job needs to pin the owner's scope for the source.
    private const string Sql =
        """
        UPDATE fetch_tasks
        SET status = CASE WHEN attempts >= @maxAttempts THEN 'failed' ELSE 'queued' END,
            error_code = CASE WHEN attempts >= @maxAttempts THEN 'source_unreachable' ELSE NULL END,
            leased_by = NULL,
            lease_until = NULL,
            leased_at = NULL,
            not_before = CASE WHEN attempts >= @maxAttempts THEN not_before ELSE now() + make_interval(secs => @backoffSeconds) END,
            finished_at = CASE WHEN attempts >= @maxAttempts THEN now() ELSE NULL END
        WHERE status = 'leased' AND lease_until < now()
        RETURNING id, user_id, source_id, episode_id, attempts, status
        """;
}
