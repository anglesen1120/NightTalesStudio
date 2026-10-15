using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common.Errors;
using NightTales.Application.Publishing;
using NightTales.Application.Telemetry;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The YouTube upload quota ledger (06.3.1-T1) against the class Postgres: seeding, the atomic reserve,
/// the Pacific day boundary (both DST sides), release, and the gauge and log contract.
/// </summary>
public sealed class QuotaLedgerTests(TestDatabase database) : IntegrationTest(database)
{
    private const string GaugeName = "youtube_quota_used";
    private static readonly DateTimeOffset Midday = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly JanuaryFifteenth = new(2026, 1, 15);

    private readonly FakeClock _clock = new(Midday);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task QuotaLedger_Reserve_SeedsRowWithConfiguredLimit()
    {
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(7);
        await using var db = Database.CreateAppDbContext(clock: _clock);

        var result = await Ledger(db, options).TryReserveAsync(platform, Midday, Ct);

        result.IsError.ShouldBeFalse();
        var row = await ReadRowAsync(platform, JanuaryFifteenth);
        row.UnitsUsed.ShouldBe(1);
        row.UnitsLimit.ShouldBe(7);
    }

    [Fact]
    public async Task QuotaLedger_Reserve_IncrementsUntilLimit()
    {
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(3);
        await using var db = Database.CreateAppDbContext(clock: _clock);
        var ledger = Ledger(db, options);

        for (var i = 0; i < 3; i++)
        {
            (await ledger.TryReserveAsync(platform, Midday, Ct)).IsError.ShouldBeFalse();
        }

        var fourth = await ledger.TryReserveAsync(platform, Midday, Ct);

        fourth.IsError.ShouldBeTrue();
        (await ReadRowAsync(platform, JanuaryFifteenth)).UnitsUsed.ShouldBe(3);
    }

    [Fact]
    public async Task QuotaLedger_Reserve_AtLimit_ReturnsRetryAfterToNextPacificMidnight()
    {
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(1);
        await using var db = Database.CreateAppDbContext(clock: _clock);
        var ledger = Ledger(db, options);
        (await ledger.TryReserveAsync(platform, Midday, Ct)).IsError.ShouldBeFalse();

        var exhausted = await ledger.TryReserveAsync(platform, Midday, Ct);

        exhausted.IsError.ShouldBeTrue();
        exhausted.FirstError.Code.ShouldBe(ErrorCodes.YoutubeQuotaExhausted);
        // 2026-01-15 12:00Z is 04:00 PST; Pacific midnight of the next day is 2026-01-16T08:00Z: 20 hours.
        exhausted.FirstError.Metadata!.ShouldContainKeyAndValue(ErrorMetadata.RetryAfterSeconds, 72000);
    }

    [Fact]
    public async Task QuotaLedger_Reserve_ConcurrentLastUnit_OnlyOneSucceeds()
    {
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(1);
        await using var first = Database.CreateAppDbContext(clock: _clock);
        await using var second = Database.CreateAppDbContext(clock: _clock);

        var results = await Task.WhenAll(
            Ledger(first, options).TryReserveAsync(platform, Midday, Ct),
            Ledger(second, options).TryReserveAsync(platform, Midday, Ct));

        results.Count(r => !r.IsError).ShouldBe(1);
        var loser = results.Single(r => r.IsError);
        loser.FirstError.Code.ShouldBe(ErrorCodes.YoutubeQuotaExhausted);
        (await ReadRowAsync(platform, JanuaryFifteenth)).UnitsUsed.ShouldBe(1);
    }

