using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Generation;

/// <summary>
/// The run row's own lifecycle (02.2.2 plan §3.4, F06): stage order, pause, success, failure, cancellation and
/// the scope rule the database also enforces as <c>ck_generation_runs_scope</c>.
/// </summary>
public sealed class GenerationRunTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 3, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void GenerationRun_AdvanceStage_ForwardOnly_RejectsBackwards()
    {
        var run = RunningStoryRun();

        run.AdvanceStage(GenerationStage.Writing, FixedNow).IsError.ShouldBeFalse();
        run.AdvanceStage(GenerationStage.Reviewing, FixedNow).IsError.ShouldBeFalse();

        var backwards = run.AdvanceStage(GenerationStage.Checking, FixedNow);

        backwards.IsError.ShouldBeTrue();
        backwards.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
        run.Stage.ShouldBe(GenerationStage.Reviewing, "a rejected stage change leaves the stage alone");

        run.AdvanceStage(GenerationStage.Queued, FixedNow).IsError.ShouldBeTrue();
        run.AdvanceStage(GenerationStage.Saving, FixedNow).IsError.ShouldBeFalse("skipping revising is a forward move");
    }

    [Fact]
    public void GenerationRun_AdvanceStage_AllowsCheckingAgainAfterRevising()
    {
        var run = RunningStoryRun();

        foreach (var stage in new[]
                 {
                     GenerationStage.Writing,
                     GenerationStage.Checking,
                     GenerationStage.Reviewing,
                     GenerationStage.Revising,
                 })
        {
            run.AdvanceStage(stage, FixedNow).IsError.ShouldBeFalse($"{stage} follows the D21 order");
        }

        // D21 re-entry: the revised draft is checked and reviewed again.
        run.AdvanceStage(GenerationStage.Checking, FixedNow).IsError.ShouldBeFalse();
        run.AdvanceStage(GenerationStage.Reviewing, FixedNow).IsError.ShouldBeFalse();
        run.AdvanceStage(GenerationStage.Scoring, FixedNow).IsError.ShouldBeFalse();
        run.Stage.ShouldBe(GenerationStage.Scoring);
    }

    [Fact]
    public void GenerationRun_AdvanceStage_SameStageTwice_IsASuccessfulNoOp()
    {
        var run = RunningStoryRun();

        run.AdvanceStage(GenerationStage.Checking, FixedNow).IsError.ShouldBeFalse();
        run.AdvanceStage(GenerationStage.Checking, FixedNow.AddSeconds(5)).IsError.ShouldBeFalse();

        run.UpdatedAt.ShouldBe(FixedNow, "the repeat changed nothing (D21 happy path checks twice)");
    }

    [Fact]
    public void GenerationRun_AdvanceStage_WhenNotRunning_ReturnsConflict()
    {
        var queued = NewStoryRun();

        queued.AdvanceStage(GenerationStage.Writing, FixedNow).FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
        queued.Stage.ShouldBe(GenerationStage.Queued);
    }

    [Fact]
    public void GenerationRun_Fail_SetsFinishedAtAndErrorCode()
    {
        var run = RunningStoryRun();
        var later = FixedNow.AddSeconds(42);

        run.Fail("story_generation_failed", "provider_timeout", countsTowardLimit: false, later);

        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe("story_generation_failed");
        run.ErrorDetail.ShouldBe("provider_timeout");
        run.CountsTowardLimit.ShouldBeFalse();
        run.FinishedAt.ShouldBe(later);
        run.StartedAt.ShouldBe(FixedNow);
        run.UpdatedAt.ShouldBe(later);
    }

    [Fact]
    public void GenerationRun_Fail_WithoutErrorCode_Throws()
    {
        Should.Throw<ArgumentException>(() => RunningStoryRun().Fail(" ", null, true, FixedNow));
    }

    [Fact]
    public void GenerationRun_Fail_Twice_IsIdempotent_ButAfterSuccessThrows()
    {
        var run = RunningStoryRun();
        run.Fail("story_moderated", null, countsTowardLimit: true, FixedNow);
        run.Fail("story_generation_failed", null, countsTowardLimit: false, FixedNow.AddMinutes(1));

        run.ErrorCode.ShouldBe("story_moderated", "the first failure stands");
        run.CountsTowardLimit.ShouldBeTrue();

        var succeeded = RunningStoryRun();
        succeeded.Succeed(0.1m, FixedNow);
        Should.Throw<InvalidOperationException>(() => succeeded.Fail("story_generation_failed", null, false, FixedNow));
    }

    [Fact]
    public void GenerationRun_MarkPaused_SetsPauseReasonAndCountsTowardLimitFalse()
    {
        var run = NewStoryRun();

        run.MarkPaused(GenerationPauseReason.SpendCapReached, FixedNow);

        run.Status.ShouldBe(GenerationRunStatus.Queued, "a paused run stays queued (TL T19)");
        run.PauseReason.ShouldBe(GenerationPauseReason.SpendCapReached);
        run.CountsTowardLimit.ShouldBeFalse();
        run.ErrorCode.ShouldBeNull("the pause reason is its own column, never overloaded into the error");
        run.ErrorDetail.ShouldBeNull();
    }

    [Fact]
    public void GenerationRun_Start_ClearsThePause_AndSetsStartedAt()
    {
        var run = NewStoryRun();
        run.MarkPaused(GenerationPauseReason.ProviderFundsExhausted, FixedNow);

        run.Start(FixedNow.AddHours(1));

        run.Status.ShouldBe(GenerationRunStatus.Running);
        run.PauseReason.ShouldBeNull();
        run.StartedAt.ShouldBe(FixedNow.AddHours(1));
    }

    [Fact]
    public void GenerationRun_MarkPaused_WhenRunning_Throws()
    {
        Should.Throw<InvalidOperationException>(
            () => RunningStoryRun().MarkPaused(GenerationPauseReason.SpendCapReached, FixedNow));
    }

    [Fact]
    public void GenerationRun_Succeed_SetsDoneStageCostAndFinishedAt()
    {
        var run = RunningStoryRun();
        run.AdvanceStage(GenerationStage.Saving, FixedNow).IsError.ShouldBeFalse();

        run.Succeed(0.123456m, FixedNow.AddSeconds(30));

        run.Status.ShouldBe(GenerationRunStatus.Succeeded);
        run.Stage.ShouldBe(GenerationStage.Done);
        run.CostUsd.ShouldBe(0.123456m);
        run.FinishedAt.ShouldBe(FixedNow.AddSeconds(30));
        run.CountsTowardLimit.ShouldBeTrue("a saved version counts (plan §3.6)");
    }

    [Fact]
    public void GenerationRun_Succeed_NegativeCost_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => RunningStoryRun().Succeed(-0.01m, FixedNow));
    }

    [Fact]
    public void GenerationRun_Cancel_FromQueued_SetsCancelledAndFinishedAt()
    {
        var run = NewStoryRun();

        run.Cancel(FixedNow.AddHours(12));

        run.Status.ShouldBe(GenerationRunStatus.Cancelled);
        run.FinishedAt.ShouldBe(FixedNow.AddHours(12));
        run.StartedAt.ShouldBeNull("ck_generation_runs_times allows a null start");
        run.CountsTowardLimit.ShouldBeTrue("cancel leaves the counting flag as it was (plan §3.6)");
    }

    [Theory]
    [InlineData(GenerationKind.Story, true)]
    [InlineData(GenerationKind.FactSheet, true)]
    [InlineData(GenerationKind.FindSources, true)]
    [InlineData(GenerationKind.Ideas, false)]
    [InlineData(GenerationKind.BibleProposal, false)]
    public void GenerationRun_Queue_EnforcesTheEpisodeScopeOfEachKind(GenerationKind kind, bool episodeScoped)
    {
        var episodeId = Guid.CreateVersion7();

        var correct = Queue(kind, episodeScoped ? episodeId : null);
        var wrong = Queue(kind, episodeScoped ? null : episodeId);

        correct.IsError.ShouldBeFalse();
        correct.Value.EpisodeId.ShouldBe(episodeScoped ? episodeId : null);
        wrong.IsError.ShouldBeTrue("T17: the scope follows the kind");
        wrong.FirstError.Code.ShouldBe("generation_run_scope_invalid");
    }

    [Fact]
    public void GenerationRun_Queue_StartsQueuedAndCounting()
    {
        var run = NewStoryRun();

        run.Id.ShouldNotBe(Guid.Empty);
        run.Status.ShouldBe(GenerationRunStatus.Queued);
        run.Stage.ShouldBe(GenerationStage.Queued);
        run.CountsTowardLimit.ShouldBeTrue();
        run.Attempt.ShouldBe(0);
        run.QueuedAt.ShouldBe(FixedNow);
        run.CreatedAt.ShouldBe(FixedNow, "queued_at == created_at by construction");
        run.StartedAt.ShouldBeNull();
        run.FinishedAt.ShouldBeNull();
        run.PreviousEpisodeStatus.ShouldBe(EpisodeStatus.Draft);
    }

    [Fact]
    public void GenerationRun_Queue_RefusesAReturnTargetOutsideDraftOrReview()
    {
        var result = GenerationRun.Queue(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            GenerationKind.Story,
            GenerationTrigger.User,
            null,
            EpisodeStatus.Published,
            null,
            null,
            0m,
            FixedNow);

        result.FirstError.Code.ShouldBe("generation_run_previous_status_invalid");
    }

    [Fact]
    public void GenerationRun_Queue_RefusesNegativeWorstCase_AndEmptyOwner()
    {
        Queue(GenerationKind.Ideas, null, worstCase: -1m).FirstError.Code.ShouldBe("generation_run_cost_invalid");
        GenerationRun.Queue(
                Guid.Empty,
                Guid.CreateVersion7(),
                null,
                GenerationKind.Ideas,
                GenerationTrigger.User,
                null,
                null,
                null,
                null,
                0m,
                FixedNow)
            .FirstError.Code.ShouldBe("generation_run_owner_required");
    }

    // ---- QA 02.2.2-T1: illegal transitions from terminal states, idempotent repeats keep the first outcome ----

    [Theory]
    [InlineData(GenerationRunStatus.Succeeded)]
    [InlineData(GenerationRunStatus.Failed)]
    [InlineData(GenerationRunStatus.Cancelled)]
    public void GenerationRun_TerminalRun_RefusesEveryFurtherTransition(GenerationRunStatus terminal)
    {
        var run = RunInTerminalState(terminal);
        var finishedAt = run.FinishedAt;

        Should.Throw<InvalidOperationException>(() => run.Start(FixedNow.AddHours(1)));
        Should.Throw<InvalidOperationException>(() => run.MarkPaused(GenerationPauseReason.SpendCapReached, FixedNow));
        if (terminal != GenerationRunStatus.Succeeded)
        {
            Should.Throw<InvalidOperationException>(() => run.Succeed(0.1m, FixedNow.AddHours(1)));
        }

        if (terminal != GenerationRunStatus.Failed)
        {
            Should.Throw<InvalidOperationException>(() => run.Fail("story_generation_failed", null, false, FixedNow));
        }

        if (terminal != GenerationRunStatus.Cancelled)
        {
            Should.Throw<InvalidOperationException>(() => run.Cancel(FixedNow.AddHours(1)));
        }

        run.AdvanceStage(GenerationStage.Writing, FixedNow).FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
        run.Status.ShouldBe(terminal);
        run.FinishedAt.ShouldBe(finishedAt, "a refused transition leaves the run alone");
    }

    [Fact]
    public void GenerationRun_Succeed_FromQueued_Throws_AndLeavesTheRunQueued()
    {
        var run = NewStoryRun();

        Should.Throw<InvalidOperationException>(() => run.Succeed(0.1m, FixedNow));

        run.Status.ShouldBe(GenerationRunStatus.Queued);
        run.CostUsd.ShouldBeNull();
    }

    [Fact]
    public void GenerationRun_Start_FromQueued_ThenAgain_KeepsTheFirstStartedAt()
    {
        var run = NewStoryRun();
        run.Start(FixedNow);

        run.Start(FixedNow.AddMinutes(5));

        run.StartedAt.ShouldBe(FixedNow);
    }

    [Fact]
    public void GenerationRun_Succeed_Twice_KeepsTheFirstCostAndFinishedAt()
    {
        var run = RunningStoryRun();
        run.Succeed(0.1m, FixedNow.AddSeconds(10));

        run.Succeed(9m, FixedNow.AddSeconds(99));

        run.CostUsd.ShouldBe(0.1m);
        run.FinishedAt.ShouldBe(FixedNow.AddSeconds(10));
    }

    [Fact]
    public void GenerationRun_Fail_Twice_KeepsTheFirstCodeAndCountingFlag()
    {
        var run = RunningStoryRun();
        run.Fail("story_moderated", "refusal", countsTowardLimit: true, FixedNow.AddSeconds(3));

        run.Fail("story_generation_failed", "other", countsTowardLimit: false, FixedNow.AddSeconds(9));

        run.ErrorCode.ShouldBe("story_moderated");
        run.ErrorDetail.ShouldBe("refusal");
        run.CountsTowardLimit.ShouldBeTrue();
        run.FinishedAt.ShouldBe(FixedNow.AddSeconds(3));
    }

    [Fact]
    public void GenerationRun_Fail_FromQueued_IsAllowed_TheSweeperPathForAStuckRun()
    {
        var run = NewStoryRun();

        run.Fail("story_generation_failed", "stuck_queued", countsTowardLimit: false, FixedNow.AddHours(13));

        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.StartedAt.ShouldBeNull();
    }

    [Fact]
    public void GenerationRun_Cancel_FromRunning_AndTwice_KeepsTheFirstFinishedAt()
    {
        var run = RunningStoryRun();
        run.Cancel(FixedNow.AddSeconds(4));

        run.Cancel(FixedNow.AddSeconds(40));

        run.Status.ShouldBe(GenerationRunStatus.Cancelled);
        run.FinishedAt.ShouldBe(FixedNow.AddSeconds(4));
        run.StartedAt.ShouldBe(FixedNow);
    }

    [Fact]
    public void GenerationRun_PausedThenCancelled_StaysNonCounting_AndClearsThePause()
    {
        var run = NewStoryRun();
        run.MarkPaused(GenerationPauseReason.ProviderFundsExhausted, FixedNow);

        run.Cancel(FixedNow.AddHours(12));

        run.CountsTowardLimit.ShouldBeFalse("plan §3.6: paused then cancelled does not count");
        run.PauseReason.ShouldBeNull();
    }

    [Fact]
    public void GenerationRun_AdvanceStage_RefusesBackwardsFromSavingAndReviewingToCheckingWithoutRevision()
    {
        var run = RunningStoryRun();
        run.AdvanceStage(GenerationStage.Reviewing, FixedNow).IsError.ShouldBeFalse();

        run.AdvanceStage(GenerationStage.Checking, FixedNow).FirstError.Code
            .ShouldBe("generation_run_stage_backwards");

        run.AdvanceStage(GenerationStage.Saving, FixedNow).IsError.ShouldBeFalse();
        run.AdvanceStage(GenerationStage.Writing, FixedNow).IsError.ShouldBeTrue();
        run.Stage.ShouldBe(GenerationStage.Saving);
    }

    [Fact]
    public void GenerationRun_Queue_EmptySeries_IsRefused()
    {
        GenerationRun.Queue(
                Guid.CreateVersion7(),
                Guid.Empty,
                null,
                GenerationKind.Ideas,
                GenerationTrigger.System,
                null,
                null,
                null,
                null,
                0m,
                FixedNow)
            .FirstError.Code.ShouldBe("generation_run_owner_required");
    }

    private static GenerationRun RunInTerminalState(GenerationRunStatus terminal)
    {
        var run = RunningStoryRun();
        switch (terminal)
        {
            case GenerationRunStatus.Succeeded:
                run.Succeed(0.1m, FixedNow.AddSeconds(10));
                break;
            case GenerationRunStatus.Failed:
                run.Fail("story_generation_failed", null, false, FixedNow.AddSeconds(10));
                break;
            default:
                run.Cancel(FixedNow.AddSeconds(10));
                break;
        }

        return run;
    }

    private static GenerationRun NewStoryRun() =>
        Queue(GenerationKind.Story, Guid.CreateVersion7()).Value;

    private static GenerationRun RunningStoryRun()
    {
        var run = NewStoryRun();
        run.Start(FixedNow);
        return run;
    }

    private static ErrorOr.ErrorOr<GenerationRun> Queue(GenerationKind kind, Guid? episodeId, decimal worstCase = 0.25m) =>
        GenerationRun.Queue(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            episodeId,
            kind,
            GenerationTrigger.User,
            "idem-1",
            episodeId is null ? null : EpisodeStatus.Draft,
            null,
            null,
            worstCase,
            FixedNow);
}
