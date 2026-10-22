using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Application.Features.Research.Commands.GenerateFactSheet;
using NightTales.Application.Features.Stories.Commands.GenerateStory;
using NightTales.Domain.Common;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
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

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration;

/// <summary>
/// The research step of a true-crime run (02.8.3 plan §3.5 rows <c>researching → waiting_owner</c>, F33): the
/// source finder's "go" branch drafts the fact sheet and stops for the owner, and the default branch — no
/// <c>source_finder</c> runner, so no go (02.7.2-T3 does not exist) — waits for the owner's sources instead.
/// </summary>
/// <remarks>
/// The step reads and writes through a real (PostgreSQL) scope because the branch it takes is the episode's sources
/// and fact sheet, and a fake of that read would only restate the step. The suite needs <c>NTS_TEST_PG</c>, like the
/// gate tests next to it. Graph: not applicable (the worktree is not indexed).
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AutopilotResearchStepTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = AutopilotStepContexts.DefaultNow;

    /// <summary>
    /// F33's default: with no finder "go" the step creates <b>no</b> <c>find_sources</c> run and parks the run in
    /// <c>waiting_owner</c> with <see cref="WaitingFor.Sources"/>, which is the owner's "add sources" ask.
    /// </summary>
    [Fact]
    public async Task TrueCrimeRun_NoFinderGo_WaitsForSources()
    {
        // No readable source: a pasted note is stored `Fetched`, so seeding one would draft the sheet instead of
        // reaching the finder's no-go branch this test is about.
        var world = await SeedAsync(withSource: false);
        await using var db = Open(world, Now);
        var run = await AddRunAsync(db, world);
        var sender = new RecordingSender();

        var step = new ResearchStep(sender, db, new FixedSourceFinderGo(hasGo: false));
        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        // The owner ask is a stop, not an advance: `StopForSources` records the wait and ends the tick.
        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotRunStep.SourcesRequired);
        run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        run.WaitingFor.ShouldBe(WaitingFor.Sources);
        run.Step.ShouldBe(AutopilotRunStep.SourcesRequired);
        sender.Sent.ShouldBeEmpty("the no-go branch never asks the finder, so no find_sources generation run exists");
    }

    /// <summary>
    /// The go branch: with a fetched source but no fact sheet the step drafts the sheet (key <c>…:fact_sheet</c>)
    /// and leaves the run in <c>researching</c>; once the sheet is drafted it stops at <c>waiting_owner</c> for the
    /// fact sheet, and never starts the story — true crime never auto-approves the sheet (F33).
    /// </summary>
    [Fact]
    public async Task TrueCrimeRun_StopsAtFactSheetWaitingOwner()
    {
        var world = await SeedAsync();
        await using var db = Open(world, Now);
        var run = await AddRunAsync(db, world);
        var sender = new RecordingSender();

        var step = new ResearchStep(sender, db, new FixedSourceFinderGo(hasGo: true));
        var drafting = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        drafting.Kind.ShouldBe(StepResultKind.Idle, "the sheet generation is in flight; the wait starts when it lands");
        run.State.ShouldBe(AutopilotRunState.Researching);
        var command = sender.Single<GenerateFactSheetCommand>();
        command.EpisodeId.ShouldBe(world.EpisodeId);
        command.IdempotencyKey.ShouldBe(AutopilotIdempotencyKeys.For(run, AutopilotRunStep.FactSheet));
        command.Trigger.ShouldBe(GenerationTrigger.Autopilot, "an autopilot sheet row must not claim a user trigger (S71-6)");

        await new FactSheetBuilder(Database, world.Actor).CreateAsync(world.UserId, world.EpisodeId, Ct);

        var awaiting = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        awaiting.Kind.ShouldBe(StepResultKind.Stopped);
        awaiting.Code.ShouldBe(AutopilotRunStep.FactSheet);
        run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        run.WaitingFor.ShouldBe(WaitingFor.FactSheet);
        sender.SentAny<GenerateStoryCommand>()
            .ShouldBeFalse("true crime stops for the owner's approval of the sheet before any story is written");
    }

    private AppDbContext Open(World world, DateTimeOffset now) =>
        Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FixedClock(now));

    private static async Task<AutopilotRun> AddRunAsync(AppDbContext db, World world)
    {
        var run = AutopilotRun.Plan(
            world.UserId,
            world.Series.Id,
            AutopilotStepContexts.DefaultSlotAt,
            startAt: Now,
            authorizationId: null,
            now: Now);

        run.AttachEpisode(world.EpisodeId);
        run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
        run.AdvanceTo(AutopilotRunState.Researching, step: null, Now);

        db.AutopilotRuns.Add(run);
        await db.SaveChangesAsync(Ct);

        return run;
    }

    private async Task<World> SeedAsync(Topic topic = Topic.TrueCrime, bool withSource = true)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor)
            .CreateAsync(user.Id, topic: topic, autopilotEnabled: true, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        await using (var seed = Open(new World(actor, user.Id, series, episode.Id), Now))
        {
            // Both dispatches of the research state are paid, so the step re-reads the series' authorization first
            // (S71 SEC-1, PF-5): the world starts with the rows 02.8.3-T3's accept route writes.
            await AutopilotSeriesSeed.AcceptAsync(seed, series, Now, Ct);

            if (withSource)
            {
                var note = StorySource.CreateNote(Guid.CreateVersion7(), user.Id, episode.Id, "a pasted source", Now);
                note.IsError.ShouldBeFalse();

                seed.StorySources.Add(note.Value);
            }

            await seed.SaveChangesAsync(Ct);
        }

        return new World(actor, user.Id, series, episode.Id);
    }

    private sealed record World(TestCurrentUser Actor, Guid UserId, SeriesAggregate Series, Guid EpisodeId);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