    [Fact]
    public async Task QuotaLedger_Reserve_AfterPacificMidnight_UsesNewDay()
    {
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(1);
        await using var db = Database.CreateAppDbContext(clock: _clock);
        var ledger = Ledger(db, options);
        (await ledger.TryReserveAsync(platform, Midday, Ct)).IsError.ShouldBeFalse();

        // 2026-01-16T08:00Z is midnight Pacific Standard Time: a new quota day starts.
        var nextDay = await ledger.TryReserveAsync(platform, new DateTimeOffset(2026, 1, 16, 8, 0, 0, TimeSpan.Zero), Ct);

        nextDay.IsError.ShouldBeFalse();
        (await ReadRowAsync(platform, JanuaryFifteenth)).UnitsUsed.ShouldBe(1);
        (await ReadRowAsync(platform, new DateOnly(2026, 1, 16))).UnitsUsed.ShouldBe(1);
    }

    [Theory]
    [InlineData("2026-03-08T12:00:00Z", "2026-03-08", "2026-03-09T07:00:00Z", 68400)]
    [InlineData("2026-11-01T12:00:00Z", "2026-11-01", "2026-11-02T08:00:00Z", 72000)]
    public async Task QuotaLedger_Reserve_OnDstBoundary_ResetsAtPacificMidnightOfThatDay(
        string exhaustedAt,
        string day,
        string resetAt,
        int expectedRetryAfterSeconds)
    {
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(1);
        var at = DateTimeOffset.Parse(exhaustedAt, System.Globalization.CultureInfo.InvariantCulture);
        var reset = DateTimeOffset.Parse(resetAt, System.Globalization.CultureInfo.InvariantCulture);
        await using var db = Database.CreateAppDbContext(clock: _clock);
        var ledger = Ledger(db, options);
        (await ledger.TryReserveAsync(platform, at, Ct)).IsError.ShouldBeFalse();

        var exhausted = await ledger.TryReserveAsync(platform, at, Ct);

        exhausted.FirstError.Metadata!.ShouldContainKeyAndValue(ErrorMetadata.RetryAfterSeconds, expectedRetryAfterSeconds);
        (await ReadRowAsync(platform, DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture))).UnitsUsed.ShouldBe(1);
        (await ledger.TryReserveAsync(platform, reset, Ct)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task QuotaLedger_Reserve_KeepsExistingLimitWhenOptionChangesMidDay()
    {
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(1);
        await using var db = Database.CreateAppDbContext(clock: _clock);
        var ledger = Ledger(db, options);
        (await ledger.TryReserveAsync(platform, Midday, Ct)).IsError.ShouldBeFalse();

        options.CurrentValue = new YouTubeOptions { Quota = new YouTubeQuotaOptions { UnitsPerDay = 5 } };
        var second = await ledger.TryReserveAsync(platform, Midday, Ct);

        // The limit is read once, when the day's row is seeded: the mid-day option change does not apply.
        second.IsError.ShouldBeTrue();
        second.FirstError.Code.ShouldBe(ErrorCodes.YoutubeQuotaExhausted);
        (await ReadRowAsync(platform, JanuaryFifteenth)).UnitsLimit.ShouldBe(1);
    }

    [Fact]
    public async Task QuotaLedger_Release_Decrements()
    {
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(2);
        await using var db = Database.CreateAppDbContext(clock: _clock);
        var ledger = Ledger(db, options);
        var reservation = (await ledger.TryReserveAsync(platform, Midday, Ct)).Value;
        (await ledger.TryReserveAsync(platform, Midday, Ct)).IsError.ShouldBeFalse();

        var released = await ledger.ReleaseAsync(reservation, Ct);

        released.IsError.ShouldBeFalse();
        (await ReadRowAsync(platform, JanuaryFifteenth)).UnitsUsed.ShouldBe(1);
        (await ledger.TryReserveAsync(platform, Midday, Ct)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task QuotaLedger_Release_OnZeroOrMissingRow_IsNoOpSuccess()
    {
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(1);
        await using var db = Database.CreateAppDbContext(clock: _clock);
        var ledger = Ledger(db, options);
        var missing = new QuotaReservation(platform, JanuaryFifteenth);

        var onMissing = await ledger.ReleaseAsync(missing, Ct);

        onMissing.IsError.ShouldBeFalse();
        (await CountRowsAsync(platform, JanuaryFifteenth)).ShouldBe(0);

        var reservation = (await ledger.TryReserveAsync(platform, Midday, Ct)).Value;
        (await ledger.ReleaseAsync(reservation, Ct)).IsError.ShouldBeFalse();
        var onZero = await ledger.ReleaseAsync(reservation, Ct);

        onZero.IsError.ShouldBeFalse();
        (await ReadRowAsync(platform, JanuaryFifteenth)).UnitsUsed.ShouldBe(0);
    }

    [Fact]
    public async Task QuotaLedger_Gauge_EqualsUsedOverLimitAfterReserveAndRelease()
    {
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(4);
        var metrics = new NightTalesMetrics();
        var observed = new List<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == GaugeName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == GaugeName)
            {
                observed.Add(measurement);
            }
        });
        listener.Start();
        await using var db = Database.CreateAppDbContext(clock: _clock);
        var ledger = Ledger(db, options, metrics);

        var reservation = (await ledger.TryReserveAsync(platform, Midday, Ct)).Value;
        (await ledger.TryReserveAsync(platform, Midday, Ct)).IsError.ShouldBeFalse();
        observed.Clear();
        listener.RecordObservableInstruments();
        observed.ShouldContain(0.5);

        await ledger.ReleaseAsync(reservation, Ct);
        observed.Clear();
        listener.RecordObservableInstruments();
        observed.ShouldContain(0.25);
    }

