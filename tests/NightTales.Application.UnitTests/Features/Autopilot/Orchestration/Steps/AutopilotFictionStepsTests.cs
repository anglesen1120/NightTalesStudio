using System.Globalization;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Structure;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The fiction half of the autopilot steps (02.8.3 plan §3.5, §7.2): the idea step's guards, the authorization
/// park, the story gate's one regeneration and its <c>gate_failed</c> ending.
/// </summary>
/// <remarks>
/// The steps run against a real <see cref="AppDbContext"/> because their contract is a set of reads and aggregate
/// moves; the paid seams (<see cref="IIdeaPlanner"/>, <see cref="IAutoApprovalGate"/>) and the money seams
/// (<see cref="IBudgetGuard"/>, <see cref="ICreditLedger"/>, <see cref="IPriceCatalog"/>,
/// <see cref="IIdeaCostEstimator"/>) are hand-written fakes, so nothing here reaches a provider.
/// </remarks>
public sealed class AutopilotFictionStepsTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly Guid UserId = Guid.Parse("018f0000-0000-7000-8000-00000000011a");

    private static readonly DateTimeOffset Now = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PlanIdeaStep_NotDue_IsIdleAndPaysNothing()
    {
        var planner = new RecordingPlanner();
        var (run, series, db, clock) = await WorldAsync(AutopilotRunState.Planned, step: null, attempt: 0, startAt: Now.AddHours(1));

        var step = new PlanIdeaStep(db, new NoChannelReader(), planner, new NoPrices(), new NoIdeaCosts(), new NoBudget(), new NoLedger(), NullLogger<PlanIdeaStep>.Instance);

        var result = await step.HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None);

        result.Kind.ShouldBe(StepResultKind.Idle);
        planner.Calls.ShouldBe(0, "a slot that has not started buys nothing");
        run.State.ShouldBe(AutopilotRunState.Planned);
    }

    [Fact]
    public async Task PlanIdeaStep_SlotMissed_FailsTheSlotWithoutPaying()
    {
        var planner = new RecordingPlanner();
        var (run, series, db, clock) = await WorldAsync(AutopilotRunState.Planned, step: null, attempt: 0, startAt: Now.AddHours(-4));

        var step = new PlanIdeaStep(db, new NoChannelReader(), planner, new NoPrices(), new NoIdeaCosts(), new NoBudget(), new NoLedger(), NullLogger<PlanIdeaStep>.Instance);

        var result = await step.HandleAsync(
            Context(run, series, clock.UtcNow, uploadAt: Now.AddHours(-1)),
            CancellationToken.None);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotRun.SlotMissedReason);
        run.State.ShouldBe(AutopilotRunState.Failed);
        planner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task PlanIdeaStep_NoAuthorization_ParksInAwaitingAuthorization()
    {
        var planner = new RecordingPlanner();
        var (run, series, db, clock) = await WorldAsync(AutopilotRunState.Planned, step: null, attempt: 0);

        var step = new PlanIdeaStep(db, new NoChannelReader(), planner, new NoPrices(), new NoIdeaCosts(), new NoBudget(), new NoLedger(), NullLogger<PlanIdeaStep>.Instance);

        var result = await step.HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe("awaiting_authorization");
        run.State.ShouldBe(AutopilotRunState.AwaitingAuthorization);
        run.Step.ShouldBe(AutopilotRunStep.Authorization);
        planner.Calls.ShouldBe(0, "an unauthorized series may not buy an idea");
    }

    [Fact]
    public async Task AwaitingAuthorizationStep_StillUnauthorized_IsIdle()
    {
        var (run, series, db, clock) = await WorldAsync(AutopilotRunState.Planned, step: null, attempt: 0);

        run.AdvanceTo(AutopilotRunState.AwaitingAuthorization, AutopilotRunStep.Authorization, Now);
        await db.SaveChangesAsync(CancellationToken.None);

        var step = new AwaitingAuthorizationStep(db, NullLogger<AwaitingAuthorizationStep>.Instance);

        var result = await step.HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None);

        result.Kind.ShouldBe(StepResultKind.Idle);
        run.State.ShouldBe(AutopilotRunState.AwaitingAuthorization);
    }

    /// <summary>
    /// An unauthorized run does not wait forever: once the missed-slot cutoff passes it ends <c>slot_missed</c> like
    /// every other step before the video, so an owner who never accepts cannot keep a due row per day forever (review
    /// findings PF-1, SEC-2).
    /// </summary>
    [Fact]
    public async Task AwaitingAuthorizationStep_SlotMissed_FailsTheRun()
    {
        var (run, series, db, clock) = await WorldAsync(AutopilotRunState.Planned, step: null, attempt: 0);

        run.AdvanceTo(AutopilotRunState.AwaitingAuthorization, AutopilotRunStep.Authorization, Now);
        await db.SaveChangesAsync(CancellationToken.None);

        var step = new AwaitingAuthorizationStep(db, NullLogger<AwaitingAuthorizationStep>.Instance);

        var result = await step.HandleAsync(
            Context(run, series, clock.UtcNow, uploadAt: Now.AddHours(-1)),
            CancellationToken.None);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotRun.SlotMissedReason);
        run.State.ShouldBe(AutopilotRunState.Failed);
        run.PauseReason.ShouldBe(AutopilotPauseReason.SlotMissed);
    }

    [Fact]
    public async Task FictionRun_GateFailure_RegeneratesOnce_ThenGateFailed()
    {
        // Attempt 1: the checks fail, so the run spends its second attempt and goes back to generating.
        var firstGate = new ScriptedGate(failedChecks: true);
        var (firstRun, firstSeries, firstDb, firstClock) = await WorldAsync(
            AutopilotRunState.StoryGate,
            AutopilotRunStep.StoryGate,
            attempt: 1,
            episodeStatus: EpisodeStatus.StoryReview);

        var gateStep = new StoryGateStep(firstDb, firstGate, NullLogger<StoryGateStep>.Instance);
        var regenerated = await gateStep.HandleAsync(Context(firstRun, firstSeries, firstClock.UtcNow), CancellationToken.None);

        regenerated.Kind.ShouldBe(StepResultKind.Advanced);
        regenerated.Code.ShouldBe("story_regenerated");
        firstRun.State.ShouldBe(AutopilotRunState.StoryGenerating);
        firstRun.Attempt.ShouldBe(2);
        firstGate.Calls.ShouldBe(1);

        // Attempt 2: the second failed gate ends the slot, it never buys a third story.
        var secondGate = new ScriptedGate(failedChecks: true);
        var (secondRun, secondSeries, secondDb, secondClock) = await WorldAsync(
            AutopilotRunState.StoryGate,
            AutopilotRunStep.StoryGate,
            attempt: 2,
            episodeStatus: EpisodeStatus.StoryReview);

        var exhausted = await new StoryGateStep(secondDb, secondGate, NullLogger<StoryGateStep>.Instance)
            .HandleAsync(Context(secondRun, secondSeries, secondClock.UtcNow), CancellationToken.None);

        exhausted.Kind.ShouldBe(StepResultKind.Stopped);
        exhausted.Code.ShouldBe("gate_failed");
        secondRun.State.ShouldBe(AutopilotRunState.GateFailed);
        secondRun.Outcome.ShouldBe(AutopilotRunOutcome.GateFailed);
    }

    [Fact]
    public async Task StoryGate_AutoApproved_AdvancesToRendering()
    {
        var gate = new ScriptedGate(autoApproved: true);
        var (run, series, db, clock) = await WorldAsync(
            AutopilotRunState.StoryGate,
            AutopilotRunStep.StoryGate,
            attempt: 1,
            episodeStatus: EpisodeStatus.StoryReview);

        var result = await new StoryGateStep(db, gate, NullLogger<StoryGateStep>.Instance)
            .HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None);

        result.Kind.ShouldBe(StepResultKind.Advanced);
        result.Code.ShouldBe("story_approved");
        run.State.ShouldBe(AutopilotRunState.Rendering);
        gate.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task PlanIdeaStep_ChannelNotActive_PausesBeforeAnySpend()
    {
        var planner = new RecordingPlanner();
        var (run, series, db, clock) = await WorldAsync(AutopilotRunState.Planned, step: null, attempt: 0);
        await AuthorizeAsync(series, db);

        var step = new PlanIdeaStep(
            db,
            new ScriptedChannelReader(ChannelConnectionStatus.Expired, Platform.YouTube),
            planner,
            new NoPrices(),
            new NoIdeaCosts(),
            new NoBudget(),
            new NoLedger(),
            NullLogger<PlanIdeaStep>.Instance);

        var result = await step.HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None);

        // Plan §3.6 row 6 and the task file's failure table: the run pauses before the first paid dispatch, so a
        // connection the owner still has to reconnect costs the slot nothing. The series is not paused (T4's
        // trigger 6 owns that).
        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.ChannelNotActive);
        run.State.ShouldBe(AutopilotRunState.Paused);
        run.PauseReason.ShouldBe(AutopilotPauseReason.ChannelNotActive);
        run.Outcome.ShouldBe(AutopilotRunOutcome.Paused);
        run.Attempt.ShouldBe(0);

        planner.Calls.ShouldBe(0, "no idea is bought through a channel the slot cannot publish on");
        (await db.GenerationRuns.CountAsync(row => row.SeriesId == series.Id, CancellationToken.None))
            .ShouldBe(0, "no generation row is committed, so nothing was reserved or paid");
    }

    [Fact]
    public async Task PlanIdeaStep_ChannelUnsupported_PausesBeforeAnySpend()
    {
        var planner = new RecordingPlanner();
        var (run, series, db, clock) = await WorldAsync(AutopilotRunState.Planned, step: null, attempt: 0);
        await AuthorizeAsync(series, db);

        var step = new PlanIdeaStep(
            db,
            // `Platform` has YouTube only today: the value 07 adds is what this case stands for.
            new ScriptedChannelReader(ChannelConnectionStatus.Active, (Platform)1),
            planner,
            new NoPrices(),
            new NoIdeaCosts(),
            new NoBudget(),
            new NoLedger(),
            NullLogger<PlanIdeaStep>.Instance);

        var result = await step.HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None);

        // Plan §3.6 row 6b: TikTok Direct Post cannot honour a cadence slot (07 CR-5), so the run holds its slot
        // before the reservation instead of paying for three steps and stopping at the publication.
        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.ChannelUnsupported);
        run.State.ShouldBe(AutopilotRunState.Paused);
        run.PauseReason.ShouldBe(AutopilotPauseReason.ChannelUnsupported);

        planner.Calls.ShouldBe(0, "an unsupported platform never buys an idea");
        (await db.GenerationRuns.CountAsync(row => row.SeriesId == series.Id, CancellationToken.None))
            .ShouldBe(0, "no generation row is committed, so nothing was reserved or paid");
    }

    [Fact]
    public async Task PlanIdeaStep_FundsExhaustedInThePaidCall_PausesTheRunAndFailsTheRow()
    {
        var planner = new RecordingPlanner(
            Error.Failure(ErrorCodes.ProviderFundsExhausted, "The provider account is out of funds."));
        var ledger = new RecordingLedger();
        var (run, series, db, clock) = await WorldAsync(AutopilotRunState.Planned, step: null, attempt: 0);
        await AuthorizeAsync(series, db);

        var step = PayingIdeaStep(db, planner, ledger);

        var result = await step.HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None);

        // CR-1 with plan §3.6 row 5 and plan §3.5's `story_generating → paused`: the row records the code the pause
        // trigger reads and the run is held with the same reason, so no later tick can idle on a queued row until
        // `slot_missed`. Retry (the old answer) saved neither and the slot could never pay again.
        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(ErrorCodes.ProviderFundsExhausted);
        run.State.ShouldBe(AutopilotRunState.Paused);
        run.PauseReason.ShouldBe(AutopilotPauseReason.ProviderFundsExhausted);
        run.Outcome.ShouldBe(AutopilotRunOutcome.Paused);

        // The orchestrator's single save is what persists both writes; the rollback of an unsaved context would
        // hide exactly the defect this case is about.
        await db.SaveChangesAsync(CancellationToken.None);
        await db.Entry(run).ReloadAsync(CancellationToken.None);
        run.State.ShouldBe(AutopilotRunState.Paused);

        var row = await db.GenerationRuns.SingleAsync(candidate => candidate.SeriesId == series.Id, CancellationToken.None);
        await db.Entry(row).ReloadAsync(CancellationToken.None);
        row.Status.ShouldBe(GenerationRunStatus.Failed, "the next tick must see a terminal row, not a queued one");
        row.ErrorCode.ShouldBe(ErrorCodes.ProviderFundsExhausted);

        ledger.Refunded.Count.ShouldBe(1, "a slot that bought nothing releases its reservation exactly once");
        ledger.Refunded[0].Reason.ShouldBe(ErrorCodes.ProviderFundsExhausted);
        ledger.Settled.ShouldBeEmpty();
    }

    [Fact]
    public async Task PlanIdeaStep_RefusedGenerationRow_RefundsTheReservation()
    {
        var planner = new RecordingPlanner();
        var ledger = new RecordingLedger();
        var (run, series, db, clock) = await WorldAsync(AutopilotRunState.Planned, step: null, attempt: 0);
        await AuthorizeAsync(series, db);

        // `GenerationRun.Queue` refuses a negative bound; what refuses the row does not matter here, only that the
        // reservation made for it is released instead of outliving a row that never existed (PF-13).
        var step = new PlanIdeaStep(
            db,
            new ScriptedChannelReader(ChannelConnectionStatus.Active, Platform.YouTube),
            planner,
            new PayingPrices(),
            new FixedIdeaCosts(-1m),
            new AllowingBudget(),
            ledger,
            NullLogger<PlanIdeaStep>.Instance);

        var result = await step.HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe("generation_run_cost_invalid");
        run.State.ShouldBe(AutopilotRunState.Failed);

        ledger.Reserved.Count.ShouldBe(1);
        ledger.Refunded.Count.ShouldBe(1, "no row was committed, so no call can spend the reservation");
        ledger.Refunded[0].Reservation.Value.ShouldBe(ledger.Reservation.Value);
        ledger.Refunded[0].Reason.ShouldBe("generation_run_cost_invalid");
        ledger.Settled.ShouldBeEmpty();

        planner.Calls.ShouldBe(0, "a slot without its generation row buys nothing");
        (await db.GenerationRuns.CountAsync(row => row.SeriesId == series.Id, CancellationToken.None))
            .ShouldBe(0);
    }

    [Fact]
    public async Task PlanIdeaStep_LostRowInsert_RefundsTheReservationAndRethrows()
    {
        var planner = new RecordingPlanner();
        var ledger = new RecordingLedger();
        var (run, series, db, clock) = await WorldAsync(AutopilotRunState.Planned, step: null, attempt: 0);
        await AuthorizeAsync(series, db);

        // The overlap PF-13 names: another ideas run of the series is still active, so this tick's insert violates
        // `ux_generation_runs_active_series` after the reservation was already taken.
        var competing = GenerationRun.Queue(
            run.UserId,
            run.SeriesId,
            episodeId: null,
            GenerationKind.Ideas,
            GenerationTrigger.User,
            "manual:ideas",
            previousEpisodeStatus: null,
            inputFactSheetId: null,
            reservationId: Guid.CreateVersion7(),
            worstCaseUsd: 0.20m,
            now: Now);

        competing.IsError.ShouldBeFalse();

        db.GenerationRuns.Add(competing.Value);
        await db.SaveChangesAsync(CancellationToken.None);

        var step = PayingIdeaStep(db, planner, ledger);

        // The exception is not swallowed: the tick's own failure path (the orchestrator's) still sees it.
        await Should.ThrowAsync<DbUpdateException>(
            () => step.HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None).AsTask());

        ledger.Reserved.Count.ShouldBe(1);
        ledger.Refunded.Count.ShouldBe(1, "the reservation may not outlive a row whose insert failed");
        ledger.Refunded[0].Reservation.Value.ShouldBe(ledger.Reservation.Value);
        ledger.Refunded[0].Reason.ShouldBe("generation_run_not_committed");
        ledger.Settled.ShouldBeEmpty();
        planner.Calls.ShouldBe(0, "the paid call runs only after the row committed");
    }

    [Fact]
    public async Task PlanIdeaStep_AdoptsACrashedRow_CompletesItAndSettlesOnce()
    {
        var planner = new RecordingPlanner();
        var ledger = new RecordingLedger();
        var (run, series, db, clock) = await WorldAsync(AutopilotRunState.Planned, step: null, attempt: 0);
        await AuthorizeAsync(series, db);

        // CR-8's crash: the planner committed its pick and died before the orchestrator's save, so the row is still
        // queued although the call it paid for finished. No job follows an ideas row, so adoption is its only writer.
        var row = GenerationRun.Queue(
            run.UserId,
            run.SeriesId,
            episodeId: null,
            GenerationKind.Ideas,
            GenerationTrigger.Autopilot,
            AutopilotIdempotencyKeys.For(run, AutopilotRunStep.Idea),
            previousEpisodeStatus: null,
            inputFactSheetId: null,
            reservationId: ledger.Reservation.Value,
            worstCaseUsd: 0.20m,
            now: Now);

        row.IsError.ShouldBeFalse();

        db.GenerationRuns.Add(row.Value);

        var idea = EpisodeIdea.CreateProposed(
            series.Id,
            run.UserId,
            row.Value.Id,
            new IdeaCandidate("title", "logline", "hook", "setting", StoryStructureTags.Empty, []),
            clock);

        idea.MarkPicked(0.5m, clock);
        db.EpisodeIdeas.Add(idea);
        await db.SaveChangesAsync(CancellationToken.None);

        // Every guard ahead of the adoption is behind this row already, so no price, budget or channel read happens.
        var step = new PlanIdeaStep(
            db,
            new NoChannelReader(),
            planner,
            new NoPrices(),
            new NoIdeaCosts(),
            new NoBudget(),
            ledger,
            NullLogger<PlanIdeaStep>.Instance);

        var result = await step.HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None);

        result.Kind.ShouldBe(StepResultKind.Advanced);
        run.State.ShouldBe(AutopilotRunState.IdeaPicked);
        run.Step.ShouldBe(AutopilotRunStep.Idea);
        run.IdeaId.ShouldBe(idea.Id);
        planner.Calls.ShouldBe(0, "a row that already bought a pick is never paid for twice");

        await db.SaveChangesAsync(CancellationToken.None);
        await db.Entry(row.Value).ReloadAsync(CancellationToken.None);

        row.Value.Status.ShouldBe(GenerationRunStatus.Succeeded);
        row.Value.CostUsd.ShouldBe(0m, "no usage row names the reservation yet, so the sum is zero");

        ledger.Settled.Count.ShouldBe(1, "the adopted row's reservation is settled once");
        ledger.Settled[0].Value.ShouldBe(ledger.Reservation.Value);
        ledger.Refunded.ShouldBeEmpty("a pick that exists is paid for, not refunded");
    }

    /// <summary>
    /// The idea step as the paying cases build it: an active YouTube channel, one price version and a spending
    /// wallet, so only the seam under test varies.
    /// </summary>
    /// <param name="db">The step's context.</param>
    /// <param name="planner">The paid seam.</param>
    /// <param name="ledger">The money seam.</param>
    private static PlanIdeaStep PayingIdeaStep(AppDbContext db, IIdeaPlanner planner, RecordingLedger ledger) =>
        new(
            db,
            new ScriptedChannelReader(ChannelConnectionStatus.Active, Platform.YouTube),
            planner,
            new PayingPrices(),
            new FixedIdeaCosts(0.20m),
            new AllowingBudget(),
            ledger,
            NullLogger<PlanIdeaStep>.Instance);

    [Fact]
    public async Task Run_StoryGenerationFailedPlatform_Attempt1_Regenerates()
    {
        var (run, series, db, clock) = await WorldAsync(
            AutopilotRunState.StoryGenerating,
            AutopilotRunStep.Story,
            attempt: 1,
            episodeStatus: EpisodeStatus.Draft);

        await FailStoryAttemptAsync(db, run, AutopilotRunStep.Story, ErrorCodes.StoryGenerationFailed);

        var sender = new RecordingSender();
        // The spend guard re-reads the series' authorization before the paid dispatch (S71 SEC-1, PF-5).
        await AuthorizeAsync(series, db);

        var step = new GenerateStoryStep(db, sender, NullLogger<GenerateStoryStep>.Instance);

        var result = await step.HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None);

        // Plan §3.6: a platform failure spends the one attempt and the regeneration is the next tick's `…:story2`
        // dispatch, so this branch sends nothing and buys nothing.
        result.Kind.ShouldBe(StepResultKind.Advanced);
        run.State.ShouldBe(AutopilotRunState.StoryGenerating);
        run.Attempt.ShouldBe(2);
        run.Step.ShouldBe(AutopilotRunStep.Story, "only a dispatch names a step, and nothing was dispatched here");
        sender.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_StoryGenerationFailedPlatform_Moderated_Fails()
    {
        var (run, series, db, clock) = await WorldAsync(
            AutopilotRunState.StoryGenerating,
            AutopilotRunStep.Story,
            attempt: 1,
            episodeStatus: EpisodeStatus.Draft);

        // QA-1: the attempt-1 row is `…:story`, not `…:story2`. Reading the wrong key hid this code and paid twice.
        await FailStoryAttemptAsync(db, run, AutopilotRunStep.Story, ErrorCodes.StoryModerated);

        var sender = new RecordingSender();
        // The spend guard re-reads the series' authorization before the paid dispatch (S71 SEC-1, PF-5).
        await AuthorizeAsync(series, db);

        var step = new GenerateStoryStep(db, sender, NullLogger<GenerateStoryStep>.Instance);

        var result = await step.HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None);

        // Plan §3.5 row 209: `story_moderated` ends the run whatever the attempt, because a retry would buy a
        // second flag (plan §3.6).
        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(ErrorCodes.StoryModerated);
        run.State.ShouldBe(AutopilotRunState.Failed);
        run.Attempt.ShouldBe(1, "moderation is never retried in the same slot");
        sender.Sent.ShouldBeEmpty("a moderated story must not buy a second generation");
    }

    [Fact]
    public async Task Run_StoryGenerationFailedPlatform_SpendCap_Pauses()
    {
        // Attempt 2 with no `…:story2` row is the regeneration: the one dispatch this step can still make.
        var (run, series, db, clock) = await WorldAsync(
            AutopilotRunState.StoryGenerating,
            AutopilotRunStep.Story,
            attempt: 2,
            episodeStatus: EpisodeStatus.Draft);

        var sender = new RefusingSender(ErrorCodes.SpendCapReached);
        // The spend guard re-reads the series' authorization before the paid dispatch (S71 SEC-1, PF-5).
        await AuthorizeAsync(series, db);

        var step = new GenerateStoryStep(db, sender, NullLogger<GenerateStoryStep>.Instance);

        var result = await step.HandleAsync(Context(run, series, clock.UtcNow), CancellationToken.None);

        // The task file's failure table ("budget refusal at any paid dispatch"): the step answers `failed` with
        // `spend_cap_reached` and nothing is dispatched again; the series pause is T4's trigger 2.
        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(ErrorCodes.SpendCapReached);
        run.State.ShouldBe(AutopilotRunState.Failed);
        run.Outcome.ShouldBe(AutopilotRunOutcome.Failed);
        sender.Sent.Count.ShouldBe(1, "the refused dispatch is the only call, and it bought nothing");
        (await db.GenerationRuns.CountAsync(row => row.SeriesId == series.Id, CancellationToken.None))
            .ShouldBe(0, "a refused dispatch commits no row");
    }

    /// <summary>
    /// Writes the accepted authorization row and the series' pointer to it — the two rows 02.8.3-T3 owns. The idea
    /// step reads the pointer off the series it was handed, so the entity is attached and updated in place rather
    /// than re-read.
    /// </summary>
    /// <param name="series">The run's series, as the context carries it.</param>
    /// <param name="db">The step's context.</param>
    /// <returns>The authorization id.</returns>
    private async Task<Guid> AuthorizeAsync(Series series, AppDbContext db)
    {
        var id = Guid.CreateVersion7();
        var stamp = Now.ToString("O", CultureInfo.InvariantCulture);
        var hash = new string('a', 64);

        await Database.ExecuteAsync(
            "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, text_hash, "
            + "topic, channel_connection_id, cadence, privacy_status, accepted_at, created_at, updated_at) "
            + $"VALUES ('{id}', '{UserId}', '{series.Id}', 1, 'ai.v1', '{hash}', 'ghost', NULL, 'weekly', "
            + $"'public', '{stamp}', '{stamp}', '{stamp}')",
            CancellationToken.None);

        db.Attach(series);
        db.Entry(series).Property(row => row.CurrentAutopilotAuthorizationId).CurrentValue = id;
        await db.SaveChangesAsync(CancellationToken.None);

        return id;
    }

    /// <summary>
    /// The failed story row 02.2.2 leaves behind: the row of the step it names fails with its code and the episode
    /// returns to draft, which is the pair <see cref="GenerateStoryStep"/> reads as the attempt's outcome.
    /// </summary>
    /// <param name="db">The step's context.</param>
    /// <param name="run">The run the row is keyed to.</param>
    /// <param name="step">The attempt's step (<see cref="AutopilotRunStep.Story"/> or <see cref="AutopilotRunStep.Story2"/>).</param>
    /// <param name="code">The failure code the row carries.</param>
    private static async Task FailStoryAttemptAsync(AppDbContext db, AutopilotRun run, string step, string code)
    {
        var row = GenerationRun.Queue(
            run.UserId,
            run.SeriesId,
            run.EpisodeId,
            GenerationKind.Story,
            GenerationTrigger.Autopilot,
            AutopilotIdempotencyKeys.For(run, step),
            EpisodeStatus.Draft,
            inputFactSheetId: null,
            reservationId: Guid.CreateVersion7(),
            worstCaseUsd: 0.20m,
            now: Now);

        row.IsError.ShouldBeFalse();

        row.Value.Fail(code, detail: null, countsTowardLimit: true, Now);

        db.GenerationRuns.Add(row.Value);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>One series, its run in <paramref name="state"/>, an optional episode, and an open context.</summary>
    private async Task<(AutopilotRun Run, Series Series, AppDbContext Db, FixedClock Clock)> WorldAsync(
        AutopilotRunState state,
        string? step,
        int attempt,
        DateTimeOffset? startAt = null,
        EpisodeStatus? episodeStatus = null)
    {
        var clock = new FixedClock(Now);

        // `series.user_id` is a foreign key onto the Identity table, so the owner exists first; a test
        // that builds a second world in the same class database reuses the row rather than colliding.
        var owners = await Database.QueryStringsAsync($"SELECT count(*)::text FROM users WHERE id = '{UserId}'");

        if (owners[0] == "0")
        {
            await new UserBuilder(Database, new TestCurrentUser { Id = UserId }).CreateAsync(UserId);
        }

        var seriesBuilder = new SeriesBuilder(Database, new TestCurrentUser { Id = UserId }, clock);
        var series = await seriesBuilder.CreateAsync(UserId, autopilotEnabled: true);

        var db = Database.CreateAppDbContext(new TestCurrentUser { Id = UserId }, clock);

        Guid? episodeId = null;

        if (episodeStatus is { } status)
        {
            episodeId = (await new EpisodeBuilder(Database, new TestCurrentUser { Id = UserId }, clock)
                .CreateAsync(UserId, series.Id, status: status)).Id;
        }

        var run = AutopilotRun.Plan(UserId, series.Id, Now.AddDays(1), startAt ?? Now.AddHours(-4), authorizationId: null, Now);

        if (state is not AutopilotRunState.Planned)
        {
            run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);

            if (episodeId is { } attached)
            {
                run.AttachEpisode(attached);
            }

            for (var spent = 0; spent < attempt; spent++)
            {
                run.ConsumeStoryAttempt();
            }

            run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.Story, Now);

            if (state is not AutopilotRunState.StoryGenerating)
            {
                run.AdvanceTo(state, step, Now);
            }
        }

        db.AutopilotRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        return (run, series, db, clock);
    }

    /// <summary>The step's view: <see cref="Now"/> as the tick's reading, one fixed timing set.</summary>
    private static AutopilotStepContext Context(
        AutopilotRun run,
        Series series,
        DateTimeOffset now,
        DateTimeOffset? uploadAt = null)
    {
        var cadence = new AutopilotCadence([DayOfWeek.Tuesday, DayOfWeek.Friday], new TimeOnly(19, 0), "Asia/Ho_Chi_Minh");

        var settings = new AutopilotSettings(
            SchemaVersion: 1,
            Cadence: cadence,
            DailyCap: 2,
            BufferDays: 2,
            Privacy: PrivacyStatus.Public,
            VetoWindowHours: 12,
            PreAuditMode: PreAuditMode.KeepCadencePrivate,
            PreAuditPauseAfter: 0,
            GateThresholdsRef: "v1",
            Tone: "ghiền",
            BannedThemes: [],
            RenderLeadMinutes: null,
            StoryLeadMinutes: null);

        var timing = new AutopilotRunTiming(
            StartAt: run.StartAt,
            PublicationCreateAt: (uploadAt ?? now.AddHours(6)).AddMinutes(-10),
            UploadAt: uploadAt ?? now.AddHours(6),
            RenderStartAt: (uploadAt ?? now.AddHours(6)).AddMinutes(-15),
            StoryStartAt: (uploadAt ?? now.AddHours(6)).AddMinutes(-25));

        return new AutopilotStepContext(
            run,
            series,
            settings,
            timing,
            new AutopilotTimingOptions(
                PublishLeadMinutes: 120,
                MinVetoWindowHours: 2,
                PublicationCreateMarginMinutes: 10,
                RenderEstimateMinutes: 15,
                StoryEstimateMinutes: 10,
                MaxBufferDays: 7),
            now);
    }

    /// <summary>The paid seam: records the calls and answers the scripted pick.</summary>
    /// <param name="refusal">The error the planner answers; null answers a pick, so a guard test reads the same planner.</param>
    private sealed class RecordingPlanner(Error? refusal = null) : IIdeaPlanner
    {
        public int Calls { get; private set; }

        public ValueTask<ErrorOr<IdeaPick>> ProposeAndPickAsync(
            Guid seriesId,
            DateTimeOffset slotAt,
            RunContext run,
            CancellationToken ct)
        {
            Calls++;

            if (refusal is { } scripted)
            {
                return ValueTask.FromResult<ErrorOr<IdeaPick>>(scripted);
            }

            return ValueTask.FromResult<ErrorOr<IdeaPick>>(new IdeaPick(
                Guid.CreateVersion7(),
                "title",
                "logline",
                "hook",
                "setting",
                StoryStructureTags.Empty,
                RequiresResearch: false,
                Score: 0m,
                Similarity: 0m));
        }

        public ValueTask<ErrorOr<Success>> MarkUsedAsync(Guid ideaId, Guid episodeId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }

    /// <summary>The gate seam: one scripted verdict, no provider and no command.</summary>
    private sealed class ScriptedGate(bool? autoApproved = null, bool failedChecks = false) : IAutoApprovalGate
    {
        public int Calls { get; private set; }

        public ValueTask<ErrorOr<StoryGateOutcome>> EvaluateStoryAsync(Guid runId, int attempt, CancellationToken ct)
        {
            Calls++;

            var result = new StoryGateResult(
                Pass: autoApproved is true,
                Hold: false,
                Attempt: attempt,
                StoryVersionId: Guid.CreateVersion7(),
                Checks: [],
                Failures: failedChecks ? ["self_review_mean_score"] : []);

            var reason = autoApproved is true
                ? (GateStopReason?)null
                : GateStopReason.FailedChecks;

            return ValueTask.FromResult<ErrorOr<StoryGateOutcome>>(
                new StoryGateOutcome(result, autoApproved is true, reason));
        }

        public ValueTask<ErrorOr<VideoGateOutcome>> EvaluateVideoAsync(Guid runId, CancellationToken ct) =>
            throw new NotSupportedException("the fiction story steps never evaluate a video.");
    }

    /// <summary>
    /// The port the idea step's earlier guards answer before it: every member throws, because the channel is only
    /// read by the branch that is about to spend, and a test of a guard ahead of it must not reach a read at all.
    /// </summary>
    private sealed class NoChannelReader : IAutopilotStepReader
    {
        public ValueTask<AutopilotChannelSnapshot?> FindChannelAsync(Guid? authorizationId, CancellationToken ct) =>
            throw new NotSupportedException("this case never reaches the channel guard.");

        public ValueTask<Guid?> FindCurrentAuthorizationAsync(
            NightTales.Domain.Series.Series series,
            CancellationToken ct) =>
            throw new NotSupportedException("this case never reaches the authorization read.");

        public ValueTask<AutopilotRenderEpisode?> FindEpisodeAsync(Guid episodeId, CancellationToken ct) =>
            throw new NotSupportedException("the idea step never reads an episode.");

        public ValueTask<AutopilotPublicationSnapshot?> FindPublicationAsync(Guid publicationId, CancellationToken ct) =>
            throw new NotSupportedException("the idea step never reads a publication.");

        public ValueTask<AutopilotActivePublication?> FindActivePublicationAsync(Guid episodeId, CancellationToken ct) =>
            throw new NotSupportedException("the idea step never reads an active publication.");

        public ValueTask<int> CountPrivateLockedRunsAsync(Guid seriesId, CancellationToken ct) =>
            throw new NotSupportedException("the idea step never counts locked-private runs.");
    }

    /// <summary>
    /// The channel the idea step's guard reads: one fixed snapshot, no database. Every other read of the port throws,
    /// because the idea step decides on the channel only and a hidden second read would be a defect.
    /// </summary>
    /// <param name="status">The connection status the guard sees.</param>
    /// <param name="platform">The platform the guard sees.</param>
    private sealed class ScriptedChannelReader(ChannelConnectionStatus status, Platform platform) : IAutopilotStepReader
    {
        public ValueTask<AutopilotChannelSnapshot?> FindChannelAsync(Guid? authorizationId, CancellationToken ct) =>
            ValueTask.FromResult<AutopilotChannelSnapshot?>(
                new AutopilotChannelSnapshot(Guid.CreateVersion7(), platform, status));

        public ValueTask<Guid?> FindCurrentAuthorizationAsync(
            NightTales.Domain.Series.Series series,
            CancellationToken ct) =>
            throw new NotSupportedException("the idea step never reads the series' authorization pointer.");

        public ValueTask<AutopilotRenderEpisode?> FindEpisodeAsync(Guid episodeId, CancellationToken ct) =>
            throw new NotSupportedException("the idea step never reads an episode.");

        public ValueTask<AutopilotPublicationSnapshot?> FindPublicationAsync(Guid publicationId, CancellationToken ct) =>
            throw new NotSupportedException("the idea step never reads a publication.");

        public ValueTask<AutopilotActivePublication?> FindActivePublicationAsync(Guid episodeId, CancellationToken ct) =>
            throw new NotSupportedException("the idea step never reads an active publication.");

        public ValueTask<int> CountPrivateLockedRunsAsync(Guid seriesId, CancellationToken ct) =>
            throw new NotSupportedException("the idea step never counts locked-private runs.");
    }

    private sealed class NoPrices : IPriceCatalog
    {
        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            throw new InvalidOperationException("the idea step must not price a slot it does not pay for.");

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) =>
            throw new InvalidOperationException("the idea step must not price a slot it does not pay for.");
    }

    private sealed class NoIdeaCosts : IIdeaCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(string priceVersion, CancellationToken ct) =>
            throw new InvalidOperationException("the idea step must not bound a slot it does not pay for.");

        public Task<ErrorOr<UsageBound>> NoveltyWorstCaseAsync(string priceVersion, CancellationToken ct) =>
            throw new InvalidOperationException("the idea step must not bound a slot it does not pay for.");
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class NoBudget : IBudgetGuard
    {
        public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null) =>
            throw new InvalidOperationException("the idea step must not check a budget for a slot it does not pay for.");
    }

    private sealed class NoLedger : ICreditLedger
    {
        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct) =>
            throw new InvalidOperationException("the idea step must not reserve for a slot it does not pay for.");

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            throw new InvalidOperationException("the idea step must not settle a slot it does not pay for.");

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            throw new InvalidOperationException("the idea step must not refund a slot it does not pay for.");
    }

    /// <summary>One active price version, so the paying cases get a quotable bound.</summary>
    private sealed class PayingPrices : IPriceCatalog
    {
        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromResult<ErrorOr<PriceVersion>>(
                PriceVersion.Create(1, Now.AddDays(-1), "{}", "{}", "hash", Now.AddDays(-1)));

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>One fixed bound: the paying cases vary the amount, never the catalogue.</summary>
    private sealed class FixedIdeaCosts(decimal worstCaseUsd) : IIdeaCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(string priceVersion, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(worstCaseUsd, 1_000, 1_000));

        public Task<ErrorOr<UsageBound>> NoveltyWorstCaseAsync(string priceVersion, CancellationToken ct) =>
            throw new NotSupportedException("the idea step never prices the novelty pass.");
    }

    /// <summary>A spend guard with room left: the paying cases test the ledger, not the caps.</summary>
    private sealed class AllowingBudget : IBudgetGuard
    {
        public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }

    /// <summary>
    /// The money seam of the paying cases: it hands out one reservation and records every reserve, settle and refund
    /// in order, so a reservation that is neither settled nor released is visible in the assertions.
    /// </summary>
    private sealed class RecordingLedger : ICreditLedger
    {
        public ReservationId Reservation { get; } = new(Guid.CreateVersion7());

        public List<decimal> Reserved { get; } = [];

        public List<ReservationId> Settled { get; } = [];

        public List<(ReservationId Reservation, string Reason)> Refunded { get; } = [];

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            Reserved.Add(bound.WorstCaseUsd);

            return ValueTask.FromResult<ErrorOr<ReservationId>>(Reservation);
        }

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct)
        {
            Settled.Add(reservationId);

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            Refunded.Add((reservationId, reason));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
