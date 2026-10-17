using System.Globalization;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Subtitles;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Usage;
using NightTales.RenderNode.Contract;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Rendering;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// <see cref="PollRenderJob"/> (09 README BD7, BD11, WT28/WT45/WT50/WT51/WT54; 09.6.3-T1 steps 3-7) against a
/// real database through the Worker host: the one status call per tick, the progress write and its realtime
/// push, the timeout watchdog, the terminal branches (succeeded → ingest, failed → usage/classify/refund/audit
/// and the retry verdict, cancelled → the only refund of a cancelled render), and the terminal-row skip.
/// </summary>
/// <remarks>
/// The job is called directly with the row id, exactly as <c>RenderPollingService</c> calls it, so nothing here
/// depends on the poller's claim. The status answers come from the shared <see cref="FakeRenderClient"/> and the
/// money ports are hand-written recorders, so every assertion is about a value this test wrote or the job wrote.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PollRenderJobTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Progress_MapsAdapterStage_PercentNeverDecreases()
    {
        var world = await SeedJobAsync("running", "voice", percent: 20);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        // A status that moved: written, pushed, and the claim instant moves one cadence.
        render.EnqueueStatus(Status(world, RenderStates.Running, "images", 40));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "running", stage: "images", percent: 40);
        ScalarBool("SELECT (next_poll_at = submitted_at + interval '10 seconds')::text", world).ShouldBeTrue(
            "a polled row is claimed again one Render:PollSeconds later");

        var pushed = harness.Notifier.Events.ShouldHaveSingleItem();
        pushed.UserId.ShouldBe(world.UserId);
        pushed.Event.Name.ShouldBe(RealtimeEventName.RenderProgress);
        pushed.Event.Ids.EpisodeId.ShouldBe(world.EpisodeId);
        pushed.Event.Ids.RenderJobId.ShouldBe(world.RenderJobId);
        pushed.Event.Data!.Stage.ShouldBe("images", "the payload carries the adapter's wire stage");
        pushed.Event.Data.Percent.ShouldBe((short)40);
        JsonSerializer.Serialize(pushed.Event).Length.ShouldBeLessThan(
            8 * 1024,
            "the serialized pg_notify body stays inside the 8 KB budget (WT54)");

        // A status that moves backwards is not credible: the stored percent never decreases, so the attempt
        // ends as a lost render instead of silently clamping.
        render.EnqueueStatus(Status(world, RenderStates.Running, "images", 25));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "failed", stage: "images", percent: 40, errorCode: "render_lost");
        ScalarText("SELECT percent::text", world).ShouldBe("40", "UpdateProgress never decreases percent (AC5)");
        harness.Money.Refunds.ShouldBe([(world.ReservationId, "render_failed")]);
    }

    [Fact]
    public async Task Progress_Unchanged_SendsNoRealtimeEvent()
    {
        var world = await SeedJobAsync("running", "voice", percent: 40);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        render.EnqueueStatus(Status(world, RenderStates.Running, "voice", 40));

        await PollAsync(harness.Host, world.RenderJobId);

        harness.Notifier.Events.ShouldBeEmpty("a status with an unchanged stage and percent pushes nothing");
        render.StatusCalls.ShouldBe(1);
        AssertRow(world, status: "running", stage: "voice", percent: 40);
        ScalarBool("SELECT (next_poll_at = submitted_at + interval '10 seconds')::text", world).ShouldBeTrue(
            "the claim instant still moves, or every tick would claim this row again");
    }

    [Fact]
    public async Task Deferred_OnlyMovesTheClaimInstant()
    {
        var world = await SeedJobAsync("running", "voice", percent: 20);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        render.EnqueueFailure(
            RenderClientMethod.GetStatus,
            new RenderClientFailure(
                RenderClientFailureKind.Deferred,
                "adapter_capacity_full",
                "scripted",
                Alert: false,
                RetryAfter: TimeSpan.FromSeconds(30)));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "running", stage: "voice", percent: 20);
        ScalarBool("SELECT (next_poll_at > submitted_at)::text", world).ShouldBeTrue();
        harness.Notifier.Events.ShouldBeEmpty();
        harness.Money.Refunds.ShouldBeEmpty();
        (await ScalarAsync($"SELECT attempt::text FROM render_jobs WHERE id = '{world.RenderJobId}'")).ShouldBe("1");
    }

    [Fact]
    public async Task Terminal_Succeeded_EnqueuesIngest()
    {
        var world = await SeedJobAsync("running", "voice", percent: 60);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        render.EnqueueStatus(Status(world, RenderStates.Succeeded, "done", 100));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "ingesting", stage: "done", percent: 100);
        harness.Ingest.Enqueued.ShouldBe([world.RenderJobId], "the committed row is handed to the ingest (WT45)");
        harness.Money.Refunds.ShouldBeEmpty("a succeeded render is settled by the ingest, never refunded (WT51)");
        CountAudit(world).ShouldBe(0, "no terminal failure or cancellation row exists");
        EpisodeStatus(world).ShouldBe("rendering", "the episode is not taken out of rendering");
    }

    [Fact]
    public async Task Terminal_Failed_RunsUsageThenClassifyThenFail()
    {
        var world = await SeedJobAsync("running", "assembling", percent: 80);
        var render = new FakeRenderClient();
        var hooks = new RecordingHooks
        {
            // A code that is never retried (03 D7): the attempt is final, so the episode comes back and exactly
            // one render_failed row is raised.
            Classification = new RenderFailureClassification("render_quality_failed", null),
        };
        await using var harness = await StartHostAsync(render, hooks);

        render.EnqueueStatus(new RenderStatusV1(
            RenderNodeSchemas.RenderStatus,
            world.TaskId,
            world.RenderJobId,
            RenderStates.Failed,
            "assembling",
            80,
            false,
            Start,
            Start,
            Start.AddMinutes(5),
            new RenderOutcomeV1("render_quality_failed", "quality", "The render did not pass the quality check."),
            null,
            null,
            null,
            null));

        await PollAsync(harness.Host, world.RenderJobId);

        hooks.Calls.ShouldBe(["usage", "classify"], "03.4.2-T1's order: usage first, then the classification");
        AssertRow(world, status: "failed", stage: "assembling", percent: 80, errorCode: "render_quality_failed");

        harness.Money.Refunds.ShouldBe([(world.ReservationId, "render_failed")]);

        var audit = SqlProbe.QueryStrings(
            Database,
            "SELECT action || '|' || actor_type || '|' || category || '|' || visibility || '|' || entity_type "
            + $"|| '|' || (data->>'code') || '|' || (data->>'final') FROM audit_logs WHERE entity_id = '{world.RenderJobId}'");
        audit.ShouldBe(["render.failed|system|episodes|user|render_job|render_quality_failed|true"]);
        SqlProbe.ScalarText(
            Database,
            $"SELECT (data ? 'retryJobId')::text FROM audit_logs WHERE entity_id = '{world.RenderJobId}'")
            .ShouldBe("false", "the final verdict carries no retry row");

        (await ScalarAsync($"SELECT count(*)::text FROM render_jobs WHERE episode_id = '{world.EpisodeId}'"))
            .ShouldBe("1", "a never-retried code creates no second attempt (AC7)");
        CountOutbox(world).ShouldBe(1, "exactly one render_failed.v1 row carries the final verdict");
        EpisodeStatus(world).ShouldBe("story_approved", "a final failure hands the episode back");

        // The row is terminal now: a second poll of it does nothing at all — no adapter call, no second refund,
        // no second audit row (AC13).
        await PollAsync(harness.Host, world.RenderJobId);

        render.StatusCalls.ShouldBe(1);
        harness.Money.Refunds.Count.ShouldBe(1);
        CountAudit(world).ShouldBe(1);
        CountOutbox(world).ShouldBe(1);
    }

    [Fact]
    public async Task Timeout_AfterSixtyMinutes_FailsRenderTimeout_NoResubmit()
    {
        var world = await SeedJobAsync("running", "subtitles", percent: 50, submittedMinutesAgo: 61, startedMinutesAgo: 60);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        await PollAsync(harness.Host, world.RenderJobId);

        // The watchdog answers before the node is even asked, and the same adapter task is never re-submitted.
        render.StatusCalls.ShouldBe(0, "the watchdog is evaluated before the status call");
        render.SubmitCalls.ShouldBe(0);

        AssertRow(world, status: "failed", stage: "subtitles", percent: 50, errorCode: "render_timeout");
        ScalarText("SELECT adapter_task_id", world).ShouldBe(world.TaskId.ToString());
        harness.Money.Refunds.ShouldBe([(world.ReservationId, "render_failed")]);

        // render_timeout is transient, so the retry policy queues attempt 2 — a new row, its own reservation.
        var retries = SqlProbe.QueryStrings(
            Database,
            $"SELECT id || '|' || attempt::text || '|' || status FROM render_jobs WHERE episode_id = '{world.EpisodeId}'");
        retries.Count.ShouldBe(2);
        retries.ShouldContain($"{world.RenderJobId}|1|failed");
        retries.ShouldContain(row => row.EndsWith("|2|queued", StringComparison.Ordinal), "the retry is queued");
        SqlProbe.ScalarText(
            Database,
            $"SELECT (next_poll_at IS NULL)::text FROM render_jobs WHERE id = '{world.RenderJobId}'")
            .ShouldBe("true", "a failed row is never claimed again");

        // The poller's claim is status-based, so a terminal row stays out of it.
        SqlProbe.Count(Database, "SELECT count(*) FROM render_jobs WHERE status IN ('submitted','running')").ShouldBe(0);
    }

    [Fact]
    public async Task Poll_TerminalJob_IsSkipped()
    {
        var world = await SeedJobAsync("succeeded", "done", percent: 100);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        await PollAsync(harness.Host, world.RenderJobId);

        render.StatusCalls.ShouldBe(0, "only a submitted or running row is polled (WT45)");
        harness.Notifier.Events.ShouldBeEmpty();
        harness.Ingest.Enqueued.ShouldBeEmpty("the ingest is enqueued once, by the tick that recorded success");
        AssertRow(world, status: "succeeded", stage: "done", percent: 100);
        CountOutbox(world).ShouldBe(0);
    }

    [Fact]
    public async Task Terminal_Cancelled_RefundsReservationOnce_OnlyAtTerminal()
    {
        var world = await SeedJobAsync("running", "voice", percent: 30, cancelRequested: true);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        // A row whose owner asked for a cancellation but whose adapter task is still running keeps running: only
        // the terminal cancelled answer refunds it (WT51).
        render.EnqueueStatus(Status(world, RenderStates.Running, "voice", 30));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "running", stage: "voice", percent: 30);
        harness.Money.Refunds.ShouldBeEmpty("a delete or a cancel request never refunds by itself");

        render.EnqueueStatus(Status(world, RenderStates.Cancelled, "voice", 30));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "cancelled", stage: "voice", percent: 30);
        harness.Money.Refunds.ShouldBe(
            [(world.ReservationId, "render_cancelled")],
            "the terminal cancelled refunds its own reservation, once, by reservation id");
        SqlProbe.QueryStrings(
            Database,
            $"SELECT action || '|' || actor_type || '|' || (data->>'code') FROM audit_logs WHERE entity_id = '{world.RenderJobId}'")
            .ShouldBe(["render.cancelled|system|render_cancelled"]);
        EpisodeStatus(world).ShouldBe("story_approved");

        // A second poll of the terminal row must not refund again.
        await PollAsync(harness.Host, world.RenderJobId);

        render.StatusCalls.ShouldBe(2);
        harness.Money.Refunds.Count.ShouldBe(1);
        CountAudit(world).ShouldBe(1);
    }

    [Fact]
    public async Task Terminal_SucceededAfterCancel_IsIngestedNotCancelled()
    {
        var world = await SeedJobAsync("running", "voice", percent: 30, cancelRequested: true);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        render.EnqueueStatus(Status(world, RenderStates.Succeeded, "done", 100));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "ingesting", stage: "done", percent: 100);
        harness.Ingest.Enqueued.ShouldBe([world.RenderJobId], "an adapter that reports succeeded wins over the cancel");
        harness.Money.Refunds.ShouldBeEmpty("the ingest settles it; nothing is refunded (WT51)");
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE entity_id = '{world.RenderJobId}'")
            .ShouldBe(0, "no cancellation row is written for a render that succeeded");
        EpisodeStatus(world).ShouldBe("rendering");
    }

    [Fact]
    public async Task Terminal_TaskIdMismatch_IsALostRender()
    {
        var world = await SeedJobAsync("running", "voice", percent: 30);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        // The node answered about a task this row no longer owns (H2): never progress, always a lost render.
        render.EnqueueStatus(new RenderStatusV1(
            RenderNodeSchemas.RenderStatus,
            Guid.CreateVersion7(),
            world.RenderJobId,
            RenderStates.Running,
            "images",
            90,
            false,
            Start,
            Start,
            null,
            null,
            null,
            null,
            null,
            null));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "failed", stage: "voice", percent: 30, errorCode: "render_lost");
        ScalarText("SELECT percent::text", world).ShouldBe("30", "another task's progress is never applied");
        harness.Notifier.Events.ShouldBeEmpty();
    }

    /// <summary>The ids one seeded poll world carries, plus the adapter task the row submitted.</summary>
    private sealed record World(
        Guid UserId,
        Guid EpisodeId,
        Guid StoryVersionId,
        Guid RenderJobId,
        Guid NodeId,
        Guid ReservationId,
        Guid TaskId);

    /// <summary>
    /// A started Worker host with the render ports scripted: the status answers come from
    /// <see cref="FakeRenderClient"/>, the realtime push, the ingest enqueue, the 03.4.2-T1 hooks and the four
    /// money ports are recorders. Nothing else about the composition root is changed.
    /// </summary>
    private async Task<Harness> StartHostAsync(FakeRenderClient render, RecordingHooks? hooks = null)
    {
        var harness = new Harness(render, hooks ?? new RecordingHooks());

        var host = new WorkerHost(Database, new FakeClock(Start))
            .WithSetting("Render:PollSeconds", "10")
            .WithSetting("Render:MaxMinutes", "60")
            .ConfigureServices(services =>
            {
                services.AddRenderJobs();
                services.RemoveAll<IRenderClient>();
                services.AddSingleton<IRenderClient>(render);
                services.RemoveAll<IRealtimeNotifier>();
                services.AddSingleton<IRealtimeNotifier>(harness.Notifier);
                services.RemoveAll<IRenderIngestEnqueuer>();
                services.AddSingleton<IRenderIngestEnqueuer>(harness.Ingest);
                services.RemoveAll<IRenderTerminalHooks>();
                services.AddSingleton<IRenderTerminalHooks>(harness.Hooks);
                services.RemoveAll<ICreditLedger>();
                services.AddSingleton<ICreditLedger>(harness.Money);
                services.RemoveAll<IBudgetGuard>();
                services.AddSingleton<IBudgetGuard>(harness.Money);
                services.RemoveAll<IPriceCatalog>();
                services.AddSingleton<IPriceCatalog>(harness.Money);
                services.RemoveAll<IImageCostEstimator>();
                services.AddSingleton<IImageCostEstimator>(harness.Money);
            });

        await host.StartAsync(Ct);
        harness.Host = host;

        return harness;
    }

    /// <summary>Runs one poll of <paramref name="renderJobId"/> the way the poller does: in its own scope.</summary>
    private static async Task PollAsync(WorkerHost host, Guid renderJobId)
    {
        using var scope = host.CreateScope();

        await scope.ServiceProvider.GetRequiredService<PollRenderJob>().RunAsync(renderJobId, Ct);
    }

    private static RenderStatusV1 Status(World world, string state, string stage, int percent) =>
        new(
            RenderNodeSchemas.RenderStatus,
            world.TaskId,
            world.RenderJobId,
            state,
            stage,
            percent,
            false,
            Start,
            Start,
            state is RenderStates.Succeeded or RenderStates.Failed or RenderStates.Cancelled
                ? Start.AddMinutes(5)
                : null,
            null,
            null,
            null,
            null,
            null);

    /// <summary>One owner with an episode in <c>rendering</c>, its story version, a worker node and a row.</summary>
    private async Task<World> SeedJobAsync(
        string status,
        string stage,
        short percent,
        bool cancelRequested = false,
        int submittedMinutesAgo = 0,
        int startedMinutesAgo = 0)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, new FakeClock(Start))
            .CreateAsync(user.Id, series.Id, ct: Ct);

        var draft = Draft();
        var version = StoryVersion.Create(
            user.Id,
            episode.Id,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new System.Text.Json.Nodes.JsonArray()),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Start);

        await using (var seedWriter = Database.CreateAppDbContext(actor, new FakeClock(Start)))
        {
            seedWriter.StoryVersions.Add(version);
            await seedWriter.SaveChangesAsync(Ct);
        }

        var nodeId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var reservationId = Guid.CreateVersion7();
        var submittedAt = Start.AddMinutes(-submittedMinutesAgo);
        var startedAt = Start.AddMinutes(-startedMinutesAgo);
        var taskId = Guid.CreateVersion7();

        await Database.ExecuteAsync(
            $"UPDATE episodes SET status = 'rendering' WHERE id = '{episode.Id}'",
            Ct);

        await Database.ExecuteAsync(
            "INSERT INTO worker_nodes (id, name, base_url, capacity, state, last_heartbeat_at, gpu_model, "
            + "tls_cert_sha256, created_at, updated_at) "
            + $"VALUES ('{nodeId}', 'poll-node-{nodeId:N}'[1:20], 'https://127.0.0.1:8443', 2, 'active', now(), "
            + "'RTX 4090', '0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF', now(), now())",
            Ct);

        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, worker_node_id, adapter_task_id, submitted_at, started_at, next_poll_at, "
            + "cancel_requested_at) "
            + $"VALUES ('{jobId}', '{user.Id}', '{episode.Id}', '{version.Id}', '{reservationId}', '{status}', "
            + $"'{stage}', {percent}, '{ParamsJson(version.Id)}'::jsonb, 1, '{nodeId}', '{taskId}', "
            + $"'{submittedAt:O}', '{startedAt:O}', '{Start:O}', "
            + (cancelRequested ? $"'{Start:O}'" : "NULL")
            + ")",
            Ct);

        return new World(user.Id, episode.Id, version.Id, jobId, nodeId, reservationId, taskId);
    }

    /// <summary>Asserts the row's status, its stored stage, its percent and its code in one query.</summary>
    private void AssertRow(World world, string status, string? stage = null, int? percent = null, string? errorCode = null)
    {
        var row = SqlProbe.ScalarText(
            Database,
            $"SELECT status || '|' || stage || '|' || percent::text || '|' || coalesce(error_code, '-') "
            + $"FROM render_jobs WHERE id = '{world.RenderJobId}'");

        var expected = string.Join(
            '|',
            status,
            stage ?? "-",
            percent?.ToString(CultureInfo.InvariantCulture) ?? "-",
            errorCode ?? "-");

        row.ShouldBe(expected);
    }

    private string ScalarText(string select, World world) =>
        SqlProbe.ScalarText(Database, $"{select} FROM render_jobs WHERE id = '{world.RenderJobId}'");

    private bool ScalarBool(string select, World world) =>
        bool.Parse(ScalarText(select, world));

    private async Task<string> ScalarAsync(string sql) =>
        (await Database.QueryStringsAsync(sql, Ct)).Single();

    /// <summary>The episode's status: a terminal transition moves it out of <c>rendering</c>.</summary>
    private string EpisodeStatus(World world) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'");

    private long CountAudit(World world) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE entity_id = '{world.RenderJobId}'");

    private long CountOutbox(World world) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE user_id = '{world.UserId}'");

    /// <summary>
    /// A complete <c>render-params.v1</c> document (05 D11): every member is <c>[JsonRequired]</c>, so the row
    /// could not be loaded without one.
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

    /// <summary>The host and the recorders every test asserts on.</summary>
    private sealed class Harness(FakeRenderClient render, RecordingHooks hooks)
    {
        public WorkerHost Host { get; set; } = null!;

        public FakeRenderClient Render { get; } = render;

        public RecordingHooks Hooks { get; } = hooks;

        public RecordingNotifier Notifier { get; } = new();

        public RecordingIngestEnqueuer Ingest { get; } = new();

        public RecordingMoney Money { get; } = new();

        public async ValueTask DisposeAsync() => await Host.DisposeAsync();
    }

    /// <summary>The realtime pushes of one poll, in order.</summary>
    private sealed class RecordingNotifier : IRealtimeNotifier
    {
        public List<(Guid UserId, RealtimeEvent Event)> Events { get; } = [];

        public Task NotifyAsync(Guid userId, RealtimeEvent realtimeEvent, CancellationToken cancellationToken)
        {
            Events.Add((userId, realtimeEvent));

            return Task.CompletedTask;
        }
    }

    /// <summary>The ids the ingest was handed; 09.6.3-T2 owns what happens next.</summary>
    private sealed class RecordingIngestEnqueuer : IRenderIngestEnqueuer
    {
        public List<Guid> Enqueued { get; } = [];

        public void Enqueue(Guid renderJobId) => Enqueued.Add(renderJobId);
    }

    /// <summary>The 03.4.2-T1 extension point: the order it is called in and the verdict it returns.</summary>
    private sealed class RecordingHooks : IRenderTerminalHooks
    {
        public List<string> Calls { get; } = [];

        public RenderFailureClassification Classification { get; init; } = new("render_node_error", null);

        public ValueTask RecordImageUsageAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct)
        {
            Calls.Add("usage");

            return ValueTask.CompletedTask;
        }

        public ValueTask<RenderFailureClassification> ClassifyAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct)
        {
            Calls.Add("classify");

            return ValueTask.FromResult(Classification);
        }
    }

    /// <summary>
    /// The four money ports of one attempt in one recorder: the refund by reservation id is what the failure and
    /// cancellation paths assert on, so an unrefunded path is an empty list.
    /// </summary>
    private sealed class RecordingMoney :
        IPriceCatalog,
        IImageCostEstimator,
        IBudgetGuard,
        ICreditLedger
    {
        public List<(Guid ReservationId, string Reason)> Refunds { get; } = [];

        public ErrorOr<Success> Budget { get; init; } = Result.Success;

        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromResult<ErrorOr<PriceVersion>>(
                PriceVersion.Create(1, Start.AddDays(-1), "{}", "{}", "hash", Start.AddDays(-1)));

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => throw new NotSupportedException();

        public Task<CostBound> WorstCaseAsync(Aspect aspect, int maxTerms, int priceVersion, CancellationToken ct) =>
            Task.FromResult(new CostBound(1.23m));

        public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null) =>
            ValueTask.FromResult(Budget);

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(Guid.CreateVersion7()));

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            Refunds.Add((reservationId.Value, reason));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
