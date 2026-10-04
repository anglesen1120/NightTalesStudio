using System.Net.Sockets;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Fetcher;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Research;
using NightTales.Infrastructure.Research.Http;

namespace NightTales.Infrastructure.Persistence.Queries;

/// <summary>
/// The queue claim of the fetcher's internal API (02.5.2-T2, 02 D10): raw SQL through the command's own connection and
/// transaction, because <c>FOR UPDATE SKIP LOCKED</c> with a per-domain partition is a statement EF cannot express.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lease.</b> The transaction first takes <c>pg_advisory_xact_lock</c> on one constant ([DECISION T42]), so two
/// concurrent lease calls run one after the other and the platform cap (4) and the per-domain cap (1) are exact: the
/// second statement's snapshot is taken after the first committed. <c>FOR UPDATE SKIP LOCKED</c> additionally keeps a
/// row that another transaction is changing out of the batch instead of waiting for it.
/// </para>
/// <para>
/// <b>Heartbeat and result.</b> The ownership predicate (<c>status = 'leased' AND leased_by = @workerId AND
/// lease_until &gt; now()</c>) is part of the statement that changes or locks the row, so there is no window between a
/// read and a write in which the sweeper could requeue the task. All time comparisons use the database clock, the same
/// one that stamped <c>lease_until</c>.
/// </para>
/// <para>
/// Logs carry nothing here: no statement text, no URL, no worker id (D19). A transient database failure is
/// <c>service_unavailable</c> like every other store; any other exception is a bug and propagates.
/// </para>
/// </remarks>
/// <param name="database">The scope's context; its connection and transaction are the command's.</param>
/// <param name="options">The validated <c>Fetcher</c> section.</param>
public sealed class FetchTaskLeaseStore(AppDbContext database, IOptions<FetcherOptions> options) : IFetchTaskLeaseStore
{
    /// <summary>The constant of the platform-wide lease lock ("NTSFETCH" in ASCII); a literal, not a hash, so it cannot collide with the hashed per-family and per-user locks.</summary>
    internal const long LeaseLockKey = 0x4E54_5346_4554_4348;

    private const string KindFetchSource = "fetch_source";

    private const string KindFindSources = "find_sources";

    /// <summary>The most candidates a finder run should return (D11: 10).</summary>
    private const int FinderMaxCandidates = 10;

    private const string LeaseSql = """
        WITH leased_now AS (
            SELECT count(*) AS n FROM fetch_tasks WHERE status = 'leased'
        ),
        domain_busy AS (
            SELECT DISTINCT registrable_domain FROM fetch_tasks
            WHERE status = 'leased' AND registrable_domain IS NOT NULL
        ),
        eligible AS (
            SELECT t.id, t.registrable_domain, t.not_before, t.created_at
            FROM fetch_tasks t
            WHERE t.status = 'queued'
              AND t.not_before <= now()
              AND (t.registrable_domain IS NULL OR t.registrable_domain NOT IN (SELECT registrable_domain FROM domain_busy))
        ),
        ranked AS (
            SELECT id,
                   row_number() OVER (PARTITION BY registrable_domain ORDER BY not_before, created_at) AS rn
            FROM eligible
        ),
        candidates AS (
            SELECT id
            FROM fetch_tasks
            WHERE id IN (SELECT id FROM ranked WHERE rn = 1)
            ORDER BY not_before, created_at
            FOR UPDATE SKIP LOCKED
            LIMIT LEAST(@max, GREATEST(0, @platformCap - (SELECT n FROM leased_now)))
        )
        UPDATE fetch_tasks t
        SET status = 'leased',
            leased_by = @workerId,
            leased_at = now(),
            lease_until = now() + make_interval(secs => @leaseSeconds),
            attempts = t.attempts + 1
        FROM candidates c
        WHERE t.id = c.id
        RETURNING t.id, t.kind, t.url, t.attempts, t.lease_until, t.registrable_domain
        """;

    private const string HeartbeatSql = """
        UPDATE fetch_tasks
        SET lease_until = now() + make_interval(secs => @heartbeatSeconds)
        WHERE id = @id AND status = 'leased' AND leased_by = @workerId AND lease_until > now()
        RETURNING lease_until
        """;

    private const string ClaimSql = """
        SELECT user_id, episode_id, source_id, generation_run_id, kind, leased_at,
               (status = 'leased' AND leased_by = @workerId AND lease_until > now()) AS live
        FROM fetch_tasks
        WHERE id = @id
        FOR UPDATE
        """;

    private const string FinderSql = """
        SELECT s.topic, s.language, s.title, e.brief
        FROM fetch_tasks t
        JOIN generation_runs g ON g.id = t.generation_run_id AND g.kind = 'find_sources'
        JOIN episodes e ON e.id = t.episode_id
        JOIN series s ON s.id = e.series_id
        WHERE t.id = @id
        """;

