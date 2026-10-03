using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
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
        await SeedNodesAsync("down", "active");
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        PublishHealthyStorage(factory, clock);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready", Ct);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.ShouldBe(true);
        report.GetProperty("status").GetString().ShouldBe("healthy");
        CheckStatus(report, "nodes").ShouldBe("healthy");
    }

    [Theory]
    [InlineData]
    [InlineData("draining")]
    [InlineData("down")]
    [InlineData("draining", "down")]
    public async Task Ready_is_degraded_200_when_only_the_registry_has_no_active_node(params string[] states)
    {
        await SeedNodesAsync(states);
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        PublishHealthyStorage(factory, clock);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready", Ct);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "a node failure never makes the probe 503 (09.9-D2)");
        report.GetProperty("status").GetString().ShouldBe("degraded");
        CheckStatus(report, "nodes").ShouldBe("degraded");
        CheckStatus(report, "db").ShouldBe("healthy");
        CheckStatus(report, "storage").ShouldBe("healthy");
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

    private static void PublishHealthyStorage(ApiFactory factory, FakeClock clock) =>
        factory.Services.GetRequiredService<IStorageHealthCache>()
            .Publish(new StorageHealth(true, clock.UtcNow, ErrorCode: null));

    /// <summary>Replaces the class database's registry with one node per given state.</summary>
    private async Task SeedNodesAsync(params string[] states)
    {
        await using var db = Database.CreateAppDbContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM worker_nodes", Ct);
        for (var index = 0; index < states.Length; index++)
        {
            var name = $"health-node-{index}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO worker_nodes (id, name, base_url, capacity, state, created_at, updated_at) VALUES (gen_random_uuid(), {name}, 'http://node.example/', 2, {states[index]}, now(), now())",
                Ct);
        }
    }

    private static string? CheckStatus(JsonElement report, string name) =>
        report.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == name)
            .GetProperty("status").GetString();
}
