using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Application.Features.Rendering.Commands.RenderEpisode;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series.Autopilot;
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
/// The <c>rendering</c> step (02.8.3 plan §3.5 rows <c>rendering → video_gate</c> and <c>rendering → failed</c>,
/// §7.2, S71-19): the run's <b>one</b> render, its own refusal taxonomy, and the episode's state as the render's
/// signal. The spend guard before the dispatch is covered by <see cref="AutopilotStepSpendGuardTests"/>.
/// </summary>
/// <remarks>
/// A real PostgreSQL scope is used for the one read the step makes through <see cref="IAppDbContext"/> (the series'
/// current authorization, task row SEC-1); the episode read is the port's fake, because the branch under test is
/// exactly what the step does with the state it reads. Graph: the worktree is not indexed.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AutopilotRenderStepTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = AutopilotStepContexts.DefaultNow;

    /// <summary>
    /// The dispatch: an approved story buys the run's single render with the run's own idempotency key and the
    /// <c>If-Match</c> of the episode as read, and consumes the attempt only once the command is accepted.
    /// </summary>
    [Fact]
    public async Task Rendering_StoryApproved_SendsTheRunsSingleRender()
    {
        var world = await SeedAsync();
        await using var db = Open(world, Now);
        var run = AutopilotRuns.WalkTo(world.Series, AutopilotRunState.Rendering, Now, world.EpisodeId);
        var reader = new FakeStepReader { Episode = Episode(world.EpisodeId, EpisodeStatus.StoryApproved, version: 7) };
        var sender = new RecordingSender();

        var step = new RenderStep(sender, reader, db, NullLogger<RenderStep>.Instance);
        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Advanced);
        result.Code.ShouldBe(AutopilotRunStep.Render);
        run.RenderAttempts.ShouldBe(1, "the accepted render consumes the run's only attempt");
        run.State.ShouldBe(AutopilotRunState.Rendering, "the render is in flight; the video gate's step moves the run on");

        var command = sender.Single<RenderEpisodeCommand>();
        command.EpisodeId.ShouldBe(world.EpisodeId);
        command.IfMatch.ShouldBe(ETags.Episode(7));
        command.IdempotencyKey.ShouldBe(AutopilotIdempotencyKeys.For(run, AutopilotRunStep.Render));
    }

    /// <summary>
    /// A duplicated tick (or a Worker restarted mid-step) never buys a second render: the attempt is already spent,
    /// so the step only reads what the episode reports.
    /// </summary>
    [Fact]
    public async Task Rendering_SecondTick_DoesNotPayTwice()
    {
        var world = await SeedAsync();
        await using var db = Open(world, Now);
        var run = AutopilotRuns.WalkTo(world.Series, AutopilotRunState.Rendering, Now, world.EpisodeId);
        run.ConsumeRenderAttempt();
        var reader = new FakeStepReader { Episode = Episode(world.EpisodeId, EpisodeStatus.Rendering) };
        var sender = new RecordingSender();

        var step = new RenderStep(sender, reader, db, NullLogger<RenderStep>.Instance);
        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Idle, "the render is in flight and nothing may be bought again");
        sender.Sent.ShouldBeEmpty();
        run.RenderAttempts.ShouldBe(1);
    }

    /// <summary>The finished render: the episode is in review, so the run moves to the video gate without a second dispatch.</summary>
    [Fact]
    public async Task Rendering_RenderFinished_MovesToVideoGate()
    {
        var world = await SeedAsync();
        await using var db = Open(world, Now);
        var run = AutopilotRuns.WalkTo(world.Series, AutopilotRunState.Rendering, Now, world.EpisodeId);
        run.ConsumeRenderAttempt();
        var reader = new FakeStepReader { Episode = Episode(world.EpisodeId, EpisodeStatus.VideoReview) };
        var sender = new RecordingSender();

        var step = new RenderStep(sender, reader, db, NullLogger<RenderStep>.Instance);
        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Advanced);
        result.Code.ShouldBe(AutopilotRunStep.Render);
        run.State.ShouldBe(AutopilotRunState.VideoGate);
        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>
    /// A render that failed puts the episode back to <c>story_approved</c>; §3.5 gives <c>rendering</c> no second
    /// attempt, so the run ends <c>failed</c> with the closed code instead of rendering again.
    /// </summary>
    [Fact]
    public async Task Rendering_RenderFailed_EndsTheRunWithoutASecondCut()
    {
        var world = await SeedAsync();
        await using var db = Open(world, Now);
        var run = AutopilotRuns.WalkTo(world.Series, AutopilotRunState.Rendering, Now, world.EpisodeId);
        run.ConsumeRenderAttempt();
        var reader = new FakeStepReader { Episode = Episode(world.EpisodeId, EpisodeStatus.StoryApproved) };
        var sender = new RecordingSender();

        var step = new RenderStep(sender, reader, db, NullLogger<RenderStep>.Instance);
        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.RenderFailed);
        run.State.ShouldBe(AutopilotRunState.Failed);
        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>
    /// The keyword lint refuses before a job exists: the run ends with the diagnostic code and the attempt is not
    /// consumed, because nothing was rendered (the owner can still fix the story and render by hand).
    /// </summary>
    [Fact]
    public async Task Rendering_KeywordLint_EndsTheRunAndKeepsTheAttempt()
    {
        var world = await SeedAsync();
        await using var db = Open(world, Now);
        var run = AutopilotRuns.WalkTo(world.Series, AutopilotRunState.Rendering, Now, world.EpisodeId);
        var reader = new FakeStepReader { Episode = Episode(world.EpisodeId, EpisodeStatus.StoryApproved) };
        var sender = new RefusingSender(ErrorCodes.KeywordsFlagged);

        var step = new RenderStep(sender, reader, db, NullLogger<RenderStep>.Instance);
        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.RenderKeywordsFlagged);
        run.State.ShouldBe(AutopilotRunState.Failed);
        run.RenderAttempts.ShouldBe(0, "a refusal before the job exists must leave the run free to render later");
    }

    /// <summary>A transient refusal retries the same render: the attempt is not consumed and the run stays in <c>rendering</c>.</summary>
    [Fact]
    public async Task Rendering_TransientRefusal_RetriesTheSameRender()
    {
        var world = await SeedAsync();
        await using var db = Open(world, Now);
        var run = AutopilotRuns.WalkTo(world.Series, AutopilotRunState.Rendering, Now, world.EpisodeId);
        var reader = new FakeStepReader { Episode = Episode(world.EpisodeId, EpisodeStatus.StoryApproved) };
        var sender = new RefusingSender(ErrorCodes.ServiceUnavailable);

        var step = new RenderStep(sender, reader, db, NullLogger<RenderStep>.Instance);
        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Retry);
        result.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
        run.RenderAttempts.ShouldBe(0);
        run.State.ShouldBe(AutopilotRunState.Rendering);
    }

    /// <summary>
    /// The missed slot outranks the render: at the cutoff the run ends <c>failed</c> with
    /// <c>pause_reason = slot_missed</c> and nothing is bought.
    /// </summary>
    [Fact]
    public async Task Rendering_MissedSlot_FailsBeforeTheRender()
    {
        var world = await SeedAsync();

        // upload_at − MinVetoWindowHours for the tests' slot and options: 20:00Z − 2 h − 2 h = 16:00Z.
        var atCutoff = AutopilotStepContexts.DefaultSlotAt.AddHours(-4);
        await using var db = Open(world, atCutoff);
        var run = AutopilotRuns.WalkTo(world.Series, AutopilotRunState.Rendering, Now, world.EpisodeId);
        var reader = new FakeStepReader { Episode = Episode(world.EpisodeId, EpisodeStatus.StoryApproved) };
        var sender = new RecordingSender();

        var step = new RenderStep(sender, reader, db, NullLogger<RenderStep>.Instance);
        var result = await step.HandleAsync(AutopilotStepContexts.For(run, world.Series, atCutoff), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.SlotMissed);
        run.State.ShouldBe(AutopilotRunState.Failed);
        run.PauseReason.ShouldBe(AutopilotPauseReason.SlotMissed);
        sender.Sent.ShouldBeEmpty();
    }

    private AppDbContext Open(World world, DateTimeOffset now) =>
        Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FixedClock(now));

    private static AutopilotRenderEpisode Episode(Guid id, EpisodeStatus status, uint version = 1) =>
        new(id, status, version, Title: "an episode", Brief: "a brief");

    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor)
            .CreateAsync(user.Id, autopilotEnabled: true, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        var world = new World(actor, user.Id, series, episode.Id);

        await using var seed = Open(world, Now);

        // The render is the run's most expensive dispatch, so the step re-reads the series' authorization first
        // (S71 SEC-1): the world starts with the rows the accept route writes.
        await AutopilotSeriesSeed.AcceptAsync(seed, series, Now, Ct);

        return world;
    }

    private sealed record World(TestCurrentUser Actor, Guid UserId, SeriesAggregate Series, Guid EpisodeId);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
