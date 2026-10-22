using System.Globalization;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Application.Publishing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories.Generation;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The fiction path of one autopilot slot on a real database (02.8.3-T1 §Tests, "Integration"): a planned run is
/// driven by the real <c>AutopilotRunOrchestrator</c>, the real step handlers and the real outbox handlers through
/// idea → episode → story → story gate → render → video gate → publication → veto window → scheduled → published,
/// and the same machine ends a slot <c>gate_failed</c>, <c>failed</c> (<c>spend_cap_reached</c>,
/// <c>owner_approval_expired</c>, <c>slot_missed</c>) or <c>waiting_owner</c> where the plan says it must.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real and what is a seam.</b> The subject is the run state machine, so everything it decides is
/// production code: the slot planner's run row, the idea step's ledger and price catalogue, <c>create_episode</c>,
/// the story dispatch, the story gate step, the render dispatch, the video gate step, the publication create and the
/// <c>publication_updated</c> steps. Three things belong to other builds and are answered here exactly where their
/// own commits would land:
/// </para>
/// <list type="number">
/// <item><description>
/// the story writer job (02.2.2) — <see cref="LandStoryAsync"/> writes the version and calls the same
/// <c>Episode.CompleteStoryGeneration</c> the pipeline's <c>StoryGenerationProgress.CompleteAsync</c> calls, so the
/// <c>story_generated.v1</c> row and the episode move are the product's;
/// </description></item>
/// <item><description>
/// the render job (09.6) — <see cref="LandRenderAsync"/> calls <c>Episode.RenderSucceeded</c>, the transition the
/// render ingest makes, so <c>render_succeeded.v1</c> and the episode move are the product's;
/// </description></item>
/// <item><description>
/// the consent job (06.2.2-T3) and the go-live write (06.2.5) — <see cref="ConsentAsync"/> calls the very
/// <c>Publication.ConsentFromAuthorization</c> the job will call, and <see cref="GoLiveAsync"/> writes the
/// <c>published</c> row the upload job will write.
/// </description></item>
/// </list>
/// <para>
/// <b>The gate is scripted, its decision is not.</b> <see cref="ScriptedFictionGate"/> answers the verdict (the
/// LLM checks of 02.8.2 are covered by their own tests) and performs the approval the real gate performs through
/// <c>ApproveStoryCommand</c>/<c>ApproveVideoCommand</c>: <c>Episode.ApproveStory</c> and
/// <c>Episode.ApproveVideo</c>. The step's mapping of that verdict onto the machine — the regeneration, the
/// <c>gate_failed</c> ending, the owner wait — is what every case here asserts.
/// </para>
/// <para>
/// Every instant comes from the harness's <see cref="FakeClock"/>; nothing sleeps and nothing polls.
/// </para>
/// </remarks>
public sealed class AutopilotFictionRunTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The Monday a cadence of Mondays+Thursdays is planned from (07:00 in Ho Chi Minh City).</summary>
    private static readonly DateTimeOffset Planned = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Plan §3.4's worked example: Thursday 2026-10-08 19:00 in Ho Chi Minh City, in UTC.</summary>
    private static readonly DateTimeOffset WorkedSlot = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The worked example's <c>start_at</c>: two buffer days before the slot.</summary>
    private static readonly DateTimeOffset WorkedStart = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The worked example's <c>publication_create_at</c>.</summary>
    private static readonly DateTimeOffset WorkedPublicationCreate = new(2026, 10, 7, 22, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FictionRun_EndToEnd_ReachesPublished()
    {
        var gate = new ScriptedFictionGate(Database);
        await using var harness = await StartAsync(services =>
        {
            ReplaceGate(services, gate);

            // The acceptance's terminal pair is the approved channel's: the harness's host otherwise carries the
            // production default (`pending`), which locks the video private (plan §3.5's pre-audit branch).
            services.Configure<YouTubeOptions>(options => options.AuditStatus = YouTubeAuditStatuses.Approved);
        });

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]),
            ct: Ct);

        // The planner writes the slot's run: the worked example's instants, byte for byte.
        await harness.TickAsync(Ct);

        var planned = await harness.RunsAsync(owner, world.SeriesId, Ct);
        planned.Count.ShouldBe(1, "one cadence day in the horizon is one slot");
        var run = planned[0];

        run.SlotAt.ShouldBe(WorkedSlot);
        run.StartAt.ShouldBe(WorkedStart);
        run.State.ShouldBe(AutopilotRunState.Planned);
        run.Outcome.ShouldBeNull();
        run.FinishedAt.ShouldBeNull();

        var timing = AutopilotTiming.Compute(run.SlotAt, world.Settings, TimingOptions(), WorkedSlot.AddDays(-3));
        timing.PublicationCreateAt.ShouldBe(WorkedPublicationCreate, "plan §3.4's worked example");
        timing.UploadAt.ShouldBe(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));

        // Nothing runs before start_at (plan §3.4).
        var early = await harness.AdvanceAsync(run.Id, owner, Ct);
        early.IsError.ShouldBeFalse();
        early.Value.Kind.ShouldBe(StepResultKind.Idle);
        (await harness.RunAsync(owner, run.Id, Ct)).State.ShouldBe(AutopilotRunState.Planned);

        // idea → episode → story. The clock stands on start_at for the whole production window.
        harness.Clock.Set(WorkedStart);

        (await harness.AdvanceAsync(run.Id, owner, Ct)).Value.Kind.ShouldBe(StepResultKind.Advanced);
        (await harness.AdvanceAsync(run.Id, owner, Ct)).Value.Kind.ShouldBe(StepResultKind.Advanced);

        var generating = await harness.RunAsync(owner, run.Id, Ct);
        generating.State.ShouldBe(AutopilotRunState.StoryGenerating);
        generating.Step.ShouldBe(AutopilotRunStep.Story);
        generating.Attempt.ShouldBe(1);
        generating.EpisodeId.ShouldNotBeNull();

        var episodeId = generating.EpisodeId!.Value;

        // The story lands; the outbox delivers `story_generated.v1`, which advances the run and lets the gate decide.
        var storyVersion = await LandStoryAsync(owner, world.SeriesId, run.SlotAt, episodeId, version: 1, AutopilotRunStep.Story);

        // The episode's own transition wrote `story_generated.v1`; its delivery (or, failing that, the next tick's
        // read of the episode) advances the run through the gate.
        (await harness.Host.RunOutboxOnceAsync(Ct)).ShouldBeGreaterThan(0, "the story landing wrote its event");

        var gated = await DriveUntilAsync(harness, run.Id, owner, AutopilotRunState.Rendering);
        gated.State.ShouldBe(AutopilotRunState.Rendering, "the gate auto-approved and the render step owns the run");
        gated.Step.ShouldBe(AutopilotRunStep.StoryGate);
        gate.StoryCalls.ShouldBe(1, "one verdict per attempt");

        var episode = await LoadEpisodeAsync(owner, episodeId);
        episode.Status.ShouldBe(EpisodeStatus.Rendering, "StoryGateStep dispatched RenderEpisodeCommand");
        episode.ApprovedStoryVersionId.ShouldBe(storyVersion);

        // The render lands; `render_succeeded.v1` moves the run to the video gate.
        await LandRenderAsync(owner, episodeId);
        (await harness.Host.RunOutboxOnceAsync(Ct)).ShouldBeGreaterThan(0, "the render landing wrote its event");
        (await DriveUntilAsync(harness, run.Id, owner, AutopilotRunState.VideoGate)).State
            .ShouldBe(AutopilotRunState.VideoGate);

        // The publication may only be created inside its window; at `publication_create_at` the gate approves and
        // the creator writes the row the owner's route would write.
        harness.Clock.Set(WorkedPublicationCreate);
        var created = await DriveUntilAsync(harness, run.Id, owner, AutopilotRunState.PublicationCreated);
        created.State.ShouldBe(AutopilotRunState.PublicationCreated);
        created.Step.ShouldBe(AutopilotRunStep.Publication);
        created.PublicationId.ShouldNotBeNull();

        var publicationId = created.PublicationId!.Value;
        (await harness.RunAsync(owner, run.Id, Ct)).Outcome.ShouldBeNull("a producing run carries no outcome");

        // The veto notice is delivered (06.2.2-T2's write), which opens the veto window.
        await harness.Database.ExecuteAsync(
            $"UPDATE publications SET veto_notice_sent_at = '{Instant(harness.Clock.UtcNow)}' WHERE id = '{publicationId}'",
            Ct);
        (await DriveUntilAsync(harness, run.Id, owner, AutopilotRunState.VetoWindow)).State
            .ShouldBe(AutopilotRunState.VetoWindow);

        // 06.2.2-T3's consent write closes the window; `publication_updated.v1` takes the run to scheduled.
        await ConsentAsync(owner, publicationId, world.AuthorizationId!.Value, harness.Clock.UtcNow);
        (await harness.Host.RunOutboxOnceAsync(Ct)).ShouldBeGreaterThan(0, "the consent write updated the row");
        var scheduled = await DriveUntilAsync(harness, run.Id, owner, AutopilotRunState.Scheduled);
        scheduled.State.ShouldBe(AutopilotRunState.Scheduled);
        scheduled.Step.ShouldBe(AutopilotRunStep.Consent);

        // 06.2.5's go-live write; the run follows the row and ends published.
        await GoLiveAsync(publicationId, harness.Clock.UtcNow);
        // The acceptance's terminal pair: the channel is past YouTube's audit (`configureServices` sets
        // `YouTube:AuditStatus` approved, applied last so it beats the binder), so the slot ends `published` —
        // plan §3.5's pre-audit branch would end it `published_private_locked`.
        var published = await DriveUntilAsync(harness, run.Id, owner, AutopilotRunState.Published);
        published.State.ShouldBe(AutopilotRunState.Published);
        published.Outcome.ShouldBe(AutopilotRunOutcome.Published);
        published.FinishedAt.ShouldNotBeNull();
        published.FinishedAt!.Value.ShouldBe(harness.Clock.UtcNow);
        published.PauseReason.ShouldBeNull();

        // Every paid step of the run carries the plan's key, composed by the product's own composer. The series
        // also carries one paid row the slot did not dispatch — the series bible's proposal, written when the
        // series started — so the autopilot rows are asserted as a set and the rest must be that proposal.
        var rows = await harness.GenerationRunKeysAsync(world.SeriesId, Ct);
        var paid = rows
            .Where(key => key.StartsWith("autopilot:", StringComparison.Ordinal))
            .ToList();
        paid.Count.ShouldBe(2, "the ideas call and the one story this run generated; rows: " + string.Join(" | ", rows));
        paid.ShouldContain(AutopilotIdempotencyKeys.For(world.SeriesId, run.SlotAt, AutopilotRunStep.Idea));
        paid.ShouldContain(AutopilotIdempotencyKeys.For(world.SeriesId, run.SlotAt, AutopilotRunStep.Story));

        rows.Where(key => !key.StartsWith("autopilot:", StringComparison.Ordinal))
            .ShouldAllBe(key => key.StartsWith("bible:", StringComparison.Ordinal));

        var slotText = run.SlotAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        foreach (var key in paid)
        {
            key.ShouldStartWith($"autopilot:{world.SeriesId}:{slotText}:");
        }

        // A terminal run is never advanced again, and the series was never paused by the fiction path.
        var after = await harness.AdvanceAsync(run.Id, owner, Ct);
        after.Value.Kind.ShouldBe(StepResultKind.Idle);
        (await SeriesIsPausedAsync(world.SeriesId)).ShouldBeFalse();
    }

    [Fact]
    public async Task FictionRun_GateFailure_RegeneratesOnce_ThenGateFailed()
    {
        var gate = new ScriptedFictionGate(Database) { StoryVerdict = StoryVerdict.FailedChecks };
        await using var harness = await StartAsync(services => ReplaceGate(services, gate));

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]),
            ct: Ct);

        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: WorkedSlot,
            startAt: WorkedStart,
            authorizationId: world.AuthorizationId,
            ct: Ct);

        harness.Clock.Set(WorkedStart);
        await harness.AdvanceAsync(run.Id, owner, Ct);
        await harness.AdvanceAsync(run.Id, owner, Ct);

        var generating = await harness.RunAsync(owner, run.Id, Ct);
        generating.State.ShouldBe(AutopilotRunState.StoryGenerating);
        var episodeId = generating.EpisodeId!.Value;

        // Attempt 1 lands, the gate fails it: the run's one regeneration is dispatched.
        await LandStoryAsync(owner, world.SeriesId, run.SlotAt, episodeId, version: 1, AutopilotRunStep.Story);
        await harness.AdvanceAsync(run.Id, owner, Ct);   // story_generating -> story_gate
        await harness.AdvanceAsync(run.Id, owner, Ct);   // the gate's verdict

        var regenerating = await harness.RunAsync(owner, run.Id, Ct);
        regenerating.State.ShouldBe(AutopilotRunState.StoryGenerating);
        regenerating.Attempt.ShouldBe(2, "the single regeneration spent the second attempt");
        gate.StoryCalls.ShouldBe(1);

        // The gate's failure only spends the attempt; the regeneration itself is the next step's dispatch.
        await harness.AdvanceAsync(run.Id, owner, Ct);

        var episode = await LoadEpisodeAsync(owner, episodeId);
        episode.Status.ShouldBe(EpisodeStatus.StoryGenerating, "GenerateStoryCommand put the episode back to work");

        // Attempt 2 lands too; the gate fails it again, and the slot ends instead of buying a third story.
        await LandStoryAsync(owner, world.SeriesId, run.SlotAt, episodeId, version: 2, AutopilotRunStep.Story2);
        await harness.AdvanceAsync(run.Id, owner, Ct);   // story_generating -> story_gate
        await harness.AdvanceAsync(run.Id, owner, Ct);   // the gate's second verdict ends the slot

        var failed = await harness.RunAsync(owner, run.Id, Ct);
        failed.State.ShouldBe(AutopilotRunState.GateFailed);
        failed.Outcome.ShouldBe(AutopilotRunOutcome.GateFailed);
        failed.Attempt.ShouldBe(2);
        failed.FinishedAt.ShouldNotBeNull();
        gate.StoryCalls.ShouldBe(2, "two attempts, two verdicts");

        var keys = await harness.GenerationRunKeysAsync(world.SeriesId, Ct);
        keys.Count.ShouldBe(3, "idea, story and the one regeneration — never a third");
        keys.ShouldContain(AutopilotIdempotencyKeys.For(world.SeriesId, run.SlotAt, AutopilotRunStep.Idea));
        keys.ShouldContain(AutopilotIdempotencyKeys.For(world.SeriesId, run.SlotAt, AutopilotRunStep.Story));
        keys.ShouldContain(AutopilotIdempotencyKeys.For(world.SeriesId, run.SlotAt, AutopilotRunStep.Story2));

        // A terminal run does not dispatch anything else, however often the tick looks at it.
        await harness.AdvanceAsync(run.Id, owner, Ct);
        gate.StoryCalls.ShouldBe(2);
        (await harness.CountGenerationRunsAsync(world.SeriesId, Ct)).ShouldBe(3);
        (await SeriesIsPausedAsync(world.SeriesId)).ShouldBeFalse("T4 owns the gate-failure pause");
    }

    [Fact]
    public async Task WaitingOwner_Expiry48h_EndsFailedWithoutPausingTheSeries()
    {
        await using var harness = await StartAsync();

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]),
            ct: Ct);

        // The wait is on the story: the run carries the episode whose story it waits for, or the step reports
        // `episode_missing` before the expiry is ever measured.
        var episodeId = await harness.CreateEpisodeAsync(owner, world.SeriesId, EpisodeStatus.StoryReview, Ct);

        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: WorkedSlot,
            startAt: WorkedStart,
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.WaitingOwner,
            step: AutopilotRunStep.StoryGate,
            episodeId: episodeId,
            attempt: 1,
            ct: Ct);
        await harness.Database.ExecuteAsync(
            $"UPDATE autopilot_runs SET waiting_for = 'story' WHERE id = '{run.Id}'",
            Ct);

        // One hour short of the window: the wait is still a wait.
        harness.Clock.Advance(TimeSpan.FromHours(47));
        await harness.AdvanceAsync(run.Id, owner, Ct);
        (await harness.RunAsync(owner, run.Id, Ct)).State.ShouldBe(AutopilotRunState.WaitingOwner);

        // 48 h: the owner's silence ends the slot, and only the slot.
        harness.Clock.Advance(TimeSpan.FromHours(1));
        await harness.TickAsync(Ct);

        var expired = await harness.RunAsync(owner, run.Id, Ct);
        expired.State.ShouldBe(AutopilotRunState.Failed);
        expired.Outcome.ShouldBe(AutopilotRunOutcome.Failed);
        expired.FinishedAt.ShouldNotBeNull();
        expired.PauseReason.ShouldBeNull("an expired owner wait is not a slot_missed failure");

        (await SeriesIsPausedAsync(world.SeriesId)).ShouldBeFalse("a series legitimately waits for its owner");
    }

    [Fact]
    public async Task FictionRun_SpendCapRefusal_EndsFailedWithoutPausingTheSeries()
    {
        await using var harness = await StartAsync(services =>
        {
            services.RemoveAll<IBudgetGuard>();
            services.AddSingleton<IBudgetGuard, RefusingBudgetGuard>();
        });

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]),
            ct: Ct);

        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: WorkedSlot,
            startAt: WorkedStart,
            authorizationId: world.AuthorizationId,
            ct: Ct);

        harness.Clock.Set(WorkedStart);
        var refused = await harness.AdvanceAsync(run.Id, owner, Ct);

        refused.IsError.ShouldBeFalse();
        refused.Value.Kind.ShouldBe(StepResultKind.Stopped);
        refused.Value.Code.ShouldBe("spend_cap_reached");

        var failed = await harness.RunAsync(owner, run.Id, Ct);
        failed.State.ShouldBe(AutopilotRunState.Failed);
        failed.Outcome.ShouldBe(AutopilotRunOutcome.Failed);
        failed.PauseReason.ShouldBeNull("the run ends; pausing the series is 02.8.3-T4's trigger 2");
        failed.FinishedAt.ShouldNotBeNull();

        (await harness.CountGenerationRunsAsync(world.SeriesId, Ct))
            .ShouldBe(0, "a refused dispatch pays nothing");
        (await SeriesIsPausedAsync(world.SeriesId)).ShouldBeFalse();
    }

    [Fact]
    public async Task FictionRun_IdlePastTheCutoff_EndsFailedWithSlotMissed()
    {
        await using var harness = await StartAsync();

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]),
            ct: Ct);

        // The run was planned for the worked slot but nothing picked it up: by now `upload_at - 2 h` has passed.
        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: WorkedSlot,
            startAt: WorkedStart,
            authorizationId: world.AuthorizationId,
            ct: Ct);

        harness.Clock.Set(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
        await harness.AdvanceAsync(run.Id, owner, Ct);

        var missed = await harness.RunAsync(owner, run.Id, Ct);
        missed.State.ShouldBe(AutopilotRunState.Failed);
        missed.Outcome.ShouldBe(AutopilotRunOutcome.Failed);
        missed.PauseReason.ShouldBe(AutopilotPauseReason.SlotMissed, "plan §3.4/§3.6 row 13");
        missed.FinishedAt.ShouldNotBeNull();
        (await harness.CountGenerationRunsAsync(world.SeriesId, Ct)).ShouldBe(0, "a missed slot pays nothing");
        (await SeriesIsPausedAsync(world.SeriesId)).ShouldBeFalse();
    }

    [Fact]
    public async Task FictionRun_TwoOwners_AdvanceIndependently()
    {
        var gate = new ScriptedFictionGate(Database);
        await using var harness = await StartAsync(services => ReplaceGate(services, gate));

        var first = await harness.CreateOwnerAsync(Ct);
        var second = await harness.CreateOwnerAsync(Ct);
        var firstWorld = await harness.CreateAuthorizedSeriesAsync(
            first,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]),
            ct: Ct);
        var secondWorld = await harness.CreateAuthorizedSeriesAsync(
            second,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]),
            ct: Ct);

        var firstRun = await harness.CreateRunAsync(
            first, firstWorld.SeriesId, WorkedSlot, WorkedStart, firstWorld.AuthorizationId, ct: Ct);
        var secondRun = await harness.CreateRunAsync(
            second, secondWorld.SeriesId, WorkedSlot, WorkedStart, secondWorld.AuthorizationId, ct: Ct);

        harness.Clock.Set(WorkedStart);

        // Two ticks of two different owners run concurrently: each reads only its own world and pays once.
        var results = await Task.WhenAll(
            Task.Run(() => harness.AdvanceAsync(firstRun.Id, first, Ct), Ct),
            Task.Run(() => harness.AdvanceAsync(secondRun.Id, second, Ct), Ct));
        results.ShouldAllBe(result => !result.IsError);
        results.ShouldAllBe(result => result.Value.Kind == StepResultKind.Advanced);

        var firstStored = await harness.RunAsync(first, firstRun.Id, Ct);
        var secondStored = await harness.RunAsync(second, secondRun.Id, Ct);
        firstStored.State.ShouldBe(AutopilotRunState.IdeaPicked);
        secondStored.State.ShouldBe(AutopilotRunState.IdeaPicked);

        (await harness.RunsAsync(first, firstWorld.SeriesId, Ct)).Count.ShouldBe(1);
        (await harness.RunsAsync(second, secondWorld.SeriesId, Ct)).Count.ShouldBe(1);
        (await harness.GenerationRunKeysAsync(firstWorld.SeriesId, Ct))
            .ShouldBe([AutopilotIdempotencyKeys.For(firstWorld.SeriesId, WorkedSlot, AutopilotRunStep.Idea)]);
        (await harness.GenerationRunKeysAsync(secondWorld.SeriesId, Ct))
            .ShouldBe([AutopilotIdempotencyKeys.For(secondWorld.SeriesId, WorkedSlot, AutopilotRunStep.Idea)]);

        // A run of one owner is invisible to the other: the second owner's advance cannot touch the first series.
        var foreign = await harness.AdvanceAsync(firstRun.Id, second, Ct);
        foreign.IsError.ShouldBeTrue();
        foreign.FirstError.Code.ShouldBe(AutopilotRunOrchestrator.RunNotFoundCode);
        (await harness.RunAsync(first, firstRun.Id, Ct)).State.ShouldBe(AutopilotRunState.IdeaPicked);
    }

    [Fact]
    public async Task Run_RenderFailed_EndsFailedRenderFailed()
    {
        await using var harness = await StartAsync();

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, ct: Ct);
        var episodeId = await harness.CreateEpisodeAsync(owner, world.SeriesId, EpisodeStatus.StoryApproved, Ct);

        // The run dispatched its one render; the render failed and the episode is back in `story_approved`.
        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: WorkedSlot,
            startAt: WorkedStart,
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.Rendering,
            step: AutopilotRunStep.StoryGate,
            episodeId: episodeId,
            attempt: 1,
            renderAttempts: 1,
            ct: Ct);

        harness.Clock.Set(WorkedStart);
        await harness.AdvanceAsync(run.Id, owner, Ct);

        var failed = await harness.RunAsync(owner, run.Id, Ct);
        failed.State.ShouldBe(AutopilotRunState.Failed);
        failed.Outcome.ShouldBe(AutopilotRunOutcome.Failed);
        failed.FinishedAt.ShouldNotBeNull();
        failed.PauseReason.ShouldBeNull("render_failed travels in the log and the metric, not on the row");
    }

    [Fact]
    public async Task Run_VideoGateDryRun_WaitsForOwner()
    {
        var gate = new ScriptedFictionGate(Database) { VideoVerdict = VideoVerdict.DryRun };
        await using var harness = await StartAsync(services => ReplaceGate(services, gate));

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, ct: Ct);
        var episodeId = await harness.CreateEpisodeAsync(owner, world.SeriesId, EpisodeStatus.VideoReview, Ct);

        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: WorkedSlot,
            startAt: WorkedStart,
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.VideoGate,
            step: AutopilotRunStep.VideoGate,
            episodeId: episodeId,
            attempt: 1,
            renderAttempts: 1,
            ct: Ct);

        harness.Clock.Set(WorkedStart);
        var result = await harness.AdvanceAsync(run.Id, owner, Ct);

        result.Value.Kind.ShouldBe(StepResultKind.Stopped);
        var waiting = await harness.RunAsync(owner, run.Id, Ct);
        waiting.State.ShouldBe(AutopilotRunState.WaitingOwner);
        waiting.WaitingFor.ShouldBe(WaitingFor.Video);
        waiting.Outcome.ShouldNotBeNull("a stop carries its outcome; only finished_at is reserved for terminals");
        waiting.FinishedAt.ShouldBeNull();
        waiting.PublicationId.ShouldBeNull("a dry run publishes nothing");
        (await LoadEpisodeAsync(owner, episodeId)).Status.ShouldBe(EpisodeStatus.VideoReview);
    }

    [Fact]
    public async Task Run_VideoGateLive_Fail_EndsGateFailed()
    {
        var gate = new ScriptedFictionGate(Database) { VideoVerdict = VideoVerdict.FailedChecks };
        await using var harness = await StartAsync(services => ReplaceGate(services, gate));

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, ct: Ct);
        var episodeId = await harness.CreateEpisodeAsync(owner, world.SeriesId, EpisodeStatus.VideoReview, Ct);

        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: WorkedSlot,
            startAt: WorkedStart,
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.VideoGate,
            step: AutopilotRunStep.VideoGate,
            episodeId: episodeId,
            attempt: 1,
            renderAttempts: 1,
            ct: Ct);

        harness.Clock.Set(WorkedStart);
        await harness.AdvanceAsync(run.Id, owner, Ct);

        var failed = await harness.RunAsync(owner, run.Id, Ct);
        failed.State.ShouldBe(AutopilotRunState.GateFailed);
        failed.Outcome.ShouldBe(AutopilotRunOutcome.GateFailed);
        failed.FinishedAt.ShouldNotBeNull();
        failed.PublicationId.ShouldBeNull("a failed cut never reaches the publication step");
    }

    [Fact]
    public async Task Run_OwnerApprovesVideo_CreatesPublicationWhenTimingAllows()
    {
        var gate = new ScriptedFictionGate(Database);
        await using var harness = await StartAsync(services => ReplaceGate(services, gate));

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, ct: Ct);
        var episodeId = await harness.CreateEpisodeAsync(owner, world.SeriesId, EpisodeStatus.VideoReview, Ct);

        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: WorkedSlot,
            startAt: WorkedStart,
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.VideoGate,
            step: AutopilotRunStep.VideoGate,
            episodeId: episodeId,
            attempt: 1,
            renderAttempts: 1,
            ct: Ct);

        // Before the create window opens nothing is created and nothing is spent.
        harness.Clock.Set(WorkedPublicationCreate.AddHours(-1));
        await harness.AdvanceAsync(run.Id, owner, Ct);
        (await harness.RunAsync(owner, run.Id, Ct)).State.ShouldBe(AutopilotRunState.VideoGate);

        // Inside the window the pass creates the episode's publication and moves the run.
        harness.Clock.Set(WorkedPublicationCreate);
        var created = await DriveUntilAsync(harness, run.Id, owner, AutopilotRunState.PublicationCreated);
        created.State.ShouldBe(AutopilotRunState.PublicationCreated);
        created.Step.ShouldBe(AutopilotRunStep.Publication);
        created.PublicationId.ShouldNotBeNull();

        var publication = await LoadPublicationAsync(owner, created.PublicationId!.Value);
        publication.Status.ShouldBe(PublicationStatus.AwaitingConsent);
        publication.EpisodeId.ShouldBe(episodeId);
        publication.AutopilotAuthorizationId.ShouldBe(world.AuthorizationId);
        publication.PublishAt.ShouldBe(WorkedSlot);

        (await LoadEpisodeAsync(owner, episodeId)).Status.ShouldBe(EpisodeStatus.Ready);
    }

    /// <summary>
    /// A replay of a stop verdict the gate already committed — a dry run, whose notice went out with the verdict —
    /// must not be read as an approval: the run moves to <c>waiting_owner</c> and publishes nothing (S71 SEC-3,
    /// CR-5: the stored <c>video</c> block exists for every verdict, so key presence is not approval).
    /// </summary>
    [Fact]
    public async Task Run_VideoGateReplayedDryRunVerdict_KeepsWaitingWithoutPublishing()
    {
        await using var harness = await StartAsync(services => ReplaceGate(services, new UnreachableGate()));

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, ct: Ct);
        var episodeId = await harness.CreateEpisodeAsync(owner, world.SeriesId, EpisodeStatus.VideoReview, Ct);

        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: WorkedSlot,
            startAt: WorkedStart,
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.VideoGate,
            step: AutopilotRunStep.VideoGate,
            episodeId: episodeId,
            attempt: 1,
            renderAttempts: 1,
            ct: Ct);
        await WriteGateResultsAsync(run.Id, mode: "dry_run", pass: true);

        // Inside the create window: a step that read the stored block as an approval would create the publication here.
        harness.Clock.Set(WorkedPublicationCreate);
        var result = await harness.AdvanceAsync(run.Id, owner, Ct);

        result.IsError.ShouldBeFalse(
            result.IsError ? string.Join(", ", result.Errors.Select(error => error.Code)) : "step");
        result.Value.Kind.ShouldBe(StepResultKind.Stopped);
        result.Value.Code.ShouldBe(OwnerActionReasons.DryRun);

        var waiting = await harness.RunAsync(owner, run.Id, Ct);
        waiting.State.ShouldBe(AutopilotRunState.WaitingOwner);
        waiting.WaitingFor.ShouldBe(WaitingFor.Video);
        waiting.PublicationId.ShouldBeNull("a dry run publishes nothing");
        (await PublicationsOfAsync(episodeId)).ShouldBeEmpty();
        (await LoadEpisodeAsync(owner, episodeId)).Status.ShouldBe(EpisodeStatus.VideoReview);
    }

    /// <summary>
    /// A replay of a live pass the gate already approved goes on to the create — without evaluating the gate a second
    /// time — and creates exactly one publication (S71 SEC-3).
    /// </summary>
    [Fact]
    public async Task Run_VideoGateReplayedApprovedVerdict_CreatesThePublicationOnce()
    {
        await using var harness = await StartAsync(services => ReplaceGate(services, new UnreachableGate()));

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, ct: Ct);
        var episodeId = await harness.CreateEpisodeAsync(owner, world.SeriesId, EpisodeStatus.Ready, Ct);

        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: WorkedSlot,
            startAt: WorkedStart,
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.VideoGate,
            step: AutopilotRunStep.VideoGate,
            episodeId: episodeId,
            attempt: 1,
            renderAttempts: 1,
            ct: Ct);
        await WriteGateResultsAsync(run.Id, mode: "live", pass: true);

        harness.Clock.Set(WorkedPublicationCreate);
        var result = await harness.AdvanceAsync(run.Id, owner, Ct);

        result.IsError.ShouldBeFalse(
            result.IsError ? string.Join(", ", result.Errors.Select(error => error.Code)) : "step");
        result.Value.Kind.ShouldBe(StepResultKind.Advanced);

        var created = await harness.RunAsync(owner, run.Id, Ct);
        created.State.ShouldBe(AutopilotRunState.PublicationCreated);
        created.PublicationId.ShouldNotBeNull();
        created.AuthorizationId.ShouldBe(world.AuthorizationId, "the create stamps the revision it published under");
        (await PublicationsOfAsync(episodeId)).Count.ShouldBe(1);

        // Every later tick re-enters the post-publication steps; none of them may create a second row.
        await harness.AdvanceAsync(run.Id, owner, Ct);
        (await PublicationsOfAsync(episodeId)).Count.ShouldBe(1);
    }

    /// <summary>
    /// An owner waiting on the video who has not approved it yet keeps waiting: the create is never sent for a
    /// <c>video_review</c> episode, so the run no longer ends <c>publication_create_failed</c> when the window opens
    /// (S71 SEC-3, path 2).
    /// </summary>
    [Fact]
    public async Task Run_WaitingForVideoBeforeTheOwnerApproves_StaysWaiting()
    {
        await using var harness = await StartAsync(services => ReplaceGate(services, new UnreachableGate()));

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, ct: Ct);
        var episodeId = await harness.CreateEpisodeAsync(owner, world.SeriesId, EpisodeStatus.VideoReview, Ct);

        harness.Clock.Set(WorkedPublicationCreate);
        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: WorkedSlot,
            startAt: WorkedStart,
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.VideoGate,
            step: AutopilotRunStep.VideoGate,
            episodeId: episodeId,
            attempt: 1,
            renderAttempts: 1,
            ct: Ct);
        await Database.ExecuteAsync(
            $"UPDATE autopilot_runs SET state = 'waiting_owner', step = 'video_gate', waiting_for = 'video' "
            + $"WHERE id = '{run.Id}'",
            Ct);

        var result = await harness.AdvanceAsync(run.Id, owner, Ct);

        result.IsError.ShouldBeFalse(
            result.IsError ? string.Join(", ", result.Errors.Select(error => error.Code)) : "step");
        result.Value.Kind.ShouldBe(StepResultKind.Idle);

        var waiting = await harness.RunAsync(owner, run.Id, Ct);
        waiting.State.ShouldBe(AutopilotRunState.WaitingOwner);
        waiting.WaitingFor.ShouldBe(WaitingFor.Video);
        waiting.PublicationId.ShouldBeNull();
        (await PublicationsOfAsync(episodeId)).ShouldBeEmpty();
    }

    /// <summary>
    /// The consent the create relies on is the series' current authorization, never the run's idea-time snapshot: a
    /// series whose pointer was cleared publishes nothing, even though the run still names a live revision
    /// (S71 SEC-5).
    /// </summary>
    [Fact]
    public async Task Run_VideoGateLivePassWithNoCurrentAuthorization_PublishesNothing()
    {
        await using var harness = await StartAsync(services => ReplaceGate(services, new UnreachableGate()));

        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, ct: Ct);
        var episodeId = await harness.CreateEpisodeAsync(owner, world.SeriesId, EpisodeStatus.Ready, Ct);

        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: WorkedSlot,
            startAt: WorkedStart,
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.VideoGate,
            step: AutopilotRunStep.VideoGate,
            episodeId: episodeId,
            attempt: 1,
            renderAttempts: 1,
            ct: Ct);
        await WriteGateResultsAsync(run.Id, mode: "live", pass: true);
        await harness.UpdateSeriesAsync(world.SeriesId, "current_autopilot_authorization_id = NULL", Ct);

        harness.Clock.Set(WorkedPublicationCreate);
        var result = await harness.AdvanceAsync(run.Id, owner, Ct);

        result.IsError.ShouldBeFalse(
            result.IsError ? string.Join(", ", result.Errors.Select(error => error.Code)) : "step");
        result.Value.Kind.ShouldBe(StepResultKind.Stopped);
        result.Value.Code.ShouldBe(AutopilotStepCodes.ChannelMissing);

        var ended = await harness.RunAsync(owner, run.Id, Ct);
        ended.State.ShouldBe(AutopilotRunState.Failed);
        ended.PublicationId.ShouldBeNull();
        (await PublicationsOfAsync(episodeId)).ShouldBeEmpty();
    }

    /// <summary>Writes the <c>gates.v1</c> document the video gate would have committed for the run.</summary>
    private Task WriteGateResultsAsync(Guid runId, string mode, bool pass) =>
        Database.ExecuteAsync(
            "UPDATE autopilot_runs SET gate_results = '"
            + "{\"schema_version\":\"gates.v1\",\"mode\":\"" + mode + "\",\"profile\":\"default\","
            + "\"evaluated_at\":\"" + Instant(WorkedStart) + "\",\"video\":{\"pass\":"
            + (pass ? "true" : "false")
            + ",\"render_job_id\":\"" + Guid.Empty + "\",\"asset_id\":\"" + Guid.Empty
            + "\",\"checks\":[],\"failures\":[]}}'::jsonb "
            + $"WHERE id = '{runId}'",
            Ct);

    /// <summary>Every <c>publications</c> row of one episode, by id.</summary>
    private Task<IReadOnlyList<string>> PublicationsOfAsync(Guid episodeId) =>
        Database.QueryStringsAsync($"SELECT id::text FROM publications WHERE episode_id = '{episodeId}'", Ct);

    /// <summary>The worked example's planning options: the shipped <c>Autopilot:Run</c>/<c>YouTube</c> defaults.</summary>
    private static AutopilotTimingOptions TimingOptions() => new(
        PublishLeadMinutes: 120,
        MinVetoWindowHours: 2,
        PublicationCreateMarginMinutes: 10,
        RenderEstimateMinutes: 15,
        StoryEstimateMinutes: 10,
        MaxBufferDays: 7);

    /// <summary>Replaces the harness's story gate with the gate of a case.</summary>
    private static void ReplaceGate(IServiceCollection services, IAutoApprovalGate gate)
    {
        services.RemoveAll<IAutoApprovalGate>();
        services.AddSingleton<IAutoApprovalGate>(gate);
    }

    /// <summary>
    /// The story writer's landing: the version the job saves plus the transition
    /// <c>StoryGenerationProgress.CompleteAsync</c> makes, so <c>story_generated.v1</c> is written by the product.
    /// </summary>
    private async Task<Guid> LandStoryAsync(
        Guid owner,
        Guid seriesId,
        DateTimeOffset slotAt,
        Guid episodeId,
        int version,
        string step)
    {
        var key = AutopilotIdempotencyKeys.For(seriesId, slotAt, step);
        var now = _clock.UtcNow;

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = owner }, _clock);

        var generationRun = await db.GenerationRuns
            .SingleAsync(row => row.SeriesId == seriesId && row.IdempotencyKey == key, Ct);

        var draft = StoryDraft(version);
        var storyVersion = StoryVersion.Create(
            owner,
            episodeId,
            version,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            "fiction-run-test",
            "pv-fiction-run",
            generationRun.Id,
            parentVersionId: null,
            factSheetId: null,
            auditReportJson: null,
            similarityScore: null,
            now);
        db.StoryVersions.Add(storyVersion);

        var episode = await db.Episodes.SingleAsync(row => row.Id == episodeId, Ct);
        var completed = episode.CompleteStoryGeneration(
            generationRun.Id,
            GenerationTrigger.Autopilot,
            [storyVersion.Id],
            storyVersion.Id,
            now);
        completed.IsError.ShouldBeFalse(completed.IsError ? completed.FirstError.Description : "story landed");

        generationRun.Start(now);
        generationRun.Succeed(0m, storyVersion.Id, null, now);

        await db.SaveChangesAsync(Ct);

        return storyVersion.Id;
    }

    /// <summary>The render's landing: the transition the render ingest makes, which raises <c>render_succeeded.v1</c>.</summary>
    private async Task LandRenderAsync(Guid owner, Guid episodeId)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = owner }, Clock());
        var episode = await db.Episodes.SingleAsync(row => row.Id == episodeId, Ct);

        var rendered = episode.RenderSucceeded(Clock().UtcNow);
        rendered.IsError.ShouldBeFalse(rendered.IsError ? rendered.FirstError.Description : "render landed");

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>06.2.2-T3's consent write, through the very verb the job will call.</summary>
    private async Task ConsentAsync(Guid owner, Guid publicationId, Guid authorizationId, DateTimeOffset now)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = owner }, Clock());
        var publication = await db.Publications.SingleAsync(row => row.Id == publicationId, Ct);

        var consented = publication.ConsentFromAuthorization("ai.v1", authorizationId, now);
        consented.IsError.ShouldBeFalse(consented.IsError ? consented.FirstError.Description : "consent written");

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>06.2.5's go-live write: the row the run follows to <c>published</c>.</summary>
    private Task GoLiveAsync(Guid publicationId, DateTimeOffset now) =>
        Database.ExecuteAsync(
            "UPDATE publications SET status = 'published', "
            + $"published_at = '{Instant(now)}', external_post_id = 'yt-fiction-run', "
            + $"external_url = 'https://youtu.be/yt-fiction-run', updated_at = '{Instant(now)}' "
            + $"WHERE id = '{publicationId}'",
            Ct);

    private async Task<Episode> LoadEpisodeAsync(Guid owner, Guid episodeId)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = owner }, Clock());

        return await db.Episodes.AsNoTracking().SingleAsync(row => row.Id == episodeId, Ct);
    }

    private async Task<Publication> LoadPublicationAsync(Guid owner, Guid publicationId)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = owner }, Clock());

        return await db.Publications.AsNoTracking().SingleAsync(row => row.Id == publicationId, Ct);
    }

    /// <summary>
    /// Drives one run until it reaches <paramref name="target"/>: a delivery of the pending outbox message first
    /// (the event path), then one step of the state machine (the tick path), until the step stops making progress.
    /// </summary>
    /// <remarks>
    /// Both drivers are the product's own — the outbox handler and <c>AutopilotRunOrchestrator.AdvanceAsync</c> —
    /// and a run that reaches the target by either is the same run. The bound keeps a stuck run from looping.
    /// </remarks>
    private static async Task<AutopilotRun> DriveUntilAsync(
        AutopilotTickHarness harness,
        Guid runId,
        Guid owner,
        AutopilotRunState target)
    {
        var run = await harness.RunAsync(owner, runId, Ct);

        for (var attempt = 0; attempt < 8 && run.State != target; attempt++)
        {
            await harness.Host.RunOutboxOnceAsync(Ct);

            run = await harness.RunAsync(owner, runId, Ct);
            if (run.State == target)
            {
                break;
            }

            var step = await harness.AdvanceAsync(runId, owner, Ct);
            step.IsError.ShouldBeFalse(step.IsError ? string.Join(", ", step.Errors.Select(error => error.Code)) : "step");

            run = await harness.RunAsync(owner, runId, Ct);
            if (step.Value.Kind is StepResultKind.Idle or StepResultKind.Stopped)
            {
                break;
            }
        }

        return run;
    }

    /// <summary>Whether the series carries a pause stamp; the pause reasons are 02.8.3-T4's triggers.</summary>
    private async Task<bool> SeriesIsPausedAsync(Guid seriesId)
    {
        var rows = await Database.QueryStringsAsync(
            $"SELECT (autopilot_paused_at IS NOT NULL)::text FROM series WHERE id = '{seriesId}'",
            Ct);

        return bool.Parse(rows[0]);
    }

    private static string Instant(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The clock of this case; every harness of the case is started over this instance.</summary>
    private readonly FakeClock _clock = new(Planned);

    /// <summary>Starts a tick harness on this case's clock.</summary>
    private Task<AutopilotTickHarness> StartAsync(Action<IServiceCollection>? configureServices = null) =>
        AutopilotTickHarness.StartAsync(Database, _clock, configureServices: configureServices, ct: Ct);

    private FakeClock Clock() => _clock;

    /// <summary>A deterministic draft: the same shape the story end-to-end tests script.</summary>
    private static StoryContentV1 StoryDraft(int version) => new(
        $"Chuyện của đêm thứ {version}",
        new StorySegmentV1("Mở đầu: ngọn đèn dầu tự cháy trong ngõ vắng.", ["oil lamp glowing"], []),
        [new StorySegmentV1("Thân bài: người kể chuyện lần theo ký ức.", ["old photograph album"], [])],
        new StorySegmentV1("Kết: ký ức trở thành lời kể.", ["quiet village lane"], []),
        "Một người kể chuyện tìm lại ký ức tuổi thơ.",
        ["truyen-ngan"],
        [],
        new StoryStructureV1("a", "b", "c", "d", "e"));

    /// <summary>The scripted verdict of the story and video gates for the fiction cases.</summary>
    private enum StoryVerdict
    {
        /// <summary>A pass, auto-approved: the run goes to <c>rendering</c>.</summary>
        Approved,

        /// <summary>Failed checks: the one regeneration, then <c>gate_failed</c>.</summary>
        FailedChecks,
    }

    /// <summary>The scripted verdict of the video gate.</summary>
    private enum VideoVerdict
    {
        /// <summary>A pass, auto-approved: the run creates its publication.</summary>
        Approved,

        /// <summary>Failed checks in live mode: the run ends <c>gate_failed</c>.</summary>
        FailedChecks,

        /// <summary>A dry run: the run waits for the owner with <c>waiting_for = video</c>.</summary>
        DryRun,
    }

    /// <summary>
    /// The fiction gate: a scripted verdict plus the approval the real gate performs, which is
    /// <c>Episode.ApproveStory</c>/<c>Episode.ApproveVideo</c> applied under the run's owner scope.
    /// </summary>
    private sealed class ScriptedFictionGate(TestDatabase database) : IAutoApprovalGate
    {
        private readonly Lock _sync = new();
        private int _storyCalls;

        public StoryVerdict StoryVerdict { get; set; } = StoryVerdict.Approved;

        public VideoVerdict VideoVerdict { get; set; } = VideoVerdict.Approved;

        public int StoryCalls
        {
            get
            {
                lock (_sync)
                {
                    return _storyCalls;
                }
            }
        }

        public async ValueTask<ErrorOr<StoryGateOutcome>> EvaluateStoryAsync(
            Guid runId,
            int attempt,
            CancellationToken ct)
        {
            lock (_sync)
            {
                _storyCalls++;
            }

            var owner = await OwnerOfAsync(runId, ct);
            var run = await LoadRunAsync(owner, runId, ct);

            if (StoryVerdict == StoryVerdict.FailedChecks)
            {
                var failed = new StoryGateResult(
                    Pass: false,
                    Hold: false,
                    Attempt: attempt,
                    StoryVersionId: run.EpisodeId ?? Guid.Empty,
                    Checks: [],
                    Failures: ["fiction_run_test_failure"]);

                return new StoryGateOutcome(failed, AutoApproved: false, GateStopReason.FailedChecks);
            }

            if (run.EpisodeId is { } episodeId)
            {
                await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = owner }, new FakeClock(run.LastTransitionAt));
                var episode = await db.Episodes.SingleAsync(row => row.Id == episodeId, ct);
                if (episode.Status == EpisodeStatus.StoryReview && episode.CurrentStoryVersionId is { } versionId)
                {
                    var approved = episode.ApproveStory(versionId, ApprovalSource.Autopilot, run.LastTransitionAt);
                    approved.IsError.ShouldBeFalse();
                    await db.SaveChangesAsync(ct);
                }
            }

            var passed = new StoryGateResult(
                Pass: true,
                Hold: false,
                Attempt: attempt,
                StoryVersionId: run.EpisodeId ?? Guid.Empty,
                Checks: [],
                Failures: []);

            return new StoryGateOutcome(passed, AutoApproved: true, Reason: null);
        }

        public async ValueTask<ErrorOr<VideoGateOutcome>> EvaluateVideoAsync(Guid runId, CancellationToken ct)
        {
            var owner = await OwnerOfAsync(runId, ct);
            var run = await LoadRunAsync(owner, runId, ct);

            switch (VideoVerdict)
            {
                case VideoVerdict.DryRun:
                    return new VideoGateOutcome(Result(Guid.Empty, []), AutoApproved: false, GateStopReason.DryRun);

                case VideoVerdict.FailedChecks:
                    return new VideoGateOutcome(
                        Result(Guid.Empty, ["fiction_run_test_failure"]),
                        AutoApproved: false,
                        GateStopReason.FailedChecks);

                default:
                    if (run.EpisodeId is { } episodeId)
                    {
                        await using var db = database.CreateAppDbContext(
                            new TestCurrentUser { Id = owner },
                            new FakeClock(run.LastTransitionAt));
                        var episode = await db.Episodes.SingleAsync(row => row.Id == episodeId, ct);
                        if (episode.Status == EpisodeStatus.VideoReview)
                        {
                            var approved = episode.ApproveVideo(run.LastTransitionAt);
                            approved.IsError.ShouldBeFalse();
                            await db.SaveChangesAsync(ct);
                        }
                    }

                    return new VideoGateOutcome(Result(Guid.Empty, []), AutoApproved: true, Reason: null);
            }

            static VideoGateResult Result(Guid assetId, IReadOnlyList<string> failures) =>
                new(Pass: failures.Count == 0, RenderJobId: Guid.Empty, assetId, Checks: [], failures);
        }

        /// <summary>The owner of the run; the gate is a singleton and the harness's world is one owner per test.</summary>
        private async ValueTask<Guid> OwnerOfAsync(Guid runId, CancellationToken ct)
        {
            var rows = await database.QueryStringsAsync(
                $"SELECT user_id::text FROM autopilot_runs WHERE id = '{runId}'",
                ct);

            rows.Count.ShouldBe(1);

            return Guid.Parse(rows[0]);
        }

        private async ValueTask<AutopilotRun> LoadRunAsync(Guid owner, Guid runId, CancellationToken ct)
        {
            await using var db = database.CreateAppDbContext(
                new TestCurrentUser { Id = owner },
                new FakeClock(AutopilotRunBuilderClock));
            var rows = await db.AutopilotRuns.AsNoTracking().ToListAsync(ct);
            var run = rows.Single(row => row.Id == runId);

            return run;
        }

        /// <summary>The seeded instant every gate approval stamps; the cases assert states, not the approval time.</summary>
        private static readonly DateTimeOffset AutopilotRunBuilderClock = Planned;
    }

    /// <summary>
    /// A gate that must never run: the run already holds the verdict the case seeded, so any call means the step
    /// re-evaluated (and would approve and notify twice) instead of replaying what the gate committed (S71 SEC-3).
    /// </summary>
    private sealed class UnreachableGate : IAutoApprovalGate
    {
        /// <inheritdoc />
        public ValueTask<ErrorOr<StoryGateOutcome>> EvaluateStoryAsync(Guid runId, int attempt, CancellationToken ct) =>
            throw new InvalidOperationException("The story gate must not be evaluated: the run already holds its verdict.");

        /// <inheritdoc />
        public ValueTask<ErrorOr<VideoGateOutcome>> EvaluateVideoAsync(Guid runId, CancellationToken ct) =>
            throw new InvalidOperationException("The video gate must not be evaluated: the run already holds its verdict.");
    }

    /// <summary>The platform spend cap, always refusing: the row <c>Budget refusal at any paid dispatch</c>.</summary>
    private sealed class RefusingBudgetGuard : IBudgetGuard
    {
        public ValueTask<ErrorOr<Success>> CheckAsync(
            CostBound worstCaseUsd,
            CancellationToken ct,
            Guid? excludingRunId = null) =>
            ValueTask.FromResult<ErrorOr<Success>>(Error.Failure("spend_cap_reached", "The platform cap is spent."));
    }
}
