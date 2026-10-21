using NightTales.RenderNode.Server;

namespace NightTales.RenderNode.Tests.Fakes;

/// <summary>
/// The real <see cref="InMemoryTaskStore"/> plus the counters a security assertion needs: the UUID guard must
/// answer 404 for a non-UUID task id <em>without</em> touching the store.
/// </summary>
internal sealed class CountingTaskStore : ITaskStore
{
    private readonly InMemoryTaskStore _inner = new();

    internal int TryGetCount { get; private set; }

    internal int InsertCount { get; private set; }

    internal int UpdateCount { get; private set; }

    /// <summary>When set, the first update reports a conflict; the second succeeds (the retry rule).</summary>
    internal bool FailFirstUpdate { get; set; }

    private bool _updateFailed;

    internal ValueTask<RenderTaskRecord?> StoredAsync(Guid taskId) => _inner.TryGetAsync(taskId, CancellationToken.None);

    /// <inheritdoc />
    public ValueTask<RenderTaskRecord?> TryGetAsync(Guid taskId, CancellationToken cancellationToken)
    {
        TryGetCount++;
        return _inner.TryGetAsync(taskId, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<RenderTaskRecord?> FindByRenderJobIdAsync(Guid renderJobId, CancellationToken cancellationToken) =>
        _inner.FindByRenderJobIdAsync(renderJobId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> InsertAsync(RenderTaskRecord record, CancellationToken cancellationToken)
    {
        InsertCount++;
        return _inner.InsertAsync(record, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> UpdateAsync(RenderTaskRecord record, CancellationToken cancellationToken)
    {
        UpdateCount++;
        if (FailFirstUpdate && !_updateFailed)
        {
            _updateFailed = true;
            return ValueTask.FromResult(false);
        }

        return _inner.UpdateAsync(record, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> RecordBackendAcceptanceAsync(
        Guid taskId,
        string backendTaskId,
        DateTimeOffset? backendStartedAt,
        CancellationToken cancellationToken) =>
        _inner.RecordBackendAcceptanceAsync(taskId, backendTaskId, backendStartedAt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> CountNonTerminalAsync(CancellationToken cancellationToken) =>
        _inner.CountNonTerminalAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Guid>> ListNonTerminalAsync(CancellationToken cancellationToken) =>
        _inner.ListNonTerminalAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(Guid taskId, CancellationToken cancellationToken) =>
        _inner.DeleteAsync(taskId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> PurgeCleanedAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) =>
        _inner.PurgeCleanedAsync(olderThan, cancellationToken);
}
