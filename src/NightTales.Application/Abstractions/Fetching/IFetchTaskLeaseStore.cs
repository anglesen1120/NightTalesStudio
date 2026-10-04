using ErrorOr;
using NightTales.Domain.Research;

namespace NightTales.Application.Abstractions.Fetching;

/// <summary>
/// What the result route needs to know about a task it has just locked (02.5.2-T2): who owns it, what it fetches and
/// when its lease began.
/// </summary>
/// <param name="TaskId">The <c>fetch_tasks</c> id.</param>
/// <param name="UserId">The owner of the episode.</param>
/// <param name="EpisodeId">The episode.</param>
/// <param name="SourceId">The source of a <c>fetch_source</c> task; <see langword="null"/> for <c>find_sources</c>.</param>
/// <param name="GenerationRunId">The run of a <c>find_sources</c> task; <see langword="null"/> otherwise.</param>
/// <param name="Kind">The task kind.</param>
/// <param name="LeasedAt">When the lease was taken, for the lease-to-result duration.</param>
/// <param name="DbNow">The database clock when the row was read, so a duration is <c>DbNow - LeasedAt</c> on one clock.</param>
public sealed record ClaimedFetchTask(
    Guid TaskId,
    Guid UserId,
    Guid EpisodeId,
    Guid? SourceId,
    Guid? GenerationRunId,
    FetchTaskKind Kind,
    DateTimeOffset? LeasedAt,
    DateTimeOffset DbNow);

/// <summary>
/// The queue-claim half of the fetcher's internal API (02.5.2-T2, 02 D10): set-based SQL that EF cannot express.
/// Every member runs inside the command's unit-of-work transaction; the lease, heartbeat and claim predicates are
/// evaluated by the database in the statement that changes or locks the row, so no check can go stale between a
/// read and a write.
/// </summary>
public interface IFetchTaskLeaseStore
{
    /// <summary>
    /// Leases up to <paramref name="max"/> queued tasks to <paramref name="workerId"/>: at most the platform cap in
    /// total and one per registrable domain (D10, F15). The platform advisory lock makes the caps exact.
    /// </summary>
    /// <param name="workerId">The fetcher instance.</param>
    /// <param name="max">The most tasks wanted, 1 to the platform cap.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The leased tasks (possibly none), or <c>service_unavailable</c> (transient failure, or the platform lock was still contended after about 2 s).</returns>
    Task<ErrorOr<IReadOnlyList<LeaseDto>>> LeaseAsync(string workerId, int max, CancellationToken cancellationToken);

    /// <summary>Extends the lease of a task <paramref name="workerId"/> still holds.</summary>
    /// <param name="taskId">The task.</param>
    /// <param name="workerId">The fetcher instance.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The new lease end, <c>fetch_task_not_found</c>, or <c>fetch_lease_lost</c>.</returns>
    Task<ErrorOr<DateTimeOffset>> HeartbeatAsync(Guid taskId, string workerId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the task row (<c>FOR UPDATE</c>) and answers it only while <paramref name="workerId"/> holds a live
    /// lease on it; the lock is held until the surrounding transaction ends, so the state change that follows cannot
    /// race a sweep or a second post.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="workerId">The fetcher instance.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The locked task, <c>fetch_task_not_found</c>, or <c>fetch_lease_lost</c>.</returns>
    Task<ErrorOr<ClaimedFetchTask>> ClaimForResultAsync(Guid taskId, string workerId, CancellationToken cancellationToken);

    /// <summary>
    /// The same predicate and result as <see cref="ClaimForResultAsync"/> but without <c>FOR UPDATE</c>: a read that takes
    /// no lock. It runs in a scope of its own with no unit of work and opens and closes its own connection.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="workerId">The fetcher instance.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The task, <c>fetch_task_not_found</c>, <c>fetch_lease_lost</c>, or <c>service_unavailable</c>.</returns>
    Task<ErrorOr<ClaimedFetchTask>> PeekForResultAsync(Guid taskId, string workerId, CancellationToken cancellationToken);

    /// <summary>The owner of a task; no tenant filter applies.</summary>
    /// <param name="taskId">The task.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The owner, <c>fetch_task_not_found</c>, or <c>service_unavailable</c> (transient failure or a deadline of the command's own).</returns>
    Task<ErrorOr<Guid>> FindOwnerAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>How many tasks are <c>leased</c> right now, platform-wide.</summary>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The count, or <c>service_unavailable</c>; callers use it for a gauge and must treat an error as "unknown".</returns>
    Task<ErrorOr<int>> CountLeasedAsync(CancellationToken cancellationToken);
}
