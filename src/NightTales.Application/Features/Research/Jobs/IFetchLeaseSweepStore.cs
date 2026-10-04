using ErrorOr;

namespace NightTales.Application.Features.Research.Jobs;

/// <summary>One <c>fetch_tasks</c> row the sweeper moved off an expired lease.</summary>
/// <param name="Id">The task id.</param>
/// <param name="UserId">The task's owner; the job pins its per-row scope to it.</param>
/// <param name="SourceId"><see langword="null"/> for a <c>find_sources</c> task.</param>
/// <param name="EpisodeId">The task's episode.</param>
/// <param name="Attempts">The attempts counted when the lease expired (unchanged by the sweep).</param>
/// <param name="Failed"><see langword="true"/> when the attempt cap was reached and the task is now <c>failed</c>;
/// <see langword="false"/> when it went back to <c>queued</c>.</param>
public sealed record ExpiredFetchLease(Guid Id, Guid UserId, Guid? SourceId, Guid EpisodeId, int Attempts, bool Failed);

/// <summary>A <c>failed</c> <c>fetch_tasks</c> row whose source is still <c>pending</c> (a crash between the two writes).</summary>
/// <param name="Id">The task id.</param>
/// <param name="UserId">The task's owner; the job pins its per-row scope to it.</param>
/// <param name="SourceId">The source that should have been failed.</param>
/// <param name="EpisodeId">The task's episode.</param>
/// <param name="ErrorCode">The task's <c>error_code</c>, or <see langword="null"/> when none was stored.</param>
public sealed record FailedTaskWithPendingSource(Guid Id, Guid UserId, Guid SourceId, Guid EpisodeId, string? ErrorCode);

/// <summary>
/// The one statement of <c>FetchLeaseSweeperJob</c> (02.5.2-T2, D10): requeue or fail every <c>fetch_tasks</c> row whose
/// lease ran out. It is a cross-user queue statement on purpose (the fetcher has no user), like the outbox cleanup.
/// </summary>
public interface IFetchLeaseSweepStore
{
    /// <summary>
    /// Moves every <c>leased</c> task with <c>lease_until &lt; now()</c> back to <c>queued</c> (not leasable for
    /// <paramref name="backoffSeconds"/>), or to <c>failed</c> with <c>source_unreachable</c> once
    /// <c>attempts &gt;= maxAttempts</c>, in one atomic statement.
    /// </summary>
    /// <param name="maxAttempts">The attempt cap (<c>Fetcher:MaxAttempts</c>).</param>
    /// <param name="backoffSeconds">The requeue delay (<c>Fetcher:RequeueBackoffSeconds</c>).</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>
    /// The rows the statement changed, or <c>service_unavailable</c> (503) for a transient database failure or a
    /// statement deadline, so the caller retries on its next tick instead of failing the run.
    /// </returns>
    Task<ErrorOr<IReadOnlyList<ExpiredFetchLease>>> RequeueExpiredLeasesAsync(
        int maxAttempts,
        int backoffSeconds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists, newest first and at most <paramref name="limit"/>, the <c>failed</c> tasks that finished within the last
    /// <paramref name="lookbackHours"/> hours and whose source is still <c>pending</c>.
    /// </summary>
    /// <param name="lookbackHours">How far back <c>finished_at</c> may lie.</param>
    /// <param name="limit">The most rows returned.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The rows, or <c>service_unavailable</c> (503) for a transient failure or a statement deadline.</returns>
    Task<ErrorOr<IReadOnlyList<FailedTaskWithPendingSource>>> ListFailedTasksWithPendingSourceAsync(
        int lookbackHours,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Counts the tasks that are <c>leased</c> now: the value of the <c>fetch_leases_active</c> gauge.</summary>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The count, or <c>service_unavailable</c> (503) for a transient failure or a statement deadline.</returns>
    Task<ErrorOr<int>> CountLeasedAsync(CancellationToken cancellationToken);
}
