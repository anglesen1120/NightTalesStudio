using System.Globalization;
using ErrorOr;
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
/// <b>The order.</b> Load (skip when not active) → the node → one <c>GET /v1/renders/{task_id}</c> → the H2
/// identity re-check → progress, or the terminal branch. The <c>Render:MaxMinutes</c> watchdog is applied
/// <em>after</em> that answer and to a non-terminal one only: a task that already reported <c>succeeded</c>,
/// <c>failed</c> or <c>cancelled</c> is recorded as what it is, and only a still-running one past the limit
/// ends as <c>render_timeout</c> — best-effort adapter cancel and delete after the commit, never a re-submit
/// (05 D15 P7). A <c>deferred</c> answer moves <c>next_poll_at</c> by its own <c>Retry-After</c>, never past the
/// watchdog's horizon (else by the <c>Render:PollSeconds</c> cadence) and spends no retry budget; a
/// <c>transient</c> one moves it by the cadence behind one stack-free warning and keeps the attempt, so a node
/// that is down cannot pin its rows at the head of the claim; <c>succeeded</c> moves the row to <c>ingesting</c>
/// and commits <em>before</em> the ingest is enqueued; <c>failed</c> runs the image-usage recorder, the
/// classifier and then the failure recorder; <c>cancelled</c> runs the cancellation recorder.
/// </para>
/// <para>
/// <b>No transaction across the node call.</b> Every read before the status answer is a plain autocommit
/// query, and <c>IUnitOfWork.BeginAsync</c> is called only by the branch that is about to write, because the
/// node call may take up to 30 s: holding a pooled connection and an <c>idle in transaction</c> session across
/// it would starve the connections Hangfire and the outbox share. The row's <c>xmin</c> token still guards the
/// commit — a cancel that lands while the node was answering refuses the write with the usual
/// <c>concurrency_conflict</c> and the next tick polls the row again — and a branch that only reads opens no
/// transaction at all.
/// </para>
/// <para>
/// <b>Tenant, secrets and content.</b> The node is resolved by <c>worker_node_id</c>; its base URL, its pinned
/// certificate and its key stay inside <see cref="IRenderClient"/> and never reach a log line. Progress carries
/// ids plus the stage and the percent, inside the same transaction as the write it describes, so the
/// transactional NOTIFY can never announce a progress the rollback took back. Every log line of this class is
/// ids, a status, a code or a count.
/// </para>
/// <para>
/// <b>The adapter's answer is not taken on faith.</b> A body whose task id is not the stored one is a lost
/// render (<c>render_lost</c>); a body whose stage is not the wire shape <c>^[a-z_]{1,32}$</c> — which is also
/// the length of the column that would store it — or whose percent is outside 0-100 is one this job refuses to
/// store (<c>render_node_error</c>), because a blank or over-long stage would otherwise be a write refused on
/// every tick. A percent <em>below</em> the stored one is not refused: the stored percent is kept, nothing is
/// pushed and the attempt keeps polling. An unknown state is logged sanitised and ends the attempt the same
/// way.
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
    private const string InvalidToken = "invalid";

    /// <summary>The longest stage or state an adapter may report: <c>render_jobs.stage</c>'s own bound.</summary>
    private const int WireTokenMaxLength = 32;

    /// <summary>The cleanup step that asks the node to stop a task.</summary>
    private const string CancelOperation = "cancel";

    /// <summary>The cleanup step that asks the node to release a task's material.</summary>
    private const string DeleteOperation = "delete";

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
        var pollInterval = TimeSpan.FromSeconds(options.PollSeconds);

        // No transaction is opened yet, and none is opened until this tick knows it has a write to make: the
        // status call below is an HTTP round trip of up to 30 s, so the reads before it are plain autocommit
        // queries and every branch that writes opens its own transaction through IUnitOfWork (P1).
        var job = await db.RenderJobs
            .SingleOrDefaultAsync(candidate => candidate.Id == renderJobId, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            LogJobMissing(renderJobId);

            return;
        }

        // Step 1. Only a submitted or running row is the poller's: a queued one belongs to SubmitRenderJob, an
        // ingesting one is 09.6.3-T2's, and a terminal one never moves again (WT45).
        if (job.Status is not (RenderJobStatus.Submitted or RenderJobStatus.Running))
        {
            LogNotActive(job.Id, job.Status);

            return;
        }

        var now = clock.UtcNow;
        var startedAt = job.StartedAt ?? job.SubmittedAt ?? now;

        // Step 2. The watchdog's deadline is read here and applied <em>after</em> the status handling below: a
        // task that already answered succeeded, failed or cancelled wins over it, and only a non-terminal answer
        // past Render:MaxMinutes ends the attempt as render_timeout (05 D15 P7: never a re-submit). The guard is
        // skipped while the row carries no submit stamp — without it there is no instant to measure.
        var timedOut = job.SubmittedAt is { } submittedAt
            && now - submittedAt >= TimeSpan.FromMinutes(options.MaxMinutes);

        if (job.AdapterTaskId is not { Length: > 0 } taskId || job.WorkerNodeId is not { } nodeId)
        {
            // Submitted rows always carry both (RenderJob.MarkSubmitted writes them together), so this is a
            // hand-written row: the poller has nothing to poll and leaves it to the watchdog.
            LogNotSubmitted(job.Id);

            return;
        }

        // Step 3. The node the attempt went to. The row is looked up, never remembered: nothing of the node
        // reaches this class except the instance the client needs (11 D13). It is read tracked-free, because
        // nothing here ever writes it (P6).
        var node = await db.WorkerNodes
            .AsNoTracking()
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
                throw new InvalidOperationException(
                    $"Polling job '{job.Id}' failed with an unmapped error ('{status.FirstError.Code}').");
            }

            if (failure.Alert)
            {
                LogAdapterAlert(job.Id, failure.OutcomeCode, failure.Detail);
            }

            if (failure.Kind is RenderClientFailureKind.Deferred or RenderClientFailureKind.TransientFailure)
            {
                // Step 5. The node cannot answer now (capacity, a restart, a busy task, a transport error or a
                // 5xx that survived the client's retries): the row keeps its status and its attempt, and only
                // next_poll_at moves. A deferred answer moves it by its own bounded Retry-After (the cadence
                // when it gave none); a transient one by the cadence, behind one stack-free warning — rethrown
                // instead, the row would keep the oldest next_poll_at and be re-claimed, with a full exception
                // line, on every single tick (F4, BD7, 11 T6).
                if (timedOut)
                {
                    await FailTimedOutAsync(
                            job,
                            options,
                            startedAt,
                            now,
                            RenderStageWire.ForMetric(job.Stage),
                            node,
                            taskId,
                            client,
                            recorder,
                            metrics,
                            unitOfWork,
                            cancellationToken)
                        .ConfigureAwait(false);

                    return;
                }

                if (failure.Kind == RenderClientFailureKind.TransientFailure)
                {
                    LogTransientStatus(job.Id, failure.OutcomeCode);
                }

                await RescheduleAsync(
                        job,
                        now,
                        IntervalOf(failure, pollInterval, TimeSpan.FromMinutes(options.MaxMinutes)),
                        unitOfWork,
                        cancellationToken)
                    .ConfigureAwait(false);

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

            // The task this row owns is not the one the node answered about, so nothing else will ever collect
            // it: the node is asked to stop it and to release its material, best effort (SEC-8).
            await CleanUpAsync(client, node, taskId, job.Id, cancellationToken).ConfigureAwait(false);

            return;
        }

        // Step 5b. A body this job refuses to store ends the attempt instead: the aggregate throws on a blank
        // stage, and a stage outside the wire shape would be refused by the column on every tick.
        if (body.State is not (RenderStates.Failed or RenderStates.Cancelled)
            && Refusal(body) is { } refusal)
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

            await CleanUpAsync(client, node, taskId, job.Id, cancellationToken).ConfigureAwait(false);

            return;
        }

        switch (body.State)
        {
            // Step 5c. A non-terminal state is progress — unless the attempt is past the watchdog, which is
            // evaluated here, after the terminal answers won: a task that is still running at minute 61 is
            // render_timeout, and the same adapter task is never re-submitted (05 D15 P7). Nothing new is a
            // write-free no-op that only moves the claim instant; anything new is written and pushed in this
            // transaction.
            case RenderStates.Queued:
            case RenderStates.Running:
                if (timedOut)
                {
                    await FailTimedOutAsync(
                            job,
                            options,
                            startedAt,
                            now,
                            RenderStageWire.ForMetric(body.Stage),
                            node,
                            taskId,
                            client,
                            recorder,
                            metrics,
                            unitOfWork,
                            cancellationToken)
                        .ConfigureAwait(false);

                    return;
                }

                await ProgressAsync(
                        job,
                        body,
                        now,
                        startedAt,
                        pollInterval,
                        metrics,
                        notifier,
                        unitOfWork,
                        cancellationToken)
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
            // is the attempt failed, refunded, audited and given its retry verdict. The transaction opens before
            // the first of the three, because the usage recorder writes (P1).
            case RenderStates.Failed:
                await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
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
                // node error instead of being polled forever. The state reaches the log line sanitised, because
                // it is adapter-supplied text (SEC-6).
                LogUnknownState(job.Id, LoggableState(body.State));

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
    /// Applies a non-terminal status and pushes it when it moved (BD7, WT54): the transaction is opened here,
    /// because the status call is over and this is the first branch that writes.
    /// </summary>
    /// <param name="job">The polled attempt.</param>
    /// <param name="status">The adapter's status body.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="startedAt">When the attempt started, for the duration observation.</param>
    /// <param name="pollInterval">The <c>Render:PollSeconds</c> claim distance.</param>
    /// <param name="metrics">The render instruments.</param>
    /// <param name="notifier">The realtime port the progress event goes to.</param>
    /// <param name="unitOfWork">The transaction this poll opened.</param>
    /// <param name="ct">Cancels the commit.</param>
    private static async Task ProgressAsync(
        RenderJob job,
        RenderStatusV1 status,
        DateTimeOffset now,
        DateTimeOffset startedAt,
        TimeSpan pollInterval,
        RenderMetrics metrics,
        IRealtimeNotifier notifier,
        IUnitOfWork unitOfWork,
        CancellationToken ct)
    {
        await unitOfWork.BeginAsync(ct).ConfigureAwait(false);

        var changed = await ApplyProgressAsync(job, status, now, notifier, ct).ConfigureAwait(false);

        if (!changed)
        {
            // The aggregate wrote nothing, so the claim instant did not move: without this the same row would be
            // re-claimed by every tick and would starve the jobs behind it (RenderJob.ReschedulePoll, BD7).
            var rescheduled = job.ReschedulePoll(now, pollInterval);
            if (rescheduled.IsError)
            {
                throw new InvalidOperationException(
                    $"Rescheduling the poll of job '{job.Id}' failed ('{rescheduled.FirstError.Code}').");
            }
        }

        await CommitAsync(unitOfWork, job.Id, ct).ConfigureAwait(false);

        // After the commit: a lost update discards this progress, and an observation written before it would
        // count a report no client ever heard (F10).
        if (changed)
        {
            metrics.ObserveDuration(
                RenderStageWire.ForMetric(status.Stage),
                RenderMetrics.OutcomeRunning,
                now - startedAt);
        }
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
        await unitOfWork.BeginAsync(ct).ConfigureAwait(false);

        await ApplyProgressAsync(job, status, now, notifier, ct).ConfigureAwait(false);

        var ingesting = job.MarkIngesting(now);
        if (ingesting.IsError)
        {
            throw new InvalidOperationException(
                $"Moving job '{job.Id}' to ingesting failed ('{ingesting.FirstError.Code}').");
        }

        await CommitAsync(unitOfWork, job.Id, ct).ConfigureAwait(false);

        // One observation per transition: the terminal path does not also observe the progress it applied above
        // (F12), and it is written after the commit like every other terminal outcome (F10).
        metrics.ObserveDuration(RenderStageWire.ForMetric(status.Stage), RenderMetrics.OutcomeSucceeded, now - startedAt);
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
        await unitOfWork.BeginAsync(ct).ConfigureAwait(false);

        var cancelled = await recorder.CancelAsync(job, ct).ConfigureAwait(false);
        if (cancelled.IsError)
        {
            await unitOfWork.RollbackAsync(ct).ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Recording the cancellation of job '{job.Id}' failed ('{cancelled.FirstError.Code}').");
        }

        await CommitAsync(unitOfWork, job.Id, ct).ConfigureAwait(false);

        // After the commit, like every terminal observation: a lost update leaves the row running, so the
        // cancellation it would have counted never happened (F10).
        metrics.ObserveDuration(RenderStageWire.ForMetric(status.Stage), RenderMetrics.OutcomeCancelled, now - startedAt);
    }

    /// <summary>
    /// Records the adapter's progress and pushes it when it moved, without committing and without observing a
    /// duration (the caller observes it, after the commit).
    /// </summary>
    /// <param name="job">The polled attempt.</param>
    /// <param name="status">The adapter's status body.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="notifier">The realtime port the progress event goes to.</param>
    /// <param name="ct">Cancels the push.</param>
    /// <returns><c>true</c> when the row moved (its stage, its percent or its status changed).</returns>
    private static async Task<bool> ApplyProgressAsync(
        RenderJob job,
        RenderStatusV1 status,
        DateTimeOffset now,
        IRealtimeNotifier notifier,
        CancellationToken ct)
    {
        var stageBefore = job.Stage;
        var percentBefore = job.Percent;
        var stateBefore = job.Status;

        // A percent below the stored one is a regressed report, not a lost render: a paid attempt is never
        // failed and re-run (and re-charged) for a counter that moved backwards. The stored percent is kept, so
        // a report that only regressed writes nothing, pushes nothing and keeps polling (F6).
        var percent = (short)Math.Max(status.Percent, job.Percent);

        var progress = job.UpdateProgress(status.Stage, percent, now);
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
                    RealtimeData.ForRenderProgress(RenderStageWire.ForPayload(status.Stage), percent)),
                ct)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// The terminal path of every failure this job owns: the transaction is opened here, the recorder fails the
    /// row, refunds its reservation once, writes the audit row and raises the outbox event inside it, and the
    /// histogram and the retry counter are moved only once it committed.
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
        await unitOfWork.BeginAsync(ct).ConfigureAwait(false);

        var recorded = await recorder.FailAsync(job, code, outcomeRetryable, ct).ConfigureAwait(false);
        if (recorded.IsError)
        {
            await unitOfWork.RollbackAsync(ct).ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Recording the terminal failure of job '{job.Id}' failed ('{recorded.FirstError.Code}').");
        }

        await CommitAsync(unitOfWork, job.Id, ct).ConfigureAwait(false);

        // After the commit, never before it: a lost update discards the failed row together with the retry row,
        // so a counter or a histogram written earlier would count a failure and a retry that never existed
        // (F10). The retry counter is the caller's, because the recorder may no longer count it itself.
        metrics.ObserveDuration(stageLabel, outcome, now - startedAt);

        if (recorded.Value.RetryCreated)
        {
            metrics.CountRetry(code);
        }
    }

    /// <summary>
    /// Ends an attempt the <c>Render:MaxMinutes</c> watchdog owns (05 D15 P7), after the adapter was asked: the
    /// row fails as <c>render_timeout</c> with no re-submit of the same task, and the adapter task the node may
    /// still be running is cancelled and deleted best-effort once the failure is committed (SEC-8).
    /// </summary>
    /// <param name="job">The attempt that ran past the limit.</param>
    /// <param name="options">The bound <c>Render</c> section, for the limit the log line carries.</param>
    /// <param name="startedAt">When the attempt started.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="stageLabel">The histogram's bounded stage label.</param>
    /// <param name="node">The node the attempt went to.</param>
    /// <param name="taskId">The adapter's task id.</param>
    /// <param name="client">The node client the best-effort cleanup goes through.</param>
    /// <param name="recorder">The terminal write path.</param>
    /// <param name="metrics">The render instruments.</param>
    /// <param name="unitOfWork">The transaction this poll opened.</param>
    /// <param name="ct">Cancels the commit and the cleanup.</param>
    private async Task FailTimedOutAsync(
        RenderJob job,
        RenderOptions options,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        string stageLabel,
        WorkerNode node,
        string taskId,
        IRenderClient client,
        IRenderFailureRecorder recorder,
        RenderMetrics metrics,
        IUnitOfWork unitOfWork,
        CancellationToken ct)
    {
        if (job.SubmittedAt is { } submittedAt)
        {
            LogTimedOut(job.Id, (int)(now - submittedAt).TotalMinutes, options.MaxMinutes);
        }

        await FailAsync(
                job,
                RenderOutcomeCodes.RenderTimeout,
                RenderMetrics.OutcomeTimeout,
                stageLabel,
                startedAt,
                now,
                null,
                recorder,
                metrics,
                unitOfWork,
                ct)
            .ConfigureAwait(false);

        await CleanUpAsync(client, node, taskId, job.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort cleanup of the adapter task an attempt this job ended left behind (SEC-8): the node is asked
    /// to stop it and to release its material, so a timed-out or refused task cannot keep occupying node
    /// capacity (and spending image budget) while its retry renders the same episode again.
    /// </summary>
    /// <param name="client">The node client.</param>
    /// <param name="node">The node the attempt went to.</param>
    /// <param name="taskId">The adapter's task id.</param>
    /// <param name="renderJobId">The row the cleanup belongs to, for the log lines.</param>
    /// <param name="ct">Cancels the calls.</param>
    private async Task CleanUpAsync(
        IRenderClient client,
        WorkerNode node,
        string taskId,
        Guid renderJobId,
        CancellationToken ct)
    {
        await BestEffortAsync(renderJobId, CancelOperation, () => client.CancelAsync(node, taskId, ct))
            .ConfigureAwait(false);
        await BestEffortAsync(renderJobId, DeleteOperation, () => client.DeleteAsync(node, taskId, ct))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one cleanup call and reports its outcome as an id and a code.
    /// </summary>
    /// <remarks>
    /// Nothing here may throw, on any answer: the failure the cleanup follows is already committed and the node
    /// is no longer the job's to reason about, so a refused or broken cleanup is a warning and nothing else. The
    /// exception's type name is logged instead of its message, because a message may carry node-supplied text.
    /// </remarks>
    /// <param name="renderJobId">The row the cleanup belongs to.</param>
    /// <param name="operation">The cleanup step (<c>cancel</c> or <c>delete</c>).</param>
    /// <param name="call">The call to make.</param>
    private async Task BestEffortAsync(Guid renderJobId, string operation, Func<Task<ErrorOr<Success>>> call)
    {
        try
        {
            var answer = await call().ConfigureAwait(false);
            if (answer.IsError)
            {
                LogCleanupFailed(renderJobId, operation, answer.FirstError.Code);
            }
        }
        catch (Exception exception)
        {
            LogCleanupFailed(renderJobId, operation, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Moves the claim instant of a row that keeps its status (<c>deferred</c> or <c>transient</c>) and commits
    /// it.
    /// </summary>
    /// <param name="job">The polled attempt.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="interval">The distance to the next poll: the adapter's own wait, or the cadence.</param>
    /// <param name="unitOfWork">The transaction this poll opened.</param>
    /// <param name="ct">Cancels the commit.</param>
    private static async Task RescheduleAsync(
        RenderJob job,
        DateTimeOffset now,
        TimeSpan interval,
        IUnitOfWork unitOfWork,
        CancellationToken ct)
    {
        await unitOfWork.BeginAsync(ct).ConfigureAwait(false);

        var rescheduled = job.ReschedulePoll(now, interval);
        if (rescheduled.IsError)
        {
            await unitOfWork.RollbackAsync(ct).ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Rescheduling the poll of job '{job.Id}' failed ('{rescheduled.FirstError.Code}').");
        }

        await CommitAsync(unitOfWork, job.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The distance to the next poll of a rescheduled row: the adapter's own wait when it gave one, the
    /// <c>Render:PollSeconds</c> cadence otherwise, and never more than the watchdog's own horizon.
    /// </summary>
    /// <remarks>
    /// The adapter's <c>Retry-After</c> is header-derived (<c>AdapterErrorMapper.RetryAfterOf</c> clamps it to
    /// 1-300 s), so the poll clamps it a second time, to <paramref name="maxInterval"/>: a node may ask for more
    /// time than the attempt has, but it may not park the row past the instant <c>Render:MaxMinutes</c> ends it,
    /// or the watchdog could never fire on a node that keeps deferring — and a wait of zero or less is no wait
    /// at all, so the cadence stands (F13, 05 D4).
    /// </remarks>
    /// <param name="failure">The deferred or transient answer that rescheduled the row.</param>
    /// <param name="pollInterval">The configured cadence.</param>
    /// <param name="maxInterval">The longest wait a poll may take: the <c>Render:MaxMinutes</c> horizon.</param>
    /// <returns>The interval the claim instant moves by; always positive.</returns>
    private static TimeSpan IntervalOf(RenderClientFailure failure, TimeSpan pollInterval, TimeSpan maxInterval)
    {
        if (failure.RetryAfter is not { } retryAfter || retryAfter <= TimeSpan.Zero)
        {
            return pollInterval;
        }

        return retryAfter < maxInterval ? retryAfter : maxInterval;
    }

    /// <summary>
    /// The code a status body is refused with, or <see langword="null"/> when it may be stored.
    /// </summary>
    /// <remarks>
    /// A stage that is not the wire shape <c>^[a-z_]{1,32}$</c>, or a percent outside 0-100, is a node breaking
    /// its own contract: the stage shape is also the length of the column it would be stored in, so an over-long
    /// one would be refused by the database on every tick instead of ending the attempt once
    /// (<c>render_node_error</c>). It is retryable, so a node that misreports costs one attempt instead of
    /// hanging the episode until the watchdog. A percent below the stored one is <em>not</em> refused: it is
    /// ignored and the poll continues (F6).
    /// </remarks>
    /// <param name="status">The adapter's status body.</param>
    /// <returns>The outcome code, or <c>null</c> when the body is credible.</returns>
    private static string? Refusal(RenderStatusV1 status) =>
        IsWireStage(status.Stage) && status.Percent is >= 0 and <= 100 ? null : RenderOutcomeCodes.RenderNodeError;

    /// <summary>
    /// Whether a node-supplied string is shaped like an adapter state or stage: <c>^[a-z_]{1,32}$</c>, the shape
    /// <c>MptRenderClient</c> already trusts for a log line and the bound of the stage column.
    /// </summary>
    /// <param name="value">The adapter's text, or <c>null</c>.</param>
    /// <returns><c>true</c> when it is a lower-case underscore token of one to 32 characters.</returns>
    private static bool IsWireStage(string? value)
    {
        if (value is not { Length: > 0 and <= WireTokenMaxLength })
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterLower(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The text a debug line may carry for an adapter-supplied string: <see cref="InvalidToken"/> for anything
    /// outside <see cref="IsWireStage"/>, so a log line can never take node-supplied text (SEC-6, BD16/S4).
    /// </summary>
    /// <param name="value">The adapter's text, or <c>null</c>.</param>
    /// <returns>The token, or <see cref="InvalidToken"/>.</returns>
    private static string LoggableState(string? value) => IsWireStage(value) ? value! : InvalidToken;

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

    [LoggerMessage(
        EventId = 6230,
        Level = LogLevel.Warning,
        Message = "render_poll_transient renderJobId={RenderJobId} code={Code}")]
    private partial void LogTransientStatus(Guid renderJobId, string? code);

    [LoggerMessage(
        EventId = 6231,
        Level = LogLevel.Warning,
        Message = "render_poll_cleanup_failed renderJobId={RenderJobId} operation={Operation} code={Code}")]
    private partial void LogCleanupFailed(Guid renderJobId, string operation, string? code);
}
