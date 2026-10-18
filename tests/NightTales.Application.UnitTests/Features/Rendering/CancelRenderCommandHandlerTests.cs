using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Rendering.Commands.CancelRender;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Subtitles;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Rendering;

/// <summary>
/// <see cref="CancelRenderCommandHandler"/> (09.6.4-T1, 09 README BD8) against a real <c>AppDbContext</c> and the
/// same grain of manual fakes the sibling rendering tests use: the status table of one <c>DELETE</c> (a
/// <c>queued</c> attempt is cancelled at once and refunded, a <c>submitted</c>/<c>running</c> one is only asked to
/// cancel, an <c>ingesting</c>/<c>succeeded</c>/<c>failed</c> one is refused), the tenant filter's 404, the
/// idempotent repeat, the bounded lock's 409 and the one thing this command must never touch (the params and the
/// quality report). Every seeded row belongs to its own user, so the outbox counts below stay exact without a
/// reset.
/// </summary>
/// <remarks>
/// The handler is called directly (no pipeline, so no idempotency, unit-of-work or audit behaviour in the way) and
/// the fake ledger records the refund. Each call still runs inside the transaction the pipeline's unit of work
/// would open - <c>BeginTransactionAsync</c> then <c>Handle</c>, <c>SaveChangesAsync</c> and the commit - so the
/// job row really is read, decided on and committed under the port's lock and its bounded <c>lock_timeout</c>
/// stays scoped to that transaction. The refund's refusal path is deliberately not exercised: the fake answers
/// success, and the request path's rollback of a refused refund is the unit-of-work's, not this handler's.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CancelRenderCommandHandlerTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A <c>render-quality.v1</c> the ingest would have stored; the cancel must leave it byte for byte.</summary>
    private const string QualityReportJson = """{"schema":"render-quality.v1","marker":"sentinel-9f2c"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Queued_CancelsAtOnce_RefundsOnce_NoOutbox()
    {
        var world = await SeedAsync("queued");
        var ledger = new RecordingCreditLedger();
        await using var db = Open(world.UserId);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var command = new CancelRenderCommand(world.RenderJobId);

        var result = await Handler(db, ledger).Handle(command, Ct);

        result.IsError.ShouldBeFalse();

        // No node ever had the task, so nothing is asked to cancel: no render_cancel_requested.v1 is raised.
        var job = TrackedJob(db);
        job.DomainEvents.OfType<RenderCancelRequested>().ShouldBeEmpty();
        command.Outcome.ShouldNotBeNull().EpisodeId.ShouldBe(world.EpisodeId);

        // The release is by reservation id, inside the request path (BD8/WT50) — the positive control the
        // "no refund" assertions of the other cases are read against.
        ledger.Refunds.ShouldBe([(world.ReservationId, RenderFailureRecorder.CancelledRefundReason)]);

        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);

        (await ScalarAsync(
                $"SELECT status || '|' || (cancel_requested_at IS NULL)::text || '|' || (error_code IS NULL)::text "
                + $"|| '|' || (finished_at IS NOT NULL)::text FROM render_jobs WHERE id = '{world.RenderJobId}'"))
            .ShouldBe("cancelled|true|true|true", "terminal at once, never cancel_requested, error_code stays null (05 D5)");
        (await ScalarAsync($"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'"))
            .ShouldBe("story_approved", "the attempt ended, so the episode leaves rendering (BD8)");
        (await ScalarAsync($"SELECT count(*)::text FROM outbox_messages WHERE user_id = '{world.UserId}'"))
            .ShouldBe("0", "a queued cancel writes no outbox row: there is nobody to tell");
    }

    [Fact]
    public async Task Submitted_RequestsCancel_WritesOutbox_NoRefundYet()
    {
        var world = await SeedAsync("submitted");
        var ledger = new RecordingCreditLedger();
        await using var db = Open(world.UserId);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var command = new CancelRenderCommand(world.RenderJobId);

        var result = await Handler(db, ledger).Handle(command, Ct);

        result.IsError.ShouldBeFalse();

        var job = TrackedJob(db);
        job.CancelRequestedAt.ShouldBe(Start);
        job.Status.ShouldBe(RenderJobStatus.Submitted, "the row is only asked to cancel; the poller ends it");
        var raised = job.DomainEvents.OfType<RenderCancelRequested>().ShouldHaveSingleItem();
        raised.RenderJobId.ShouldBe(world.RenderJobId);
        raised.EpisodeId.ShouldBe(world.EpisodeId);
        raised.UserId.ShouldBe(world.UserId);
        raised.OccurredAt.ShouldBe(Start);
        command.Outcome.ShouldNotBeNull().EpisodeId.ShouldBe(world.EpisodeId);

        // The refund belongs to the terminal cancelled, never to this request path ([RULED — README §B10 WT51]).
        ledger.Refunds.ShouldBeEmpty();
        ledger.Calls.ShouldBeEmpty();

        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);

        (await ScalarAsync(
                $"SELECT status || '|' || (cancel_requested_at = '{Start:O}')::text FROM render_jobs WHERE id = '{world.RenderJobId}'"))
            .ShouldBe("submitted|true");
        (await ScalarAsync($"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'"))
            .ShouldBe("rendering", "the episode stays rendering until the adapter's terminal cancelled (WT51)");
        (await ScalarAsync(
                $"SELECT type || '|' || (payload->>'renderJobId') || '|' || (payload->>'episodeId') || '|' "
                + $"|| (payload->>'userId') || '|' || ((payload->>'occurredAt') IS NOT NULL)::text FROM outbox_messages "
                + $"WHERE user_id = '{world.UserId}'"))
            .ShouldBe(
                $"render_cancel_requested.v1|{world.RenderJobId}|{world.EpisodeId}|{world.UserId}|true",
                "exactly one ids-only render_cancel_requested.v1 row, the Worker's hand-off (D11)");
    }

    [Fact]
    public async Task SecondDelete_IsIdempotent_NoSecondRefund()
    {
        // (a) A second DELETE of an already cancelled row: the status guard writes nothing and refunds nothing.
        var cancelled = await SeedAsync("queued");
        var cancelledLedger = new RecordingCreditLedger();

        await using (var first = Open(cancelled.UserId))
        {
            await using var firstTransaction = await first.Database.BeginTransactionAsync(Ct);

            (await Handler(first, cancelledLedger).Handle(new CancelRenderCommand(cancelled.RenderJobId), Ct))
                .IsError.ShouldBeFalse();
            await first.SaveChangesAsync(Ct);
            await firstTransaction.CommitAsync(Ct);
        }

        cancelledLedger.Refunds.ShouldBe([(cancelled.ReservationId, RenderFailureRecorder.CancelledRefundReason)]);

        await using (var second = Open(cancelled.UserId))
        {
            await using var secondTransaction = await second.Database.BeginTransactionAsync(Ct);
            var repeat = new CancelRenderCommand(cancelled.RenderJobId);

            (await Handler(second, cancelledLedger).Handle(repeat, Ct)).IsError.ShouldBeFalse();

            repeat.Outcome.ShouldBeNull("the repeat changed nothing, so no second audit row is written");
            await second.SaveChangesAsync(Ct);
            await secondTransaction.CommitAsync(Ct);
        }

        cancelledLedger.Refunds.Count.ShouldBe(1, "a terminal row is never refunded a second time (WT50)");
        (await ScalarAsync(
                $"SELECT status || '|' || (finished_at = '{Start:O}')::text FROM render_jobs WHERE id = '{cancelled.RenderJobId}'"))
            .ShouldBe("cancelled|true", "and the repeat does not move the terminal stamp");
        (await ScalarAsync($"SELECT count(*)::text FROM outbox_messages WHERE user_id = '{cancelled.UserId}'"))
            .ShouldBe("0");

        // (b) A second DELETE of a submitted row whose cancel was already requested: the stamp guard, same answer.
        var requested = await SeedAsync("submitted");
        var requestedLedger = new RecordingCreditLedger();

        await using (var firstRequest = Open(requested.UserId))
        {
            await using var firstRequestTransaction = await firstRequest.Database.BeginTransactionAsync(Ct);

            (await Handler(firstRequest, requestedLedger).Handle(new CancelRenderCommand(requested.RenderJobId), Ct))
                .IsError.ShouldBeFalse();
            await firstRequest.SaveChangesAsync(Ct);
            await firstRequestTransaction.CommitAsync(Ct);
        }

        (await ScalarAsync($"SELECT count(*)::text FROM outbox_messages WHERE user_id = '{requested.UserId}'"))
            .ShouldBe("1");

        await using (var secondRequest = Open(requested.UserId))
        {
            await using var secondRequestTransaction = await secondRequest.Database.BeginTransactionAsync(Ct);
            var repeat = new CancelRenderCommand(requested.RenderJobId);

            (await Handler(secondRequest, requestedLedger).Handle(repeat, Ct)).IsError.ShouldBeFalse();

            repeat.Outcome.ShouldBeNull();
            await secondRequest.SaveChangesAsync(Ct);
            await secondRequestTransaction.CommitAsync(Ct);
        }

        (await ScalarAsync($"SELECT count(*)::text FROM outbox_messages WHERE user_id = '{requested.UserId}'"))
            .ShouldBe("1", "one render_cancel_requested.v1 row, never a second");
        requestedLedger.Refunds.ShouldBeEmpty("the request path never refunds a submitted attempt (WT51)");
    }

    [Fact]
    public async Task Ingesting_ReturnsEpisodeInvalidState()
    {
        var world = await SeedAsync("ingesting");
        var ledger = new RecordingCreditLedger();
        await using var db = Open(world.UserId);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var command = new CancelRenderCommand(world.RenderJobId);

        var result = await Handler(db, ledger).Handle(command, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);
        result.FirstError.Type.ShouldBe(ErrorType.Conflict, "a refused cancel is the aggregate's 409, not a 500");
        command.Outcome.ShouldBeNull();
        ledger.Refunds.ShouldBeEmpty();
        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);

        (await ScalarAsync(
                $"SELECT status || '|' || (cancel_requested_at IS NULL)::text FROM render_jobs WHERE id = '{world.RenderJobId}'"))
            .ShouldBe("ingesting|true", "the output is already being stored: nothing changes (BD8)");
        (await ScalarAsync($"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'")).ShouldBe("rendering");
        (await ScalarAsync($"SELECT count(*)::text FROM outbox_messages WHERE user_id = '{world.UserId}'"))
            .ShouldBe("0");
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    public async Task Succeeded_Or_Failed_ReturnsEpisodeInvalidState(string status)
    {
        var world = await SeedAsync(status);
        var ledger = new RecordingCreditLedger();
        await using var db = Open(world.UserId);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var command = new CancelRenderCommand(world.RenderJobId);

        var result = await Handler(db, ledger).Handle(command, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        command.Outcome.ShouldBeNull();
        ledger.Refunds.ShouldBeEmpty("a settled or failed attempt is not refunded by a cancel");
        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);

        (await ScalarAsync(
                $"SELECT status || '|' || (cancel_requested_at IS NULL)::text FROM render_jobs WHERE id = '{world.RenderJobId}'"))
            .ShouldBe($"{status}|true", "one attempt records one verdict: the terminal state wins (WT51)");
        (await ScalarAsync($"SELECT count(*)::text FROM outbox_messages WHERE user_id = '{world.UserId}'"))
            .ShouldBe("0");
    }

    [Fact]
    public async Task ForeignJob_ReturnsNotFound()
    {
        var world = await SeedAsync("queued");
        var ledger = new RecordingCreditLedger();

        // The row is there; the 404 below is the tenant filter, not a missing id (the control for D10).
        (await ScalarAsync($"SELECT count(*)::text FROM render_jobs WHERE id = '{world.RenderJobId}'"))
            .ShouldBe("1");

        await using var db = Open(Guid.CreateVersion7());
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var command = new CancelRenderCommand(world.RenderJobId);

        var result = await Handler(db, ledger).Handle(command, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.NotFound, "another user's job is 404, never 403 (D10)");
        command.Outcome.ShouldBeNull();
        ledger.Refunds.ShouldBeEmpty();
        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);

        (await ScalarAsync($"SELECT status FROM render_jobs WHERE id = '{world.RenderJobId}'"))
            .ShouldBe("queued", "and the row is untouched by the stranger's call");
        (await ScalarAsync($"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'")).ShouldBe("rendering");
    }

    [Fact]
    public async Task Cancel_DoesNotTouchParamsOrQualityReport()
    {
        // Both the immediate (queued) and the requested (submitted) cancel leave the render's settings and the
        // stored quality report exactly as they were: the command is ids-only and never re-encodes the params.
        foreach (var status in new[] { "queued", "submitted" })
        {
            var world = await SeedAsync(status, qualityReport: QualityReportJson);
            var ledger = new RecordingCreditLedger();
            var before = await SnapshotAsync(world.RenderJobId);
            before.ShouldNotBeNullOrEmpty("the seed carries both a params document and a quality report");

            await using var db = Open(world.UserId);
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            var result = await Handler(db, ledger).Handle(new CancelRenderCommand(world.RenderJobId), Ct);
            result.IsError.ShouldBeFalse();
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);

            (await SnapshotAsync(world.RenderJobId))
                .ShouldBe(before, $"a {status} cancel never rewrites params or the quality report");
        }
    }

    [Fact]
    public async Task LockTimeout_ReturnsConcurrencyConflict_AndNothingChanges()
    {
        var world = await SeedAsync("submitted");
        var ledger = new RecordingCreditLedger();
        var user = new TestCurrentUser { Id = world.UserId };

        // Another writer (the queued submit holding the row across its adapter call, WT55) holds the lock.
        await using var holder = Database.CreateAppDbContext(user);
        await using var held = await holder.Database.BeginTransactionAsync(Ct);
        await holder.LockRenderJobForUpdateAsync(world.RenderJobId, Ct);

        await using var db = Open(world.UserId);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var command = new CancelRenderCommand(world.RenderJobId);

        var result = await Handler(db, ledger).Handle(command, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ConcurrencyConflict, "the bounded wait's refusal is the retryable 409 (D5)");
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        command.Outcome.ShouldBeNull();
        ledger.Refunds.ShouldBeEmpty();
        await transaction.RollbackAsync(Ct);
        await held.CommitAsync(Ct);

        (await ScalarAsync(
                $"SELECT status || '|' || (cancel_requested_at IS NULL)::text FROM render_jobs WHERE id = '{world.RenderJobId}'"))
            .ShouldBe("submitted|true", "the refused call wrote nothing");
        (await ScalarAsync($"SELECT count(*)::text FROM outbox_messages WHERE user_id = '{world.UserId}'"))
            .ShouldBe("0");

        // Positive control: with the lock released the very same cancel goes through, so the 409 was contention.
        await using var retry = Open(world.UserId);
        await using var retryTransaction = await retry.Database.BeginTransactionAsync(Ct);

        (await Handler(retry, ledger).Handle(new CancelRenderCommand(world.RenderJobId), Ct)).IsError.ShouldBeFalse();
        await retry.SaveChangesAsync(Ct);
        await retryTransaction.CommitAsync(Ct);

        (await ScalarAsync(
                $"SELECT (cancel_requested_at IS NOT NULL)::text FROM render_jobs WHERE id = '{world.RenderJobId}'"))
            .ShouldBe("true");
    }

    /// <summary>A tracked write context for <paramref name="userId"/>, at the tests' frozen instant.</summary>
    private AppDbContext Open(Guid userId) =>
        Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));

    /// <summary>The handler under test over the recording ledger (its logger is never read).</summary>
    private static CancelRenderCommandHandler Handler(AppDbContext db, RecordingCreditLedger ledger) =>
        new(db, ledger, new FakeClock(Start), NullLogger<CancelRenderCommandHandler>.Instance);

    /// <summary>The one job the handler loaded, so its domain events and in-memory state can be asserted.</summary>
    private static RenderJob TrackedJob(AppDbContext db) =>
        db.ChangeTracker.Entries<RenderJob>().Select(entry => entry.Entity).ShouldHaveSingleItem();

    /// <summary>The stored <c>params</c> and <c>quality_report</c> of one row, joined for a byte comparison.</summary>
    private async Task<string> SnapshotAsync(Guid jobId) =>
        await ScalarAsync($"SELECT params::text || '|' || quality_report::text FROM render_jobs WHERE id = '{jobId}'");

    private async Task<string> ScalarAsync(string sql) =>
        (await Database.QueryStringsAsync(sql, Ct)).Single();

    /// <summary>
    /// One owner with a <c>rendering</c> episode, its story version and one <c>render_jobs</c> row in
    /// <paramref name="status"/>: written as raw SQL so any state of the status table is reachable, with a real
    /// <c>render-params.v1</c> document because every member is <c>[JsonRequired]</c> (the ingest test's seed shape).
    /// </summary>
    private async Task<World> SeedAsync(
        string status,
        string? qualityReport = null)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, new FakeClock(Start))
            .CreateAsync(user.Id, series.Id, status: EpisodeStatus.Rendering, ct: Ct);

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

        var jobId = Guid.CreateVersion7();
        var reservationId = Guid.CreateVersion7();
        var report = qualityReport is null ? "NULL" : $"'{qualityReport}'::jsonb";

        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, worst_case_usd, next_poll_at, cancel_requested_at, quality_report) "
            + $"VALUES ('{jobId}', '{user.Id}', '{episode.Id}', '{version.Id}', '{reservationId}', '{status}', "
            + $"'{RenderJob.QueuedStage}', 0, '{ParamsJson(version.Id)}'::jsonb, 1, 1.200000, NULL, NULL, {report})",
            Ct);

        return new World(user.Id, episode.Id, version.Id, jobId, reservationId);
    }

    /// <summary>The ids one seeded render world carries.</summary>
    private sealed record World(
        Guid UserId,
        Guid EpisodeId,
        Guid StoryVersionId,
        Guid RenderJobId,
        Guid ReservationId);

    /// <summary>
    /// A complete <c>render-params.v1</c> document (05 D11): the row could not be loaded without one because every
    /// member is <c>[JsonRequired]</c>.
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

    /// <summary>
    /// The one money port this command reaches, as a recorder: only <see cref="ICreditLedger.RefundAsync"/> is ever
    /// called (the reserve and the settle belong to the submit and the ingest), and the recorded
    /// <c>(reservationId, reason)</c> pairs are what the refund assertions are keyed on (09.6.3-T1's recorder shape).
    /// </summary>
    private sealed class RecordingCreditLedger : ICreditLedger
    {
        public List<(Guid ReservationId, string Reason)> Refunds { get; } = [];

        /// <summary>Every ledger call, in order — empty when the cancel never touched the ledger at all.</summary>
        public List<string> Calls { get; } = [];

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct) =>
            throw new NotSupportedException("a cancel never reserves");

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            throw new NotSupportedException("a cancel never settles: the refund is the release");

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            Calls.Add("refund");
            Refunds.Add((reservationId.Value, reason));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
