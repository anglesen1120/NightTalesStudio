using ErrorOr;
using NightTales.Domain.Rendering;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// What recording one terminal failure decided (09.6.3-T1 step 7, WT28/WT50).
/// </summary>
/// <param name="Final">
/// <c>true</c> when no retry follows: the failed row stays the episode's newest attempt, the episode returns to
/// <c>story_approved</c> and <c>render_failed.v1</c> carries <c>Final = true</c>. <c>false</c> while the retry
/// row exists and the episode stays <c>rendering</c>.
/// </param>
/// <param name="RetryJobId">The <c>queued</c> retry row, or <c>null</c> when the attempt is final.</param>
/// <param name="FailureCode">
/// The 05 D5 outcome code the attempt was failed with, so a caller can label its own instrumentation without
/// keeping a second copy of the code.
/// </param>
/// <param name="RetryCreated">
/// <c>true</c> only when a retry row was really created for this failure (<c>RetryJobId is not null</c>): the
/// caller counts the retry <em>after</em> it commits, so a counter is never incremented for a retry the commit
/// discarded (a concurrent cancel wins the commit and the poller reads the row again next tick).
/// </param>
public sealed record RenderFailureResult(bool Final, Guid? RetryJobId, string FailureCode, bool RetryCreated);

/// <summary>
/// The one write path of a terminal render outcome (09.6.3-T1 steps 5 and 7, WT28/WT50/WT51): it fails the row,
/// decides and creates the retry, refunds the attempt's reservation, writes the audit row and raises the
/// outbox event.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inside the caller's transaction.</b> A job has no Mediator pipeline, and the audit row must commit with
/// the failure it records, so both members require an open <c>IUnitOfWork</c> transaction and throw when there
/// is none (the <c>EfAuditWriter</c> guard of 09.3.6-T1).
/// </para>
/// <para>
/// <b>The order is binding.</b> On a failure: <c>Fail</c>, then the retry decision, then the refund, then the
/// audit row, then <c>RaiseFailed</c> — and the episode returns to <c>story_approved</c> only when the attempt
/// is final, because the episode must stay <c>rendering</c> while a retry row exists (WT28). On a cancel:
/// <c>Cancel</c>, the episode back to <c>story_approved</c>, the refund, the audit row.
/// </para>
/// <para>
/// <b>A cancel-requested attempt is never retried (WT51).</b> When the row already carries
/// <c>CancelRequestedAt</c> — the owner asked the adapter to cancel, which is what happens on the 60-minute
/// watchdog or on any outcome the adapter answers before it reports <c>cancelled</c> — the verdict is final
/// whatever the code says: no retry row, no reservation of a second attempt, one refund and one
/// <c>render_failed.v1</c> with <c>Final = true</c>. WT51 lets only the adapter's own <c>succeeded</c> win over
/// a cancel; it never lets a cancelled render be re-run at the owner's expense.
/// </para>
/// <para>
/// <b>Money.</b> A refund is by <c>reservation_id</c> and idempotent, so it is issued exactly once per
/// terminal transition and a repeat is a success (WT50). A retry never reuses that reservation: its own row is
/// created with the id <c>ReserveAsync</c> returned for it.
/// </para>
/// </remarks>
public interface IRenderFailureRecorder
{
    /// <summary>
    /// Ends a render attempt with one of the 05 D5 outcome codes: fails the row, queues a retry when the policy
    /// allows one, refunds the reservation once, records the audit row and raises <c>render_failed.v1</c>. A row
    /// the owner already asked to cancel (<c>CancelRequestedAt</c> is set) is always final: it is never retried.
    /// </summary>
    /// <param name="job">The tracked attempt; it must still be queued, submitted, running or ingesting.</param>
    /// <param name="code">The 05 D5 outcome code the attempt is failed with.</param>
    /// <param name="outcomeRetryable">
    /// The 03 classifier's per-outcome verdict, when it has one; it is not even asked for a cancel-requested row.
    /// </param>
    /// <param name="ct">Cancels the reads and the reservation of the retry.</param>
    /// <returns>The retry verdict, or the typed error of the step that refused the failure itself.</returns>
    Task<ErrorOr<RenderFailureResult>> FailAsync(
        RenderJob job,
        string code,
        bool? outcomeRetryable,
        CancellationToken ct);

    /// <summary>
    /// Ends a render attempt the adapter reported terminal <c>cancelled</c> (WT51): cancels the row, returns
    /// the episode to <c>story_approved</c>, refunds the reservation once and records the audit row.
    /// </summary>
    /// <param name="job">The tracked attempt; it must still be submitted or running.</param>
    /// <param name="ct">Cancels the writes.</param>
    /// <returns><c>Success</c>, or the typed error of a row that can no longer be cancelled.</returns>
    Task<ErrorOr<Success>> CancelAsync(RenderJob job, CancellationToken ct);
}
