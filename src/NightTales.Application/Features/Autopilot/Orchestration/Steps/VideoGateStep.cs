using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Gates;

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
/// video half already stored never evaluates, approves or notifies a second time — which also keeps
/// <c>ApproveVideoCommand</c> at one call per run (S67-8/S67-9: no duplicate notice, and an already-approved cut is
/// not approved again). What it does depends on the stored verdict, read as a verdict and not as key presence
/// (S71 SEC-3, CR-5): a stored stop finishes that stop's move — the gate committed the verdict and the notice, only
/// the run's save was lost — and a stored live pass goes on to the create, where the episode's <c>ready</c> state is
/// what proves the approval landed.
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

        var stored = StoredVideoVerdict.Read(run.GateResults);
        if (stored is not { } known)
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
        else if (!known.Passed)
        {
            // The gate committed this verdict — and the notice that goes with it — but the run's own move did not
            // land (a crash or a lost save between the two writes). Re-evaluating would approve and notify a second
            // time, so the tick finishes the move the gate already decided (S71 SEC-3, CR-5).
            var reasonCode = ReasonCodeOf(known.Reason);
            LogReplayedStop(run.Id, reasonCode);

            return StopForOwner(run, known.Reason, now);
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
    /// What the run's stored <c>gates.v1</c> document says about the video gate (S71 SEC-3, CR-5): absent or
    /// unreadable, a stop the gate committed, or a live pass — the only shape that approved the cut.
    /// </summary>
    /// <remarks>
    /// Key presence is not approval: <see cref="GateResultWriter.WriteVideoAsync"/> writes the <c>video</c> block for
    /// every verdict, including a dry run, a failing check list and a refused approval. The two facts that turn a
    /// verdict into an approval are the block's own <c>pass</c> and the document's <c>mode</c>, so a replay reads
    /// both. A live pass still does not prove the approval landed — the episode's <c>ready</c> state is that proof,
    /// and <see cref="AutopilotPublicationCreator.TryCreateAsync"/> is the one place that reads it.
    /// </remarks>
    /// <param name="Passed">Whether the stored verdict is a live pass.</param>
    /// <param name="Reason">
    /// Why the stored verdict stopped, or <see cref="GateStopReason.None"/> exactly when <paramref name="Passed"/>.
    /// </param>
    private readonly record struct StoredVideoVerdict(bool Passed, GateStopReason Reason)
    {
        private const string PassedKeyName = "pass";

        /// <summary>Reads the video half of a stored document.</summary>
        /// <param name="gateResults">The run's <c>gate_results</c>.</param>
        /// <returns>
        /// The verdict, or <see langword="null"/> when the document is missing, unreadable or holds no video block —
        /// all of which mean "the gate has not committed a verdict", so the caller evaluates it.
        /// </returns>
        internal static StoredVideoVerdict? Read(string? gateResults)
        {
            if (string.IsNullOrWhiteSpace(gateResults))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(gateResults);

                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty(GateResultWriter.VideoKeyName, out var video)
                    || video.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (!video.TryGetProperty(PassedKeyName, out var pass) || pass.ValueKind != JsonValueKind.True)
                {
                    return new StoredVideoVerdict(Passed: false, GateStopReason.FailedChecks);
                }

                // A pass in dry-run mode is recorded and approves nothing. A document whose mode is missing or not
                // the live wire value is read the same way: the owner decides, and nothing is published.
                return IsLiveMode(root)
                    ? new StoredVideoVerdict(Passed: true, GateStopReason.None)
                    : new StoredVideoVerdict(Passed: false, GateStopReason.DryRun);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool IsLiveMode(JsonElement root) =>
            root.TryGetProperty(GateResultWriter.ModeKeyName, out var mode)
            && mode.ValueKind == JsonValueKind.String
            && string.Equals(mode.GetString(), GateMode.Live.ToWire(), StringComparison.Ordinal);
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

    [LoggerMessage(
        EventId = 6650,
        Level = LogLevel.Information,
        Message = "Autopilot run {RunId} replayed the video verdict the gate already committed ({ReasonCode}); the run moves instead of evaluating again.")]
    private partial void LogReplayedStop(Guid runId, string reasonCode);
}
