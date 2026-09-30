using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.Security;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Tenancy;

/// <summary>
/// The Api's <see cref="NightTales.Application.Abstractions.ICurrentUser"/> (09 README D10): the
/// caller's <c>sub</c> and <c>role</c> claims are the actor of every tenant-filtered query of the
/// request, and an anonymous request has no actor at all — so a filtered lookup misses instead of
/// reading someone else's row.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ApiCurrentUserTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task HttpCurrentUser_AuthenticatedRequest_ReadsTheClaims()
    {
        var userId = Guid.CreateVersion7();
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.ClientFor(userId, PlatformRole.Admin);

        var actor = await client.GetFromJsonAsync<ProbeEndpoints.ProbeActor>(
            ProbeEndpoints.ActorPath,
            TestContextToken.Value);

        actor.ShouldNotBeNull();
        actor.Id.ShouldBe((Guid?)userId, "sub is the user the tenant filter compares user_id against");
        actor.Role.ShouldBe(nameof(PlatformRole.Admin));
        actor.Actor.ShouldBe(nameof(ActorType.Admin));
        actor.IsAuthenticated.ShouldBeTrue();
    }

    [Fact]
    public async Task HttpCurrentUser_AnonymousRequest_HasNoUser()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        var actor = await client.GetFromJsonAsync<ProbeEndpoints.ProbeActor>(
            ProbeEndpoints.ActorPath,
            TestContextToken.Value);

        actor.ShouldNotBeNull();
        actor.Id.ShouldBeNull("an anonymous request owns nothing, so every filtered query misses");
        actor.IsAuthenticated.ShouldBeFalse();
        actor.Role.ShouldBe(nameof(PlatformRole.User));
        actor.Actor.ShouldBe(nameof(ActorType.User));
    }

    [Fact]
    public async Task HttpCurrentUser_EmptySubjectClaim_HasNoUserAndNoElevatedRole()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();
        // The Test scheme accepts Guid.Empty as a Guid, so this request reaches the host with a sub
        // claim and an admin role claim — exactly the token shape the empty guid must not elevate.
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.Empty.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, TestAuthHandler.RoleClaimValue(PlatformRole.Admin));

        var actor = await client.GetFromJsonAsync<ProbeEndpoints.ProbeActor>(
            ProbeEndpoints.ActorPath,
            TestContextToken.Value);

        actor.ShouldNotBeNull();
        actor.Id.ShouldBeNull("the empty guid is not a user, so the tenant filter must see no actor");
        actor.IsAuthenticated.ShouldBeFalse();
        actor.Role.ShouldBe(nameof(PlatformRole.User), "an elevated role claim without a valid sub must not elevate the actor");
        actor.Actor.ShouldBe(nameof(ActorType.User));
    }
}

/// <summary>
/// The claim-level boundaries of the Api's actor (09.2.3-T1). <see cref="TestAuthHandler"/> never emits
/// an empty or unparseable <c>sub</c>, and it emits no principal at all without a user header, so these
/// cases are checked on a directly constructed <see cref="HttpCurrentUser"/>: the <c>role</c> claim is
/// only believed once a valid <c>sub</c> names the user it is elevated for.
/// </summary>
public sealed class HttpCurrentUserClaimsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void RoleClaim_WithoutAValidSubject_IsNotElevated(string? subject)
    {
        var actor = ActorWithClaims(subject, TestAuthHandler.RoleClaimValue(PlatformRole.Admin));

        actor.Id.ShouldBeNull("no valid sub means no user, so the tenant filter sees no actor");
        actor.IsAuthenticated.ShouldBeFalse();
        actor.Role.ShouldBe(PlatformRole.User, "a role claim is only believed behind a valid sub");
        actor.Actor.ShouldBe(ActorType.User);
    }

    [Fact]
    public void ValidSubjectWithElevatedRoleClaim_IsElevated()
    {
        var userId = Guid.CreateVersion7();

        var actor = ActorWithClaims(userId.ToString(), TestAuthHandler.RoleClaimValue(PlatformRole.Admin));

        actor.Id.ShouldBe((Guid?)userId);
        actor.IsAuthenticated.ShouldBeTrue();
        actor.Role.ShouldBe(PlatformRole.Admin);
        actor.Actor.ShouldBe(ActorType.Admin);
    }

    [Fact]
    public void NoClaims_IsAnonymous()
    {
        var actor = ActorWithClaims(null, null);

        actor.Id.ShouldBeNull();
        actor.IsAuthenticated.ShouldBeFalse();
        actor.Role.ShouldBe(PlatformRole.User);
        actor.Actor.ShouldBe(ActorType.User);
    }

    [Fact]
    public void SystemScope_PinsTheActorForOneUser()
    {
        var actor = ActorWithClaims(null, null);
        var owner = Guid.CreateVersion7();

        using (actor.For(owner))
        {
            actor.Id.ShouldBe((Guid?)owner);
            actor.Actor.ShouldBe(ActorType.System, "the outbox override runs as the payload's user");
            actor.Role.ShouldBe(PlatformRole.User, "a system scope learns no platform role, only a token does");
        }

        actor.Id.ShouldBeNull();
        actor.Actor.ShouldBe(ActorType.User);
    }

    private static HttpCurrentUser ActorWithClaims(string? subject, string? role)
    {
        var claims = new List<Claim>();
        if (subject is not null)
        {
            claims.Add(new Claim(HttpCurrentUser.SubjectClaimType, subject));
        }

        if (role is not null)
        {
            claims.Add(new Claim(HttpCurrentUser.RoleClaimType, role));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var context = new DefaultHttpContext { User = principal };

        return new HttpCurrentUser(new StubHttpContextAccessor(context), new ScopedCurrentUser());
    }

    private sealed class StubHttpContextAccessor(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }
}