    private readonly FetcherOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<ErrorOr<IReadOnlyList<LeaseDto>>> LeaseAsync(
        string workerId,
        int max,
        CancellationToken cancellationToken)
    {
        try
        {
            // First statement of the transaction: the second caller waits here, then reads a fresh snapshot.
            await using (var lockCommand = CreateCommand("SELECT pg_advisory_xact_lock(@key)"))
            {
                lockCommand.Parameters.AddWithValue("key", LeaseLockKey);
                await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var rows = new List<LeasedRow>(max);
            await using (var command = CreateCommand(LeaseSql))
            {
                command.Parameters.AddWithValue("max", max);
                command.Parameters.AddWithValue("platformCap", _options.MaxLeasesPlatformWide);
                command.Parameters.AddWithValue("workerId", workerId);
                command.Parameters.AddWithValue("leaseSeconds", _options.LeaseSeconds);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(new LeasedRow(
                        reader.GetFieldValue<Guid>(0),
                        reader.GetString(1),
                        await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
                        reader.GetInt32(3),
                        reader.GetFieldValue<DateTimeOffset>(4)));
                }
            }

            var userAgent = SafeHttp.UserAgent(_options);
            var leases = new List<LeaseDto>(rows.Count);
            foreach (var row in rows.OrderBy(row => row.Id))
            {
                var finder = row.Kind == KindFindSources
                    ? await ReadFinderRequestAsync(row.Id, cancellationToken).ConfigureAwait(false)
                    : null;
                leases.Add(new LeaseDto(row.Id, row.Kind, row.Url, row.Attempt, row.LeaseUntil, userAgent, finder));
            }

            return leases;
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            return Unavailable();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<DateTimeOffset>> HeartbeatAsync(Guid taskId, string workerId, CancellationToken cancellationToken)
    {
        try
        {
            await using (var command = CreateCommand(HeartbeatSql))
            {
                command.Parameters.AddWithValue("id", taskId);
                command.Parameters.AddWithValue("workerId", workerId);
                command.Parameters.AddWithValue("heartbeatSeconds", _options.HeartbeatSeconds);

                var leaseUntil = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (leaseUntil is DateTime moment)
                {
                    return new DateTimeOffset(DateTime.SpecifyKind(moment, DateTimeKind.Utc));
                }

                if (leaseUntil is DateTimeOffset offset)
                {
                    return offset;
                }
            }

            // No row changed: either the task is gone (404) or this worker no longer holds a live lease (409).
            return await ExistsAsync(taskId, cancellationToken).ConfigureAwait(false)
                ? FetcherErrors.LeaseLost
                : FetcherErrors.TaskNotFound;
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            return Unavailable();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<ClaimedFetchTask>> ClaimForResultAsync(
        Guid taskId,
        string workerId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = CreateCommand(ClaimSql);
            command.Parameters.AddWithValue("id", taskId);
            command.Parameters.AddWithValue("workerId", workerId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return FetcherErrors.TaskNotFound;
            }

            if (!reader.GetBoolean(6))
            {
                return FetcherErrors.LeaseLost;
            }

            return new ClaimedFetchTask(
                taskId,
                reader.GetFieldValue<Guid>(0),
                reader.GetFieldValue<Guid>(1),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetFieldValue<Guid>(2),
                await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetFieldValue<Guid>(3),
                ParseKind(reader.GetString(4)),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetFieldValue<DateTimeOffset>(5));
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            return Unavailable();
        }
    }

    /// <inheritdoc />
    public async Task<Guid?> FindOwnerAsync(Guid taskId, CancellationToken cancellationToken)
    {
        // The peek runs in a scope of its own with no unit of work: it reads one column and takes no lock, so it opens the
        // connection itself and has no transaction to join.
        await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = CreateCommand("SELECT user_id FROM fetch_tasks WHERE id = @id", requireTransaction: false);
            command.Parameters.AddWithValue("id", taskId);

            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid owner ? owner : null;
        }
        finally
        {
            await database.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<int> CountLeasedAsync(CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("SELECT count(*)::int FROM fetch_tasks WHERE status = 'leased'");

        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0);
    }

    private static FetchTaskKind ParseKind(string wire) => wire switch
    {
        KindFetchSource => FetchTaskKind.FetchSource,
        KindFindSources => FetchTaskKind.FindSources,
        _ => throw new InvalidOperationException("fetch_tasks.kind holds a value the CHECK constraint forbids."),
    };

    private static bool IsTransient(Exception exception) =>
        exception is NpgsqlException { IsTransient: true }
        || exception.InnerException is NpgsqlException { IsTransient: true }
        || exception is NpgsqlException { InnerException: SocketException }
        || exception is SocketException;

    private static Error Unavailable() =>
        Error.Failure(code: ErrorCodes.ServiceUnavailable, description: "The database is temporarily unavailable; retry.");

    private async Task<bool> ExistsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("SELECT EXISTS (SELECT 1 FROM fetch_tasks WHERE id = @id)");
        command.Parameters.AddWithValue("id", taskId);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    /// <summary>
    /// Re-derives the requester payload of a <c>find_sources</c> task from its <c>generation_runs</c> row (T38), so no
    /// brief text is duplicated into the fetcher-visible table. The allow-list is empty until 02.7.2-T1 ships its source.
    /// </summary>
    private async Task<FinderRequestDto?> ReadFinderRequestAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(FinderSql);
        command.Parameters.AddWithValue("id", taskId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new FinderRequestDto(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), [], FinderMaxCandidates)
            : null;
    }

    /// <summary>
    /// A raw command on the scope's connection and transaction, with the configured command timeout. It refuses to run
    /// outside a transaction: the advisory lock and the row lock would die with their own statement.
    /// </summary>
    private NpgsqlCommand CreateCommand(string sql, bool requireTransaction = true)
    {
        var transaction = database.Database.CurrentTransaction;
        if (requireTransaction && transaction is null)
        {
            throw new InvalidOperationException(
                "The fetch-task lease store runs inside the command's transaction (09 README D5); outside one its locks protect nothing.");
        }

        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction?.GetDbTransaction() as NpgsqlTransaction;
        command.CommandTimeout = database.Database.GetCommandTimeout() ?? PersistenceOptions.DefaultCommandTimeoutSeconds;

        return command;
    }

    private sealed record LeasedRow(Guid Id, string Kind, string? Url, int Attempt, DateTimeOffset LeaseUntil);
}
