using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Stories;
using NightTales.Application.Rendering;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using NightTales.Worker.Services;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The Worker's render poller (09 README BD7, WF07/W12; 09.6.3-T1 plan §2): driven one tick at a time with a
/// <see cref="FakeClock"/>, it claims jobs only while it holds the single-instance advisory lock, takes at most
/// <c>Render:PollBatchSize</c> due rows per tick at most <c>Render:PollParallelism</c> at a time, and runs each
/// of them in a scope pinned to the job owner's <see cref="ISystemContext"/>, so a poll never reads another
/// tenant's rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RenderPollingServiceTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static readonly string TryLockSql =
        $"SELECT pg_try_advisory_lock(hashtext('{RenderPollingService.LockKey}'))";

    private static readonly string UnlockSql =
        $"SELECT pg_advisory_unlock(hashtext('{RenderPollingService.LockKey}'))";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoInstances_OneWinsAdvisoryLock()
    {
        // The poller claims every due row of the database, so each test of this class starts from an empty
        // render_jobs table; the owners and episodes the other test seeded stay and are never claimed.
        ResetJobs();

        var clock = new FakeClock(Start);
        var owner = await SeedOwnerAsync(Ct);
        var jobId = await SeedJobAsync(owner, Start, Ct);

        var pins = new RecordingPins();
        await using var host = await StartHostAsync(clock, pins);
        var poller = host.Services.GetRequiredService<RenderPollingService>();

        // A second session holding the same advisory lock stands in for a second process: a tick that does not
        // get the lock must not claim anything at all.
        await using (var otherProcess = new NpgsqlConnection(Database.ConnectionString))
        {
            await otherProcess.OpenAsync(Ct);
            (await ScalarBoolAsync(otherProcess, TryLockSql)).ShouldBeTrue(
                "the test's own session holds the lock, like another Worker process would");

            (await poller.TickAsync(Ct)).ShouldBeFalse("another process holds the advisory lock (BD7)");
            pins.Pins.ShouldBeEmpty("a lost lock claims no job");

            (await ScalarBoolAsync(otherProcess, UnlockSql)).ShouldBeTrue("the other process lets the lock go");
        }

        (await poller.TickAsync(Ct)).ShouldBeTrue("the lock is free again, so this instance polls");

        // The one claimed job is pinned twice: the poller's dispatch scope (BD7) and the scope PollRenderJob
        // opens for its own unit of work, which it pins before it resolves any AppDbContext (D10). Both pins
        // carry the job's owner, and the scope is pinned to it.
        pins.Pins.Count.ShouldBe(2);
        pins.Pins.ShouldAllBe(pin => pin.UserId == owner.UserId);
        pins.Pins[0].VisibleJobIds.ShouldBe([jobId], "the pinned scope sees the owner's row and no other tenant's");
        pins.Pins[1].VisibleJobIds.ShouldBe([jobId]);

        // A second instance inside this process never polls while the first holds the lock for the loop's
        // lifetime: the advisory lock is session-scoped, so the two do not share it.
        using var second = SecondInstance(host);
        (await second.TickAsync(Ct)).ShouldBeFalse("the first instance holds the lock in this process too");
        pins.Pins.Count.ShouldBe(2, "only the advisory-lock winner ever claims a job");
    }

    [Fact]
    public async Task Tick_RespectsBatchAndParallelismBounds()
    {
        // Same reason as the lock test: the claim is cross-tenant, so no other test's rows may be due here.
        ResetJobs();

        // Only the two oldest rows are due at the clock's instant (Start + 45 s): betaDue polls 30 s in,
        // alphaSecond a minute in and betaFuture a day in.
        var clock = new FakeClock(Start.AddSeconds(45));
        var alpha = await SeedOwnerAsync(Ct);
        var beta = await SeedOwnerAsync(Ct);

        // The claim is ordered by next_poll_at, so the two oldest due rows are alpha's first job and beta's.
        var alphaFirst = await SeedJobAsync(alpha, Start, Ct);
        var betaDue = await SeedJobAsync(beta, Start.AddSeconds(30), Ct);
        var alphaSecond = await SeedJobAsync(alpha, Start.AddMinutes(1), Ct);
        var betaFuture = await SeedJobAsync(beta, Start.AddDays(1), Ct);

        var pins = new RecordingPins();
        await using var host = await StartHostAsync(clock, pins, pollBatchSize: 2, pollParallelism: 1);
        var poller = host.Services.GetRequiredService<RenderPollingService>();

        (await poller.TickAsync(Ct)).ShouldBeTrue();

        // One tick claims at most Render:PollBatchSize jobs, oldest next_poll_at first. Every claimed job is
        // pinned twice (the poller's dispatch scope and the job's own unit-of-work scope), and with
        // Render:PollParallelism = 1 the two jobs run one after the other, so the pins stay in claim order.
        pins.Pins.Select(pin => pin.UserId).ShouldBe(
            [alpha.UserId, alpha.UserId, beta.UserId, beta.UserId],
            customMessage: "the batch is the oldest due rows, in next_poll_at order");
        pins.MaxActive.ShouldBe(2, "one job at a time holds both of its pins (Render:PollParallelism = 1)");

        // Cross-tenant: the pinned scope runs the production tenant filter, so it sees exactly its owner's rows
        // and never the other tenant's.
        pins.Pins[0].VisibleJobIds.ShouldBe([alphaFirst, alphaSecond], ignoreOrder: true);
        pins.Pins[2].VisibleJobIds.ShouldBe([betaDue, betaFuture], ignoreOrder: true);

        // The one due row the batch left behind is claimed by the next tick; the row that is not due yet is not.
        // A poll moves the claim instant of every row it touched (RenderJob.ReschedulePoll), and the seeded rows
        // carry no adapter task, so this test moves those two claim instants itself.
        SqlProbe.Execute(
            Database,
            $"UPDATE render_jobs SET next_poll_at = '{Start.AddMinutes(5):O}' "
            + $"WHERE id IN ('{alphaFirst}', '{betaDue}')");

        clock.Set(Start.AddMinutes(2));

        (await poller.TickAsync(Ct)).ShouldBeTrue();

        pins.Pins.Count.ShouldBe(6, "the third due row is claimed by the next tick, with its two pins");
        pins.Pins[4].UserId.ShouldBe(alpha.UserId);
        pins.Pins[4].VisibleJobIds.ShouldBe([alphaFirst, alphaSecond], ignoreOrder: true);
        pins.Pins[5].VisibleJobIds.ShouldBe([alphaFirst, alphaSecond], ignoreOrder: true);
        pins.MaxActive.ShouldBe(2, "every tick keeps the same parallelism bound");
    }

    /// <summary>
    /// A started harness over the class database with the class <see cref="FakeClock"/>, the tick's bounds and the
    /// recording system context in place of the production one. The poller's own loop is dropped by
    /// <see cref="WorkerHost"/>; the singleton stays, so a test drives one tick.
    /// </summary>
    private async Task<WorkerHost> StartHostAsync(
        FakeClock clock,
        RecordingPins pins,
        int pollBatchSize = 50,
        int pollParallelism = 4)
    {
        var host = new WorkerHost(Database, clock)
            .WithSetting(
                $"{RenderOptions.SectionName}:{nameof(RenderOptions.PollBatchSize)}",
                pollBatchSize.ToString(CultureInfo.InvariantCulture))
            .WithSetting(
                $"{RenderOptions.SectionName}:{nameof(RenderOptions.PollParallelism)}",
                pollParallelism.ToString(CultureInfo.InvariantCulture))
            .ConfigureServices(services =>
            {
                services.RemoveAll<ISystemContext>();
                services.AddScoped<ISystemContext>(provider => new RecordingSystemContext(
                    provider.GetRequiredService<ScopedCurrentUser>(),
                    provider,
                    pins));
            });

        await host.StartAsync(Ct);

        return host;
    }

    /// <summary>
    /// A second poller over the same host graph, built by hand because the host holds exactly one singleton: the
    /// in-process half of the single-instance contract.
    /// </summary>
    private static RenderPollingService SecondInstance(WorkerHost host) =>
        new(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<IOptions<RenderOptions>>(),
            host.Services.GetRequiredService<RenderMetrics>(),
            host.Services.GetRequiredService<IClock>(),
            host.Database.ConnectionString,
            NullLogger<RenderPollingService>.Instance);

    /// <summary>One owner (a user and their series), so several episodes of it can carry several render jobs.</summary>
    private sealed record Owner(Guid UserId, Guid SeriesId);

    /// <summary>Empties <c>render_jobs</c>, the one table whose due rows a tick claims regardless of tenant.</summary>
    private void ResetJobs() => SqlProbe.Execute(Database, "DELETE FROM render_jobs");

    private async Task<Owner> SeedOwnerAsync(CancellationToken ct)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: ct);

        return new Owner(user.Id, series.Id);
    }

    /// <summary>
    /// Gives <paramref name="owner"/> a fresh episode with its story version and a <c>submitted</c>
    /// <c>render_jobs</c> row due at <paramref name="nextPollAt"/> (its own episode, because BD5's partial unique
    /// index allows one active render per episode). The row is written with raw SQL — the point here is the
    /// poller's claim, not the command that would have created it — and its <c>worker_node_id</c> points at no
    /// node, so <c>PollRenderJob</c> skips it with a logged warning instead of calling an adapter.
    /// </summary>
    private async Task<Guid> SeedJobAsync(Owner owner, DateTimeOffset nextPollAt, CancellationToken ct)
    {
        var actor = new TestCurrentUser { Id = owner.UserId };
        var episode = await new EpisodeBuilder(Database, actor, new FakeClock(Start))
            .CreateAsync(owner.UserId, owner.SeriesId, ct: ct);
        var draft = Draft();
        var version = StoryVersion.Create(
            owner.UserId,
            episode.Id,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            "model-x",
            "rel=1",
            null,
            null,
            null,
            null,
            null,
            Start);

        await using (var db = Database.CreateAppDbContext(actor, new FakeClock(Start)))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(ct);
        }

        var jobId = Guid.CreateVersion7();
        var @params = ParamsJson(version.Id);

        SqlProbe.Execute(Database,
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, percent, params, attempt, worker_node_id, submitted_at, started_at, next_poll_at) "
            + $"VALUES ('{jobId}', '{owner.UserId}', '{episode.Id}', '{version.Id}', '{Guid.CreateVersion7()}', 'submitted', "
            + $"'{RenderJob.QueuedStage}', 0, '{@params}'::jsonb, 1, "
            + $"'{Guid.CreateVersion7()}', '{Start:O}', '{Start:O}', '{nextPollAt:O}')");

        return jobId;
    }

    /// <summary>
    /// A complete <c>render-params.v1</c> document (05 D11): every member of <see cref="RenderParamsV1"/> is
    /// <c>[JsonRequired]</c>, so a row whose <c>params</c> omitted one could not be loaded at all. Nothing on the poll
    /// path cross-checks these values — the job is skipped before any adapter call — but the stored document is the
    /// real one the column's converter writes.
    /// </summary>
    private static string ParamsJson(Guid storyVersionId) =>
        JsonSerializer.Serialize(new RenderParamsV1(
            RenderParamsV1.CurrentMapperVersion,
            storyVersionId,
            "landscape",
            "vi",
            "kokoro:test",
            Guid.CreateVersion7(),
            "style=test@0123abcd;neg=v1",
            new ImagePlanSummary(3, 1, 2, 1, 0, [], ["paragraph:1"], "style=test@0123abcd;neg=v1"),
            new SubtitlesParams(true, "landscape@1"),
            null,
            3));

    /// <summary>The smallest clean story document the version factory accepts; its text is never rendered here.</summary>
    private static StoryContentV1 Draft() => new(
        "The pier at dawn",
        new StorySegmentV1("Grey mist drifted over the riverside.", ["quiet riverside morning"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", ["empty wooden pier at dusk"], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    private static async Task<bool> ScalarBoolAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteScalarAsync(Ct) is true;
    }

    /// <summary>
    /// The scoped <see cref="ISystemContext"/> the poller's per-job scopes resolve. It delegates the pin to the
    /// production <see cref="ScopedCurrentUser"/> — so the tenant filter under test is the real one — and records,
    /// while the scope is pinned, which <c>render_jobs</c> rows that scope can read.
    /// </summary>
    private sealed class RecordingSystemContext(
        ISystemContext inner,
        IServiceProvider services,
        RecordingPins pins) : ISystemContext
    {
        /// <inheritdoc />
        public IDisposable For(Guid userId)
        {
            var handle = inner.For(userId);
            var lease = pins.Enter(userId);

            try
            {
                // The pin is in place, so this ordinary tenant-filtered read is exactly what the job itself may
                // see: another tenant's rows must never be among them.
                var db = services.GetRequiredService<IAppDbContext>();
                var visible = db.RenderJobs.AsNoTracking().Select(job => job.Id).ToArray();
                lease.Observe(visible);
            }
            catch
            {
                lease.Dispose();
                handle.Dispose();

                throw;
            }

            return new PinnedScope(handle, lease);
        }

        private sealed class PinnedScope(IDisposable handle, RecordingPins.PinLease lease) : IDisposable
        {
            public void Dispose()
            {
                lease.Dispose();
                handle.Dispose();
            }
        }
    }

    /// <summary>What every pin of every tick saw; the tick's jobs run in parallel, so the state is guarded.</summary>
    private sealed class RecordingPins
    {
        private readonly Lock _gate = new();
        private readonly List<Pin> _pins = [];
        private int _active;
        private int _maxActive;

        public IReadOnlyList<Pin> Pins
        {
            get
            {
                lock (_gate)
                {
                    return [.. _pins];
                }
            }
        }

        /// <summary>The most jobs that were ever inside their pinned scope at the same time.</summary>
        public int MaxActive
        {
            get
            {
                lock (_gate)
                {
                    return _maxActive;
                }
            }
        }

        public PinLease Enter(Guid userId)
        {
            var active = Interlocked.Increment(ref _active);
            lock (_gate)
            {
                _maxActive = Math.Max(_maxActive, active);
            }

            return new PinLease(this, userId);
        }

        internal void Observe(Guid userId, Guid[] visibleJobIds)
        {
            lock (_gate)
            {
                _pins.Add(new Pin(userId, visibleJobIds));
            }
        }

        internal sealed class PinLease(RecordingPins owner, Guid userId) : IDisposable
        {
            private bool _disposed;

            public void Observe(Guid[] visibleJobIds) => owner.Observe(userId, visibleJobIds);

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Interlocked.Decrement(ref owner._active);
            }
        }

        public sealed record Pin(Guid UserId, Guid[] VisibleJobIds);
    }
}
