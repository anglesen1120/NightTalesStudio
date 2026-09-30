using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NightTales.Application.Abstractions;
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

    /// <summary>Reports the host's <c>ICurrentUser</c>, not the principal (09.2.3-T1).</summary>
    internal const string ActorPath = "/__probe/actor";

    internal static void Map(IEndpointRouteBuilder routes)
    {
        // The principal, not ICurrentUser: the probe reports the authenticated principal the Test
        // scheme produced — and ApiFactory_ClientFor_AuthenticatesAsGivenUser asserts on it. 09.2.3-T1
        // registers HttpCurrentUser over the same claims, so the probe stays independent of whichever
        // actor registration the host uses.
        routes.MapGet(WhoAmIPath, (ClaimsPrincipal principal) => Results.Ok(new WhoAmI(
            principal.FindFirst(TestAuthHandler.SubjectClaimType)?.Value,
            principal.FindFirst(TestAuthHandler.RoleClaimType)?.Value,
            principal.Identity?.IsAuthenticated ?? false)));

        // Resolves IClock from the host: an unregistered or unreplaced clock fails the request.
        routes.MapGet(ClockPath, ([FromServices] IClock clock) => Results.Ok(clock.UtcNow));

        // 09.2.3-T1: the actor every tenant-filtered query of this request is scoped to, which the
        // principal probe above cannot show.
        routes.MapGet(
            ActorPath,
            ([FromServices] ICurrentUser currentUser) => Results.Ok(new ProbeActor(
                currentUser.Id,
                currentUser.Role.ToString(),
                currentUser.Actor.ToString(),
                currentUser.IsAuthenticated)));
    }

    internal sealed record WhoAmI(string? Sub, string? Role, bool IsAuthenticated);

    /// <summary>What the host's <c>ICurrentUser</c> resolved for the request (09.2.3-T1).</summary>
    internal sealed record ProbeActor(Guid? Id, string Role, string Actor, bool IsAuthenticated);
}
