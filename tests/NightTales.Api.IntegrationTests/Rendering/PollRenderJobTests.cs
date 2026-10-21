using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.Application.Features.Stories;
using NightTales.Application.Rendering;
using NightTales.Application.Telemetry;
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
/// push, the <c>Render:MaxMinutes</c> watchdog (applied to a non-terminal answer only, with the best-effort
/// adapter cancel and delete of the task it ends), the transient and deferred reschedules that keep a broken
/// node from pinning the claim, the terminal branches (succeeded → ingest, failed → usage/classify/refund/audit
/// and the retry verdict, cancelled → the only refund of a cancelled render), the refused bodies, and the
/// terminal-row skip.
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
    }

    [Fact]
    public async Task Progress_RegressedPercent_IsIgnored_AndThePollContinues()
    {
        var world = await SeedJobAsync("running", "images", percent: 40);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        // A status that moves backwards is a regressed report, not a lost render: failing it would refund a paid
        // attempt and start a second paid one for a counter that moved backwards. The stored percent is kept,
        // nothing is pushed, and the claim instant still moves so the row is not re-claimed by every tick (F6).
        render.EnqueueStatus(Status(world, RenderStates.Running, "images", 25));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "running", stage: "images", percent: 40);
        ScalarText("SELECT percent::text", world).ShouldBe("40", "UpdateProgress never decreases percent (AC5)");
        ScalarBool("SELECT (next_poll_at = submitted_at + interval '10 seconds')::text", world).ShouldBeTrue(
            "the poll keeps going: the claim instant moves");
        harness.Notifier.Events.ShouldBeEmpty("a report that moved nothing pushes nothing");
        harness.Money.Refunds.ShouldBeEmpty("a regressed report never fails a paid attempt");
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1L, "and never queues a second paid attempt");
    }

    [Fact]
    public async Task Transient_StatusAnswer_ReschedulesAndDoesNotThrow()
    {
        var world = await SeedJobAsync("running", "voice", percent: 20);
        var render = new FakeRenderClient();
        var sink = new CapturingLoggerProvider();
        await using var harness = await StartHostAsync(render, sink: sink);

        // A node that is down answers transient. A thrown tick would leave next_poll_at where it was, so the row
        // would stay at the head of the claim on every tick, with a full exception line, and starve the healthy
        // nodes' jobs once they are more than a batch (F4).
        render.EnqueueFailure(
            RenderClientMethod.GetStatus,
            new RenderClientFailure(
                RenderClientFailureKind.TransientFailure,
                "render_node_error",
                "scripted",
                Alert: false,
                RetryAfter: null));

        var thrown = await CaptureAsync(PollAsync(harness.Host, world.RenderJobId));

        thrown.ShouldBeNull("a transient answer is not an error of the poll");
        render.StatusCalls.ShouldBe(1);
        AssertRow(world, status: "running", stage: "voice", percent: 20);
        ScalarBool("SELECT (next_poll_at = submitted_at + interval '10 seconds')::text", world).ShouldBeTrue(
            "the transient answer moves the claim instant to the Render:PollSeconds cadence");
        harness.Notifier.Events.ShouldBeEmpty();
        harness.Money.Refunds.ShouldBeEmpty("the attempt is not failed: no refund and no retry");
        (await ScalarAsync($"SELECT attempt::text FROM render_jobs WHERE id = '{world.RenderJobId}'")).ShouldBe("1");

        // Exactly one warning, and it names the row and the code: no exception, and no stack trace (F4).
        var transient = sink.Entries.Where(entry => entry.EventId == 6230).ToList();
        transient.Count.ShouldBe(1);
        transient[0].Level.ShouldBe(LogLevel.Warning);
        transient[0].Message.ShouldContain(world.RenderJobId.ToString("D"), Case.Sensitive);
        transient[0].Message.ShouldContain("render_node_error", Case.Sensitive);
    }

    [Fact]
    public async Task Deferred_RetryAfterPastTheWatchdog_IsClampedToIt()
    {
        var world = await SeedJobAsync("running", "voice", percent: 20);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        // Retry-After is header text: a node may ask for a week, but it may not park a row past the instant
        // Render:MaxMinutes ends the attempt, or the watchdog could never fire on a node that keeps deferring
        // (F13).
        render.EnqueueFailure(
            RenderClientMethod.GetStatus,
            new RenderClientFailure(
                RenderClientFailureKind.Deferred,
                "adapter_capacity_full",
                "scripted",
                Alert: false,
                RetryAfter: TimeSpan.FromDays(7)));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "running", stage: "voice", percent: 20);
        ScalarBool("SELECT (next_poll_at = submitted_at + interval '60 minutes')::text", world).ShouldBeTrue(
            "a Retry-After past the watchdog horizon is clamped to it");
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
        ScalarBool("SELECT (next_poll_at = submitted_at + interval '30 seconds')::text", world).ShouldBeTrue(
            "a deferred answer moves the claim instant by its own bounded Retry-After (F13)");
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

        // The adapter is asked first: the watchdog only ends an answer that is still non-terminal at the
        // deadline, so a task that finished at minute 59 is recorded as what it is instead of being thrown away
        // and re-rendered at full image cost (F5). This task is still running, so the attempt is over.
        render.EnqueueStatus(Status(world, RenderStates.Running, "subtitles", 50));
        render.EnqueueCancel(default);
        render.EnqueueDelete(default);

        await PollAsync(harness.Host, world.RenderJobId);

        render.StatusCalls.ShouldBe(1, "the status answer is what decides the watchdog");
        render.SubmitCalls.ShouldBe(0, "the same adapter task is never re-submitted");
        render.CancelCalls.ShouldBe(1, "the task the node may still run is stopped, best effort (SEC-8)");
        render.DeleteCalls.ShouldBe(1, "and its material is released, after the failure commit");

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

        // The poller's claim is status-based, so neither the timed-out row (failed) nor the queued retry is in it.
        // Scoped to this episode: the other tests of this class and their rows share the database.
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}' "
                + "AND status IN ('submitted','running')")
            .ShouldBe(0L);
    }

    [Fact]
    public async Task Timeout_TaskThatFinishedBeforeThePoll_ItsAnswerWins()
    {
        // The row's own clock says the attempt is past Render:MaxMinutes, but the task finished: what the
        // adapter answered is recorded as what it is, because the watchdog may never discard a finished render
        // and re-render it at full image cost (F5, 05 D15 P7).
        var world = await SeedJobAsync(
            "running",
            "images",
            percent: 99,
            submittedMinutesAgo: 61,
            startedMinutesAgo: 60);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        render.EnqueueStatus(Status(world, RenderStates.Succeeded, "done", 100));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "ingesting", stage: "done", percent: 100);
        harness.Ingest.Enqueued.ShouldBe([world.RenderJobId]);
        harness.Money.Refunds.ShouldBeEmpty("a succeeded render is settled by the ingest, never refunded (WT51)");
        render.CancelCalls.ShouldBe(0, "nothing is cancelled: the render finished");
        render.DeleteCalls.ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1L, "a finished render never queues a retry row");
    }

    [Fact]
    public async Task Refusal_OverLongStage_IsANodeError_NotAnEndlessCommitFailure()
    {
        var world = await SeedJobAsync("running", "voice", percent: 20);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        // render_jobs.stage is varchar(32), so a longer adapter stage would be refused by the database on every
        // tick and hold the row (and its bound) until the watchdog: the attempt ends as render_node_error
        // instead, and the task it came from is cleaned up (F7/SEC-5).
        render.EnqueueStatus(Status(world, RenderStates.Running, new string('i', 33), 40));
        render.EnqueueCancel(default);
        render.EnqueueDelete(default);

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "failed", stage: "voice", percent: 20, errorCode: "render_node_error");
        harness.Notifier.Events.ShouldBeEmpty("the adapter's stage never reaches a client");
        harness.Money.Refunds.ShouldBe([(world.ReservationId, "render_failed")]);
        render.CancelCalls.ShouldBe(1, "a refused body still collects the task it came from");
        render.DeleteCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Refusal_StageOutsideTheWireShape_IsANodeError()
    {
        var world = await SeedJobAsync("running", "voice", percent: 20);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        // The adapter's stage is stored verbatim and travels into a realtime payload, so a text outside the
        // wire shape `^[a-z_]{1,32}$` is a body this job refuses: a state a misbehaving node invented is not a
        // stage a client may see (SEC-5).
        render.EnqueueStatus(Status(world, RenderStates.Running, "Images 50%", 40));
        render.EnqueueCancel(default);
        render.EnqueueDelete(default);

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "failed", stage: "voice", percent: 20, errorCode: "render_node_error");
        harness.Notifier.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnknownState_IsLoggedSanitised_AndEndsAsANodeError()
    {
        var world = await SeedJobAsync("running", "voice", percent: 20);
        var render = new FakeRenderClient();
        var sink = new CapturingLoggerProvider();
        await using var harness = await StartHostAsync(render, sink: sink);

        // A state 05.4.1-T2 does not define is adapter-supplied text: it ends the attempt as a node error, and
        // the log line carries the fixed token instead of the node's own string, so a misbehaving node can
        // neither forge a log line nor push text into one (SEC-6).
        render.EnqueueStatus(Status(world, "Not A State\nrender_poll_fake", "voice", 40));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "failed", stage: "voice", percent: 20, errorCode: "render_node_error");
        var unknown = sink.Entries.Where(entry => entry.EventId == 6227).ToList();
        unknown.Count.ShouldBe(1);
        unknown[0].Message.ShouldContain("state=invalid", Case.Sensitive);
        unknown[0].Message.ShouldNotContain("render_poll_fake", Case.Sensitive);
        harness.Notifier.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Timeout_OnACancelRequestedRow_QueuesNoRetry()
    {
        // The owner cancelled this render and the adapter never answered cancelled: the watchdog ends it as
        // render_timeout, and the verdict is final. A retry here would re-render and re-charge an episode its
        // creator already cancelled (WT51, SEC-4).
        var world = await SeedJobAsync(
            "running",
            "voice",
            percent: 30,
            cancelRequested: true,
            submittedMinutesAgo: 61,
            startedMinutesAgo: 60);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);
        render.EnqueueStatus(Status(world, RenderStates.Running, "voice", 30));
        render.EnqueueCancel(default);
        render.EnqueueDelete(default);

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "failed", stage: "voice", percent: 30, errorCode: "render_timeout");
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1L, "a cancelled render is never retried");
        SqlProbe.QueryStrings(
                Database,
                "SELECT (data->>'final')::text FROM audit_logs "
                + $"WHERE entity_id = '{world.RenderJobId}'")
            .ShouldBe(["true"], "the cancellation makes the verdict final (WT50)");
        harness.Money.Refunds.ShouldBe(
            [(world.ReservationId, "render_failed")],
            "the attempt is still refunded once");
        CountOutbox(world).ShouldBe(1, "exactly one render_failed.v1 carries the final verdict");
        EpisodeStatus(world).ShouldBe("story_approved", "the episode comes back to its creator");
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

        // The node answered about a task this row no longer owns (H2): never progress, always a lost render —
        // and the task this row does own is stopped and released, best effort (SEC-8).
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
        render.EnqueueCancel(default);
        render.EnqueueDelete(default);

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "failed", stage: "voice", percent: 30, errorCode: "render_lost");
        ScalarText("SELECT percent::text", world).ShouldBe("30", "another task's progress is never applied");
        harness.Notifier.Events.ShouldBeEmpty();
        render.CancelCalls.ShouldBe(1);
        render.DeleteCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Metrics_TerminalTimeout_ObservesDurationAndCountsRetryOnTheSingleMeter()
    {
        var world = await SeedJobAsync("running", "subtitles", percent: 50, submittedMinutesAgo: 61, startedMinutesAgo: 60);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);
        var host = harness.Host.Services.GetRequiredService<NightTalesMetrics>();

        // The listener is attached to the host's own meter instance: a second Meter of the same name would
        // export every render series twice, so a measurement that never arrives here is the defect (AC8).
        var durations = new List<(string Name, string Stage, string Outcome, double Value, string Meter)>();
        var counts = new List<(string Name, string Code, long Value, string Meter)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, self) =>
            {
                if (ReferenceEquals(instrument.Meter, host.Meter))
                {
                    self.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => durations.Add((
            instrument.Name,
            TagOf(tags, NightTalesMetrics.StageTag),
            TagOf(tags, NightTalesMetrics.OutcomeTag),
            value,
            instrument.Meter.Name)));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => counts.Add((
            instrument.Name,
            TagOf(tags, RenderMetrics.CodeTag),
            value,
            instrument.Meter.Name)));
        listener.Start();

        // The task is still running at the deadline, so the watchdog ends the attempt (F5).
        render.EnqueueStatus(Status(world, RenderStates.Running, "subtitles", 50));
        render.EnqueueCancel(default);
        render.EnqueueDelete(default);

        await PollAsync(harness.Host, world.RenderJobId);

        var duration = durations.ShouldHaveSingleItem();
        duration.Name.ShouldBe(NightTalesMetrics.RenderDurationName);
        duration.Stage.ShouldBe("subtitles", "the bounded stage label, never the adapter's own text");
        duration.Outcome.ShouldBe(RenderMetrics.OutcomeTimeout);
        duration.Value.ShouldBe(3600d, "the attempt ran from its started_at to the watchdog");
        duration.Meter.ShouldBe(NightTalesMetrics.MeterName, "the instrument lives on the host's single meter");

        // render_timeout is transient, so this poll also queued attempt 2 and the retry counter moved once.
        var retry = counts.ShouldHaveSingleItem();
        retry.Name.ShouldBe(RenderMetrics.RetriesInstrumentName);
        retry.Code.ShouldBe("render_timeout");
        retry.Value.ShouldBe(1L, "one retry row was created, so the counter moved exactly once");
        retry.Meter.ShouldBe(NightTalesMetrics.MeterName);
    }

    [Fact]
    public async Task Terminal_Failed_AttemptTwo_TransientCode_IsFinal()
    {
        // The same code and the same adapter answer as the timeout test, on the last attempt: the budget is
        // spent, so the verdict is final and no third row may appear (AC7, AC10).
        var world = await SeedJobAsync(
            "running",
            "assembling",
            percent: 70,
            submittedMinutesAgo: 61,
            startedMinutesAgo: 60,
            attempt: 2);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        render.EnqueueStatus(Status(world, RenderStates.Running, "assembling", 70));
        render.EnqueueCancel(default);
        render.EnqueueDelete(default);

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "failed", stage: "assembling", percent: 70, errorCode: "render_timeout");
        harness.Money.Refunds.ShouldBe([(world.ReservationId, "render_failed")]);

        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1L, "attempt 2 is the last one: a transient code does not queue a third attempt");
        SqlProbe.QueryStrings(
                Database,
                "SELECT (data->>'final')::text || '|' || (data ? 'retryJobId')::text FROM audit_logs "
                + $"WHERE entity_id = '{world.RenderJobId}'")
            .ShouldBe(["true|false"], "the audit row carries the final verdict of a spent budget");
        CountOutbox(world).ShouldBe(1);
        EpisodeStatus(world).ShouldBe("story_approved", "with no retry the episode comes back to the creator");
    }

    [Fact]
    public async Task Terminal_Failed_RetryRefusedByTheBudgetGuard_IsFinal()
    {
        var world = await SeedJobAsync("running", "subtitles", percent: 50, submittedMinutesAgo: 61, startedMinutesAgo: 60);
        var render = new FakeRenderClient();
        var money = new RecordingMoney
        {
            Budget = Error.Failure("budget_spend_cap", "The platform spend cap is reached."),
        };
        await using var harness = await StartHostAsync(render, money: money);

        render.EnqueueStatus(Status(world, RenderStates.Running, "subtitles", 50));
        render.EnqueueCancel(default);
        render.EnqueueDelete(default);

        await PollAsync(harness.Host, world.RenderJobId);

        // The retry re-checks the cap even for a code the table retries, and the failed attempt's own bound is
        // excluded from that check (the row is already in the transaction, 03 D8/CR-4).
        money.ExcludedRunIds.ShouldBe([world.RenderJobId]);
        AssertRow(world, status: "failed", stage: "subtitles", percent: 50, errorCode: "render_timeout");
        money.Refunds.ShouldBe(
            [(world.ReservationId, "render_failed")],
            "a refused retry is not an error of the failure: the attempt is still refunded once");

        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1L, "a refused retry creates no row");
        SqlProbe.QueryStrings(
                Database,
                "SELECT (data->>'final')::text || '|' || (data ? 'retryJobId')::text FROM audit_logs "
                + $"WHERE entity_id = '{world.RenderJobId}'")
            .ShouldBe(["true|false"], "the refused retry makes the verdict final (WT50)");
        CountOutbox(world).ShouldBe(1);
        EpisodeStatus(world).ShouldBe("story_approved");
    }

    [Fact]
    public async Task Progress_ConcurrentCancelWins_AndTheNextTickSettlesIt()
    {
        var world = await SeedJobAsync("running", "voice", percent: 20);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        // A cancel that commits while the status call is in flight: the poll path takes no row lock (BD7), so
        // the xmin concurrency token is what refuses the progress write instead of overwriting the cancel. The
        // outer update fires once: a second one would bump the row again and refuse the settling tick too.
        var cancelled = false;
        render.OnStatus = (_, _) =>
        {
            if (!cancelled)
            {
                cancelled = true;

                SqlProbe.Execute(
                    Database,
                    $"UPDATE render_jobs SET cancel_requested_at = '{Start:O}'::timestamptz "
                    + $"WHERE id = '{world.RenderJobId}'");
            }

            return ValueTask.CompletedTask;
        };
        render.EnqueueStatus(Status(world, RenderStates.Running, "images", 40));

        var refused = await Should.ThrowAsync<InvalidOperationException>(
            () => PollAsync(harness.Host, world.RenderJobId));

        refused.Message.ShouldContain("concurrency_conflict", Case.Sensitive);
        AssertRow(world, status: "running", stage: "voice", percent: 20);
        ScalarText("SELECT (cancel_requested_at IS NOT NULL)::text", world)
            .ShouldBe("true", "the cancel survives the refused progress write");
        harness.Money.Refunds.ShouldBeEmpty();

        // The next tick re-polls the row the poller still owns and settles it: the refund happens once, here.
        render.EnqueueStatus(Status(world, RenderStates.Cancelled, "voice", 20));

        await PollAsync(harness.Host, world.RenderJobId);

        AssertRow(world, status: "cancelled", stage: "voice", percent: 20);
        harness.Money.Refunds.ShouldBe([(world.ReservationId, "render_cancelled")]);
        CountAudit(world).ShouldBe(1);
        EpisodeStatus(world).ShouldBe("story_approved");
    }

    [Fact]
    public async Task Terminal_Cancelled_TwoConcurrentTicks_CommitOnceAndRefundByReservation()
    {
        var world = await SeedJobAsync("running", "voice", percent: 30);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        // Both ticks read the row before either settles it: the barrier holds each one inside the status call
        // until the other arrived, so both see the terminal cancelled answer (the poll path takes no row lock).
        var arrival = new Arrival(2);
        render.OnStatus = (_, _) => new ValueTask(arrival.SignalAsync());
        render.EnqueueStatus(Status(world, RenderStates.Cancelled, "voice", 30));

        var outcomes = await Task.WhenAll(
            CaptureAsync(PollAsync(harness.Host, world.RenderJobId)),
            CaptureAsync(PollAsync(harness.Host, world.RenderJobId)));

        render.StatusCalls.ShouldBe(2, "both ticks really reached the adapter with the same row");
        outcomes.Count(outcome => outcome is null).ShouldBe(1, "exactly one tick commits the settlement");
        var refused = outcomes.Single(outcome => outcome is not null);
        refused.ShouldBeOfType<InvalidOperationException>();
        refused!.Message.ShouldContain("concurrency_conflict", Case.Sensitive);

        // The durable effect is one cancelled row and one audit row, and every refund call names the same
        // reservation — which is what makes the ledger's per-reservation refund exactly-once (WT50/WT51).
        AssertRow(world, status: "cancelled", stage: "voice", percent: 30);
        CountAudit(world).ShouldBe(1);
        harness.Money.Refunds.ShouldNotBeEmpty();
        harness.Money.Refunds.Select(refund => refund.ReservationId).Distinct().ShouldBe([world.ReservationId]);
        EpisodeStatus(world).ShouldBe("story_approved");
    }

    [Fact]
    public async Task Progress_EventPayload_IsStageAndPercentOnly_InsideTheWireCap()
    {
        var world = await SeedJobAsync("running", "voice", percent: 20);
        var render = new FakeRenderClient();
        await using var harness = await StartHostAsync(render);

        render.EnqueueStatus(Status(world, RenderStates.Running, "images", 40));

        await PollAsync(harness.Host, world.RenderJobId);

        var pushed = harness.Notifier.Events.ShouldHaveSingleItem();

        // The real wire body, not a re-serialization of the record: this is what pg_notify carries (AC12).
        var payload = RealtimePayload.Build(pushed.UserId, pushed.Event);
        Encoding.UTF8.GetByteCount(payload).ShouldBeLessThan(
            RealtimePayload.MaxBytes,
            "the NOTIFY body stays inside the 8 KB cap (WT54)");

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        root.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["userId", "event", "ids", "data"], ignoreOrder: true);
        root.GetProperty("event").GetString().ShouldBe("render_progress");

        var data = root.GetProperty("data");
        data.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["stage", "percent"], "the payload carries the stage and the percent and no other member");
        data.GetProperty("stage").GetString().ShouldBe("images");
        data.GetProperty("percent").GetInt16().ShouldBe((short)40);
    }

    [Fact]
    public async Task Poll_NeverLogsTheNodeBaseUrlOrItsKey()
    {
        var world = await SeedJobAsync("running", "voice", percent: 20);
        var render = new FakeRenderClient();
        var sink = new CapturingLoggerProvider();
        await using var harness = await StartHostAsync(render, sink: sink);

        // The row's node carries a URL and a key that must never reach a log line (11 D13, 09.6.3 notes).
        const string Node = "render-node-secret-4f2a.example";
        SqlProbe.Execute(
            Database,
            "UPDATE worker_nodes SET base_url = 'https://" + Node + ":8443/', api_key_hint = 'canary-hint-aaaa', "
            + "api_key_ciphertext = '\\x00ff'::bytea WHERE id = '" + world.NodeId + "'");

        // The alerting deferred answer and then a terminal failure: the two paths that log the most.
        render.EnqueueFailure(
            RenderClientMethod.GetStatus,
            new RenderClientFailure(
                RenderClientFailureKind.Deferred,
                "adapter_capacity_full",
                "scripted",
                Alert: true,
                RetryAfter: TimeSpan.FromSeconds(30)));

        await PollAsync(harness.Host, world.RenderJobId);

        render.EnqueueStatus(Status(world, RenderStates.Failed, "voice", 20));

        await PollAsync(harness.Host, world.RenderJobId);

        var messages = sink.Entries.Select(entry => entry.Message).ToList();
        sink.Entries.ShouldContain(entry => entry.EventId == 6228, "the adapter alert is one of the lines under test");
        sink.Entries.ShouldContain(entry => entry.EventId == 6211, "and so is the terminal failure record");
        messages.ShouldAllBe(message => !message.Contains(Node, StringComparison.Ordinal));
        messages.ShouldAllBe(message => !message.Contains("canary-hint-aaaa", StringComparison.Ordinal));
        messages.ShouldAllBe(message => !message.Contains("8443", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains(world.RenderJobId.ToString("D"), StringComparison.Ordinal));
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

    /// <summary>The outcome of one poll that is expected to throw, or <c>null</c> when it completed.</summary>
    private static async Task<Exception?> CaptureAsync(Task poll)
    {
        try
        {
            await poll;

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Holds <paramref name="expected"/> concurrent polls inside one hook until all of them arrived, so a test
    /// can make two ticks read the same row before either settles it. A poll that never arrives fails loudly
    /// instead of hanging (<see cref="Task.WaitAsync(TimeSpan)"/>).
    /// </summary>
    private sealed class Arrival(int expected)
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task SignalAsync()
        {
            lock (_gate)
            {
                _arrived++;
                if (_arrived >= expected)
                {
                    _release.TrySetResult();
                }
            }

            return _release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
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
    /// <param name="render">The scripted node client.</param>
    /// <param name="hooks">The 03.4.2-T1 extension point; the default classifies as <c>render_node_error</c>.</param>
    /// <param name="money">The money ports; a test that scripts a budget or a refund verdict passes its own.</param>
    /// <param name="sink">A log sink, for the tests that assert what a job never logs.</param>
    private async Task<Harness> StartHostAsync(
        FakeRenderClient render,
        RecordingHooks? hooks = null,
        RecordingMoney? money = null,
        CapturingLoggerProvider? sink = null)
    {
        var harness = new Harness(render, hooks ?? new RecordingHooks(), money ?? new RecordingMoney());

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

                if (sink is not null)
                {
                    services.AddLogging(logging => logging.AddProvider(sink));
                }
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
        int startedMinutesAgo = 0,
        int attempt = 1)
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
            + $"VALUES ('{nodeId}', 'poll-node-{nodeId:N}', 'https://127.0.0.1:8443', 2, 'active', now(), "
            + "'RTX 4090', '0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF', now(), now())",
            Ct);

        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, worker_node_id, adapter_task_id, submitted_at, started_at, next_poll_at, "
            + "cancel_requested_at) "
            + $"VALUES ('{jobId}', '{user.Id}', '{episode.Id}', '{version.Id}', '{reservationId}', '{status}', "
            + $"'{stage}', {percent}, '{ParamsJson(version.Id)}'::jsonb, {attempt}, '{nodeId}', '{taskId}', "
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
    private sealed class Harness(FakeRenderClient render, RecordingHooks hooks, RecordingMoney money)
    {
        public WorkerHost Host { get; set; } = null!;

        public FakeRenderClient Render { get; } = render;

        public RecordingHooks Hooks { get; } = hooks;

        public RecordingNotifier Notifier { get; } = new();

        public RecordingIngestEnqueuer Ingest { get; } = new();

        public RecordingMoney Money { get; } = money;

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

        // 09.6.3-T2 added the two success-path members to the same seam. The poller never calls them (they belong
        // to the ingest's final transaction), so recording them changes no existing order assertion.
        public ValueTask<System.Text.Json.Nodes.JsonNode?> MergeImagesAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct)
        {
            Calls.Add("images");

            return ValueTask.FromResult<System.Text.Json.Nodes.JsonNode?>(null);
        }

        public ValueTask RecordKeptVideoAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct)
        {
            Calls.Add("kept");

            return ValueTask.CompletedTask;
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

        /// <summary>The <c>excludingRunId</c> of every budget check, in order.</summary>
        public List<Guid?> ExcludedRunIds { get; } = [];

        public ErrorOr<Success> Budget { get; init; } = Result.Success;

        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromResult<ErrorOr<PriceVersion>>(
                PriceVersion.Create(1, Start.AddDays(-1), "{}", "{}", "hash", Start.AddDays(-1)));

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => throw new NotSupportedException();

        public Task<CostBound> WorstCaseAsync(Aspect aspect, int maxTerms, int priceVersion, CancellationToken ct) =>
            Task.FromResult(new CostBound(1.23m));

        public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null)
        {
            ExcludedRunIds.Add(excludingRunId);

            return ValueTask.FromResult(Budget);
        }

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

    /// <summary>One line as the sink saw it: the structured message is what a redaction test greps.</summary>
    /// <param name="EventId">The id the <c>LoggerMessage</c> declared.</param>
    /// <param name="Level">The level the line was written at.</param>
    /// <param name="Message">The rendered message.</param>
    private sealed record LogEntry(int EventId, LogLevel Level, string Message);

    /// <summary>
    /// A log provider that keeps every line of every category: the only way to assert what a job never writes
    /// (the node's base URL and its key), since the harness replaces real ports rather than the logger.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose()
        {
        }

        private sealed class Sink(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries)
                {
                    owner.Entries.Add(new LogEntry(eventId.Id, logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
