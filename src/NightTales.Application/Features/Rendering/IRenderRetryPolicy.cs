using ErrorOr;
using NightTales.Domain.Rendering;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The retry verdict of a failed render attempt (BD7, 03 D7): whether one more attempt is queued, and the
/// creation of that attempt's own <c>render_jobs</c> row.
/// </summary>
/// <remarks>
/// <para>
/// Only transient outcome codes are retried, at most once (<c>attempt &lt; 2</c>), and a retry is always a
/// <b>new row with its own reservation</b>: the new attempt pays for itself (WT50). The 03 classifier's
/// per-outcome flag wins over the static tables, because <c>image_generation_failed</c> is retryable for some
/// image rows and not for others (03.4.2-T1).
/// </para>
/// <para>
/// <b>The failed row's reservation is refunded after this runs, not before.</b> The recorder's order is
/// <c>Fail</c> → this retry (its own <c>ReserveAsync</c>) → the refund of the failed attempt, so the retry's
/// budget re-check still sees the failed bound in the open sum and excludes it by id. With the real credit
/// ledger of 12.1/12.2 an owner at their reserve limit can therefore be refused the retry until that refund has
/// credited the failed bound back (09.6.3-T1 F9) — a known consequence of the ordering, which is deliberately
/// not reordered.
/// </para>
/// <para>
/// <see cref="CreateRetryAsync"/> runs inside the caller's failure transaction (D5): the failed row, its
/// refund, the audit row, the retry row and the <c>render_requested.v1</c> outbox row of the new attempt
/// commit or roll back together. It counts nothing itself: the caller counts one retry after its commit, on
/// <c>RenderFailureResult.RetryCreated</c>, so <c>render_retries_total</c> never counts a discarded row.
/// </para>
/// </remarks>
public interface IRenderRetryPolicy
{
    /// <summary>Decides whether the failed attempt gets one more try.</summary>
    /// <param name="code">The 05 D5 outcome code the attempt failed with.</param>
    /// <param name="attempt">The attempt number of the failed row, counted from 1.</param>
    /// <param name="outcomeRetryable">
    /// The 03 classifier's per-outcome verdict, when it has one; <c>null</c> falls back to the static code
    /// tables.
    /// </param>
    /// <returns><c>true</c> when a retry attempt must be queued.</returns>
    /// <remarks>
    /// A row the owner asked to cancel is never put to this table: <c>IRenderFailureRecorder.FailAsync</c> makes
    /// such an attempt final whatever this would answer (WT51).
    /// </remarks>
    bool ShouldRetry(string code, int attempt, bool? outcomeRetryable = null);

    /// <summary>
    /// Queues the next attempt of a failed render job: re-checks the platform budget, reserves the new
    /// attempt's own credits and inserts its <c>queued</c> row.
    /// </summary>
    /// <param name="failed">The row the attempt is retried from; it must already be <c>failed</c>.</param>
    /// <param name="ct">Cancels the reads and the reservation.</param>
    /// <returns>
    /// The new <c>queued</c> row, or the typed error of the step that refused it (no active price version, a
    /// spend cap, an insufficient credit balance) — a refusal means <em>no retry</em> and a final failure.
    /// </returns>
    Task<ErrorOr<RenderJob>> CreateRetryAsync(RenderJob failed, CancellationToken ct);
}
