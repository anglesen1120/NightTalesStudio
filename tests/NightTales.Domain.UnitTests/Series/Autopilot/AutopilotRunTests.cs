using NightTales.Domain.Series.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Autopilot;

/// <summary>
/// The behaviour of the <c>autopilot_runs</c> aggregate (02.8.3 plan §3.3–§3.5): the planned shape, the three
/// counters, the pause/resume pair with its derived previous state, and the two terminal entry points.
/// </summary>
/// <remarks>
/// Pure unit tests: no clock, no database, no context. The arrangement of a run inside a state lives in
/// <see cref="AutopilotRunScenario"/> (declared beside the state-machine tests), which walks the machine the way the
/// orchestrator does.
/// </remarks>
public sealed class AutopilotRunTests
{
    /// <summary>
    /// A run paused in <c>before</c> resumes to <c>after</c>: the derivation of plan §3.5's row
    /// <c>paused → previous state</c> over the evidence the row carries (decision 13).
    /// </summary>
    public static TheoryData<AutopilotRunState, AutopilotRunState> ResumeTable => new()
    {
        { AutopilotRunState.Planned, AutopilotRunState.Planned },
        { AutopilotRunState.AwaitingAuthorization, AutopilotRunState.Planned },
        { AutopilotRunState.IdeaPicked, AutopilotRunState.IdeaPicked },
        { AutopilotRunState.Researching, AutopilotRunState.Researching },
        { AutopilotRunState.StoryGenerating, AutopilotRunState.StoryGenerating },
        { AutopilotRunState.StoryGate, AutopilotRunState.StoryGate },
        { AutopilotRunState.WaitingOwner, AutopilotRunState.WaitingOwner },
        { AutopilotRunState.Rendering, AutopilotRunState.Rendering },
        { AutopilotRunState.VideoGate, AutopilotRunState.VideoGate },
        { AutopilotRunState.PublicationCreated, AutopilotRunState.PublicationCreated },
        { AutopilotRunState.VetoWindow, AutopilotRunState.PublicationCreated },
        { AutopilotRunState.Scheduled, AutopilotRunState.Scheduled },
    };

    [Fact]
    public void AutopilotRun_Plan_StartsPlannedAndRecordsTheSlot()
    {
        var run = AutopilotRun.Plan(
            AutopilotRunScenario.UserId,
            AutopilotRunScenario.SeriesId,
            AutopilotRunScenario.SlotAt,
            AutopilotRunScenario.Now,
            AutopilotRunScenario.AuthorizationId,
            AutopilotRunScenario.Now);

        run.Id.ShouldNotBe(Guid.Empty);
        run.UserId.ShouldBe(AutopilotRunScenario.UserId);
        run.SeriesId.ShouldBe(AutopilotRunScenario.SeriesId);
        run.SlotAt.ShouldBe(AutopilotRunScenario.SlotAt);
        run.StartAt.ShouldBe(AutopilotRunScenario.Now);
        run.State.ShouldBe(AutopilotRunState.Planned);
        run.AuthorizationId.ShouldBe(AutopilotRunScenario.AuthorizationId);
        run.Step.ShouldBeNull("no step has completed yet");
        run.Outcome.ShouldBeNull("a planned run has not stopped");
        run.PauseReason.ShouldBeNull();
        run.WaitingFor.ShouldBeNull();
        run.Attempt.ShouldBe(0);
        run.RenderAttempts.ShouldBe(0);
        run.FinishedAt.ShouldBeNull();
        run.StartedAt.ShouldBe(AutopilotRunScenario.Now);
        run.LastTransitionAt.ShouldBe(AutopilotRunScenario.Now);
        run.CreatedAt.ShouldBe(AutopilotRunScenario.Now);
        run.UpdatedAt.ShouldBe(AutopilotRunScenario.Now);
        run.DomainEvents.ShouldBeEmpty("planning raises no event of its own (plan §3.5)");
    }

