namespace NightTales.RenderNode.Server;

/// <summary>
/// The in-process <see cref="ITaskStore"/>: the store of mock-mpt (11.1.2-T1) and of the core's own tests
/// (11.3.5-T1 Outputs). It implements the whole seam contract under one lock, including the merge of
/// <see cref="ITaskStore.UpdateAsync"/>, so the 05 D4 status invariants hold for a caller that updates
/// naively. The adapter's <c>SqliteTaskStore</c> (11.3.5-T2) must satisfy the same contract.
/// </summary>
public sealed class InMemoryTaskStore : ITaskStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RenderTaskRecord> _byTaskId = [];
    private readonly Dictionary<Guid, Guid> _taskIdByRenderJobId = [];

    /// <inheritdoc />
    public ValueTask<RenderTaskRecord?> TryGetAsync(Guid taskId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_byTaskId.TryGetValue(taskId, out var record) ? record : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<RenderTaskRecord?> FindByRenderJobIdAsync(Guid renderJobId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(
                _taskIdByRenderJobId.TryGetValue(renderJobId, out var taskId)
                    && _byTaskId.TryGetValue(taskId, out var record)
                    ? record
                    : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> InsertAsync(RenderTaskRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            if (_byTaskId.ContainsKey(record.TaskId) || _taskIdByRenderJobId.ContainsKey(record.RenderJobId))
            {
                return ValueTask.FromResult(false);
            }

            _byTaskId[record.TaskId] = record;
            _taskIdByRenderJobId[record.RenderJobId] = record.TaskId;
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> UpdateAsync(RenderTaskRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            if (!_byTaskId.TryGetValue(record.TaskId, out var existing))
            {
                return ValueTask.FromResult(false);
            }

            _byTaskId[record.TaskId] = existing.Merge(record);
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RecordBackendAcceptanceAsync(
        Guid taskId,
        string backendTaskId,
        DateTimeOffset? backendStartedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(backendTaskId);

        lock (_gate)
        {
            if (!_byTaskId.TryGetValue(taskId, out var existing))
            {
                return ValueTask.FromResult(false);
            }

            // Only the two backend fields move; CancelRequested and the observer's fields are untouched, so a
            // cancel that raced the submit is preserved.
            _byTaskId[taskId] = existing with
            {
                BackendTaskId = backendTaskId,
                BackendStartedAt = backendStartedAt ?? existing.BackendStartedAt,
            };
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> CountNonTerminalAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_byTaskId.Values.Count(record => !record.IsTerminal));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Guid>> ListNonTerminalAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var ids = _byTaskId.Values.Where(record => !record.IsTerminal).Select(record => record.TaskId).ToList();
            return ValueTask.FromResult<IReadOnlyList<Guid>>(ids);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(Guid taskId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(Remove(taskId));
        }
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeCleanedAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var purged = 0;
            foreach (var record in _byTaskId.Values.Where(record => record.CleanedAt is { } cleaned && cleaned < olderThan).ToList())
            {
                purged += Remove(record.TaskId) ? 1 : 0;
            }

            return ValueTask.FromResult(purged);
        }
    }

    private bool Remove(Guid taskId)
    {
        if (!_byTaskId.Remove(taskId, out var record))
        {
            return false;
        }

        _taskIdByRenderJobId.Remove(record.RenderJobId);
        return true;
    }
}
