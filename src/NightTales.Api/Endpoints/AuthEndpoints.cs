using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NightTales.Api.Http;
using NightTales.Application.Features.Auth.Register.Commands;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The <c>/auth</c> route group (09 README D14/D7, 09.3.1-T1). This task maps
/// <c>POST /api/v1/auth/register</c> only; 09.3.1-T3 adds verify-email/resend and 09.3.2-T1 adds the
/// login/refresh/logout routes to the same group.
/// </summary>
/// <remarks>
/// The endpoint is deliberately thin (09.1.2-T1's rule): it binds the frozen body of 10-web-ui SC30 to
/// <see cref="RegisterCommand"/>, sends it through the D4 pipeline and maps the result with the D7
/// helpers. The register gate itself (403 <c>registration_closed</c> in P) is the handler's
/// <c>IRegistrationFlow</c>, not a branch in the endpoint.
/// </remarks>
public static class AuthEndpoints
{
    /// <summary>The group's prefix; every auth route of 09.3 hangs under it.</summary>
    public const string GroupPrefix = "/api/v1/auth";

    /// <summary>The rate-limit policy the register route carries (owned by 09.3.5-T1, 09 README BD14).</summary>
    /// <remarks>
    /// Referenced by name only: until 09.3.5-T1 registers the policy the metadata is inert, so register
    /// traffic is unlimited — the alternative (a policy defined here) would create a second owner of the
    /// registered limit.
    /// </remarks>
    public const string AuthRateLimitPolicy = "auth";

    /// <summary>Maps <c>POST /api/v1/auth/register</c> onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var auth = routes.MapGroup(GroupPrefix);

        auth.MapPost(
                "/register",
                async (RegisterCommand command, ISender sender, HttpContext http, CancellationToken ct) =>
                    (await sender.Send(command, ct).ConfigureAwait(false)).ToAcceptedResult(http))
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy);

        return routes;
    }
}
