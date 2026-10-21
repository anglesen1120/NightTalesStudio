using System.Globalization;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Events;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// The <c>StoryApproved</c> → bible-proposal queueing half of 02.6.3-T2 (plan §7.1 handler steps 2–7): the guards,
/// the budget refusal, the <c>generation_runs</c> row, the reservation and the enqueue. The rows are delivered by
/// the Worker's own <c>OutboxDispatcher</c> (<see cref="WorkerHost.RunOutboxOnceAsync"/>), so what is asserted is
/// the production path from the event to the queued job, with the paid call left to the job's own tests.
/// </summary>
/// <remarks>
/// The prompt catalogue and the price version are not seeded: <see cref="IBibleProposalCostEstimator"/> is
/// replaced by a fixed estimator (the fact-sheet suite's pattern) so these tests assert the queueing contract and
/// not the catalogue, and <see cref="IBudgetGuard"/> is the TestKit's scriptable fake so only this handler's
/// reaction to a refusal is in play. The proposals themselves are seeded directly where a test needs one.
/// </remarks>
public sealed class StoryApprovedBibleProposalTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = SeriesBibleHarness.Start;

    private static CancellationToken Ct => SeriesBibleHarness.Ct;

    [Fact]
    public async Task StoryApproved_UserApproval_QueuesOneProposalRun_AndEnqueuesTheJob()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episode = await SeedApprovedEpisodeAsync(world);
        await InsertStoryApprovedAsync(world, episode, ApprovalSource.User);

        await using var worker = await StartWorkerAsync();
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        RunRow(world.SeriesId).ShouldBe(
            $"true|false|system|queued|{Key(episode)}|true|true",
            "series-level run: NULL episode_id, never counted, system trigger, reserved and priced");
        worker.Jobs.Enqueued.Select(enqueue => enqueue.JobType).ShouldBe(["ProposeBibleUpdateJob"]);
        worker.Jobs.Enqueued[0].Delay.ShouldBeNull("the first delivery enqueues immediately");
        worker.Jobs.Failures.ShouldBeEmpty();
        Deliveries(world).ShouldBe(1, "the delivery succeeded, so the message is not redelivered");
    }

    [Fact]
    public async Task StoryApproved_TwiceForSameEpisode_ProducesOneProposal()
    {
        // The outbox is at-least-once: two deliveries of the same approval (a crash after the handler's commit, a
        // lease that expired) must produce one run, because the key names the episode's approved version.
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episode = await SeedApprovedEpisodeAsync(world);
        await InsertStoryApprovedAsync(world, episode, ApprovalSource.User);
        await InsertStoryApprovedAsync(world, episode, ApprovalSource.User);

        await using var worker = await StartWorkerAsync();
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBe(2);

        RunCount(world.SeriesId).ShouldBe(1);
        RunRow(world.SeriesId).ShouldBe($"true|false|system|queued|{Key(episode)}|true|true");
        worker.Jobs.Failures.ShouldBeEmpty();
        Deliveries(world).ShouldBe(2);

        // The second delivery re-enqueues the run it found still queued: a commit whose enqueue died would
        // otherwise leave a run nobody runs. The job itself is idempotent on the run, so the repeat is harmless.
        worker.Jobs.Enqueued.Count(enqueue => enqueue.JobType == "ProposeBibleUpdateJob").ShouldBe(2);
    }

    [Fact]
    public async Task StoryApproved_SeriesArchived_NoProposalQueued()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episode = await SeedApprovedEpisodeAsync(world);
        SqlProbe.Execute(
            Database,
            $"UPDATE series SET archived_at = '{Start:O}' WHERE id = '{world.SeriesId.ToString("D", CultureInfo.InvariantCulture)}'");
        await InsertStoryApprovedAsync(world, episode, ApprovalSource.User);

        await using var worker = await StartWorkerAsync();
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        RunCount(world.SeriesId).ShouldBe(0);
        worker.Jobs.Enqueued.ShouldBeEmpty();
        worker.Jobs.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoryApproved_ProposalDisabled_SkipsWithoutRun()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episode = await SeedApprovedEpisodeAsync(world);
        await InsertStoryApprovedAsync(world, episode, ApprovalSource.User);

        await using var worker = await StartWorkerAsync(bibleEnabled: false);
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        RunCount(world.SeriesId).ShouldBe(0, "the kill switch means no row at all: nothing is spent and nothing is stored");
        worker.Jobs.Enqueued.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoryApproved_BudgetExceeded_FailsRunWithSpendCapReached()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episode = await SeedApprovedEpisodeAsync(world);
        await InsertStoryApprovedAsync(world, episode, ApprovalSource.User);

        var guard = new FakeBudgetGuard { Deny = true };
        await using var worker = await StartWorkerAsync(guard);
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        SqlProbe.ScalarText(
                Database,
                $"""
                 SELECT status || '|' || error_code || '|' || counts_toward_limit::text || '|' || (reservation_id IS NULL)::text
                 FROM generation_runs WHERE series_id = '{world.SeriesId.ToString("D", CultureInfo.InvariantCulture)}'
                 """)
            .ShouldBe("failed|spend_cap_reached|false|true", "a nice-to-have never breaks the budget (plan §7.1 step 3)");

        // The event was delivered (not dead-lettered): the refusal is an outcome, not a handler failure.
        Deliveries(world).ShouldBe(1);
        worker.Jobs.Enqueued.ShouldBeEmpty();
        worker.Jobs.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoryApproved_TrueCrimeAutopilot_Skips()
    {
        var world = await SeedTrueCrimeSeriesAsync();
        var episode = await SeedApprovedEpisodeAsync(world);
        await InsertStoryApprovedAsync(world, episode, ApprovalSource.Autopilot);

        await using var worker = await StartWorkerAsync();
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        RunCount(world.SeriesId).ShouldBe(0, "true crime never auto-approves, so this combination may not spend");
        worker.Jobs.Enqueued.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoryApproved_AutopilotApproval_QueuesRunWithAutopilotTrigger()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episode = await SeedApprovedEpisodeAsync(world);
        await InsertStoryApprovedAsync(world, episode, ApprovalSource.Autopilot);

        await using var worker = await StartWorkerAsync();
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        RunRow(world.SeriesId).ShouldBe(
            $"true|false|autopilot|queued|{Key(episode)}|true|true",
            "an autopilot approval is the autopilot trigger; the run still never counts toward the daily allowance");
        worker.Jobs.Enqueued.Select(enqueue => enqueue.JobType).ShouldBe(["ProposeBibleUpdateJob"]);
    }

    [Fact]
    public async Task ProposeBible_RunRow_HasNullEpisodeIdAndPassesScopeCheck()
    {
        // D2's scope rule: a series-level kind must carry episode_id = NULL, and the row's kind is the one the
        // sweep, the usage meter and the UI all read. The insert passing at all is the check constraint's verdict.
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episode = await SeedApprovedEpisodeAsync(world);
        await InsertStoryApprovedAsync(world, episode, ApprovalSource.User);

        await using var worker = await StartWorkerAsync();
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        SqlProbe.ScalarText(
                Database,
                $"""
                 SELECT kind || '|' || (episode_id IS NULL)::text || '|' || (series_id IS NOT NULL)::text || '|' || attempt::text
                     || '|' || (result_story_version_id IS NULL)::text || '|' || (input_fact_sheet_id IS NULL)::text
                 FROM generation_runs WHERE series_id = '{world.SeriesId.ToString("D", CultureInfo.InvariantCulture)}'
                 """)
            .ShouldBe("bible_proposal|true|true|0|true|true");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM generation_runs WHERE kind = 'bible_proposal' AND counts_toward_limit = true")
            .ShouldBe(0, "D2 counts kind = story with trigger = user only");
    }

    [Fact]
    public async Task ProposeBible_SecondApprovalWhileFirstRunning_RunsAfterFirst()
    {
        // Plan §7.1 step 7 (handler half): the series-level active-run index refuses the second run, and the
        // delivery is deferred through the scheduler instead of failing. The job that eventually queues it is
        // driven end to end by the job suite.
        var world = await SeriesBibleHarness.SeedAsync(Database);
        await SeedApprovedEpisodeAsync(world);
        var second = await SeedApprovedEpisodeAsync(world);
        await SeedActiveBibleRunAsync(world);
        await InsertStoryApprovedAsync(world, second, ApprovalSource.User);

        await using var worker = await StartWorkerAsync();
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        RunCount(world.SeriesId).ShouldBe(1, "only the seeded run exists: the deferred delivery queued nothing");
        SqlProbe.ScalarText(
                Database,
                $"""
                 SELECT idempotency_key FROM generation_runs
                 WHERE series_id = '{world.SeriesId.ToString("D", CultureInfo.InvariantCulture)}'
                 """)
            .ShouldBe(BlockingKey, "the seeded run is untouched");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM generation_runs WHERE idempotency_key = '{Key(second)}'")
            .ShouldBe(0);

        worker.Jobs.Enqueued.Count(enqueue => enqueue.JobType == "BibleProposalHandoffJob").ShouldBe(1);
        worker.Jobs.Enqueued
            .Single(enqueue => enqueue.JobType == "BibleProposalHandoffJob")
            .Delay
            .ShouldBe(TimeSpan.FromMinutes(2), "the deferred delivery waits one run out");
        worker.Jobs.Enqueued.ShouldNotContain(enqueue => enqueue.JobType == "ProposeBibleUpdateJob");
        worker.Jobs.Failures.ShouldBeEmpty();
        Deliveries(world).ShouldBe(1, "the refusal is handled, so the message is neither redelivered nor dead-lettered by this delivery");

        // Both episodes exist, so the deferral is about the index and nothing else.
        SqlProbe.Count(Database, $"SELECT count(*) FROM episodes WHERE series_id = '{world.SeriesId.ToString("D", CultureInfo.InvariantCulture)}'")
            .ShouldBe(2);
    }

    [Fact]
    public async Task StoryApproved_RedeliveredAfterTheKillSwitchIsOff_EnqueuesNothingForTheOpenRun()
    {
        // S7: the switch is read before the replay guard, so a switched-off host never re-enqueues the job of a run
        // that is already queued for the same approval.
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episode = await SeedApprovedEpisodeAsync(world);
        await InsertStoryApprovedAsync(world, episode, ApprovalSource.User);

        {
            await using var enabled = await StartWorkerAsync();
            (await enabled.RunOutboxOnceAsync(Ct)).ShouldBe(1);
        }
        RunCount(world.SeriesId).ShouldBe(1);

        await InsertStoryApprovedAsync(world, episode, ApprovalSource.User);
        await using var disabled = await StartWorkerAsync(bibleEnabled: false);
        (await disabled.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        disabled.Jobs.Enqueued.ShouldNotContain(
            enqueue => enqueue.JobType == "ProposeBibleUpdateJob",
            "a switched-off proposal must not re-enqueue the job of the open run");
        RunCount(world.SeriesId).ShouldBe(1, "no second run is queued");
    }

    [Fact]
    public async Task StoryApproved_ReservesTheWorstCaseOfBothD7Attempts()
    {
        // P1 + Sec3 (ruling R3): the run reserves, guards and stores the bound of both D7 attempts, not of one call.
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episode = await SeedApprovedEpisodeAsync(world);
        await InsertStoryApprovedAsync(world, episode, ApprovalSource.User);

        await using var worker = await StartWorkerAsync();
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        RunCount(world.SeriesId).ShouldBe(1);
        SqlProbe.ScalarText(
                Database,
                $"""
                 SELECT (worst_case_usd = 0.004)::text FROM generation_runs
                 WHERE series_id = '{world.SeriesId.ToString("D", CultureInfo.InvariantCulture)}'
                 """)
            .ShouldBe("true", "FixedEstimator prices one call at 0.002; two attempts reserve 0.004");
    }

    /// <summary>The idempotency key of one approval: the episode's approved version, never the run's episode column.</summary>
    private static string Key(ApprovedEpisode episode) => $"bible:{episode.Id}:{episode.VersionId}";

    /// <summary>The seeded <c>queued</c> run the index refuses the second insert with.</summary>
    private const string BlockingKey = "bible:blocked:seed";

    /// <summary>One approved episode: the record and the version its summary is derived from.</summary>
    private sealed record ApprovedEpisode(Guid Id, Guid VersionId);

    /// <summary>A series of topic true crime with its bible row, for the defensive autopilot guard.</summary>
    private async Task<World> SeedTrueCrimeSeriesAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor, new FakeClock(Start))
            .CreateAsync(user.Id, topic: Topic.TrueCrime, ct: Ct);
        await SeriesBibleHarness.EnsureBibleAsync(Database, user.Id, series.Id);

        return new World(user.Id, series.Id);
    }

    private async Task<ApprovedEpisode> SeedApprovedEpisodeAsync(World world)
    {
        var episodeId = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        var versionId = Guid.Parse(
            SqlProbe.ScalarText(
                Database,
                $"SELECT approved_story_version_id FROM episodes WHERE id = '{episodeId.ToString("D", CultureInfo.InvariantCulture)}'"),
            CultureInfo.InvariantCulture);

        return new ApprovedEpisode(episodeId, versionId);
    }

    /// <summary>
    /// Seeds the <c>queued</c> series-level run of a first approval, the state the D2 active-run index holds
    /// against the second one. It is written through the production factory, so the row obeys the same scope rule.
    /// </summary>
    private async Task SeedActiveBibleRunAsync(World world)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(Start));
        var run = GenerationRun.Queue(
                world.UserId,
                world.SeriesId,
                episodeId: null,
                GenerationKind.BibleProposal,
                GenerationTrigger.System,
                BlockingKey,
                previousEpisodeStatus: null,
                inputFactSheetId: null,
                Guid.CreateVersion7(),
                0.002m,
                Start,
                countsTowardLimit: false)
            .Value;
        db.GenerationRuns.Add(run);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Writes one <c>story_approved.v1</c> row with the production serializer, so the payload is the wire shape.</summary>
    private Task InsertStoryApprovedAsync(World world, ApprovedEpisode episode, ApprovalSource approvalSource)
    {
        var id = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(
            new StoryApproved(episode.Id, world.SeriesId, world.UserId, episode.VersionId, approvalSource, Start),
            OutboxJson.Options);

        return Database.ExecuteAsync(
            $"""
             INSERT INTO outbox_messages (id, type, payload, user_id, occurred_at, next_attempt_at, attempts)
             VALUES ('{id}', 'story_approved.v1', '{payload}', '{world.UserId}', '{Start:O}', '{Start:O}', 0)
             """,
            Ct);
    }

    /// <summary>The run row of the series as its observable columns: NULL episode, counted, trigger, status, key, reservation, price.</summary>
    private string RunRow(Guid seriesId) =>
        SqlProbe.ScalarText(
            Database,
            $"""
             SELECT (episode_id IS NULL)::text || '|' || counts_toward_limit::text || '|' || trigger || '|' || status
                 || '|' || idempotency_key || '|' || (reservation_id IS NOT NULL)::text || '|' || (worst_case_usd > 0)::text
             FROM generation_runs WHERE series_id = '{seriesId.ToString("D", CultureInfo.InvariantCulture)}'
             """);

    private long RunCount(Guid seriesId) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM generation_runs WHERE series_id = '{seriesId.ToString("D", CultureInfo.InvariantCulture)}'");

    /// <summary>
    /// How many <c>story_approved.v1</c> rows this test's own owner had processed. Scoped to the owner: the class
    /// shares one database and every test of it seeds its own approvals, so an unscoped count would also see the
    /// rows of whatever else ran first and depend on the order.
    /// </summary>
    private long Deliveries(World world) =>
        SqlProbe.Count(
            Database,
            $"""
             SELECT count(*) FROM outbox_messages
             WHERE type = 'story_approved.v1'
                 AND user_id = '{world.UserId.ToString("D", CultureInfo.InvariantCulture)}'
                 AND processed_at IS NOT NULL
             """);

    /// <summary>
    /// The Worker host of these tests: the production graph with the priced catalogue replaced by a fixed bound and
    /// the platform budget by the TestKit's scriptable guard, so the assertions are about this handler's decisions.
    /// </summary>
    private async Task<WorkerHost> StartWorkerAsync(FakeBudgetGuard? guard = null, bool bibleEnabled = true)
    {
        var host = new WorkerHost(Database, new FakeClock(Start));
        host.WithSetting("Bible:Proposal:Enabled", bibleEnabled ? "true" : "false");
        host.ConfigureServices(services =>
        {
            services.RemoveAll<IBibleProposalCostEstimator>();
            services.AddSingleton<IBibleProposalCostEstimator>(new FixedEstimator());
            services.RemoveAll<IBudgetGuard>();
            services.AddSingleton<IBudgetGuard>(guard ?? new FakeBudgetGuard());
        });
        await host.StartAsync(Ct);

        return host;
    }

    /// <summary>The worst case of one proposal at a fixed bound: the catalogue and its price version are not this suite's subject.</summary>
    private sealed class FixedEstimator : IBibleProposalCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.002m, 8_000, 1_200));
    }
}
