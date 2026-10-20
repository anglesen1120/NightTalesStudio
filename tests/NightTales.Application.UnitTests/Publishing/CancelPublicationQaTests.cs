using System.Globalization;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
/// later-state refusals beyond <c>uploading</c>, a retry after a failed quota release, the release targeting the
/// day the row recorded rather than the clock day, and which reserved-day payloads warn: a JSON null (absence, no
/// line) versus a key that is there and unusable (one warning, no release). Helpers are copied from
/// <c>CancelPublicationHandlerTests</c>, whose copies are private.
/// </summary>
public sealed class CancelPublicationQaTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The frozen instant every test's clock reads.</summary>
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The quota day a reserving row would have written (06 README D10).</summary>
    private static readonly DateOnly ReservedDay = new(2026, 3, 1);

    /// <summary>
    /// The id of the line <c>PublicationQuotaRelease</c> writes for a reserved-day key that is there and unusable
    /// (06.2.6-T1; the next free id after the cancel handler's 6503/6504).
    /// </summary>
    private const int UnreadableReservedDayEventId = 6505;

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

    [Fact]
    public async Task JsonNullQuotaKey_IsAbsent_WithoutReleaseOrWarning()
    {
        // 06.2.3-T2 removes the key instead of storing a null, so a null under it is a row with nothing reserved:
        // no ledger call, and — unlike a value that is there and unusable — no warning line either.
        var scene = await NewSceneAsync(PublicationStatus.Scheduled, """{"quotaReservedDay": null}""");
        var ledger = new RecordingQuotaLedger();
        var logger = new RecordingLogger();

        var run = await CancelAsync(scene, ledger: ledger, logger: logger);

        run.Result.IsError.ShouldBeFalse();
        run.Command.Outcome.ShouldNotBeNull();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("cancelled");
        ledger.Released.ShouldBeEmpty();
        logger.Entries.Where(entry => entry.EventId.Id == UnreadableReservedDayEventId).ShouldBeEmpty();
        logger.Entries.Where(entry => entry.Level >= LogLevel.Warning).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"quotaReservedDay\": 20260301}")]
    [InlineData("{\"quotaReservedDay\": \"yesterday\"}")]
    public async Task MalformedQuotaKey_WarnsOnce_AndReleasesNothing(string payload)
    {
        // The other half of the same rule: the key is there and cannot be used, so the release is skipped and the
        // one line that says so carries the row's id. Exactly one: the payload is parsed once per cancel.
        var scene = await NewSceneAsync(PublicationStatus.Scheduled, payload);
        var ledger = new RecordingQuotaLedger();
        var logger = new RecordingLogger();

        var run = await CancelAsync(scene, ledger: ledger, logger: logger);

        run.Result.IsError.ShouldBeFalse();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("cancelled");
        ledger.Released.ShouldBeEmpty();

        var unreadable = logger.Entries.Where(entry => entry.EventId.Id == UnreadableReservedDayEventId).ToArray();

        unreadable.Length.ShouldBe(1);
        unreadable[0].Level.ShouldBe(LogLevel.Warning);
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
        RecordingQuotaLedger? ledger = null,
        ILogger<CancelPublicationHandler>? logger = null)
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
            logger ?? NullLogger<CancelPublicationHandler>.Instance);

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

    /// <summary>One captured line: its level, its event id and its rendered message.</summary>
    /// <param name="Level">The level the line was written at.</param>
    /// <param name="EventId">The <c>LoggerMessage</c> id the line was written under.</param>
    /// <param name="Message">The rendered message.</param>
    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    /// <summary>
    /// Keeps every line the handler wrote, with its id and level, so a test can tell the warning about an unusable
    /// reserved day from the handler's own success line.
    /// </summary>
    private sealed class RecordingLogger : ILogger<CancelPublicationHandler>
    {
        private readonly List<LogEntry> _entries = [];

        /// <summary>Every line, in the order the handler wrote it.</summary>
        internal IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            _entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }
}
