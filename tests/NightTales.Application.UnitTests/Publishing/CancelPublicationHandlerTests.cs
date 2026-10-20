using System.Globalization;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common;
using NightTales.Application.Features.Publishing.Commands.CancelPublication;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// The cancel command of 06.2.6-T1 (task §Tests, 06 README D7/D10/D13): which states cancel and which answer 409,
/// the idempotent repeat that survives a stale <c>If-Match</c>, the 412/404 refusals that write nothing, and the
/// quota release that happens only when the row still carries a reserved day (D10 — a unit is given back only for
/// an insert YouTube never received).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests need a real database.</b> The handler reads the publication through the tenant filter under
/// the row lock and writes it through the real write model, whose outbox interceptor stores
/// <c>publication_updated.v1</c> beside the row: the 404, the lock and the outbox row are all the real model's
/// answers, so a fake database would assert the wrong thing. The class therefore derives from
/// <see cref="IntegrationTest"/> like its <c>ConsentPublicationHandlerTests</c> sibling.
/// </para>
/// <para>
/// <b>Why the ledger is a fake here.</b> The release <em>call</em> is this command's own decision: with the key
/// present exactly one <see cref="QuotaReservation"/> of the row's platform and the reserved day must be handed
/// back, with the key absent or unreadable none at all, and a ledger refusal must fail the command untouched.
/// A recording <see cref="IQuotaLedger"/> pins those calls exactly; the real ledger's row arithmetic
/// (<c>units_used</c> back to 0, never negative, no row for a past day) is the Api integration test's, which owns
/// a real <c>platform_quota_usage</c> table.
/// </para>
/// <para>
/// <b>What the unit level cannot see.</b> The handler writes the aggregate and leaves the save to the pipeline's
/// <c>UnitOfWorkBehavior</c>, so <see cref="CancelAsync"/> does what that behavior does inside the request's
/// transaction — <c>Handle</c>, then <c>SaveChangesAsync</c> and the commit — and rolls back on every refusal. The
/// <c>audit_logs</c> row is written by the pipeline's <c>AuditBehavior</c>, which these tests call without: they
/// assert the command's own projection (<see cref="CancelPublicationCommand.Outcome"/>) instead, and the row itself
/// belongs to the Api's <c>CancelPublicationTests</c>.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
public sealed class CancelPublicationHandlerTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The frozen instant every test's clock reads.</summary>
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The Pacific quota day a reserving row would have written (06 README D10).</summary>
    private static readonly DateOnly ReservedDay = new(2026, 3, 1);

    [Theory]
    [InlineData(PublicationStatus.AwaitingConsent)]
    [InlineData(PublicationStatus.Scheduled)]
    public async Task CancelsFromTheTwoCancellableStates_AndSetsThePreviousStatus(PublicationStatus status)
    {
        // D7: both states before the upload may be cancelled, from the app or from the veto-list row. The audit
        // projection carries the wire value of the state the row left, which is what the UI shows the owner.
        var scene = await NewSceneAsync(status);

        var run = await CancelAsync(scene);

        run.Result.IsError.ShouldBeFalse();

        var outcome = run.Command.Outcome.ShouldNotBeNull();

        outcome.PublicationId.ShouldBe(scene.PublicationId);
        outcome.EpisodeId.ShouldBe(scene.EpisodeId);
        outcome.PreviousStatus.ShouldBe(PublicationBuilder.Wire(status));

        (await ScalarAsync(Column(scene, "status"))).ShouldBe("cancelled");
        (await ScalarAsync(Column(scene, $"(updated_at = '{Now:O}'::timestamptz)::text")))
            .ShouldBe("true", "the cancel stamps the command clock's instant");

        var payload = await SingleOutboxPayloadAsync(scene);

        // One transition, one event: the outbox row 06.2.2-T3/02 read to stop any pending work (D13).
        using (var json = JsonDocument.Parse(payload))
        {
            json.RootElement.GetProperty("status").GetString().ShouldBe("cancelled");
            json.RootElement.GetProperty("publicationId").GetGuid().ShouldBe(scene.PublicationId);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReleasesQuotaOnlyWhenKeyPresent(bool keyPresent)
    {
        // D10: the key is written by 06.2.3-T2 when it reserves and cleared when it releases or finishes, so its
        // presence means the unit is still outstanding and the cancel must hand it back — exactly once. Without
        // the key the row never reached the upload's reserve step and there is nothing to give back. The stored day
        // is deliberately NOT the clock's Pacific day (the clock reads 2026-03-02, the payload says 2026-03-01), so
        // the one-call assertion proves the day was read out of the payload rather than taken from the clock.
        var scene = await NewSceneAsync(
            PublicationStatus.Scheduled,
            platformPayloadJson: keyPresent ? PayloadWithQuotaDay(ReservedDay) : null);
        var ledger = new RecordingQuotaLedger();

        var run = await CancelAsync(scene, ledger: ledger);

        run.Result.IsError.ShouldBeFalse();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("cancelled");

        if (keyPresent)
        {
            ledger.Released.Count.ShouldBe(1);
            ledger.Released[0].ShouldBe(new QuotaReservation(PublicationBuilder.Platform, ReservedDay));
        }
        else
        {
            ledger.Released.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"2026-03-01\"")]
    [InlineData("{\"quotaReservedDay\": 20260301}")]
    [InlineData("{\"quotaReservedDay\": {\"day\": \"2026-03-01\"}}")]
    [InlineData("{\"quotaReservedDay\": \"2026/03/01\"}")]
    [InlineData("{\"quotaReservedDay\": \"2026-02-30\"}")]
    [InlineData("{\"quotaReservedDay\": \"yesterday\"}")]
    [InlineData("{}")]
    [InlineData("{\"preAudit\": true, \"requestedPrivacy\": \"private\"}")]
    public async Task UnreadableQuotaKey_IsTreatedAsAbsent_AndTheCancelStillSucceeds(string payload)
    {
        // Task §Failure handling: a hand-edited row or a format change by 06.2.3-T2 must never fail the owner's
        // cancel. The column is jsonb, so a truncated document cannot exist in it; what can are a non-object
        // document, a key of the wrong JSON kind and a string that is not the ISO day — all read as "nothing to
        // release" while the row still moves to cancelled. A JSON null under the key is not one of them: the
        // writer removes the key instead of storing a null, so null is plain absence and
        // CancelPublicationQaTests pins that it neither releases nor warns.
        var scene = await NewSceneAsync(PublicationStatus.Scheduled, platformPayloadJson: payload);
        var ledger = new RecordingQuotaLedger();

        var run = await CancelAsync(scene, ledger: ledger);

        run.Result.IsError.ShouldBeFalse();
        run.Command.Outcome.ShouldNotBeNull();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("cancelled");
        ledger.Released.ShouldBeEmpty();
    }

    [Fact]
    public async Task StaleIfMatch_HasNoSideEffects()
    {
        var scene = await NewSceneAsync(PublicationStatus.Scheduled, PayloadWithQuotaDay(ReservedDay));
        var stale = ETags.Publication(
            uint.Parse(await ScalarAsync(Column(scene, "xmin::text")), CultureInfo.InvariantCulture) - 1);
        var ledger = new RecordingQuotaLedger();
        var updatedAt = await ScalarAsync(Column(scene, "updated_at::text"));

        var run = await CancelAsync(scene, ifMatch: stale, ledger: ledger);

        // 412 publication_conflict before any write: the row, its stamp and the quota are all untouched and there
        // is nothing to audit.
        run.Result.IsError.ShouldBeTrue();
        run.Result.FirstError.Code.ShouldBe(PublicationErrors.ConflictCode);
        run.Command.Outcome.ShouldBeNull();
        ledger.Released.ShouldBeEmpty();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("scheduled");
        (await ScalarAsync(Column(scene, "updated_at::text"))).ShouldBe(updatedAt);
        (await OutboxPayloadsAsync(scene)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AlreadyCancelled_IsIdempotent_WithoutReleaseOrOutcome()
    {
        // Task §Edge cases: a DELETE retried after a lost response carries the tag the client read before the first
        // cancel, which names no version of the row any more. The repeat therefore answers 204 before the ETag
        // comparison, writes nothing, raises no second event and — even though the payload still names a reserved
        // day — hands nothing back a second time.
        var scene = await NewSceneAsync(
            PublicationStatus.Cancelled,
            PayloadWithQuotaDay(ReservedDay));
        var stale = ETags.Publication(
            uint.Parse(await ScalarAsync(Column(scene, "xmin::text")), CultureInfo.InvariantCulture) - 1);
        var ledger = new RecordingQuotaLedger();
        var updatedAt = await ScalarAsync(Column(scene, "updated_at::text"));

        var run = await CancelAsync(scene, ifMatch: stale, ledger: ledger);

        run.Result.IsError.ShouldBeFalse();
        run.Command.Outcome.ShouldBeNull();
        ledger.Released.ShouldBeEmpty();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("cancelled");
        (await ScalarAsync(Column(scene, "updated_at::text"))).ShouldBe(updatedAt);
        (await OutboxPayloadsAsync(scene)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Uploading_IsRefused_WithoutReleaseOrWrite()
    {
        // D7: once the upload runs there is a video to take down rather than a job to stop, so the answer is the
        // 409 that points the UI at YouTube Studio — the row keeps `uploading` and the reserved unit is NOT
        // released (YouTube already received the insert).
        var scene = await NewSceneAsync(PublicationStatus.Uploading, PayloadWithQuotaDay(ReservedDay));
        var ledger = new RecordingQuotaLedger();
        var updatedAt = await ScalarAsync(Column(scene, "updated_at::text"));

        var run = await CancelAsync(scene, ledger: ledger);

        run.Result.IsError.ShouldBeTrue();
        run.Result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        run.Command.Outcome.ShouldBeNull();
        ledger.Released.ShouldBeEmpty();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("uploading");
        (await ScalarAsync(Column(scene, "updated_at::text"))).ShouldBe(updatedAt);
        (await OutboxPayloadsAsync(scene)).ShouldBeEmpty();
    }

    [Fact]
    public async Task UnknownId_IsNotFound()
    {
        // The load runs through the tenant filter, so an id that names nothing at all takes the same 404 another
        // owner's id gets (the Api test proves the foreign-owner half on the route).
        var scene = await NewSceneAsync(PublicationStatus.Scheduled, PayloadWithQuotaDay(ReservedDay));
        var ledger = new RecordingQuotaLedger();

        var run = await CancelAsync(scene, publicationId: Guid.CreateVersion7(), ledger: ledger);

        run.Result.IsError.ShouldBeTrue();
        run.Result.FirstError.Code.ShouldBe("not_found");
        run.Command.Outcome.ShouldBeNull();
        ledger.Released.ShouldBeEmpty();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("scheduled");
    }

    [Fact]
    public async Task LedgerFailure_FailsTheCommand_AndLeavesTheRowUncancelled()
    {
        // Task §Failure handling: a ledger that cannot give the unit back fails the whole command, so the
        // transaction rolls back and the row is never reported as cancelled — the owner retries instead of
        // believing a cancel that never reached the ledger.
        var scene = await NewSceneAsync(PublicationStatus.Scheduled, PayloadWithQuotaDay(ReservedDay));
        var ledger = new RecordingQuotaLedger(
            Error.Failure("youtube_quota_release_failed", "The quota ledger could not release the reservation."));
        var updatedAt = await ScalarAsync(Column(scene, "updated_at::text"));

        var run = await CancelAsync(scene, ledger: ledger);

        run.Result.IsError.ShouldBeTrue();
        run.Result.FirstError.Code.ShouldBe("youtube_quota_release_failed");
        run.Command.Outcome.ShouldBeNull();
        ledger.Released.Count.ShouldBe(1);
        ledger.Released[0].ShouldBe(new QuotaReservation(PublicationBuilder.Platform, ReservedDay));

        (await ScalarAsync(Column(scene, "status"))).ShouldBe("scheduled");
        (await ScalarAsync(Column(scene, "updated_at::text"))).ShouldBe(updatedAt);
        (await OutboxPayloadsAsync(scene)).ShouldBeEmpty();
    }

    [Fact]
    public async Task EpisodeGone_IsNotFound()
    {
        // Contract step 1: a publication whose episode was cancelled reads as gone, so the answer is the 404 an
        // unknown id gets — the row is not cancellable through this route and no quota is released for it.
        var scene = await NewSceneAsync(
            PublicationStatus.Scheduled,
            PayloadWithQuotaDay(ReservedDay),
            EpisodeStatus.Cancelled);
        var ledger = new RecordingQuotaLedger();

        var run = await CancelAsync(scene, ledger: ledger);

        run.Result.IsError.ShouldBeTrue();
        run.Result.FirstError.Code.ShouldBe("not_found");
        run.Command.Outcome.ShouldBeNull();
        ledger.Released.ShouldBeEmpty();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("scheduled");
    }

    /// <summary>The rows of one scene, as the tests address them.</summary>
    private sealed record Scene(TestCurrentUser CurrentUser, Guid EpisodeId, Guid PublicationId);

    /// <summary>One command's result and the command itself, so its audit projection can be asserted.</summary>
    private sealed record CancelRun(ErrorOr<Deleted> Result, CancelPublicationCommand Command);

    /// <summary>
    /// Seeds one user, series, episode, active connection and the episode's publication in <paramref name="status"/>.
    /// The publication goes in through <see cref="PublicationBuilder"/>'s raw insert, which writes no outbox row and
    /// reaches the statuses no factory produces: each test counts only the messages its own command raised.
    /// </summary>
    /// <param name="status">The state of the seeded publication.</param>
    /// <param name="platformPayloadJson">The row's <c>platform_payload</c>; <see langword="null"/> writes no key.</param>
    /// <param name="episodeStatus">The state of the episode the publication belongs to.</param>
    private async Task<Scene> NewSceneAsync(
        PublicationStatus status,
        string? platformPayloadJson = null,
        EpisodeStatus episodeStatus = EpisodeStatus.Ready)
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.CreateVersion7();
        var currentUser = new TestCurrentUser { Id = userId };

        await new UserBuilder(Database, currentUser).CreateAsync(userId, ct: ct);

        var series = await new SeriesBuilder(Database, currentUser).CreateAsync(userId, ct: ct);

        var episode = await new EpisodeBuilder(Database, currentUser)
            .CreateAsync(userId, series.Id, status: episodeStatus, ct: ct);

        var connection = await new ChannelConnectionBuilder(Database, currentUser)
            .CreateAsync(userId, ChannelConnectionStatus.Active, ct: ct);

        var publicationId = await new PublicationBuilder(Database).CreateAsync(
            userId,
            episode.Id,
            connection.Id,
            status,
            platformPayloadJson: platformPayloadJson,
            ct: ct);

        return new Scene(currentUser, episode.Id, publicationId);
    }

    /// <summary>Sends the command the way the route would, through the handler under test.</summary>
    /// <param name="scene">The seeded rows.</param>
    /// <param name="publicationId">The id on the command; the scene's own unless a test targets an unknown one.</param>
    /// <param name="ifMatch">The header; the row's current tag unless a test sends a stale value.</param>
    /// <param name="ledger">The recording ledger; a fresh one per call when a test does not inspect it.</param>
    private async Task<CancelRun> CancelAsync(
        Scene scene,
        Guid? publicationId = null,
        string? ifMatch = null,
        RecordingQuotaLedger? ledger = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock(Now);

        await using var context = Database.CreateAppDbContext(scene.CurrentUser, clock);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        var command = new CancelPublicationCommand(
            publicationId ?? scene.PublicationId,
            ifMatch ?? await EtagAsync(scene));

        var handler = new CancelPublicationHandler(
            context,
            ledger ?? new RecordingQuotaLedger(),
            clock,
            NullLogger<CancelPublicationHandler>.Instance);

        var result = await handler.Handle(command, ct);

        if (result.IsError)
        {
            await transaction.RollbackAsync(ct);
        }
        else
        {
            // The handler writes the aggregate and leaves the save to the pipeline's UnitOfWorkBehavior, so the
            // test does exactly what that behavior does inside the request's transaction: save (which is where the
            // outbox interceptor stores publication_updated.v1) and commit.
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        return new CancelRun(result, command);
    }

    /// <summary>The row's current strong ETag (<c>"pu-&lt;xmin&gt;"</c>).</summary>
    private async Task<string> EtagAsync(Scene scene) =>
        ETags.Publication(uint.Parse(
            await ScalarAsync(Column(scene, "xmin::text")),
            CultureInfo.InvariantCulture));

    /// <summary>The <c>publication_updated.v1</c> payloads this scene's publication raised.</summary>
    private Task<IReadOnlyList<string>> OutboxPayloadsAsync(Scene scene) =>
        Database.QueryStringsAsync(
            "SELECT payload::text FROM outbox_messages WHERE type = 'publication_updated.v1' "
            + $"AND payload::text LIKE '%{scene.PublicationId:D}%'",
            TestContext.Current.CancellationToken);

    /// <summary>The one outbox payload the command must have raised.</summary>
    private async Task<string> SingleOutboxPayloadAsync(Scene scene)
    {
        var payloads = await OutboxPayloadsAsync(scene);

        payloads.Count.ShouldBe(1);

        return payloads[0];
    }

    /// <summary>One expression of the scene's own publication row.</summary>
    private static string Column(Scene scene, string expression) =>
        $"SELECT {expression} FROM publications WHERE id = '{scene.PublicationId:D}'";

    private async Task<string> ScalarAsync(string sql)
    {
        var rows = await Database.QueryStringsAsync(sql, TestContext.Current.CancellationToken);

        rows.Count.ShouldBe(1);

        return rows[0];
    }

    /// <summary>The <c>platform_payload</c> a reserving row carries (06.2.3-T2's key contract).</summary>
    private static string PayloadWithQuotaDay(DateOnly day) =>
        JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["quotaReservedDay"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });

    /// <summary>
    /// The ledger of these tests: it records every reservation handed back and answers with a success, or with the
    /// failure a test injected. A reservation is a defect here — the cancel path never reserves — so
    /// <see cref="TryReserveAsync"/> throws instead of silently counting one.
    /// </summary>
    private sealed class RecordingQuotaLedger(Error? releaseFailure = null) : IQuotaLedger
    {
        private readonly List<QuotaReservation> _released = [];

        /// <summary>Every reservation <see cref="ReleaseAsync"/> was handed, in order.</summary>
        internal List<QuotaReservation> Released => _released;

        public Task<ErrorOr<QuotaReservation>> TryReserveAsync(
            string platform,
            DateTimeOffset now,
            CancellationToken ct = default) =>
            throw new NotSupportedException("The cancel command never reserves quota.");

        public Task<ErrorOr<Success>> ReleaseAsync(QuotaReservation reservation, CancellationToken ct = default)
        {
            _released.Add(reservation);

            if (releaseFailure is null)
            {
                return Task.FromResult<ErrorOr<Success>>(Result.Success);
            }

            return Task.FromResult<ErrorOr<Success>>(releaseFailure.Value);
        }
    }
}
