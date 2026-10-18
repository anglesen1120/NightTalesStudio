using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Usage;
using NightTales.RenderNode.Contract;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// <c>DELETE /api/v1/render-jobs/{id}</c> against the real host and PostgreSQL (09 README BD8, 09.6.4-T1):
/// a <c>queued</c> job ends at once with its reservation released by id, a <c>submitted</c>/<c>running</c>
/// job is only <em>asked</em> to cancel (the refund belongs to the poller's terminal), an <c>ingesting</c>
/// job is refused, and the repeat is the same 204 without a second row or a second refund.
/// </summary>
/// <remarks>
/// <para>
/// The credit ledger is a recording fake registered in the Api host, because the acceptance checks assert
/// the exact <c>reservation_id</c> a refund was applied to and that no refund happens in the request path.
/// Every other port is the production implementation over the rows this class seeds. The Api host resolves
/// the removed <see cref="IRenderClient"/> to a recording fake in one test, which is how the
/// "the API process performs no node call" acceptance check is asserted (BD11).
/// </para>
/// <para>
/// Worktrees are not indexed by codebase-memory, so this file was written from <c>read</c>/<c>grep</c> over
/// absolute paths and mirrors <c>RenderEpisodeEndpointTests</c> (the <c>queued</c> flow, through the real
/// <c>POST /episodes/{id}/render</c> producer) and <c>PollRenderJobTests</c> (the raw row of an in-flight or
/// terminal job, with a complete <c>render-params.v1</c> document).
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CancelRenderEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    private const string RenderRequested = "render_requested.v1";
    private const string RenderCancelRequested = "render_cancel_requested.v1";

    /// <summary>The audit action the cancel command writes (the queued path never uses the recorder's row).</summary>
    private const string CancelAction = "render.cancelled";

    /// <summary>A word only the seeded story text carries, so an ids-only audit row can be proven free of it.</summary>
    private const string Canary = "spindlewick";

    /// <summary>The paragraph keyword of an episode the linter lets through.</summary>
    private const string CleanKeyword = "empty wooden pier at dusk";

    /// <summary>The 64-hex fingerprint the <c>worker_nodes</c> row needs (never a secret, D11).</summary>
    private const string TlsFingerprint =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Delete_Queued_Returns204()
    {
        var world = await SeedQueuedWorldAsync();
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);
        using var rendered = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "cancel-queued");
        rendered.StatusCode.ShouldBe(HttpStatusCode.Accepted, await rendered.Content.ReadAsStringAsync(Ct));
        var jobId = (await BodyAsync(rendered)).GetProperty("renderJobId").GetGuid();

        // The real producer's row: queued, paying the reservation the ledger returned, and carrying the one
        // outbox row this cancel may leave behind.
        var reservationId = Scalar($"SELECT reservation_id::text FROM render_jobs WHERE id = '{jobId}'");
        reservationId.ShouldBe(Id(ledger.Reservation));
        OutboxCount(jobId, RenderRequested).ShouldBe(1);

        using var response = await DeleteAsync(client, jobId);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        // Terminal at once: cancelled, no cancel stamp, no error code (render_cancelled is a state, never a code).
        Scalar($"SELECT status || '|' || coalesce(cancel_requested_at::text, '-') || '|' || coalesce(error_code, '-') "
            + $"FROM render_jobs WHERE id = '{jobId}'").ShouldBe("cancelled|-|-");
        Scalar($"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_approved");

        // The reservation is released by its own id, exactly once (WT50).
        ledger.Refunds.ShouldHaveSingleItem();
        ledger.Refunds[0].Reservation.Value.ShouldBe(Guid.Parse(reservationId, CultureInfo.InvariantCulture));
        ledger.Refunds[0].Reason.ShouldBe(RenderFailureRecorder.CancelledRefundReason);

        // No adapter call: nothing was enqueued beyond the original render_requested.v1 (BD11).
        OutboxCount(jobId, RenderCancelRequested).ShouldBe(0);
        OutboxTotal(jobId).ShouldBe(1);
        ledger.ReserveCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Delete_Submitted_Returns204_AndEnqueuesAdapterCancel()
    {
        var job = await SeedJobAsync("submitted");
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(job.UserId);

        using var response = await DeleteAsync(client, job.JobId);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        // Asked to cancel, not cancelled: the row keeps its state and stamps the request.
        Scalar($"SELECT status FROM render_jobs WHERE id = '{job.JobId}'").ShouldBe("submitted");
        CancelRequestedAt(job.JobId).ShouldBe(Epoch(Start));
        Scalar($"SELECT coalesce(error_code, '-') FROM render_jobs WHERE id = '{job.JobId}'").ShouldBe("-");

        // Exactly one outbox row, ids only.
        OutboxCount(job.JobId, RenderCancelRequested).ShouldBe(1);
        OutboxTotal(job.JobId).ShouldBe(1);
        var payload = JsonNode.Parse(Scalar(
            $"SELECT payload::text FROM outbox_messages WHERE type = '{RenderCancelRequested}' "
            + $"AND payload->>'renderJobId' = '{job.JobId}'"))!.AsObject();
        Keys(payload).ShouldBe("episodeId,occurredAt,renderJobId,userId");
        payload["renderJobId"]!.GetValue<string>().ShouldBe(Id(job.JobId));
        payload["episodeId"]!.GetValue<string>().ShouldBe(Id(job.EpisodeId));
        payload["userId"]!.GetValue<string>().ShouldBe(Id(job.UserId));
        payload["occurredAt"]!.GetValue<DateTimeOffset>().ShouldBe(Start);

        // The episode stays rendering until the adapter's terminal answer.
        Scalar($"SELECT status FROM episodes WHERE id = '{job.EpisodeId}'").ShouldBe("rendering");

        // And the request path refunds nothing; the positive control proves the fake does record a refund.
        ledger.Refunds.ShouldBeEmpty();
        (await ledger.RefundAsync(new ReservationId(job.ReservationId), RenderFailureRecorder.CancelledRefundReason, Ct))
            .IsError.ShouldBeFalse();
        ledger.Refunds.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Delete_Submitted_DoesNotRefund_RefundHappensAtTerminal()
    {
        var job = await SeedJobAsync("submitted");
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(job.UserId);

        using var response = await DeleteAsync(client, job.JobId);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        // The refund of a submitted/running cancel is applied once when the poller records the terminal
        // cancelled (WT51): the DELETE request never touches the ledger.
        ledger.Refunds.ShouldBeEmpty();

        // Positive control: the same harness ledger records the refund when the terminal path invokes it.
        var refund = await ledger.RefundAsync(
            new ReservationId(job.ReservationId), RenderFailureRecorder.CancelledRefundReason, Ct);
        refund.IsError.ShouldBeFalse();
        ledger.Refunds.ShouldHaveSingleItem().Reservation.Value.ShouldBe(job.ReservationId);
    }

    [Fact]
    public async Task Delete_Ingesting_Returns409ProblemDetails()
    {
        var job = await SeedJobAsync("ingesting", stage: "downloading", percent: 80);
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(job.UserId);

        using var response = await DeleteAsync(client, job.JobId);
        (await ProblemAsync(response, 409)).Code.ShouldBe("episode_invalid_state");

        // Nothing changed: the output is already being stored (BD8).
        Scalar($"SELECT status || '|' || stage || '|' || percent::text FROM render_jobs WHERE id = '{job.JobId}'")
            .ShouldBe("ingesting|downloading|80");
        CancelRequestedAt(job.JobId).ShouldBe("-");
        Scalar($"SELECT status FROM episodes WHERE id = '{job.EpisodeId}'").ShouldBe("rendering");
        OutboxTotal(job.JobId).ShouldBe(0);
        ledger.Refunds.ShouldBeEmpty();

        // Positive control: the fake records the refund the refused request must never make.
        await ledger.RefundAsync(new ReservationId(job.ReservationId), RenderFailureRecorder.CancelledRefundReason, Ct);
        ledger.Refunds.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Delete_Twice_IsIdempotent()
    {
        // A submitted job: the second DELETE writes no second outbox row and no second audit row.
        var job = await SeedJobAsync("submitted");
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(job.UserId);

        using (var first = await DeleteAsync(client, job.JobId))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync(Ct));
        }

        using (var second = await DeleteAsync(client, job.JobId))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.NoContent, await second.Content.ReadAsStringAsync(Ct));
        }

        OutboxCount(job.JobId, RenderCancelRequested).ShouldBe(1);
        OutboxTotal(job.JobId).ShouldBe(1);
        AuditCount(job.JobId).ShouldBe(1);
        CancelRequestedAt(job.JobId).ShouldBe(Epoch(Start));
        ledger.Refunds.ShouldBeEmpty();

        // A queued job: the first DELETE cancels and refunds, the second changes nothing.
        var queued = await SeedQueuedWorldAsync();
        var queuedLedger = new RecordingLedger();
        using var queuedFactory = Factory(queuedLedger);
        using var queuedClient = queuedFactory.ClientFor(queued.UserId);
        using var rendered = await RenderAsync(queuedClient, queued.EpisodeId, Etag(queued.EpisodeId), "cancel-twice");
        rendered.StatusCode.ShouldBe(HttpStatusCode.Accepted, await rendered.Content.ReadAsStringAsync(Ct));
        var jobId = (await BodyAsync(rendered)).GetProperty("renderJobId").GetGuid();

        using (var cancelling = await DeleteAsync(queuedClient, jobId))
        {
            cancelling.StatusCode.ShouldBe(HttpStatusCode.NoContent, await cancelling.Content.ReadAsStringAsync(Ct));
        }

        using (var repeating = await DeleteAsync(queuedClient, jobId))
        {
            repeating.StatusCode.ShouldBe(HttpStatusCode.NoContent, await repeating.Content.ReadAsStringAsync(Ct));
        }

        Scalar($"SELECT status FROM render_jobs WHERE id = '{jobId}'").ShouldBe("cancelled");
        queuedLedger.Refunds.Count.ShouldBe(1);
        OutboxCount(jobId, RenderRequested).ShouldBe(1);
        OutboxCount(jobId, RenderCancelRequested).ShouldBe(0);
        AuditCount(jobId).ShouldBe(1);
    }

    [Fact]
    public async Task Delete_ForeignJob_Returns404_NotFound()
    {
        var owner = await SeedJobAsync("submitted");
        var stranger = await SeedJobAsync("submitted");
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(stranger.UserId);

        string foreignBody;
        using (var foreign = await DeleteAsync(client, owner.JobId))
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

        // Another user's job and an unknown id answer the same not_found shape (D10): never 403, and no way
        // to tell the two apart.
        JsonSerializer.Deserialize<ProblemResponse>(foreignBody, ProblemResponse.Options)!.Code.ShouldBe("not_found");
        JsonSerializer.Deserialize<ProblemResponse>(absentBody, ProblemResponse.Options)!.Code.ShouldBe("not_found");
        Keys(JsonNode.Parse(foreignBody)!.AsObject())
            .ShouldBe(Keys(JsonNode.Parse(absentBody)!.AsObject()));

        // The foreign row is untouched.
        Scalar($"SELECT status || '|' || coalesce(cancel_requested_at::text, '-') FROM render_jobs WHERE id = '{owner.JobId}'")
            .ShouldBe("submitted|-");
        OutboxTotal(owner.JobId).ShouldBe(0);
        ledger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_WithoutAuth_Returns401()
    {
        var job = await SeedJobAsync("submitted");
        using var factory = Factory(new RecordingLedger());
        using var client = factory.AnonymousClient();

        using var response = await DeleteAsync(client, job.JobId);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.Content.ReadAsStringAsync(Ct));

        Scalar($"SELECT status || '|' || coalesce(cancel_requested_at::text, '-') FROM render_jobs WHERE id = '{job.JobId}'")
            .ShouldBe("submitted|-");
        OutboxTotal(job.JobId).ShouldBe(0);
    }

    [Fact]
    public async Task Delete_AuditRowCarriesIdsOnly()
    {
        var world = await SeedQueuedWorldAsync();
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);
        using var rendered = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "cancel-audit");
        rendered.StatusCode.ShouldBe(HttpStatusCode.Accepted, await rendered.Content.ReadAsStringAsync(Ct));
        var jobId = (await BodyAsync(rendered)).GetProperty("renderJobId").GetGuid();

        using var response = await DeleteAsync(client, jobId);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        // One row for one cancel: the queued path audits through the command's own behaviour (BD15), not the
        // Worker's failure recorder, so the action never doubles up.
        AuditCount(jobId).ShouldBe(1);
        SqlProbe.ScalarText(Database,
                $"SELECT entity_type || '|' || subject_user_id::text FROM audit_logs "
                + $"WHERE action = '{CancelAction}' AND entity_id = '{jobId}'")
            .ShouldBe($"render_job|{Id(world.UserId)}");

        // Ids only: no story text, no params, no adapter text.
        var data = SqlProbe.ScalarText(Database,
            $"SELECT data::text FROM audit_logs WHERE action = '{CancelAction}' AND entity_id = '{jobId}'");
        var audit = JsonNode.Parse(data)!.AsObject();
        Keys(audit).ShouldBe("episodeId,renderJobId");
        audit["episodeId"]!.GetValue<string>().ShouldBe(Id(world.EpisodeId));
        audit["renderJobId"]!.GetValue<string>().ShouldBe(Id(jobId));
        data.ShouldNotContain(Canary);
        data.ShouldNotContain("pier", Case.Insensitive);
    }

    [Fact]
    public async Task Api_DoesNotCallNodeOnCancel()
    {
        var render = new RecordingRenderClient();
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger, render);

        // A queued job through the real producer, then its cancel.
        var world = await SeedQueuedWorldAsync();
        using (var client = factory.ClientFor(world.UserId))
        {
            using var rendered = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "cancel-node-queued");
            rendered.StatusCode.ShouldBe(HttpStatusCode.Accepted, await rendered.Content.ReadAsStringAsync(Ct));
            var queuedJobId = (await BodyAsync(rendered)).GetProperty("renderJobId").GetGuid();

            using var response = await DeleteAsync(client, queuedJobId);
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        }

        // A submitted job: the DELETE writes the outbox row the Worker will deliver, from the Api process.
        var job = await SeedJobAsync("submitted");
        using (var client = factory.ClientFor(job.UserId))
        {
            using var response = await DeleteAsync(client, job.JobId);
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        }

        OutboxCount(job.JobId, RenderCancelRequested).ShouldBe(1);
        render.Calls.ShouldBeEmpty("the API must never perform a node call for a cancel (BD11)");

        // Positive control: the Api host does resolve the registered client, and the recorder records a call.
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRenderClient>().ShouldBeSameAs(render);
        (await render.CancelAsync(null!, "control-task", Ct)).IsError.ShouldBeFalse();
        render.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Delete_RowLockedByAnotherTransaction_Returns409ConcurrencyConflict()
    {
        var job = await SeedJobAsync("submitted");
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(job.UserId);

        // Another transaction holds the row's lock, the way a queued SubmitRenderJob holds it across its
        // adapter call (WT55).
        await using var holder = Database.CreateAppDbContext(new TestCurrentUser { Id = job.UserId });
        await using var held = await holder.Database.BeginTransactionAsync(Ct);
        await holder.LockRenderJobForUpdateAsync(job.JobId, Ct);

        var started = Stopwatch.GetTimestamp();
        using (var refused = await DeleteAsync(client, job.JobId))
        {
            // The bounded 2s wait is refused and mapped to the existing 409, not surfaced as a 500.
            (await ProblemAsync(refused, 409)).Code.ShouldBe("concurrency_conflict");
        }

        var waited = Stopwatch.GetElapsedTime(started);
        waited.ShouldBeGreaterThan(TimeSpan.FromSeconds(1), "the lock wait must be honoured, not skipped");
        waited.ShouldBeLessThan(TimeSpan.FromSeconds(30));

        // The refusal changed nothing.
        Scalar($"SELECT status || '|' || coalesce(cancel_requested_at::text, '-') FROM render_jobs WHERE id = '{job.JobId}'")
            .ShouldBe("submitted|-");
        OutboxTotal(job.JobId).ShouldBe(0);
        ledger.Refunds.ShouldBeEmpty();

        // Release the lock: the same DELETE now succeeds, so the 409 was the contention and not the state.
        await held.RollbackAsync(Ct);
        using (var accepted = await DeleteAsync(client, job.JobId))
        {
            accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await accepted.Content.ReadAsStringAsync(Ct));
        }

        CancelRequestedAt(job.JobId).ShouldBe(Epoch(Start));
        OutboxCount(job.JobId, RenderCancelRequested).ShouldBe(1);
    }

    private ApiFactory Factory(ICreditLedger ledger, IRenderClient? render = null) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<ICreditLedger>();
                services.AddSingleton<ICreditLedger>(ledger);

                if (render is not null)
                {
                    services.RemoveAll<IRenderClient>();
                    services.AddSingleton<IRenderClient>(render);
                }
            });

    /// <summary>
    /// One owner with a series, an approved story version of short, clean text and the price version the
    /// paid render step needs, ready for the real <c>POST /episodes/{id}/render</c> producer.
    /// </summary>
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

    /// <summary>
    /// One owner with one episode in <c>rendering</c>, its story version, a worker node and a
    /// <c>render_jobs</c> row of the given status — written with raw SQL exactly as
    /// <c>PollRenderJobTests.SeedJobAsync</c> does, so the row carries a complete <c>render-params.v1</c>
    /// document and the adapter task the Worker would have adopted.
    /// </summary>
    private async Task<Job> SeedJobAsync(string status, string stage = "downloading", short percent = 10)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, new FakeClock(Start)).CreateAsync(
            user.Id,
            series.Id,
            lengthTier: LengthTier.Short,
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
        var jobId = Guid.CreateVersion7();
        var reservationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);

        await Database.ExecuteAsync($"UPDATE episodes SET status = 'rendering' WHERE id = '{episode.Id}'", Ct);

        await Database.ExecuteAsync(
            "INSERT INTO worker_nodes (id, name, base_url, capacity, state, last_heartbeat_at, gpu_model, "
            + "tls_cert_sha256, created_at, updated_at) "
            + $"VALUES ('{nodeId}', 'cancel-node-{nodeId:N}', 'https://127.0.0.1:8443', 2, 'active', now(), "
            + $"'RTX 4090', '{TlsFingerprint}', now(), now())",
            Ct);

        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, worker_node_id, adapter_task_id, submitted_at, started_at, next_poll_at) "
            + $"VALUES ('{jobId}', '{user.Id}', '{episode.Id}', '{version.Id}', '{reservationId}', '{status}', "
            + $"'{stage}', {percent}, '{ParamsJson(version.Id)}'::jsonb, 1, '{nodeId}', '{taskId}', "
            + $"'{Start:O}', '{Start:O}', '{Start:O}')",
            Ct);

        return new Job(user.Id, episode.Id, version.Id, jobId, nodeId, taskId, reservationId);
    }

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
            "cancel-endpoint-price-version",
            Start.AddDays(-1)));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The story the render reads: one keyword per segment, and no name for the corpus to match.</summary>
    private static StoryContentV1 Draft() => new(
        "The pier at dawn",
        new StorySegmentV1($"The {Canary} mist drifted over the riverside.", ["quiet riverside morning", "grey mist over water"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", [CleanKeyword], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    /// <summary>
    /// A complete <c>render-params.v1</c> document (05 D11): every member is <c>[JsonRequired]</c>, so the
    /// row could not be loaded without one.
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

    /// <summary>The episode's current strong ETag, read from <c>xmin</c> like the client's own copy.</summary>
    private string Etag(Guid episodeId) =>
        ETags.Episode(uint.Parse(
            Scalar($"SELECT xmin::text FROM episodes WHERE id = '{episodeId}'"),
            CultureInfo.InvariantCulture));

    private static async Task<HttpResponseMessage> RenderAsync(HttpClient client, Guid episodeId, string? tag, string? key)
    {
        using var request = Request(episodeId, tag, key);

        return await client.SendAsync(request, Ct);
    }

    private static HttpRequestMessage Request(Guid episodeId, string? tag, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/render");
        if (tag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return request;
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid renderJobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/render-jobs/{renderJobId}");

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return document.RootElement.Clone();
    }

    private static async Task<ProblemResponse> ProblemAsync(HttpResponseMessage response, int status)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe((HttpStatusCode)status, text);

        return JsonSerializer.Deserialize<ProblemResponse>(text, ProblemResponse.Options)!;
    }

    /// <summary>The member names of a stored JSON document, so an ids-only payload is asserted as a whole.</summary>
    /// <param name="node">The document.</param>
    /// <returns>The names, ordinal-sorted and comma-joined.</returns>
    private static string Keys(JsonObject node) =>
        string.Join(',', node.Select(pair => pair.Key).Order(StringComparer.Ordinal));

    /// <summary>Runs one scalar probe against this class's database.</summary>
    private string Scalar(string sql) => SqlProbe.ScalarText(Database, sql);

    /// <summary>How many outbox rows of one type name the given render job.</summary>
    private long OutboxCount(Guid renderJobId, string type) =>
        SqlProbe.Count(Database,
            $"SELECT count(*) FROM outbox_messages WHERE type = '{type}' AND payload->>'renderJobId' = '{renderJobId}'");

    /// <summary>Every outbox row of the given render job, whatever its type.</summary>
    private long OutboxTotal(Guid renderJobId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE payload->>'renderJobId' = '{renderJobId}'");

    /// <summary>How many audit rows a cancel action carries for the given render job.</summary>
    private long AuditCount(Guid renderJobId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE action = '{CancelAction}' AND entity_id = '{renderJobId}'");

    private string CancelRequestedAt(Guid renderJobId) =>
        Scalar($"SELECT coalesce(extract(epoch from cancel_requested_at)::bigint::text, '-') FROM render_jobs WHERE id = '{renderJobId}'");

    private static string Epoch(DateTimeOffset instant) =>
        instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string Id(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private sealed record World(Guid UserId, Guid EpisodeId, Guid VersionId);

    private sealed record Job(
        Guid UserId,
        Guid EpisodeId,
        Guid VersionId,
        Guid JobId,
        Guid NodeId,
        string TaskId,
        Guid ReservationId);

    /// <summary>
    /// The credit ledger these endpoint tests run against: it returns one known reservation id, so the
    /// persisted <c>render_jobs.reservation_id</c> and the refund's own id can both be asserted, and it
    /// records every refund, so the request path can be proven never to release a submitted job's credits.
    /// </summary>
    private sealed class RecordingLedger : ICreditLedger
    {
        private readonly Guid _reservation = Guid.CreateVersion7();

        /// <summary>The id every reserve returns.</summary>
        public Guid Reservation => _reservation;

        /// <summary>How often the command reserved credits.</summary>
        public int ReserveCalls { get; private set; }

        /// <summary>The refunds applied, in call order.</summary>
        public List<RefundCall> Refunds { get; } = [];

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

            return ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(_reservation));
        }

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Refunds.Add(new RefundCall(reservationId, reason));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }

    /// <summary>One recorded refund: the reservation it released and the reason it recorded.</summary>
    private sealed record RefundCall(ReservationId Reservation, string Reason);

    /// <summary>
    /// The <see cref="IRenderClient"/> an Api test host resolves: it records every call and never opens a
    /// socket, so the "the Api performs no node call for a cancel" acceptance check is a real assertion.
    /// <see cref="CancelAsync"/> answers success rather than throwing, because the positive control invokes
    /// it directly to prove the recorder works and the host really resolved this instance.
    /// </summary>
    private sealed class RecordingRenderClient : IRenderClient
    {
        /// <summary>The calls that reached the Api host's render client, in call order.</summary>
        public List<string> Calls { get; } = [];

        /// <inheritdoc />
        public Task<ErrorOr<AdapterAcceptedV1>> SubmitAsync(WorkerNode node, AdapterSubmitRequestV1 body, CancellationToken ct)
        {
            Calls.Add(nameof(SubmitAsync));

            throw new InvalidOperationException("The Api must never submit a render.");
        }

        /// <inheritdoc />
        public Task<ErrorOr<RenderStatusV1>> GetStatusAsync(WorkerNode node, string taskId, CancellationToken ct)
        {
            Calls.Add(nameof(GetStatusAsync));

            throw new InvalidOperationException("The Api must never poll a render node.");
        }

        /// <inheritdoc />
        public Task<ErrorOr<Success>> CancelAsync(WorkerNode node, string taskId, CancellationToken ct)
        {
            Calls.Add(nameof(CancelAsync));

            return Task.FromResult<ErrorOr<Success>>(Result.Success);
        }

        /// <inheritdoc />
        public Task<ErrorOr<Success>> DeleteAsync(WorkerNode node, string taskId, CancellationToken ct)
        {
            Calls.Add(nameof(DeleteAsync));

            throw new InvalidOperationException("The Api must never delete a render node task.");
        }
    }
}
