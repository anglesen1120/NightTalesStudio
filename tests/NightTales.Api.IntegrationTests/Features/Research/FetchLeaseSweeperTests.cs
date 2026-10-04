using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Research.Fetcher;
using NightTales.Application.Features.Research.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using NightTales.Worker.Jobs;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// <see cref="FetchLeaseSweeperJob"/> through the Worker's own graph (02.5.2-T2, 02 D10): the real
/// <c>IFetchLeaseSweepStore</c> statement on PostgreSQL 17, and the per-row pinned scope that fails the source of a task
/// that ran out of attempts. Tasks are seeded with SQL and their leases are put in the past with the database clock
/// (<c>now()</c>), the clock the sweep statement itself uses.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FetchLeaseSweeperTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Sweeper_RequeuesExpiredLease_WithBackoff()
    {
        var world = await SeedWorldAsync();
        var source = SeedSource(world, "a.example");
        var task = SeedLeasedTask(world, source, attempts: 1, leaseUntil: "now() - interval '1 minute'");
        await using var host = await StartWorkerAsync();

        var summary = await SweepAsync(host);

        summary.ShouldBe(new FetchLeaseSweepSummary(Requeued: 1, Failed: 0, SourcesFailed: 0));
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || attempts::text || '/' || coalesce(error_code, '-') FROM fetch_tasks WHERE id = '{task}'")
            .ShouldBe("queued/1/-");
        SqlProbe.Count(
                Database,
                "SELECT count(*) FROM fetch_tasks "
                + $"WHERE id = '{task}' AND not_before > now() + interval '20 seconds' AND not_before <= now() + interval '31 seconds'")
            .ShouldBe(1, "the requeue backoff is Fetcher:RequeueBackoffSeconds (30 s) from the sweep");
        SqlProbe.Count(Database, $"SELECT count(*) FROM fetch_tasks WHERE id = '{task}' AND finished_at IS NULL")
            .ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT status FROM story_sources WHERE id = '{source}'").ShouldBe("pending");
    }

    [Fact]
    public async Task Sweeper_ClearsLeasedAtOnRequeue()
    {
        var world = await SeedWorldAsync();
        var source = SeedSource(world, "b.example");
        var task = SeedLeasedTask(world, source, attempts: 1, leaseUntil: "now() - interval '5 seconds'");
        await using var host = await StartWorkerAsync();

        (await SweepAsync(host)).Requeued.ShouldBe(1);

        // leased_at is what fetch_duration_seconds measures from, so a requeued task must not carry the old one into its
        // next lease; the lease columns are cleared together (ck_fetch_tasks_leased allows it only for a non-leased task).
        SqlProbe.Count(
                Database,
                "SELECT count(*) FROM fetch_tasks "
                + $"WHERE id = '{task}' AND leased_at IS NULL AND leased_by IS NULL AND lease_until IS NULL")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Sweeper_SecondExpiry_MarksTaskAndSourceFailed()
    {
        var world = await SeedWorldAsync();
        var source = SeedSource(world, "c.example");
        var task = SeedLeasedTask(world, source, attempts: 2, leaseUntil: "now() - interval '1 minute'");
        await using var host = await StartWorkerAsync();

        var summary = await SweepAsync(host);

        summary.ShouldBe(new FetchLeaseSweepSummary(Requeued: 0, Failed: 1, SourcesFailed: 1));
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || attempts::text || '/' || error_code FROM fetch_tasks WHERE id = '{task}'")
            .ShouldBe("failed/2/source_unreachable");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM fetch_tasks WHERE id = '{task}' AND finished_at IS NOT NULL "
                + "AND leased_by IS NULL AND lease_until IS NULL AND leased_at IS NULL")
            .ShouldBe(1);
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || error_code || '/' || (fetched_at IS NOT NULL)::text FROM story_sources WHERE id = '{source}'")
            .ShouldBe("failed/source_unreachable/true");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Sweeper_SecondExpiry_LeavesASourceThatIsNoLongerPendingAlone()
    {
        var world = await SeedWorldAsync();
        var source = SeedSource(world, "d.example", status: "failed", errorCode: "source_blocked_by_robots");
        var task = SeedLeasedTask(world, source, attempts: 2, leaseUntil: "now() - interval '1 minute'");
        await using var host = await StartWorkerAsync();

        var summary = await SweepAsync(host);

        summary.ShouldBe(new FetchLeaseSweepSummary(Requeued: 0, Failed: 1, SourcesFailed: 0));
        SqlProbe.ScalarText(Database, $"SELECT status FROM fetch_tasks WHERE id = '{task}'").ShouldBe("failed");
        SqlProbe.ScalarText(Database, $"SELECT error_code FROM story_sources WHERE id = '{source}'")
            .ShouldBe("source_blocked_by_robots");
    }

    [Fact]
    public async Task Sweeper_Reconcile_FailsThePendingSourceOfAnAlreadyFailedTask()
    {
        var world = await SeedWorldAsync();
        var source = SeedSource(world, "e.example");
        _ = SeedFailedTask(world, source, "source_blocked_by_robots");
        await using var host = await StartWorkerAsync();

        var summary = await SweepAsync(host);

        summary.ShouldBe(new FetchLeaseSweepSummary(Requeued: 0, Failed: 0, SourcesFailed: 1));
        SqlProbe.ScalarText(Database, $"SELECT status || '/' || error_code FROM story_sources WHERE id = '{source}'")
            .ShouldBe("failed/source_blocked_by_robots");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);

        (await SweepAsync(host)).SourcesFailed.ShouldBe(0);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Sweeper_Reconcile_IgnoresAFailedTaskThatFinishedLongAgo()
    {
        var world = await SeedWorldAsync();
        var source = SeedSource(world, "f.example");
        var task = SeedFailedTask(world, source, "source_unreachable");
        SqlProbe.Execute(Database, $"UPDATE fetch_tasks SET finished_at = now() - interval '48 hours' WHERE id = '{task}'");
        await using var host = await StartWorkerAsync();

        var summary = await SweepAsync(host);

        summary.SourcesFailed.ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM story_sources WHERE id = '{source}'").ShouldBe("pending");
    }

    [Fact]
    public async Task Sweeper_SetsTheLeaseGaugeFromTheDatabase_AfterRequeues()
    {
        var world = await SeedWorldAsync();
        _ = SeedLeasedTask(world, SeedSource(world, "g1.example"), attempts: 1, leaseUntil: "now() - interval '1 minute'");
        _ = SeedLeasedTask(world, SeedSource(world, "g2.example"), attempts: 1, leaseUntil: "now() - interval '1 minute'");
        _ = SeedLeasedTask(world, SeedSource(world, "g3.example"), attempts: 1, leaseUntil: "now() + interval '5 minutes'");
        await using var host = await StartWorkerAsync();
        using var scope = host.CreateScope();
        var gauge = scope.ServiceProvider.GetRequiredService<FetchLeaseGauge>();

        gauge.Value.ShouldBe(0);
        _ = await SweepAsync(host);

        // The database may hold leases from sibling tests of this class; the gauge must equal the database's own count,
        // which includes the one live lease seeded here and none of the two requeued ones.
        var leased = SqlProbe.Count(Database, "SELECT count(*) FROM fetch_tasks WHERE status = 'leased'");
        leased.ShouldBeGreaterThanOrEqualTo(1);
        ((long)gauge.Value).ShouldBe(leased);
    }

    [Fact]
    public async Task Sweeper_IgnoresActiveLease()
    {
        var world = await SeedWorldAsync();
        var source = SeedSource(world, "e.example");
        var task = SeedLeasedTask(world, source, attempts: 2, leaseUntil: "now() + interval '60 seconds'");
        await using var host = await StartWorkerAsync();

        var summary = await SweepAsync(host);

        summary.ShouldBe(new FetchLeaseSweepSummary(0, 0, 0));
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || attempts::text || '/' || leased_by FROM fetch_tasks WHERE id = '{task}'")
            .ShouldBe("leased/2/fetcher-1");
        SqlProbe.ScalarText(Database, $"SELECT status FROM story_sources WHERE id = '{source}'").ShouldBe("pending");
    }

    [Fact]
    public async Task Sweeper_IsIdempotentAcrossTwoRuns()
    {
        var world = await SeedWorldAsync();
        var requeuedSource = SeedSource(world, "f.example");
        var failedSource = SeedSource(world, "g.example");
        var requeued = SeedLeasedTask(world, requeuedSource, attempts: 1, leaseUntil: "now() - interval '1 minute'");
        var failed = SeedLeasedTask(world, failedSource, attempts: 2, leaseUntil: "now() - interval '1 minute'");
        await using var host = await StartWorkerAsync();

        var first = await SweepAsync(host);
        var notBefore = SqlProbe.ScalarText(Database, $"SELECT not_before::text FROM fetch_tasks WHERE id = '{requeued}'");
        var second = await SweepAsync(host);

        first.ShouldBe(new FetchLeaseSweepSummary(Requeued: 1, Failed: 1, SourcesFailed: 1));
        second.ShouldBe(new FetchLeaseSweepSummary(0, 0, 0));
        SqlProbe.ScalarText(Database, $"SELECT status || '/' || attempts::text FROM fetch_tasks WHERE id = '{requeued}'")
            .ShouldBe("queued/1");
        SqlProbe.ScalarText(Database, $"SELECT not_before::text FROM fetch_tasks WHERE id = '{requeued}'")
            .ShouldBe(notBefore, "the second run must not push the backoff out again");
        SqlProbe.ScalarText(Database, $"SELECT status FROM fetch_tasks WHERE id = '{failed}'").ShouldBe("failed");
        SqlProbe.ScalarText(Database, $"SELECT status FROM story_sources WHERE id = '{failedSource}'").ShouldBe("failed");
    }

    [Fact]
    public void Sweeper_Registration_UpsertsEveryMinuteOnTheDefaultQueue()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scheduler = new InlineJobScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        new FetchLeaseSweeperRegistration().Register(scheduler);

        scheduler.RecurringUpserts.ShouldHaveSingleItem().ShouldBe(new RecordedRecurringCall(
            FetchLeaseSweeperRegistration.RecurringId,
            nameof(FetchLeaseSweeperJob),
            JobQueues.Name(JobQueue.Default),
            Hangfire.Cron.Minutely()));
        FetchLeaseSweeperRegistration.RecurringId.ShouldBe("fetch-lease-sweeper");
    }

    [Theory]
    [InlineData(0, 30, 60)]
    [InlineData(2, 0, 60)]
    [InlineData(2, 30, 0)]
    public void Options_NonPositiveValue_IsInvalid(int maxAttempts, int backoffSeconds, int sweepIntervalSeconds)
    {
        FetchLeaseSweeperOptions.Validate(new FetchLeaseSweeperOptions
        {
            MaxAttempts = maxAttempts,
            RequeueBackoffSeconds = backoffSeconds,
            SweepIntervalSeconds = sweepIntervalSeconds,
        }).Count.ShouldBe(1);
        FetchLeaseSweeperOptions.Validate(new FetchLeaseSweeperOptions()).ShouldBeEmpty();
    }

    /// <summary>Seeds one user, series and episode.</summary>
    private async Task<World> SeedWorldAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return new World(user.Id, episode.Id);
    }

    private Guid SeedSource(World world, string domain, string status = "pending", string? errorCode = null)
    {
        var id = Guid.NewGuid();
        SqlProbe.Execute(
            Database,
            "INSERT INTO story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, error_code, created_at) "
            + $"VALUES ('{id}', '{world.UserId}', '{world.EpisodeId}', 'url', 'https://{domain}/', 'https://{domain}/', '{domain}', "
            + $"'{status}', {(errorCode is null ? "NULL" : $"'{errorCode}'")}, now())");

        return id;
    }

    private Guid SeedLeasedTask(World world, Guid source, int attempts, string leaseUntil)
    {
        var id = Guid.NewGuid();
        SqlProbe.Execute(
            Database,
            "INSERT INTO fetch_tasks (id, user_id, episode_id, source_id, kind, url, registrable_domain, status, attempts, "
            + "not_before, created_at, leased_by, leased_at, lease_until) "
            + $"VALUES ('{id}', '{world.UserId}', '{world.EpisodeId}', '{source}', 'fetch_source', 'https://x.example/', 'x.example', "
            + $"'leased', {attempts}, now() - interval '10 minutes', now() - interval '10 minutes', 'fetcher-1', "
            + $"now() - interval '3 minutes', {leaseUntil})");

        return id;
    }

    private Guid SeedFailedTask(World world, Guid source, string errorCode)
    {
        var id = Guid.NewGuid();
        SqlProbe.Execute(
            Database,
            "INSERT INTO fetch_tasks (id, user_id, episode_id, source_id, kind, url, registrable_domain, status, attempts, "
            + "not_before, created_at, error_code, finished_at) "
            + $"VALUES ('{id}', '{world.UserId}', '{world.EpisodeId}', '{source}', 'fetch_source', 'https://x.example/', 'x.example', "
            + $"'failed', 1, now() - interval '10 minutes', now() - interval '10 minutes', '{errorCode}', now() - interval '1 minute')");

        return id;
    }

    private async Task<WorkerHost> StartWorkerAsync()
    {
        var host = new WorkerHost(Database, new FakeClock(Now));
        await host.StartAsync(Ct);

        return host;
    }

    private static async Task<FetchLeaseSweepSummary> SweepAsync(WorkerHost host)
    {
        using var scope = host.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<FetchLeaseSweeperJob>().SweepAsync(Ct);
    }

    private sealed record World(Guid UserId, Guid EpisodeId);
}
