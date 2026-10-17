using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;

namespace NightTales.Application.Features.Rendering.Jobs;

/// <summary>
/// The poll of one render attempt (09.6.3-T1 step 3, BD7): reads the adapter's status once, records the
/// progress (and pushes it), and runs the 03.4.2-T1 terminal order when the task ended.
/// </summary>
/// <remarks>
/// <para>
/// <b>Id only.</b> The job takes the render job id (D12), resolves the row's owner through
/// <see cref="ISystemWorkQuery"/> — the one allow-listed cross-user read — and runs the whole unit of work in a
/// scope pinned by <see cref="ISystemContext.For"/> before any context is resolved, so every query is
/// tenant-filtered and a foreign id is a logged no-op. The rerun is safe: a row the poller does not own
/// (<c>queued</c>, <c>ingesting</c> or terminal) is skipped without a write.
/// </para>
/// <para>
/// <b>The order.</b> Load (skip when terminal) → the <c>Render:MaxMinutes</c> watchdog → the node → one
/// <c>GET /v1/renders/{task_id}</c> → the H2 identity re-check → progress, or the terminal branch. A
/// <c>deferred</c> answer moves <c>next_poll_at</c> and spends no retry budget; a transient one is rethrown to
/// the caller (the next tick polls again); <c>succeeded</c> moves the row to <c>ingesting</c> and commits
/// <em>before</em> the ingest is enqueued; <c>failed</c> runs the image-usage recorder, the classifier and then
/// the failure recorder; <c>cancelled</c> runs the cancellation recorder. The timeout watchdog fails the
/// attempt as <c>render_timeout</c> without a node round-trip and without ever re-submitting it (05 D15 P7).
/// </para>
/// <para>
/// <b>Tenant, secrets and content.</b> The node is resolved by <c>worker_node_id</c>; its base URL, its pinned
/// certificate and its key stay inside <see cref="IRenderClient"/> and never reach a log line. Progress carries
/// ids plus the stage and the percent, inside the same transaction as the write it describes, so the
/// transactional NOTIFY can never announce a progress the rollback took back. Every log line of this class is
/// ids, a status, a code or a count.
/// </para>
/// <para>
/// <b>The adapter's answer is not taken on faith.</b> A body whose task id is not the stored one, whose stage is
/// blank, or whose percent is outside 0-100 or below the stored one is a status this job refuses to store: the
/// attempt ends as <c>render_node_error</c> (or <c>render_lost</c> for a task identity mismatch) instead of
/// writing a refused value or throwing, and the retry policy can give it another node.
/// </para>
/// </remarks>
/// <param name="systemWork">The allow-listed cross-user read that resolves the row's owner (D10).</param>
/// <param name="scopes">Creates the per-job scope the pinned services are resolved from.</param>
/// <param name="logger">The Worker's logger; every line carries ids only.</param>
[JobPolicy(Queue = JobQueue.Render, Retries = 0)]
public sealed partial class PollRenderJob(
    ISystemWorkQuery systemWork,
    IServiceScopeFactory scopes,
    ILogger<PollRenderJob> logger) : IBackgroundJob
{
    /// <summary>Runs one poll of <paramref name="renderJobId"/>.</summary>
    /// <param name="renderJobId">The <c>render_jobs</c> row to poll (the only argument, D12).</param>
    /// <param name="cancellationToken">The poller's token for this job.</param>
    /// <returns>A task that completes when the row advanced, was left to another tick, or ended.</returns>
    public async Task RunAsync(Guid renderJobId, CancellationToken cancellationToken)
    {
        var ownerId = await ResolveOwnerAsync(renderJobId, cancellationToken).ConfigureAwait(false);
        if (ownerId is not { } owner)
        {
            LogJobMissing(renderJobId);

            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(owner);

        // Resolved only after For(): the tenant filter of the context is fixed at construction (D10).
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<IAppDbContext>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var clock = services.GetRequiredService<IClock>();
        var options = services.GetRequiredService<RenderOptions>();
        var metrics = services.GetRequiredService<RenderMetrics>();
        var client = services.GetRequiredService<IRenderClient>();
        var notifier = services.GetRequiredService<IRealtimeNotifier>();
        var hooks = services.GetRequiredService<IRenderTerminalHooks>();
        var recorder = services.GetRequiredService<IRenderFailureRecorder>();
        var ingest = services.GetRequiredService<IRenderIngestEnqueuer>();

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        var job = await db.RenderJobs
            .SingleOrDefaultAsync(candidate => candidate.Id == renderJobId, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            LogJobMissing(renderJobId);
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return;
        }

        // Step 1. Only a submitted or running row is the poller's: a queued one belongs to SubmitRenderJob, an
        // ingesting one is 09.6.3-T2's, and a terminal one never moves again (WT45).
        if (job.Status is not (RenderJobStatus.Submitted or RenderJobStatus.Running))
        {
            LogNotActive(job.Id, job.Status);
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return;
        }

        var now = clock.UtcNow;
        var startedAt = job.StartedAt ?? job.SubmittedAt ?? now;

        // Step 2. The watchdog, evaluated before the node is even asked: a row that has been in flight longer
        // than Render:MaxMinutes is over, and no progress write may follow (05 D15 P7: never a re-submit). The
        // guard is skipped while the row carries no submit stamp — without it there is no instant to measure.
        if (job.SubmittedAt is { } submittedAt
            && now - submittedAt >= TimeSpan.FromMinutes(options.MaxMinutes))
        {
            LogTimedOut(job.Id, (int)(now - submittedAt).TotalMinutes, options.MaxMinutes);

            await FailAsync(
                job,
                RenderOutcomeCodes.RenderTimeout,
                RenderMetrics.OutcomeTimeout,
                RenderStageWire.ForMetric(job.Stage),
                startedAt,
                now,
                null,
                recorder,
                metrics,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        if (job.AdapterTaskId is not { Length: > 0 } taskId || job.WorkerNodeId is not { } nodeId)
        {
            // Submitted rows always carry both (RenderJob.MarkSubmitted writes them together), so this is a
            // hand-written row: the poller has nothing to poll and leaves it to the watchdog.
            LogNotSubmitted(job.Id);
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return;
        }

        // Step 3. The node the attempt went to. The row is looked up, never remembered: nothing of the node
        // reaches this class except the instance the client needs (11 D13).
        var node = await db.WorkerNodes
            .SingleOrDefaultAsync(candidate => candidate.Id == nodeId, cancellationToken)
            .ConfigureAwait(false);

        if (node is null)
        {
            // The node row is gone, so no poll can ever reach its task again and the attempt cannot progress:
            // it ends as a node error, which the retry policy may send to another node.
            LogNodeMissing(job.Id, nodeId);

            await FailAsync(
                job,
                RenderOutcomeCodes.RenderNodeError,
                RenderMetrics.OutcomeFailed,
                RenderStageWire.ForMetric(job.Stage),
                startedAt,
                now,
                null,
                recorder,
                metrics,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        // Step 4. The one status call of this tick.
        var status = await client.GetStatusAsync(node, taskId, cancellationToken).ConfigureAwait(false);

        if (status.IsError)
        {
            if (!RenderClientErrors.TryGetFailure(status.FirstError, out var failure))
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"Polling job '{job.Id}' failed with an unmapped error ('{status.FirstError.Code}').");
            }

            if (failure.Alert)
            {
                LogAdapterAlert(job.Id, failure.OutcomeCode, failure.Detail);
            }

            if (failure.Kind == RenderClientFailureKind.Deferred)
            {
                // Step 5. The node cannot answer now (capacity, a restart, a busy task): the row keeps its
                // status and its attempt, and only next_poll_at moves (BD7, 11 T6).
                await RescheduleAsync(job, now, unitOfWork, cancellationToken).ConfigureAwait(false);

                return;
            }

            if (failure.Kind == RenderClientFailureKind.TerminalFailure)
            {
                // Step 6. render_lost and the node's own terminal faults: the attempt is over, the recorder
                // refunds its reservation once and the retry policy decides whether it gets another node.
                await FailAsync(
                    job,
                    failure.OutcomeCode ?? DefaultRenderTerminalHooks.NodeErrorCode,
                    RenderMetrics.OutcomeFailed,
                    RenderStageWire.ForMetric(job.Stage),
                    startedAt,
                    now,
                    null,
                    recorder,
                    metrics,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);

                return;
            }

            // An output kind cannot come from a status call: the adapter's answer is drift, so the tick is
            // rethrown and the watchdog bounds it (a row that never answers ends as render_timeout).
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            LogUnexpectedFailure(job.Id, failure.Kind.ToString(), failure.OutcomeCode);

            throw new InvalidOperationException(
                $"The status call of job '{job.Id}' failed as '{failure.Kind}' ('{failure.OutcomeCode}').");
        }

        var body = status.Value;

        // Step 5 (H2, 09.6.2 hand-over). The answer must be about the task this row submitted: a body for
        // another task is a task this row no longer owns, which is a lost render, never progress.
        if (!string.Equals(
                body.TaskId.ToString("D", CultureInfo.InvariantCulture),
                taskId,
                StringComparison.Ordinal))
        {
            LogTaskMismatch(job.Id, taskId, body.TaskId);

            await FailAsync(
                job,
                RenderOutcomeCodes.RenderLost,
                RenderMetrics.OutcomeFailed,
                RenderStageWire.ForMetric(job.Stage),
                startedAt,
                now,
                null,
                recorder,
                metrics,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        // Step 5b. A body this job refuses to store ends the attempt instead: the aggregate would throw on a
        // blank stage and refuse a percent that moves backwards, and neither may become a silent clamp.
        if (body.State is not (RenderStates.Failed or RenderStates.Cancelled)
            && Refusal(body, job) is { } refusal)
        {
            LogNotCredible(job.Id, refusal, body.Percent, job.Percent);

            await FailAsync(
                job,
                refusal,
                RenderMetrics.OutcomeFailed,
                RenderStageWire.ForMetric(body.Stage),
                startedAt,
                now,
                null,
                recorder,
                metrics,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        switch (body.State)
        {
            // Step 5c. A non-terminal state is progress. Nothing new is a write-free no-op that only moves the
            // claim instant; anything new is written and pushed in this transaction.
            case RenderStates.Queued:
            case RenderStates.Running:
                await ProgressAsync(job, body, now, startedAt, metrics, notifier, unitOfWork, cancellationToken)
                    .ConfigureAwait(false);

                return;

            // Step 6a. The render finished: the row is taken out of the poller's claim (WT45) and committed
            // before the ingest is handed the id (WF14).
            case RenderStates.Succeeded:
                await SucceedAsync(job, body, now, startedAt, metrics, notifier, unitOfWork, cancellationToken)
                    .ConfigureAwait(false);

                ingest.Enqueue(job.Id);

                return;

            // Step 6b (BD7). 03.4.2-T1's order: the images are metered, the outcome is classified and only then
            // is the attempt failed, refunded, audited and given its retry verdict.
            case RenderStates.Failed:
                await hooks.RecordImageUsageAsync(job, body, cancellationToken).ConfigureAwait(false);

                var classification = await hooks.ClassifyAsync(job, body, cancellationToken).ConfigureAwait(false);

                await FailAsync(
                    job,
                    classification.Code,
                    RenderMetrics.OutcomeFailed,
                    RenderStageWire.ForMetric(body.Stage),
                    startedAt,
                    now,
                    classification.OutcomeRetryable,
                    recorder,
                    metrics,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);

                return;

            // Step 6c (WT51). The adapter's terminal cancelled is the only path that refunds a cancelled render,
            // and a task that reports succeeded instead was ingested above, never cancelled.
            case RenderStates.Cancelled:
                await CancelAsync(job, body, now, startedAt, recorder, metrics, unitOfWork, cancellationToken)
                    .ConfigureAwait(false);

                return;

            default:
                // A state 05.4.1-T2 does not define: the node's contract is broken, so the attempt ends as a
                // node error instead of being polled forever.
                LogUnknownState(job.Id, body.State);

                await FailAsync(
                    job,
                    RenderOutcomeCodes.RenderNodeError,
                    RenderMetrics.OutcomeFailed,
                    RenderStageWire.ForMetric(body.Stage),
                    startedAt,
                    now,
                    null,
                    recorder,
                    metrics,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);

                return;
        }
    }

    /// <summary>
    /// Resolves the row's owner through the one allow-listed cross-user read (D10): a single
    /// <c>(id, user_id)</c> pair crosses the tenant boundary, never another column.
    /// </summary>
    /// <param name="renderJobId">The row to resolve.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The owner, or <see langword="null"/> when the row does not exist.</returns>
    private async Task<Guid?> ResolveOwnerAsync(Guid renderJobId, CancellationToken ct)
    {
        Guid? owner = null;

        await foreach (var (_, userId) in systemWork
            .PendingAsync<RenderJob>(job => job.Id == renderJobId, 1, ct)
            .ConfigureAwait(false))
        {
            owner = userId;
        }

        return owner;
    }

    /// <summary>
    /// Applies a non-terminal status and pushes it when it moved (BD7, WT54): the write, the realtime event and
    /// the next claim instant all belong to this transaction.
    /// </summary>
    /// <param name="job">The polled attempt.</param>
    /// <param name="status">The adapter's status body.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="startedAt">When the attempt started, for the duration observation.</param>
    /// <param name="metrics">The render instruments.</param>
    /// <param name="notifier">The realtime port the progress event goes to.</param>
    /// <param name="unitOfWork">The transaction this poll opened.</param>
    /// <param name="ct">Cancels the commit.</param>
    private static async Task ProgressAsync(
        RenderJob job,
        RenderStatusV1 status,
        DateTimeOffset now,
        DateTimeOffset startedAt,
        RenderMetrics metrics,
        IRealtimeNotifier notifier,
        IUnitOfWork unitOfWork,
        CancellationToken ct)
    {
        var changed = await ApplyProgressAsync(job, status, now, startedAt, metrics, notifier, ct).ConfigureAwait(false);

        if (!changed)
        {
            // The aggregate wrote nothing, so the claim instant did not move: without this the same row would be
            // re-claimed by every tick and would starve the jobs behind it (RenderJob.ReschedulePoll, BD7).
            var rescheduled = job.ReschedulePoll(now);
            if (rescheduled.IsError)
            {
                throw new InvalidOperationException(
                    $"Rescheduling the poll of job '{job.Id}' failed ('{rescheduled.FirstError.Code}').");
            }
        }

        await CommitAsync(unitOfWork, job.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a succeeded attempt to <c>ingesting</c> (UC5, WT45): the progress first, then the transition that
    /// takes the row out of the poller's claim, then the commit — the caller enqueues the ingest only after it.
    /// </summary>
    /// <param name="job">The polled attempt.</param>
    /// <param name="status">The adapter's terminal status body.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="startedAt">When the attempt started.</param>
    /// <param name="metrics">The render instruments.</param>
    /// <param name="notifier">The realtime port the last progress event goes to.</param>
    /// <param name="unitOfWork">The transaction this poll opened.</param>
    /// <param name="ct">Cancels the commit.</param>
    private static async Task SucceedAsync(
        RenderJob job,
        RenderStatusV1 status,
        DateTimeOffset now,
        DateTimeOffset startedAt,
        RenderMetrics metrics,
        IRealtimeNotifier notifier,
        IUnitOfWork unitOfWork,
        CancellationToken ct)
    {
        await ApplyProgressAsync(job, status, now, startedAt, metrics, notifier, ct).ConfigureAwait(false);

        var ingesting = job.MarkIngesting(now);
        if (ingesting.IsError)
        {
            throw new InvalidOperationException(
                $"Moving job '{job.Id}' to ingesting failed ('{ingesting.FirstError.Code}').");
        }

        metrics.ObserveDuration(RenderStageWire.ForMetric(status.Stage), RenderMetrics.OutcomeSucceeded, now - startedAt);

        await CommitAsync(unitOfWork, job.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends an attempt the adapter reported terminal <c>cancelled</c> (WT51): the recorder cancels the row,
    /// returns the episode to <c>story_approved</c>, refunds the reservation once and audits the cancellation.
    /// </summary>
    /// <param name="job">The polled attempt.</param>
    /// <param name="status">The adapter's terminal status body.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="startedAt">When the attempt started.</param>
    /// <param name="recorder">The terminal write path.</param>
    /// <param name="metrics">The render instruments.</param>
    /// <param name="unitOfWork">The transaction this poll opened.</param>
    /// <param name="ct">Cancels the commit.</param>
    private static async Task CancelAsync(
        RenderJob job,
        RenderStatusV1 status,
        DateTimeOffset now,
        DateTimeOffset startedAt,
        IRenderFailureRecorder recorder,
        RenderMetrics metrics,
        IUnitOfWork unitOfWork,
        CancellationToken ct)
    {
        var cancelled = await recorder.CancelAsync(job, ct).ConfigureAwait(false);
        if (cancelled.IsError)
        {
            await unitOfWork.RollbackAsync(ct).ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Recording the cancellation of job '{job.Id}' failed ('{cancelled.FirstError.Code}').");
        }

        metrics.ObserveDuration(RenderStageWire.ForMetric(status.Stage), RenderMetrics.OutcomeCancelled, now - startedAt);

        await CommitAsync(unitOfWork, job.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the adapter's progress and pushes it when it moved, without committing.
    /// </summary>
    /// <param name="job">The polled attempt.</param>
    /// <param name="status">The adapter's status body.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="startedAt">When the attempt started.</param>
    /// <param name="metrics">The render instruments.</param>
    /// <param name="notifier">The realtime port the progress event goes to.</param>
    /// <param name="ct">Cancels the push.</param>
    /// <returns><c>true</c> when the row moved (its stage, its percent or its status changed).</returns>
    private static async Task<bool> ApplyProgressAsync(
        RenderJob job,
        RenderStatusV1 status,
        DateTimeOffset now,
        DateTimeOffset startedAt,
        RenderMetrics metrics,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var stageBefore = job.Stage;
        var percentBefore = job.Percent;
        var stateBefore = job.Status;

        var progress = job.UpdateProgress(status.Stage, (short)status.Percent, now);
        if (progress.IsError)
        {
            throw new InvalidOperationException(
                $"Recording the progress of job '{job.Id}' failed ('{progress.FirstError.Code}').");
        }

        var changed = job.Status != stateBefore
            || job.Percent != percentBefore
            || !string.Equals(stageBefore, job.Stage, StringComparison.Ordinal);

        if (!changed)
        {
            return false;
        }

        // Inside the transaction on purpose: PostgreSQL's NOTIFY is transactional, so the browser never hears
        // about a progress this transaction rolled back, and the payload carries ids plus stage and percent only
        // ([RULED — README §B10 WT54], BD11/BD16).
        await notifier.NotifyAsync(
                job.UserId,
                new RealtimeEvent(
                    RealtimeEventName.RenderProgress,
                    new RealtimeIds(EpisodeId: job.EpisodeId, RenderJobId: job.Id),
                    RealtimeData.ForRenderProgress(RenderStageWire.ForPayload(status.Stage), (short)status.Percent)),
                ct)
            .ConfigureAwait(false);

        metrics.ObserveDuration(RenderStageWire.ForMetric(status.Stage), RenderMetrics.OutcomeRunning, now - startedAt);

        return true;
    }

    /// <summary>
    /// The terminal path of every failure this job owns: the recorder fails the row, refunds its reservation
    /// once, writes the audit row and raises the outbox event inside this transaction, the histogram is observed,
    /// and the transaction is committed.
    /// </summary>
    /// <param name="job">The failed attempt.</param>
    /// <param name="code">The 05 D5 outcome code.</param>
    /// <param name="outcome">The histogram's outcome label.</param>
    /// <param name="stageLabel">The histogram's bounded stage label.</param>
    /// <param name="startedAt">When the attempt started.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="outcomeRetryable">The classifier's per-outcome verdict, when it has one.</param>
    /// <param name="recorder">The terminal write path.</param>
    /// <param name="metrics">The render instruments.</param>
    /// <param name="unitOfWork">The transaction this poll opened.</param>
    /// <param name="ct">Cancels the commit.</param>
    private static async Task FailAsync(
        RenderJob job,
        string code,
        string outcome,
        string stageLabel,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        bool? outcomeRetryable,
        IRenderFailureRecorder recorder,
        RenderMetrics metrics,
        IUnitOfWork unitOfWork,
        CancellationToken ct)
    {
        var recorded = await recorder.FailAsync(job, code, outcomeRetryable, ct).ConfigureAwait(false);
        if (recorded.IsError)
        {
            await unitOfWork.RollbackAsync(ct).ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Recording the terminal failure of job '{job.Id}' failed ('{recorded.FirstError.Code}').");
        }

        metrics.ObserveDuration(stageLabel, outcome, now - startedAt);

        await CommitAsync(unitOfWork, job.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the claim instant of a row that keeps its status (<c>deferred</c>) and commits it.
    /// </summary>
    /// <param name="job">The polled attempt.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="unitOfWork">The transaction this poll opened.</param>
    /// <param name="ct">Cancels the commit.</param>
    private static async Task RescheduleAsync(
        RenderJob job,
        DateTimeOffset now,
        IUnitOfWork unitOfWork,
        CancellationToken ct)
    {
        var rescheduled = job.ReschedulePoll(now);
        if (rescheduled.IsError)
        {
            await unitOfWork.RollbackAsync(ct).ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Rescheduling the poll of job '{job.Id}' failed ('{rescheduled.FirstError.Code}').");
        }

        await CommitAsync(unitOfWork, job.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The code a status body is refused with, or <see langword="null"/> when it may be stored.
    /// </summary>
    /// <remarks>
    /// A blank stage and a percent outside 0-100 are a node that breaks its own contract
    /// (<c>render_node_error</c>); a percent below the stored one is evidence of a restarted or wrong task, which
    /// is a lost render rather than a silent clamp (<c>render_lost</c>). Both are retryable, so a node that
    /// misreports costs one attempt instead of hanging the episode until the watchdog.
    /// </remarks>
    /// <param name="status">The adapter's status body.</param>
    /// <param name="job">The polled attempt.</param>
    /// <returns>The outcome code, or <c>null</c> when the body is credible.</returns>
    private static string? Refusal(RenderStatusV1 status, RenderJob job)
    {
        if (string.IsNullOrWhiteSpace(status.Stage) || status.Percent is < 0 or > 100)
        {
            return RenderOutcomeCodes.RenderNodeError;
        }

        return status.Percent < job.Percent ? RenderOutcomeCodes.RenderLost : null;
    }

    /// <summary>
    /// Commits the poll's transaction; a refused commit (a lost update, a transient error) is rethrown so the
    /// row stays as it was and the next tick polls it again.
    /// </summary>
    /// <param name="unitOfWork">The transaction this poll opened.</param>
    /// <param name="renderJobId">The row being polled, for the error message.</param>
    /// <param name="ct">The caller's token.</param>
    private static async Task CommitAsync(IUnitOfWork unitOfWork, Guid renderJobId, CancellationToken ct)
    {
        var committed = await unitOfWork.CommitAsync(ct).ConfigureAwait(false);
        if (committed.IsError)
        {
            throw new InvalidOperationException(
                $"Committing the poll of job '{renderJobId}' failed ('{committed.FirstError.Code}').");
        }
    }

    [LoggerMessage(
        EventId = 6220,
        Level = LogLevel.Warning,
        Message = "poll_render_job_missing renderJobId={RenderJobId}")]
    private partial void LogJobMissing(Guid renderJobId);

    [LoggerMessage(
        EventId = 6221,
        Level = LogLevel.Information,
        Message = "poll_render_job_not_active renderJobId={RenderJobId} status={Status}")]
    private partial void LogNotActive(Guid renderJobId, RenderJobStatus status);

    [LoggerMessage(
        EventId = 6222,
        Level = LogLevel.Warning,
        Message = "poll_render_job_not_submitted renderJobId={RenderJobId}")]
    private partial void LogNotSubmitted(Guid renderJobId);

    [LoggerMessage(
        EventId = 6223,
        Level = LogLevel.Error,
        Message = "render_poll_timed_out renderJobId={RenderJobId} inFlightMinutes={InFlightMinutes} limitMinutes={LimitMinutes}")]
    private partial void LogTimedOut(Guid renderJobId, int inFlightMinutes, int limitMinutes);

    [LoggerMessage(
        EventId = 6224,
        Level = LogLevel.Warning,
        Message = "render_poll_node_missing renderJobId={RenderJobId} workerNodeId={WorkerNodeId}")]
    private partial void LogNodeMissing(Guid renderJobId, Guid workerNodeId);

    [LoggerMessage(
        EventId = 6225,
        Level = LogLevel.Warning,
        Message = "render_poll_task_mismatch renderJobId={RenderJobId} expectedTaskId={ExpectedTaskId} reportedTaskId={ReportedTaskId}")]
    private partial void LogTaskMismatch(Guid renderJobId, string expectedTaskId, Guid reportedTaskId);

    [LoggerMessage(
        EventId = 6226,
        Level = LogLevel.Warning,
        Message = "render_poll_status_refused renderJobId={RenderJobId} code={Code} reportedPercent={ReportedPercent} storedPercent={StoredPercent}")]
    private partial void LogNotCredible(Guid renderJobId, string code, int reportedPercent, short storedPercent);

    [LoggerMessage(
        EventId = 6227,
        Level = LogLevel.Warning,
        Message = "render_poll_state_unknown renderJobId={RenderJobId} state={State}")]
    private partial void LogUnknownState(Guid renderJobId, string state);

    [LoggerMessage(
        EventId = 6228,
        Level = LogLevel.Warning,
        Message = "render_poll_adapter_alert renderJobId={RenderJobId} code={Code} detail={Detail}")]
    private partial void LogAdapterAlert(Guid renderJobId, string? code, string? detail);

    [LoggerMessage(
        EventId = 6229,
        Level = LogLevel.Warning,
        Message = "render_poll_unexpected_failure renderJobId={RenderJobId} kind={Kind} code={Code}")]
    private partial void LogUnexpectedFailure(Guid renderJobId, string kind, string? code);
}
