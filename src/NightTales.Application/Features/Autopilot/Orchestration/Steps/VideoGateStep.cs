using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The <c>video_gate</c> step (02.8.3 plan §3.5 rows <c>video_gate → publication_created</c>,
/// <c>video_gate → gate_failed</c> and <c>video_gate → waiting_owner</c>): evaluates the 10 video checks through
/// <see cref="IAutoApprovalGate.EvaluateVideoAsync"/> and, on a pass, hands the approved cut to
/// <see cref="AutopilotPublicationCreator"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate is evaluated once per run.</b> <c>video_gate</c> has no self-transition, so the run cannot record a
/// step name while it waits for the create window to open; the marker is the run's own <c>gates.v1</c> document,
/// which the gate writes before it answers (<see cref="GateResultWriter.VideoKeyName"/>). A tick that finds the
/// video half already stored goes straight to the create decision instead of evaluating, approving and notifying a
/// second time — which also keeps <c>ApproveVideoCommand</c> at one call per run (S67-8/S67-9: no duplicate notice,
/// and an already-approved cut is not approved again).
/// </para>
/// <para>
/// <b>The gate owns the notice.</b> A stop is recorded by <c>GateOutcomeRecorder</c>, which raises
/// <c>autopilot_owner_action_required.v1</c> in the same save as the verdict, so this step only moves the run to
/// <c>waiting_owner</c> with <see cref="WaitingFor.Video"/>; it never raises a notice of its own.
/// </para>
/// <para>
/// <b>No regeneration.</b> The video half has no second attempt (plan §3.5, "the video gate has no regeneration"):
/// failed checks end the run <c>gate_failed</c>, while a dry run, an owner-only mode, a true-crime hold or an
/// unreachable moderator stop it for the owner, who can approve by hand or reopen the story.
/// </para>
/// <para>
/// <b>Errors.</b> A gate that cannot be evaluated returns an <see cref="ErrorOr"/> failure and committed no verdict,
/// so the step is <see cref="StepResultKind.Retry"/>: the next tick evaluates it. A missed slot outranks the gate
/// (<c>now ≥ SlotMissedCutoff</c> ends the run <c>slot_missed</c> before anything is evaluated).
/// </para>
/// </remarks>
public sealed partial class VideoGateStep : IAutopilotStepHandler
{
    private readonly IAutoApprovalGate _gate;
    private readonly AutopilotPublicationCreator _creator;
    private readonly ILogger<VideoGateStep> _logger;

    /// <summary>Builds the step.</summary>
    /// <param name="gate">The video gate the run's finished render is evaluated through.</param>
    /// <param name="creator">The one place an approved run creates its publication.</param>
    /// <param name="logger">The ids-only log of this step.</param>
    public VideoGateStep(
        IAutoApprovalGate gate,
        AutopilotPublicationCreator creator,
        ILogger<VideoGateStep> logger)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(creator);
        ArgumentNullException.ThrowIfNull(logger);

        _gate = gate;
        _creator = creator;
        _logger = logger;
    }

    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.VideoGate;

    /// <inheritdoc />
    public async ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;
        var now = context.Now;

        if (now >= AutopilotTiming.SlotMissedCutoff(context.Timing, context.TimingOptions))
        {
            run.Fail(AutopilotRun.SlotMissedReason, now);
            LogSlotMissed(run.Id);

            return StepResult.Stopped(AutopilotStepCodes.SlotMissed);
        }

        if (!HasVideoVerdict(run.GateResults))
        {
            var verdict = await _gate.EvaluateVideoAsync(run.Id, ct).ConfigureAwait(false);
            if (verdict.IsError)
            {
                LogEvaluationFailed(run.Id, verdict.FirstError.Code);

                return StepResult.Retry(verdict.FirstError.Code);
            }

            var outcome = verdict.Value;
            if (!outcome.AutoApproved)
            {
                return StopForOwner(run, outcome.Reason, now);
            }

            LogApproved(run.Id);
        }

        return await _creator.TryCreateAsync(context, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the run <c>gate_failed</c> on failed checks, and holds it for the owner with
    /// <see cref="WaitingFor.Video"/> on every other stop (the gate's own notice already told the owner why).
    /// </summary>
    private StepResult StopForOwner(AutopilotRun run, GateStopReason? reason, DateTimeOffset now)
    {
        // `null` is a pass with no approval, which cannot reach this branch; treating it as failed checks keeps the
        // fail-closed rule ("no pass on unknown") rather than guessing a hold.
        if (reason is null or GateStopReason.FailedChecks)
        {
            run.Finish(AutopilotRunOutcome.GateFailed, now);
            LogGateFailed(run.Id);

            return StepResult.Stopped(AutopilotStepCodes.GateFailed);
        }

        var code = ReasonCodeOf(reason.Value);

        run.SetWaitingFor(WaitingFor.Video);
        run.AdvanceTo(AutopilotRunState.WaitingOwner, AutopilotRunStep.VideoGate, now);
        LogWaitingForOwner(run.Id, code);

        return StepResult.Stopped(code);
    }

    /// <summary>The owner-action code of a video-gate stop (<c>GateOutcomeRecorder</c>'s own vocabulary).</summary>
    private static string ReasonCodeOf(GateStopReason reason) => reason switch
    {
        GateStopReason.DryRun => OwnerActionReasons.DryRun,
        GateStopReason.Hold => OwnerActionReasons.TrueCrimeHold,
        GateStopReason.ModeratorUnavailable => OwnerActionReasons.ModeratorUnavailable,
        GateStopReason.FailedChecks => OwnerActionReasons.FailedChecks,
        _ => OwnerActionReasons.OwnerApprovalRequired,
    };

    /// <summary>
    /// Whether the run already carries the video half of its <c>gates.v1</c> document, i.e. the gate evaluated this
    /// run's render at least once. An unreadable document counts as absent, so the gate re-evaluates (fail closed).
    /// </summary>
    private static bool HasVideoVerdict(string? gateResults)
    {
        if (string.IsNullOrWhiteSpace(gateResults))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(gateResults);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(GateResultWriter.VideoKeyName, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [LoggerMessage(
        EventId = 6565,
        Level = LogLevel.Warning,
        Message = "The video gate of autopilot run {RunId} could not be evaluated ({ErrorCode}); the step retries.")]
    private partial void LogEvaluationFailed(Guid runId, string errorCode);

    [LoggerMessage(
        EventId = 6566,
        Level = LogLevel.Information,
        Message = "The video gate of autopilot run {RunId} approved its render.")]
    private partial void LogApproved(Guid runId);

    [LoggerMessage(
        EventId = 6567,
        Level = LogLevel.Warning,
        Message = "Autopilot run {RunId} ended gate_failed: its video failed the checks and there is no second attempt.")]
    private partial void LogGateFailed(Guid runId);

    [LoggerMessage(
        EventId = 6568,
        Level = LogLevel.Information,
        Message = "Autopilot run {RunId} waits for the owner on its video ({ReasonCode}).")]
    private partial void LogWaitingForOwner(Guid runId, string reasonCode);

    [LoggerMessage(
        EventId = 6579,
        Level = LogLevel.Warning,
        Message = "Autopilot run {RunId} missed its slot at the video gate; the run ends slot_missed.")]
    private partial void LogSlotMissed(Guid runId);
}
