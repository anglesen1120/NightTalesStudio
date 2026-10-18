using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.Application.Features.Rendering.Outbox;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.RenderNode.Contract;
using NightTales.TestKit;
using NightTales.TestKit.Api;
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
/// Independent QA of 09.6.4-T1 (<c>DELETE /api/v1/render-jobs/{id}</c>, 09 README BD8, WT50/WT51/WT55): the
/// exploratory, negative and boundary cases the feature's own tests do not carry. It asserts the invariants an
/// operator cares about rather than a restatement of the feature tests: the ledger's refusal must not leave a
/// terminal row with an open reservation, a malformed or absent id must never be a 5xx, two racing DELETEs must
/// leave exactly one stamp and one outbox row, the queued <c>SubmitRenderJob</c> a cancel beats must really
/// become a no-op, a redelivered <c>render_cancel_requested.v1</c> must be harmless, and a cancel-requested row
/// the node never answers must still end as a final failure without a retry row.
/// </summary>
/// <remarks>
/// <para>
/// Worktrees are not indexed by codebase-memory: this file was written from <c>read</c>/<c>grep</c> over
/// absolute paths, and it mirrors <see cref="CancelRenderEndpointTests"/> (the Api host + the real render
/// producer), <see cref="CancelRenderRequestedHandlerTests"/> (the Worker host + the outbox delivery) and
/// <see cref="CancelRenderPollTests"/> (the poller driven directly). Hand-written fakes only, no mocking
/// library.
/// </para>
/// <para>
/// Class-database hygiene: every test that leaves a pending <c>render_cancel_requested.v1</c> row consumes it
/// with its own dispatcher run before it ends, so no later test can observe a stray cancel delivery on its own
/// scripted node client. The one row a test leaves behind — the <c>render_failed.v1</c> the poller writes, which
/// has no consumer yet — cannot reach a render client.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CancelRenderEdgeCaseTests(TestDatabase database) : IntegrationTest(database)
{
    private const string RenderRequested = "render_requested.v1";
    private const string RenderCancelRequested = "render_cancel_requested.v1";

    /// <summary>The command's own audit action (the queued path never uses the recorder's row).</summary>
    private const string CancelAction = "render.cancelled";

    /// <summary>The 64-hex fingerprint the <c>worker_nodes</c> row needs (never a secret, D11).</summary>
    private const string TlsFingerprint =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Delete_Queued_ThePendingSubmitDeliveryBecomesANoOp()
    {
        // AC1's last clause: "the queued SubmitRenderJob becomes a no-op". The feature's own endpoint test only
        // asserts that no second outbox row is written; here the still-pending render_requested.v1 delivery is
        // really run through the production handler and the production job.
        var world = await SeedQueuedWorldAsync();
        var ledger = new ApiLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);
        using var rendered = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "qa-queued-noop");
        rendered.StatusCode.ShouldBe(HttpStatusCode.Accepted, await rendered.Content.ReadAsStringAsync(Ct));
        var jobId = (await BodyAsync(rendered)).GetProperty("renderJobId").GetGuid();

        using (var response = await DeleteAsync(client, jobId))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        }

        JobRow(jobId).ShouldBe("cancelled|null|-", "terminal at once, no cancel stamp, no outcome code");
        EpisodeStatusOf(world.EpisodeId).ShouldBe("story_approved");

        // The row the command committed is the delivery a Worker would still pick up.
        var messageId = Guid.Parse(
            Scalar($"SELECT id::text FROM outbox_messages WHERE type = '{RenderRequested}' "
                + $"AND payload->>'renderJobId' = '{jobId}'"),
            CultureInfo.InvariantCulture);
        var payload = Scalar($"SELECT payload::text FROM outbox_messages WHERE id = '{messageId}'");
        var requested = JsonSerializer.Deserialize<RenderRequested>(payload, OutboxJson.Options)!;
        requested.RenderJobId.ShouldBe(jobId, "the stored payload names the row the cancel ended");

        // The production handler + the production job, exactly as the dispatcher and Hangfire would run them.
        var render = new FakeRenderClient();
        await using var rig = await StartWorkerAsync(render);
        using (var scope = rig.Host.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<RenderRequestedHandler>()
                .HandleAsync(requested, new OutboxContext(messageId, world.UserId, 1, Start), Ct);
        }

        (await rig.Host.Jobs.RunPendingAsync(Ct)).ShouldBe(1, "the delivery really enqueued and ran SubmitRenderJob");
        rig.Host.Jobs.Enqueued.ShouldHaveSingleItem().JobType.ShouldBe(nameof(SubmitRenderJob));

        render.SubmitCalls.ShouldBe(0, "a cancelled row never reaches a node");
        render.Calls.ShouldBeEmpty();
        JobRow(jobId).ShouldBe("cancelled|null|-", "the no-op submit wrote nothing");
        OutboxTotal(jobId).ShouldBe(1, "no outbox row beyond the original render_requested.v1");
    }

    [Fact]
    public async Task Delete_Queued_WhenTheRefundIsRefusedOrThrows_RollsBackAndChangesNothing()
    {
        // The handler deliberately returns the ledger's error instead of swallowing it (unlike the Worker's
        // terminal paths, SEC-3 in STATUS.md), so this is the one path where a refused refund rolls the cancel
        // back. Neither the feature's endpoint tests nor its unit tests exercise it (the unit tests say so
        // explicitly in their remarks), so what is persisted here is an assumption until it is asserted.
        //
        // The status is deliberately not pinned: 12.1.2 will catalogue what a refused-ledger answer maps to, so
        // this asserts only the invariants an operator cares about — never a 2xx, the row unchanged and still
        // reserved, no refund committed, no outbox row, the episode still held — never the code. SEC-3 tracks
        // the refusal posture.
        var refused = await SeedJobAsync("queued");
        var refusingLedger = new ApiLedger(RefundMode.Refused);
        using (var factory = Factory(refusingLedger))
        using (var client = factory.ClientFor(refused.UserId))
        {
            using var response = await DeleteAsync(client, refused.RenderJobId);
            var text = await response.Content.ReadAsStringAsync(Ct);
            var status = (int)response.StatusCode;

            (status is >= 200 and < 300).ShouldBeFalse($"{status} {text} — a refused refund is never a success");
            refusingLedger.RefundAttempts.ShouldHaveSingleItem("the handler reached the ledger once before rolling back");
            refusingLedger.CommittedRefunds.ShouldBeEmpty("a refused refund is never committed");
            AssertUntouched(refused, "a refused refund must leave the row queued and reserved");
        }

        var throwing = await SeedJobAsync("queued");
        var throwingLedger = new ApiLedger(RefundMode.Throws);
        using (var factory = Factory(throwingLedger))
        using (var client = factory.ClientFor(throwing.UserId))
        {
            using var response = await DeleteAsync(client, throwing.RenderJobId);
            var text = await response.Content.ReadAsStringAsync(Ct);
            var status = (int)response.StatusCode;

            (status is >= 200 and < 300).ShouldBeFalse($"{status} {text} — a thrown refund is never a success");
            throwingLedger.RefundAttempts.ShouldHaveSingleItem("the handler reached the ledger once before the throw");
            throwingLedger.CommittedRefunds.ShouldBeEmpty("a thrown refund is never committed");
            AssertUntouched(throwing, "a thrown refund must leave the row queued and reserved");
        }

        // Both fakes recorded the one attempt each: the rollback is the whole command's, not the ledger's.
        AssertUntouched(refused, "still queued after the refusal");
        AssertUntouched(throwing, "still queued after the throw");

        // Positive control for the two "no committed refund" assertions above: the same fake records a commit the
        // moment a refund is really applied (the success mode the other tests of this class use).
        var committingLedger = new ApiLedger();
        await committingLedger.RefundAsync(new ReservationId(refusingLedger.Reservation), "qa-probe", Ct);
        committingLedger.CommittedRefunds.ShouldHaveSingleItem().Reason.ShouldBe("qa-probe");
    }

    [Fact]
    public async Task Delete_TwoConcurrentRequests_OneStampOneOutboxRowOneAuditRow()
    {
        // The failure table's "double cancel across two API instances": one transaction must win and the other
        // must either see the stamp (204) or be refused by the bounded lock wait (409) — never a 500, never two
        // stamps, never two outbox rows.
        var job = await SeedJobAsync("submitted");
        var ledger = new ApiLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(job.UserId);

        var first = DeleteAsync(client, job.RenderJobId);
        var second = DeleteAsync(client, job.RenderJobId);
        var responses = await Task.WhenAll(first, second);

        try
        {
            var codes = responses.Select(response => (int)response.StatusCode).Order().ToArray();
            codes.All(code => code is 204 or 409).ShouldBeTrue($"a contended cancel is 204 or 409, never a 5xx: {string.Join(',', codes)}");
            codes.ShouldContain(204, "at least one request must apply the cancel");

            CancelRequestedAt(job.RenderJobId).ShouldBe(Epoch(Start), "one stamp, written by the winner");
            OutboxCount(job.RenderJobId, RenderCancelRequested).ShouldBe(1, "exactly one adapter-cancel hand-off");
            OutboxTotal(job.RenderJobId).ShouldBe(1);
            AuditCount(job.RenderJobId).ShouldBe(1, "the losing 204/repeat and the 409 write no second audit row");
            ledger.RefundAttempts.ShouldBeEmpty("a submitted cancel never refunds in the request path");
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await ConsumeCancelDeliveryAsync(job);
    }

    [Fact]
    public async Task Delete_ForeignRunningJob_IsIndistinguishableFromAnUnknownId_AndTheOwnerCanStillCancel()
    {
        // D10 on a running row: another user's job answers the same not_found body as a random guid, the tenant
        // lock touches nothing, and the owner's own DELETE still works afterwards.
        var owner = await SeedJobAsync("running");
        var stranger = await SeedJobAsync("submitted");
        var ledger = new ApiLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(stranger.UserId);

        var xmin = Xmin(owner.RenderJobId);

        string foreignBody;
        using (var foreign = await DeleteAsync(client, owner.RenderJobId))
        {
            foreignBody = await foreign.Content.ReadAsStringAsync(Ct);
            foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound, foreignBody);
        }

        string absentBody;
        using (var absent = await DeleteAsync(client, Guid.CreateVersion7()))
        {
            absentBody = await absent.Content.ReadAsStringAsync(Ct);
            absent.StatusCode.ShouldBe(HttpStatusCode.NotFound, absentBody);
        }

        // Another user's running job and an unknown id answer the same not_found shape (D10): the same body keys,
        // the same code, never 403 — the envelope's own traceId is the only thing that may differ.
        JsonSerializer.Deserialize<ProblemResponse>(foreignBody, ProblemResponse.Options)!.Code.ShouldBe("not_found");
        JsonSerializer.Deserialize<ProblemResponse>(absentBody, ProblemResponse.Options)!.Code.ShouldBe("not_found");
        Keys(JsonNode.Parse(foreignBody)!.AsObject())
            .ShouldBe(Keys(JsonNode.Parse(absentBody)!.AsObject()));

        JobRow(owner.RenderJobId).ShouldBe("running|null|-", "the foreign row is untouched");
        Xmin(owner.RenderJobId).ShouldBe(xmin, "and not even written");
        OutboxTotal(owner.RenderJobId).ShouldBe(0);
        ledger.RefundAttempts.ShouldBeEmpty();

        using (var mine = factory.ClientFor(owner.UserId))
        using (var response = await DeleteAsync(mine, owner.RenderJobId))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        }

        CancelRequestedAt(owner.RenderJobId).ShouldBe(Epoch(Start), "the owner's own cancel still lands");
        OutboxCount(owner.RenderJobId, RenderCancelRequested).ShouldBe(1);

        await ConsumeCancelDeliveryAsync(owner);
    }

    [Fact]
    public async Task AdapterCancel_DeliveredTwice_IsHarmless()
    {
        // D11 delivery is at least once: two copies of the same render_cancel_requested.v1 must each ask the node
        // (the adapter call is idempotent, 05 D4) and must still write nothing and refund nothing.
        var world = await SeedWorldAsync();
        var job = await SeedJobAsync(world, status: "running", cancelRequested: true);
        var first = await SeedCancelMessageAsync(world, job.RenderJobId);
        var second = await SeedCancelMessageAsync(world, job.RenderJobId);

        var render = new FakeRenderClient().EnqueueCancel(Result.Success);
        await using var rig = await StartWorkerAsync(render);
        var xmin = Xmin(job.RenderJobId);

        await DeliverAsync(rig.Host, [first, second]);

        // One row, one handler, one adapter call: the second copy is the same idempotent ask (05 D4) and neither
        // copy writes the row or refunds anything.
        CallsFor(render, RenderClientMethod.Cancel, job.TaskId)
            .ShouldBe(2, "each of the two rows reaches the node exactly once");
        CallsFor(render, RenderClientMethod.Delete, job.TaskId).ShouldBe(0, "a cancel delivery never deletes the task (05 D4)");
        JobRow(job.RenderJobId).ShouldBe("running|" + Epoch(Start) + "|-", "no write of any kind");
        Xmin(job.RenderJobId).ShouldBe(xmin);
        rig.Money.Refunds.ShouldBeEmpty("only the poller's terminal cancelled refunds (WT51)");
        OutboxCount(job.RenderJobId, RenderCancelRequested).ShouldBe(2);
    }

    [Fact]
    public async Task Poll_CancelRequestedAdapterDeferredPastTheLimit_FailsAsTimeout_NeverRetries()
    {
        // Failure table row 1 read end to end: a row whose owner asked to cancel and whose node never answers
        // (the adapter cancel is Deferred, the confirmation never arrives) still ends at the 60-minute watchdog —
        // final, refunded once, and never retried into a second charge.
        var world = await SeedWorldAsync();
        var job = await SeedJobAsync(
            world,
            status: "running",
            cancelRequested: true,
            submittedMinutesAgo: 70,
            startedMinutesAgo: 69);

        var render = new FakeRenderClient()
            .EnqueueFailure(
                RenderClientMethod.GetStatus,
                new RenderClientFailure(
                    RenderClientFailureKind.Deferred,
                    RenderOutcomeCodes.RenderNodeError,
                    "scripted",
                    Alert: false,
                    RetryAfter: TimeSpan.FromSeconds(5)))
            .EnqueueCancel(Result.Success)
            .EnqueueDelete(Result.Success);

        await using var rig = await StartWorkerAsync(render);

        await PollAsync(rig.Host, job.RenderJobId);

        JobRow(job.RenderJobId).ShouldBe(
            "failed|" + Epoch(Start) + "|render_timeout",
            "past Render:MaxMinutes the watchdog owns the row: errored, with the owner's cancel stamp left as it was");
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1L, "a cancel-requested row is never retried (WT51/SEC-4)");
        rig.Money.Refunds.ShouldBe(
            [(job.ReservationId, "render_failed")],
            "a cancelled-by-request timeout is still refunded once, by reservation id");
        EpisodeStatusOf(world.EpisodeId).ShouldBe("story_approved");
        SqlProbe.QueryStrings(
                Database,
                "SELECT action || '|' || actor_type || '|' || (data->>'final')::text FROM audit_logs "
                + $"WHERE entity_id = '{job.RenderJobId}'")
            .ShouldBe(["render.failed|system|true"], "the verdict is final, so no retry row and no retry event");
        CallsFor(render, RenderClientMethod.Cancel, job.TaskId)
            .ShouldBe(1, "the timeout path asks the node to stop the task (SEC-8)");
        CallsFor(render, RenderClientMethod.Delete, job.TaskId).ShouldBe(1, "and to release its material");
    }

    [Fact]
    public async Task Lifecycle_DeleteThenWorkerCancelThenTerminalPoll_ThenRepeatDelete_OneRefund()
    {
        // AC2 + AC3 + AC5 + AC6 + AC10 in one real run, across both hosts: the request only stamps, the Worker's
        // delivery asks the node, the poller's terminal refunds exactly once, and the repeat DELETE adds nothing.
        var job = await SeedJobAsync("submitted");
        var money = new MoneyRecorder();

        using var factory = new ApiFactory(Database, new FakeClock(Start))
            .WithServices(services =>
            {
                services.RemoveAll<ICreditLedger>();
                services.AddSingleton<ICreditLedger>(money);
            });
        using var client = factory.ClientFor(job.UserId);

        using (var response = await DeleteAsync(client, job.RenderJobId))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        }

        CancelRequestedAt(job.RenderJobId).ShouldBe(Epoch(Start));
        OutboxCount(job.RenderJobId, RenderCancelRequested).ShouldBe(1);
        AuditCount(job.RenderJobId).ShouldBe(1, "the request writes its own audit row (BD15)");
        money.Refunds.ShouldBeEmpty("the request path refunds nothing (WT51)");

        var render = new FakeRenderClient().EnqueueCancel(Result.Success);
        await using var rig = await StartWorkerAsync(render, money);

        await DeliverAsync(rig.Host, [await CancelMessageIdAsync(job.RenderJobId)]);

        CallsFor(render, RenderClientMethod.Cancel, job.TaskId)
            .ShouldBe(1, "exactly one adapter cancel, from the Worker, with the row's own task id");
        var mine = render.Calls
            .Where(call => string.Equals(call.TaskId, job.TaskId, StringComparison.Ordinal))
            .ToList();
        mine.ShouldHaveSingleItem().Node.Id.ShouldBe(job.NodeId);
        mine[0].Method.ShouldBe(RenderClientMethod.Cancel);
        JobRow(job.RenderJobId).ShouldBe("submitted|" + Epoch(Start) + "|-", "the delivery writes nothing");
        money.Refunds.ShouldBeEmpty();

        // The poller's terminal cancelled is the one instant the money moves.
        render.EnqueueStatus(Status(job, RenderStates.Cancelled, "voice", 30));
        await PollAsync(rig.Host, job.RenderJobId);

        JobRow(job.RenderJobId).ShouldBe("cancelled|" + Epoch(Start) + "|-");
        EpisodeStatusOf(job.EpisodeId).ShouldBe("story_approved");
        CallsFor(render, RenderClientMethod.Cancel, job.TaskId)
            .ShouldBe(1, "the poll's terminal cancelled never cancels the adapter a second time");
        money.Refunds.ShouldBe([(job.ReservationId, "render_cancelled")], "once, at the terminal, by reservation id");
        AuditCount(job.RenderJobId).ShouldBe(2, "the poller's terminal row is the second render.cancelled");

        var terminalXmin = Xmin(job.RenderJobId);
        using (var repeat = await DeleteAsync(client, job.RenderJobId))
        {
            repeat.StatusCode.ShouldBe(HttpStatusCode.NoContent, await repeat.Content.ReadAsStringAsync(Ct));
        }

        money.Refunds.Count.ShouldBe(1, "a DELETE after the terminal refunds nothing again");
        AuditCount(job.RenderJobId).ShouldBe(2, "and writes no third audit row");
        Xmin(job.RenderJobId).ShouldBe(terminalXmin, "and does not even touch the row");
        OutboxTotal(job.RenderJobId).ShouldBe(1);
    }

    [Fact]
    public async Task Delete_MalformedIdAndIgnoredHeaders_NeverServerError()
    {
        // Boundary/exploratory: the route constraint, and the headers this route deliberately ignores
        // ([DECISION] 09.6.4 plan §Notes: no If-Match, no Idempotency-Key).
        var job = await SeedJobAsync("submitted");
        var ledger = new ApiLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(job.UserId);

        using (var malformed = await client.DeleteAsync("/api/v1/render-jobs/not-a-guid", Ct))
        {
            var text = await malformed.Content.ReadAsStringAsync(Ct);
            ((int)malformed.StatusCode).ShouldBe(404, text);
            malformed.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError);
            JobRow(job.RenderJobId).ShouldBe("submitted|null|-", "an unroutable id changes nothing");
        }

        using (var blank = await client.DeleteAsync("/api/v1/render-jobs/", Ct))
        {
            ((int)blank.StatusCode).ShouldBe(404, await blank.Content.ReadAsStringAsync(Ct));
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/render-jobs/{job.RenderJobId}")
        {
            Content = new StringContent("""{"unexpected":true}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"stale-etag\"");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "qa-ignored");

        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty("a 204 cancel carries no content");

        CancelRequestedAt(job.RenderJobId).ShouldBe(Epoch(Start), "the ignored headers did not block the cancel");
        OutboxCount(job.RenderJobId, RenderCancelRequested).ShouldBe(1);

        await ConsumeCancelDeliveryAsync(job);
    }

    [Fact]
    public async Task Poll_CancelRequestedAdapterCancelled_RefundsOnceAndIsIdempotentOnRepeat()
    {
        // The terminal cancelled of a cancel-requested row: one refund, one audit row, and the second poll of the
        // now-terminal row writes nothing and refunds nothing (WT45).
        var world = await SeedWorldAsync();
        var job = await SeedJobAsync(world, status: "running", cancelRequested: true, startedMinutesAgo: 30);
        var render = new FakeRenderClient();
        await using var rig = await StartWorkerAsync(render);

        render.EnqueueStatus(Status(job, RenderStates.Cancelled, "voice", 30));
        await PollAsync(rig.Host, job.RenderJobId);

        JobRow(job.RenderJobId).ShouldBe("cancelled|" + Epoch(Start) + "|-");
        rig.Money.Refunds.ShouldBe([(job.ReservationId, "render_cancelled")]);
        SqlProbe.Count(Database, $"SELECT count(*) FROM assets WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0L, "a cancelled task's outputs are never downloaded or stored");

        await PollAsync(rig.Host, job.RenderJobId);

        rig.Money.Refunds.Count.ShouldBe(1, "a second poll of a terminal row never refunds again");
        render.StatusCalls.ShouldBe(1, "a terminal row is not polled again (WT45)");
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE entity_id = '{job.RenderJobId}'")
            .ShouldBe(1L);
    }

    /// <summary>Asserts a rolled-back cancel left no trace at all on its row, its episode and the audit trail.</summary>
    /// <param name="job">The queued attempt the refused cancel targeted.</param>
    /// <param name="because">The sentence a failure shows.</param>
    private void AssertUntouched(Job job, string because)
    {
        JobRow(job.RenderJobId).ShouldBe("queued|null|-", because);
        SqlProbe.ScalarText(Database, $"SELECT (finished_at IS NULL)::text FROM render_jobs WHERE id = '{job.RenderJobId}'")
            .ShouldBe("true", because);
        EpisodeStatusOf(job.EpisodeId).ShouldBe("rendering", $"{because} — the episode still held by the attempt");
        AuditCount(job.RenderJobId).ShouldBe(0, $"{because} — a rolled-back command writes no audit row");
        OutboxTotal(job.RenderJobId).ShouldBe(0, $"{because} — and no outbox row");
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE id = '{job.RenderJobId}'")
            .ShouldBe(1L, because);
    }

    /// <summary>
    /// Consumes the <c>render_cancel_requested.v1</c> row a test leaves pending, so no later test of this class
    /// can observe a stray adapter call on its own scripted client.
    /// </summary>
    /// <param name="job">The job whose cancel delivery must be drained.</param>
    private async Task ConsumeCancelDeliveryAsync(Job job)
    {
        var render = new FakeRenderClient().EnqueueCancel(Result.Success);
        await using var rig = await StartWorkerAsync(render);

        await DeliverAsync(rig.Host, [await CancelMessageIdAsync(job.RenderJobId)]);
    }

    /// <summary>Delivers the given outbox rows once each through the host's own dispatcher.</summary>
    /// <param name="host">The started Worker host.</param>
    /// <param name="messageIds">The rows that must end processed.</param>
    private async Task DeliverAsync(WorkerHost host, IReadOnlyList<Guid> messageIds)
    {
        for (var attempt = 0; attempt < 3 && messageIds.Any(id => !IsProcessed(id)); attempt++)
        {
            await host.RunOutboxOnceAsync(Ct);
        }

        messageIds.All(IsProcessed).ShouldBeTrue("every seeded row was delivered");
    }

    /// <summary>The Api host over this class's database, with the ledger scripted.</summary>
    /// <param name="ledger">The ledger the cancel resolves.</param>
    /// <returns>The factory; dispose it with the test.</returns>
    private ApiFactory Factory(ICreditLedger ledger) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<ICreditLedger>();
                services.AddSingleton<ICreditLedger>(ledger);
            });

    /// <summary>
    /// A started Worker host with the node client scripted and the money/hook/realtime/blob ports recorded, so
    /// every assertion is about a value this test wrote or a production path wrote.
    /// </summary>
    /// <param name="render">The scripted node client.</param>
    /// <param name="money">The ledger recorder; a new one when the caller does not supply it.</param>
    /// <returns>The started host and its recorders.</returns>
    private async Task<WorkerRig> StartWorkerAsync(FakeRenderClient render, MoneyRecorder? money = null)
    {
        var rig = new WorkerRig(render, money ?? new MoneyRecorder());

        var host = new WorkerHost(Database, new FakeClock(Start))
            .WithSetting("Render:PollSeconds", "10")
            .WithSetting("Render:MaxMinutes", "60")
            .ConfigureServices(services =>
            {
                // The Worker composition already calls AddRenderJobs, which registers the cancel outbox handler:
                // repeating it here would deliver every cancel twice (CancelRenderRequestedHandlerTests).
                services.RemoveAll<IRenderClient>();
                services.AddSingleton<IRenderClient>(render);
                services.RemoveAll<IRealtimeNotifier>();
                services.AddSingleton<IRealtimeNotifier>(rig.Notifier);
                services.RemoveAll<IRenderIngestEnqueuer>();
                services.AddSingleton<IRenderIngestEnqueuer>(rig.Ingest);
                services.RemoveAll<IRenderTerminalHooks>();
                services.AddSingleton<IRenderTerminalHooks>(rig.Hooks);
                services.RemoveAll<ICreditLedger>();
                services.AddSingleton<ICreditLedger>(rig.Money);
                services.RemoveAll<IBudgetGuard>();
                services.AddSingleton<IBudgetGuard>(rig.Money);
                services.RemoveAll<IPriceCatalog>();
                services.AddSingleton<IPriceCatalog>(rig.Money);
                services.RemoveAll<IImageCostEstimator>();
                services.AddSingleton<IImageCostEstimator>(rig.Money);
                services.RemoveAll<IBlobStore>();
                services.AddSingleton<IBlobStore>(rig.Blobs);
            });

        await host.StartAsync(Ct);
        rig.Host = host;

        return rig;
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
    /// <param name="job">The seeded job.</param>
    /// <param name="state">The 05.4.1-T2 state.</param>
    /// <param name="stage">The adapter's wire stage.</param>
    /// <param name="percent">The adapter's progress.</param>
    /// <returns>The status body.</returns>
    private static RenderStatusV1 Status(Job job, string state, string stage, int percent) =>
        new(
            RenderNodeSchemas.RenderStatus,
            Guid.Parse(job.TaskId, CultureInfo.InvariantCulture),
            job.RenderJobId,
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

    /// <summary>
    /// One owner with a series, an approved story version of short, clean text and the price version the paid
    /// render step needs, ready for the real <c>POST /episodes/{id}/render</c> producer.
    /// </summary>
    /// <returns>The ids the producer needs.</returns>
    private async Task<World> SeedQueuedWorldAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, new FakeClock(Start)).CreateAsync(
            user.Id,
            series.Id,
            lengthTier: LengthTier.Short,
            status: EpisodeStatus.StoryApproved,
            ct: Ct);
        await SeedPriceVersionAsync(user.Id);
        var versionId = await SeedApprovedVersionAsync(user.Id, episode.Id);

        return new World(user.Id, episode.Id, versionId);
    }

    /// <summary>One owner with its episode in <c>rendering</c>, its story version and a worker node.</summary>
    /// <returns>The ids a second row of the same world reads.</returns>
    private async Task<World> SeedWorldAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, new FakeClock(Start)).CreateAsync(
            user.Id,
            series.Id,
            ct: Ct);

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
            + $"VALUES ('{nodeId}', 'qa-cancel-node-{nodeId:N}', 'https://127.0.0.1:8443', 2, 'active', now(), "
            + $"'RTX 4090', '{TlsFingerprint}', now(), now())",
            Ct);

        await Database.ExecuteAsync($"UPDATE episodes SET status = 'rendering' WHERE id = '{episode.Id}'", Ct);

        return new World(user.Id, episode.Id, version.Id, nodeId);
    }

    /// <summary>
    /// One owner with one episode in <c>rendering</c>, its story version, a worker node and a
    /// <c>render_jobs</c> row of the given status — written with raw SQL exactly as the sibling tests do, so the
    /// row carries a complete <c>render-params.v1</c> document and the adapter task a submitted row would carry.
    /// </summary>
    /// <param name="status">The row's status.</param>
    /// <param name="cancelRequested">Whether the row already carries the owner's cancel request.</param>
    /// <param name="submittedMinutesAgo">How long before the poll instant the attempt was submitted.</param>
    /// <param name="startedMinutesAgo">How long before the poll instant it started; 0 means the submit instant.</param>
    /// <param name="stage">The row's stored stage.</param>
    /// <param name="percent">The row's stored progress.</param>
    /// <returns>The ids the assertions read.</returns>
    private async Task<Job> SeedJobAsync(
        string status,
        bool cancelRequested = false,
        int submittedMinutesAgo = 5,
        int startedMinutesAgo = 0,
        string stage = "voice",
        short percent = 30)
    {
        var world = await SeedWorldAsync();

        return await SeedJobAsync(
            world,
            status,
            cancelRequested,
            submittedMinutesAgo,
            startedMinutesAgo,
            stage,
            percent);
    }

    /// <summary>One <c>render_jobs</c> row of an existing world.</summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="status">The row's status.</param>
    /// <param name="cancelRequested">Whether the row already carries the owner's cancel request.</param>
    /// <param name="submittedMinutesAgo">How long before Start the attempt was submitted.</param>
    /// <param name="startedMinutesAgo">How long before Start it started.</param>
    /// <param name="stage">The row's stored stage.</param>
    /// <param name="percent">The row's stored progress.</param>
    /// <returns>The ids the assertions of the test read.</returns>
    private async Task<Job> SeedJobAsync(
        World world,
        string status,
        bool cancelRequested = false,
        int submittedMinutesAgo = 5,
        int startedMinutesAgo = 0,
        string stage = "voice",
        short percent = 30)
    {
        var jobId = Guid.CreateVersion7();
        var reservationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7().ToString("D");
        var submittedAt = Start.AddMinutes(-submittedMinutesAgo);
        var startedAt = Start.AddMinutes(-startedMinutesAgo);

        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, worker_node_id, adapter_task_id, submitted_at, started_at, next_poll_at, "
            + "cancel_requested_at) "
            + $"VALUES ('{jobId}', '{world.UserId}', '{world.EpisodeId}', '{world.StoryVersionId}', "
            + $"'{reservationId}', '{status}', '{stage}', {percent}, '{ParamsJson(world.StoryVersionId)}'::jsonb, "
            + $"1, '{world.NodeId}', '{taskId}', '{submittedAt:O}', "
            + $"{(startedMinutesAgo == 0 ? $"'{submittedAt:O}'" : $"'{startedAt:O}'")}, '{Start:O}', "
            + (cancelRequested ? $"'{Start:O}'" : "NULL")
            + ")",
            Ct);

        return new Job(world.UserId, world.EpisodeId, world.StoryVersionId, jobId, world.NodeId, taskId, reservationId);
    }

    /// <summary>
    /// Writes the one <c>outbox_messages</c> row the interceptor would have written for the cancel command.
    /// </summary>
    /// <param name="world">The seeded world (the payload's owner and episode).</param>
    /// <param name="renderJobId">The row the event names.</param>
    /// <returns>The message id.</returns>
    private async Task<Guid> SeedCancelMessageAsync(World world, Guid renderJobId)
    {
        var id = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(
            new RenderCancelRequested(renderJobId, world.EpisodeId, world.UserId, Start),
            OutboxJson.Options);

        await Database.ExecuteAsync(
            "INSERT INTO outbox_messages (id, type, payload, user_id, occurred_at, next_attempt_at, attempts) "
            + $"VALUES ('{id}', '{RenderCancelRequested}', '{payload}', '{world.UserId}', '{Start:O}', "
            + $"'{Start:O}', 0)",
            Ct);

        return id;
    }

    /// <summary>The pending <c>render_cancel_requested.v1</c> row of one job.</summary>
    /// <param name="renderJobId">The row the event names.</param>
    /// <returns>The message id.</returns>
    private async Task<Guid> CancelMessageIdAsync(Guid renderJobId) =>
        Guid.Parse(
            await ScalarAsync(
                $"SELECT id::text FROM outbox_messages WHERE type = '{RenderCancelRequested}' "
                + $"AND payload->>'renderJobId' = '{renderJobId}'"),
            CultureInfo.InvariantCulture);

    /// <summary>Whether one outbox row committed its delivery.</summary>
    /// <param name="messageId">The row.</param>
    /// <returns><c>true</c> when it is processed.</returns>
    private bool IsProcessed(Guid messageId) =>
        Scalar($"SELECT (processed_at IS NOT NULL)::text FROM outbox_messages WHERE id = '{messageId}'") == "true";

    private async Task<Guid> SeedApprovedVersionAsync(Guid userId, Guid episodeId)
    {
        var draft = Draft();
        var version = StoryVersion.Create(
            userId,
            episodeId,
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

        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start)))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database,
            $"UPDATE episodes SET status = 'story_approved', approved_story_version_id = '{version.Id}', "
            + $"approved_at = '{Start:O}' WHERE id = '{episodeId}'");

        return version.Id;
    }

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
            "qa-cancel-price-version",
            Start.AddDays(-1)));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The story the render reads: one keyword per segment, and no name for the corpus to match.</summary>
    /// <returns>The content document.</returns>
    private static StoryContentV1 Draft() => new(
        "The pier at dawn",
        new StorySegmentV1("The rain drifted over the riverside.", ["quiet riverside morning", "grey mist over water"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", ["empty wooden pier at dusk"], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    /// <summary>
    /// A complete <c>render-params.v1</c> document (05 D11): every member is <c>[JsonRequired]</c>, so the
    /// row could not be loaded without one.
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

    /// <summary>The episode's current strong ETag, read from <c>xmin</c> like the client's own copy.</summary>
    /// <param name="episodeId">The episode.</param>
    /// <returns>The quoted tag.</returns>
    private string Etag(Guid episodeId) =>
        ETags.Episode(uint.Parse(
            Scalar($"SELECT xmin::text FROM episodes WHERE id = '{episodeId}'"),
            CultureInfo.InvariantCulture));

    private static async Task<HttpResponseMessage> RenderAsync(HttpClient client, Guid episodeId, string? tag, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/render");
        if (tag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid renderJobId) =>
        await client.DeleteAsync($"/api/v1/render-jobs/{renderJobId}", Ct);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return document.RootElement.Clone();
    }

    /// <summary>The member names of a stored JSON document, so a wire shape is asserted as a whole.</summary>
    /// <param name="node">The document.</param>
    /// <returns>The names, ordinal-sorted and comma-joined.</returns>
    private static string Keys(JsonObject node) =>
        string.Join(',', node.Select(pair => pair.Key).Order(StringComparer.Ordinal));

    /// <summary>
    /// How many adapter calls of one method went to one attempt's task. Other test classes share this class's
    /// database and their pending outbox rows are delivered by the same dispatcher pass, so a bare
    /// <c>CancelCalls</c> count is not this test's to assert on.
    /// </summary>
    /// <param name="render">The scripted client.</param>
    /// <param name="method">The method to count.</param>
    /// <param name="taskId">The adapter task id this test's row owns.</param>
    /// <returns>The number of matching calls.</returns>
    private static int CallsFor(FakeRenderClient render, RenderClientMethod method, string taskId) =>
        render.Calls.Count(call => call.Method == method && string.Equals(call.TaskId, taskId, StringComparison.Ordinal));

    /// <summary>One row's cancel-relevant shape: status, the cancel stamp (null when unset) and the outcome code.</summary>
    /// <param name="renderJobId">The row.</param>
    /// <returns>The <c>status|cancelRequestedAt|null|-</c> string.</returns>
    private string JobRow(Guid renderJobId) =>
        Scalar($"SELECT status || '|' || coalesce(extract(epoch from cancel_requested_at)::bigint::text, 'null') || '|' "
            + $"|| coalesce(error_code, '-') FROM render_jobs WHERE id = '{renderJobId}'");

    private string Xmin(Guid renderJobId) => Scalar($"SELECT xmin::text FROM render_jobs WHERE id = '{renderJobId}'");

    private string CancelRequestedAt(Guid renderJobId) =>
        Scalar($"SELECT coalesce(extract(epoch from cancel_requested_at)::bigint::text, '-') "
            + $"FROM render_jobs WHERE id = '{renderJobId}'");

    private string EpisodeStatusOf(Guid episodeId) =>
        Scalar($"SELECT status FROM episodes WHERE id = '{episodeId}'");

    /// <summary>How many outbox rows of one type name the given render job.</summary>
    /// <param name="renderJobId">The row.</param>
    /// <param name="type">The event type name.</param>
    /// <returns>The row count.</returns>
    private long OutboxCount(Guid renderJobId, string type) =>
        SqlProbe.Count(Database,
            $"SELECT count(*) FROM outbox_messages WHERE type = '{type}' AND payload->>'renderJobId' = '{renderJobId}'");

    /// <summary>Every outbox row of the given render job, whatever its type.</summary>
    /// <param name="renderJobId">The row.</param>
    /// <returns>The row count.</returns>
    private long OutboxTotal(Guid renderJobId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE payload->>'renderJobId' = '{renderJobId}'");

    /// <summary>How many audit rows the cancel action carries for the given render job.</summary>
    /// <param name="renderJobId">The row.</param>
    /// <returns>The row count.</returns>
    private long AuditCount(Guid renderJobId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE action = '{CancelAction}' AND entity_id = '{renderJobId}'");

    private string Scalar(string sql) => SqlProbe.ScalarText(Database, sql);

    private async Task<string> ScalarAsync(string sql) =>
        (await Database.QueryStringsAsync(sql, Ct)).Single();

    private static string Epoch(DateTimeOffset instant) =>
        instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string Id(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>The ids one seeded world carries, with the node when the world has one.</summary>
    private sealed record World(Guid UserId, Guid EpisodeId, Guid StoryVersionId, Guid NodeId = default);

    /// <summary>The one row a test targets, its node, its adapter task and its reservation.</summary>
    private sealed record Job(
        Guid UserId,
        Guid EpisodeId,
        Guid StoryVersionId,
        Guid RenderJobId,
        Guid NodeId,
        string TaskId,
        Guid ReservationId);

    /// <summary>The started Worker host and the recorders the assertions read.</summary>
    private sealed class WorkerRig(FakeRenderClient render, MoneyRecorder money)
    {
        public WorkerHost Host { get; set; } = null!;

        public FakeRenderClient Render { get; } = render;

        public MoneyRecorder Money { get; } = money;

        public HooksRecorder Hooks { get; } = new();

        public RecordingNotifier Notifier { get; } = new();

        public RecordingIngestEnqueuer Ingest { get; } = new();

        public InMemoryBlobStore Blobs { get; } = new();

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

    /// <summary>The 03.4.2-T1 extension point: the calls it received, in order.</summary>
    private sealed class HooksRecorder : IRenderTerminalHooks
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
    /// The four money ports of one Worker attempt in one recorder: the refund by reservation id is what the
    /// terminal paths are asserted on, so an unrefunded path is an empty list.
    /// </summary>
    private sealed class MoneyRecorder :
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

    /// <summary>What the cancel's refund call does in this test.</summary>
    private enum RefundMode
    {
        /// <summary>Releases the reservation (the phase-P ledger's behaviour).</summary>
        Success,

        /// <summary>Answers a coded error, the shape a real ledger of 12.1.2 may answer with.</summary>
        Refused,

        /// <summary>Throws, the shape of a ledger the process cannot reach.</summary>
        Throws,
    }

    /// <summary>
    /// The Api host's <see cref="ICreditLedger"/>. The two failure modes exist to assert what a queued cancel
    /// persists when the money cannot be released: the handler returns the error (or the throw escapes) instead
    /// of swallowing it the way the Worker's terminal paths do.
    /// </summary>
    private sealed class ApiLedger(RefundMode mode = RefundMode.Success) : ICreditLedger
    {
        /// <summary>The reservation id every reserve hands out.</summary>
        public Guid Reservation { get; } = Guid.CreateVersion7();

        /// <summary>How often the command reserved credits.</summary>
        public int ReserveCalls { get; private set; }

        /// <summary>The refund calls the command made, in order.</summary>
        public List<(Guid ReservationId, string Reason)> RefundAttempts { get; } = [];

        /// <summary>
        /// The refunds the ledger actually applied. A refusal answers an error and a throw escapes, so neither is
        /// ever here; only the success mode records the moment a refund is released — which is what a rolled-back
        /// cancel must leave empty.
        /// </summary>
        public List<(Guid ReservationId, string Reason)> CommittedRefunds { get; } = [];

        /// <inheritdoc />
        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReserveCalls++;

            return ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(Reservation));
        }

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RefundAttempts.Add((reservationId.Value, reason));

            return mode switch
            {
                RefundMode.Refused => ValueTask.FromResult<ErrorOr<Success>>(
                    Error.Failure("ledger_unavailable", "The ledger cannot release the reservation now.")),
                RefundMode.Throws => throw new InvalidOperationException("scripted ledger failure"),
                _ => Commit(reservationId, reason),
            };
        }

        private ValueTask<ErrorOr<Success>> Commit(ReservationId reservationId, string reason)
        {
            CommittedRefunds.Add((reservationId.Value, reason));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
