using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The enrolment-only token of 09.10.4-T1 ([RULED — README §B10 WT34], BD4): what it may reach, what it
/// carries, and how long it lives.
/// </summary>
/// <remarks>
/// <para>
/// The token is driven through the harness's <c>X-Test-Scope</c> header, which the test scheme turns into
/// the same <c>scope</c> claim a real token carries, so the fallback authorization policy — the thing
/// under test — sees exactly what production sees.
/// </para>
/// <para>
/// The endpoint walk covers every route that carries the platform's authorization contract. The four
/// <c>AllowAnonymous</c> credential routes (<c>/auth/login</c>, <c>/auth/register</c>, <c>/auth/refresh</c>,
/// <c>/auth/logout</c>) are excluded on purpose: they opt out of authorization by contract, and a bearer
/// token on them grants nothing — the routes read no identity from it. Everything else, health included,
/// answers 404.
/// </para>
/// </remarks>
public sealed class MfaEnrolmentScopeTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EnrolmentToken_ReachesOnlyMeMfaRoutes()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Owner);

        using var client = factory.ClientFor(
            user.Id,
            PlatformRole.Owner,
            scope: TestAuthHandler.EnrolmentScope);

        // WT41: this token needs no re-authentication, which is the whole point of it.
        var setup = await client.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);

        var pending = await MfaTestFlow.SetupSecretAsync(setup, Ct);

        var verify = await client.PostAsJsonAsync(
            MfaTestFlow.VerifyPath,
            new { code = MfaTestFlow.Code(pending, clock.UtcNow) },
            Ct);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        var recoveryCodes = await client.PostAsync(MfaTestFlow.RecoveryCodesPath, content: null, Ct);

        recoveryCodes.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AuthTestFlow.ProblemCodeAsync(recoveryCodes)).ShouldBe(ErrorCodes.NotFound);

        var disable = await client.DeleteAsync(MfaTestFlow.MfaPath, Ct);
        disable.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EnrolmentToken_IsNarrowedToFifteenMinutes()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Owner);

        using var scope = factory.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<ITokenIssuer>();

        var issued = issuer.IssueEnrolmentToken(user, clock.UtcNow);

        issued.ExpiresAt.ShouldBe(clock.UtcNow + TimeSpan.FromMinutes(15));

        var token = new JsonWebTokenHandler().ReadJsonWebToken(issued.Token);

        token.GetClaim("scope").Value.ShouldBe(Es256TokenIssuer.EnrolmentScope);
        token.GetClaim("amr").Value.ShouldBe("pwd", "BD4: the enrolment token records the password step");
        token.TryGetClaim("sid", out _).ShouldBeFalse("it belongs to no refresh family (BD4)");
        token.ValidTo.ShouldBe(issued.ExpiresAt.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AdminRoute_WithoutEnrolment_Returns404()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock).MapEndpoints(MfaProbeEndpoints.Map);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Admin);

        using var client = factory.ClientFor(user.Id, PlatformRole.Admin);

        var hidden = await client.GetAsync(MfaProbeEndpoints.AdminGuardedPath, Ct);

        hidden.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "an admin without an authenticator must not see the admin surface at all (BD4)");
        (await AuthTestFlow.ProblemCodeAsync(hidden)).ShouldBe(ErrorCodes.NotFound);

        _ = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        var visible = await client.GetAsync(MfaProbeEndpoints.AdminGuardedPath, Ct);

        visible.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EndpointWalk_EnrolmentToken_Is404EverywhereElse()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Owner);

        using var client = factory.ClientFor(
            user.Id,
            PlatformRole.Owner,
            scope: TestAuthHandler.EnrolmentScope);

        (string Method, string Path)[] walk =
        [
            ("GET", "/api/v1/series"),
            ("GET", "/api/v1/episodes"),
            ("GET", "/api/v1/voices"),
            ("GET", "/api/v1/style-templates"),
            ("POST", MfaTestFlow.RecoveryCodesPath),
            ("DELETE", MfaTestFlow.MfaPath),
            ("POST", MfaTestFlow.ReauthPath),
            ("GET", "/health/live"),
        ];

        foreach (var (method, path) in walk)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);

            var response = await client.SendAsync(request, Ct);

            response.StatusCode.ShouldBe(
                HttpStatusCode.NotFound,
                $"{method} {path} must not be reachable with the enrolment-only token (WT34)");
            (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.NotFound);
        }

        // The two enrolment routes are the exception the policy exists for.
        var setup = await client.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
