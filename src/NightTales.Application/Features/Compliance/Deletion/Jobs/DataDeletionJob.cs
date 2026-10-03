using System.Diagnostics;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Application.Features.Compliance.Deletion.Jobs;

/// <summary>
/// The hourly deletion run (13.3.1 plan §4 UC2/UC3): metric phase, allow-listed cross-user scan, atomic per-row claim,
/// then <see cref="DeletionRowExecutor"/> per claimed row. This class is the feature's only <c>IgnoreQueryFilters</c>
/// site (09.1.2-T1 allow-list names it); every other read runs in a user-pinned scope.
/// </summary>
/// <remarks>
/// <para>
/// The claim is one conditional <c>UPDATE</c> <em>per candidate</em>, issued right before that row runs (at most <c>BatchSize</c> = 20): its affected-row
/// count says exactly which worker won, so two concurrent runs cannot both execute a row, and no lock is held across a
/// handler call. A row whose scope has no handler is never claimed: it stays <c>Pending</c>, counts
/// <c>deletion_requests_unhandled_total</c> and logs one warning per run, so a deferred scope is never "completed".
/// Stale in-progress claims (crash) are reclaimable after <c>LeaseMinutes</c> and, like failed rows, only while attempts remain; the claimed instant is the token the executor's completion must still match.
/// </para>
/// <para>It sends no e-mail and keeps no alert state: the due-soon alert is a monitoring rule over the gauge.</para>
/// </remarks>
/// <param name="services">The job's own DI scope; the scan and claim context comes from it.</param>
/// <param name="executor">Runs one claimed row in its own scope.</param>
/// <param name="clock">The run's instants.</param>
/// <param name="options">The <c>DataDeletion</c> settings.</param>
/// <param name="metrics">The gauges and counters.</param>
/// <param name="logger">Counts, scopes and ids only.</param>
[JobPolicy(Queue = JobQueue.Default, Retries = 3)]
public sealed partial class DataDeletionJob(
    IServiceProvider services,
    DeletionRowExecutor executor,
    IClock clock,
    DataDeletionOptions options,
    DataDeletionMetrics metrics,
    ILogger<DataDeletionJob> logger) : IBackgroundJob
{
    private const string TenantFilter = "tenant";

    /// <summary>Runs one tick.</summary>
    /// <param name="cancellationToken">The scheduler's shutdown token.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var now = Truncate(clock.UtcNow);
        var database = services.GetRequiredService<IAppDbContext>();
        var registry = services.GetRequiredService<IDeletionScopeHandlerRegistry>();

        await MetricPhaseAsync(database, now, cancellationToken).ConfigureAwait(false);

        var registered = registry.RegisteredScopes.ToList();
        var unhandled = await CountUnhandledAsync(database, now, registered, cancellationToken).ConfigureAwait(false);

        var leaseCutoff = now - TimeSpan.FromMinutes(options.LeaseMinutes);
        var maxAttempts = options.MaxAttempts;

        var candidates = await database.DeletionRequests
            .IgnoreQueryFilters([TenantFilter])
            .Where(request => request.CompletedAt == null && request.DueAt <= now && registered.Contains(request.Scope))
            .Where(Claimable(leaseCutoff, maxAttempts))
            .OrderBy(request => request.DueAt)
            .ThenBy(request => request.Id)
            .Select(request => new { request.Id, request.UserId })
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // CR-1: each row is claimed immediately before it runs, with its own lease start, and that claim instant is
        // the token the executor's complete/fail updates must still match. A slow earlier row therefore cannot let a
        // later row's lease lapse, and a delayed worker can neither re-run nor fail another worker's claim.
        var claimed = 0;
        var completed = 0;
        var failed = 0;

        // A crashed claim that has used every attempt is never reclaimed (Claimable), so it is failed here instead of
        // staying in progress forever. The conditional update cannot race a live claim: a re-claim moves claimed_at.
        var exhausted = await database.DeletionRequests
            .IgnoreQueryFilters([TenantFilter])
            .Where(request => request.CompletedAt == null
                && request.Status == DeletionStatus.InProgress
                && request.ClaimedAt < leaseCutoff
                && request.Attempts >= maxAttempts)
            .Select(request => new { request.Id, request.Scope })
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in exhausted)
        {
            var errorAt = Truncate(clock.UtcNow);
            var marked = await database.DeletionRequests
                .IgnoreQueryFilters([TenantFilter])
                .Where(request => request.Id == row.Id
                    && request.Status == DeletionStatus.InProgress
                    && request.ClaimedAt < leaseCutoff
                    && request.Attempts >= maxAttempts)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(request => request.Status, DeletionStatus.Failed)
                        .SetProperty(request => request.LastErrorCode, ErrorCodes.ServiceUnavailable)
                        .SetProperty(request => request.LastErrorAt, errorAt),
                    cancellationToken)
                .ConfigureAwait(false);

            if (marked == 1)
            {
                metrics.Failed(row.Scope.ToWire(), ErrorCodes.ServiceUnavailable);
                failed++;
            }
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var claimedAt = Truncate(clock.UtcNow);
            var cutoff = claimedAt - TimeSpan.FromMinutes(options.LeaseMinutes);
            var won = await database.DeletionRequests
                .IgnoreQueryFilters([TenantFilter])
                .Where(request => request.Id == candidate.Id && request.CompletedAt == null)
                .Where(Claimable(cutoff, maxAttempts))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(request => request.Status, DeletionStatus.InProgress)
                        .SetProperty(request => request.ClaimedAt, claimedAt)
                        .SetProperty(request => request.Attempts, request => request.Attempts + 1),
                    cancellationToken)
                .ConfigureAwait(false);

            if (won != 1)
            {
                continue;
            }

            claimed++;
            var outcome = await executor.ExecuteAsync(candidate.UserId, candidate.Id, DeletionActor.Job, cancellationToken, claimedAt)
                .ConfigureAwait(false);
            if (outcome.Kind == DeletionOutcomeKind.Completed)
            {
                completed++;
            }
            else if (outcome.Kind == DeletionOutcomeKind.Failed)
            {
                failed++;
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        metrics.RunFinished(elapsed.TotalSeconds);
        var elapsedMs = (long)elapsed.TotalMilliseconds;
        LogRun(claimed, completed, failed, unhandled, elapsedMs);
    }

    /// <summary>Pending, a crashed (stale) claim with attempts left, or a failed row with attempts left.</summary>
    private static Expression<Func<DeletionRequest, bool>> Claimable(DateTimeOffset leaseCutoff, int maxAttempts) =>
        request => request.Status == DeletionStatus.Pending
            || (request.Status == DeletionStatus.InProgress && request.ClaimedAt < leaseCutoff && request.Attempts < maxAttempts)
            || (request.Status == DeletionStatus.Failed && request.Attempts < maxAttempts);

    private async Task MetricPhaseAsync(IAppDbContext database, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var lead = now + TimeSpan.FromHours(options.AlertLeadHours);
            var open = database.DeletionRequests.IgnoreQueryFilters([TenantFilter]).Where(request => request.CompletedAt == null);

            var openCount = await open.CountAsync(cancellationToken).ConfigureAwait(false);
            var overdue = await open.CountAsync(request => request.DueAt < now, cancellationToken).ConfigureAwait(false);
            var dueSoon = await open.CountAsync(request => request.DueAt <= lead, cancellationToken).ConfigureAwait(false);
            metrics.SetCounts(openCount, overdue, dueSoon);

            if (dueSoon > 0)
            {
                var soonest = await open
                    .Where(request => request.DueAt <= lead)
                    .OrderBy(request => request.DueAt)
                    .Select(request => new { request.Id, request.Scope, request.DueAt })
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);

                var soonestScope = soonest.Scope.ToWire();
                var dueInHours = Math.Round((soonest.DueAt - now).TotalHours, 1);
                LogDueSoon(soonestScope, dueInHours, soonest.Id);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // UC3 is a signal only: the run's real work goes on.
            var failedType = exception.GetType().Name;
            LogMetricsFailed(failedType);
        }
    }

    private async Task<int> CountUnhandledAsync(
        IAppDbContext database,
        DateTimeOffset now,
        List<DeletionScope> registered,
        CancellationToken cancellationToken)
    {
        var groups = await database.DeletionRequests
            .IgnoreQueryFilters([TenantFilter])
            .Where(request => request.CompletedAt == null && request.DueAt <= now && !registered.Contains(request.Scope))
            .GroupBy(request => request.Scope)
            .Select(group => new { Scope = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var group in groups)
        {
            metrics.Unhandled(group.Scope.ToWire(), group.Count);
        }

        if (groups.Count > 0)
        {
            var names = string.Join(',', groups.Select(group => group.Scope.ToWire()));
            var rowCount = groups.Sum(group => group.Count);
            LogUnhandled(names, rowCount);
        }

        return groups.Sum(group => group.Count);
    }

    /// <summary>Microsecond precision, which is what <c>timestamptz</c> stores.</summary>
    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    [LoggerMessage(EventId = 4310, Level = LogLevel.Information, Message = "data_deletion_run claimed={Claimed} completed={Completed} failed={Failed} unhandled={Unhandled} duration_ms={DurationMs}")]
    private partial void LogRun(int claimed, int completed, int failed, int unhandled, long durationMs);

    [LoggerMessage(EventId = 4311, Level = LogLevel.Warning, Message = "deletion_request_due_soon scope={Scope} due_in_hours={DueInHours} requestId={RequestId}")]
    private partial void LogDueSoon(string scope, double dueInHours, Guid requestId);

    [LoggerMessage(EventId = 4312, Level = LogLevel.Warning, Message = "data_deletion_scope_unregistered scopes={Scopes} rows={Rows}")]
    private partial void LogUnhandled(string scopes, int rows);

    [LoggerMessage(EventId = 4313, Level = LogLevel.Warning, Message = "data_deletion_metrics_failed exception={ExceptionType}")]
    private partial void LogMetricsFailed(string exceptionType);
}
