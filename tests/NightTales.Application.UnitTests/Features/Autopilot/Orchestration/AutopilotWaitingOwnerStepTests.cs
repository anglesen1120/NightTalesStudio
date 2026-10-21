using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Application.Features.Rendering.Commands.RenderEpisode;
using NightTales.Application.Features.Research.Commands.GenerateFactSheet;
using NightTales.Application.Features.Stories.Commands.GenerateStory;
using NightTales.Application.Publishing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
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
/// The owner wait of a run (02.8.3 plan §3.5, column <c>waiting_for</c>): the 48 h expiry ends the run without
/// pausing the series, and each <see cref="WaitingFor"/> answer opens exactly one edge of the machine.
/// </summary>
/// <remarks>
/// The step reads the episode, its sources and its fact sheet through a real (PostgreSQL) scope — the answer it
/// detects <em>is</em> those rows — so the suite needs <c>NTS_TEST_PG</c>, like the gate tests next to it. Graph: not
/// applicable (the worktree is not indexed).
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AutopilotWaitingOwnerStepTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = AutopilotStepContexts.DefaultNow;

    /// <summary>
    /// A wait older than <c>Autopilot:Run:WaitingOwnerExpiryHours</c> (48) ends the run <c>failed</c> with
    /// <c>owner_approval_expired</c>, and the <em>series</em> is left alone: the slot's loss is recorded on the run,
    /// never as a pause and never as a <c>slot_missed</c> refusal on the series (task row + plan §3.6).
    /// </summary>
    [Fact]
    public async Task WaitingOwner_Expiry48h_EndsFailedWithoutPausingTheSeries()
    {
        var enteredAt = Now.AddHours(-48);
        var world = await SeedAsync();
        await using var db = Open(world, Now);
        var run = await AddRunAsync(db, world, enteredAt, WaitingFor.FactSheet, AutopilotRunStep.FactSheet);
        var sender = new RecordingSender();
        var step = Step(sender, db);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(WaitingOwnerStep.OwnerApprovalExpiredCode);
        run.State.ShouldBe(AutopilotRunState.Failed);
        run.Outcome.ShouldBe(AutopilotRunOutcome.Failed);
        run.FinishedAt.ShouldNotBeNull();
        sender.Sent.ShouldBeEmpty("an expired wait resumes nothing");

        world.Series.AutopilotPausedAt.ShouldBeNull("a true-crime series legitimately waits for its owner; only the run ends");
        world.Series.AutopilotPauseReason.ShouldBeNull();
    }

    /// <summary>A wait one minute short of the window is still a wait: the tick leaves the row alone.</summary>
    [Fact]
    public async Task WaitingOwner_JustInsideTheWindow_IsIdle()
    {
        var world = await SeedAsync();
        await using var db = Open(world, Now);
        var run = await AddRunAsync(db, world, Now.AddHours(-48).AddMinutes(1), WaitingFor.Story, AutopilotRunStep.Story);
        var step = Step(new RecordingSender(), db);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Idle);
        run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        // `AdvanceTo` stamps the stop outcome of the state it enters, so a run still waiting reports
        // `WaitingOwner` rather than no outcome: the plan §3.5 ladder treats that outcome as a stop, not a terminal.
        run.Outcome.ShouldBe(AutopilotRunOutcome.WaitingOwner);
    }

    /// <summary>
    /// The owner approved the sheet: the wait's answer is the story generation (key <c>…:story</c>,
    /// <c>Trigger=Autopilot</c>), the run moves to <c>story_generating</c> and one story attempt is spent.
    /// </summary>
    [Fact]
    public async Task WaitingOwner_FactSheetApproved_StartsStoryGeneration()
    {
        var world = await SeedAsync();
        await using var db = Open(world, Now);
        var run = await AddRunAsync(db, world, Now, WaitingFor.FactSheet, AutopilotRunStep.FactSheet);
        var sender = new RecordingSender();
        var step = Step(sender, db);

        await new FactSheetBuilder(Database, world.Actor)
            .CreateVersionAsync(
                world.UserId,
                world.EpisodeId,
                version: 1,
                facts: [],
                sourceAliases: new Dictionary<string, Guid>(StringComparer.Ordinal),
                status: FactSheetStatus.Approved,
                approvedBy: world.UserId,
                approvedAt: Now,
                ct: Ct);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Advanced);
        run.State.ShouldBe(AutopilotRunState.StoryGenerating);
        run.Step.ShouldBe(AutopilotRunStep.Story);
        run.Attempt.ShouldBe(1);

        var command = sender.Single<GenerateStoryCommand>();
        command.EpisodeId.ShouldBe(world.EpisodeId);
        command.Trigger.ShouldBe(GenerationTrigger.Autopilot);
        command.IdempotencyKey.ShouldBe(AutopilotIdempotencyKeys.For(run, AutopilotRunStep.Story));
    }

    /// <summary>
    /// The owner pasted a source while the wait was on the sources: the run drafts the sheet itself (the same command
    /// the owner's own button sends) and the wait is re-labelled to the sheet's approval, without leaving
    /// <c>waiting_owner</c> — the §3.5 table has no <c>waiting_owner → researching</c> edge.
    /// </summary>
    [Fact]
    public async Task WaitingOwner_SourcesAdded_DraftsFactSheetAndRelabelsTheWait()
    {
        var world = await SeedAsync(withFetchedSource: true);
        await using var db = Open(world, Now);
        var run = await AddRunAsync(db, world, Now, WaitingFor.Sources, AutopilotRunStep.SourcesRequired);
        var enteredAt = run.LastTransitionAt;
        var sender = new RecordingSender();
        var step = Step(sender, db);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now.AddMinutes(5)), Ct);

        result.Kind.ShouldBe(StepResultKind.Advanced);
        run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        run.WaitingFor.ShouldBe(WaitingFor.FactSheet);
        run.LastTransitionAt.ShouldBe(enteredAt, "the 48 h window counts from entering waiting_owner, not from the answer");

        var command = sender.Single<GenerateFactSheetCommand>();
        command.EpisodeId.ShouldBe(world.EpisodeId);
        command.IdempotencyKey.ShouldBe(AutopilotIdempotencyKeys.For(run, AutopilotRunStep.FactSheet));
    }

    /// <summary>
    /// The owner approved the story: the wait dispatches the run's single render with an <c>If-Match</c> read from the
    /// approved row, and the run moves to <c>rendering</c>.
    /// </summary>
    [Fact]
    public async Task WaitingOwner_StoryApproved_DispatchesTheSingleRender()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryApproved);
        await using var db = Open(world, Now);
        var run = await AddRunAsync(db, world, Now, WaitingFor.Story, AutopilotRunStep.Story);
        var sender = new RecordingSender();
        var step = Step(sender, db);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Advanced);
        run.State.ShouldBe(AutopilotRunState.Rendering);
        run.Step.ShouldBe(
            AutopilotRunStep.Story,
            "the step is the last completed one; `render` is written on the success edge, never at dispatch");

        var command = sender.Single<RenderEpisodeCommand>();
        command.EpisodeId.ShouldBe(world.EpisodeId);
        command.IdempotencyKey.ShouldBe(AutopilotIdempotencyKeys.For(run, AutopilotRunStep.Render));
        command.IfMatch.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A wait whose answer has not arrived answers <see cref="StepResult.Idle"/> and sends nothing: the tick must not
    /// re-dispatch, and the episode is the only signal.
    /// </summary>
    [Fact]
    public async Task WaitingOwner_WaitNotAnswered_IsIdle()
    {
        var world = await SeedAsync();
        await using var db = Open(world, Now);
        var run = await AddRunAsync(db, world, Now, WaitingFor.Story, AutopilotRunStep.Story);
        var sender = new RecordingSender();
        var step = Step(sender, db);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now.AddMinutes(30)), Ct);

        result.Kind.ShouldBe(StepResultKind.Idle);
        run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>
    /// The video answer belongs to the publication creator: the step forwards to it and returns its result unchanged,
    /// so the step itself writes no column (the creator owns the move to <c>publication_created</c>).
    /// </summary>
    [Fact]
    public async Task WaitingOwner_VideoApproved_ForwardsToThePublicationCreator()
    {
        var world = await SeedAsync(status: EpisodeStatus.Ready);
        await using var db = Open(world, Now);

        // Inside the create window of the slot (publication_create_at <= now < upload_at - min veto): the creator
        // reaches its channel guard instead of answering "not due yet".
        var now = Now.AddHours(10);
        var run = await AddRunAsync(db, world, Now, WaitingFor.Video, AutopilotRunStep.VideoGate);
        var sender = new RecordingSender();
        var step = Step(sender, db);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, now), Ct);

        // The fake reader knows no channel, so the creator refuses with its fixed code and fails the run; the point of
        // this test is that the step handed the answer over instead of driving a state itself.
        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.ChannelMissing);
        run.State.ShouldBe(AutopilotRunState.Failed);
        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>
    /// A run waiting on the video whose episode is still in a story state (CR-4, D-9): the video answer belongs to a
    /// render the owner approved, and a story-state episode has none, so the wait stays idle and sends nothing — no
    /// render, no story, no publication. The resume-render branch that used to answer here is the dead path this
    /// pins down: it re-dispatched a render for an episode that never finished its story.
    /// </summary>
    [Fact]
    public async Task WaitingOwner_VideoWaitOnAStoryStateEpisode_IsIdleAndSendsNothing()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryApproved);
        await using var db = Open(world, Now);

        // Inside the slot's create window and well inside the 48 h expiry, so neither guard decides for the step.
        var now = Now.AddHours(10);
        var run = await AddRunAsync(db, world, Now, WaitingFor.Video, AutopilotRunStep.VideoGate);
        var sender = new RecordingSender();
        var step = Step(sender, db);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, now), Ct);

        result.Kind.ShouldBe(StepResultKind.Idle);
        run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        run.WaitingFor.ShouldBe(WaitingFor.Video);
        sender.Sent.ShouldBeEmpty("a story-state episode has no video to publish and no render to resume");
    }

    /// <summary>
    /// The owner approved a story whose text trips the 03.2.2-T2 keyword lint: the render is refused on every tick, so
    /// the run ends <c>failed</c> with the render step's own diagnostic code instead of retrying for ever.
    /// </summary>
    [Fact]
    public async Task WaitingOwner_StoryApprovedButKeywordsFlagged_EndsTheRunInsteadOfRetrying()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryApproved);
        await using var db = Open(world, Now);
        var run = await AddRunAsync(db, world, Now, WaitingFor.Story, AutopilotRunStep.Story);
        var sender = new RefusingSender(ErrorCodes.KeywordsFlagged);
        var step = Step(sender, db);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.RenderKeywordsFlagged);
        run.State.ShouldBe(AutopilotRunState.Failed);
        run.Outcome.ShouldBe(AutopilotRunOutcome.Failed);
        sender.Single<RenderEpisodeCommand>().EpisodeId.ShouldBe(world.EpisodeId);
    }

    /// <summary>
    /// The owner answers a story wait after the slot is missed (the wait may last up to 48 h): the render is paid, so
    /// the answer must not dispatch it — the run ends <c>failed</c> with <c>slot_missed</c> and nothing is sent
    /// (task row SEC-8).
    /// </summary>
    [Fact]
    public async Task WaitingOwner_StoryAnsweredAfterTheSlotIsMissed_BuysNothing()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryApproved);
        await using var db = Open(world, Now);

        // The slot's cutoff is upload_at - MinVetoWindowHours, i.e. four hours before the slot: a tick one hour before
        // the slot is past it, and the wait itself is only an hour old.
        var late = AutopilotStepContexts.DefaultSlotAt.AddHours(-1);
        var run = await AddRunAsync(db, world, late.AddHours(-1), WaitingFor.Story, AutopilotRunStep.Story);
        var sender = new RecordingSender();
        var step = Step(sender, db);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, late), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotRun.SlotMissedReason);
        run.State.ShouldBe(AutopilotRunState.Failed);
        sender.Sent.ShouldBeEmpty("a slot that can no longer publish must not pay for its render");
    }

    /// <summary>
    /// The owner answers while the series is paused (§3.6): the story is paid, so the wait pauses the run with the
    /// series' reason instead of sending the command (task rows SEC-1, PF-5).
    /// </summary>
    [Fact]
    public async Task WaitingOwner_SeriesPaused_DoesNotBuyTheStory()
    {
        var world = await SeedAsync();
        await using var db = Open(world, Now);

        await using (var pause = Open(world, Now))
        {
            AutopilotSeriesSeed.Pause(pause, world.Series, "content_reported", Now);
        }

        var run = await AddRunAsync(db, world, Now, WaitingFor.FactSheet, AutopilotRunStep.FactSheet);
        var sender = new RecordingSender();
        var step = Step(sender, db);

        await new FactSheetBuilder(Database, world.Actor)
            .CreateVersionAsync(
                world.UserId,
                world.EpisodeId,
                version: 1,
                facts: [],
                sourceAliases: new Dictionary<string, Guid>(StringComparer.Ordinal),
                status: FactSheetStatus.Approved,
                approvedBy: world.UserId,
                approvedAt: Now,
                ct: Ct);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe("content_reported");
        run.State.ShouldBe(AutopilotRunState.Paused);
        run.PauseReason.ShouldBe(AutopilotPauseReason.ContentReported);
        sender.Sent.ShouldBeEmpty("a paused series buys no story, whatever the owner answered");
    }

    private static WaitingOwnerStep Step(ISender sender, AppDbContext db) =>
        new(
            sender,
            db,
            new AutopilotRunOptions { WaitingOwnerExpiryHours = 48 },
            new AutopilotPublicationCreator(
                sender,
                db,
                new YouTubeOptions(),
                NullLogger<AutopilotPublicationCreator>.Instance));

    private AppDbContext Open(World world, DateTimeOffset now) =>
        Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FixedClock(now));

    private static async Task<AutopilotRun> AddRunAsync(
        AppDbContext db,
        World world,
        DateTimeOffset enteredAt,
        WaitingFor waitingFor,
        string? step)
    {
        var run = AutopilotRun.Plan(
            world.UserId,
            world.Series.Id,
            AutopilotStepContexts.DefaultSlotAt,
            startAt: enteredAt,
            authorizationId: null,
            now: enteredAt);

        run.AttachEpisode(world.EpisodeId);
        run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, enteredAt);
        run.AdvanceTo(AutopilotRunState.Researching, step: null, enteredAt);
        run.SetWaitingFor(waitingFor);
        run.AdvanceTo(AutopilotRunState.WaitingOwner, step, enteredAt);

        db.AutopilotRuns.Add(run);
        await db.SaveChangesAsync(Ct);

        return run;
    }

    private async Task<World> SeedAsync(
        EpisodeStatus status = EpisodeStatus.Draft,
        bool withFetchedSource = false,
        Topic topic = Topic.TrueCrime)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor)
            .CreateAsync(user.Id, topic: topic, autopilotEnabled: true, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        // The step re-reads the series' authorization before every paid answer (S71 SEC-1, PF-5), so the world
        // starts with the rows 02.8.3-T3's accept route writes.
        await using (var authorize = Open(new World(actor, user.Id, series, episode.Id), Now))
        {
            await AutopilotSeriesSeed.AcceptAsync(authorize, series, Now, Ct);
        }

        if (withFetchedSource)
        {
            await using var seed = Open(new World(actor, user.Id, series, episode.Id), Now);
            var note = StorySource.CreateNote(Guid.CreateVersion7(), user.Id, episode.Id, "a pasted source", Now);
            note.IsError.ShouldBeFalse();

            seed.StorySources.Add(note.Value);
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
