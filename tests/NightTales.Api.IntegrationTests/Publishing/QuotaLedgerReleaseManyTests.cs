using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Publishing;
using NightTales.Application.Telemetry;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The batch release of the YouTube quota ledger (06.4.1-T1 §Design step 6, STATUS S72-3) against the class
/// Postgres: a whole disconnect cascade hands its reservations to <see cref="IQuotaLedger.ReleaseManyAsync"/> at
/// once, and the adapter must answer with one statement per day and one gauge read-back — never one of each per
/// row, which is what the port's default (<c>ReleaseAsync</c> in a loop) costs.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is measured, and how.</b> The gauge is an observable instrument whose value the ledger <em>sets</em>
/// after reading the day back; one publication is therefore one read of <c>platform_quota_usage</c>, and the
/// commands EF sends are the observable evidence (the same interceptor the query-shape tests use). Counting
/// statements also pins the per-day grouping: two days are two <c>UPDATE</c>s, not one per reservation.
/// </para>
/// <para>
/// The single release's contract — the same rules the batch applies to a count — is <c>QuotaLedgerTests</c>'s;
/// this class owns the batch and repeats the table's own boundaries (zero, missing day, over-release) at batch
/// size. It is a separate class so the two helpers' <c>platform_quota_usage</c> resets never interleave.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
public sealed class QuotaLedgerReleaseManyTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Midday = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly JanuaryFifteenth = new(2026, 1, 15);

    /// <summary>
    /// 2026-01-16T20:00Z is 12:00 PST on the 16th: the next Pacific quota day, with nothing to do with UTC's day.
    /// </summary>
    private static readonly DateTimeOffset NextPacificDayMidday = new(2026, 1, 16, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly JanuarySixteenth = new(2026, 1, 16);

    private readonly FakeClock _clock = new(Midday);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReleaseMany_SameDay_ReleasesEveryUnit_WithOneStatementPerDayAndOneGaugeReadBack()
    {
        // Three cancelled publications of one channel: one reservation each on the same day. The batch must hand
        // back three units with ONE decrement and ONE gauge read-back, which is the whole reason S72-3 asked for a
        // batch instead of the port's per-row default.
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(5);
        var interceptor = new CountingDbCommandInterceptor();
        await using var db = CountingContext(interceptor);
        var ledger = Ledger(db, options);
        var reservations = new List<QuotaReservation>(3);

        for (var i = 0; i < 3; i++)
        {
            reservations.Add((await ledger.TryReserveAsync(platform, Midday, Ct)).Value);
        }

        (await ReadRowAsync(platform, JanuaryFifteenth)).UnitsUsed.ShouldBe(3);
        interceptor.Reset();

        var released = await ledger.ReleaseManyAsync(reservations, Ct);

        released.IsError.ShouldBeFalse();
        (await ReadRowAsync(platform, JanuaryFifteenth)).UnitsUsed.ShouldBe(0);
        interceptor.Texts.Count(IsQuotaUpdate).ShouldBe(1, "one day is one UPDATE, not one per reservation");
        interceptor.Texts.Count(IsQuotaRead).ShouldBe(1, "one gauge publication for the whole batch");
    }

    [Fact]
    public async Task ReleaseMany_TwoDays_UpdatesBothDays_AndStillPublishesOneGauge()
    {
        // The cascade can span Pacific midnight (a reservation taken before the rollover, the disconnect after it).
        // Both days must land in the same call, and the gauge still reports the clock's day once — the same ratio the
        // single release publishes (a release of yesterday's unit does not move today's number twice).
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(5);
        var interceptor = new CountingDbCommandInterceptor();
        await using var db = CountingContext(interceptor);
        var ledger = Ledger(db, options);
        var yesterday = new List<QuotaReservation>(2);

        for (var i = 0; i < 2; i++)
        {
            yesterday.Add((await ledger.TryReserveAsync(platform, Midday, Ct)).Value);
        }

        var today = (await ledger.TryReserveAsync(platform, NextPacificDayMidday, Ct)).Value;
        (await ReadRowAsync(platform, JanuaryFifteenth)).UnitsUsed.ShouldBe(2);
        (await ReadRowAsync(platform, JanuarySixteenth)).UnitsUsed.ShouldBe(1);

        var reservations = new List<QuotaReservation>(yesterday) { today };
        interceptor.Reset();

        var released = await ledger.ReleaseManyAsync(reservations, Ct);

        released.IsError.ShouldBeFalse();
        (await ReadRowAsync(platform, JanuaryFifteenth)).UnitsUsed.ShouldBe(0);
        (await ReadRowAsync(platform, JanuarySixteenth)).UnitsUsed.ShouldBe(0);
        interceptor.Texts.Count(IsQuotaUpdate).ShouldBe(2, "one UPDATE per distinct day");
        interceptor.Texts.Count(IsQuotaRead).ShouldBe(1, "the platform's gauge is still published once");
    }

    [Fact]
    public async Task ReleaseMany_DayWithNoRow_IsNoOpSuccess_AndNeverInserts()
    {
        // The bookkeeping may have nothing to undo: a past day's row is never re-created by a release (D10), because
        // an insert would resurrect a quota day that is already over with a limit read from today's options.
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(5);
        var interceptor = new CountingDbCommandInterceptor();
        await using var db = CountingContext(interceptor);
        var ledger = Ledger(db, options);

        var released = await ledger.ReleaseManyAsync([new QuotaReservation(platform, JanuaryFifteenth)], Ct);

        released.IsError.ShouldBeFalse();
        (await CountRowsAsync(platform, JanuaryFifteenth)).ShouldBe(0, "a release never inserts a row");
        interceptor.Texts.Count(IsQuotaUpdate).ShouldBe(1, "the guarded UPDATE is still the one statement");
    }

    [Fact]
    public async Task ReleaseMany_MoreUnitsThanUsed_ClampsAtZero()
    {
        // A batch larger than the counter's value releases what the day has and drops the rest: the guard is the
        // single release's clamp applied to the batch's count, so two extra reservations can never push
        // units_used negative and hand a negative limit to every later reader.
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(5);
        var interceptor = new CountingDbCommandInterceptor();
        await using var db = CountingContext(interceptor);
        var ledger = Ledger(db, options);
        var reservation = (await ledger.TryReserveAsync(platform, Midday, Ct)).Value;
        interceptor.Reset();

        var released = await ledger.ReleaseManyAsync(
            [reservation, new QuotaReservation(platform, JanuaryFifteenth), new QuotaReservation(platform, JanuaryFifteenth)],
            Ct);

        released.IsError.ShouldBeFalse();
        (await ReadRowAsync(platform, JanuaryFifteenth)).UnitsUsed.ShouldBe(0, "units_used never goes below zero");
    }

    [Fact]
    public async Task ReleaseMany_Empty_IsNoOpSuccess_TouchingNothing()
    {
        // Nothing cancelled means nothing to give back: no statement, no gauge read-back — a channel with no open
        // publication must not cost a round trip.
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(5);
        var interceptor = new CountingDbCommandInterceptor();
        await using var db = CountingContext(interceptor);
        var ledger = Ledger(db, options);
        (await ledger.TryReserveAsync(platform, Midday, Ct)).IsError.ShouldBeFalse();
        interceptor.Reset();

        var released = await ledger.ReleaseManyAsync([], Ct);

        released.IsError.ShouldBeFalse();
        interceptor.Count.ShouldBe(0, "an empty batch executes nothing");
        (await ReadRowAsync(platform, JanuaryFifteenth)).UnitsUsed.ShouldBe(1);
    }

    [Fact]
    public async Task ReleaseMany_StampsBothDaysUpdatedAtFromTheClock()
    {
        // One clock read for the whole batch: both decrements carry the release instant, never a per-row clock read
        // that could straddle the instant (and the write model's audit stamps come from the same clock elsewhere).
        var platform = await FreshPlatformAsync();
        var options = new MutableYouTubeOptions(5);
        await using var db = Database.CreateAppDbContext(clock: _clock);
        var ledger = Ledger(db, options);
        var yesterday = (await ledger.TryReserveAsync(platform, Midday, Ct)).Value;
        var today = (await ledger.TryReserveAsync(platform, NextPacificDayMidday, Ct)).Value;
        var releasedAt = new DateTimeOffset(2026, 1, 16, 21, 30, 0, TimeSpan.Zero);
        _clock.Set(releasedAt);

        var released = await ledger.ReleaseManyAsync([yesterday, today], Ct);

        released.IsError.ShouldBeFalse();
        (await ReadRowAsync(platform, JanuaryFifteenth)).UpdatedAt.ShouldBe(releasedAt);
        (await ReadRowAsync(platform, JanuarySixteenth)).UpdatedAt.ShouldBe(releasedAt);
    }

    /// <summary>Whether one recorded command is the gauge's read-back of <c>platform_quota_usage</c>.</summary>
    /// <param name="text">The SQL EF sent.</param>
    private static bool IsQuotaRead(string text) =>
        text.StartsWith("SELECT", StringComparison.Ordinal)
        && text.Contains("platform_quota_usage", StringComparison.Ordinal);

    /// <summary>Whether one recorded command is a decrement of <c>platform_quota_usage</c>.</summary>
    /// <param name="text">The SQL EF sent.</param>
    private static bool IsQuotaUpdate(string text) =>
        text.StartsWith("UPDATE", StringComparison.Ordinal)
        && text.Contains("platform_quota_usage", StringComparison.Ordinal);

    /// <summary>An empty ledger table, so one test's rows never reach the next.</summary>
    private async Task<string> FreshPlatformAsync()
    {
        await using var db = Database.CreateAppDbContext();
        await db.PlatformQuotaUsage.ExecuteDeleteAsync(Ct);

        return "youtube";
    }

    /// <summary>The ledger over the production provider options, with the interceptor attached.</summary>
    /// <param name="interceptor">The recorder of every command the context executes.</param>
    private AppDbContext CountingContext(CountingDbCommandInterceptor interceptor) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(Database.ConnectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(interceptor)
                .Options,
            new TestCurrentUser());

    private YouTubeQuotaLedger Ledger(
        AppDbContext db,
        MutableYouTubeOptions options,
        NightTalesMetrics? metrics = null,
        ILogger<YouTubeQuotaLedger>? logger = null) =>
        new(db, options, metrics ?? new NightTalesMetrics(), _clock, logger ?? NullLogger<YouTubeQuotaLedger>.Instance);

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
}
