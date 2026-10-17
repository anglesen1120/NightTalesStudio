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
/// <b>Three outcomes, one policy.</b> A deferred adapter answer (<c>adapter_capacity_full</c>,
/// <c>adapter_node_unhealthy</c>) and "no dispatchable node" both reschedule a fresh delivery and return
/// normally, so they spend none of the three attempts of <see cref="JobPolicyAttribute.Retries"/> (BD7,
/// 11 T6); a terminal failure ends the row through <see cref="IRenderFailureRecorder"/> inside this job's own
/// transaction; only a transient failure is rethrown, so Hangfire's retry is the retry (BD7, 05 D5). A submit
/// that reached the adapter is idempotent by <c>render_job_id</c> plus the body's SHA-256 (05 D4), so the
/// retry of a lost answer adopts the same task instead of creating a second one.
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

            AlertIfQueuedTooLong(job, now, render);
            Reschedule(renderJobId, healthPoll, scheduler);

            return;
        }

        var node = picked.Value;

        // Step 3. The prompts and the body, from the approved version, the stored params and the style in force
        // (05.1.2-T1, 05 CR-14): a style revision that moved since the command is recomputed and logged.
        var built = await BuildBodyAsync(db, storyInput, presets, job, cancellationToken).ConfigureAwait(false);
        if (built.IsError)
        {
            // No 05 D5 outcome code covers a recipe that cannot be built, and RenderJob.Fail accepts only the
            // ten catalogued ones, so the run is rethrown into Hangfire's retries and the operator alarm
            // (11 D20 H14) with the code in the log rather than failed with a wrong code (open item).
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            LogRecipeInvalid(job.Id, built.FirstError.Code);
            throw new InvalidOperationException(
                $"The render submit body of job '{job.Id}' cannot be built ({built.FirstError.Code}).");
        }

        // Step 4. The one adapter call. The node's URL and key stay inside the client (11 D13).
        var accepted = await client.SubmitAsync(node, built.Value, cancellationToken).ConfigureAwait(false);

        if (accepted.IsError)
        {
            if (!RenderClientErrors.TryGetFailure(accepted.FirstError, out var failure))
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"Submitting job '{job.Id}' failed with an unmapped error ('{accepted.FirstError.Code}').");
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

                AlertIfQueuedTooLong(job, now, render);
                Reschedule(renderJobId, failure.RetryAfter ?? healthPoll, scheduler);

                return;
            }

            if (failure.Kind == RenderClientFailureKind.TerminalFailure)
            {
                // Step 6 (WT28/WT50): the recorder fails the row, refunds the reservation once, writes the audit
                // row and raises render_failed.v1 — all inside this transaction, which commits below.
                if (failure.Alert)
                {
                    LogAdapterAlert(job.Id, failure.OutcomeCode, failure.Detail);
                }

                var failed = await failureRecorder
                    .FailAsync(job, failure.OutcomeCode ?? DefaultRenderTerminalHooks.NodeErrorCode, null, cancellationToken)
                    .ConfigureAwait(false);

                if (failed.IsError)
                {
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    throw new InvalidOperationException(
                        $"Recording the terminal failure of job '{job.Id}' failed ('{failed.FirstError.Code}').");
                }

                await CommitAsync(unitOfWork, job.Id, cancellationToken).ConfigureAwait(false);

                return;
            }

            // A transient answer is the only one the job's retry policy owns: rethrow so Hangfire's three
            // attempts apply, and roll back first so no half-written effect of this run survives.
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            LogTransientFailure(job.Id, failure.OutcomeCode);

            throw new InvalidOperationException(
                $"The render node call of job '{job.Id}' failed transiently ('{failure.OutcomeCode}'); the job is retried.");
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

    /// <summary>Schedules the next delivery of this job, outside the retry budget (BD7).</summary>
    /// <param name="renderJobId">The row to submit again.</param>
    /// <param name="delay">The wait before the next delivery.</param>
    /// <param name="scheduler">The Worker's job port.</param>
    private static void Reschedule(Guid renderJobId, TimeSpan delay, IJobScheduler scheduler) =>
        scheduler.Schedule<SubmitRenderJob>((job, ct) => job.RunAsync(renderJobId, ct), delay);

    /// <summary>
    /// Raises the queue alarm of 11 D20 H14 when the job has been <c>queued</c> longer than
    /// <see cref="RenderOptions.QueueAlertMinutes"/>. It is a log line with a stable event id and ids only: no
    /// content, no node and no params. It repeats on every reschedule past the threshold, which is the
    /// "still no node" signal; the aggregate counter of 11.3.3-T2 is the dashboard's own reading.
    /// </summary>
    /// <param name="job">The queued attempt.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="options">The bound <c>Render</c> section.</param>
    private void AlertIfQueuedTooLong(RenderJob job, DateTimeOffset now, RenderOptions options)
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
