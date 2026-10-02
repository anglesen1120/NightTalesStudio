using System.Net;
using System.Security.Claims;
using Hangfire;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.DependencyInjection;
using NightTales.Api.Http;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Jobs;

namespace NightTales.Api.Internal.Jobs;

/// <summary>
/// The <c>InternalJobs</c> section (09.2.6-T2 Outputs): where the dashboard may be reached from.
/// </summary>
public sealed class JobsDashboardOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "InternalJobs";

    /// <summary>
    /// The networks allowed to reach the dashboard (<c>InternalJobs:AllowedCidrs</c>). An empty list —
    /// the default, and what the committed <c>appsettings.json</c> carries — means the session cookie
    /// alone decides; a configured list is the second gate the task calls for, and it fails closed.
    /// </summary>
    /// <remarks>
    /// Each entry is a CIDR block (<c>10.0.0.0/8</c>, <c>2001:db8::/32</c>), and the host refuses to
    /// start on a malformed one — including one whose address has host bits set (<c>10.0.0.1/8</c>),
    /// which <see cref="IsValidCidr"/> rejects explicitly: <see cref="IPNetwork.TryParse(string?, out IPNetwork)"/>
    /// itself would accept it and silently normalise it to <c>10.0.0.0/8</c>, so a typo could otherwise
    /// widen the allow-list without a trace.
    /// </remarks>
    public IList<string> AllowedCidrs { get; set; } = [];

    /// <summary>Whether one configured entry is a CIDR block whose address has no host bits set.</summary>
    /// <param name="entry">The configured value.</param>
    public static bool IsValidCidr(string? entry)
    {
        var trimmed = entry?.Trim();

        if (!IPNetwork.TryParse(trimmed, out var network)
            || !IPAddress.TryParse(AddressPart(trimmed!), out var address))
        {
            return false;
        }

        // System.Net.IPNetwork accepts an address with host bits set and normalises it (`10.0.0.1/8`
        // parses as `10.0.0.0/8`), so the address as configured is compared with the parsed network's
        // canonical base: an entry that differs is a typo the host refuses to start on, never a silently
        // widened list. (The AspNetCore `IPNetwork` the plan's remark came from does reject host bits;
        // this is the System.Net type of the same name, with different behaviour.)
        return address.Equals(network.BaseAddress);
    }

    /// <summary>The address part of an entry, before its <c>/prefix</c>.</summary>
    /// <param name="entry">A trimmed, parseable CIDR or bare address.</param>
    private static string AddressPart(string entry)
    {
        var separator = entry.LastIndexOf('/');

        return separator < 0 ? entry : entry[..separator];
    }
}

/// <summary>
/// The dashboard's composition and its one route (09.2.6-T2 Steps 1 and 4): the Hangfire storage the
/// dashboard reads, the <c>__Host-nts-jobs</c> cookie scheme, the antiforgery service its POSTs are
/// validated with, and <c>POST /internal/jobs/login</c>, which mints the cookie.
/// </summary>
/// <remarks>
/// <para>
/// Only the dashboard runs here: <see cref="JobsServiceCollectionExtensions.AddHangfireStorage"/> adds
/// the storage and the client, never a Hangfire server nor the recurring-job reconciliation the Worker
/// owns (09 README D12).
/// </para>
/// <para>
/// The login route requires a valid access token <em>and</em> a re-authentication inside
/// <see cref="JobsDashboardAuthentication.ReauthWindow"/> (09.10.4-T1's guard, the same one the sensitive
/// <c>/me</c> routes use). It deliberately does not accept the credentials itself: the caller proves
/// password + TOTP on <c>POST /api/v1/auth/reauth</c> — the one place that owns that flow, its failure
/// counter and its audit rows — and this route only asks whether that proof is recent.
/// </para>
/// </remarks>
public static class JobsDashboardEndpoints
{
    /// <summary>Registers everything the dashboard host needs (Program.cs, or a DI extension of the Api).</summary>
    /// <param name="services">The Api's service collection.</param>
    /// <param name="configuration">The host configuration; it carries the connection string and the sections.</param>
    public static IServiceCollection AddJobsDashboard(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHangfireStorage(configuration);

        services.AddOptions<JobsDashboardOptions>()
            .BindConfiguration(JobsDashboardOptions.SectionName)
            .Validate(
                options => options.AllowedCidrs.All(JobsDashboardOptions.IsValidCidr),
                $"every '{JobsDashboardOptions.SectionName}:{nameof(JobsDashboardOptions.AllowedCidrs)}' entry must be a "
                + "CIDR network whose address has no host bits set, for example 10.0.0.0/8 or 2001:db8::/32.")
            .ValidateOnStart();

        // One view row per dashboard session: the audit middleware remembers a session's id here for the
        // cookie's own lifetime, so the two-second /stats poll cannot write a row per tick.
        services.AddMemoryCache();

        services.AddAuthentication().AddCookie(JobsDashboardAuthentication.SchemeName, ConfigureJobsCookie);

        // Hangfire's dashboard middleware validates IAntiforgery itself for every POST of its branch
        // (1.8.25 AspNetCoreDashboardMiddleware.Invoke: IsRequestValidAsync, 403 on failure) and its
        // context emits the token its JavaScript echoes back, so registering the service is what arms the
        // dashboard's antiforgery — the framework's UseAntiforgery middleware is not part of the path.
        services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);

