using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.Application.Rendering;
using NightTales.Domain.Common;

namespace NightTales.Worker.Services;

/// <summary>
/// The Worker's render poller (09 README BD7, WF07/W12, 09.6.3 plan §2): one <see cref="BackgroundService"/>
/// that, every <c>Render:PollSeconds</c>, claims the due render jobs of every tenant and runs
/// <see cref="PollRenderJob"/> for each in a scope pinned to its owner.
/// </summary>
/// <remarks>
/// <para>
/// It is a <see cref="BackgroundService"/> and not a Hangfire recurring job: a sub-minute cron is not
/// reliable (WF07/W12, 09 README D11 — Hangfire's schedule poll is 15 s by default, so a 10 s cadence has to
/// live in a loop). Exactly one instance polls per database and per process: a PostgreSQL session advisory
/// lock, <c>pg_try_advisory_lock(hashtext('render_poller'))</c>, is taken on a dedicated connection and held
/// for the lifetime of the loop, so a rolling deploy or a restarted process never polls twice
/// (<see cref="NodeHealthPollerLock"/> is the same guard for the node poller).
/// </para>
/// <para>
/// The claim is the one allow-listed cross-tenant read of this feature (09 README D10): it selects
/// <c>(id, user_id)</c> pairs and nothing else, and every claimed id is then processed in a DI scope of its
/// own where <see cref="ISystemContext.For"/> pins the tenant <em>before</em> the scope resolves its
/// <c>AppDbContext</c>. Inside such a scope the ordinary tenant filter is on, so a poll can only ever read its
/// own job's rows. A job that throws is logged (ids only) and never stops the tick or its siblings.
/// </para>
/// <para>
/// Nothing about a node reaches a log from here: the claim carries ids, and the node key and base URL stay
/// inside 09.6.2-T1's client (S4).
/// </para>
/// </remarks>
/// <param name="scopeFactory">Creates the claim scope and the per-job scopes (09 README D11).</param>
/// <param name="options">The bound <c>Render</c> section: the tick cadence, the batch cap and the parallelism bound.</param>
/// <param name="metrics">The render instruments; the tick publishes the <c>render_jobs_active</c> snapshot through <see cref="RenderMetrics.SetActiveJobs"/> (BD12).</param>
/// <param name="clock">The Worker's clock: the due instant is read from here, never from PostgreSQL's own <c>now()</c>, so a test's <c>FakeClock</c> decides what a tick can claim (09 D3/D13).</param>
/// <param name="connectionString">The platform database the advisory-lock connection opens on.</param>
/// <param name="logger">Tick, lock and per-job failures.</param>
public sealed partial class RenderPollingService(
    IServiceScopeFactory scopeFactory,
    IOptions<RenderOptions> options,
    RenderMetrics metrics,
    IClock clock,
    string connectionString,
    ILogger<RenderPollingService> logger) : BackgroundService
{
    /// <summary>The advisory-lock key; <c>hashtext</c> turns it into the 32-bit lock id (BD7).</summary>
    public const string LockKey = "render_poller";

    /// <summary>
    /// The claim: the due jobs of every tenant, oldest poll instant first, capped at
    /// <c>Render:PollBatchSize</c>. It reads <c>(id, user_id)</c> only and runs on the write model's
    /// connection because no tenant-filtered query can cross tenants (09 README D10); the partial index
    /// <c>ix_render_jobs_status_next_poll_at</c> serves it (BD5).
    /// </summary>
    private const string ClaimSql =
        "SELECT id, user_id FROM render_jobs "
        + "WHERE status IN ('submitted', 'running') AND next_poll_at <= @now "
        + "ORDER BY next_poll_at "
        + "LIMIT @batch";

    /// <summary>
    /// The platform-wide count behind the <c>render_jobs_active</c> gauge: the four statuses BD5 counts as an
    /// active render. It is read once per tick and published through <see cref="RenderMetrics.SetActiveJobs"/>,
    /// so a scrape never issues a query (BD12).
    /// </summary>
    private const string ActiveCountSql =
        "SELECT count(*) FROM render_jobs WHERE status IN ('queued', 'submitted', 'running', 'ingesting')";

    /// <summary>The lock command; <see cref="LockKey"/> is a constant, so no value is ever interpolated into SQL.</summary>
    private static readonly string TryLockSql = $"SELECT pg_try_advisory_lock(hashtext('{LockKey}'))";

    /// <summary>Releases the session lock before the holding connection is closed.</summary>
    private static readonly string UnlockSql = $"SELECT pg_advisory_unlock(hashtext('{LockKey}'))";

    private NpgsqlConnection? _lockConnection;
    private bool _lockNotHeldLogged;

    /// <summary>
    /// One tick: take the advisory lock, claim the due jobs and dispatch them. The loop calls it every
    /// <c>Render:PollSeconds</c>; it is public because the harness drives a tick directly instead of waiting out
    /// that timer, so no test sleeps (<c>OutboxDispatcher.ProcessBatchAsync</c> is public for the same reason).
    /// </summary>
    /// <param name="cancellationToken">The tick's cancellation token.</param>
    /// <returns>
    /// <c>true</c> when this instance held the advisory lock and ran the claim; <c>false</c> when another
    /// instance or process holds it, in which case this tick claims nothing.
    /// </returns>
    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        if (!await TryAcquireLockAsync(cancellationToken))
        {
            if (!_lockNotHeldLogged)
            {
                _lockNotHeldLogged = true;
                LogLockNotHeld();
            }

            return false;
        }

        _lockNotHeldLogged = false;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        var (due, active) = await ReadAsync(db, cancellationToken);

        metrics.SetActiveJobs(active);

        if (due.Count > 0)
        {
            await DispatchAsync(due, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// The loop: one <see cref="TickAsync"/> per interval. Every failure is caught and logged, so a database
    /// outage never stops the Worker's outbox or Hangfire, and the advisory lock is released on the way out
    /// (09.6.3 plan §"Failure handling").
    /// </summary>
    /// <param name="stoppingToken">The host's shutdown token.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.PollSeconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    LogTickFailed(exception);
                }

                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            await ReleaseLockAsync();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // The lock is session-scoped, so dropping the connection releases it even when no clean stop ran.
        Interlocked.Exchange(ref _lockConnection, null)?.Dispose();

        base.Dispose();
    }

    /// <summary>
    /// The claim statement and the active-job count on one short-lived connection: EF counts opens and closes,
    /// so this pair balances whatever the scope's context did before (the same pattern as
    /// <c>RenderJobNodeLoadReader</c>), and the connection is released before the jobs run.
    /// </summary>
    private async Task<(List<ClaimedJob> Due, int Active)> ReadAsync(
        IAppDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var due = await ReadDueAsync(connection, cancellationToken);
            var active = await ReadActiveCountAsync(connection, cancellationToken);

            return (due, active);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Reads the due <c>(id, user_id)</c> pairs, oldest poll instant first, at most the batch cap.</summary>
    private async Task<List<ClaimedJob>> ReadDueAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ClaimSql;
        command.CommandType = CommandType.Text;
        AddParameter(command, "now", clock.UtcNow);
        AddParameter(command, "batch", options.Value.PollBatchSize);

        var due = new List<ClaimedJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            due.Add(new ClaimedJob(reader.GetGuid(0), reader.GetGuid(1)));
        }

        return due;
    }

    /// <summary>Counts the rows <c>render_jobs_active</c> reports.</summary>
    private static async Task<int> ReadActiveCountAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ActiveCountSql;
        command.CommandType = CommandType.Text;

        return await command.ExecuteScalarAsync(cancellationToken) is long count ? (int)count : 0;
    }

    /// <summary>
    /// Runs <see cref="PollRenderJob"/> for every claimed job, at most <c>Render:PollParallelism</c> at once, so
    /// one slow adapter call cannot fan the tick out over the whole batch (BD7). A job's failure is logged with
    /// its id and nothing else.
    /// </summary>
    private async Task DispatchAsync(IReadOnlyList<ClaimedJob> due, CancellationToken cancellationToken)
    {
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Value.PollParallelism,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(due, parallel, async (job, token) =>
        {
            try
            {
                await RunJobAsync(job, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Ids only: the adapter's own message never reaches this log (S4).
                LogJobFailed(job.Id, exception);
            }
        });
    }

    /// <summary>
    /// One job in a DI scope of its own pinned to the job's owner before any <c>AppDbContext</c> is resolved
    /// (09 README D10), so the poll reads only that owner's rows.
    /// </summary>
    private async Task RunJobAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(job.UserId);

        await scope.ServiceProvider.GetRequiredService<PollRenderJob>().RunAsync(job.Id, cancellationToken);
    }

    /// <summary>
    /// Takes the session advisory lock on a connection of its own, held for as long as this instance polls.
    /// While the lock is held the holding session is probed with a cheap round trip first, because a backend
    /// the server terminated releases the lock without the client noticing until the connection is used; a
    /// session that turns out dead is dropped and the lock re-taken on this tick.
    /// </summary>
    private async Task<bool> TryAcquireLockAsync(CancellationToken cancellationToken)
    {
        if (_lockConnection is { State: ConnectionState.Open } held)
        {
            try
            {
                await ProbeAsync(held, cancellationToken);

                return true;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                await DropLockConnectionAsync();
            }
        }

        var candidate = new NpgsqlConnection(connectionString);

        try
        {
            await candidate.OpenAsync(cancellationToken);

            await using var command = candidate.CreateCommand();
            command.CommandText = TryLockSql;
            command.CommandType = CommandType.Text;

            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                _lockConnection = candidate;

                return true;
            }

            await candidate.DisposeAsync();

            return false;
        }
        catch
        {
            await candidate.DisposeAsync();

            throw;
        }
    }

    /// <summary>A cheap round trip: the session answering means the session-scoped lock is still ours.</summary>
    private static async Task ProbeAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandType = CommandType.Text;

        await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>Drops the held connection — dead or merely suspect — so the lock can be re-taken.</summary>
    private async Task DropLockConnectionAsync()
    {
        var connection = Interlocked.Exchange(ref _lockConnection, null);

        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>Releases the session lock, then closes the connection that held it.</summary>
    private async Task ReleaseLockAsync()
    {
        var connection = Interlocked.Exchange(ref _lockConnection, null);

        if (connection is null)
        {
            return;
        }

        try
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = UnlockSql;
                command.CommandType = CommandType.Text;

                // Shutdown must not be blocked by an unresponsive server: the lock dies with the session.
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // The session is already gone, so the server released the lock with it.
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    // EventIds 6200-6202: the render poller's block of 09.6.3-T1 (the Application half of the task took 6190-6199,
    // and 2500/2501 already belong to RenderDownloadClient).
    [LoggerMessage(EventId = 6200, Level = LogLevel.Warning, Message = "render.poller.lock-not-held")]
    private partial void LogLockNotHeld();

    [LoggerMessage(EventId = 6201, Level = LogLevel.Error, Message = "render.poller.tick-failed")]
    private partial void LogTickFailed(Exception exception);

    [LoggerMessage(EventId = 6202, Level = LogLevel.Warning, Message = "render.poller.job-failed renderJobId={RenderJobId}")]
    private partial void LogJobFailed(Guid renderJobId, Exception exception);

    /// <summary>One claimed row: the id the job runs under and the owner its scope is pinned to.</summary>
    private readonly record struct ClaimedJob(Guid Id, Guid UserId);
}
