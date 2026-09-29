using System.Net.Http.Json;
using NightTales.Domain.Common;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Harness;

/// <summary>
/// The test authentication scheme (09 README D13): <c>ApiFactory.ClientFor</c> authenticates as the
/// given user through <c>X-Test-User</c>/<c>X-Test-Role</c>, and a plain client stays anonymous.
/// </summary>
public sealed class ApiFactoryAuthTests : IntegrationTest
{
    public ApiFactoryAuthTests(TestDatabase database)
        : base(database)
    {
    }

    [Fact]
    public async Task ApiFactory_ClientFor_AuthenticatesAsGivenUser()
    {
        var userId = Guid.CreateVersion7();

        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(userId, PlatformRole.Admin);

        var who = await client.GetFromJsonAsync<ProbeEndpoints.WhoAmI>(
            ProbeEndpoints.WhoAmIPath,
            TestContextToken.Value);

        who.ShouldNotBeNull();
        who.IsAuthenticated.ShouldBeTrue();
        who.Sub.ShouldBe(userId.ToString());
        who.Role.ShouldBe("admin");
    }

    [Fact]
    public async Task ApiFactory_AnonymousClient_IsUnauthenticated()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        var who = await client.GetFromJsonAsync<ProbeEndpoints.WhoAmI>(
            ProbeEndpoints.WhoAmIPath,
            TestContextToken.Value);

        who.ShouldNotBeNull();
        who.IsAuthenticated.ShouldBeFalse();
        who.Sub.ShouldBeNull();
    }

    [Fact]
    public async Task ApiFactory_ReplacesClock_WithTheGivenFakeClock()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 4, 3, 2, 1, TimeSpan.Zero));

        using var factory = new ApiFactory(Database, clock).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        var utcNow = await client.GetFromJsonAsync<DateTimeOffset>(
            ProbeEndpoints.ClockPath,
            TestContextToken.Value);

        utcNow.ShouldBe(clock.UtcNow);
    }

    [Fact]
    public async Task TestAuthHandler_NonGuidUserHeader_StaysUnauthenticated()
    {
        // QA 09.2.1-T2: a malformed X-Test-User is an authentication failure, never an authenticated
        // request and never a 500 — on a probe without RequireAuthorization that reads as anonymous.
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "not-a-guid");

        var who = await client.GetFromJsonAsync<ProbeEndpoints.WhoAmI>(
            ProbeEndpoints.WhoAmIPath,
            TestContextToken.Value);

        who.ShouldNotBeNull();
        who.IsAuthenticated.ShouldBeFalse();
        who.Sub.ShouldBeNull();
    }

    [Fact]
    public async Task TestAuthHandler_UnknownRoleHeader_StaysUnauthenticated()
    {
        // QA 09.2.1-T2: a role value outside PlatformRole fails authentication instead of silently
        // defaulting to User.
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, "supreme_overlord");

        var who = await client.GetFromJsonAsync<ProbeEndpoints.WhoAmI>(
            ProbeEndpoints.WhoAmIPath,
            TestContextToken.Value);

        who.ShouldNotBeNull();
        who.IsAuthenticated.ShouldBeFalse();
        who.Sub.ShouldBeNull();
    }

    [Fact]
    public async Task TestAuthHandler_MissingRoleHeader_DefaultsToUser()
    {
        // QA 09.2.1-T2: X-Test-User alone authenticates as PlatformRole.User (the documented default).
        var userId = Guid.CreateVersion7();

        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());

        var who = await client.GetFromJsonAsync<ProbeEndpoints.WhoAmI>(
            ProbeEndpoints.WhoAmIPath,
            TestContextToken.Value);

        who.ShouldNotBeNull();
        who.IsAuthenticated.ShouldBeTrue();
        who.Sub.ShouldBe(userId.ToString());
        who.Role.ShouldBe("user");
    }
}