        services.AddSingleton<JobsDashboardAuthorizationFilter>();
        services.AddSingleton(provider => new DashboardOptions
        {
            // Assigning AsyncAuthorization also drops Hangfire's default LocalRequestsOnlyAuthorizationFilter
            // (the property's setter clears Authorization while it is still the default instance), which
            // would otherwise answer its own 401 before this filter ever runs.
            AsyncAuthorization = [provider.GetRequiredService<JobsDashboardAuthorizationFilter>()],
            // Hangfire asks this "is the dashboard read-only for this session", while IsOwner answers the
            // opposite question ("does this session belong to the owner"), so the two are inverses.
            // Handing over IsOwner itself would make the owner read-only and the admin writable, and
            // Hangfire's CommandDispatcher would answer the owner's every retry with its own 401.
            IsReadOnlyFunc = context => !JobsDashboardAuthorizationFilter.IsOwner(context),
            AppPath = null,
            DisplayStorageConnectionString = false,
        });

        return services;
    }

    /// <summary>Maps the dashboard's login route onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapJobsDashboardEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // Not part of the public OpenAPI document (09.2.6 plan §5): the dashboard is an operator surface,
        // and no generated client consumes its login.
        // The credentialed policy is the one the auth routes carry, applied the same way (a group that
        // requires it): the SPA at `App:PublicOrigin` calls this route from another origin and the
        // response installs the `__Host-nts-jobs` cookie, which a browser only stores for a credentialed
        // cross-origin answer (BD3). The policy's allow-list is `App:PublicOrigin` alone, the same origin
        // the audit middleware's POST gate accepts.
        var jobs = routes.MapGroup(JobsDashboardAuthentication.DashboardPath);

        jobs.RequireCors(SecurityHeadersExtensions.AuthCorsPolicy);

        jobs.MapPost("/login", LoginAsync)
            .RequireAuthorization()
            .ExcludeFromDescription();

        return routes;
    }

    /// <summary>
    /// <c>POST /internal/jobs/login</c>: 204 with the dashboard cookie, or the platform's refusal — 404
    /// for a caller who may never hold it, 401 <c>reauth_required</c> for one who has not just proven
    /// password + TOTP.
    /// </summary>
    private static async Task<IResult> LoginAsync(
        HttpContext http,
        ICurrentUser currentUser,
        IReauthGuard reauthGuard,
        CancellationToken ct)
    {
        // Both refusals are 404, like every other answer of this surface: a `user` must not learn that a
        // dashboard exists, and BD4's enrolment-only token reaches nothing but its two enrolment routes
        // (WT34). The dashboard filter checks the same two things; refusing here is what keeps such a
        // caller from ever holding a cookie in the first place.
        if (MfaAuthorizationServiceCollectionExtensions.IsEnrolmentToken(http.User)
            || currentUser.Id is not { } userId
            || currentUser.Role is not (PlatformRole.Owner or PlatformRole.Admin))
        {
            return ProblemDetailsWriter.ToProblem([Errors.NotFound()], http);
        }

        // 09.10.4-T1's rules, unchanged: a family with no recent re-authentication answers 401
        // reauth_required, a dead one answers 401 session_revoked. The caller's own error carries both.
        var recent = await reauthGuard
            .RequireRecentAsync(JobsDashboardAuthentication.ReauthWindow, ct)
            .ConfigureAwait(false);

        if (recent.IsError)
        {
            // Whatever the reason, a presented cookie dies with the refusal (09.2.6-T2 failure handling):
            // a session this route no longer vouches for must not survive on the client.
            await http.SignOutAsync(JobsDashboardAuthentication.SchemeName).ConfigureAwait(false);

            return ProblemDetailsWriter.ToProblem(recent.Errors, http);
        }

        await http.SignInAsync(
                JobsDashboardAuthentication.SchemeName,
                new ClaimsPrincipal(new ClaimsIdentity(
                    Claims(userId, currentUser.Role, http.User),
                    JobsDashboardAuthentication.SchemeName)),
                // Absolute lifetime, never extended: the handler turns ExpireTimeSpan into the ticket's
                // ExpiresUtc and the cookie's Max-Age, so no request can slide it.
                new AuthenticationProperties { AllowRefresh = false, IsPersistent = false })
            .ConfigureAwait(false);

        http.Response.Headers.CacheControl = "no-store";

        return Results.NoContent();
    }

    /// <summary>
    /// The dashboard session's claims: the caller's <c>sub</c> and <c>role</c>, a fresh session id, and
    /// the access token's <c>stamp</c> claim when it carries one.
    /// </summary>
    /// <remarks>
    /// The stamp is copied rather than resolved from the database: the access token's <c>stamp</c> claim is
    /// exactly the value <see cref="IJobsDashboardSessionValidator"/> compares with the live
    /// <c>users.security_stamp</c> on every dashboard request, so this route does not add a second read of
    /// that column. A caller whose token carries no stamp claim (the enrolment-only token never does) gets a
    /// cookie without one, and the per-request check then compares only the account's existence, status and
    /// role.
    /// </remarks>
    private static IEnumerable<Claim> Claims(Guid userId, PlatformRole role, ClaimsPrincipal caller)
    {
        yield return new Claim(JobsDashboardAuthentication.SubjectClaimType, userId.ToString());
        yield return new Claim(JobsDashboardAuthentication.RoleClaimType, role.ToString().ToLowerInvariant());
        yield return new Claim(JobsDashboardAuthentication.SessionClaimType, Guid.CreateVersion7().ToString("N"));

        if (caller.FindFirst(JobsDashboardAuthentication.SecurityStampClaimType)?.Value is { Length: > 0 } stamp)
        {
            yield return new Claim(JobsDashboardAuthentication.SecurityStampClaimType, stamp);
        }
    }

    /// <summary>
    /// The cookie the dashboard reads (09.2.6-T2 Design): <c>__Host-nts-jobs</c>, <c>HttpOnly</c>,
    /// <c>Secure</c>, <c>SameSite=Strict</c>, 30 minutes absolute with no sliding.
    /// </summary>
    private static void ConfigureJobsCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = JobsDashboardAuthentication.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // __Host- requires Path=/ and no Domain; the browser enforces all four attributes itself.
        options.Cookie.Path = "/";
        options.Cookie.IsEssential = true;

        options.ExpireTimeSpan = JobsDashboardAuthentication.Lifetime;
        options.SlidingExpiration = false;

        // Nothing ever challenges this scheme (the filter authenticates the cookie itself), but a stray
        // challenge must not answer with a redirect to a login page: this surface refuses with its own 404.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = options.Events.OnRedirectToLogin;
    }
}
