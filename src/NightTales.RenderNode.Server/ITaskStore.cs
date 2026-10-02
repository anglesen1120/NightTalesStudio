using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The node's task store seam (11 README D5): <c>SqliteTaskStore</c> in the adapter (11.3.5-T2), and
/// <see cref="InMemoryTaskStore"/> in mock-mpt (11.1.2-T1) and in the tests.
/// </summary>
/// <remarks>
/// <see cref="UpdateAsync"/> replaces the row with <see cref="RenderTaskRecord.Merge"/> applied
/// (percent never decreases, a terminal row is final, <c>cancel_requested</c> is one-way) and
/// returns <c>false</c> when the row no longer exists — the "conflict" the caller retries once
/// (11.3.5-T1 Failure handling).
/// </remarks>
public interface ITaskStore
{
    /// <summary>The row of a task id, or null when the task is unknown (purged, cleaned or never submitted).</summary>
    ValueTask<RenderTaskRecord?> TryGetAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>
    /// The row of a <c>render_job_id</c>, whatever its body hash: the submit path answers 200 for its own hash and
    /// 409 for another one, and §3 decides that before the health check and the capacity gate.
    /// </summary>
    ValueTask<RenderTaskRecord?> FindByRenderJobIdAsync(Guid renderJobId, CancellationToken cancellationToken);

    /// <summary>Inserts a new row; false when a row with the same task id or render job id already exists.</summary>
    ValueTask<bool> InsertAsync(RenderTaskRecord record, CancellationToken cancellationToken);

    /// <summary>Merges the record into the stored row; false when the row is gone.</summary>
    ValueTask<bool> UpdateAsync(RenderTaskRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Records the backend's acceptance on the current row, changing only <c>BackendTaskId</c> and
    /// <c>BackendStartedAt</c>. It deliberately does not go through <see cref="RenderTaskRecord.Merge"/>:
    /// a cancel accepted while the submit was still in flight must survive the submit result
    /// (11.3.5-T1 Failure handling). False when the row is gone.
    /// </summary>
    ValueTask<bool> RecordBackendAcceptanceAsync(
        Guid taskId,
        string backendTaskId,
        DateTimeOffset? backendStartedAt,
        CancellationToken cancellationToken);

    /// <summary>The number of non-terminal rows — the capacity gate's input.</summary>
    ValueTask<int> CountNonTerminalAsync(CancellationToken cancellationToken);

    /// <summary>The non-terminal task ids, for the adapter's poller.</summary>
    ValueTask<IReadOnlyList<Guid>> ListNonTerminalAsync(CancellationToken cancellationToken);

    /// <summary>Removes the row outright; the endpoint path marks <c>cleaned_at</c> instead.</summary>
    ValueTask<bool> DeleteAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>Removes rows cleaned before <paramref name="olderThan"/> and returns how many went (D15 P4: 7 days).</summary>
    ValueTask<int> PurgeCleanedAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}
