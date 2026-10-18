using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Rendering.Outbox;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Rendering;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Infrastructure.Persistence.Outbox;
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
/// <see cref="RenderCancelRequestedHandler"/> (09.6.4-T1, BD8, D11/D12, WT51): the <c>render_cancel_requested.v1</c>
/// delivery asks the node that holds the attempt to cancel its adapter task — once, with the row's own node and
/// task id, from the Worker only — writes nothing, refunds nothing and never fails the render job, and its
/// answer is mapped: a no-op success is a delivered cancel, a Deferred/transient answer is thrown so the outbox
/// retries, and a terminal answer is logged and swallowed.
/// </summary>
/// <remarks>
/// <para>
/// The delivery runs the way the Worker's dispatcher runs it: <c>WorkerHost</c> composes the production graph,
/// <see cref="WorkerHost.RunOutboxOnceAsync"/> claims and delivers the seeded <c>outbox_messages</c> row in its
/// own pinned scope, and the node client is the scripted <see cref="FakeRenderClient"/>. The message row is
/// written by SQL, like <c>OutboxDispatcherTests</c> does, so a test owns the type, the payload and the owner —
/// and, for the skip cases, an id no row answers to.
/// </para>
/// <para>
/// The Worker composition already calls <c>AddRenderJobs</c>, which registers this handler
/// (<c>RenderJobsServiceCollectionExtensions</c>): a second registration would deliver every cancel twice, so
/// this class composes the host without repeating it.
/// </para>
/// <para>
/// Every zero-adapter-call assertion is paired with the delivery's own committed outcome
/// (<c>processed_at</c>), so "no call" can never pass because nothing ran. The one refund assertion carries an
/// in-test positive control: the same recorder records a refund the moment one is applied.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CancelRenderRequestedHandlerTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The stable <c>outbox_messages.type</c> of the event under test.</summary>
    private const string CancelType = "render_cancel_requested.v1";

    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AdapterCancel_IsCalledOnce_WithNodeAndTaskId()
    {
        var world = await SeedWorldAsync();
        var job = await SeedJobAsync(world, status: "submitted", cancelRequested: true);
        var messageId = await SeedCancelMessageAsync(world, job.RenderJobId);
        var render = new FakeRenderClient().EnqueueCancel(Result.Success);
        var money = new RecordingMoney();
        await using var host = await StartHostAsync(render, money);

        var xmin = JobXmin(job);

        (await host.RunOutboxOnceAsync(Ct)).ShouldBe(1, "exactly one cancel message is delivered");

        render.CancelCalls.ShouldBe(1, "the handler asks the adapter to cancel exactly once");
        var call = render.Calls.ShouldHaveSingleItem();
        call.Method.ShouldBe(RenderClientMethod.Cancel);
        call.Node.Id.ShouldBe(world.NodeId, "the node comes from the row's worker_node_id, looked up, never remembered");
        call.TaskId.ShouldBe(job.TaskId, "the adapter task id the row adopted, never a null or another task's id");
        render.StatusCalls.ShouldBe(0);
        render.DeleteCalls.ShouldBe(0, "the handler never deletes the adapter task; the terminal owner cleans up (05 D4)");

        // Read, never written: no stamp of its own, no error code, no status move — the poller owns the row's fate.
        JobXmin(job).ShouldBe(xmin, "the delivery performed no row update");
        AssertJob(job, status: "submitted", errorCode: null, cancelRequested: true);

        // WT51: the request path and this delivery never refund; only the poller's terminal cancelled does.
        money.Refunds.ShouldBeEmpty("a cancel request is not a refund");
        // Positive control for that recorder: the same instance records a refund the moment one is applied.
        await money.RefundAsync(new ReservationId(job.ReservationId), "probe", Ct);
        money.Refunds.ShouldHaveSingleItem().Reason.ShouldBe("probe");

        await AssertDeliveredAsync(messageId);
    }

    [Fact]
    public async Task AdapterCancel_TerminalTask_IsNoOp()
    {
        var world = await SeedWorldAsync();
        var job = await SeedJobAsync(world, status: "submitted", cancelRequested: true);
        var messageId = await SeedCancelMessageAsync(world, job.RenderJobId);

        // 05 D4: a task the adapter already finished answers 200 as a no-op, and the client reports it as the same
        // success as a 202 — the handler must treat it as a delivered cancel, not as an error to retry.
        var render = new FakeRenderClient().EnqueueCancel(Result.Success);
        var money = new RecordingMoney();
        await using var host = await StartHostAsync(render, money);

        (await host.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        render.CancelCalls.ShouldBe(1);
        AssertJob(job, status: "submitted", errorCode: null, cancelRequested: true);
        money.Refunds.ShouldBeEmpty();
        await AssertDeliveredAsync(messageId);
    }

    [Fact]
    public async Task AdapterCancel_Deferred_ThrowsForOutboxRetry()
    {
        var world = await SeedWorldAsync();
        var job = await SeedJobAsync(world, status: "submitted", cancelRequested: true);
        var messageId = await SeedCancelMessageAsync(world, job.RenderJobId);
        var render = new FakeRenderClient().EnqueueFailure(
            RenderClientMethod.Cancel,
            new RenderClientFailure(
                RenderClientFailureKind.Deferred,
                RenderOutcomeCodes.RenderNodeError,
                "scripted",
                Alert: false,
                RetryAfter: TimeSpan.FromSeconds(5)));
        var money = new RecordingMoney();
        await using var host = await StartHostAsync(render, money);

        (await host.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        // The dispatcher caught the handler's throw and recorded the retry: the message is due again, unprocessed,
        // and the reason it stores is the handler's own message.
        (await ScalarAsync(
                "SELECT attempts::text || '|' || (processed_at IS NOT NULL)::text "
                + $"FROM outbox_messages WHERE id = '{messageId}'"))
            .ShouldBe("1|false", "one failed delivery was recorded and the message stays pending for the backoff");
        (await ScalarAsync($"SELECT coalesce(error, '-') FROM outbox_messages WHERE id = '{messageId}'"))
            .ShouldContain("the outbox retries it", Case.Sensitive, "the stored reason is the handler's throw");
        (await ScalarAsync(
                $"SELECT (next_attempt_at > '{Start:O}'::timestamptz)::text FROM outbox_messages WHERE id = '{messageId}'"))
            .ShouldBe("true", "the retry is scheduled after the delivery instant");

        // A node that cannot take the cancel leaves the render untouched: the row keeps its request and is never
        // failed by it (the 60-minute watchdog decides it, BD8).
        AssertJob(job, status: "submitted", errorCode: null, cancelRequested: true);
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1L, "the failed delivery created no retry row");
        render.DeleteCalls.ShouldBe(0);

        // The throw itself, outside the dispatcher: the handler is the thing the outbox retries.
        using var scope = host.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<RenderCancelRequestedHandler>();
        var thrown = await CaptureAsync(handler.HandleAsync(
            new RenderCancelRequested(job.RenderJobId, world.EpisodeId, world.UserId, Start),
            new OutboxContext(messageId, world.UserId, 1, Start),
            Ct).AsTask());

        thrown.ShouldBeOfType<InvalidOperationException>();
        thrown!.Message.ShouldContain("outbox retries it", Case.Sensitive);
        render.CancelCalls.ShouldBe(2, "the direct call is a second, idempotent cancel of the same task");
        money.Refunds.ShouldBeEmpty("a deferred cancel never refunds");
    }

    [Fact]
    public async Task AdapterCancel_TerminalFailure_DoesNotFailTheJob()
    {
        var world = await SeedWorldAsync();
        var job = await SeedJobAsync(world, status: "submitted", cancelRequested: true);
        var messageId = await SeedCancelMessageAsync(world, job.RenderJobId);

        // A task the node no longer knows (render_lost), or a key/idempotency fault: terminal, so nothing is
        // retried — and a cancel request must never fail, refund or write a row (WT51).
        var render = new FakeRenderClient().EnqueueFailure(
            RenderClientMethod.Cancel,
            new RenderClientFailure(
                RenderClientFailureKind.TerminalFailure,
                RenderOutcomeCodes.RenderLost,
                "scripted",
                Alert: true,
                RetryAfter: null));
        var money = new RecordingMoney();
        await using var host = await StartHostAsync(render, money);

        (await host.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        AssertJob(job, status: "submitted", errorCode: null, cancelRequested: true);
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1L, "the terminal cancel failure neither failed the row nor queued a retry");
        money.Refunds.ShouldBeEmpty("only the poller's terminal cancelled refunds");
        render.CancelCalls.ShouldBe(1);
        await AssertDeliveredAsync(messageId);
    }

    [Fact]
    public async Task AdapterCancel_Skips_WhenTheRowIsMissing()
    {
        var world = await SeedWorldAsync();
        var messageId = await SeedCancelMessageAsync(world, Guid.CreateVersion7());
        var render = new FakeRenderClient();
        var money = new RecordingMoney();
        await using var host = await StartHostAsync(render, money);

        (await host.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        // A vanished id (the tenant filter hid a foreign row, or retention swept it) is a logged no-op: the fake
        // refuses an unexpected call, so the delivery's committed outcome is what proves the path ran at all.
        render.Calls.ShouldBeEmpty();
        money.Refunds.ShouldBeEmpty();
        await AssertDeliveredAsync(messageId);
    }

    [Fact]
    public async Task AdapterCancel_Skips_WhenTheRowIsAlreadyTerminal()
    {
        var world = await SeedWorldAsync();
        var job = await SeedJobAsync(world, status: "succeeded", cancelRequested: false);
        var messageId = await SeedCancelMessageAsync(world, job.RenderJobId);
        var render = new FakeRenderClient();
        var money = new RecordingMoney();
        await using var host = await StartHostAsync(render, money);

        (await host.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        render.Calls.ShouldBeEmpty("a terminal row never moves again, so nothing is asked of its node (WT45/WT51)");
        AssertJob(job, status: "succeeded", errorCode: null, cancelRequested: false);
        money.Refunds.ShouldBeEmpty();
        await AssertDeliveredAsync(messageId);
    }

    [Fact]
    public async Task AdapterCancel_Skips_WhenTheRowIsIngesting()
    {
        var world = await SeedWorldAsync();
        var job = await SeedJobAsync(world, status: "ingesting", cancelRequested: true);
        var messageId = await SeedCancelMessageAsync(world, job.RenderJobId);
        var render = new FakeRenderClient();
        var money = new RecordingMoney();
        await using var host = await StartHostAsync(render, money);

        (await host.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        // The output is already being stored (BD8): the adapter task must keep running for the download.
        render.Calls.ShouldBeEmpty();
        AssertJob(job, status: "ingesting", errorCode: null, cancelRequested: true);
        money.Refunds.ShouldBeEmpty();
        await AssertDeliveredAsync(messageId);
    }

    [Fact]
    public async Task AdapterCancel_Skips_WhenTheAdapterTaskIdIsMissing()
    {
        var world = await SeedWorldAsync();
        var job = await SeedJobAsync(world, status: "submitted", withAdapterTask: false, cancelRequested: true);
        var messageId = await SeedCancelMessageAsync(world, job.RenderJobId);
        var render = new FakeRenderClient();
        var money = new RecordingMoney();
        await using var host = await StartHostAsync(render, money);

        (await host.RunOutboxOnceAsync(Ct)).ShouldBe(1);

        // No task was ever adopted (the WT55 re-check refuses a cancel-requested row), so no call is ever attempted
        // with a null task id.
        render.Calls.ShouldBeEmpty();
        AssertJob(job, status: "submitted", errorCode: null, cancelRequested: true);
        money.Refunds.ShouldBeEmpty();
        await AssertDeliveredAsync(messageId);
    }

    /// <summary>One owner, its episode in <c>rendering</c>, the story version the row points at and a node.</summary>
    private async Task<World> SeedWorldAsync()
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
        await Database.ExecuteAsync(
            "INSERT INTO worker_nodes (id, name, base_url, capacity, state, last_heartbeat_at, gpu_model, "
            + "tls_cert_sha256, created_at, updated_at) "
            + $"VALUES ('{nodeId}', 'cancel-node-{nodeId:N}', 'https://127.0.0.1:8443', 2, 'active', now(), "
            + "'RTX 4090', '0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF', now(), now())",
            Ct);

        await Database.ExecuteAsync($"UPDATE episodes SET status = 'rendering' WHERE id = '{episode.Id}'", Ct);

        return new World(user.Id, episode.Id, version.Id, nodeId);
    }

    /// <summary>One <c>render_jobs</c> row of this world, with or without the adapter task it would carry.</summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="status">The row's status.</param>
    /// <param name="withAdapterTask">Whether the row is submitted to a node (its two stamps move together).</param>
    /// <param name="cancelRequested">Whether the row already carries the owner's request.</param>
    /// <returns>The ids the assertions of the delivery read.</returns>
    private async Task<Job> SeedJobAsync(
        World world,
        string status,
        bool withAdapterTask = true,
        bool cancelRequested = false)
    {
        var jobId = Guid.CreateVersion7();
        var reservationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7().ToString("D");
        var submittedAt = Start.AddMinutes(-5);
        var startedAt = Start.AddMinutes(-4);

        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, worker_node_id, adapter_task_id, submitted_at, started_at, next_poll_at, "
            + "cancel_requested_at) "
            + $"VALUES ('{jobId}', '{world.UserId}', '{world.EpisodeId}', '{world.StoryVersionId}', "
            + $"'{reservationId}', '{status}', 'voice', 30, '{ParamsJson(world.StoryVersionId)}'::jsonb, 1, "
            + $"'{world.NodeId}', {(withAdapterTask ? $"'{taskId}'" : "NULL")}, '{submittedAt:O}', "
            + $"'{startedAt:O}', '{Start:O}', {(cancelRequested ? $"'{Start:O}'" : "NULL")})",
            Ct);

        return new Job(jobId, reservationId, taskId);
    }

    /// <summary>
    /// Writes the one <c>outbox_messages</c> row the interceptor would have written for the command: the event's
    /// own name, the payload of the record as <see cref="OutboxJson"/> serializes it, and the owner the delivery
    /// must be pinned to.
    /// </summary>
    /// <param name="world">The seeded world (the payload's owner and episode).</param>
    /// <param name="renderJobId">The row the event names; a missing one is the vanished-id case.</param>
    /// <returns>The message id, for the delivery's own outcome assertion.</returns>
    private async Task<Guid> SeedCancelMessageAsync(World world, Guid renderJobId)
    {
        var id = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(
            new RenderCancelRequested(renderJobId, world.EpisodeId, world.UserId, Start),
            OutboxJson.Options);

        await Database.ExecuteAsync(
            "INSERT INTO outbox_messages (id, type, payload, user_id, occurred_at, next_attempt_at, attempts) "
            + $"VALUES ('{id}', '{CancelType}', '{payload}', '{world.UserId}', '{Start:O}', '{Start:O}', 0)",
            Ct);

        return id;
    }

    /// <summary>
    /// A started Worker host with the node client and the ledger scripted — nothing else about the composition
    /// root changes, so the delivery runs the production outbox path.
    /// </summary>
    /// <param name="render">The scripted node client (it refuses a call no test scripted).</param>
    /// <param name="money">The ledger recorder; the handler must never write to it.</param>
    /// <returns>The started host.</returns>
    private async Task<WorkerHost> StartHostAsync(FakeRenderClient render, RecordingMoney money)
    {
        var host = new WorkerHost(Database, new FakeClock(Start))
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRenderClient>();
                services.AddSingleton<IRenderClient>(render);
                services.RemoveAll<ICreditLedger>();
                services.AddSingleton<ICreditLedger>(money);
            });

        await host.StartAsync(Ct);

        return host;
    }

    /// <summary>The row's status, its error code and whether it carries a cancel request, in one read.</summary>
    private void AssertJob(Job job, string status, string? errorCode, bool cancelRequested) =>
        ScalarText(
                "SELECT status || '|' || coalesce(error_code, '-') || '|' || "
                + $"(cancel_requested_at IS NOT NULL)::text FROM render_jobs WHERE id = '{job.RenderJobId}'")
            .ShouldBe($"{status}|{errorCode ?? "-"}|{(cancelRequested ? "true" : "false")}");

    /// <summary>The row's <c>xmin</c>: the same value proves the delivery wrote nothing to it.</summary>
    private string JobXmin(Job job) =>
        ScalarText($"SELECT xmin::text FROM render_jobs WHERE id = '{job.RenderJobId}'");

    /// <summary>
    /// The delivery's own committed outcome: without it, a "no adapter call" assertion could pass because the
    /// message was never delivered at all.
    /// </summary>
    private async Task AssertDeliveredAsync(Guid messageId) =>
        (await ScalarAsync($"SELECT (processed_at IS NOT NULL)::text FROM outbox_messages WHERE id = '{messageId}'"))
            .ShouldBe("true", "the delivery committed, so the handler really ran");

    private string ScalarText(string sql) => SqlProbe.ScalarText(Database, sql);

    private async Task<string> ScalarAsync(string sql) =>
        (await Database.QueryStringsAsync(sql, Ct)).Single();

    /// <summary>The outcome of one call that is expected to throw, or <c>null</c> when it completed.</summary>
    private static async Task<Exception?> CaptureAsync(Task call)
    {
        try
        {
            await call;

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

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

    /// <summary>The ids one seeded cancel world carries.</summary>
    private sealed record World(Guid UserId, Guid EpisodeId, Guid StoryVersionId, Guid NodeId);

    /// <summary>The one row a delivery targets, and the reservation an unrefunded path must leave alone.</summary>
    private sealed record Job(Guid RenderJobId, Guid ReservationId, string TaskId);

    /// <summary>
    /// The ledger port of one delivery as a recorder. The handler must never write to it, so an unrefunded
    /// delivery is an empty list — and the positive control in
    /// <see cref="AdapterCancel_IsCalledOnce_WithNodeAndTaskId"/> proves the list fills when a refund is applied.
    /// </summary>
    private sealed class RecordingMoney : ICreditLedger
    {
        public List<(Guid ReservationId, string Reason)> Refunds { get; } = [];

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct) =>
            throw new NotSupportedException("the cancel delivery never reserves");

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            throw new NotSupportedException("the cancel delivery never settles");

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            Refunds.Add((reservationId.Value, reason));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