    [Fact]
    public void AutopilotRun_Plan_WithoutOwnerOrSeries_Throws()
    {
        Should.Throw<ArgumentException>(() => AutopilotRun.Plan(
            Guid.Empty, AutopilotRunScenario.SeriesId, AutopilotRunScenario.SlotAt, AutopilotRunScenario.Now,
            authorizationId: null, now: AutopilotRunScenario.Now));

        Should.Throw<ArgumentException>(() => AutopilotRun.Plan(
            AutopilotRunScenario.UserId, Guid.Empty, AutopilotRunScenario.SlotAt, AutopilotRunScenario.Now,
            authorizationId: null, now: AutopilotRunScenario.Now));
    }

    [Fact]
    public void AutopilotRun_AdvanceTo_BlockedThenContinues_ClearsTheOutcome()
    {
        var run = AutopilotRunScenario.Planned();

        run.AdvanceTo(
            AutopilotRunState.AwaitingAuthorization,
            AutopilotRunStep.Authorization,
            AutopilotRunScenario.Now);

        run.State.ShouldBe(AutopilotRunState.AwaitingAuthorization);
        run.Step.ShouldBe(AutopilotRunStep.Authorization);
        run.Outcome.ShouldBe(AutopilotRunOutcome.AwaitingAuthorization, "the run stopped for an authorization");
        run.FinishedAt.ShouldBeNull("a blocked run is not done");
        run.LastTransitionAt.ShouldBe(AutopilotRunScenario.Now);

        // `AutopilotAuthorizationAccepted`: the run continues, so the outcome is cleared, and a re-accepted
        // authorization is snapshotted onto the row that relied on it.
        var reaccepted = Guid.CreateVersion7();
        var later = AutopilotRunScenario.Now.AddMinutes(5);
        run.AdvanceTo(AutopilotRunState.Planned, step: null, later, authorizationId: reaccepted);

        run.State.ShouldBe(AutopilotRunState.Planned);
        run.Outcome.ShouldBeNull();
        run.FinishedAt.ShouldBeNull();
        run.AuthorizationId.ShouldBe(reaccepted);
        run.Step.ShouldBe(AutopilotRunStep.Authorization, "a null step keeps the last completed one");
        run.LastTransitionAt.ShouldBe(later);
        run.UpdatedAt.ShouldBe(later);
    }

    [Fact]
    public void AutopilotRun_AdvanceTo_UnknownStep_Throws()
    {
        var run = AutopilotRunScenario.Planned();

        Should.Throw<ArgumentException>(() => run.AdvanceTo(
            AutopilotRunState.IdeaPicked,
            "story_gate:2",
            AutopilotRunScenario.Now));

        run.State.ShouldBe(AutopilotRunState.Planned, "the refused move wrote nothing");
    }

