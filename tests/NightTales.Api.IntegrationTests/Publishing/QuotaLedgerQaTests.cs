using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Api.Http;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common.Errors;
using NightTales.Application.Publishing;
using NightTales.Application.Publishing.Quota;
using NightTales.Application.Telemetry;
using NightTales.Infrastructure.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// QA of the YouTube upload quota ledger (06.3.1-T1): the daily-limit boundary, the exact Pacific-midnight instants,
/// concurrent reserves, release on zero, the Retry-After header of a refused reserve, and the ETA endpoint's
/// over-limit and flag-off behaviour.
/// </summary>
public sealed class QuotaLedgerQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Platform = "youtube";
    private const string FeatureKey = "youtube_publish";
    private const string EtaRoute = "/api/v1/publishing/youtube/eta";
    private static readonly DateTimeOffset February = new(2026, 2, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly FebruaryTwentieth = new(2026, 2, 20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task QuotaLedger_Qa_HundredthReserveSucceeds_HundredAndFirstIsRefusedWithRetryAfter()
    {
        await using (var reset = Database.CreateAppDbContext())
        {
            await reset.PlatformQuotaUsage
                .Where(r => r.Platform == Platform && r.QuotaDay == FebruaryTwentieth)
                .ExecuteDeleteAsync(Ct);
        }

        await using var db = Database.CreateAppDbContext(clock: new FakeClock(February));
        var ledger = NewLedger(db, unitsPerDay: 100);

        for (var i = 0; i < 100; i++)
        {
            (await ledger.TryReserveAsync(Platform, February, Ct)).IsError.ShouldBeFalse();
        }

        var refused = await ledger.TryReserveAsync(Platform, February, Ct);

        refused.IsError.ShouldBeTrue();
        refused.FirstError.Code.ShouldBe(ErrorCodes.YoutubeQuotaExhausted);
        // 2026-02-20 12:00Z is 04:00 PST; Pacific midnight of the next day is 2026-02-21T08:00Z: 20 hours.
        refused.FirstError.Metadata!.ShouldContainKeyAndValue(ErrorMetadata.RetryAfterSeconds, 72000);
    }

    [Fact]
    public async Task QuotaLedger_Qa_ReleaseOnZeroUnits_StaysZero_AndMissingRowReleaseCreatesNothing()
    {
        var day = new DateOnly(2026, 2, 24);
        var now = new DateTimeOffset(2026, 2, 24, 12, 0, 0, TimeSpan.Zero);
        await using var db = Database.CreateAppDbContext(clock: new FakeClock(now));
        var ledger = NewLedger(db, unitsPerDay: 100);

        var reservation = (await ledger.TryReserveAsync(Platform, now, Ct)).Value;
        (await ledger.ReleaseAsync(reservation, Ct)).IsError.ShouldBeFalse();
        (await ledger.ReleaseAsync(reservation, Ct)).IsError.ShouldBeFalse("a release on a zero row is a no-op success");

        var units = await Database.QueryStringsAsync(
            $"SELECT units_used::text FROM platform_quota_usage WHERE platform = '{Platform}' AND quota_day = '{Iso(day)}'",
            Ct);
        units.Single().ShouldBe("0");

        var missingDay = new DateOnly(2026, 2, 25);
        (await ledger.ReleaseAsync(new QuotaReservation(Platform, missingDay), Ct)).IsError.ShouldBeFalse();
        var rows = await Database.QueryStringsAsync(
            $"SELECT count(*)::text FROM platform_quota_usage WHERE platform = '{Platform}' AND quota_day = '{Iso(missingDay)}'",
            Ct);
        rows.Single().ShouldBe("0", "a release on a missing row creates no row");
    }

    [Fact]
    public async Task QuotaLedger_Qa_FiftyConcurrentReserves_LimitTen_ExactlyTenSucceed()
    {
        var day = new DateOnly(2026, 2, 23);
        var now = new DateTimeOffset(2026, 2, 23, 12, 0, 0, TimeSpan.Zero);
        await Database.ExecuteAsync(
            $"DELETE FROM platform_quota_usage WHERE platform = '{Platform}' AND quota_day = '{Iso(day)}'",
            Ct);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 50).Select(async _ =>
        {
            await gate.Task.WaitAsync(Ct);
            // A separate DI scope (its own DbContext and connection) per reserve, as separate requests would have.
            await using var db = Database.CreateAppDbContext(clock: new FakeClock(now));
            return await NewLedger(db, unitsPerDay: 10).TryReserveAsync(Platform, now, Ct);
        }).ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Count(r => !r.IsError).ShouldBe(10);
        results.Where(r => r.IsError).ShouldAllBe(r => r.FirstError.Code == ErrorCodes.YoutubeQuotaExhausted);
        var units = await Database.QueryStringsAsync(
            $"SELECT units_used::text FROM platform_quota_usage WHERE platform = '{Platform}' AND quota_day = '{Iso(day)}'",
            Ct);
        units.Single().ShouldBe("10", "no lost update and no 11th unit");
    }

    [Fact]
    public async Task QuotaLedger_Qa_PacificMidnightInstants_MapToDifferentDays_WithRetryAfterAtLeastOne()
    {
        await using var db = Database.CreateAppDbContext();
        var ledger = NewLedger(db, unitsPerDay: 1);

        // PST (February): 07:59:59.999Z is 23:59:59.999 on the 18th; 08:00:00Z is 00:00 on the 19th.
        var beforePst = DateTimeOffset.Parse("2026-02-19T07:59:59.999Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var atPst = DateTimeOffset.Parse("2026-02-19T08:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        (await ledger.TryReserveAsync(Platform, beforePst, Ct)).IsError.ShouldBeFalse();
        var refusedBeforePst = await ledger.TryReserveAsync(Platform, beforePst, Ct);
        refusedBeforePst.FirstError.Metadata!.ShouldContainKeyAndValue(ErrorMetadata.RetryAfterSeconds, 1);
        (await ledger.TryReserveAsync(Platform, atPst, Ct)).IsError.ShouldBeFalse("08:00:00Z opens the next Pacific day");

        // PDT (June): 06:59:59.999Z is 23:59:59.999 on the 14th; 07:00:00Z is 00:00 on the 15th.
        var beforePdt = DateTimeOffset.Parse("2026-06-15T06:59:59.999Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var atPdt = DateTimeOffset.Parse("2026-06-15T07:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        (await ledger.TryReserveAsync(Platform, beforePdt, Ct)).IsError.ShouldBeFalse();
        var refusedBeforePdt = await ledger.TryReserveAsync(Platform, beforePdt, Ct);
        refusedBeforePdt.FirstError.Metadata!.ShouldContainKeyAndValue(ErrorMetadata.RetryAfterSeconds, 1);
        (await ledger.TryReserveAsync(Platform, atPdt, Ct)).IsError.ShouldBeFalse("07:00:00Z opens the next Pacific day");

        var days = await Database.QueryStringsAsync(
            $"SELECT quota_day::text FROM platform_quota_usage WHERE platform = '{Platform}' "
            + "AND quota_day IN ('2026-02-18', '2026-02-19', '2026-06-14', '2026-06-15') ORDER BY quota_day",
            Ct);
        days.ShouldBe(["2026-02-18", "2026-02-19", "2026-06-14", "2026-06-15"]);
    }

    [Fact]
    public async Task QuotaLedger_Qa_RefusedReserve_ProblemResponse_CarriesRetryAfterHeader()
    {
        var now = new DateTimeOffset(2026, 2, 26, 12, 0, 0, TimeSpan.Zero);
        await using var db = Database.CreateAppDbContext(clock: new FakeClock(now));
        var ledger = NewLedger(db, unitsPerDay: 1);
        (await ledger.TryReserveAsync(Platform, now, Ct)).IsError.ShouldBeFalse();
        var refused = await ledger.TryReserveAsync(Platform, now, Ct);

        using var exchange = new ProblemExchange();
        var problem = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(refused.Errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        exchange.Context.Response.Headers.RetryAfter.ToString().ShouldBe("72000");
        problem!.Code.ShouldBe(ErrorCodes.YoutubeQuotaExhausted);
    }

    [Fact]
    public async Task YouTubeEta_Qa_OverLimitRow_ReturnsZeroAvailableAndNextSlot()
    {
        await ResetEtaAsync();
        var clock = new FakeClock();
        await SeedDayAsync(PacificQuotaDay.Day(clock.UtcNow), used: 105, limit: 100);
        await SetFlagAsync(FeatureKey, enabled: true);
        using var factory = new ApiFactory(Database, clock);
        using var client = factory.ClientFor(await CreateUserAsync());

        using var response = await client.GetAsync(EtaRoute, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("available").GetInt32().ShouldBe(0, "units_used > units_limit clamps to zero");
        json.RootElement.GetProperty("nextSlotAt").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);
        json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToList().ShouldBe(["available", "nextSlotAt"]);
    }

    [Fact]
    public async Task YouTubeEta_Qa_FlagOff_Anonymous_Is401()
    {
        await ResetEtaAsync();
        await SetFlagAsync(FeatureKey, enabled: false);
        using var factory = new ApiFactory(Database, new FakeClock());
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync(EtaRoute, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static YouTubeQuotaLedger NewLedger(NightTales.Infrastructure.Persistence.AppDbContext db, int unitsPerDay) =>
        new(db, new FixedYouTubeOptions(unitsPerDay), new NightTalesMetrics(), NullLogger<YouTubeQuotaLedger>.Instance);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<Guid> CreateUserAsync()
        => (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(email: null, ct: Ct)).Id;

    private Task ResetEtaAsync() =>
        Database.ExecuteAsync(
            "DELETE FROM platform_quota_usage; "
            + $"DELETE FROM feature_flags WHERE key = '{FeatureKey}'",
            Ct);

    private Task SetFlagAsync(string key, bool enabled) =>
        Database.ExecuteAsync(
            "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
            + $"VALUES ('{key}', {(enabled ? "true" : "false")}, '{{}}', now(), now()) "
            + "ON CONFLICT (key) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = now()",
            Ct);

    private Task SeedDayAsync(DateOnly day, int used, int limit) =>
        Database.ExecuteAsync(
            "INSERT INTO platform_quota_usage (platform, quota_day, units_used, units_limit, created_at, updated_at) "
            + $"VALUES ('{Platform}', '{Iso(day)}', {used}, {limit}, now(), now())",
            Ct);

    /// <summary>A fixed options monitor: the ledger reads the daily limit at seed time.</summary>
    private sealed class FixedYouTubeOptions(int unitsPerDay) : IOptionsMonitor<YouTubeOptions>
    {
        private readonly YouTubeOptions _value = new() { Quota = new YouTubeQuotaOptions { UnitsPerDay = unitsPerDay } };

        public YouTubeOptions CurrentValue => _value;

        public YouTubeOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<YouTubeOptions, string?> listener) => null;
    }
}
