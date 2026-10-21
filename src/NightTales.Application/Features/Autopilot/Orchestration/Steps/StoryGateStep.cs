using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// <see cref="AutopilotRunState.StoryGate"/>: ask the story gate about the version this run produced and take the
/// one decision the plan §3.5 table gives it (rows <c>story_gate → rendering</c>, <c>→ story_generating</c>,
/// <c>→ gate_failed</c>, <c>→ waiting_owner</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate is the judge.</b> <see cref="IAutoApprovalGate.EvaluateStoryAsync"/> evaluates, records the verdict,
/// and — when it approves — sends the owner's own <c>ApproveStoryCommand</c> under the run owner's system scope, so
/// no route and no policy lives here; it also raises the owner-action notice of a stop. That is why this step never
/// notifies: the same stop would otherwise reach the owner twice (02.8.2's "one stop, one notice", S67-8).
/// </para>
/// <para>
/// <b>Mapping</b> (task file §Failure handling, plan §7.3, hand-over 03.2.2-T2):
/// <list type="bullet">
/// <item><c>AutoApproved</c> on a fiction series → <see cref="AutopilotRunState.Rendering"/>, the gate having
/// approved the story itself. A true-crime series never reaches it: its gate holds by design.</item>
/// <item><c>Hold</c> (the true-crime hold), <c>DryRun</c> (02.8.3-T3's mode), <c>OwnerApprovalRequired</c> (the
/// approve command refused: <c>keywords_flagged</c>, <c>story_attestation_required</c>, an ETag or state
/// conflict — the gate records which) and <c>ModeratorUnavailable</c> (fail closed) → waiting for the owner with
/// <see cref="WaitingFor.Story"/>.</item>
/// <item>A failed check list on attempt 1 → the run's one regeneration (attempt 2, key <c>…:story2</c>); on attempt
/// 2 → <see cref="AutopilotRunOutcome.GateFailed"/>, the <c>gate_failed</c> ending of plan §3.5.</item>
/// <item>An error from the gate is not a verdict: <c>approval_audit_failed</c> and the version/episode conflicts
/// are retried — the retry is the replay that backfills the audit row or the re-evaluation — and any other error
/// fails the slot with its code, because a code path inside the gate is a defect rather than a slow dependency.</item>
/// </list>
/// </para>
/// <para>
/// <b>Containment (S67-9).</b> An episode already approved for the version it currently holds is not evaluated
/// again: the run advances to <see cref="AutopilotRunState.Rendering"/> and no second approval, no second audit row
/// and no second notice is possible.
/// </para>
/// </remarks>
public sealed partial class StoryGateStep(
    IAppDbContext db,
    IAutoApprovalGate gate,
    ILogger<StoryGateStep> logger) : IAutopilotStepHandler
{
    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.StoryGate;

    /// <inheritdoc />
    public async ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;

        if (AutopilotStepSupport.SlotIsMissed(context))
        {
            LogSlotMissed(run.Id, run.SeriesId);

            return AutopilotStepSupport.FailSlotMissed(context);
        }

        if (run.EpisodeId is not { } episodeId)
        {
            return Fail(context, AutopilotStepSupport.EpisodeMissingCode);
        }

        var episode = await db.Episodes
            .FirstOrDefaultAsync(candidate => candidate.Id == episodeId, ct)
            .ConfigureAwait(false);

        if (episode is null)
        {
            return Fail(context, AutopilotStepSupport.EpisodeMissingCode);
        }

        if (IsAlreadyApproved(episode))
        {
            LogAlreadyApproved(run.Id, episodeId);

            return Rendering(context);
        }

        // A run paused mid-generation resumes into this state (Resume derives `story` → StoryGate while no render
        // was requested), so a Draft or still-generating episode has no story to evaluate yet: send it back rather
        // than evaluating a version that does not exist and failing the slot on the evaluator's missing-story error.
        if (episode.Status is EpisodeStatus.Draft or EpisodeStatus.StoryGenerating)
        {
            run.AdvanceTo(AutopilotRunState.StoryGenerating, step: null, context.Now);

            LogNotProducedYet(run.Id, episodeId);

            return new StepResult(StepResultKind.Advanced, AutopilotStepSupport.StoryInFlightCode);
        }

        var attempt = Math.Max(run.Attempt, 1);
        var evaluated = await gate.EvaluateStoryAsync(run.Id, attempt, ct).ConfigureAwait(false);

        if (evaluated.IsError)
        {
            return GateError(context, evaluated.FirstError.Code);
        }

        var outcome = evaluated.Value;

        if (outcome.AutoApproved && context.Series.Topic.IsFiction())
        {
            LogApproved(run.Id, episodeId, outcome.Result.StoryVersionId);

            return Rendering(context);
        }

        if (NeedsOwner(outcome, context.Series.Topic))
        {
            return WaitForOwner(context, outcome, episodeId);
        }

        return FailedChecks(context, outcome, episodeId);
    }

    /// <summary>S67-9: the version the run would evaluate is already approved, so nothing is evaluated again.</summary>
    private static bool IsAlreadyApproved(Episode episode) =>
        episode.ApprovedStoryVersionId is { } approved
        && episode.CurrentStoryVersionId == approved
        && episode.Status is EpisodeStatus.StoryApproved
            or EpisodeStatus.Rendering
            or EpisodeStatus.VideoReview
            or EpisodeStatus.Ready
            or EpisodeStatus.Scheduled
            or EpisodeStatus.Published;

    /// <summary>Why the gate stopped without approving, when the answer is "ask the owner".</summary>
    private static bool NeedsOwner(StoryGateOutcome outcome, Topic topic) =>
        outcome.Reason is GateStopReason.Hold
            or GateStopReason.DryRun
            or GateStopReason.OwnerApprovalRequired
            or GateStopReason.ModeratorUnavailable
        || (outcome.AutoApproved && !topic.IsFiction());

    /// <summary>A stop the owner must resolve: waiting for the story, with the notice the gate already raised.</summary>
    private StepResult WaitForOwner(AutopilotStepContext context, StoryGateOutcome outcome, Guid episodeId)
    {
        var run = context.Run;

        run.SetWaitingFor(WaitingFor.Story);
        run.AdvanceTo(AutopilotRunState.WaitingOwner, AutopilotRunStep.StoryGate, context.Now);

        // The reason name is read once: the log call stays cheap even when its level is off (CA1873).
        var reason = outcome.Reason?.ToString() ?? "none";
        LogWaitingOwner(run.Id, episodeId, reason);

        return StepResult.Stopped(AutopilotStepSupport.WaitingOwnerCode);
    }

    /// <summary>The checks did not pass: one regeneration on attempt 1, the <c>gate_failed</c> ending on attempt 2.</summary>
    private StepResult FailedChecks(AutopilotStepContext context, StoryGateOutcome outcome, Guid episodeId)
    {
        var run = context.Run;

        if (run.Attempt < AutopilotRun.MaxStoryAttempts)
        {
            run.ConsumeStoryAttempt();
            run.AdvanceTo(AutopilotRunState.StoryGenerating, step: null, context.Now);

            LogRegenerating(run.Id, episodeId, outcome.Result.StoryVersionId, outcome.Result.Failures.Count);

            return new StepResult(StepResultKind.Advanced, AutopilotStepSupport.StoryRegeneratedCode);
        }

        run.Finish(AutopilotRunOutcome.GateFailed, context.Now);

        LogGateFailed(run.Id, episodeId, outcome.Result.StoryVersionId, outcome.Result.Failures.Count);

        return StepResult.Stopped(AutopilotStepSupport.GateFailedCode);
    }

    /// <summary>An error from the gate: the seam's contract is "an error is not a verdict", and the caller retries.</summary>
    private StepResult GateError(AutopilotStepContext context, string code)
    {
        // `approval_audit_failed`: the approval is committed and only its audit row is missing, so the next tick's
        // replay is what backfills it (02.8.2-T1 R2). A read or corpus failure is equally transient, and a slot that
        // cannot be *evaluated* must not be burned: the missed-slot cutoff still ends it if the fault lasts.
        LogGateRetried(context.Run.Id, code);

        return StepResult.Retry(code);
    }

    /// <summary>The story passed: the render step owns the run from here.</summary>
    private static StepResult Rendering(AutopilotStepContext context)
    {
        context.Run.AdvanceTo(AutopilotRunState.Rendering, AutopilotRunStep.StoryGate, context.Now);

        return new StepResult(StepResultKind.Advanced, AutopilotStepSupport.StoryApprovedCode);
    }

    /// <summary>Fails the run with <paramref name="code"/>; the gate could not be asked a question it can answer.</summary>
    private StepResult Fail(AutopilotStepContext context, string code)
    {
        context.Run.Fail(code, context.Now);

        LogFailed(context.Run.Id, code);

        return StepResult.Stopped(code);
    }

    [LoggerMessage(
        EventId = 6590,
        Level = LogLevel.Warning,
        Message = "autopilot_story_gate_slot_missed runId={RunId} seriesId={SeriesId}")]
    private partial void LogSlotMissed(Guid runId, Guid seriesId);

    [LoggerMessage(
        EventId = 6591,
        Level = LogLevel.Information,
        Message = "autopilot_story_gate_approved runId={RunId} episodeId={EpisodeId} storyVersionId={StoryVersionId}")]
    private partial void LogApproved(Guid runId, Guid episodeId, Guid storyVersionId);

    [LoggerMessage(
        EventId = 6592,
        Level = LogLevel.Information,
        Message = "autopilot_story_gate_already_approved runId={RunId} episodeId={EpisodeId}")]
    private partial void LogAlreadyApproved(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6593,
        Level = LogLevel.Information,
        Message = "autopilot_story_gate_waiting_owner runId={RunId} episodeId={EpisodeId} reason={Reason}")]
    private partial void LogWaitingOwner(Guid runId, Guid episodeId, string reason);

    [LoggerMessage(
        EventId = 6594,
        Level = LogLevel.Information,
        Message = "autopilot_story_gate_regenerating runId={RunId} episodeId={EpisodeId} storyVersionId={StoryVersionId} failures={FailureCount}")]
    private partial void LogRegenerating(Guid runId, Guid episodeId, Guid storyVersionId, int failureCount);

    [LoggerMessage(
        EventId = 6595,
        Level = LogLevel.Warning,
        Message = "autopilot_story_gate_failed runId={RunId} episodeId={EpisodeId} storyVersionId={StoryVersionId} failures={FailureCount}")]
    private partial void LogGateFailed(Guid runId, Guid episodeId, Guid storyVersionId, int failureCount);

    [LoggerMessage(
        EventId = 6596,
        Level = LogLevel.Information,
        Message = "autopilot_story_gate_retried runId={RunId} code={Code}")]
    private partial void LogGateRetried(Guid runId, string code);

    [LoggerMessage(
        EventId = 6597,
        Level = LogLevel.Warning,
        Message = "autopilot_story_gate_error runId={RunId} code={Code}")]
    private partial void LogFailed(Guid runId, string code);

    [LoggerMessage(
        EventId = 6599,
        Level = LogLevel.Information,
        Message = "autopilot_story_gate_not_produced_yet runId={RunId} episodeId={EpisodeId}")]
    private partial void LogNotProducedYet(Guid runId, Guid episodeId);
}
