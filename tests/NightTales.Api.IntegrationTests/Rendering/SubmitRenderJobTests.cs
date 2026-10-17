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
using NightTales.Application.Features.Stories;
using NightTales.Application.Rendering;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Series;
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
    public async Task Submit_QueueAlertFiresOnce_WhenTheQueueCrossesTheThreshold()
    {
        var world = await SeedAsync();
        var client = new FakeRenderClient();

        // Queued exactly 360 minutes before the clock, so this delivery is the one that crosses
        // Render:QueueAlertMinutes while the previous delivery instant (now - HealthPollSeconds) is still inside
        // the threshold. The id's own timestamp is the row's only creation stamp (WT49).
        var jobId = SeedQueuedJob(world, Guid.CreateVersion7(Start.AddMinutes(-360)));
        var selector = new FakeNodeSelector().EnqueueUnavailable().EnqueueUnavailable();
        var sink = new CapturingLoggerProvider();
        var clock = new FakeClock(Start);

        await using var worker = await StartWorkerAsync(client, selector, sink, clock);
        await RunSubmitAsync(worker, jobId);

        worker.Jobs.Failures.ShouldBeEmpty();
        client.SubmitCalls.ShouldBe(0);
        var alert = sink.Entries.Where(entry => entry.EventId == SubmitRenderJob.QueueAlertEventId).ShouldHaveSingleItem();
        alert.Level.ShouldBe(LogLevel.Error);
        alert.Message.ShouldContain("render_queue_alert", Case.Sensitive);
        alert.Message.ShouldContain(Id(jobId), Case.Sensitive);

        // The next delivery is one health cadence later and already past the threshold: the alarm has fired for
        // the crossing, so a second line would be noise — 120 of them an hour per stuck job (F8).
        clock.Advance(TimeSpan.FromSeconds(worker.Services.GetRequiredService<RenderNodesOptions>().HealthPollSeconds));
        await RunSubmitAsync(worker, jobId);

        worker.Jobs.Failures.ShouldBeEmpty();
        sink.Entries.Count(entry => entry.EventId == SubmitRenderJob.QueueAlertEventId)
            .ShouldBe(1, "a job queued past the threshold logs the crossing once, not on every reschedule");
        SqlProbe.ScalarText(Database, $"SELECT status FROM render_jobs WHERE id = '{Id(jobId)}'").ShouldBe("queued");
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
        var reschedule = worker.Jobs.Enqueued[^1];
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
        worker.Jobs.Enqueued[^1].Delay.ShouldBe(TimeSpan.FromSeconds(healthPoll));
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

        // The task the adapter accepted has to be taken off the node again: both cleanup calls answer success
        // (the fake refuses an unexpected call rather than inventing an answer).
        client.EnqueueCancel(Result.Success);
        client.EnqueueDelete(Result.Success);
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

    /// <summary>
    /// A terminal adapter answer on the submit itself (F1/SEC-1): the row is still <c>queued</c> — no task was
    /// ever adopted — but it must end anyway, or it would hold the episode and its reservation for good.
    /// </summary>
    /// <remarks>
    /// The scripted answer is the shape the mapper gives a rotated node key (401 <c>Unauthorized</c>) or a body the
    /// node refuses as a contract violation (400): a terminal <c>render_node_error</c> that asks for the operator
    /// alarm. <c>render_node_error</c> is a transient D5 code, so the policy queues attempt 2 and the episode stays
    /// <c>rendering</c>.
    /// </remarks>
    [Fact]
    public async Task Submit_TerminalFailure_FailsTheQueuedRow_AndLeavesTheRetryVerdict()
    {
        var world = await SeedAsync();
        var jobId = await RequestRenderAsync(world);
        var node = await SeedNodeAsync(world.UserId);
        var client = new FakeRenderClient();
        var money = new RecordingWorkerMoney();

        client.EnqueueFailure(RenderClientMethod.Submit, Terminal("render_node_error", alert: true));
        var selector = new FakeNodeSelector().Enqueue(node);
        var sink = new CapturingLoggerProvider();

        await using var worker = await StartWorkerAsync(client, selector, sink, money: money);
        await worker.RunOutboxOnceAsync(Ct);
        await worker.Jobs.RunPendingAsync(Ct);

        worker.Jobs.Failures.ShouldBeEmpty("a terminal adapter answer ends the row, it is never thrown to Hangfire");
        client.SubmitCalls.ShouldBe(1);
        client.CancelCalls.ShouldBe(0, "the task was never accepted, so there is nothing to clean up");
        sink.Entries.ShouldContain(entry => entry.EventId == 6196, "a terminal answer with Alert raises the operator alarm");

        // The row failed without ever adopting a task: no poll instant, no adapter task, one verdict.
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || error_code || '|' || coalesce(adapter_task_id, '-') || '|' || coalesce(next_poll_at::text, '-') "
                + $"FROM render_jobs WHERE id = '{Id(jobId)}'")
            .ShouldBe("failed|render_node_error|-|-");

        // Exactly one refund of this attempt's own reservation, one audit row, one render_failed.v1.
        var reservationId = SqlProbe.ScalarText(Database, $"SELECT reservation_id FROM render_jobs WHERE id = '{Id(jobId)}'");
        money.Refunds.ShouldBe([(Guid.Parse(reservationId), "render_failed")]);
        SqlProbe.QueryStrings(
                Database,
                "SELECT action || '|' || (data->>'code') || '|' || (data->>'final') FROM audit_logs "
                + $"WHERE entity_id = '{Id(jobId)}'")
            .ShouldBe(["render.failed|render_node_error|false"]);
        CountRenderFailedOutbox(world).ShouldBe(1L);
        RenderFailedFinal(world).ShouldBe("false");

        // A transient code on attempt 1: the policy queues attempt 2 and the episode stays rendering (WT28).
        var rows = SqlProbe.QueryStrings(
            Database,
            $"SELECT id || '|' || attempt::text || '|' || status FROM render_jobs WHERE episode_id = '{Id(world.EpisodeId)}'");
        rows.ShouldContain($"{Id(jobId)}|1|failed");
        rows.ShouldContain(row => row.EndsWith("|2|queued", StringComparison.Ordinal),
            "render_node_error is retryable, so a fresh attempt is queued");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{Id(world.EpisodeId)}'")
            .ShouldBe("rendering", "a retry row keeps the episode rendering");
    }

    /// <summary>
    /// The same terminal answer on the last automatic attempt: the verdict is final, so the episode comes back to
    /// <c>story_approved</c>, no third attempt is queued and both the audit row and <c>render_failed.v1</c> say so.
    /// </summary>
    [Fact]
    public async Task Submit_TerminalFailure_OnTheLastAttempt_IsFinal_AndReleasesTheEpisode()
    {
        var world = await SeedAsync();
        var jobId = await RequestRenderAsync(world);
        var node = await SeedNodeAsync(world.UserId);
        var client = new FakeRenderClient();
        var money = new RecordingWorkerMoney();

        // Attempt 2 is the last one RenderRetryPolicy allows (MaxAttempts = 2).
        SqlProbe.Execute(Database, $"UPDATE render_jobs SET attempt = 2 WHERE id = '{Id(jobId)}'");

        client.EnqueueFailure(RenderClientMethod.Submit, Terminal("render_node_error", alert: true));
        var selector = new FakeNodeSelector().Enqueue(node);

        await using var worker = await StartWorkerAsync(client, selector, money: money);
        await worker.RunOutboxOnceAsync(Ct);
        await worker.Jobs.RunPendingAsync(Ct);

        worker.Jobs.Failures.ShouldBeEmpty();
        SqlProbe.ScalarText(Database, $"SELECT status || '|' || error_code FROM render_jobs WHERE id = '{Id(jobId)}'")
            .ShouldBe("failed|render_node_error");
        money.Refunds.Count.ShouldBe(1);
        SqlProbe.QueryStrings(
                Database,
                "SELECT action || '|' || (data->>'code') || '|' || (data->>'final') FROM audit_logs "
                + $"WHERE entity_id = '{Id(jobId)}'")
            .ShouldBe(["render.failed|render_node_error|true"]);
        SqlProbe.ScalarText(
                Database,
                $"SELECT (data ? 'retryJobId')::text FROM audit_logs WHERE entity_id = '{Id(jobId)}'")
            .ShouldBe("false", "a final verdict carries no retry row");
        CountRenderFailedOutbox(world).ShouldBe(1L);
        RenderFailedFinal(world).ShouldBe("true");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM render_jobs WHERE episode_id = '{Id(world.EpisodeId)}'")
            .ShouldBe(1L, "the attempt budget is spent: no third attempt is queued");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{Id(world.EpisodeId)}'")
            .ShouldBe("story_approved", "a final failure hands the episode back to its owner");
    }

    /// <summary>
    /// A transient adapter answer (F2): the row ends through the recorder instead of being thrown to Hangfire,
    /// because a rethrow leaves the row <c>queued</c> once the attempts are spent — unrefunded, still in the
    /// open-bounds sum, and still holding <c>ux_render_jobs_active_episode</c>.
    /// </summary>
    [Fact]
    public async Task Submit_TransientFailure_EndsTheRow_InsteadOfThrowingToHangfire()
    {
        var world = await SeedAsync();
        var jobId = await RequestRenderAsync(world);
        var node = await SeedNodeAsync(world.UserId);
        var client = new FakeRenderClient();
        var money = new RecordingWorkerMoney();

        client.EnqueueFailure(RenderClientMethod.Submit, Transient("render_node_error"));
        var selector = new FakeNodeSelector().Enqueue(node);
        var sink = new CapturingLoggerProvider();

        await using var worker = await StartWorkerAsync(client, selector, sink, money: money);
        await worker.RunOutboxOnceAsync(Ct);
        await worker.Jobs.RunPendingAsync(Ct);

        worker.Jobs.Failures.ShouldBeEmpty("the attempt is ended in the job's own transaction, never rethrown");
        client.SubmitCalls.ShouldBe(1);
        sink.Entries.ShouldContain(entry => entry.EventId == 6198);
        SqlProbe.ScalarText(Database, $"SELECT status || '|' || error_code FROM render_jobs WHERE id = '{Id(jobId)}'")
            .ShouldBe("failed|render_node_error");
        money.Refunds.Count.ShouldBe(1, "the reservation is refunded once, exactly as on the terminal path");
        SqlProbe.QueryStrings(
                Database,
                "SELECT action || '|' || (data->>'code') || '|' || (data->>'final') FROM audit_logs "
                + $"WHERE entity_id = '{Id(jobId)}'")
            .ShouldBe(["render.failed|render_node_error|false"]);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM render_jobs WHERE episode_id = '{Id(world.EpisodeId)}' AND attempt = 2 AND status = 'queued'")
            .ShouldBe(1L);
    }

    /// <summary>
    /// A recipe the submit body cannot be built from (F2's second half): the attempt fails at once with the
    /// catalogued code and is never retried, because a rebuilt body would fail the same way.
    /// </summary>
    /// <remarks>
    /// The stored params name a language the wire table does not know (<c>render_language_unknown</c> is the
    /// precise reason in the log); the code the row and the events carry is the catalogued
    /// <c>render_assembly_failed</c>, because 05 D5 has no code for a broken recipe.
    /// </remarks>
    [Fact]
    public async Task Submit_RecipeCannotBeBuilt_FailsTerminal_WithoutSpendingARetry()
    {
        var world = await SeedAsync();
        var jobId = SeedQueuedJob(world, Guid.CreateVersion7(), language: "xx");
        var node = await SeedNodeAsync(world.UserId);
        var client = new FakeRenderClient();
        var money = new RecordingWorkerMoney();

        var selector = new FakeNodeSelector().Enqueue(node);
        var sink = new CapturingLoggerProvider();

        await using var worker = await StartWorkerAsync(client, selector, sink, money: money);
        await RunSubmitAsync(worker, jobId);

        worker.Jobs.Failures.ShouldBeEmpty();
        client.SubmitCalls.ShouldBe(0, "a recipe that cannot be built never reaches a node");
        sink.Entries.ShouldContain(entry => entry.EventId == 6195 && entry.Message.Contains(SubmitRenderJob.LanguageUnknownCode, StringComparison.Ordinal));
        SqlProbe.ScalarText(Database, $"SELECT status || '|' || error_code FROM render_jobs WHERE id = '{Id(jobId)}'")
            .ShouldBe("failed|render_assembly_failed");
        money.Refunds.Count.ShouldBe(1);
        SqlProbe.QueryStrings(
                Database,
                "SELECT action || '|' || (data->>'code') || '|' || (data->>'final') FROM audit_logs "
                + $"WHERE entity_id = '{Id(jobId)}'")
            .ShouldBe(["render.failed|render_assembly_failed|true"]);
        CountRenderFailedOutbox(world).ShouldBe(1L);
        RenderFailedFinal(world).ShouldBe("true");
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{Id(world.EpisodeId)}'")
            .ShouldBe(1L, "a broken recipe is deterministic, so it is never retried");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{Id(world.EpisodeId)}'")
            .ShouldBe("story_approved");
    }

    [Fact]
    public async Task Submit_NeverLogsTheNodeBaseUrlOrItsKey()
    {
        var world = await SeedAsync();
        var jobId = await RequestRenderAsync(world);

        // The node the selector hands the job carries a URL and a key hint that must never reach a log line.
        // The URL is plain http: an https node row must carry a pinned fingerprint (WorkerNode.Diagnose), and
        // the pinning is not what this test is about.
        const string Node = "render-node-secret-4f2a.example";
        var node = await SeedNodeAsync(world.UserId, $"http://{Node}:8443/");
        SqlProbe.Execute(
            Database,
            $"UPDATE worker_nodes SET api_key_hint = 'canary-hint-aaaa' WHERE id = '{Id(node.Id)}'");

        var client = new FakeRenderClient();
        client.EnqueueFailure(
            RenderClientMethod.Submit,
            new RenderClientFailure(
                RenderClientFailureKind.Deferred,
                "adapter_capacity_full",
                "scripted",
                Alert: true,
                RetryAfter: TimeSpan.FromSeconds(30)));
        var selector = new FakeNodeSelector().Enqueue(node);
        var sink = new CapturingLoggerProvider();

        await using var worker = await StartWorkerAsync(client, selector, sink);
        await worker.RunOutboxOnceAsync(Ct);
        await worker.Jobs.RunPendingAsync(Ct);

        // 6196 is the adapter alert of SubmitRenderJob: the alerting path is the one that logs most.
        sink.Entries.ShouldContain(entry => entry.EventId == 6196, "the adapter alert is one of the lines under test");
        var messages = sink.Entries.Select(entry => entry.Message).ToList();
        messages.ShouldAllBe(message => !message.Contains(Node, StringComparison.Ordinal));
        messages.ShouldAllBe(message => !message.Contains("canary-hint-aaaa", StringComparison.Ordinal));
        messages.ShouldAllBe(message => !message.Contains("8443", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains(Id(jobId), StringComparison.Ordinal));
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
    /// <param name="baseUrl">The node's URL; a redaction test gives it a canary host.</param>
    /// <returns>The node the selector hands the job.</returns>
    private async Task<WorkerNode> SeedNodeAsync(Guid userId, string baseUrl = "http://render-node.example/")
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));
        var node = WorkerNode.Create(
            $"submit-job-{Guid.NewGuid():N}",
            new Uri(baseUrl),
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
    /// <param name="attempt">The attempt number the row carries (2 for the last automatic attempt).</param>
    /// <param name="language">The language the stored params name; an unknown one makes the recipe unbuildable.</param>
    /// <returns>The row's id.</returns>
    private Guid SeedQueuedJob(World world, Guid jobId, int attempt = 1, string language = "vi")
    {
        SqlProbe.Execute(Database,
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, percent, params, attempt) "
            + $"VALUES ('{Id(jobId)}', '{Id(world.UserId)}', '{Id(world.EpisodeId)}', '{Id(world.VersionId)}', "
            + $"'{Id(Guid.CreateVersion7())}', 'queued', '{RenderJob.QueuedStage}', 0, '{ParamsJson(world.VersionId, language)}'::jsonb, {attempt})");

        return jobId;
    }

    /// <summary>
    /// A complete <c>render-params.v1</c> document (05 D11): every member of <see cref="RenderParamsV1"/> is
    /// <c>[JsonRequired]</c>, so the column's converter refuses a row whose <c>params</c> omitted one — and the
    /// submit job could not load the row at all.
    /// </summary>
    /// <param name="storyVersionId">The story version the params point at.</param>
    /// <param name="language">The language name the params carry.</param>
    /// <returns>The JSON text the <c>params</c> column stores.</returns>
    private static string ParamsJson(Guid storyVersionId, string language = "vi") =>
        JsonSerializer.Serialize(new RenderParamsV1(
            RenderParamsV1.CurrentMapperVersion,
            storyVersionId,
            "landscape",
            language,
            "kokoro:test",
            Guid.CreateVersion7(),
            "style=test@0123abcd;neg=v1",
            new ImagePlanSummary(3, 1, 2, 1, 0, [], ["paragraph:1"], "style=test@0123abcd;neg=v1"),
            new SubtitlesParams(true, "landscape@1"),
            null,
            3));

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
        CapturingLoggerProvider? sink = null,
        FakeClock? clock = null,
        RecordingWorkerMoney? money = null)
    {
        var host = new WorkerHost(Database, clock ?? new FakeClock(Start))
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

                // The recorder the failure/retry assertions read: the refund by reservation id is what the
                // terminal paths are proved on, and the three price/budget ports make the retry decision
                // deterministic (the Worker's own catalog reads the database, the guard reads the open bounds).
                if (money is not null)
                {
                    services.RemoveAll<ICreditLedger>();
                    services.AddSingleton<ICreditLedger>(money);
                    services.RemoveAll<IBudgetGuard>();
                    services.AddSingleton<IBudgetGuard>(money);
                    services.RemoveAll<IPriceCatalog>();
                    services.AddSingleton<IPriceCatalog>(money);
                    services.RemoveAll<IImageCostEstimator>();
                    services.AddSingleton<IImageCostEstimator>(money);
                }

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

    /// <summary>
    /// A terminal adapter answer as the job's own client reports it, wrapped for the fake's
    /// <c>EnqueueFailure</c> (the real client produces this shape through <c>AdapterErrorMapper</c>: a 401, a 400
    /// contract violation, a 409 idempotency conflict or a 413 all become a terminal <c>render_node_error</c>).
    /// </summary>
    /// <param name="code">The 05 D5 outcome code.</param>
    /// <param name="alert">Whether the answer asks for the operator alarm.</param>
    /// <returns>The failure to script.</returns>
    private static RenderClientFailure Terminal(string code, bool alert) =>
        new(RenderClientFailureKind.TerminalFailure, code, "scripted", alert, RetryAfter: null);

    /// <summary>A transient adapter answer as the job's own client reports it (a transport error, a timeout, a 5xx).</summary>
    /// <param name="code">The 05 D5 outcome code.</param>
    /// <returns>The failure to script.</returns>
    private static RenderClientFailure Transient(string code) =>
        new(RenderClientFailureKind.TransientFailure, code, "scripted", Alert: false, RetryAfter: null);

    /// <summary>The <c>render_failed.v1</c> rows of this world: the render request's own row is a different type.</summary>
    /// <param name="world">The test world.</param>
    /// <returns>The row count.</returns>
    private long CountRenderFailedOutbox(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM outbox_messages WHERE type = 'render_failed.v1' AND user_id = '{Id(world.UserId)}'");

    /// <summary>The <c>final</c> member of the one <c>render_failed.v1</c> payload (WT28's retry verdict).</summary>
    /// <param name="world">The test world.</param>
    /// <returns>The serialized boolean, as text.</returns>
    private string RenderFailedFinal(World world) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT payload->>'final' FROM outbox_messages WHERE type = 'render_failed.v1' AND user_id = '{Id(world.UserId)}'");

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

    /// <summary>
    /// The four money ports one attempt's failure and retry need, as recorders: the failure recorder refunds
    /// through <see cref="ICreditLedger"/>, and <see cref="RenderRetryPolicy"/> prices the next attempt through
    /// the other three. Every answer succeeds, so whether a retry row appears depends only on the codes a test
    /// scripts — the Worker's own catalogue and budget guard read the database.
    /// </summary>
    private sealed class RecordingWorkerMoney :
        IPriceCatalog,
        IImageCostEstimator,
        IBudgetGuard,
        ICreditLedger
    {
        /// <summary>Every refund the recorder issued, in order: the reservation id and its reason.</summary>
        public List<(Guid ReservationId, string Reason)> Refunds { get; } = [];

        /// <summary>The <c>excludingRunId</c> of every budget check, in order.</summary>
        public List<Guid?> ExcludedRunIds { get; } = [];

        /// <inheritdoc />
        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromResult<ErrorOr<PriceVersion>>(
                PriceVersion.Create(1, Start.AddDays(-1), "{}", "{}", "hash", Start.AddDays(-1)));

        /// <inheritdoc />
        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<CostBound> WorstCaseAsync(Aspect aspect, int maxTerms, int priceVersion, CancellationToken ct) =>
            Task.FromResult(new CostBound(1.23m));

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null)
        {
            ExcludedRunIds.Add(excludingRunId);

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }

        /// <inheritdoc />
        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(Guid.CreateVersion7()));

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            Refunds.Add((reservationId.Value, reason));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
