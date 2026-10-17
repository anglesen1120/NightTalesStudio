using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.Application.Rendering;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Pricing;
using NightTales.TestKit;
using NightTales.TestKit.Api;
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
/// 09.6.3-T1, submit side: <c>render_requested.v1</c> becomes exactly one <c>SubmitRenderJob</c> and exactly one
/// adapter task, the row adopts the task (09 README BD7), a second delivery is a no-op, and the three ruled
/// non-failure exits hold (no node, adapter capacity, unhealthy node). The real Api and Worker graphs and
/// PostgreSQL run; only the node client and the node selector are scripted.
/// </summary>
/// <remarks>
/// <para>
/// The job row is created the production way — <c>POST /api/v1/episodes/{id}/render</c> against the real host —
/// so the params, the story version, the style template and the outbox row are the ones 09.6.1-T1 writes. The
/// Worker host then delivers the outbox row and runs the job through <see cref="InlineJobScheduler"/>, which is
/// what Hangfire would do (nothing sleeps, D13).
/// </para>
/// <para>
/// The Worker composition must already call <c>AddRenderJobs</c> (09.6.3-T1 contract line 35): this test does not
/// register the job a second time, because <c>AddOutboxHandler</c> adds (never <c>TryAdd</c>) and a second call
/// would deliver every render request twice.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SubmitRenderJobTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The word only the seeded story carries, so a content-free row is proven free of it.</summary>
    private const string Canary = "spindlewick";

    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Submits_OneAdapterTask_AndMarksSubmitted()
    {
        var world = await SeedAsync();
        var jobId = await RequestRenderAsync(world);
        var node = await SeedNodeAsync(world.UserId);
        var client = new FakeRenderClient();
        var taskId = Guid.CreateVersion7();
        client.EnqueueSubmit(FakeRenderClient.Accepted(taskId));
        var selector = new FakeNodeSelector().Enqueue(node);

        await using var worker = await StartWorkerAsync(client, selector);

        (await worker.RunOutboxOnceAsync(Ct)).ShouldBeGreaterThan(0);
        worker.Jobs.Enqueued.Count(job => job.JobType == nameof(SubmitRenderJob)).ShouldBe(1);
        await worker.Jobs.RunPendingAsync(Ct);
        worker.Jobs.Failures.ShouldBeEmpty();

        // One adapter call, with the body of this job (05 D3/D4).
        client.SubmitCalls.ShouldBe(1);
        var call = client.Calls.ShouldHaveSingleItem();
        call.Method.ShouldBe(RenderClientMethod.Submit);
        call.Node.Id.ShouldBe(node.Id);
        call.Body!.RenderJobId.ShouldBe(jobId);
        call.Body.Schema.ShouldNotBeNullOrWhiteSpace();
        call.Body.ImagePlan.MaxImages.ShouldBeGreaterThan(0);
        JsonSerializer.Serialize(call.Body).ShouldContain(Canary, Case.Sensitive, "the narration goes to the node");

        // The row adopts the task, the node and the first poll instant; nothing else changed.
        SqlProbe.ScalarText(Database,
                "SELECT status || '|' || adapter_task_id || '|' || (worker_node_id = "
                + $"'{Id(node.Id)}')::text || '|' || attempt || '|' || "
                + "(next_poll_at = submitted_at + interval '10 seconds')::text "
                + $"FROM render_jobs WHERE id = '{Id(jobId)}'")
            .ShouldBe($"submitted|{Id(taskId)}|true|1|true");

        // The row carries no story text (P6/P8), and only the one outbox row of the command exists.
        SqlProbe.ScalarText(Database, $"SELECT params::text FROM render_jobs WHERE id = '{Id(jobId)}'")
            .ShouldNotContain(Canary);
    }

    [Fact]
    public async Task Submit_Skips_WhenAdapterTaskIdPresent()
    {
        var world = await SeedAsync();
        var jobId = await RequestRenderAsync(world);
        var node = await SeedNodeAsync(world.UserId);
        var client = new FakeRenderClient();
        var taskId = Guid.CreateVersion7();
        client.EnqueueSubmit(FakeRenderClient.Accepted(taskId));
        var selector = new FakeNodeSelector().Enqueue(node);

        await using var worker = await StartWorkerAsync(client, selector);
        await worker.RunOutboxOnceAsync(Ct);
        await worker.Jobs.RunPendingAsync(Ct);
        client.SubmitCalls.ShouldBe(1);

        // At-least-once: the same row delivered again exits at the adapter_task_id guard.
        await using (var scope = worker.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SubmitRenderJob>().RunAsync(jobId, Ct);
        }

        client.SubmitCalls.ShouldBe(1, "a second delivery must not create a second adapter task");
        SqlProbe.ScalarText(Database, $"SELECT status || '|' || adapter_task_id FROM render_jobs WHERE id = '{Id(jobId)}'")
            .ShouldBe($"submitted|{Id(taskId)}");
    }

    [Fact]
    public async Task Submit_NoNode_Reschedules_WithoutRetryBudget()
    {
        var world = await SeedAsync();
        var client = new FakeRenderClient();

        // A row queued one minute ago: young enough that the 360-minute alarm must stay silent.
        var jobId = SeedQueuedJob(world, Guid.CreateVersion7(Start.AddMinutes(-1)));
        var selector = new FakeNodeSelector().EnqueueUnavailable();

        await using var worker = await StartWorkerAsync(client, selector);
        var healthPoll = worker.Services.GetRequiredService<RenderNodesOptions>().HealthPollSeconds;
        await RunSubmitAsync(worker, jobId);

        worker.Jobs.Failures.ShouldBeEmpty("a missing node is a reschedule, never a spent attempt");
        client.SubmitCalls.ShouldBe(0);
        var reschedule = worker.Jobs.Enqueued.ShouldHaveSingleItem();
        reschedule.JobType.ShouldBe(nameof(SubmitRenderJob));
        reschedule.Delay.ShouldBe(TimeSpan.FromSeconds(healthPoll));

        SqlProbe.ScalarText(Database, $"SELECT status || '|' || attempt FROM render_jobs WHERE id = '{Id(jobId)}'")
            .ShouldBe("queued|1");
    }

    [Fact]
    public async Task Submit_QueueAlertFiresAt360Minutes()
    {
        var world = await SeedAsync();
        var client = new FakeRenderClient();

        // Queued 361 minutes ago (the id's own timestamp is the row's only creation stamp, WT49).
        var jobId = SeedQueuedJob(world, Guid.CreateVersion7(Start.AddMinutes(-361)));
        var selector = new FakeNodeSelector().EnqueueUnavailable();
        var sink = new CapturingLoggerProvider();

        await using var worker = await StartWorkerAsync(client, selector, sink);
        await RunSubmitAsync(worker, jobId);

        worker.Jobs.Failures.ShouldBeEmpty();
        client.SubmitCalls.ShouldBe(0);
        var alert = sink.Entries.Where(entry => entry.EventId == SubmitRenderJob.QueueAlertEventId).ShouldHaveSingleItem();
        alert.Level.ShouldBe(LogLevel.Error);
        alert.Message.ShouldContain("render_queue_alert", Case.Sensitive);
        alert.Message.ShouldContain(Id(jobId), Case.Sensitive);
    }

    [Fact]
    public async Task Submit_CapacityFull_DefersWithRetryAfter()
    {
        var world = await SeedAsync();
        var jobId = await RequestRenderAsync(world);
        var node = await SeedNodeAsync(world.UserId);
        var client = new FakeRenderClient();
        var retryAfter = TimeSpan.FromSeconds(45);
        client.EnqueueFailure(
            RenderClientMethod.Submit,
            new RenderClientFailure(
                RenderClientFailureKind.Deferred,
                "adapter_capacity_full",
                "scripted",
                Alert: false,
                retryAfter));
        var selector = new FakeNodeSelector().Enqueue(node);

        await using var worker = await StartWorkerAsync(client, selector);
        await worker.RunOutboxOnceAsync(Ct);
        await worker.Jobs.RunPendingAsync(Ct);

        worker.Jobs.Failures.ShouldBeEmpty("a deferred answer is never a failure");
        client.SubmitCalls.ShouldBe(1);
        var reschedule = worker.Jobs.Enqueued.Last();
        reschedule.JobType.ShouldBe(nameof(SubmitRenderJob));
        reschedule.Delay.ShouldBe(retryAfter, "the adapter's own wait wins over the health cadence");

        SqlProbe.ScalarText(Database,
                $"SELECT status || '|' || coalesce(adapter_task_id, '-') || '|' || attempt FROM render_jobs WHERE id = '{Id(jobId)}'")
            .ShouldBe("queued|-|1");
    }

    [Fact]
    public async Task Submit_NodeUnhealthy_PicksAgainLater()
    {
        var world = await SeedAsync();
        var jobId = await RequestRenderAsync(world);
        var node = await SeedNodeAsync(world.UserId);
        var client = new FakeRenderClient();
        var taskId = Guid.CreateVersion7();
        client.EnqueueFailure(
            RenderClientMethod.Submit,
            new RenderClientFailure(
                RenderClientFailureKind.Deferred,
                "adapter_node_unhealthy",
                "scripted",
                Alert: false,
                RetryAfter: null));
        client.EnqueueSubmit(FakeRenderClient.Accepted(taskId));
        var selector = new FakeNodeSelector().Enqueue(node).Enqueue(node);

        await using var worker = await StartWorkerAsync(client, selector);
        var healthPoll = worker.Services.GetRequiredService<RenderNodesOptions>().HealthPollSeconds;
        await worker.RunOutboxOnceAsync(Ct);
        await worker.Jobs.RunPendingAsync(Ct);

        worker.Jobs.Failures.ShouldBeEmpty();
        client.SubmitCalls.ShouldBe(1);
        worker.Jobs.Enqueued.Last().Delay.ShouldBe(TimeSpan.FromSeconds(healthPoll));
        SqlProbe.ScalarText(Database, $"SELECT status FROM render_jobs WHERE id = '{Id(jobId)}'").ShouldBe("queued");

        // The scheduled delivery runs on the health cadence; this time the node accepts.
        await worker.Jobs.RunPendingAsync(Ct);

        client.SubmitCalls.ShouldBe(2);
        SqlProbe.ScalarText(Database, $"SELECT status || '|' || adapter_task_id FROM render_jobs WHERE id = '{Id(jobId)}'")
            .ShouldBe($"submitted|{Id(taskId)}");
    }

    [Fact]
    public async Task Submit_RechecksRowAfterAdapterCall_CancelsAndDeletesOnRace()
    {
        var world = await SeedAsync();
        var jobId = await RequestRenderAsync(world);
        var node = await SeedNodeAsync(world.UserId);
        var client = new FakeRenderClient();
        var taskId = Guid.CreateVersion7();
        client.EnqueueSubmit(FakeRenderClient.Accepted(taskId));
        var selector = new FakeNodeSelector().Enqueue(node);

        // A cancel requested before the job ran: the row is still queued, so the job submits, and the re-check
        // after the adapter call (WT55) is what must refuse to adopt the task.
        SqlProbe.Execute(Database, $"UPDATE render_jobs SET cancel_requested_at = '{Start:O}'::timestamptz WHERE id = '{Id(jobId)}'");

        await using var worker = await StartWorkerAsync(client, selector);
        await worker.RunOutboxOnceAsync(Ct);
        await worker.Jobs.RunPendingAsync(Ct);

        worker.Jobs.Failures.ShouldBeEmpty();
        client.SubmitCalls.ShouldBe(1);
        client.CancelCalls.ShouldBe(1);
        client.DeleteCalls.ShouldBe(1);
        client.Calls.Where(call => call.Method is RenderClientMethod.Cancel or RenderClientMethod.Delete)
            .ShouldAllBe(call => call.TaskId == Id(taskId));

        // No MarkSubmitted: the row keeps its state and never points at the task that was deleted.
        SqlProbe.ScalarText(Database,
                $"SELECT status || '|' || coalesce(adapter_task_id, '-') || '|' || coalesce(submitted_at::text, '-') FROM render_jobs WHERE id = '{Id(jobId)}'")
            .ShouldBe("queued|-|-");
    }

    /// <summary>Seeds an owner with a series, an approved episode and the price version every paid step needs.</summary>
    /// <returns>The seeded ids.</returns>
    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(
            user.Id,
            series.Id,
            lengthTier: LengthTier.Short,
            ct: Ct);
        await SeedPriceVersionAsync(user.Id);

        var draft = Draft();
        var version = StoryVersion.Create(user.Id, episode.Id, 1, StoryVersionSource.Generated, draft,
            StoryContentJson.Write(draft, new JsonArray()), "model-x", "rel=1", null, null, null, null, null, Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }, new FakeClock(Start)))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database,
            $"UPDATE episodes SET status='story_approved', approved_story_version_id='{Id(version.Id)}', approved_at='{Start:O}' WHERE id='{Id(episode.Id)}'");

        return new World(user.Id, series.Id, episode.Id, version.Id);
    }

    /// <summary>The story the render reads: one keyword per segment.</summary>
    /// <returns>The content document the approved version stores.</returns>
    private static StoryContentV1 Draft() => new(
        "The pier at dawn",
        new StorySegmentV1($"The {Canary} mist drifted over the riverside.", ["quiet riverside morning"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", ["empty wooden pier at dusk"], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    private async Task SeedPriceVersionAsync(Guid userId)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));
        if (db.PriceVersions.Any(version => version.Version == 1))
        {
            return;
        }

        var rates = new PriceVersionRates(
            new Dictionary<string, PriceRates> { ["gpt-6-luna"] = new(0.10m, 0.01m, 0.125m, 0.50m) },
            new Dictionary<string, ImagePriceRate>(),
            null,
            TextCostCalculator.DefaultSurchargeThresholdTokens).ToJson();
        db.PriceVersions.Add(PriceVersion.Create(
            1,
            Start.AddDays(-1),
            rates,
            PriceVersionJson.WriteLimits(PriceLimits.D4Defaults),
            "submit-job-price-version",
            Start.AddDays(-1)));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Inserts a real <c>worker_nodes</c> row, so the submitted row's foreign key is satisfiable.</summary>
    /// <param name="userId">The owner the seeding context runs as (the table is global).</param>
    /// <returns>The node the selector hands the job.</returns>
    private async Task<WorkerNode> SeedNodeAsync(Guid userId)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));
        var node = WorkerNode.Create(
            $"submit-job-{Guid.NewGuid():N}",
            new Uri("http://render-node.example/"),
            capacity: 2,
            tlsCertSha256: null,
            gpuModel: null).Value;
        db.WorkerNodes.Add(node);
        await db.SaveChangesAsync(Ct);

        return node;
    }

    /// <summary>
    /// Inserts a <c>queued</c> row with raw SQL under an id of the caller's choosing: the queue alarm reads the
    /// row's age from its UUIDv7 id, so a test that scripts the alert must choose the id itself (the command
    /// generates it from the system clock).
    /// </summary>
    /// <param name="world">The approved world the row belongs to.</param>
    /// <param name="jobId">The version 7 id that carries the queued instant.</param>
    /// <returns>The row's id.</returns>
    private Guid SeedQueuedJob(World world, Guid jobId)
    {
        SqlProbe.Execute(Database,
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, percent, params, attempt) "
            + $"VALUES ('{Id(jobId)}', '{Id(world.UserId)}', '{Id(world.EpisodeId)}', '{Id(world.VersionId)}', "
            + $"'{Id(Guid.CreateVersion7())}', 'queued', '{RenderJob.QueuedStage}', 0, '{{\"schema\":\"render-params.v1\",\"music\":null}}'::jsonb, 1)");

        return jobId;
    }

    /// <summary>Creates the job row the production way: <c>POST /api/v1/episodes/{id}/render</c>.</summary>
    /// <param name="world">The approved episode to render.</param>
    /// <returns>The queued row's id.</returns>
    private async Task<Guid> RequestRenderAsync(World world)
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start))
            .WithServices(services =>
            {
                services.RemoveAll<ICreditLedger>();
                services.AddSingleton<ICreditLedger>(new RecordingLedger());
            });
        using var client = factory.ClientFor(world.UserId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{Id(world.EpisodeId)}/render");
        request.Headers.TryAddWithoutValidation("If-Match", Etag(world.EpisodeId));
        request.Headers.TryAddWithoutValidation("Idempotency-Key", $"submit-job-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return document.RootElement.GetProperty("renderJobId").GetGuid();
    }

    /// <summary>The episode's current strong ETag, read from <c>xmin</c> like the client's own copy.</summary>
    /// <param name="episodeId">The episode to read.</param>
    /// <returns>The quoted tag.</returns>
    private string Etag(Guid episodeId) =>
        ETags.Episode(uint.Parse(
            SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM episodes WHERE id='{Id(episodeId)}'"),
            CultureInfo.InvariantCulture));

    private async Task<WorkerHost> StartWorkerAsync(
        FakeRenderClient client,
        FakeNodeSelector selector,
        CapturingLoggerProvider? sink = null)
    {
        var host = new WorkerHost(Database, new FakeClock(Start))
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRenderClient>();
                services.AddSingleton<IRenderClient>(client);
                services.RemoveAll<INodeSelector>();
                services.AddSingleton<INodeSelector>(selector);

                // Fallback for the value injection of the two option types the job resolves directly; a Worker
                // binding of the same type (S4) wins, because TryAdd keeps the first registration.
                services.TryAddSingleton(new RenderOptions());
                services.TryAddSingleton(new RenderNodesOptions());

                if (sink is not null)
                {
                    services.AddLogging(logging => logging.AddProvider(sink));
                }
            });

        await host.StartAsync(Ct);

        return host;
    }

    /// <summary>Runs <c>SubmitRenderJob</c> once for <paramref name="jobId"/>, the way the dispatcher would.</summary>
    /// <param name="worker">The started Worker host.</param>
    /// <param name="jobId">The row to submit.</param>
    private static async Task RunSubmitAsync(WorkerHost worker, Guid jobId)
    {
        await using var scope = worker.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SubmitRenderJob>().RunAsync(jobId, Ct);
    }

    private static string Id(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId, Guid VersionId);

    /// <summary>One captured log line: the event id, the level and the rendered message.</summary>
    /// <param name="EventId">The id the <c>LoggerMessage</c> declared.</param>
    /// <param name="Level">The level the line was written at.</param>
    /// <param name="Message">The rendered message.</param>
    private sealed record LogEntry(int EventId, LogLevel Level, string Message);

    /// <summary>The hand-written log sink (no mocking library, D13).</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];
        private readonly Lock _gate = new();

        internal IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Capture(LogEntry entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                owner.Capture(new LogEntry(eventId.Id, logLevel, formatter(state, exception)));
            }
        }
    }

    /// <summary>
    /// The credit ledger the render command runs against: one known reservation id, no ledger service.
    /// </summary>
    private sealed class RecordingLedger : ICreditLedger
    {
        private readonly Guid _reservation = Guid.CreateVersion7();

        /// <inheritdoc />
        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            return ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(_reservation));
        }

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }
}
