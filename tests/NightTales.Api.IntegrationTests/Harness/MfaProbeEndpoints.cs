using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NightTales.Api.Http.Filters;

namespace NightTales.Api.IntegrationTests.Harness;

/// <summary>
/// Test-only routes that carry 09.10.4-T1's two route guards, mapped onto the host through
/// <c>ApiFactory.MapEndpoints</c> exactly like <see cref="ProbeEndpoints"/>.
/// </summary>
/// <remarks>
/// The guards exist for routes this wave does not have yet: <c>RequireReauth</c> is what later plans attach
/// to their sensitive routes (settings, role change, MFA reset, grants), and <c>RequireMfaEnrolled</c> is
/// what the <c>/admin/*</c> surface will carry. A test-only route is therefore the only honest way to
/// observe their behaviour against the real pipeline, which is what the task's acceptance checks ask for
/// ("observed on a test-only guarded route").
/// </remarks>
internal static class MfaProbeEndpoints
{
    /// <summary>A route that requires a re-authentication no older than five minutes (BD4).</summary>
    internal const string ReauthGuardedPath = "/__probe/mfa/reauth-guarded";

    /// <summary>A route that is 404 until the caller has an authenticator enrolled (BD4).</summary>
    internal const string AdminGuardedPath = "/__probe/mfa/admin-guarded";

    /// <summary>The window the guarded probe route requires.</summary>
    internal static readonly TimeSpan ReauthWindow = TimeSpan.FromMinutes(5);

    internal static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet(ReauthGuardedPath, () => Results.Ok())
            .RequireReauth(ReauthWindow);

        routes.MapGet(AdminGuardedPath, () => Results.Ok())
            .RequireMfaEnrolled();
    }
}
