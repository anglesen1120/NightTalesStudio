using ErrorOr;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The capacity rule of 05.4.1-T2 §4 (F07/C10): the node counts its own non-terminal tasks and refuses a
/// submit at <c>active ≥ MaxConcurrentTasks</c> with <c>429 adapter_capacity_full</c>. "Count non-terminal +
/// insert" happens under one permit, so N simultaneous submits can insert at most the free slots — never two
/// rows into one slot.
/// </summary>
public sealed class CapacityGate : IDisposable
{
    private readonly SemaphoreSlim _permit = new(1, 1);
    private readonly ITaskStore _store;
    private readonly IOptions<RenderNodeServerOptions> _options;

    public CapacityGate(ITaskStore store, IOptions<RenderNodeServerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _options = options;
    }

    /// <summary>
    /// Inserts <paramref name="candidate"/> when a slot is free. Errors: <c>adapter_idempotency_conflict</c>
    /// when a row for the same <c>render_job_id</c> already exists — the caller re-reads the stored body hash
    /// and answers the §3 replay (200) or the conflict (409) — and <c>adapter_capacity_full</c> when the node is
    /// busy.
    /// </summary>
    /// <remarks>
    /// The job-id re-check runs inside the same critical section as the count and the insert, so the §3
    /// idempotency decision is race-safe against capacity: two in-flight submits of one job id can never get
    /// <c>429 adapter_capacity_full</c> for the first one's row (the pre-gate lookup misses while that row is
    /// still being inserted). The check comes before the count, so a duplicate neither consumes a slot nor is
    /// refused by one.
    /// </remarks>
    public async Task<ErrorOr<RenderTaskRecord>> TryEnterAsync(RenderTaskRecord candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        await _permit.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _store.FindByRenderJobIdAsync(candidate.RenderJobId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return Error.Conflict(AdapterErrorCodes.IdempotencyConflict);
            }

            var active = await _store.CountNonTerminalAsync(cancellationToken).ConfigureAwait(false);
            if (active >= _options.Value.MaxConcurrentTasks)
            {
                return Error.Conflict(AdapterErrorCodes.CapacityFull);
            }

            return await _store.InsertAsync(candidate, cancellationToken).ConfigureAwait(false)
                ? candidate
                : Error.Conflict(AdapterErrorCodes.IdempotencyConflict);
        }
        finally
        {
            _permit.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _permit.Dispose();
}