    [Fact]
    public async Task QuotaLedger_Exhaustion_LogsOnlyPlatformAndQuotaDay()
    {
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(1);
        var lines = new List<string>();
        using var factory = new LoggerFactory([new CapturingLoggerProvider(lines)]);
        await using var db = Database.CreateAppDbContext(clock: _clock);
        var ledger = Ledger(db, options, logger: factory.CreateLogger<YouTubeQuotaLedger>());
        (await ledger.TryReserveAsync(platform, Midday, Ct)).IsError.ShouldBeFalse();

        (await ledger.TryReserveAsync(platform, Midday, Ct)).IsError.ShouldBeTrue();

        lines.ShouldHaveSingleItem().ShouldBe("publishing.quota.exhausted platform=youtube quota_day=2026-01-15");
    }

    private async Task<string> FreshPlatformAsync()
    {
        await using var db = Database.CreateAppDbContext();
        await db.PlatformQuotaUsage.ExecuteDeleteAsync(Ct);
        return "youtube";
    }

    private static YouTubeQuotaLedger Ledger(
        AppDbContext db,
        MutableYouTubeOptions options,
        NightTalesMetrics? metrics = null,
        ILogger<YouTubeQuotaLedger>? logger = null) =>
        new(db, options, metrics ?? new NightTalesMetrics(), logger ?? NullLogger<YouTubeQuotaLedger>.Instance);

    private async Task<PlatformQuotaUsage> ReadRowAsync(string platform, DateOnly day)
    {
        await using var db = Database.CreateAppDbContext(clock: _clock);
        return await db.PlatformQuotaUsage.AsNoTracking()
            .SingleAsync(r => r.Platform == platform && r.QuotaDay == day, Ct);
    }

    private async Task<int> CountRowsAsync(string platform, DateOnly day)
    {
        await using var db = Database.CreateAppDbContext(clock: _clock);
        return await db.PlatformQuotaUsage.AsNoTracking()
            .CountAsync(r => r.Platform == platform && r.QuotaDay == day, Ct);
    }

    /// <summary>A mutable options monitor: the test sets the value the ledger reads at seed time.</summary>
    private sealed class MutableYouTubeOptions(int unitsPerDay) : IOptionsMonitor<YouTubeOptions>
    {
        public YouTubeOptions CurrentValue { get; set; } =
            new() { Quota = new YouTubeQuotaOptions { UnitsPerDay = unitsPerDay } };

        public YouTubeOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<YouTubeOptions, string?> listener) => null;
    }

    /// <summary>Renders every line the ledger logs into a list, so a test can scan the formatted text.</summary>
    private sealed class CapturingLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Add(formatter(state, exception));
        }
    }
}
