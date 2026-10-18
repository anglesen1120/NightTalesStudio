using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.Application.Features.Stories;
using NightTales.Application.Rendering;
using NightTales.Application.Telemetry;
using NightTales.Domain.Rendering;
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
using NightTales.TestKit.Storage;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The cancel leg of <see cref="PollRenderJob"/> (09.6.4-T1, BD8, WT50/WT51, 09.6.3-T1 step 6c) against a real
/// database through the Worker host: the adapter's terminal <c>cancelled</c> is the one instant a cancelled
/// render's reservation is refunded — by <c>reservation_id</c>, exactly once, with no blob, no <c>assets</c> row
/// and no outbox event — while a cancel-requested row the adapter reports <c>succeeded</c> is ingested and one it
/// reports <c>failed</c> stays final with no retry row.
/// </summary>
/// <remarks>
/// <para>
/// The job is called directly with the row id, exactly as <c>RenderPollingService</c> calls it, so nothing here
/// depends on the poller's claim. The adapter answers come from the shared <see cref="FakeRenderClient"/>, the
/// money ports, the realtime push, the ingest enqueue, the 03.4.2-T1 hooks and the blob store are hand-written
/// recorders, so every assertion is about a value this test wrote or the job wrote.
/// </para>
/// <para>
/// The terminal <c>cancelled</c> refund, the audit row and the duration observation are the same effects the
/// 09.6.3-T1 poll tests assert; this class adds what only a cancel-requested row shows: the mutual exclusion of
/// the refund and the ingest, the <c>succeeded</c>-wins precedence (WT51), and the final failure of a row whose
/// owner already asked to cancel. The settle of the <c>succeeded</c> attempt belongs to 09.6.3-T2's ingest job,
/// which runs behind its own harness (<c>IngestRenderOutputJobTests</c>): this class stops at the enqueue, which
/// is where the poll's contract ends.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CancelRenderPollTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PollRenderJob_AdapterCancelled_RefundsReservationOnce_AtTerminal()
    {
        var world = await SeedJobAsync("running", "voice", percent: 30, cancelRequested: true, startedMinutesAgo: 30);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);
        using var durations = ObserveDurations(harness.Host.Services.GetRequiredService<NightTalesMetrics>());

        // A row whose owner asked for a cancel but whose adapter task is still running keeps running: the request
        // itself never refunds, writes the row or stores anything (WT51).
        render.EnqueueStatus(Status(world, RenderStates.Running, "voice", 30));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "running", stage: "voice", percent: 30);
        harness.Money.Refunds.ShouldBeEmpty("a cancel request never refunds by itself");
        durations.Observed.ShouldBeEmpty("a poll that moved nothing observes no duration");

        // The adapter's terminal cancelled is the one instant the refund happens.
        render.EnqueueStatus(Status(world, RenderStates.Cancelled, "voice", 30));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "cancelled", stage: "voice", percent: 30);
        EpisodeStatus(world).ShouldBe("story_approved", "the episode comes back to its creator (BD8)");
        harness.Money.Refunds.ShouldBe(
            [(world.ReservationId, "render_cancelled")],
            "the terminal cancelled refunds its own reservation, once, by reservation id (WT50)");

        // Nothing of a cancelled task is downloaded or stored (05 D4), and `render_cancelled` is a state, never an
        // event: no asset row, no blob, no ingest and no outbox row.
        SqlProbe.Count(Database, $"SELECT count(*) FROM assets WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0L, "the outputs of a cancelled task are never served, so no asset row is created");
        harness.Blobs.Calls.ToArray()
            .Where(call => call.Operation == BlobOperation.Put && call.Key.Contains(world.RenderJobId.ToString("D"), StringComparison.Ordinal))
            .ShouldBeEmpty("and nothing of this job is stored (a store-wide put count would also see the host's own probes)");
        harness.Ingest.Enqueued.ShouldBeEmpty("a cancelled attempt is never ingested");
        CountOutbox(world).ShouldBe(0, "a cancelled render writes no outbox event (05 D5)");

        SqlProbe.QueryStrings(
                Database,
                "SELECT action || '|' || actor_type || '|' || (data->>'code') FROM audit_logs "
                + $"WHERE entity_id = '{world.RenderJobId}'")
            .ShouldBe(["render.cancelled|system|render_cancelled"], "the platform ends it, on the owner's behalf");

        var duration = durations.Observed.ShouldHaveSingleItem();
        duration.Name.ShouldBe(NightTalesMetrics.RenderDurationName);
        duration.Stage.ShouldBe("voice", "the bounded stage label, never the adapter's own text");
        duration.Outcome.ShouldBe(RenderMetrics.OutcomeCancelled);
        duration.Value.ShouldBe(1800d, "the attempt ran from its started_at to the terminal instant");
        duration.Meter.ShouldBe(NightTalesMetrics.MeterName, "the instrument lives on the host's single meter");

        // A second poll of the now-terminal row is skipped: nothing is written and nothing is refunded again.
        await PollAsync(harness.Host, world.RenderJobId);

        render.StatusCalls.ShouldBe(2, "a terminal row is not polled again (WT45)");
        harness.Money.Refunds.Count.ShouldBe(1, "the refund is applied once, at the terminal instant");
        CountAudit(world).ShouldBe(1);
        durations.Observed.Count.ShouldBe(1);
        AssertRow(world, status: "cancelled", stage: "voice", percent: 30);
    }

    [Fact]
    public async Task PollRenderJob_CancelRequestedButAdapterSucceeded_IngestsInsteadOfCancelling()
    {
        var world = await SeedJobAsync("running", "voice", percent: 30, cancelRequested: true, startedMinutesAgo: 30);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);
        using var durations = ObserveDurations(harness.Host.Services.GetRequiredService<NightTalesMetrics>());

        // The terminal state the adapter reports wins (WT51): the attempt really rendered, so it is ingested and
        // settled instead of being thrown away by the cancel request.
        render.EnqueueStatus(Status(world, RenderStates.Succeeded, "done", 100));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "ingesting", stage: "done", percent: 100);
        harness.Ingest.Enqueued.ShouldBe([world.RenderJobId], "the success is handed to the ingest");
        harness.Money.Refunds.ShouldBeEmpty("the ingest settles the reservation; a cancelled render is not refunded here");
        harness.Money.Settled.ShouldBeEmpty("and the poll itself only enqueues the ingest — 09.6.3-T2 settles it");

        // The same meter that reported no cancelled outcome for this poll reports the succeeded one: the listener is
        // live, so an absent cancellation observation would be a defect rather than an unobserved instrument.
        durations.Observed.ShouldHaveSingleItem().Outcome.ShouldBe(RenderMetrics.OutcomeSucceeded);

        CountAudit(world).ShouldBe(0, "a render that succeeded writes no render.cancelled audit row");
        EpisodeStatus(world).ShouldBe("rendering", "the episode stays held until the ingest ends it");
        SqlProbe.Count(Database, $"SELECT count(*) FROM assets WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0L, "the poll downloads nothing; the ingest stores the output");
        harness.Blobs.Calls.ToArray()
            .Where(call => call.Operation == BlobOperation.Put && call.Key.Contains(world.RenderJobId.ToString("D"), StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task PollRenderJob_CancelRequestedThenAdapterFailed_IsFinal_NoRetryRow_OneRefund()
    {
        var world = await SeedJobAsync("running", "voice", percent: 30, cancelRequested: true, startedMinutesAgo: 30);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        // A row its owner already asked to cancel is never retried, whatever the adapter answers afterwards: a retry
        // would re-render and re-charge an episode its creator cancelled (WT51, SEC-4).
        render.EnqueueStatus(Status(world, RenderStates.Failed, "voice", 30));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "failed", stage: "voice", percent: 30, errorCode: "render_node_error");
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1L, "no retry row is created for a cancel-requested attempt");
        harness.Money.Refunds.ShouldBe(
            [(world.ReservationId, "render_failed")],
            "the failed attempt is still refunded once, by reservation id");
        SqlProbe.QueryStrings(
                Database,
                "SELECT action || '|' || actor_type || '|' || (data->>'final')::text FROM audit_logs "
                + $"WHERE entity_id = '{world.RenderJobId}'")
            .ShouldBe(["render.failed|system|true"], "the cancellation makes the verdict final (WT50)");
        CountOutbox(world).ShouldBe(1, "exactly one render_failed.v1 carries the final verdict");
        EpisodeStatus(world).ShouldBe("story_approved", "the episode comes back to its creator");

        // The adapter itself stopped the task, so the poll asks its node for nothing else: cancelling and deleting
        // a task is the timeout path's cleanup (SEC-8), never a terminal answer the adapter already gave.
        render.CancelCalls.ShouldBe(0);
        render.DeleteCalls.ShouldBe(0);
    }

    /// <summary>Reads one string tag off a measurement, or the empty string when the tag is absent.</summary>
    private static string TagOf(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
            {
                return tag.Value as string ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// A live listener over the host's own meter and the durations it observed, in order. The instrument is enabled
    /// only for <see cref="NightTalesMetrics.Meter"/>, so a second meter of the same name would export nothing here
    /// (AC8); the probe owns the listener, because a collected one silently stops receiving measurements.
    /// </summary>
    /// <param name="metrics">The host's render instruments.</param>
    /// <returns>The probe; dispose it at the end of the test.</returns>
    private static DurationProbe ObserveDurations(NightTalesMetrics metrics)
    {
        var observed = new List<DurationObservation>();

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, self) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter))
                {
                    self.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => observed.Add(
            new DurationObservation(
                instrument.Name,
                TagOf(tags, NightTalesMetrics.StageTag),
                TagOf(tags, NightTalesMetrics.OutcomeTag),
                value,
                instrument.Meter.Name)));
        listener.Start();

        return new DurationProbe(listener, observed);
    }

    /// <summary>One <c>render_duration_seconds</c> observation as the listener saw it.</summary>
    private sealed record DurationObservation(string Name, string Stage, string Outcome, double Value, string Meter);

    /// <summary>The listener and the observations it collected.</summary>
    private sealed class DurationProbe(MeterListener listener, List<DurationObservation> observed) : IDisposable
    {
        public List<DurationObservation> Observed { get; } = observed;

        public void Dispose() => listener.Dispose();
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
    /// <see cref="FakeRenderClient"/>, the realtime push, the ingest enqueue, the 03.4.2-T1 hooks, the four money
    /// ports and the blob store are recorders. Nothing else about the composition root is changed.
    /// </summary>
    /// <param name="render">The scripted node client.</param>
    /// <returns>The host and the recorders every test asserts on.</returns>
    private async Task<Harness> StartHostAsync(FakeRenderClient render)
    {
        var harness = new Harness(render, new RecordingHooks(), new RecordingMoney(), new InMemoryBlobStore());

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
                services.RemoveAll<IBlobStore>();
                services.AddSingleton<IBlobStore>(harness.Blobs);
            });

        await host.StartAsync(Ct);
        harness.Host = host;

        return harness;
    }

    /// <summary>Runs one poll of <paramref name="renderJobId"/> the way the poller does: in its own scope.</summary>
    /// <param name="host">The started Worker host.</param>
    /// <param name="renderJobId">The row to poll.</param>
    private static async Task PollAsync(WorkerHost host, Guid renderJobId)
    {
        using var scope = host.CreateScope();

        await scope.ServiceProvider.GetRequiredService<PollRenderJob>().RunAsync(renderJobId, Ct);
    }

    /// <summary>The adapter's answer for one task, as the client reports it.</summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="state">The 05.4.1-T2 state.</param>
    /// <param name="stage">The adapter's wire stage.</param>
    /// <param name="percent">The adapter's progress.</param>
    /// <returns>The status body.</returns>
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
    /// <param name="status">The row's status.</param>
    /// <param name="stage">The row's stored stage.</param>
    /// <param name="percent">The row's stored progress.</param>
    /// <param name="cancelRequested">Whether the owner already asked to cancel it.</param>
    /// <param name="startedMinutesAgo">How long before the poll instant the attempt started.</param>
    /// <returns>The ids the assertions read.</returns>
    private async Task<World> SeedJobAsync(
        string status,
        string stage,
        short percent,
        bool cancelRequested = false,
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
            StoryContentJson.Write(draft, new JsonArray()),
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
        var submittedAt = Start.AddMinutes(-5);
        var startedAt = Start.AddMinutes(-startedMinutesAgo);
        var taskId = Guid.CreateVersion7();

        await Database.ExecuteAsync(
            $"UPDATE episodes SET status = 'rendering' WHERE id = '{episode.Id}'",
            Ct);

        await Database.ExecuteAsync(
            "INSERT INTO worker_nodes (id, name, base_url, capacity, state, last_heartbeat_at, gpu_model, "
            + "tls_cert_sha256, created_at, updated_at) "
            + $"VALUES ('{nodeId}', 'poll-node-{nodeId:N}', 'https://127.0.0.1:8443', 2, 'active', now(), "
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
    /// <param name="world">The seeded world.</param>
    /// <param name="status">The expected status.</param>
    /// <param name="stage">The expected stage; <c>null</c> means "any".</param>
    /// <param name="percent">The expected percent; <c>null</c> means "any".</param>
    /// <param name="errorCode">The expected code; <c>null</c> means "none" (the cancel paths never set one).</param>
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

    /// <summary>The episode's status: a terminal transition moves it out of <c>rendering</c>.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The episode's status.</returns>
    private string EpisodeStatus(World world) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'");

    /// <summary>The audit rows of this render job.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The row count.</returns>
    private long CountAudit(World world) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE entity_id = '{world.RenderJobId}'");

    /// <summary>The outbox rows of this world's owner.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The row count.</returns>
    private long CountOutbox(World world) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE user_id = '{world.UserId}'");

    /// <summary>
    /// A complete <c>render-params.v1</c> document (05 D11): every member is <c>[JsonRequired]</c>, so the row
    /// could not be loaded without one.
    /// </summary>
    /// <param name="storyVersionId">The story version the params point at.</param>
    /// <returns>The JSON text the <c>params</c> column stores.</returns>
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
    /// <returns>The content document the seeded version stores.</returns>
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
    private sealed class Harness(
        FakeRenderClient render,
        RecordingHooks hooks,
        RecordingMoney money,
        InMemoryBlobStore blobs)
    {
        public WorkerHost Host { get; set; } = null!;

        public FakeRenderClient Render { get; } = render;

        public RecordingHooks Hooks { get; } = hooks;

        public RecordingNotifier Notifier { get; } = new();

        public RecordingIngestEnqueuer Ingest { get; } = new();

        public RecordingMoney Money { get; } = money;

        public InMemoryBlobStore Blobs { get; } = blobs;

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

        public ValueTask<JsonNode?> MergeImagesAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct)
        {
            Calls.Add("images");

            return ValueTask.FromResult<JsonNode?>(null);
        }

        public ValueTask RecordKeptVideoAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct)
        {
            Calls.Add("kept");

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The four money ports of one attempt in one recorder: the refund by reservation id is what the failure and
    /// cancellation paths assert on, so an unrefunded path is an empty list — and the settlement of a succeeded
    /// attempt belongs to the ingest, which is why this recorder counts it instead of performing it.
    /// </summary>
    private sealed class RecordingMoney :
        IPriceCatalog,
        IImageCostEstimator,
        IBudgetGuard,
        ICreditLedger
    {
        public List<(Guid ReservationId, string Reason)> Refunds { get; } = [];

        public List<Guid> Settled { get; } = [];

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

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct)
        {
            Settled.Add(reservationId.Value);

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            Refunds.Add((reservationId.Value, reason));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
