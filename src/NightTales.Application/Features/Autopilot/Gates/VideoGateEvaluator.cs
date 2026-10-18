using System.Diagnostics;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Compliance;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The video half of the auto-approval gate (02.8.2 plan §3.2, §3.3, §4 UC4): it loads the finished render of one
/// autopilot run's episode from stored data, evaluates the ten checks (<see cref="VideoGateRules"/>), records the
/// verdict on <c>autopilot_runs.gate_results</c> and, on a live pass, approves the cut through the same
/// <c>ApproveVideoCommand</c> a user calls, as a system actor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads only, and never a paid call.</b> Everything comes from the episode's newest <c>succeeded</c>
/// <c>render_jobs</c> row (the highest UUIDv7 id, WT49), its stored <c>quality_report</c>, the episode's video asset
/// row, the series aspect and the <c>openai_image</c> moderation rows of that render. A render that already failed
/// the 05.5.1 policy never reaches this gate (09.6.3 ended it <c>render_quality_failed</c>); check 2 is defence in
/// depth for the stored verdict.
/// </para>
/// <para>
/// <b>Fail closed, never a pass on unknown data.</b> A missing run, a run without an episode, an episode without a
/// series, an episode without a <c>succeeded</c> render and an episode that is neither <c>video_review</c> nor
/// <c>ready</c> are answered with an <see cref="ErrorOr{T}"/> error and no verdict at all: the run step is retried
/// by the tick, and the video is never approved from a state the gate could not read. Every member the gate *could*
/// not measure is a null in the input, which fails its own check with <c>field_absent</c> and leaves the episode in
/// <c>video_review</c> for the owner (plan §"Failure handling").
/// </para>
/// <para>
/// <b>One transaction per verdict.</b> The <c>gate_results</c> write, its audit row and the owner notice commit
/// together inside <see cref="GateOutcomeRecorder"/> (00 §5.2), and the approval is the command's own unit of work,
/// so a crash between the two leaves a recorded verdict and an unapproved video, never an approval nobody recorded.
/// The replay of an already-approved run is a success: an episode that reached <c>ready</c> for this same render job
/// is not approved a second time and writes no second audit row.
/// </para>
/// <para>
/// <b>The caller pins the tenant scope.</b> The run's owner is what every read and write here belongs to, and
/// 09 README D10 makes the scope (not this class) responsible for it: the caller resolves
/// <c>ISystemContext.For(run.UserId)</c> before its <c>DbContext</c> exists — the per-user run scope of 02.8.3-T1,
/// exactly as the other per-user jobs do — so <see cref="IAppDbContext"/> here is already the owner's. The
/// approval of a live pass is <see cref="AutopilotApprovalService"/>'s own scope, which it pins itself. No HTTP
/// route exposes this class; 02.8.3-T1's orchestrator is its only caller.
/// </para>
/// </remarks>
/// <param name="db">The caller's write model; every read of this class is tracked-free.</param>
/// <param name="factory">Maps the stored rows and documents into the gate's input (fail closed).</param>
/// <param name="recorder">Writes the verdict, the audit row and the owner notice in one transaction.</param>
/// <param name="approvals">Sends the user's own approve command on a live pass, as a system actor.</param>
/// <param name="options">
/// The bound <c>Autopilot:Gates</c> section (mode, profiles). It arrives as a value, not as a monitor: the
/// Application assembly may not reference <c>Microsoft.Extensions.Options</c>, so the host registers the scoped
/// <c>CurrentValue</c> of its monitor and every evaluation still reads the live section.
/// </param>
/// <param name="metrics">The D19 gate metrics; every return path records exactly one.</param>
/// <param name="logger">The ids-only log of this gate.</param>
public sealed partial class VideoGateEvaluator(
    IAppDbContext db,
    VideoGateInputFactory factory,
    GateOutcomeRecorder recorder,
    AutopilotApprovalService approvals,
    AutopilotGateOptions options,
    AutopilotGateMetrics metrics,
    ILogger<VideoGateEvaluator> logger)
{
    /// <summary>The <c>gate</c> label of every metric and approval-failure row this class writes.</summary>
    public const string GateName = "video";

    /// <summary>
    /// The <c>moderation_events.stage</c> value 03.4.2-T1 writes per moderated image slot (03.4 plan): the only
    /// discriminator <c>moderation_events</c> has for an image row, since the table carries no render-job column.
    /// </summary>
    public const string ImageModerationStage = "render_image";

    /// <summary>The error code of a run id that does not exist in the caller's scope.</summary>
    public const string RunMissingErrorCode = "autopilot_gate_run_missing";

    /// <summary>The error code of a run that names no episode.</summary>
    public const string EpisodeMissingErrorCode = "autopilot_gate_episode_missing";

    /// <summary>The error code of an episode whose series cannot be read.</summary>
    public const string SeriesMissingErrorCode = "autopilot_gate_series_missing";

    /// <summary>The error code of an episode with no <c>succeeded</c> render: there is no video to gate.</summary>
    public const string RenderMissingErrorCode = "autopilot_gate_render_missing";

    /// <summary>The error code of a live pass whose episode is not in <c>video_review</c> and not already <c>ready</c>.</summary>
    public const string EpisodeStateErrorCode = "autopilot_gate_episode_state";

    /// <summary>The metric result of a verdict that passed and was approved (or replayed).</summary>
    private const string MetricPass = "pass";

    /// <summary>The metric result of a verdict that failed a check, or passed but could not be approved.</summary>
    private const string MetricFail = "fail";

    /// <summary>The metric result of a passing verdict in dry-run mode, which approves nothing.</summary>
    private const string MetricDryRun = "dry_run";

    /// <summary>The metric result of an evaluation that could not produce a usable verdict.</summary>
    private const string MetricError = "error";

    /// <summary>
    /// Evaluates the video gate of one run and, on a live pass, approves the cut.
    /// </summary>
    /// <param name="runId">The autopilot run whose episode's finished render is gated.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// The verdict and what the gate did with it, or — never a verdict — <see cref="RunMissingErrorCode"/>,
    /// <see cref="EpisodeMissingErrorCode"/>, <see cref="SeriesMissingErrorCode"/>,
    /// <see cref="RenderMissingErrorCode"/> or <see cref="EpisodeStateErrorCode"/> when the gate has nothing to
    /// evaluate, or the error of the write/approval that failed (a recorded failure code travels through
    /// <see cref="GateOutcomeRecorder.RecordApprovalFailureAsync"/> instead).
    /// </returns>
    public async ValueTask<ErrorOr<VideoGateOutcome>> EvaluateAsync(Guid runId, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();

        var mode = options.Mode;

        var run = await db.AutopilotRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, ct)
            .ConfigureAwait(false);
        if (run is null)
        {
            LogRunMissing(runId);

            return Faulted(startedAt, RunMissingErrorCode, $"Autopilot run {runId} does not exist in this scope.");
        }

        if (run.EpisodeId is not { } episodeId)
        {
            LogEpisodeMissing(runId);

            return Faulted(
                startedAt,
                EpisodeMissingErrorCode,
                $"Autopilot run {runId} has no episode, so it has no video to gate.");
        }

        var episode = await db.Episodes
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == episodeId, ct)
            .ConfigureAwait(false);
        if (episode is null)
        {
            LogEpisodeMissing(runId);

            return Faulted(
                startedAt,
                EpisodeMissingErrorCode,
                $"The episode {episodeId} of autopilot run {runId} does not exist in this scope.");
        }

        var renderJob = await db.RenderJobs
            .AsNoTracking()
            .Where(job => job.EpisodeId == episodeId && job.Status == RenderJobStatus.Succeeded)
            .OrderByDescending(job => job.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (renderJob is null)
        {
            LogRenderMissing(runId, episodeId);

            return Faulted(
                startedAt,
                RenderMissingErrorCode,
                $"The episode {episodeId} of autopilot run {runId} has no succeeded render to gate.");
        }

        var series = await db.Series
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == episode.SeriesId, ct)
            .ConfigureAwait(false);
        if (series is null)
        {
            LogSeriesMissing(runId, episodeId, episode.SeriesId);

            return Faulted(
                startedAt,
                SeriesMissingErrorCode,
                $"The series {episode.SeriesId} of episode {episodeId} does not exist in this scope.");
        }

        var asset = episode.FinalVideoAssetId is { } assetId
            ? await db.Assets
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == assetId, ct)
                .ConfigureAwait(false)
            : null;

        // Check 8 (plan §3.2): the image moderation rows of *this* render. `moderation_events` carries no
        // render-job column (00 §6), so 03.4.2-T1's own discriminator is used: episode + source `openai_image` +
        // stage `render_image`, created between this job's submission and its finish (03.4.2-T1 plan: "created
        // after the render job's started_at"; one render job per episode is active at a time, 09.6.1). An
        // unreadable window falls back to the wider count, which can only refuse an approvable video — never let
        // an earlier render's row pass one.
        var moderationRows = db.ModerationEvents
            .AsNoTracking()
            .Where(candidate => candidate.EpisodeId == episodeId
                && candidate.Source == ModerationSource.OpenaiImage
                && candidate.Stage == ImageModerationStage);

        var windowStart = renderJob.SubmittedAt ?? renderJob.StartedAt;
        if (windowStart is { } notEarlierThan)
        {
            moderationRows = moderationRows.Where(candidate => candidate.CreatedAt >= notEarlierThan);
        }

        if (renderJob.FinishedAt is { } notLaterThan)
        {
            moderationRows = moderationRows.Where(candidate => candidate.CreatedAt <= notLaterThan);
        }

        var imageModerationEvents = await moderationRows
            .CountAsync(ct)
            .ConfigureAwait(false);

        var thresholdRef = factory.ReadGateThresholdsRef(series.AutopilotSettings);
        var profileName = options.ResolveName(thresholdRef);
        if (thresholdRef is not null && !string.Equals(profileName, thresholdRef, StringComparison.Ordinal))
        {
            // An unknown ref never disables the gate: it resolves to `default` and the run log shows why
            // (plan §10 decision 6).
            LogProfileUnknown(runId, thresholdRef, profileName);
        }

        var profile = options.Resolve(thresholdRef);
        var input = factory.Build(renderJob, asset, series.Aspect, imageModerationEvents);
        var result = VideoGateRules.Evaluate(input, profile);

        // The approve path needs the state the command itself requires: only `video_review` accepts it. An episode
        // that already reached `ready` is this render's own approval replayed, and every other state means the run
        // and the episode disagree — the gate records no verdict there, because a stored pass would claim an
        // approval that cannot happen.
        if (result.Pass && mode == GateMode.Live && episode.Status is not (EpisodeStatus.VideoReview or EpisodeStatus.Ready))
        {
            LogStateNotApprovable(runId, episodeId, episode.Status, renderJob.Id);

            return Faulted(
                startedAt,
                EpisodeStateErrorCode,
                $"Episode {episodeId} is {episode.Status} and only a video under review can be approved.");
        }

        var notifyReason = StopReasonFor(result, mode);
        var recorded = await recorder
            .RecordVideoAsync(runId, result, mode, profileName, notifyReason, ct)
            .ConfigureAwait(false);
        if (recorded.IsError)
        {
            LogRecordFailed(runId, renderJob.Id, recorded.FirstError.Code);

            return WithMetric(recorded.Errors, startedAt, MetricError, result.Failures);
        }

        if (!result.Pass)
        {
            LogFailedChecks(runId, renderJob.Id, result.Failures.Count);

            return WithMetric(
                Stopped(result, GateStopReason.FailedChecks),
                startedAt,
                MetricFail,
                result.Failures);
        }

        if (mode == GateMode.DryRun)
        {
            LogDryRun(runId, renderJob.Id);

            return WithMetric(
                Stopped(result, GateStopReason.DryRun),
                startedAt,
                MetricDryRun,
                result.Failures);
        }

        if (episode.Status == EpisodeStatus.Ready)
        {
            LogReplay(runId, episodeId, renderJob.Id);

            return WithMetric(
                Passed(result),
                startedAt,
                MetricPass,
                result.Failures);
        }

        var approved = await approvals
            .ApproveVideoAsync(runId, result, mode, ct)
            .ConfigureAwait(false);
        if (approved.IsError)
        {
            var failureCode = ApprovalFailureCodes.Of(approved.FirstError);

            LogApprovalFailed(runId, renderJob.Id, approved.FirstError.Code, failureCode);

            var failureRecorded = await recorder
                .RecordApprovalFailureAsync(runId, GateName, failureCode, ct)
                .ConfigureAwait(false);
            if (failureRecorded.IsError)
            {
                LogFailureNotRecorded(runId, renderJob.Id, failureRecorded.FirstError.Code);

                return WithMetric(failureRecorded.Errors, startedAt, MetricError, result.Failures);
            }

            return WithMetric(
                Stopped(result, GateStopReason.OwnerApprovalRequired),
                startedAt,
                MetricFail,
                result.Failures);
        }

        if (approved.Value == ApprovalOutcome.Approved)
        {
            LogApproved(runId, renderJob.Id);

            return WithMetric(
                Passed(result),
                startedAt,
                MetricPass,
                result.Failures);
        }

        if (approved.Value == ApprovalOutcome.AlreadyApproved)
        {
            // The command's own replay: the approval — and its audit row — belong to the first call, so this one
            // adds neither (plan §4 UC4).
            LogReplay(runId, episodeId, renderJob.Id);

            return WithMetric(
                Passed(result),
                startedAt,
                MetricPass,
                result.Failures);
        }

        // A live pass cannot come back skipped; reading it as "nothing was approved" is the safe direction.
        LogNotApproved(runId, renderJob.Id);

        return WithMetric(
            Stopped(result, GateStopReason.DryRun),
            startedAt,
            MetricDryRun,
            result.Failures);
    }

    /// <summary>
    /// The reason the owner is told about, or <see langword="null"/> for a live pass that approved (plan §4 UC6). A
    /// failing check is the more specific story for the owner even in dry run, exactly as the true-crime hold
    /// outranks the mode on the story side.
    /// </summary>
    private static GateStopReason? StopReasonFor(VideoGateResult result, GateMode mode) => !result.Pass
        ? GateStopReason.FailedChecks
        : mode == GateMode.DryRun
            ? GateStopReason.DryRun
            : null;

    /// <summary>The outcome of a verdict that passed and was approved (or replayed): it stopped nowhere.</summary>
    private static VideoGateOutcome Passed(VideoGateResult result) => new(result, true, null);

    /// <summary>The outcome of a verdict that stopped short of an approval, for the reason given.</summary>
    private static VideoGateOutcome Stopped(VideoGateResult result, GateStopReason reason) => new(result, false, reason);

    /// <summary>Records the one metric of this call and returns <paramref name="outcome"/>.</summary>
    private ErrorOr<VideoGateOutcome> WithMetric(
        ErrorOr<VideoGateOutcome> outcome,
        long startedAt,
        string metricResult,
        IReadOnlyList<string> failedCheckIds)
    {
        metrics.RecordGate(GateName, metricResult, Stopwatch.GetElapsedTime(startedAt), failedCheckIds);

        return outcome;
    }

    /// <summary>Records the one metric of an evaluation that has no verdict, and returns the error.</summary>
    private ErrorOr<VideoGateOutcome> Faulted(long startedAt, string code, string description) =>
        WithMetric(Error.Failure(code, description), startedAt, MetricError, []);

    [LoggerMessage(
        EventId = 6480,
        Level = LogLevel.Information,
        Message = "The video gate of autopilot run {RunId} approved render job {RenderJobId} as a system actor.")]
    private partial void LogApproved(Guid runId, Guid renderJobId);

    [LoggerMessage(
        EventId = 6481,
        Level = LogLevel.Information,
        Message = "The video gate of autopilot run {RunId} passed render job {RenderJobId} in dry-run mode; nothing was approved.")]
    private partial void LogDryRun(Guid runId, Guid renderJobId);

    [LoggerMessage(
        EventId = 6482,
        Level = LogLevel.Information,
        Message = "The video gate of autopilot run {RunId} replayed the approval of episode {EpisodeId} for render job {RenderJobId}; no second approval was sent.")]
    private partial void LogReplay(Guid runId, Guid episodeId, Guid renderJobId);

    [LoggerMessage(
        EventId = 6483,
        Level = LogLevel.Warning,
        Message = "The video gate of autopilot run {RunId} failed {FailureCount} checks of render job {RenderJobId}; the episode stays in video review.")]
    private partial void LogFailedChecks(Guid runId, Guid renderJobId, int failureCount);

    [LoggerMessage(
        EventId = 6484,
        Level = LogLevel.Warning,
        Message = "The approval of autopilot run {RunId} for render job {RenderJobId} was refused with {ErrorCode}; recorded as {FailureCode}.")]
    private partial void LogApprovalFailed(Guid runId, Guid renderJobId, string errorCode, string failureCode);

    [LoggerMessage(
        EventId = 6485,
        Level = LogLevel.Error,
        Message = "The approval failure of autopilot run {RunId} for render job {RenderJobId} could not be recorded ({ErrorCode}); the owner was not notified by this call.")]
    private partial void LogFailureNotRecorded(Guid runId, Guid renderJobId, string errorCode);

    [LoggerMessage(
        EventId = 6486,
        Level = LogLevel.Error,
        Message = "The video verdict of autopilot run {RunId} could not be recorded for render job {RenderJobId} ({ErrorCode}).")]
    private partial void LogRecordFailed(Guid runId, Guid renderJobId, string errorCode);

    [LoggerMessage(
        EventId = 6487,
        Level = LogLevel.Warning,
        Message = "The video gate of autopilot run {RunId} was not evaluated: the run does not exist in this scope.")]
    private partial void LogRunMissing(Guid runId);

    [LoggerMessage(
        EventId = 6488,
        Level = LogLevel.Warning,
        Message = "The video gate of autopilot run {RunId} was not evaluated: its episode is missing.")]
    private partial void LogEpisodeMissing(Guid runId);

    [LoggerMessage(
        EventId = 6489,
        Level = LogLevel.Warning,
        Message = "The video gate of autopilot run {RunId} passed render job {RenderJobId} but nothing was approved.")]
    private partial void LogNotApproved(Guid runId, Guid renderJobId);

    [LoggerMessage(
        EventId = 6496,
        Level = LogLevel.Warning,
        Message = "The video gate of autopilot run {RunId} was not evaluated: episode {EpisodeId} has no succeeded render.")]
    private partial void LogRenderMissing(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6497,
        Level = LogLevel.Warning,
        Message = "The video gate of autopilot run {RunId} was not evaluated: series {SeriesId} of episode {EpisodeId} does not exist in this scope.")]
    private partial void LogSeriesMissing(Guid runId, Guid episodeId, Guid seriesId);

    [LoggerMessage(
        EventId = 6498,
        Level = LogLevel.Warning,
        Message = "The video gate of autopilot run {RunId} found episode {EpisodeId} in {Status}; render job {RenderJobId} cannot be approved from there.")]
    private partial void LogStateNotApprovable(Guid runId, Guid episodeId, EpisodeStatus status, Guid renderJobId);

    [LoggerMessage(
        EventId = 6499,
        Level = LogLevel.Warning,
        Message = "Autopilot run {RunId} names unknown gate profile {ProfileRef}; the gate resolves {ProfileName} instead.")]
    private partial void LogProfileUnknown(Guid runId, string profileRef, string profileName);
}
