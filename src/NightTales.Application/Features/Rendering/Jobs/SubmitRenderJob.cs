using System.Globalization;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Stories;
using NightTales.Application.Features.Catalog;
using NightTales.Application.Rendering;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Rendering.Music;
using NightTales.Domain.Rendering.Subtitles;
using NightTales.Domain.Stories.Content;
using NightTales.RenderNode.Contract;

namespace NightTales.Application.Features.Rendering.Jobs;

/// <summary>
/// The Hangfire job of one <c>queued</c> render attempt (09.6.3-T1 step 1, D12): it picks a dispatchable
/// node, builds the <c>render-submit.v1</c> body of 05 D3/D4 and adopts the adapter's task id on the row.
/// Enqueued by <see cref="RenderRequestedHandler"/> when the command's <c>render_requested.v1</c> row is
/// delivered (D11) — never by the Api.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two reschedules and four failures, one policy.</b> A deferred adapter answer
/// (<c>adapter_capacity_full</c>, <c>adapter_node_unhealthy</c>) and "no dispatchable node" both reschedule a
/// fresh delivery and return normally, so they spend no retry budget (BD7, 11 T6). Every failure — a terminal
/// adapter answer, a transient one, an error the client's mapper does not know, and a recipe the submit body
/// cannot be built from — ends the row through <see cref="IRenderFailureRecorder"/> inside this job's own
/// transaction: the recorder refunds the reservation once, writes the audit row and raises
/// <c>render_failed.v1</c>, and <c>RenderRetryPolicy</c> decides whether a fresh attempt follows (WT28/WT50).
/// An adapter outcome is never rethrown to Hangfire, because a rethrow leaves the row <c>queued</c> for good
/// once the attempts are spent: nothing refunds it, its bound stays in the open sum and
/// <c>ux_render_jobs_active_episode</c> keeps the episode. A submit that reached the adapter is idempotent by
/// <c>render_job_id</c> plus the body's SHA-256 (05 D4), so a retried delivery adopts the same task instead of
/// creating a second one.
/// </para>
/// <para>
/// <b>Where BD7's transport retries went.</b> The three retries of <see cref="JobPolicyAttribute.Retries"/>
/// are spent <em>inside</em> the single <c>SubmitAsync</c> call: <c>MptRenderClient</c>'s pipeline retries an
/// idempotent call with exponential backoff under a 30 s total timeout and a 10 s attempt timeout (BD6/WF08).
/// A transient answer therefore means the transport budget is already gone, and this job spends no second loop
/// on it — an in-job retry would multiply the pipeline's own attempts and hold the row lock across every one
/// of them. The remaining <c>Retries = 3</c> covers the faults that are not the adapter's: a lost database
/// connection on the commit, an activation failure.
/// </para>
/// <para>
/// <b>The row is locked before the adapter call and re-checked after it</b> [RULED — README §B10 WT55]. A
/// cancel that lands while the submit is in flight (or a row that left <c>queued</c>) means the accepted task
/// must not be adopted: it is cancelled and deleted at once and <see cref="RenderJob.MarkSubmitted"/> never
/// runs, so no orphan adapter task and no phantom <c>submitted</c> row survive.
/// </para>
/// <para>
/// <b>Tenant and content.</b> The job knows only the render job id (D12), resolves the owner through
/// <see cref="ISystemWorkQuery"/> and runs the whole unit of work in a scope pinned by
/// <see cref="ISystemContext.For"/> before any context is resolved (D10). It takes the style template, the
/// story text and the music track in order to build the body, and stores none of them: the submit carries the
/// story text and the prompts, neither the row, a log line nor an exception of this class does (P6/P8, BD16),
/// and the node's base URL and key never leave the client (11 D13).
/// </para>
/// <para>
/// <b>Open hand-over.</b> Step 0 of the task's design — 12.8.3-T2's paid-dispatch gate
/// (<c>IPaidDispatchGate.IsOpenAsync</c>, <c>provider_funds_exhausted</c>) — is not implemented here because
/// the port does not exist yet; its reschedule answer is the one of the no-node path below.
/// </para>
/// </remarks>
/// <param name="systemWork">The allow-listed cross-user read that resolves the row's owner (D10).</param>
/// <param name="scopes">Creates the per-run scope the pinned services are resolved from.</param>
/// <param name="logger">The Worker's logger; every line carries ids only.</param>
[JobPolicy(Queue = JobQueue.Render, Retries = 3)]
public sealed partial class SubmitRenderJob(
    ISystemWorkQuery systemWork,
    IServiceScopeFactory scopes,
    ILogger<SubmitRenderJob> logger) : IBackgroundJob
{
    /// <summary>The event id of the <c>render_queue_alert</c> line (11 D20 H14).</summary>
    public const int QueueAlertEventId = 6192;

    /// <summary>The code returned when the stored params name a language the wire table does not know.</summary>
    public const string LanguageUnknownCode = "render_language_unknown";

    /// <summary>The code returned when the job's style template no longer exists.</summary>
    public const string StyleTemplateMissingCode = "render_style_template_missing";

    /// <summary>The code returned when the stored params name a music track the catalog no longer holds.</summary>
    public const string MusicTrackMissingCode = "render_music_track_missing";

    /// <summary>The code returned when the approved version plans no scene at all.</summary>
    public const string NoScenesCode = "render_no_scenes";

    /// <summary>Runs the submit of <paramref name="renderJobId"/>.</summary>
    /// <param name="renderJobId">The <c>render_jobs</c> row (the only argument, D12).</param>
    /// <param name="cancellationToken">Hangfire's shutdown token.</param>
    /// <returns>A task that completes when the row was submitted, rescheduled, failed or left to another run.</returns>
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
        var render = services.GetRequiredService<RenderOptions>();
        var nodeOptions = services.GetRequiredService<RenderNodesOptions>();
        var scheduler = services.GetRequiredService<IJobScheduler>();
        var selector = services.GetRequiredService<INodeSelector>();
        var client = services.GetRequiredService<IRenderClient>();
        var storyInput = services.GetRequiredService<IStoryRenderInputProvider>();
        var presets = services.GetRequiredService<SubtitlePresetCatalog>();
        var failureRecorder = services.GetRequiredService<IRenderFailureRecorder>();
        var metrics = services.GetRequiredService<RenderMetrics>();

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        // Step 1 (WT55): the row lock, then the tenant-filtered load. Another user's id and a vanished row both
        // lock and load nothing, so the job is a logged no-op.
        await db.LockRenderJobForUpdateAsync(renderJobId, cancellationToken).ConfigureAwait(false);
        var job = await db.RenderJobs
            .SingleOrDefaultAsync(candidate => candidate.Id == renderJobId, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            LogJobMissing(renderJobId);
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return;
        }

        // Delivery is at least once (D11): an adopted task or a row that left `queued` is a no-op, so a second
        // delivery of `render_requested.v1` can never create a second adapter task (BD7 acceptance).
        if (job.AdapterTaskId is not null || job.Status != RenderJobStatus.Queued)
        {
            LogAlreadyDispatched(job.Id, job.Status);
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return;
        }

        var now = clock.UtcNow;
        var healthPoll = TimeSpan.FromSeconds(nodeOptions.HealthPollSeconds);

        // Step 2. No dispatchable node is a reschedule, never a spent attempt and never a failure (BD7).
        var picked = await selector.PickAsync(cancellationToken).ConfigureAwait(false);
        if (picked.IsError)
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            AlertIfQueuedTooLong(job, now, healthPoll, render);
            Reschedule(renderJobId, healthPoll, scheduler);

            return;
        }

        var node = picked.Value;

        // Step 3. The prompts and the body, from the approved version, the stored params and the style in force
        // (05.1.2-T1, 05 CR-14): a style revision that moved since the command is recomputed and logged.
        var built = await BuildBodyAsync(db, storyInput, presets, job, cancellationToken).ConfigureAwait(false);
        if (built.IsError)
        {
            // A recipe that cannot be built is deterministic — a rebuilt body would fail the same way — so the
            // attempt ends here. No 05 D5 code names a broken recipe; the closest catalogued one is
            // render_assembly_failed (the render cannot be assembled), and outcomeRetryable: false stops the
            // recorder from queuing a retry the recipe can never satisfy. The precise reason is in the log's code.
            LogRecipeInvalid(job.Id, built.FirstError.Code);

            await RecordFailureAsync(
                    failureRecorder,
                    metrics,
                    unitOfWork,
                    job,
                    RenderOutcomeCodes.RenderAssemblyFailed,
                    outcomeRetryable: false,
                    cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        // Step 4. The one adapter call. The node's URL and key stay inside the client (11 D13), and the pipeline
        // behind it already spent the transport retries (class remarks).
        var accepted = await client.SubmitAsync(node, built.Value, cancellationToken).ConfigureAwait(false);

        if (accepted.IsError)
        {
            if (!RenderClientErrors.TryGetFailure(accepted.FirstError, out var failure))
            {
                // Fail closed: an error the client's mapper does not know is adapter drift, and rethrowing it
                // would strand the row. It is recorded as the generic node failure so the refund, the audit row
                // and the retry decision all run; the log names the code the client answered with.
                LogUnmappedFailure(job.Id, accepted.FirstError.Code);

                await RecordFailureAsync(
                        failureRecorder,
                        metrics,
                        unitOfWork,
                        job,
                        DefaultRenderTerminalHooks.NodeErrorCode,
                        outcomeRetryable: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            if (failure.Kind == RenderClientFailureKind.Deferred)
            {
                // Step 5. The node cannot take the work now: its own RetryAfter, else the health cadence, and
                // never a failure (BD7, 11 T6).
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                if (failure.Alert)
                {
                    LogAdapterAlert(job.Id, failure.OutcomeCode, failure.Detail);
                }

                var wait = failure.RetryAfter ?? healthPoll;
                AlertIfQueuedTooLong(job, now, wait, render);
                Reschedule(renderJobId, wait, scheduler);

                return;
            }

            var code = failure.OutcomeCode ?? DefaultRenderTerminalHooks.NodeErrorCode;

            if (failure.Kind == RenderClientFailureKind.TerminalFailure)
            {
                // Step 6 (WT28/WT50): the recorder fails the row, refunds the reservation once, writes the audit
                // row and raises render_failed.v1 — all inside this transaction, which the recorder's result
                // commits. A terminal answer is the one shape an operator must be told about.
                if (failure.Alert)
                {
                    LogAdapterAlert(job.Id, failure.OutcomeCode, failure.Detail);
                }
            }
            else
            {
                // TransientFailure, or an output kind a submit can never answer with (drift): either way the
                // attempt ends here. Rethrowing would leave the row `queued` for good once Hangfire's attempts
                // are spent, with its reservation unrefunded and its bound still open; RenderRetryPolicy instead
                // decides whether attempt 2 follows, so BD7's ≤ 2 attempts still hold.
                LogTransientFailure(job.Id, failure.OutcomeCode);
            }

            await RecordFailureAsync(
                    failureRecorder,
                    metrics,
                    unitOfWork,
                    job,
                    code,
                    outcomeRetryable: null,
                    cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        // The stored adapter_task_id is the wire guid in its canonical form: the same string is what the status,
        // cancel and delete routes carry (05 D4).
        var taskId = accepted.Value.TaskId.ToString("D", CultureInfo.InvariantCulture);

        // Step 7 (WT55). Takes the lock again and re-reads: a row that carries a cancel request or left
        // `queued` while the call was in flight never adopts the task — it is cancelled and deleted at once.
        await db.LockRenderJobForUpdateAsync(renderJobId, cancellationToken).ConfigureAwait(false);
        var recheck = await db.RenderJobs
            .AsNoTracking()
            .Where(candidate => candidate.Id == renderJobId)
            .Select(candidate => new { candidate.Status, candidate.CancelRequestedAt })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (recheck is null || recheck.CancelRequestedAt is not null || recheck.Status != RenderJobStatus.Queued)
        {
            LogSubmitRacedCancellation(job.Id, taskId);
            await CancelAndDeleteAsync(client, node, taskId, job.Id, cancellationToken).ConfigureAwait(false);
            await CommitAsync(unitOfWork, job.Id, cancellationToken).ConfigureAwait(false);

            return;
        }

        // Step 8. The adapter accepted the task: the row is submitted, with the accepted task id, the node and
        // the first poll instant (BD7; RenderJob.MarkSubmitted stamps now + RenderJob.PollInterval).
        var marked = job.MarkSubmitted(taskId, node.Id, now);
        if (marked.IsError)
        {
            LogSubmitRacedCancellation(job.Id, taskId);
            await CancelAndDeleteAsync(client, node, taskId, job.Id, cancellationToken).ConfigureAwait(false);
            await CommitAsync(unitOfWork, job.Id, cancellationToken).ConfigureAwait(false);

            return;
        }

        await CommitAsync(unitOfWork, job.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The instant a render job was queued, read from its UUIDv7 id: <c>render_jobs</c> has no
    /// <c>created_at</c> column (WT49) and a queued row carries neither <see cref="RenderJob.StartedAt"/> nor
    /// <see cref="RenderJob.SubmittedAt"/>, so the id — generated with <c>Guid.CreateVersion7()</c> by
    /// 09.6.1-T1 under the command's clock — is the only creation stamp the aggregate offers.
    /// </summary>
    /// <param name="renderJobId">The row's id.</param>
    /// <param name="queuedAt">The id's embedded timestamp, when the id is a version 7 UUID.</param>
    /// <returns><c>false</c> for an id that is not a version 7 UUID (a hand-built fixture), so the alarm is
    /// simply not evaluated instead of guessing.</returns>
    public static bool TryGetQueuedAt(Guid renderJobId, out DateTimeOffset queuedAt)
    {
        queuedAt = default;

        if (renderJobId.Version != 7)
        {
            return false;
        }

        // bigEndian: true writes the RFC 4122 byte order, whose first six bytes are the 48-bit Unix-millisecond
        // timestamp the version 7 layout fixes.
        Span<byte> bytes = stackalloc byte[16];
        if (!renderJobId.TryWriteBytes(bytes, bigEndian: true, out _))
        {
            return false;
        }

        var milliseconds = 0L;
        for (var index = 0; index < 6; index++)
        {
            milliseconds = (milliseconds << 8) | bytes[index];
        }

        queuedAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

        return true;
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
    /// Builds the <c>render-submit.v1</c> body of one attempt (05 D3/D4, 05.1.2-T1 CR-14): the story text and
    /// the scene prompts are recomputed from the approved version, the style in force is compared with the
    /// stored <c>style_prompt_version</c>, and a plan or version that moved is recomputed — the send then
    /// describes what is really rendered instead of what was queued.
    /// </summary>
    /// <param name="db">The pinned write model (style template and music catalog reads).</param>
    /// <param name="storyInput">The approved version's persisted text and scene keywords.</param>
    /// <param name="presets">The subtitle preset catalog.</param>
    /// <param name="job">The queued attempt, whose <see cref="RenderJob.Params"/> are the stored settings.</param>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>The body, or the first reason it cannot be built.</returns>
    private async Task<ErrorOr<AdapterSubmitRequestV1>> BuildBodyAsync(
        IAppDbContext db,
        IStoryRenderInputProvider storyInput,
        SubtitlePresetCatalog presets,
        RenderJob job,
        CancellationToken ct)
    {
        var stored = job.Params;

        var story = await storyInput.GetAsync(stored.StoryVersionId, ct).ConfigureAwait(false);
        if (story.IsError)
        {
            return story.Errors;
        }

        if (!CatalogWireNames.TryParseLanguage(stored.Language, out var language))
        {
            return Error.Failure(LanguageUnknownCode, "The stored render params name an unknown language.");
        }

        var style = await db.StyleTemplates
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == stored.StyleTemplateId, ct)
            .ConfigureAwait(false);

        if (style is null)
        {
            return Error.Failure(StyleTemplateMissingCode, "The style template of the stored render params no longer exists.");
        }

        // The plan is not stored on the row (params carry its counts only): it is recomputed from the same
        // approved version, language and tier the command planned from (03.2 plan.md §4).
        var plan = RenderImagePlanner.Plan(new RenderImagePlanInput(
            story.Value.Segments,
            StoryText.CountTokens(story.Value.Body),
            language,
            story.Value.Tier,
            ImagePlanOptions.Defaults));

        if (plan.Terms.Count == 0)
        {
            return Error.Failure(NoScenesCode, "The approved version plans no scene, so no submit can be built.");
        }

        var stylePromptVersion = StylePromptVersion.For(style);
        var effective = stored;
        var stale = !string.Equals(stored.StylePromptVersion, stylePromptVersion, StringComparison.Ordinal)
            || stored.ImagePlan.Planned != plan.Terms.Count;

        if (stored.Aspect is not (SubtitlePresetCatalog.LandscapeAspect or SubtitlePresetCatalog.PortraitAspect))
        {
            return Error.Validation(
                RenderSubmitMapper.AspectInvalid,
                "The series aspect must be 'landscape' or 'portrait'.");
        }

        var preset = presets.For(stored.Aspect);

        MusicTrack? track = null;
        if (stored.Music is { } music)
        {
            track = await db.MusicTracks
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.TrackId == music.TrackId, ct)
                .ConfigureAwait(false);

            if (track is null)
            {
                return Error.Failure(MusicTrackMissingCode, "The music track of the stored render params no longer exists.");
            }
        }

        if (stale)
        {
            effective = RenderSubmitMapper.BuildParams(new RenderParamsInput(
                stored.StoryVersionId,
                stored.Aspect,
                stored.Language,
                stored.VoiceId,
                stored.StyleTemplateId,
                stylePromptVersion,
                ImagePlanSummary.From(plan, stylePromptVersion),
                stored.Subtitles,
                stored.Music is { } musicChoice
                    ? new MusicChoice(musicChoice.TrackId, track!.FileName, musicChoice.BgmVolume)
                    : null,
                stored.Music?.Attribution,
                stored.ClipSeconds));

            LogImagePlanRecomputed(
                job.Id,
                stored.StylePromptVersion,
                stylePromptVersion,
                stored.ImagePlan.Planned,
                plan.Terms.Count);
        }

        var prompts = RenderSubmitMapper.BuildPrompts(plan, style);

        return RenderSubmitMapper.BuildSubmit(job.Id, effective, story.Value, plan, prompts, preset, track);
    }

    /// <summary>
    /// Cancels and then deletes the task this run just created, for the WT55 race only: the row left
    /// <c>queued</c> while the call was in flight, so the task must not outlive the attempt. Both calls are
    /// idempotent (05 D4), and a failure is logged and swallowed — a retry of this job exits at the row's state
    /// and could never issue the call again.
    /// </summary>
    /// <param name="client">The node client.</param>
    /// <param name="node">The node the task went to.</param>
    /// <param name="taskId">The adapter's task id.</param>
    /// <param name="renderJobId">The row, for the log line only.</param>
    /// <param name="ct">Cancels the calls.</param>
    private async Task CancelAndDeleteAsync(
        IRenderClient client,
        WorkerNode node,
        string taskId,
        Guid renderJobId,
        CancellationToken ct)
    {
        var cancelled = await client.CancelAsync(node, taskId, ct).ConfigureAwait(false);
        if (cancelled.IsError)
        {
            LogAdapterCleanupFailed(renderJobId, "cancel", cancelled.FirstError.Code);
        }

        var deleted = await client.DeleteAsync(node, taskId, ct).ConfigureAwait(false);
        if (deleted.IsError)
        {
            LogAdapterCleanupFailed(renderJobId, "delete", deleted.FirstError.Code);
        }
    }

    /// <summary>Commits the run's unit of work; a failed commit is a transient database error and rethrows.</summary>
    /// <param name="unitOfWork">The job's unit of work.</param>
    /// <param name="renderJobId">The row, for the message only.</param>
    /// <param name="ct">Cancels the commit.</param>
    private static async Task CommitAsync(IUnitOfWork unitOfWork, Guid renderJobId, CancellationToken ct)
    {
        var committed = await unitOfWork.CommitAsync(ct).ConfigureAwait(false);
        if (committed.IsError)
        {
            throw new InvalidOperationException(
                $"Committing the submit of job '{renderJobId}' failed ('{committed.FirstError.Code}').");
        }
    }

    /// <summary>
    /// Ends the attempt through the one failure path (WT28/WT50): the recorder fails the row, decides and
    /// queues a retry, refunds the reservation once, writes the audit row and raises <c>render_failed.v1</c>
    /// inside this job's own transaction, and this method commits it.
    /// </summary>
    /// <remarks>
    /// The retry counter is fed <em>after</em> the commit, and only when a retry row was really created: a lost
    /// commit discards that row, so counting before it would report a retry that does not exist (F10). A refusal
    /// from the recorder is a programming error of this call site (a state the recorder cannot fail), and it is
    /// rethrown for Hangfire's remaining attempts.
    /// </remarks>
    /// <param name="recorder">The one terminal-outcome write path.</param>
    /// <param name="metrics">The retry counter.</param>
    /// <param name="unitOfWork">The job's unit of work.</param>
    /// <param name="job">The attempt being ended.</param>
    /// <param name="code">The 05 D5 outcome code of this failure.</param>
    /// <param name="outcomeRetryable">The retry verdict the caller already knows, when it knows one.</param>
    /// <param name="ct">Cancels the recorder's reads and the commit.</param>
    private static async Task RecordFailureAsync(
        IRenderFailureRecorder recorder,
        RenderMetrics metrics,
        IUnitOfWork unitOfWork,
        RenderJob job,
        string code,
        bool? outcomeRetryable,
        CancellationToken ct)
    {
        var failed = await recorder
            .FailAsync(job, code, outcomeRetryable, ct)
            .ConfigureAwait(false);

        if (failed.IsError)
        {
            await unitOfWork.RollbackAsync(ct).ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Recording the failure of render job '{job.Id}' failed ('{failed.FirstError.Code}').");
        }

        await CommitAsync(unitOfWork, job.Id, ct).ConfigureAwait(false);

        if (failed.Value.RetryCreated)
        {
            metrics.CountRetry(failed.Value.FailureCode);
        }
    }

    /// <summary>Schedules the next delivery of this job, outside the retry budget (BD7).</summary>
    /// <param name="renderJobId">The row to submit again.</param>
    /// <param name="delay">The wait before the next delivery.</param>
    /// <param name="scheduler">The Worker's job port.</param>
    private static void Reschedule(Guid renderJobId, TimeSpan delay, IJobScheduler scheduler) =>
        scheduler.Schedule<SubmitRenderJob>((job, ct) => job.RunAsync(renderJobId, ct), delay);

    /// <summary>
    /// Raises the queue alarm of 11 D20 H14 when the job crosses <see cref="RenderOptions.QueueAlertMinutes"/>
    /// <c>queued</c>. It is a log line with a stable event id and ids only: no content, no node and no params.
    /// </summary>
    /// <remarks>
    /// The alarm fires on the one delivery that crosses the threshold, never on every delivery after it: a job
    /// with no dispatchable node is offered again every <c>RenderNodes:HealthPollSeconds</c> (30 s), so a
    /// repeating alarm would be 120 lines an hour of noise per stuck job (F8). The crossing is read as "the
    /// previous delivery instant was still inside the threshold": the previous instant is this delivery's own
    /// instant minus the wait it just scheduled, which is the cadence when the alarm is about a missing node and
    /// the adapter's <c>Retry-After</c> when it is about a busy one. Nothing here counts attempts — the alarm
    /// never spends retry budget, and the aggregate counter of 11.3.3-T2 is the dashboard's own reading.
    /// </remarks>
    /// <param name="job">The queued attempt.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="wait">The distance to the delivery this call is scheduling.</param>
    /// <param name="options">The bound <c>Render</c> section.</param>
    private void AlertIfQueuedTooLong(RenderJob job, DateTimeOffset now, TimeSpan wait, RenderOptions options)
    {
        if (!TryGetQueuedAt(job.Id, out var queuedAt))
        {
            return;
        }

        var age = now - queuedAt;
        var threshold = TimeSpan.FromMinutes(options.QueueAlertMinutes);
        if (age < threshold)
        {
            return;
        }

        // The previous delivery instant was still inside the threshold, so this delivery is the crossing one —
        // and the only one that logs. A job already past it when it is first offered (the threshold moved, or a
        // backlog delayed the delivery) is not announced at all: the alarm reports the crossing, not the state.
        if (age - wait >= threshold)
        {
            return;
        }

        LogQueueAlert(job.Id, (int)age.TotalMinutes, options.QueueAlertMinutes);
    }

    [LoggerMessage(
        EventId = 6190,
        Level = LogLevel.Warning,
        Message = "submit_render_job_missing renderJobId={RenderJobId}")]
    private partial void LogJobMissing(Guid renderJobId);

    [LoggerMessage(
        EventId = 6191,
        Level = LogLevel.Information,
        Message = "submit_render_job_already_dispatched renderJobId={RenderJobId} status={Status}")]
    private partial void LogAlreadyDispatched(Guid renderJobId, RenderJobStatus status);

    [LoggerMessage(
        EventId = QueueAlertEventId,
        Level = LogLevel.Error,
        Message = "render_queue_alert renderJobId={RenderJobId} queuedMinutes={QueuedMinutes} thresholdMinutes={ThresholdMinutes}")]
    private partial void LogQueueAlert(Guid renderJobId, int queuedMinutes, int thresholdMinutes);

    [LoggerMessage(
        EventId = 6193,
        Level = LogLevel.Warning,
        Message = "render_submit_raced_cancellation renderJobId={RenderJobId} taskId={TaskId}")]
    private partial void LogSubmitRacedCancellation(Guid renderJobId, string taskId);

    [LoggerMessage(
        EventId = 6194,
        Level = LogLevel.Information,
        Message = "image_plan_recomputed renderJobId={RenderJobId} storedVersion={StoredVersion} currentVersion={CurrentVersion} storedPlanned={StoredPlanned} planned={Planned}")]
    private partial void LogImagePlanRecomputed(
        Guid renderJobId,
        string storedVersion,
        string currentVersion,
        int storedPlanned,
        int planned);

    [LoggerMessage(
        EventId = 6195,
        Level = LogLevel.Error,
        Message = "render_submit_recipe_invalid renderJobId={RenderJobId} code={Code}")]
    private partial void LogRecipeInvalid(Guid renderJobId, string code);

    [LoggerMessage(
        EventId = 6196,
        Level = LogLevel.Warning,
        Message = "render_submit_adapter_alert renderJobId={RenderJobId} code={Code} detail={Detail}")]
    private partial void LogAdapterAlert(Guid renderJobId, string? code, string? detail);

    // 6197 is taken by LoggingRenderIngestEnqueuer.LogIngestNotWired (IRenderIngestEnqueuer.cs) and 6200-6202 by
    // RenderPollingService, so this one line of the submit sits in the gap above them (6203 stays free for the
    // poller's next line).
    [LoggerMessage(
        EventId = 6204,
        Level = LogLevel.Error,
        Message = "render_submit_unmapped_failure renderJobId={RenderJobId} code={Code}")]
    private partial void LogUnmappedFailure(Guid renderJobId, string code);

    [LoggerMessage(
        EventId = 6198,
        Level = LogLevel.Warning,
        Message = "render_submit_transient_failure renderJobId={RenderJobId} code={Code}")]
    private partial void LogTransientFailure(Guid renderJobId, string? code);

    [LoggerMessage(
        EventId = 6199,
        Level = LogLevel.Warning,
        Message = "render_adapter_cleanup_failed renderJobId={RenderJobId} call={Call} code={Code}")]
    private partial void LogAdapterCleanupFailed(Guid renderJobId, string call, string code);
}
