using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The two armed faults the story-approval integration tests need, in one per-test object.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a seam at all.</b> The version bump of SEC-1 and the owner's own approval of F2 both have to land
/// between the verdict's commit and the approval service's read, and the gate composes those two steps itself.
/// The approval service resolves the run's owner through <see cref="ISystemWorkQuery"/> before it creates its
/// own scope, so <see cref="AutoApproveSeamSystemWork"/> runs the armed hook exactly there — after the verdict is
/// stored, before anything is approved — without standing in for any production service.
/// </para>
/// <para>
/// <b>The audit fault.</b> <see cref="AutoApproveSeamAuditWriter"/> wraps the real
/// <c>EfAuditWriter</c> and makes exactly one <c>autopilot.story_auto_approved</c> write throw, so the
/// service's own fail-closed answer and the replay's backfill can be observed on a committed approval.
/// </para>
/// </remarks>
internal sealed class AutoApproveSeam
{
    private Func<Task>? _beforeApproval;
    private int _autoApprovalWritesToFail;

    /// <summary>Runs <paramref name="hook"/> once, at the approval service's next owner lookup.</summary>
    /// <param name="hook">The arrangement to apply: a version bump, or the owner's own approval.</param>
    internal void RunBeforeApproval(Func<Task> hook) => _beforeApproval = hook;

    /// <summary>Makes the next <c>autopilot.story_auto_approved</c> audit write throw, exactly once.</summary>
    internal void FailNextAutoApprovalWrite() => Interlocked.Exchange(ref _autoApprovalWritesToFail, 1);

    /// <summary>The armed hook, consumed: a second owner lookup runs nothing.</summary>
    /// <returns>The hook's own task, or a completed one when nothing is armed.</returns>
    internal Task BeforeApprovalAsync()
    {
        var hook = Interlocked.Exchange(ref _beforeApproval, null);

        return hook is null ? Task.CompletedTask : hook();
    }

    /// <summary>Whether this write is the one armed auto-approval failure; consuming the arming.</summary>
    /// <param name="action">The audit action about to be written.</param>
    /// <returns><see langword="true"/> for the first auto-approval write after the arming.</returns>
    internal bool ShouldFailAutoApprovalWrite(string action) =>
        string.Equals(action, AutopilotApprovalService.StoryApprovedAction, StringComparison.Ordinal)
        && Interlocked.CompareExchange(ref _autoApprovalWritesToFail, 0, 1) == 1;
}

/// <summary>
/// The armed hook of <see cref="AutoApproveSeam"/>, run at the one moment between the recorded verdict and the
/// approval service's own scope. It delegates every read to the real cross-user port.
/// </summary>
/// <param name="inner">The production <c>SystemWorkQuery</c>.</param>
/// <param name="seam">The arrangements this test armed.</param>
internal sealed class AutoApproveSeamSystemWork(ISystemWorkQuery inner, AutoApproveSeam seam) : ISystemWorkQuery
{
    /// <inheritdoc />
    public async IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T>(
        Expression<Func<T, bool>> where,
        int batch,
        [EnumeratorCancellation] CancellationToken ct)
        where T : Entity, IUserOwned
    {
        // The approval service resolves the run's owner as its first act, so this is where SEC-1's version bump
        // and F2's owner approval have to land. Every other caller (the outbox-style sweeps) reads through
        // untouched: only a run lookup is armed, and only once.
        if (typeof(T) == typeof(AutopilotRun))
        {
            await seam.BeforeApprovalAsync().ConfigureAwait(false);
        }

        await foreach (var pair in inner.PendingAsync<T>(where, batch, ct).ConfigureAwait(false))
        {
            yield return pair;
        }
    }
}

/// <summary>
/// The real audit writer with one armed failure: the first <c>autopilot.story_auto_approved</c> write throws,
/// every other write and every later auto-approval write passes through.
/// </summary>
/// <param name="inner">The production <c>EfAuditWriter</c>.</param>
/// <param name="seam">The arrangements this test armed.</param>
internal sealed class AutoApproveSeamAuditWriter(IAuditWriter inner, AutoApproveSeam seam) : IAuditWriter
{
    /// <inheritdoc />
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (seam.ShouldFailAutoApprovalWrite(entry.Action))
        {
            // The service answers a throwing write as GateErrors.ApprovalAuditFailed and leaves the committed
            // approval alone (AutopilotApprovalService.WriteAuditAsync), which is the behaviour under test.
            throw new InvalidOperationException(
                $"seam: the first {AutopilotApprovalService.StoryApprovedAction} write fails.");
        }

        return inner.WriteAsync(entry, cancellationToken);
    }
}