    [Fact]
    public void AutopilotRun_AdvanceTo_WaitingOwner_NeedsAReasonAndClearsItOnLeaving()
    {
        var run = AutopilotRunScenario.IdeaPicked();

        Should.Throw<InvalidOperationException>(() => run.AdvanceTo(
            AutopilotRunState.WaitingOwner, step: null, AutopilotRunScenario.Now));

        run.SetWaitingFor(WaitingFor.Sources);
        run.AdvanceTo(AutopilotRunState.WaitingOwner, AutopilotRunStep.SourcesRequired, AutopilotRunScenario.Now);

        run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        run.WaitingFor.ShouldBe(WaitingFor.Sources);
        run.Outcome.ShouldBe(AutopilotRunOutcome.WaitingOwner);
        run.FinishedAt.ShouldBeNull();

        // `FactSheetApproved`/`StoryApproved`: the owner answered, so the column is cleared again.
        run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.SourcesRequired, AutopilotRunScenario.Now);
        run.WaitingFor.ShouldBeNull();
        run.Outcome.ShouldBeNull("the run continues");
    }

    [Fact]
    public void AutopilotRun_ConsumeStoryAttempt_ThirdAttempt_Throws()
    {
        var run = AutopilotRunScenario.StoryGenerating();

        run.Attempt.ShouldBe(1, "the first generation is the first attempt");

        // The one regenerate of plan §3.5: `story_gate → story_generating` with `attempt < 2`.
        run.AdvanceTo(AutopilotRunState.StoryGate, AutopilotRunStep.Story, AutopilotRunScenario.Now);
        run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.StoryGate, AutopilotRunScenario.Now);
        run.ConsumeStoryAttempt();

        run.Attempt.ShouldBe(2);
        run.State.ShouldBe(AutopilotRunState.StoryGenerating);

        Should.Throw<InvalidOperationException>(() => run.ConsumeStoryAttempt());
        run.Attempt.ShouldBe(AutopilotRun.MaxStoryAttempts, "a refused third attempt spends nothing");
    }

    [Fact]
    public void AutopilotRun_ConsumeRenderAttempt_SecondAttempt_Throws()
    {
        var run = AutopilotRunScenario.Rendering();

        run.RenderAttempts.ShouldBe(1);

        Should.Throw<InvalidOperationException>(() => run.ConsumeRenderAttempt());
        run.RenderAttempts.ShouldBe(AutopilotRun.MaxRenderAttempts);

        // A run that has not rendered yet may spend the one render exactly once.
        var fresh = AutopilotRunScenario.StoryGate();
        fresh.ConsumeRenderAttempt();
        fresh.RenderAttempts.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(ResumeTable))]
    public void AutopilotRun_PauseThenResume_RestoresPreviousStateAndClearsOutcome(
        AutopilotRunState before,
        AutopilotRunState after)
    {
        var run = AutopilotRunScenario.RunAt(before);
        var pausedAt = AutopilotRunScenario.Now.AddMinutes(30);

        run.Pause(AutopilotPauseReason.GateFailedStreak, pausedAt);

        run.State.ShouldBe(AutopilotRunState.Paused);
        run.Outcome.ShouldBe(AutopilotRunOutcome.Paused);
        run.PauseReason.ShouldBe(AutopilotPauseReason.GateFailedStreak);
        run.FinishedAt.ShouldBeNull("a pause never finishes the run");
        run.LastTransitionAt.ShouldBe(pausedAt);

        var resumedAt = pausedAt.AddMinutes(30);
        run.Resume(resumedAt);

        run.State.ShouldBe(after);
        run.Outcome.ShouldBe(
            AutopilotRunStateMachine.OutcomeFor(after),
            "a resumed run clears its outcome unless the state it returns to is itself a stop");
        run.PauseReason.ShouldBeNull("the reason belongs to the pause that ended");
        run.FinishedAt.ShouldBeNull();
        run.LastTransitionAt.ShouldBe(resumedAt);
    }

    [Fact]
    public void AutopilotRun_PauseThenResume_KeepsTheOwnerAskAndTheStep()
    {
        var run = AutopilotRunScenario.RunAt(AutopilotRunState.WaitingOwner);
        var step = run.Step;

        run.Pause(AutopilotPauseReason.ChannelNotActive, AutopilotRunScenario.Now.AddMinutes(1));
        run.Resume(AutopilotRunScenario.Now.AddMinutes(2));

        run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        run.WaitingFor.ShouldBe(WaitingFor.Story, "the owner's ask survived the pause");
        run.Step.ShouldBe(step);
        run.Outcome.ShouldBe(AutopilotRunOutcome.WaitingOwner);
    }

    [Fact]
    public void AutopilotRun_PauseThenResume_AfterTheF33Answer_ReturnsToTheStoryGeneration()
    {
        // The F33 branch: the run waited for the owner's links, the owner pasted them, and the row continued
        // with `sources_required` as its last completed step and no `waiting_for` left.
        var run = AutopilotRunScenario.IdeaPicked();
        run.SetWaitingFor(WaitingFor.Sources);
        run.AdvanceTo(AutopilotRunState.WaitingOwner, AutopilotRunStep.SourcesRequired, AutopilotRunScenario.Now);
        run.AdvanceTo(
            AutopilotRunState.StoryGenerating, AutopilotRunStep.SourcesRequired, AutopilotRunScenario.Now);
        run.WaitingFor.ShouldBeNull("the owner already answered");

        run.Pause(AutopilotPauseReason.ChannelNotActive, AutopilotRunScenario.Now.AddMinutes(1));
        run.Resume(AutopilotRunScenario.Now.AddMinutes(2));

        run.State.ShouldBe(
            AutopilotRunState.StoryGenerating,
            "a step of `sources_required` without a pending ask is past the wait, so resume must not replay it");
        run.Step.ShouldBe(AutopilotRunStep.SourcesRequired);
        run.WaitingFor.ShouldBeNull();
        run.Outcome.ShouldBeNull();
    }

    [Fact]
    public void AutopilotRun_Fail_FromAPausedRun_EndsFailedAndClearsTheReason()
    {
        // `any non-terminal → failed` includes a held row: a pause must never trap a run that has to give up.
        var run = AutopilotRunScenario.RunAt(AutopilotRunState.WaitingOwner);
        run.Pause(AutopilotPauseReason.ChannelNotActive, AutopilotRunScenario.Now.AddMinutes(1));

        var failedAt = AutopilotRunScenario.Now.AddHours(49);
        run.Fail("owner_approval_expired", failedAt);

        run.State.ShouldBe(AutopilotRunState.Failed);
        run.Outcome.ShouldBe(AutopilotRunOutcome.Failed);
        run.PauseReason.ShouldBeNull("the pause ended with the failure");
        run.WaitingFor.ShouldBeNull("a failed run waits for nothing");
        run.FinishedAt.ShouldBe(failedAt);

        // The same row cannot also be resumed back into the machine afterwards.
        Should.Throw<InvalidOperationException>(() => run.Resume(failedAt.AddMinutes(1)));
    }

    [Fact]
    public void AutopilotRun_AdvanceTo_Rendering_RefusesTheRenderStepBeforeItCompleted()
    {
        var run = AutopilotRunScenario.StoryGate();

        // `step` is the last completed step (plan §3.3), so the dispatch may not claim the render step: a pause
        // during the render would then resume into `video_gate` with nothing rendered.
        Should.Throw<InvalidOperationException>(
            () => run.AdvanceTo(AutopilotRunState.Rendering, AutopilotRunStep.Render, AutopilotRunScenario.Now));
        run.State.ShouldBe(AutopilotRunState.StoryGate, "a refused call leaves the aggregate untouched");

        run.AdvanceTo(AutopilotRunState.Rendering, AutopilotRunStep.StoryGate, AutopilotRunScenario.Now);

        run.State.ShouldBe(AutopilotRunState.Rendering);
        run.Step.ShouldBe(AutopilotRunStep.StoryGate, "the gate stays the last completed step until the render lands");
    }

    [Fact]
    public void AutopilotRun_PauseDuringTheRender_ResumesToRenderingNotVideoGate()
    {
        // The true-crime path: the owner approved the story, the render was dispatched against the new version.
        var run = AutopilotRunScenario.StoryGate();
        run.ConsumeRenderAttempt();
        run.AdvanceTo(AutopilotRunState.Rendering, AutopilotRunStep.StoryGate, AutopilotRunScenario.Now);

        run.Pause(AutopilotPauseReason.SpendCapReached, AutopilotRunScenario.Now.AddMinutes(1));
        run.Resume(AutopilotRunScenario.Now.AddMinutes(2));

        run.State.ShouldBe(
            AutopilotRunState.Rendering,
            "no video finished during the hold, so the run must not resume into the video gate");
        run.Step.ShouldBe(AutopilotRunStep.StoryGate);
        run.RenderAttempts.ShouldBe(AutopilotRun.MaxRenderAttempts, "the dispatched render is not re-attempted");
    }

    /// <summary>
    /// The gate spent the run's one regeneration and a §3.6 trigger paused the run before it was dispatched (S71
    /// CR-10): resuming must go back to <c>story_generating</c>, because resuming into the gate would re-evaluate the
    /// story that just failed and waste the regeneration the run already paid for.
    /// </summary>
    [Fact]
    public void AutopilotRun_PauseAfterTheGateSpentTheRegeneration_ResumesToTheStoryGeneration()
    {
        var run = AutopilotRunScenario.StoryGate();

        // The gate's failed verdict: `story_gate → story_generating` with `step` still `story_gate` (only a dispatch
        // names the new step) and the second attempt spent.
        run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.StoryGate, AutopilotRunScenario.Now);
        run.ConsumeStoryAttempt();
        run.Step.ShouldBe(AutopilotRunStep.StoryGate);

        run.Pause(AutopilotPauseReason.ProviderFundsExhausted, AutopilotRunScenario.Now.AddMinutes(1));
        run.Resume(AutopilotRunScenario.Now.AddMinutes(2));

        run.State.ShouldBe(
            AutopilotRunState.StoryGenerating,
            "step story_gate with both attempts spent is the pending regeneration, not another gate pass");
        run.Step.ShouldBe(AutopilotRunStep.StoryGate);
    }

    /// <summary>
    /// The opposite case: the regeneration was dispatched (step <c>story2</c>) and then landed, so a pause resumes into
    /// the gate — the generation is paid and guarded by its key, the gate is free.
    /// </summary>
    [Fact]
    public void AutopilotRun_PauseAfterTheRegenerationLanded_ResumesToTheGate()
    {
        var run = AutopilotRunScenario.StoryGate();

        run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.StoryGate, AutopilotRunScenario.Now);
        run.ConsumeStoryAttempt();
        run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.Story2, AutopilotRunScenario.Now);
        run.AdvanceTo(AutopilotRunState.StoryGate, AutopilotRunStep.Story2, AutopilotRunScenario.Now);

        run.Pause(AutopilotPauseReason.SpendCapReached, AutopilotRunScenario.Now.AddMinutes(1));
        run.Resume(AutopilotRunScenario.Now.AddMinutes(2));

        run.State.ShouldBe(AutopilotRunState.StoryGate);
        run.Step.ShouldBe(AutopilotRunStep.Story2);
    }

    [Fact]
    public void AutopilotRun_PauseTwice_ReplacesTheReasonAndResumeWhenNotPaused_Throws()
    {
        var run = AutopilotRunScenario.Planned();

        Should.Throw<InvalidOperationException>(() => run.Resume(AutopilotRunScenario.Now));

        run.Pause(AutopilotPauseReason.SpendCapReached, AutopilotRunScenario.Now);
        run.Pause(AutopilotPauseReason.OwnerPaused, AutopilotRunScenario.Now.AddMinutes(1));

        run.PauseReason.ShouldBe(AutopilotPauseReason.OwnerPaused, "the later trigger is the one the owner is told about");
        run.State.ShouldBe(AutopilotRunState.Paused);
        run.Outcome.ShouldBe(AutopilotRunOutcome.Paused);

        // A terminal run cannot be held: only the owner resumes a pause, and a finished run has nothing to resume.
        var published = AutopilotRunScenario.RunAt(AutopilotRunState.Published);
        Should.Throw<InvalidOperationException>(
            () => published.Pause(AutopilotPauseReason.OwnerPaused, AutopilotRunScenario.Now));
    }

    [Fact]
    public void AutopilotRun_PausedRun_RefusesADirectJumpToAnotherState()
    {
        var run = AutopilotRunScenario.RunAt(AutopilotRunState.Rendering);
        run.Pause(AutopilotPauseReason.VideoRejected, AutopilotRunScenario.Now.AddMinutes(1));

        // The machine admits `paused → <previous>`, and the aggregate pins which one that is.
        Should.Throw<InvalidOperationException>(() => run.AdvanceTo(
            AutopilotRunState.Planned, step: null, AutopilotRunScenario.Now.AddMinutes(2)));

        Should.Throw<InvalidOperationException>(() => run.AdvanceTo(
            AutopilotRunState.StoryGate, step: null, AutopilotRunScenario.Now.AddMinutes(2)));

        run.State.ShouldBe(AutopilotRunState.Paused);
    }

    [Fact]
    public void AutopilotRun_Fail_SlotMissed_RecordsThePauseReasonAndTheTerminalColumns()
    {
        var run = AutopilotRunScenario.RunAt(AutopilotRunState.VideoGate);
        var failedAt = AutopilotRunScenario.Now.AddHours(20);

        run.Fail(AutopilotRun.SlotMissedReason, failedAt);

        run.State.ShouldBe(AutopilotRunState.Failed);
        run.Outcome.ShouldBe(AutopilotRunOutcome.Failed);
        run.PauseReason.ShouldBe(AutopilotPauseReason.SlotMissed, "plan §3.4 and §3.6 row 13 count these misses");
        run.FinishedAt.ShouldBe(failedAt);
        run.LastTransitionAt.ShouldBe(failedAt);
        run.WaitingFor.ShouldBeNull();
    }

    [Fact]
    public void AutopilotRun_Fail_OtherReasons_AreNotStoredAndASecondFailIsANoOp()
    {
        var run = AutopilotRunScenario.StoryGenerating();
        var failedAt = AutopilotRunScenario.Now.AddMinutes(7);

        run.Fail("story_moderated", failedAt);

        run.State.ShouldBe(AutopilotRunState.Failed);
        run.Outcome.ShouldBe(AutopilotRunOutcome.Failed);
        run.PauseReason.ShouldBeNull("plan §3.3 has no column for a failed run's reason (decision 4)");
        run.FinishedAt.ShouldBe(failedAt);

        // A redelivered failure handler answers the same way instead of throwing.
        run.Fail("story_moderated", failedAt.AddMinutes(1));
        run.FinishedAt.ShouldBe(failedAt, "the first failure is the one that finished the run");

        Should.Throw<ArgumentException>(() => run.Fail("  ", failedAt));

        // A run that ended another way refuses to also fail.
        var gateFailed = AutopilotRunScenario.RunAt(AutopilotRunState.GateFailed);
        Should.Throw<InvalidOperationException>(() => gateFailed.Fail("render_failed", failedAt));
    }

    [Fact]
    public void AutopilotRun_Finish_RejectsNonTerminalOutcomesAndIllegalTargets()
    {
        var run = AutopilotRunScenario.RunAt(AutopilotRunState.Scheduled);

        Should.Throw<ArgumentOutOfRangeException>(
            () => run.Finish(AutopilotRunOutcome.WaitingOwner, AutopilotRunScenario.Now));
        Should.Throw<ArgumentOutOfRangeException>(
            () => run.Finish(AutopilotRunOutcome.Paused, AutopilotRunScenario.Now));

        // `scheduled → published` and `scheduled → published_private_locked` exist; nothing else does.
        var rendering = AutopilotRunScenario.RunAt(AutopilotRunState.Rendering);
        Should.Throw<InvalidOperationException>(
            () => rendering.Finish(AutopilotRunOutcome.Published, AutopilotRunScenario.Now));

        run.Finish(AutopilotRunOutcome.PublishedPrivateLocked, AutopilotRunScenario.Now);

        run.State.ShouldBe(AutopilotRunState.PublishedPrivateLocked);
        run.Outcome.ShouldBe(AutopilotRunOutcome.PublishedPrivateLocked);
        run.FinishedAt.ShouldBe(AutopilotRunScenario.Now);
    }

    [Fact]
    public void AutopilotRun_Attach_RejectsASecondDifferentRow()
    {
        var run = AutopilotRunScenario.StoryGenerating();
        var episode = run.EpisodeId.GetValueOrDefault();
        episode.ShouldNotBe(Guid.Empty, "the arrangement attaches an episode");

        // The same id twice is the at-least-once delivery of the step, which must stay a no-op.
        run.AttachEpisode(episode);
        run.EpisodeId.ShouldBe(episode);

        Should.Throw<InvalidOperationException>(() => run.AttachEpisode(Guid.CreateVersion7()));
        Should.Throw<InvalidOperationException>(() => run.AttachIdea(Guid.CreateVersion7()));
        Should.Throw<ArgumentException>(() => run.AttachPublication(Guid.Empty));
    }

    [Fact]
    public void AutopilotRun_Regenerate_ReachesTheSecondGateThroughTheLoop()
    {
        var run = AutopilotRunScenario.StoryGate();

        // Gate fails with the one regenerate left.
        run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.StoryGate, AutopilotRunScenario.Now);
        run.ConsumeStoryAttempt();
        run.Attempt.ShouldBe(2);

        run.AdvanceTo(AutopilotRunState.StoryGate, AutopilotRunStep.Story2, AutopilotRunScenario.Now);

        run.State.ShouldBe(AutopilotRunState.StoryGate);
        run.Step.ShouldBe(AutopilotRunStep.Story2);
        run.Outcome.ShouldBeNull("the second gate has not ruled yet");

        // The second gate's failure has no attempt left, so it is terminal.
        run.Finish(AutopilotRunOutcome.GateFailed, AutopilotRunScenario.Now);
        run.State.ShouldBe(AutopilotRunState.GateFailed);
        run.Outcome.ShouldBe(AutopilotRunOutcome.GateFailed);
    }
}
