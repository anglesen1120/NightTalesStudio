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

    /// <summary>Answers <c>text/html</c>, the one content type the hardening CSP is written for (09.9.4-T1).</summary>
    internal const string HtmlPath = "/__probe/security/html";

    /// <summary>A route whose handler throws; 09.9.4-T1 asserts the hardening headers survive the 500.</summary>
    internal const string ThrowPath = "/__probe/security/throw";

    /// <summary>A route that answers 400; the headers of a client error must match a success one.</summary>
    internal const string BadRequestPath = "/__probe/security/bad-request";

    /// <summary>Reads the whole request body and answers its byte count (the default request-body cap).</summary>
    internal const string PostEchoPath = "/__probe/security/post-echo";

    /// <summary>The same, but with its own <c>RequestSizeLimitAttribute</c> larger than the host default.</summary>
    internal const string LargePostEchoPath = "/__probe/security/post-echo-large";

    /// <summary>The exception text of <see cref="ThrowPath"/>; a 500 body must never carry it.</summary>
    internal const string ThrowMessage = "probe-security-canary-7f31";

    /// <summary>The body cap of <see cref="LargePostEchoPath"/>, four times the host's 1 MiB default.</summary>
    internal const long LargePostEchoLimit = 4 * 1024 * 1024;

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

        // 09.9.4-T1: the hardening middleware's two content-type branches (CSP on HTML only) and the
        // error path (the exception handler clears the response, so the headers must be written again).
        routes.MapGet(HtmlPath, () => Results.Content("<html><body>docs</body></html>", "text/html"));

        routes.MapGet(ThrowPath, Throw);

        routes.MapGet(BadRequestPath, () => Results.BadRequest());

        // The request-body cap: the first route keeps the host default, the second declares its own
        // larger one — both read the same `IRequestSizeLimitMetadata` the Kestrel-level cap reads.
        routes.MapPost(PostEchoPath, EchoBodyAsync);
        routes.MapPost(LargePostEchoPath, EchoBodyAsync)
            .WithMetadata(new RequestSizeLimitAttribute(LargePostEchoLimit));
    }

    private static IResult Throw() => throw new InvalidOperationException(ThrowMessage);

    private static async Task<IResult> EchoBodyAsync(HttpRequest request, CancellationToken ct)
    {
        // The request body stream is owned by the server: read it into a local buffer rather than
        // wrapping it in a StreamReader whose disposal would close the server's own stream.
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);

        return Results.Ok(new PostEcho((int)buffer.Length));
    }

    internal sealed record WhoAmI(string? Sub, string? Role, bool IsAuthenticated);

    /// <summary>What the host's <c>ICurrentUser</c> resolved for the request (09.2.3-T1).</summary>
    internal sealed record ProbeActor(Guid? Id, string Role, string Actor, bool IsAuthenticated);

    /// <summary>The byte count of the body <c>PostEchoPath</c> accepted.</summary>
    internal sealed record PostEcho(int Bytes);
}
