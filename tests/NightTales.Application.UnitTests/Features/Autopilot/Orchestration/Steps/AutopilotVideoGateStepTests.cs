using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Application.Features.Publishing.Commands.CreatePublication;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Gates;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The <c>video_gate</c> step (02.8.3 plan §3.5 rows <c>video_gate → publication_created</c>,
/// <c>video_gate → waiting_owner</c> and <c>video_gate → gate_failed</c>, §7.2, S71-19): one evaluation per run, the
/// owner hold for every stop that is not a failed check, and the replay of a verdict the gate already committed
/// (S71 SEC-3).
/// </summary>
/// <remarks>
/// The gate is scripted, because the checks themselves are 02.8.2's and are covered by the evaluator suites; the
/// creator is real, so the pass case proves the step hands the decision to the one place that creates a publication.
/// Graph: the worktree is not indexed.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AutopilotVideoGateStepTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = AutopilotStepContexts.DefaultNow;

    /// <summary>Failed checks end the run <c>gate_failed</c>: §3.5 has no second cut, so there is nothing to hold for.</summary>
    [Fact]
    public async Task VideoGate_FailedChecks_EndsTheRunGateFailed()
    {
        var (run, step, gate, sender) = await Arrange(Outcome(approved: false, GateStopReason.FailedChecks));

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.GateFailed);
        run.Run.State.ShouldBe(AutopilotRunState.Failed);
        run.Run.Outcome.ShouldBe(AutopilotRunOutcome.GateFailed);
        gate.Calls.ShouldBe(1);
        sender.Sent.ShouldBeEmpty("a failed cut is never published");
    }

    /// <summary>A dry run approves nothing: the verdict is recorded by the gate and the run waits for its owner.</summary>
    [Fact]
    public async Task VideoGate_DryRun_HoldsForTheOwner()
    {
        var (run, step, gate, sender) = await Arrange(Outcome(approved: false, GateStopReason.DryRun));

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(OwnerActionReasons.DryRun);
        run.Run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        run.Run.WaitingFor.ShouldBe(WaitingFor.Video);
        gate.Calls.ShouldBe(1);
        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>A moderator outage fails the check closed, but it is the owner's decision, not the run's failure.</summary>
    [Fact]
    public async Task VideoGate_ModeratorUnavailable_HoldsForTheOwner()
    {
        var (run, step, _, sender) = await Arrange(Outcome(approved: false, GateStopReason.ModeratorUnavailable));

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(OwnerActionReasons.ModeratorUnavailable);
        run.Run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        run.Run.WaitingFor.ShouldBe(WaitingFor.Video);
        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>An evaluation that could not finish is retried on the next tick and moves nothing.</summary>
    [Fact]
    public async Task VideoGate_EvaluationError_Retries()
    {
        var (run, step, gate, sender) = await Arrange(Error.Failure("moderation_unavailable", "the moderator timed out"));

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Retry);
        result.Code.ShouldBe("moderation_unavailable");
        run.Run.State.ShouldBe(AutopilotRunState.VideoGate);
        gate.Calls.ShouldBe(1);
        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>
    /// A live pass whose approval did not land on the episode (still in review) keeps the run waiting for the owner
    /// instead of creating a publication for a cut nobody approved (S71 SEC-3).
    /// </summary>
    [Fact]
    public async Task VideoGate_LivePassWithoutAnApprovedCut_HoldsForTheOwner()
    {
        var (run, step, gate, sender) = await Arrange(Outcome(approved: true, reason: null));

        // Inside the create window (publication_create_at = 2026-10-20T18:00Z for the tests' slot and settings):
        // before it the creator answers WaitUntil and the step would stop one branch earlier.
        var inWindow = AutopilotStepContexts.DefaultSlotAt.AddHours(-25);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, inWindow), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(OwnerActionReasons.OwnerApprovalRequired);
        run.Run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        run.Run.WaitingFor.ShouldBe(WaitingFor.Video);
        gate.Calls.ShouldBe(1);
        sender.SentAny<CreatePublicationCommand>()
            .ShouldBeFalse("only an episode in `ready` may be published");
    }

    /// <summary>
    /// A verdict the gate already committed (its notice went out) is replayed, not evaluated again: the second
    /// evaluation would approve and notify twice (S71 SEC-3, CR-5).
    /// </summary>
    [Fact]
    public async Task VideoGate_StoredFailedVerdict_IsReplayedWithoutEvaluatingAgain()
    {
        var (run, step, gate, sender) = await Arrange(Outcome(approved: true, reason: null));
        run.Run.RecordGateResults(StoredGates.Video(passed: false, GateMode.Live)).IsError.ShouldBeFalse();

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.GateFailed);
        run.Run.State.ShouldBe(AutopilotRunState.Failed);
        gate.Calls.ShouldBe(0, "the stored verdict is the gate's own write; re-evaluating would notify the owner twice");
        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>A stored pass under the dry-run mode approves nothing, so the replay holds the run for its owner.</summary>
    [Fact]
    public async Task VideoGate_StoredDryRunPass_HoldsForTheOwnerWithoutEvaluatingAgain()
    {
        var (run, step, gate, _) = await Arrange(Outcome(approved: true, reason: null));
        run.Run.RecordGateResults(StoredGates.Video(passed: true, GateMode.DryRun)).IsError.ShouldBeFalse();

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Code.ShouldBe(OwnerActionReasons.DryRun);
        run.Run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        gate.Calls.ShouldBe(0);
    }

    /// <summary>The missed slot outranks the gate: nothing is evaluated and the run ends <c>slot_missed</c>.</summary>
    [Fact]
    public async Task VideoGate_MissedSlot_FailsWithoutEvaluating()
    {
        var (run, step, gate, sender) = await Arrange(Outcome(approved: true, reason: null));

        // upload_at − MinVetoWindowHours for the tests' slot and options: 20:00Z − 2 h − 2 h = 16:00Z.
        var atCutoff = AutopilotStepContexts.DefaultSlotAt.AddHours(-4);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, atCutoff), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.SlotMissed);
        run.Run.State.ShouldBe(AutopilotRunState.Failed);
        gate.Calls.ShouldBe(0);
        sender.Sent.ShouldBeEmpty();
    }

    private static ErrorOr<VideoGateOutcome> Outcome(bool approved, GateStopReason? reason) =>
        new VideoGateOutcome(
            new VideoGateResult(
                Pass: reason is null or GateStopReason.DryRun,
                RenderJobId: Guid.CreateVersion7(),
                AssetId: Guid.CreateVersion7(),
                Checks: [],
                Failures: []),
            AutoApproved: approved,
            Reason: reason);

    private async Task<(RunAndSeries Run, VideoGateStep Step, FakeVideoGate Gate, RecordingSender Sender)> Arrange(
        ErrorOr<VideoGateOutcome> verdict)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor)
            .CreateAsync(user.Id, autopilotEnabled: true, ct: Ct);

        var run = AutopilotRuns.WalkTo(series, AutopilotRunState.VideoGate, Now);
        var reader = new FakeStepReader
        {
            Episode = new AutopilotRenderEpisode(
                AutopilotRuns.EpisodeId,
                EpisodeStatus.VideoReview,
                Version: 3,
                Title: "an episode",
                Brief: "a brief"),
        };

        var gate = new FakeVideoGate { Verdict = verdict };
        var sender = new RecordingSender();
        var creator = new AutopilotPublicationCreator(sender, reader, new FakeChannelAuditPolicy(preAudit: false));

        return (
            new RunAndSeries(run, series),
            new VideoGateStep(gate, creator, NullLogger<VideoGateStep>.Instance),
            gate,
            sender);
    }

    private sealed record RunAndSeries(AutopilotRun Run, SeriesAggregate Series);
}
