using System.Net;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Common;

namespace NightTales.Api.Internal.Jobs;

/// <summary>
/// The scheme, path and claim vocabulary of the Hangfire dashboard (09.2.6-T2): one place for the
/// values the filter, the login endpoint and the audit middleware must agree on.
/// </summary>
/// <remarks>
/// <para>
/// The cookie is the platform's second session, deliberately separate from the refresh cookie of BD3:
/// it is named <c>__Host-nts-jobs</c> (the browser then enforces <c>Secure</c>, host-only and
/// <c>Path=/</c> itself), it authenticates only dashboard requests, and it carries its own 30-minute
/// absolute lifetime (no sliding).
/// </para>
/// <para>
/// The ticket is a standard ASP.NET Core cookie ticket, so a tampered, foreign or expired cookie is
/// refused by the framework's own ticket unprotection before a single claim is read.
/// </para>
/// </remarks>
public static class JobsDashboardAuthentication
{
    /// <summary>The cookie scheme of the dashboard; never the host's default scheme (09.2.6-T2 Design).</summary>
    public const string SchemeName = "JobsDashboard";

    /// <summary>The cookie name; the <c>__Host-</c> prefix requires <c>Secure</c> and <c>Path=/</c>.</summary>
    public const string CookieName = "__Host-nts-jobs";

    /// <summary>The dashboard's path in the Api: Hangfire's own branch and the login route live under it.</summary>
    public const string DashboardPath = "/internal/jobs";

    /// <summary>The route that mints the cookie (09.2.6-T2 Design).</summary>
    public const string LoginPath = "/internal/jobs/login";

    /// <summary>The dashboard cookie's absolute lifetime (09.2.6-T2 Design: 30 minutes, no sliding).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How recent the caller's re-authentication must be for the cookie to be minted (09.2.6-T2 Design:
    /// password + TOTP within 5 minutes, the window 09.10.4-T1's guard is called with).
    /// </summary>
    public static readonly TimeSpan ReauthWindow = TimeSpan.FromMinutes(5);

    /// <summary>The caller's id, copied from the access token's <c>sub</c> (09 README D16).</summary>
    public const string SubjectClaimType = "sub";

    /// <summary>The caller's role, copied from the access token's <c>role</c> claim.</summary>
    public const string RoleClaimType = "role";

    /// <summary>
    /// The user's <c>security_stamp</c>, copied from the access token's <c>stamp</c> claim
    /// (<c>Es256TokenIssuer</c>, 09 README BD3): the dashboard's live-account check
    /// (<see cref="IJobsDashboardSessionValidator"/>) compares it with the account row on every request,
    /// exactly as 09.3.2-T2 will for the access token.
    /// </summary>
    public const string SecurityStampClaimType = "stamp";

    /// <summary>The dashboard session's own id, minted per login; the audit dedupes its view rows on it.</summary>
    public const string SessionClaimType = "jti";

    /// <summary>
    /// Where the filter leaves the validated session for this request, so Hangfire's
    /// <c>IsReadOnlyFunc</c> (synchronous) and the audit middleware can read it without authenticating
    /// the cookie a second time. Public because it is the contract between the filter and the audit
    /// middleware — the integration suite drives the middleware with a session under this key — and the
    /// solution has no <c>InternalsVisibleTo</c>.
    /// </summary>
    public const string SessionItemKey = "NightTales.Api.Internal.Jobs.Session";

    /// <summary>
    /// Authenticates the dashboard cookie of <paramref name="http"/> and reads the session out of it.
    /// </summary>
    /// <remarks>
    /// The cookie scheme is authenticated explicitly: the host's default scheme (the bearer of BD3, the
    /// test scheme in the harness) knows nothing about this cookie, so nothing else would populate
    /// <see cref="HttpContext.User"/> for a dashboard request. Only an <c>owner</c> or an <c>admin</c>
    /// session is returned — a <c>user</c> ticket is "no session", so such a caller can neither see the
    /// dashboard nor be audited as an operator.
    /// </remarks>
    /// <param name="http">The request being authorized.</param>
    /// <returns>The session, or <c>null</c> when there is none to trust.</returns>
    internal static async Task<JobsDashboardSession?> ReadSessionAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(SchemeName).ConfigureAwait(false);

