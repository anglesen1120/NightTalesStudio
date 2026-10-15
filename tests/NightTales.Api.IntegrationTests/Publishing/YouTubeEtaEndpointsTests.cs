using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Application.Common.Errors;
using NightTales.Application.Publishing.Quota;
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
/// 06.3.1-T1 over the real host: <c>GET /api/v1/publishing/youtube/eta</c> reports the platform's remaining
/// daily upload units from the <c>platform_quota_usage</c> row for today's Pacific quota day, reads without
/// writing, gates on the <c>youtube_publish</c> flag and answers 401 to an anonymous caller.
/// </summary>
/// <remarks>
/// The class shares one database, so every test starts by clearing the quota rows and its own flag row. That
/// keeps each test to the single (youtube, today) row it seeds and stops it depending on leftovers.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class YouTubeEtaEndpointsTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Route = "/api/v1/publishing/youtube/eta";
    private const string FeatureKey = "youtube_publish";
    private const string Platform = "youtube";

    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task YouTubeEta_ReturnsAvailableUnits()
    {
        await ResetAsync();
        var clock = new FakeClock();
        await SeedDayAsync(PacificQuotaDay.Day(clock.UtcNow), used: 37, limit: 100);
        await SetFlagAsync(FeatureKey, enabled: true);
        using var factory = NewFactory(clock);
        using var client = factory.ClientFor(await CreateUserAsync());

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("available").GetInt32().ShouldBe(63);
        json.RootElement.GetProperty("nextSlotAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task YouTubeEta_WhenDayFull_ReturnsNextSlotAt()
    {
        await ResetAsync();
        var clock = new FakeClock();
        await SeedDayAsync(PacificQuotaDay.Day(clock.UtcNow), used: 5, limit: 5);
        await SetFlagAsync(FeatureKey, enabled: true);
        using var factory = NewFactory(clock);
        using var client = factory.ClientFor(await CreateUserAsync());

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("available").GetInt32().ShouldBe(0);

        var nextSlot = json.RootElement.GetProperty("nextSlotAt").GetDateTimeOffset();
        nextSlot.ShouldBe(PacificQuotaDay.NextResetAt(clock.UtcNow));
        nextSlot.ShouldBeGreaterThan(clock.UtcNow);
        TimeZoneInfo.ConvertTime(nextSlot, Pacific).TimeOfDay.ShouldBe(TimeSpan.Zero, "the reset is a Pacific midnight");
    }

    [Fact]
    public async Task YouTubeEta_WithNoRowForToday_ReturnsFullLimit()
    {
        await ResetAsync();
        await SetFlagAsync(FeatureKey, enabled: true);
        using var factory = NewFactory(new FakeClock());
        using var client = factory.ClientFor(await CreateUserAsync());

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("available").GetInt32().ShouldBe(100);
        json.RootElement.GetProperty("nextSlotAt").ValueKind.ShouldBe(JsonValueKind.Null);

        var rows = await Database.QueryStringsAsync("SELECT count(*)::text FROM platform_quota_usage", Ct);
        rows.Single().ShouldBe("0", "the read path never seeds a day row");
    }

    [Fact]
    public async Task YouTubeEta_WithoutFeature_Returns404FeatureDisabled()
    {
        await ResetAsync();
        await SeedDayAsync(PacificQuotaDay.Day(new FakeClock().UtcNow), used: 37, limit: 100);
        await SetFlagAsync(FeatureKey, enabled: false);
        using var factory = NewFactory(new FakeClock());
        using var client = factory.ClientFor(await CreateUserAsync());

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem!.Code.ShouldBe(ErrorCodes.FeatureDisabled);
    }

    [Fact]
    public async Task YouTubeEta_Anonymous_Is401()
    {
        await ResetAsync();
        await SetFlagAsync(FeatureKey, enabled: true);
        using var factory = NewFactory(new FakeClock());
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem!.Code.ShouldBe(ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task YouTubeEta_BodyHasOnlyTheContractProperties()
    {
        await ResetAsync();
        var clock = new FakeClock();
        await SeedDayAsync(PacificQuotaDay.Day(clock.UtcNow), used: 1, limit: 100);
        await SetFlagAsync(FeatureKey, enabled: true);
        using var factory = NewFactory(clock);
        using var client = factory.ClientFor(await CreateUserAsync());

        using var response = await client.GetAsync(Route, Ct);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToList()
            .ShouldBe(["available", "nextSlotAt"]);
    }

    [Fact]
    public async Task YouTubeEta_TwoUsers_SeeTheSamePlatformCounter()
    {
        await ResetAsync();
        var clock = new FakeClock();
        await SeedDayAsync(PacificQuotaDay.Day(clock.UtcNow), used: 37, limit: 100);
        await SetFlagAsync(FeatureKey, enabled: true);
        using var factory = NewFactory(clock);
        using var first = factory.ClientFor(await CreateUserAsync("first@nighttales.test"));
        using var second = factory.ClientFor(await CreateUserAsync("second@nighttales.test"));

        using var firstResponse = await first.GetAsync(Route, Ct);
        using var secondResponse = await second.GetAsync(Route, Ct);

        using var firstJson = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync(Ct));
        using var secondJson = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync(Ct));
        firstJson.RootElement.GetProperty("available").GetInt32().ShouldBe(63);
        secondJson.RootElement.GetProperty("available").GetInt32().ShouldBe(63);
    }

    private ApiFactory NewFactory(FakeClock clock) => new(Database, clock);

    private async Task<Guid> CreateUserAsync(string? email = null)
        => (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(email: email, ct: Ct)).Id;

    /// <summary>Clears the shared quota rows and this feature's flag row so the test starts from nothing.</summary>
    private Task ResetAsync() =>
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
            + $"VALUES ('{Platform}', '{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}', {used}, {limit}, now(), now())",
            Ct);
}
