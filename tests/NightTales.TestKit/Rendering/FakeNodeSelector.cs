using ErrorOr;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Rendering;

namespace NightTales.TestKit.Rendering;

/// <summary>
/// A scripted <see cref="INodeSelector"/> for the submit tests of 09.6.3-T1: it answers from a FIFO of picks, so
/// a test can script "no node, then a node" without a <c>worker_nodes</c> table, a health cache or a load read.
/// </summary>
/// <remarks>
/// An unscripted pick throws <see cref="InvalidOperationException"/>: the submit job picks exactly once per run,
/// so an extra pick is a test bug. <see cref="EnqueueUnavailable"/> scripts the selector's own
/// <c>render_worker_unavailable</c> failure.
/// </remarks>
public sealed class FakeNodeSelector : INodeSelector
{
    private readonly Lock _gate = new();
    private readonly Queue<ErrorOr<WorkerNode>> _picks = [];
    private int _pickCalls;

    /// <summary>How many picks ran.</summary>
    public int PickCalls
    {
        get
        {
            lock (_gate)
            {
                return _pickCalls;
            }
        }
    }

    /// <summary>Scripts one successful pick of <paramref name="node"/>.</summary>
    /// <param name="node">The node the selector returns.</param>
    /// <returns>This selector, for chaining.</returns>
    public FakeNodeSelector Enqueue(WorkerNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return EnqueuePick(node);
    }

    /// <summary>Scripts one pick of each of <paramref name="nodes"/>, in order.</summary>
    /// <param name="nodes">The nodes the selector returns.</param>
    /// <returns>This selector, for chaining.</returns>
    public FakeNodeSelector Enqueue(params WorkerNode[] nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        foreach (var node in nodes)
        {
            EnqueuePick(node);
        }

        return this;
    }

    /// <summary>Scripts one <c>render_worker_unavailable</c> pick: no dispatchable node.</summary>
    /// <returns>This selector, for chaining.</returns>
    public FakeNodeSelector EnqueueUnavailable() =>
        EnqueuePick(Error.Failure(
            ErrorCodes.RenderWorkerUnavailable,
            "no render node with a free slot"));

    /// <inheritdoc />
    public Task<ErrorOr<WorkerNode>> PickAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _pickCalls++;

            if (_picks.Count > 0)
            {
                return Task.FromResult(_picks.Dequeue());
            }
        }

        throw new InvalidOperationException("The test did not script a pick on FakeNodeSelector.");
    }

    private FakeNodeSelector EnqueuePick(ErrorOr<WorkerNode> answer)
    {
        lock (_gate)
        {
            _picks.Enqueue(answer);
        }

        return this;
    }
}
