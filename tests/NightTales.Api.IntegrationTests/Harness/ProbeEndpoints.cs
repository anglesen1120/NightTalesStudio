using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NightTales.Domain.Common;
using NightTales.TestKit.Api;

namespace NightTales.Api.IntegrationTests.Harness;

/// <summary>
/// Test-only probe routes, mapped onto the host through
/// <see cref="MapEndpointsStartupFilter"/> (<c>ApiFactory.MapEndpoints</c>): they report what the
/// host actually resolved for the request, which is what makes the authentication tests real.
/// </summary>
internal static class ProbeEndpoints
{
    internal const string WhoAmIPath = "/__probe/whoami";

    internal const string ClockPath = "/__probe/clock";

    internal static void Map(IEndpointRouteBuilder routes)
    {
        // The principal, not ICurrentUser: until 09.2.3-T1 lands HttpCurrentUser the DI registration
        // is AnonymousCurrentUser, so the probe reports the authenticated principal the Test scheme
        // produced — and ApiFactory_ClientFor_AuthenticatesAsGivenUser asserts on it.
        routes.MapGet(WhoAmIPath, (ClaimsPrincipal principal) => Results.Ok(new WhoAmI(
            principal.FindFirst(TestAuthHandler.SubjectClaimType)?.Value,
            principal.FindFirst(TestAuthHandler.RoleClaimType)?.Value,
            principal.Identity?.IsAuthenticated ?? false)));

        // Resolves IClock from the host: an unregistered or unreplaced clock fails the request.
        routes.MapGet(ClockPath, ([FromServices] IClock clock) => Results.Ok(clock.UtcNow));
    }

    internal sealed record WhoAmI(string? Sub, string? Role, bool IsAuthenticated);
}
