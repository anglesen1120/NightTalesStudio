using System.Data.Common;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;

namespace NightTales.Application.Features.Rendering.Commands.CancelRender;

/// <summary>
/// <c>DELETE /api/v1/render-jobs/{id}</c> (09 README BD8, 09.6.4-T1). One transaction (D5) behind the job row's
/// lock and behind the D10 tenant filter, with the job's status — read under that lock — deciding what the
/// cancel means.
/// </summary>
/// <remarks>
/// <para>
/// <b>The row lock first, with a bounded wait.</b> <c>SubmitRenderJob</c> holds the same lock across its adapter
/// call (mandated by WT55), so an unbounded wait would queue this request behind a node's HTTP call; the wait is
/// bounded and its refusal answers the existing 409 <c>concurrency_conflict</c> (D5) instead of a 500. The lock
/// is also what makes the "cancel races the poller" and "double cancel across two API instances" rows of
/// 09.6.4's failure table true: the loser of the race is refused, and its retry reads the row's new state.
/// </para>
/// <para>
/// <b>A <c>queued</c> job is cancelled at once.</b> No node ever had the task (<c>adapter_task_id</c> is set by
/// <c>MarkSubmitted</c> alone), so the row goes terminal <c>cancelled</c> with <c>cancel_requested_at</c> null,
/// the reservation is released by its own id (WT50) and the episode returns to <c>story_approved</c>. No outbox
/// row is raised: nothing has to be told. A <c>DELETE</c> that wins the row lock before the queued
/// <c>SubmitRenderJob</c> leaves that job a no-op at its first status guard (WT55); one that lands while the submit
/// holds the lock is refused by the bounded wait with 409 and is retried against the submitted row.
/// </para>
/// <para>
/// <b>A <c>submitted</c>/<c>running</c> job is only asked to cancel.</b> <see cref="RenderJob.RequestCancel"/>
/// stamps <c>cancel_requested_at</c> and raises <c>render_cancel_requested.v1</c>; the Worker's handler asks the
/// node, the poller turns the adapter's terminal <c>cancelled</c> into the row's terminal state and the refund
/// ([RULED — README §B10 WT51]) — the request path never refunds and never calls a node (BD11). The episode
/// stays <c>rendering</c> until that terminal, and a job the adapter answers <c>succeeded</c> is ingested and
/// settled instead (terminal state wins, WT51).
/// </para>
/// <para>
/// <b>An <c>ingesting</c>, <c>succeeded</c> or <c>failed</c> job is refused</b> with the aggregate's own 409
/// <c>episode_invalid_state</c>: the output is already being stored (BD8), and nothing changes.
/// </para>
/// <para>
/// <b>Audit.</b> The command's own row comes from the <c>AuditBehavior</c> inside this transaction (BD15), so
/// the queued path does <em>not</em> go through <see cref="IRenderFailureRecorder"/>: that recorder writes a
/// <c>render.cancelled</c> row of its own and the command would then leave two rows with the same action for one
/// cancel. The writes it would have made are made here, on the same ports — the domain's <c>Cancel</c>, the
/// episode's <c>RenderFailed</c> and the ledger's refund by reservation id. <see cref="CancelRenderCommand.Outcome"/>
/// stays null for every idempotent repeat, so a repeat writes no row.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="ledger">The credit ledger the queued attempt's reservation is released through.</param>
/// <param name="clock">The command's clock reading.</param>
/// <param name="logger">The log of the cancel; every line carries ids and codes only (D19).</param>
public sealed partial class CancelRenderCommandHandler(
    IAppDbContext db,
    ICreditLedger ledger,
    IClock clock,
    ILogger<CancelRenderCommandHandler> logger)
    : ICommandHandler<CancelRenderCommand, ErrorOr<Success>>
{
    /// <summary>
    /// The bounded row-lock wait (the P4 hand-over of 09.6.3-T1: "the 09.6.4 cancel path should use
    /// <c>lock_timeout</c>"). Two seconds clears the poller's short write lock and refuses a request that would
    /// otherwise sit behind a submit's adapter call; the answer is the 409 the client retries.
    /// </summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);

    /// <summary>PostgreSQL's SQLSTATE for a refused wait (<c>lock_not_available</c>).</summary>
    private const string LockNotAvailable = "55P03";

    /// <summary>
    /// The stable description of a contended cancel (09 README D17): it names the caller's situation, never the
    /// SQL or the lock.
    /// </summary>
    private const string LockContendedDescription =
        "The render job is being changed by another request; read it again and retry.";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        CancelRenderCommand command,
        CancellationToken cancellationToken)
    {
        var locked = await LockAsync(command.RenderJobId, cancellationToken).ConfigureAwait(false);
        if (locked.IsError)
        {
            return locked.Errors;
        }

        // Read under the lock, through the tenant filter: another user's id and an unknown id both answer the
        // same 404 (D10), and the status cannot change between this read and the commit below without the
        // xmin token turning that commit into the 409 of D5.
        var job = await db.RenderJobs
            .SingleOrDefaultAsync(candidate => candidate.Id == command.RenderJobId, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            return Errors.NotFound();
        }

        var now = clock.UtcNow;

        switch (job.Status)
        {
            case RenderJobStatus.Queued:
                return await CancelQueuedAsync(job, command, now, cancellationToken).ConfigureAwait(false);

            case RenderJobStatus.Cancelled:
                // Idempotent: the row is already terminal, so nothing is written and the reservation is not
                // refunded a second time (WT50, the "refund already applied" row of 09.6.4's failure table).
                return Result.Success;

            case RenderJobStatus.Submitted:
            case RenderJobStatus.Running:
                if (job.CancelRequestedAt is not null)
                {
                    // The first DELETE stamped the row and wrote its outbox row (or the Worker's delivery of
                    // it is in flight): a second one writes neither again.
                    return Result.Success;
                }

                var requested = job.RequestCancel(now);
                if (requested.IsError)
                {
                    return requested.Errors;
                }

                command.Outcome = new CancelOutcome(job.EpisodeId);

                return Result.Success;

            default:
                // ingesting, succeeded or failed: the aggregate owns the answer — Cancel refuses all three
                // with the 409 episode_invalid_state (BD8) — and nothing is written here.
                return job.Cancel(now);
        }
    }

    /// <summary>
    /// Ends a <c>queued</c> attempt at once (BD8, WT50): the row is terminal <c>cancelled</c> and never carries
    /// <c>cancel_requested_at</c>, its reservation is released by its own id, and the episode returns to
    /// <c>story_approved</c>.
    /// </summary>
    /// <param name="job">The locked, tracked row.</param>
    /// <param name="command">The command whose audit projection records the change.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="cancellationToken">Cancels the refund and the episode read.</param>
    /// <returns><c>Success</c>, or the error that rolls the whole cancel back.</returns>
    private async Task<ErrorOr<Success>> CancelQueuedAsync(
        RenderJob job,
        CancelRenderCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cancelled = job.Cancel(now);
        if (cancelled.IsError)
        {
            return cancelled.Errors;
        }

        await MoveOutOfRenderingAsync(job, now, cancellationToken).ConfigureAwait(false);

        // The release is by reservation id, never by job id (WT50). A refusal is returned rather than swallowed:
        // this is a request path, so the whole cancel rolls back and the client's retry can release the
        // reservation, while a committed cancel whose credits were never released would leave the bound open with
        // nothing left to release it. (The Worker's terminal paths log and commit instead, because there the
        // terminal record must not be lost.)
        var refunded = await ledger
            .RefundAsync(
                new ReservationId(job.ReservationId),
                RenderFailureRecorder.CancelledRefundReason,
                cancellationToken)
            .ConfigureAwait(false);

        if (refunded.IsError)
        {
            return refunded.Errors;
        }

        command.Outcome = new CancelOutcome(job.EpisodeId);

        return Result.Success;
    }

    /// <summary>
    /// Returns the episode to <c>story_approved</c> (BD8, WT50): the attempt ended and the creator may fix the
    /// story or start another render.
    /// </summary>
    /// <remarks>
    /// The transition is idempotent for <c>story_approved</c>, so a refusal means another writer already decided
    /// the episode's state (or the row is gone). It is logged and does <em>not</em> roll the cancel back, exactly
    /// as <see cref="RenderFailureRecorder"/> treats the same refusal: the terminal row and the released
    /// reservation are what the caller asked for, and the episode's own state belongs to the writer that set it.
    /// </remarks>
    /// <param name="job">The cancelled attempt.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="cancellationToken">Cancels the episode read.</param>
    private async Task MoveOutOfRenderingAsync(
        RenderJob job,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == job.EpisodeId, cancellationToken)
            .ConfigureAwait(false);

        if (episode is null)
        {
            LogEpisodeMissing(job.Id, job.EpisodeId);

            return;
        }

        var moved = episode.RenderFailed(now);
        if (moved.IsError)
        {
            LogEpisodeNotRendering(job.Id, job.EpisodeId, moved.FirstError.Code);
        }
    }

    /// <summary>
    /// Takes the job row's lock as the transaction's first statement, with <see cref="LockWait"/> bounding it.
    /// </summary>
    /// <remarks>
    /// The timeout refusal is mapped here rather than in the pipeline: the D5 mapping covers the
    /// <c>SaveChangesAsync</c> refusals of a commit, while a <c>lock_timeout</c> is raised by the lock statement
    /// itself and would otherwise surface as a 500. The provider's SQLSTATE is read through
    /// <see cref="DbException"/> so the Application layer keeps no Npgsql reference.
    /// </remarks>
    /// <param name="renderJobId">The job to lock; nothing is locked for another user's id or an unknown one.</param>
    /// <param name="cancellationToken">Cancels the statements.</param>
    /// <returns><c>Success</c>, or the 409 <c>concurrency_conflict</c> of a contended row.</returns>
    private async Task<ErrorOr<Success>> LockAsync(Guid renderJobId, CancellationToken cancellationToken)
    {
        try
        {
            // The bound is the port's, set for this transaction only, so the jobs that take the same lock keep
            // their unbounded wait (WT55 mandates the submit's long-held lock).
            await db.LockRenderJobForUpdateAsync(renderJobId, LockWait, cancellationToken).ConfigureAwait(false);

            return Result.Success;
        }
        catch (DbException exception) when (IsLockTimeout(exception))
        {
            return Error.Conflict(ErrorCodes.ConcurrencyConflict, LockContendedDescription);
        }
    }

    /// <summary>Whether the provider refused the bounded wait, walking the chain like the feature's other refusals.</summary>
    /// <param name="exception">The exception the lock statement raised.</param>
    /// <returns><c>true</c> for a <c>lock_not_available</c> refusal.</returns>
    private static bool IsLockTimeout(DbException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: LockNotAvailable })
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(
        EventId = 6375,
        Level = LogLevel.Warning,
        Message = "render_cancel_episode_missing renderJobId={RenderJobId} episodeId={EpisodeId}")]
    private partial void LogEpisodeMissing(Guid renderJobId, Guid episodeId);

    [LoggerMessage(
        EventId = 6376,
        Level = LogLevel.Warning,
        Message = "render_cancel_episode_not_rendering renderJobId={RenderJobId} episodeId={EpisodeId} code={Code}")]
    private partial void LogEpisodeNotRendering(Guid renderJobId, Guid episodeId, string code);
}
