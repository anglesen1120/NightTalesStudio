using System.Net;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Harness;

/// <summary>The 09.1.1-T1 liveness probe, now exercised through the harness host.</summary>
public sealed class HealthLiveTests : IntegrationTest
{
    public HealthLiveTests(TestDatabase database)
        : base(database)
    {
    }

    [Fact]
    public async Task HealthLive_ReturnsOk()
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live", TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
