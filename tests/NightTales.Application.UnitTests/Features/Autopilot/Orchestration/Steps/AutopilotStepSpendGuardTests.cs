using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Application.Features.Stories.Commands.GenerateStory;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The spend guard every paid autopilot dispatch runs first (task rows SEC-1, PF-5, CR-2, SEC-5, CR-7, CR-10): an
/// archived series, an autopilot that is off or paused, or an authorization that is missing, revoked or another
/// series' must stop the dispatch and record the reason on the run instead of buying anything.
/// </summary>
/// <remarks>
/// The guard lives in <c>AutopilotStepSupport</c>, which is internal and this solution has no
/// <c>InternalsVisibleTo</c>, so each case drives it through the public step handler that owns the dispatch. The steps
/// read the episode, the generation rows and the series through a real (PostgreSQL) scope, so the suite needs
/// <c>NTS_TEST_PG</c>, like the step tests next to it. Graph: not applicable (the worktree is not indexed).
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AutopilotStepSpendGuardTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = AutopilotStepContexts.DefaultNow;

    /// <summary>
    /// A paused series (plan §3.6 row 1) whose run already passed the idea step: the episode step must not create the
    /// episode nor send the paid story command, and the run pauses with the series' own reason (SEC-1, PF-5).
    /// </summary>
    [Fact]
    public async Task CreateEpisodeStep_PausedSeries_PausesTheRunWithoutBuying()
    {
        var world = await SeedAsync();
        await using var db = Open(world);
        await AutopilotSeriesSeed.AcceptAsync(db, world.Series, Now, Ct);
        AutopilotSeriesSeed.Pause(db, world.Series, "gate_failed_streak", Now);

        var run = await AddRunAsync(db, world, AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, attempt: 0);
        var sender = new RecordingSender();
        var step = new CreateEpisodeStep(db, sender, new NeverPlanner(), NullLogger<CreateEpisodeStep>.Instance);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe("gate_failed_streak", "the run records the series' own trigger, not a fixed one");
        run.State.ShouldBe(AutopilotRunState.Paused);
        run.PauseReason.ShouldBe(AutopilotPauseReason.GateFailedStreak);
        sender.Sent.ShouldBeEmpty("no GenerateStoryCommand may be sent for a paused series");
        (await db.Episodes.CountAsync(row => row.SeriesId == world.Series.Id, Ct))
            .ShouldBe(1, "the guard runs before a second episode is created");
    }

    /// <summary>
    /// The run's one regeneration is a second paid story (CR-2, PF-5): with the series' autopilot paused by the spend
    /// cap the step must not send <c>…:story2</c> and must pause the run with that same reason.
    /// </summary>
    [Fact]
    public async Task GenerateStoryStep_PausedSeries_DoesNotBuyTheRegeneration()
    {
        var world = await SeedAsync(status: EpisodeStatus.Draft);
        await using var db = Open(world);
        await AutopilotSeriesSeed.AcceptAsync(db, world.Series, Now, Ct);
        AutopilotSeriesSeed.Pause(db, world.Series, "spend_cap_reached", Now);

        var run = await AddRunAsync(db, world, AutopilotRunState.StoryGenerating, AutopilotRunStep.Story, attempt: 2);
        var sender = new RecordingSender();
        var step = new GenerateStoryStep(db, sender, NullLogger<GenerateStoryStep>.Instance);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe("spend_cap_reached");
        run.State.ShouldBe(AutopilotRunState.Paused);
        run.PauseReason.ShouldBe(AutopilotPauseReason.SpendCapReached);
        sender.Sent.ShouldBeEmpty("attempt 2 is paid, and a paused series may not pay");
    }

    /// <summary>
    /// Autopilot switched off: both the source search and the fact sheet are paid, so the research step stops before
    /// either and the run pauses (SEC-1). Without a series pause reason the run records <c>owner_paused</c>, because
    /// the owner is the one who switched it off.
    /// </summary>
    [Fact]
    public async Task ResearchStep_AutopilotDisabled_PausesTheRunWithoutSearching()
    {
        var world = await SeedAsync();
        await using var db = Open(world);
        await AutopilotSeriesSeed.AcceptAsync(db, world.Series, Now, Ct);
        AutopilotSeriesSeed.Disable(db, world.Series);

        var run = await AddRunAsync(db, world, AutopilotRunState.Researching, step: null, attempt: 0);
        var sender = new RecordingSender();
        var step = new ResearchStep(sender, db, new FixedSourceFinderGo(hasGo: true));

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe("owner_paused");
        run.State.ShouldBe(AutopilotRunState.Paused);
        run.PauseReason.ShouldBe(AutopilotPauseReason.OwnerPaused);
        sender.Sent.ShouldBeEmpty("a switched-off series buys neither a search nor a fact sheet");
    }

    /// <summary>
    /// The research state's two dispatches (a source search and a fact sheet) are paid, and a run whose slot is past
    /// <c>upload_at − MinVetoWindowHours</c> can no longer publish: it ends <c>failed</c> with <c>slot_missed</c> and
    /// sends nothing (task row CR-2).
    /// </summary>
    [Fact]
    public async Task ResearchStep_SlotMissed_FailsWithoutSearching()
    {
        var world = await SeedAsync();
        await using var db = Open(world);
        await AutopilotSeriesSeed.AcceptAsync(db, world.Series, Now, Ct);

        var run = await AddRunAsync(db, world, AutopilotRunState.Researching, step: null, attempt: 0);
        var sender = new RecordingSender();
        var step = new ResearchStep(sender, db, new FixedSourceFinderGo(hasGo: true));

        // The cutoff is upload_at - MinVetoWindowHours, i.e. four hours before the slot: one hour before the slot is
        // past it.
        var late = AutopilotStepContexts.DefaultSlotAt.AddHours(-1);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, late), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotRun.SlotMissedReason);
        run.State.ShouldBe(AutopilotRunState.Failed);
        sender.Sent.ShouldBeEmpty("a missed slot pays for neither a search nor a fact sheet");
    }

    /// <summary>
    /// The owner revoked the authorization after the run passed the idea step: the render is the most expensive
    /// dispatch and it must not happen (SEC-1). The run pauses; <c>POST …/resume</c> after a re-accept continues it.
    /// </summary>
    [Fact]
    public async Task RenderStep_RevokedAuthorization_DoesNotRender()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryApproved);
        await using var db = Open(world);
        await AutopilotSeriesSeed.AcceptAsync(db, world.Series, Now, Ct, revokedAt: Now);

        var run = await AddRunAsync(db, world, AutopilotRunState.Rendering, AutopilotRunStep.StoryGate, attempt: 1);
        var sender = new RecordingSender();
        var step = new RenderStep(sender, new ThrowingReader(), db, NullLogger<RenderStep>.Instance);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(ErrorCodes.AutopilotAuthorizationRequired);
        run.State.ShouldBe(AutopilotRunState.Paused);
        sender.Sent.ShouldBeEmpty("a revoked authorization never pays for a render");
    }

    /// <summary>
    /// A live revision that belongs to <b>another</b> series of the same owner (SEC-5): the pointer is a column, so the
    /// guard must filter the row by the run's series rather than trust the id it names.
    /// </summary>
    [Fact]
    public async Task RenderStep_ForeignSeriesAuthorization_DoesNotRender()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryApproved);
        await using var db = Open(world);

        var other = await new SeriesBuilder(Database, world.Actor).CreateAsync(
            world.UserId,
            title: "Another series",
            autopilotEnabled: true,
            ct: Ct);

        await AutopilotSeriesSeed.AcceptForeignAsync(db, world.Series, other.Id, Now, Ct);

        var run = await AddRunAsync(db, world, AutopilotRunState.Rendering, AutopilotRunStep.StoryGate, attempt: 1);
        var sender = new RecordingSender();
        var step = new RenderStep(sender, new ThrowingReader(), db, NullLogger<RenderStep>.Instance);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(ErrorCodes.AutopilotAuthorizationRequired);
        run.State.ShouldBe(AutopilotRunState.Paused);
        sender.Sent.ShouldBeEmpty("another series' revision authorizes nothing here");
    }

    /// <summary>
    /// An archived series produces nothing again (F16): the run ends <c>failed</c> with <c>series_archived</c> instead
    /// of pausing, because no resume could make the slot producible.
    /// </summary>
    [Fact]
    public async Task RenderStep_ArchivedSeries_FailsTheRunWithoutRendering()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryApproved);
        await using var db = Open(world);
        await AutopilotSeriesSeed.AcceptAsync(db, world.Series, Now, Ct);

        world.Series.Archive(Now).IsError.ShouldBeFalse();

        var run = await AddRunAsync(db, world, AutopilotRunState.Rendering, AutopilotRunStep.StoryGate, attempt: 1);
        var sender = new RecordingSender();
        var step = new RenderStep(sender, new ThrowingReader(), db, NullLogger<RenderStep>.Instance);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(ErrorCodes.SeriesArchived);
        run.State.ShouldBe(AutopilotRunState.Failed);
        run.Outcome.ShouldBe(AutopilotRunOutcome.Failed);
        sender.Sent.ShouldBeEmpty("an archived series never pays for a render");
    }

    /// <summary>
    /// A held <c>…:story</c> row carries its own reason (CR-7): a <c>spend_cap_reached</c> hold pauses the run with
    /// that reason and answers the wire value, not <c>provider_funds_exhausted</c> and not a PascalCase enum name.
    /// </summary>
    [Fact]
    public async Task GenerateStoryStep_SpendCapHold_RecordsTheRowsOwnReason()
    {
        var world = await SeedAsync(status: EpisodeStatus.Draft);
        await using var db = Open(world);
        await AutopilotSeriesSeed.AcceptAsync(db, world.Series, Now, Ct);

        var run = await AddRunAsync(db, world, AutopilotRunState.StoryGenerating, AutopilotRunStep.Story, attempt: 1);
        await HoldStoryAsync(db, run, AutopilotRunStep.Story, GenerationPauseReason.SpendCapReached);

        var sender = new RecordingSender();
        var step = new GenerateStoryStep(db, sender, NullLogger<GenerateStoryStep>.Instance);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe("spend_cap_reached", "the code is the wire value the alert copy and the metrics use");
        run.State.ShouldBe(AutopilotRunState.Paused);
        run.PauseReason.ShouldBe(AutopilotPauseReason.SpendCapReached);
        sender.Sent.ShouldBeEmpty("a held run waits for the owner, it does not re-pay");
    }

    /// <summary>
    /// The gate failed attempt 1 and spent attempt 2, then a §3.6 trigger paused the run before the regeneration was
    /// dispatched (CR-10): resuming goes back to <c>story_generating</c>, and the step dispatches <c>…:story2</c>
    /// instead of re-evaluating the story that already failed.
    /// </summary>
    [Fact]
    public async Task GenerateStoryStep_ResumedAfterFailedGate_RegeneratesRatherThanReGating()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryReview);
        await using var db = Open(world);
        await AutopilotSeriesSeed.AcceptAsync(db, world.Series, Now, Ct);

        var run = await AddRunAsync(db, world, AutopilotRunState.StoryGate, AutopilotRunStep.StoryGate, attempt: 2);

        run.Pause(AutopilotPauseReason.ProviderFundsExhausted, Now);
        run.Resume(Now);

        run.State.ShouldBe(
            AutopilotRunState.StoryGenerating,
            "step story_gate with both attempts spent is the pending regeneration, not another gate pass");

        var sender = new RecordingSender();
        var step = new GenerateStoryStep(db, sender, NullLogger<GenerateStoryStep>.Instance);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Advanced);
        run.State.ShouldBe(AutopilotRunState.StoryGenerating);
        run.Step.ShouldBe(AutopilotRunStep.Story2);
        sender.Single<GenerateStoryCommand>().IdempotencyKey
            .ShouldBe(AutopilotIdempotencyKeys.For(run, AutopilotRunStep.Story2));
    }

    private AppDbContext Open(World world) =>
        Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FixedClock(Now));

    /// <summary>The run in <paramref name="state"/> with its episode attached and <paramref name="attempt"/> attempts spent.</summary>
    private static async Task<AutopilotRun> AddRunAsync(
        AppDbContext db,
        World world,
        AutopilotRunState state,
        string? step,
        int attempt)
    {
        var run = AutopilotRun.Plan(
            world.UserId,
            world.Series.Id,
            AutopilotStepContexts.DefaultSlotAt,
            startAt: Now.AddHours(-4),
            authorizationId: null,
            now: Now.AddHours(-4));

        run.AttachIdea(Guid.CreateVersion7());
        run.AttachEpisode(world.EpisodeId);
        run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now.AddHours(-3));

        // The ladder is walked by hand, because the §3.5 table has no shortcut: Researching comes straight off
        // IdeaPicked, while Rendering only off StoryGate.
        if (state is AutopilotRunState.Researching)
        {
            run.AdvanceTo(AutopilotRunState.Researching, step, Now.AddHours(-2));
        }
        else if (state is not AutopilotRunState.IdeaPicked)
        {
            for (var spent = 0; spent < attempt; spent++)
            {
                run.ConsumeStoryAttempt();
            }

            run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.Story, Now.AddHours(-2));

            if (state is not AutopilotRunState.StoryGenerating)
            {
                run.AdvanceTo(AutopilotRunState.StoryGate, AutopilotRunStep.StoryGate, Now.AddHours(-1));

                if (state is not AutopilotRunState.StoryGate)
                {
                    run.AdvanceTo(state, step, Now);
                }
            }
        }

        db.AutopilotRuns.Add(run);
        await db.SaveChangesAsync(Ct);

        return run;
    }

    /// <summary>The held row 02.2.2 leaves behind: queued, with the reason the provider or the cap gave.</summary>
    private static async Task HoldStoryAsync(
        AppDbContext db,
        AutopilotRun run,
        string step,
        GenerationPauseReason reason)
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

        row.Value.MarkPaused(reason, Now);

        db.GenerationRuns.Add(row.Value);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>One enabled series with its owner and its episode, in the shape a paid step expects.</summary>
    private async Task<World> SeedAsync(EpisodeStatus status = EpisodeStatus.Draft)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, autopilotEnabled: true, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        return new World(actor, user.Id, series, episode.Id);
    }

    private sealed record World(TestCurrentUser Actor, Guid UserId, SeriesAggregate Series, Guid EpisodeId);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>The idea seam: this suite never reaches a paid idea dispatch, so a call is a defect.</summary>
    private sealed class NeverPlanner : IIdeaPlanner
    {
        public ValueTask<ErrorOr<IdeaPick>> ProposeAndPickAsync(
            Guid seriesId,
            DateTimeOffset slotAt,
            RunContext run,
            CancellationToken ct) =>
            throw new NotSupportedException("the guard runs before the idea planner in this suite.");

        public ValueTask<ErrorOr<Success>> MarkUsedAsync(Guid ideaId, Guid episodeId, CancellationToken ct) =>
            throw new NotSupportedException("the guard runs before the idea is marked used in this suite.");
    }

    /// <summary>The episode port of the render cases: a read is a defect, because the guard must stop first.</summary>
    private sealed class ThrowingReader : IAutopilotStepReader
    {
        public ValueTask<AutopilotChannelSnapshot?> FindChannelAsync(Guid? authorizationId, CancellationToken ct) =>
            throw new NotSupportedException("the render cases never reach a channel.");

        public ValueTask<Guid?> FindCurrentAuthorizationAsync(
            NightTales.Domain.Series.Series series,
            CancellationToken ct) =>
            throw new NotSupportedException("the guard runs before the authorization read in these cases.");

        public ValueTask<AutopilotRenderEpisode?> FindEpisodeAsync(Guid episodeId, CancellationToken ct) =>
            throw new NotSupportedException("the guard runs before the episode read in these cases.");

        public ValueTask<AutopilotPublicationSnapshot?> FindPublicationAsync(Guid publicationId, CancellationToken ct) =>
            throw new NotSupportedException("the render cases never read a publication.");

        public ValueTask<AutopilotActivePublication?> FindActivePublicationAsync(Guid episodeId, CancellationToken ct) =>
            throw new NotSupportedException("the render cases never read an active publication.");

        public ValueTask<int> CountPrivateLockedRunsAsync(Guid seriesId, CancellationToken ct) =>
            throw new NotSupportedException("the render cases never count locked-private runs.");
    }
}
