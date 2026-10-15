using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Api.Http;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common.Errors;
using NightTales.Application.Publishing;
using NightTales.Application.Publishing.Quota;
using NightTales.Application.Telemetry;
using NightTales.Domain.Common;
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
            return await NewLedger(db, unitsPerDay: 10, clock: new FakeClock(now)).TryReserveAsync(Platform, now, Ct);
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

    [Theory]
    [InlineData("2026-02-19T07:59:59.999Z", "2026-02-19T08:00:00Z", "2026-02-18", "2026-02-19")]
    [InlineData("2026-06-15T06:59:59.999Z", "2026-06-15T07:00:00Z", "2026-06-14", "2026-06-15")]
    public async Task QuotaLedger_Qa_PacificMidnightInstant_OpensNextDay_AndRefusedOneSecondBefore(
        string beforeMidnight,
        string atMidnight,
        string dayBefore,
        string dayAfter)
    {
        // PST (February) and PDT (June): the last instant of a Pacific day and the exact instant the next one opens.
        var before = DateTimeOffset.Parse(beforeMidnight, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var at = DateTimeOffset.Parse(atMidnight, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        await Database.ExecuteAsync(
            $"DELETE FROM platform_quota_usage WHERE platform = '{Platform}' AND quota_day IN ('{dayBefore}', '{dayAfter}')",
            Ct);
        await using var db = Database.CreateAppDbContext();
        var ledger = NewLedger(db, unitsPerDay: 1, clock: new FakeClock(before));

        (await ledger.TryReserveAsync(Platform, before, Ct)).IsError.ShouldBeFalse();
        var refusedBefore = await ledger.TryReserveAsync(Platform, before, Ct);
        refusedBefore.FirstError.Metadata!.ShouldContainKeyAndValue(ErrorMetadata.RetryAfterSeconds, 1);
        (await ledger.TryReserveAsync(Platform, at, Ct)).IsError.ShouldBeFalse("the exact Pacific midnight opens the next day");

        var days = await Database.QueryStringsAsync(
            $"SELECT quota_day::text FROM platform_quota_usage WHERE platform = '{Platform}' "
            + $"AND quota_day IN ('{dayBefore}', '{dayAfter}') ORDER BY quota_day",
            Ct);
        days.ShouldBe([dayBefore, dayAfter]);
    }


    [Fact]
    public async Task QuotaLedger_Qa_RefusedReserve_ProblemResponse_CarriesRetryAfterHeader()
    {
        var now = new DateTimeOffset(2026, 2, 26, 12, 0, 0, TimeSpan.Zero);
        await using var db = Database.CreateAppDbContext();
        var ledger = NewLedger(db, unitsPerDay: 1, clock: new FakeClock(now));
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

    private static YouTubeQuotaLedger NewLedger(NightTales.Infrastructure.Persistence.AppDbContext db, int unitsPerDay, IClock clock) =>
        new(db, new FixedYouTubeOptions(unitsPerDay), new NightTalesMetrics(), clock, NullLogger<YouTubeQuotaLedger>.Instance);

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