        if (!result.Succeeded || result.Principal is not { } principal)
        {
            return null;
        }

        if (!Guid.TryParse(principal.FindFirst(SubjectClaimType)?.Value, out var userId) || userId == Guid.Empty)
        {
            return null;
        }

        if (!TryParseRole(principal.FindFirst(RoleClaimType)?.Value, out var role) || role is PlatformRole.User)
        {
            return null;
        }

        var sessionId = principal.FindFirst(SessionClaimType)?.Value;

        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        // Absent when the access token that minted the cookie carried no `stamp` (the test harness's fake
        // token does not): the live-account check then compares only the account's existence, status and
        // role, never a stamp the ticket never had.
        var securityStamp = principal.FindFirst(SecurityStampClaimType)?.Value;

        return new JobsDashboardSession(
            userId,
            role,
            sessionId,
            string.IsNullOrEmpty(securityStamp) ? null : securityStamp);
    }

    /// <summary>
    /// Ends a refused dashboard session and answers the platform's bare 404, starting the response, which
    /// is what keeps it a 404.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hangfire's middleware runs <c>SetResponseStatusCode(httpContext, GetUnauthorizedStatusCode(...))</c>
    /// after a filter refuses (Hangfire 1.8.25, <c>AspNetCoreDashboardMiddleware.Invoke</c>, read from the
    /// installed package), and that helper only writes when
    /// <see cref="HttpResponse.HasStarted"/> is false — so a filter that merely sets the status is
    /// rewritten to Hangfire's 401 or 403. Starting the response here pins the 404 the contract of
    /// 09.2.6-T2 answers on this surface, with an empty body that confirms nothing.
    /// </para>
    /// <para>
    /// When the request carried the dashboard cookie, the refusal also deletes it (09.2.6-T2 Failure
    /// handling: "the cookie is cleared"): the sign-out writes the deletion <c>Set-Cookie</c> with the
    /// very attributes the cookie was issued with, so a browser cannot retry a ticket this surface no
    /// longer vouches for. A request with no cookie — an anonymous probe — is not answered a
    /// <c>Set-Cookie</c> at all.
    /// </para>
    /// </remarks>
    /// <param name="http">The refused request.</param>
    /// <param name="ct">The request's cancellation token.</param>
    internal static async Task RefuseAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.Cookies.ContainsKey(CookieName))
        {
            await http.SignOutAsync(SchemeName).ConfigureAwait(false);
        }

        http.Response.StatusCode = StatusCodes.Status404NotFound;

        await http.Response.StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="value"/> is one of the platform's three snake_case roles, case
    /// insensitively.
    /// </summary>
    /// <remarks>
    /// The closed set is parsed by hand for the same reason <c>HttpCurrentUser</c> does it: <see
    /// cref="Enum.TryParse{TEnum}(string, out TEnum)"/> would also accept a numeric value (<c>2</c> is
    /// <see cref="PlatformRole.Owner"/>) or a comma-joined list, neither of which a ticket may smuggle in.
    /// </remarks>
    private static bool TryParseRole(string? value, out PlatformRole role)
    {
        if (string.Equals(value, nameof(PlatformRole.Owner), StringComparison.OrdinalIgnoreCase))
        {
            role = PlatformRole.Owner;

            return true;
        }

        if (string.Equals(value, nameof(PlatformRole.Admin), StringComparison.OrdinalIgnoreCase))
        {
            role = PlatformRole.Admin;

            return true;
        }

        role = PlatformRole.User;

        return string.Equals(value, nameof(PlatformRole.User), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The operator behind one dashboard request (09.2.6-T2 Design): who the cookie says they are, and with
/// which role — the audit row's actor and the read-only switch's input.
/// </summary>
/// <param name="UserId">The operator's <c>sub</c>, copied from the access token that minted the cookie.</param>
/// <param name="Role">Their role, an <c>owner</c> or an <c>admin</c>: the filter never returns a <c>user</c>.</param>
/// <param name="SessionId">The cookie's own <c>jti</c>, so one view row is written per dashboard session.</param>
/// <param name="SecurityStamp">
/// The cookie's <c>stamp</c> claim, or <see langword="null"/> when the access token that minted it carried
/// none; the live-account check compares it with <c>users.security_stamp</c> when it is present.
/// </param>
public sealed record JobsDashboardSession(Guid UserId, PlatformRole Role, string SessionId, string? SecurityStamp);

/// <summary>
/// The dashboard's authorization filter (09.2.6-T2 Design/Step 2): a valid cookie, an
/// <c>owner</c>/<c>admin</c> role, the optional <c>InternalJobs:AllowedCidrs</c> allow-list, and — for a
/// command POST — the owner's role. Anything else is answered 404, never Hangfire's 401.
/// </summary>
/// <remarks>
/// <para>
/// It implements Hangfire's <em>asynchronous</em> filter (<see cref="IDashboardAsyncAuthorizationFilter"/>)
/// rather than the synchronous <see cref="IDashboardAuthorizationFilter"/> the plan sketches, because the
/// refusal must await <see cref="HttpResponse.StartAsync(CancellationToken)"/> to keep its 404 (see
/// <see cref="JobsDashboardAuthentication.RefuseAsync"/>) and the cookie is authenticated with an
/// asynchronous call. The middleware treats both interfaces the same way, so the behaviour — and the
/// wiring's intent — is exactly the one the plan describes.
/// </para>
/// <para>
/// It runs on every request of the dashboard branch, including the <c>/stats</c> poll hangfire.js posts
/// every two seconds: the cookie is un-protected by the framework, the allow-list is parsed once at
/// start-up, and the session's claims are re-validated against the live account with one bounded,
/// no-tracking read (<see cref="IJobsDashboardSessionValidator"/>). 09.3.2-T2's cached stamp check is not
/// landed, so the dashboard reads the account row itself rather than waiting for it: a demotion, a
/// suspension or a stamp rotation ends the session on the next request, never after the ticket's
/// 30-minute lifetime.
/// </para>
/// </remarks>
/// <param name="options">The <c>InternalJobs</c> section: the CIDR allow-list.</param>
public sealed class JobsDashboardAuthorizationFilter(IOptions<JobsDashboardOptions> options)
    : IDashboardAsyncAuthorizationFilter
{
    /// <summary>
    /// The configured networks, parsed once. <see cref="JobsDashboardOptions.IsValidCidr"/> has already
    /// proven every entry parseable and canonical at start-up (<c>AddJobsDashboard</c>), so a malformed
    /// list never serves traffic.
    /// </summary>
    private readonly IReadOnlyList<IPNetwork> _allowedNetworks =
        [.. options.Value.AllowedCidrs.Select(entry => IPNetwork.Parse(entry.Trim()))];

    /// <inheritdoc />
    public async Task<bool> AuthorizeAsync(DashboardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var http = HttpContextOf(context);
        var session = await JobsDashboardAuthentication.ReadSessionAsync(http).ConfigureAwait(false);

        if (session is null
            || !IsAddressAllowed(http)
            || !await IsStillAuthorizedAsync(http, session).ConfigureAwait(false))
        {
            await JobsDashboardAuthentication.RefuseAsync(http, http.RequestAborted).ConfigureAwait(false);

            return false;
        }

        // Left for IsOwner (synchronous) and for the audit middleware, which reads it after the dashboard
        // ran and needs to know which operator acted.
        http.Items[JobsDashboardAuthentication.SessionItemKey] = session;

        if (HttpMethods.IsPost(http.Request.Method)
            && !IsReadOnlyPost(http.Request.Path)
            && session.Role is not PlatformRole.Owner)
        {
            // An admin may read the dashboard and nothing else (09.2.6-T2 Goal). Hangfire's read-only mode
            // would answer its own 401 here; this surface answers 404 everywhere it refuses, consistently
            // with the anonymous and the wrong-role cases, and it refuses before the dispatcher runs.
            await JobsDashboardAuthentication.RefuseAsync(http, http.RequestAborted).ConfigureAwait(false);

            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the dashboard session of <paramref name="context"/> is the owner's — the input of the
    /// <c>IsReadOnlyFunc</c> the dashboard is configured with, which reads an owner as the writable
    /// session and every other session as the read-only one, so an admin's pages render without the
    /// action buttons (the filter refuses the POST itself as well: the UI is a courtesy, the filter is
    /// the control).
    /// </summary>
    /// <param name="context">The dashboard request.</param>
    public static bool IsOwner(DashboardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return HttpContextOf(context).Items[JobsDashboardAuthentication.SessionItemKey]
            is JobsDashboardSession { Role: PlatformRole.Owner };
    }

    /// <summary>
    /// The only dashboard POST that reads rather than commands: the metrics poll hangfire.js issues with
    /// <c>$.post(statisticsUrl, …)</c> against Hangfire's <c>/stats</c> route (<c>JsonStats</c>), which an
    /// admin may keep calling. Every other POST of the branch is a command, so a command route Hangfire
    /// adds later is treated as a command by default (fail closed) rather than silently as a read.
    /// </summary>
    /// <param name="path">The dashboard-relative path (the branch has already trimmed the prefix).</param>
    internal static bool IsReadOnlyPost(PathString path) =>
        string.Equals(path.Value?.TrimEnd('/'), "/stats", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The dashboard-relative path of a request that reached the middleware <em>before</em> Hangfire's
    /// branch: outside the branch the prefix is still part of <see cref="HttpRequest.Path"/>, while the
    /// dashboard's own routes and the audit payload are relative to it.
    /// </summary>
    /// <param name="http">The request.</param>
    internal static PathString RelativePath(HttpContext http) =>
        http.Request.Path.StartsWithSegments(JobsDashboardAuthentication.DashboardPath, out _, out var remaining)
            ? remaining
            : http.Request.Path;

    /// <summary>
    /// Whether the account behind <paramref name="session"/> still matches its claims, read live.
    /// </summary>
    /// <remarks>
    /// The filter is a singleton (one instance for the process), so the check cannot hold a
    /// <c>AppDbContext</c>: it resolves the scoped validator from the request's own scope, exactly as the
    /// audit middleware resolves its writer. A refusal here is the D2 case of 09.2.6-T2's failure
    /// handling — the account was demoted, suspended, deleted or its stamp rotated after the cookie was
    /// minted — and it answers the same 404 as every other refusal, cookie cleared.
    /// </remarks>
    /// <param name="http">The request being authorized.</param>
    /// <param name="session">The session its cookie carried.</param>
    private static async Task<bool> IsStillAuthorizedAsync(HttpContext http, JobsDashboardSession session)
    {
        var validator = http.RequestServices.GetRequiredService<IJobsDashboardSessionValidator>();

        return await validator
            .IsStillValidAsync(session.UserId, session.Role, session.SecurityStamp, http.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the request's peer is inside <c>InternalJobs:AllowedCidrs</c>. An empty list means the
    /// allow-list is not configured and the cookie alone decides; a configured list with no peer address
    /// (a non-TCP transport) fails closed.
    /// </summary>
    private bool IsAddressAllowed(HttpContext http)
    {
        if (_allowedNetworks.Count == 0)
        {
            return true;
        }

        return http.Connection.RemoteIpAddress is { } address
            && _allowedNetworks.Any(network => network.Contains(address));
    }

    /// <summary>
    /// The request behind a <see cref="DashboardContext"/>. The dashboard is an ASP.NET Core middleware
    /// (<c>Hangfire.AspNetCore</c>, 09 README D1), so its context always carries the host's
    /// <see cref="HttpContext"/>; the message names the impossible state instead of returning null.
    /// </summary>
    private static HttpContext HttpContextOf(DashboardContext context) =>
        context is AspNetCoreDashboardContext aspNetCore
            ? aspNetCore.HttpContext
            : throw new InvalidOperationException(
                $"The jobs dashboard requires an {nameof(AspNetCoreDashboardContext)}; "
                + $"{context.GetType().FullName} has no HttpContext to authorize.");
}
