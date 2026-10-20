using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Gates;
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

        var step = new PlanIdeaStep(db, planner, new NoPrices(), new NoIdeaCosts(), new NoBudget(), new NoLedger(), NullLogger<PlanIdeaStep>.Instance);

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

        var step = new PlanIdeaStep(db, planner, new NoPrices(), new NoIdeaCosts(), new NoBudget(), new NoLedger(), NullLogger<PlanIdeaStep>.Instance);

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

        var step = new PlanIdeaStep(db, planner, new NoPrices(), new NoIdeaCosts(), new NoBudget(), new NoLedger(), NullLogger<PlanIdeaStep>.Instance);

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

    /// <summary>One series, its run in <paramref name="state"/>, an optional episode, and an open context.</summary>
    private async Task<(AutopilotRun Run, Series Series, AppDbContext Db, FixedClock Clock)> WorldAsync(
        AutopilotRunState state,
        string? step,
        int attempt,
        DateTimeOffset? startAt = null,
        EpisodeStatus? episodeStatus = null)
    {
        var clock = new FixedClock(Now);
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
    private sealed class RecordingPlanner : IIdeaPlanner
    {
        public int Calls { get; private set; }

        public ValueTask<ErrorOr<IdeaPick>> ProposeAndPickAsync(
            Guid seriesId,
            DateTimeOffset slotAt,
            RunContext run,
            CancellationToken ct)
        {
            Calls++;

            return ValueTask.FromResult<ErrorOr<IdeaPick>>(
                new IdeaPick(
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
}
