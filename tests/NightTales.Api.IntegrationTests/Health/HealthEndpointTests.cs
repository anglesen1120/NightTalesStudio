using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Storage;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Health;

[Trait("Category", "Integration")]
public sealed class HealthEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=nts_unreachable;Username=nts;Password=nts-not-a-real-password;Timeout=2";

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Live_is_200_with_database_stopped()
    {
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting("ConnectionStrings:Postgres", UnreachableDatabase);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_is_healthy_with_all_dependencies()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        factory.Services.GetRequiredService<IStorageHealthCache>()
            .Publish(new StorageHealth(true, clock.UtcNow, ErrorCode: null));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready", Ct);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.ShouldBe(true);
        report.GetProperty("status").GetString().ShouldBe("healthy");
    }

    [Fact]
    public async Task Ready_is_503_when_database_down()
    {
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting("ConnectionStrings:Postgres", UnreachableDatabase);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        body.ShouldNotContain("nts-not-a-real-password");
        body.ShouldNotContain("127.0.0.1");
        body.ShouldNotContain("Exception");
    }

    [Fact]
    public async Task Ready_is_degraded_200_when_storage_down()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        factory.Services.GetRequiredService<IStorageHealthCache>()
            .Publish(new StorageHealth(false, clock.UtcNow, StorageHealth.UnavailableErrorCode));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready", Ct);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        report.GetProperty("status").GetString().ShouldBe("degraded");
    }
}
