using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Domain.Common;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Health;

/// <summary>
/// QA gap tests for 09.9.3-T1: the health probes carry no AllowAnonymous, so the fallback policy that
/// keeps the enrolment-only token (BD4) on the MFA routes also covers them, while anonymous probes pass.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HealthScopeTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Anonymous_probe_is_answered(string path)
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Enrolment_only_token_is_rejected(string path)
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var client = factory.ClientFor(Guid.NewGuid(), PlatformRole.User, scope: TestAuthHandler.EnrolmentScope);

        var response = await client.GetAsync(path, Ct);

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Probe_bodies_carry_no_connection_string_or_stack_trace(string path)
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var client = factory.CreateClient();

        var body = await (await client.GetAsync(path, Ct)).Content.ReadAsStringAsync(Ct);

        body.ShouldNotContain("Host=", Case.Insensitive);
        body.ShouldNotContain("Password", Case.Insensitive);
        body.ShouldNotContain(" at ", Case.Sensitive);
        body.ShouldNotContain("Exception", Case.Insensitive);
    }
}
