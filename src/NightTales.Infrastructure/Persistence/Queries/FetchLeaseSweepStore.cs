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
    public Task<ErrorOr<IReadOnlyList<ExpiredFetchLease>>> RequeueExpiredLeasesAsync(
        int maxAttempts,
        int backoffSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backoffSeconds);

        return RunAsync<IReadOnlyList<ExpiredFetchLease>>(
            Sql,
            command =>
            {
                command.Parameters.Add(new NpgsqlParameter("maxAttempts", maxAttempts));
                command.Parameters.Add(new NpgsqlParameter("backoffSeconds", backoffSeconds));
            },
            async reader =>
            {
                var rows = new List<ExpiredFetchLease>();
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
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ErrorOr<IReadOnlyList<FailedTaskWithPendingSource>>> ListFailedTasksWithPendingSourceAsync(
        int lookbackHours,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lookbackHours);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return RunAsync<IReadOnlyList<FailedTaskWithPendingSource>>(
            PendingSourceSql,
            command =>
            {
                command.Parameters.Add(new NpgsqlParameter("lookbackHours", lookbackHours));
                command.Parameters.Add(new NpgsqlParameter("limit", limit));
            },
            async reader =>
            {
                var rows = new List<FailedTaskWithPendingSource>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(new FailedTaskWithPendingSource(
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.GetGuid(2),
                        reader.GetGuid(3),
                        await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4)));
                }

                return rows;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ErrorOr<int>> CountLeasedAsync(CancellationToken cancellationToken) =>
        RunAsync(
            "SELECT count(*) FROM fetch_tasks WHERE status = 'leased'",
            _ => { },
            async reader =>
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

                return (int)reader.GetInt64(0);
            },
            cancellationToken);

    private async Task<ErrorOr<T>> RunAsync<T>(
        string commandText,
        Action<DbCommand> bind,
        Func<DbDataReader, Task<T>> read,
        CancellationToken cancellationToken)
    {
        var opened = false;

        try
        {
            await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            opened = true;

            await using var command = database.Database.GetDbConnection().CreateCommand();
            command.CommandText = commandText;
            bind(command);

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            return await read(reader).ConfigureAwait(false);
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

    // Failed tasks whose source is still pending; `ORDER BY finished_at DESC LIMIT` bounds the pass and the join takes
    // the rows a prior crash left behind (the source is flipped in the same pass that fails the task, so a healthy
    // system returns none).
    private const string PendingSourceSql =
        """
        SELECT t.id, t.user_id, t.source_id, t.episode_id, t.error_code
        FROM fetch_tasks t
        JOIN story_sources s ON s.id = t.source_id AND s.status = 'pending'
        WHERE t.status = 'failed'
          AND t.finished_at > now() - make_interval(hours => @lookbackHours)
        ORDER BY t.finished_at DESC
        LIMIT @limit
        """;
}
