using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Application.Features.Stories.Jobs;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using NightTales.Worker.Jobs;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>
/// <see cref="StaleGenerationSweeperJob"/> through the Worker's own graph (02.2.2-T3, plan §7.3): the real
/// <c>ISystemWorkQuery</c>, the per-row pinned scope, the terminal writes of <see cref="StoryGenerationProgress"/>
/// and the refund port (recorded here, because the P ledger is a no-op). Branch (a) fails a <c>running</c> run past
/// its deadline with the kind's generic code and returns the episode to its previous status; branch (b) cancels a
/// run stuck <c>queued</c> for more than 12 hours with <c>story_generation_cancelled</c> — the 12.8.3 e-mail is a
/// documented hand-over, so no <c>email_requested.v1</c> row may appear (02 §10 T113).
/// </summary>
[Trait("Category", "Integration")]
public sealed class StaleGenerationSweeperTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Sweeper_RunningPastDeadline_FailsNotCounted_ReturnsEpisodeToPreviousStatus()
    {
        var world = await SeedAsync(EpisodeStatus.StoryGenerating);
        var reservation = Guid.CreateVersion7();
        var runId = await SeedRunAsync(world, GenerationKind.Story, queuedAt: Now.AddMinutes(-30), startedAt: Now.AddMinutes(-20), reservation);
        var ledger = new RecordingLedger();
        await using var host = await StartWorkerAsync(ledger);

        var summary = await SweepAsync(host);

        summary.Failed.ShouldBe(1);
        summary.Cancelled.ShouldBe(0);
        summary.Skipped.ShouldBe(0);
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || error_code || '/' || error_detail || '/' || counts_toward_limit::text "
                + $"FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("failed/story_generation_failed/stale_run_sweep/false");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM generation_runs WHERE id = '{runId}' AND finished_at IS NOT NULL")
            .ShouldBe(1);
        ledger.Refunds.ShouldBe([("stale_run", new ReservationId(reservation))]);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_generation_failed.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Sweeper_QueuedOver12h_CancelsAndRefunds()
    {
        var world = await SeedAsync(EpisodeStatus.StoryGenerating);
        var reservation = Guid.CreateVersion7();
        var runId = await SeedRunAsync(world, GenerationKind.Story, queuedAt: Now.AddHours(-13), startedAt: null, reservation);
        var ledger = new RecordingLedger();
        await using var host = await StartWorkerAsync(ledger);

        var summary = await SweepAsync(host);

        summary.Failed.ShouldBe(0);
        summary.Cancelled.ShouldBe(1);
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || error_code || '/' || error_detail || '/' || counts_toward_limit::text "
                + $"FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("cancelled/story_generation_cancelled/stale_run_sweep/false");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
        ledger.Refunds.ShouldBe([("stale_run", new ReservationId(reservation))]);
        // 12.8.3 hand-over: the stuck-queued cancel e-mail has no sender yet, so none is written (02 §10 T113(e)).
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'email_requested.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task Sweeper_CoversEveryKind()
    {
        var story = await SeedAsync(EpisodeStatus.StoryGenerating);
        var other = await SeedAsync(EpisodeStatus.StoryReview);
        var reservations = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToArray();

        var storyRun = await SeedRunAsync(story, GenerationKind.Story, queuedAt: Now.AddMinutes(-30), startedAt: Now.AddMinutes(-20), reservations[0]);
        var factSheetRun = await SeedRunAsync(other, GenerationKind.FactSheet, queuedAt: Now.AddMinutes(-30), startedAt: Now.AddMinutes(-20), reservations[1]);
        var bibleRun = await SeedRunAsync(other, GenerationKind.BibleProposal, queuedAt: Now.AddMinutes(-30), startedAt: Now.AddMinutes(-20), reservations[2]);
        var ideasRun = await SeedRunAsync(other, GenerationKind.Ideas, queuedAt: Now.AddMinutes(-30), startedAt: Now.AddMinutes(-20), reservations[3]);
        var findSourcesRun = await SeedRunAsync(other, GenerationKind.FindSources, queuedAt: Now.AddMinutes(-30), startedAt: Now.AddMinutes(-20), reservations[4]);
        var ledger = new RecordingLedger();
        await using var host = await StartWorkerAsync(ledger);

        var summary = await SweepAsync(host);

        summary.Failed.ShouldBe(5);
        summary.Cancelled.ShouldBe(0);
        summary.Skipped.ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT error_code FROM generation_runs WHERE id = '{storyRun}'").ShouldBe("story_generation_failed");
        SqlProbe.ScalarText(Database, $"SELECT error_code FROM generation_runs WHERE id = '{factSheetRun}'").ShouldBe("fact_sheet_generation_failed");
        SqlProbe.ScalarText(Database, $"SELECT error_code FROM generation_runs WHERE id = '{bibleRun}'").ShouldBe("bible_proposal_generation_failed");
        SqlProbe.ScalarText(Database, $"SELECT error_code FROM generation_runs WHERE id = '{ideasRun}'").ShouldBe("ideas_generation_failed");
        SqlProbe.ScalarText(Database, $"SELECT error_code FROM generation_runs WHERE id = '{findSourcesRun}'").ShouldBe("find_sources_generation_failed");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM generation_runs WHERE counts_toward_limit = false AND id IN "
                + $"('{storyRun}', '{factSheetRun}', '{bibleRun}', '{ideasRun}', '{findSourcesRun}')")
            .ShouldBe(5);

        // Only the story run may move its episode; the other kinds' episode stays exactly as it was (plan §7.3).
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{story.EpisodeId}'").ShouldBe("draft");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{other.EpisodeId}'").ShouldBe("story_review");
        ledger.Refunds.Select(refund => refund.Reason).Distinct().ShouldBe(["stale_run"]);
        ledger.Refunds.Count.ShouldBe(5);
    }

    [Fact]
    public async Task Sweeper_SeriesLevelRun_FailsWithoutTouchingAnEpisode()
    {
        var story = await SeedAsync(EpisodeStatus.StoryGenerating);
        var bible = await SeedAsync(EpisodeStatus.StoryReview);
        // The story run is still fresh: it must survive the pass untouched so the episode-move check is meaningful.
        var storyRun = await SeedRunAsync(story, GenerationKind.Story, queuedAt: Now.AddMinutes(-5), startedAt: null, Guid.CreateVersion7());
        var bibleReservation = Guid.CreateVersion7();
        var bibleRun = await SeedRunAsync(bible, GenerationKind.BibleProposal, queuedAt: Now.AddMinutes(-30), startedAt: Now.AddMinutes(-20), bibleReservation);
        var ledger = new RecordingLedger();
        await using var host = await StartWorkerAsync(ledger);

        var summary = await SweepAsync(host);

        summary.Failed.ShouldBe(1);
        summary.Cancelled.ShouldBe(0);
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || error_code FROM generation_runs WHERE id = '{bibleRun}'")
            .ShouldBe("failed/bible_proposal_generation_failed");
        // episode_id IS NULL: nothing exists for this run to move, and the unrelated episode stays put.
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{bible.EpisodeId}'").ShouldBe("story_review");
        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{storyRun}'").ShouldBe("queued");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{story.EpisodeId}'").ShouldBe("story_generating");
        ledger.Refunds.ShouldBe([("stale_run", new ReservationId(bibleReservation))]);
    }

    [Fact]
    public async Task Sweeper_StuckQueuedRun_IsCancelled_WithErrorCode_AndNotCounted()
    {
        var world = await SeedAsync(EpisodeStatus.StoryGenerating);
        // A held (paused) run is still queued: 12 hours later the sweeper cancels it, which is not a user
        // cancel — the D8 code is set and the run does not count toward the daily limit (02 CR-29).
        var runId = await SeedRunAsync(
            world,
            GenerationKind.Story,
            queuedAt: Now.AddHours(-13),
            startedAt: null,
            Guid.CreateVersion7(),
            run => run.MarkPaused(GenerationPauseReason.ProviderFundsExhausted, Now.AddHours(-13)));
        var ledger = new RecordingLedger();
        await using var host = await StartWorkerAsync(ledger);

        var summary = await SweepAsync(host);

        summary.Cancelled.ShouldBe(1);
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || error_code || '/' || counts_toward_limit::text || '/' || (pause_reason IS NULL)::text "
                + $"FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("cancelled/story_generation_cancelled/false/true");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
    }

    [Fact]
    public async Task Sweeper_RunningRunStartedRecently_IsNotSwept_EvenWhenQueuedLongAgo()
    {
        var world = await SeedAsync(EpisodeStatus.StoryGenerating);
        // A paused run resumed after a long wait: queued_at is old, started_at is fresh. The indexed expression
        // COALESCE(started_at, queued_at) takes started_at, so the run is not stale.
        var runId = await SeedRunAsync(world, GenerationKind.Story, queuedAt: Now.AddHours(-13), startedAt: Now.AddMinutes(-1), Guid.CreateVersion7());
        var ledger = new RecordingLedger();
        await using var host = await StartWorkerAsync(ledger);

        var summary = await SweepAsync(host);

        summary.Failed.ShouldBe(0);
        summary.Cancelled.ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("running");
        ledger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sweeper_Predicates_TranslateToTheIndexedCoalesceExpression()
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = Guid.CreateVersion7() }, new FakeClock(Now));

        var running = db.GenerationRuns.Where(StaleGenerationSweeperJob.RunningPastDeadline(Now, [Guid.CreateVersion7()])).ToQueryString();
        var queued = db.GenerationRuns.Where(StaleGenerationSweeperJob.QueuedPastLimit(Now, [Guid.CreateVersion7()])).ToQueryString();

        // Index ix on (status, COALESCE(started_at, queued_at)), migration 02_GenerationRuns.
        running.ShouldContain("COALESCE(");
        running.ShouldContain("started_at");
        running.ShouldContain("queued_at");
        queued.ShouldContain("COALESCE(");
        queued.ShouldContain("started_at");
        queued.ShouldContain("queued_at");
    }

    [Fact]
    public async Task Sweeper_FailsRunMidFlight_JobsNextProgressCallsAreRejected_AndNothingSettlesAfterTheRefund()
    {
        var world = await SeedAsync(EpisodeStatus.StoryGenerating);
        var reservation = Guid.CreateVersion7();
        var runId = await SeedRunAsync(world, GenerationKind.Story, queuedAt: Now.AddMinutes(-30), startedAt: Now.AddMinutes(-20), reservation);
        var ledger = new RecordingLedger();
        await using var host = await StartWorkerAsync(ledger);

        // The job's scope: it loaded the run while it was running and keeps the tracked instance.
        using var jobScope = host.CreateScope();
        using var pinned = jobScope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var jobDb = jobScope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var tracked = await jobDb.GenerationRuns.SingleAsync(run => run.Id == runId, Ct);
        tracked.Status.ShouldBe(GenerationRunStatus.Running);
        var progress = jobScope.ServiceProvider.GetRequiredService<StoryGenerationProgress>();

        var summary = await SweepAsync(host);

        summary.Failed.ShouldBe(1);
        var stage = await progress.RecordStageAsync(runId, GenerationStage.Scoring, Ct);
        stage.IsError.ShouldBeTrue();
        stage.FirstError.Code.ShouldBe(StoryGenerationProgress.RunTerminalCode);
        var completed = await progress.CompleteAsync(new StoryCompletion(runId, [Guid.CreateVersion7()], Guid.CreateVersion7()), Ct);
        completed.FirstError.Code.ShouldBe(StoryGenerationProgress.RunTerminalCode);
        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("failed");
        SqlProbe.ScalarText(Database, $"SELECT stage FROM generation_runs WHERE id = '{runId}'").ShouldNotBe("scoring");
        ledger.Settled.ShouldBeEmpty();
        ledger.Refunds.ShouldBe([("stale_run", new ReservationId(reservation))]);
    }

    [Fact]
    public async Task Sweeper_QueuedRunStartedByAWorkerBetweenReadAndCancel_StaysRunning_NoRefund_EpisodeUntouched()
    {
        var world = await SeedAsync(EpisodeStatus.StoryGenerating);
        var runId = await SeedRunAsync(world, GenerationKind.Story, queuedAt: Now.AddHours(-13), startedAt: null, Guid.CreateVersion7());
        var ledger = new RecordingLedger();
        await using var host = await StartWorkerAsync(ledger);

        // The worker's start commits after the sweeper read "queued" and before its cancel claim.
        using (var jobScope = host.CreateScope())
        {
            using var pinned = jobScope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
            var started = await jobScope.ServiceProvider.GetRequiredService<StoryGenerationProgress>().StartAsync(runId, Ct);
            started.IsError.ShouldBeFalse();
        }

        using var sweeperScope = host.CreateScope();
        using var sweeperPinned = sweeperScope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var cancelled = await sweeperScope.ServiceProvider.GetRequiredService<StoryGenerationProgress>().CancelAsync(
            runId,
            StaleGenerationSweeperJob.StaleRefundReason,
            "story_generation_cancelled",
            StaleGenerationSweeperJob.StaleDetail,
            Ct,
            recovery: true,
            stale: new StaleClaim(GenerationRunStatus.Queued, Now - StaleGenerationSweeperJob.QueuedLimit));

        cancelled.IsError.ShouldBeTrue();
        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("running");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_generating");
        ledger.Refunds.ShouldBeEmpty();
        (await SweepAsync(host)).Cancelled.ShouldBe(0);
    }

    [Fact]
    public void Sweeper_Registration_UpsertsEveryFiveMinutesOnTheDefaultQueue()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scheduler = new InlineJobScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        new StaleGenerationSweeperRegistration().Register(scheduler);

        scheduler.RecurringUpserts.ShouldHaveSingleItem().ShouldBe(new RecordedRecurringCall(
            StaleGenerationSweeperRegistration.RecurringId,
            nameof(StaleGenerationSweeperJob),
            JobQueues.Name(JobQueue.Default),
            "*/5 * * * *"));
        StaleGenerationSweeperRegistration.RecurringId.ShouldBe("stale-generation-sweeper");
    }

    /// <summary>Seeds one user, series and episode; <paramref name="status"/> is the episode's stored status.</summary>
    private async Task<World> SeedAsync(EpisodeStatus status)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: Topic.Ghost, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary>
    /// Seeds one <c>generation_runs</c> row of <paramref name="kind"/> for the world. A non-null
    /// <paramref name="startedAt"/> makes it a <c>running</c> run; the series-level kinds
    /// (<see cref="GenerationKind.Ideas"/>, <see cref="GenerationKind.BibleProposal"/>) get no episode id.
    /// </summary>
    private async Task<Guid> SeedRunAsync(
        World world,
        GenerationKind kind,
        DateTimeOffset queuedAt,
        DateTimeOffset? startedAt,
        Guid reservationId,
        Action<GenerationRun>? beforeSave = null)
    {
        var seriesLevel = kind is GenerationKind.Ideas or GenerationKind.BibleProposal;

        await using var db = Database.CreateAppDbContext(
            new TestCurrentUser { Id = world.UserId },
            new FakeClock(queuedAt));
        var run = GenerationRun.Queue(
                world.UserId,
                world.SeriesId,
                seriesLevel ? null : world.EpisodeId,
                kind,
                GenerationTrigger.User,
                null,
                seriesLevel ? null : EpisodeStatus.Draft,
                null,
                reservationId,
                0.5m,
                queuedAt)
            .Value;
        if (startedAt is { } started)
        {
            run.Start(started);
        }
        beforeSave?.Invoke(run);
        db.GenerationRuns.Add(run);
        await db.SaveChangesAsync(Ct);

        return run.Id;
    }

    private async Task<WorkerHost> StartWorkerAsync(RecordingLedger ledger)
    {
        var host = new WorkerHost(Database, new FakeClock(Now)).ConfigureServices(services =>
        {
            services.RemoveAll<ICreditLedger>();
            services.AddSingleton<ICreditLedger>(ledger);
        });
        await host.StartAsync(Ct);

        return host;
    }

    private static async Task<StaleSweepSummary> SweepAsync(WorkerHost host)
    {
        using var scope = host.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<StaleGenerationSweeperJob>().SweepAsync(Ct);
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    /// <summary>The recording ledger of the sweeper tests: the P implementation is a no-op, so the refund
    /// contract is proven by capturing the call the progress writer makes.</summary>
    private sealed class RecordingLedger : ICreditLedger
    {
        public List<(string Reason, ReservationId Reservation)> Refunds { get; } = [];

        public List<ReservationId> Settled { get; } = [];

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(Guid userId, CreditOperation op, Guid referenceId, UsageBound bound, CancellationToken ct) =>
            throw new NotSupportedException("The sweeper never reserves.");

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct)
        {
            Settled.Add(reservationId);

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            Refunds.Add((reason, reservationId));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
