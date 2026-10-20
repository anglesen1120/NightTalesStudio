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
/// Independent QA for task 06.2.6-T1 (cancel command). Pins behaviour the existing handler tests do not cover: the
/// later-state refusals beyond <c>uploading</c>, a retry after a failed quota release, and the release targeting the
/// day the row recorded rather than the clock day. Helpers are copied from <c>CancelPublicationHandlerTests</c>, whose
/// copies are private.
/// </summary>
public sealed class CancelPublicationQaTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The frozen instant every test's clock reads.</summary>
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The quota day a reserving row would have written (06 README D10).</summary>
    private static readonly DateOnly ReservedDay = new(2026, 3, 1);

    [Theory]
    [InlineData(PublicationStatus.Processing, "processing")]
    [InlineData(PublicationStatus.Published, "published")]
    [InlineData(PublicationStatus.Failed, "failed")]
    public async Task LaterStates_AreRefused_WithoutReleaseOrWrite(PublicationStatus status, string stored)
    {
        var scene = await NewSceneAsync(status, PayloadWithQuotaDay(ReservedDay));
        var ledger = new RecordingQuotaLedger();
        var updatedAt = await ScalarAsync(Column(scene, "updated_at::text"));

        var run = await CancelAsync(scene, ledger: ledger);

        run.Result.IsError.ShouldBeTrue();
        run.Result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        run.Command.Outcome.ShouldBeNull();
        ledger.Released.ShouldBeEmpty();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe(stored);
        (await ScalarAsync(Column(scene, "updated_at::text"))).ShouldBe(updatedAt);
    }

    [Fact]
    public async Task ReleaseFailure_LeavesTheRowUncancelled_AndTheRetryReleasesExactlyOnce()
    {
        var scene = await NewSceneAsync(PublicationStatus.Scheduled, PayloadWithQuotaDay(ReservedDay));
        var failing = new RecordingQuotaLedger(
            Error.Failure("youtube_quota_release_failed", "The quota ledger could not release the reservation."));

        var first = await CancelAsync(scene, ledger: failing);

        first.Result.IsError.ShouldBeTrue();
        first.Result.FirstError.Code.ShouldBe("youtube_quota_release_failed");
        first.Command.Outcome.ShouldBeNull();
        failing.Released.Count.ShouldBe(1);
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("scheduled");

        var healthy = new RecordingQuotaLedger();

        var retry = await CancelAsync(scene, ledger: healthy);

        retry.Result.IsError.ShouldBeFalse();
        retry.Command.Outcome.ShouldNotBeNull();
        healthy.Released.Count.ShouldBe(1);
        healthy.Released[0].ShouldBe(new QuotaReservation(PublicationBuilder.Platform, ReservedDay));
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("cancelled");
    }

    [Theory]
    [InlineData("2028-02-29")]
    [InlineData("2025-12-31")]
    public async Task ReleaseTargetsTheRecordedDay_NotTheClockDay(string day)
    {
        var recorded = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var scene = await NewSceneAsync(PublicationStatus.Scheduled, PayloadWithQuotaDay(recorded));
        var ledger = new RecordingQuotaLedger();

        var run = await CancelAsync(scene, ledger: ledger);

        run.Result.IsError.ShouldBeFalse();
        ledger.Released.Count.ShouldBe(1);
        ledger.Released[0].ShouldBe(new QuotaReservation(PublicationBuilder.Platform, recorded));
    }

    /// <summary>The rows of one scene, as the tests address them.</summary>
    private sealed record Scene(TestCurrentUser CurrentUser, Guid EpisodeId, Guid PublicationId);

    /// <summary>One command's result and the command itself, so its outcome projection can be asserted.</summary>
    private sealed record CancelRun(ErrorOr<Deleted> Result, CancelPublicationCommand Command);

    /// <summary>Seeds one user, series, episode, active connection and the episode's publication in <paramref name="status"/>.</summary>
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

    /// <summary>Runs the handler inside a transaction that commits on success and rolls back on error.</summary>
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

    /// <summary>One expression of the scene's own publication row.</summary>
    private static string Column(Scene scene, string expression) =>
        $"SELECT {expression} FROM publications WHERE id = '{scene.PublicationId:D}'";

    private async Task<string> ScalarAsync(string sql)
    {
        var rows = await Database.QueryStringsAsync(sql, TestContext.Current.CancellationToken);

        return rows.Single();
    }

    /// <summary>A <c>platform_payload</c> carrying the reservation key for <paramref name="day"/>.</summary>
    private static string PayloadWithQuotaDay(DateOnly day) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["quotaReservedDay"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });

    /// <summary>
    /// Records every reservation the release path is handed. Fails the release when <paramref name="releaseFailure"/>
    /// is set. A reservation is a defect on the cancel path, so <see cref="TryReserveAsync"/> throws.
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

            if (releaseFailure is not null)
            {
                return Task.FromResult<ErrorOr<Success>>(releaseFailure.Value);
            }

            return Task.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
