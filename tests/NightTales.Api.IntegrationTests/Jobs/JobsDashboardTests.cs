using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Api.Http;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Api.Internal.Jobs;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;
using HangfireLogProvider = Hangfire.Logging.LogProvider;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// The Hangfire dashboard's auth matrix (09.2.6-T2 Tests table): a caller with no dashboard cookie gets
/// a 404, a <c>user</c> can never hold one, an <c>admin</c> reads but cannot act, an <c>owner</c> acts and
/// every access is audited, a cookie is only minted after a recent re-authentication, a cross-origin POST
/// is refused, and a configured <c>InternalJobs:AllowedCidrs</c> list is enforced.
/// </summary>
/// <remarks>
/// Cookies are handled by hand, exactly as the auth suite does it: the dashboard cookie is
/// <c>__Host-</c> (so <c>Secure</c>), and the in-process test server is reached over plain HTTP, where a
/// container would drop it. The owner test reads the antiforgery token out of the page the dashboard
/// really renders — the same <c>meta</c> tags hangfire.js echoes back — so it proves the dashboard's own
/// antiforgery wiring end to end and not a bypass of it.
/// </remarks>
public sealed class JobsDashboardTests(TestDatabase database) : IntegrationTest(database)
{
    private const string DashboardPath = "/internal/jobs/";

    private const string LoginPath = JobsDashboardAuthentication.LoginPath;

    private const string JobsCookieName = JobsDashboardAuthentication.CookieName;

    /// <summary>The origin a browser at this host sends on the dashboard's own POSTs.</summary>
    private const string OwnOrigin = "http://localhost";

    /// <summary>The two headers a credentialed cross-origin answer of this surface carries (BD3).</summary>
    private const string AllowOrigin = "Access-Control-Allow-Origin";

    private const string AllowCredentials = "Access-Control-Allow-Credentials";

    /// <summary>An origin that is not <c>App:PublicOrigin</c>, so the allow-list must refuse it.</summary>
    private const string ForeignOrigin = "https://evil.test";

    /// <summary>The CSP every other HTML answer keeps (09.9.4-T1, BD12) — the dashboard has its own.</summary>
    private const string DocsCsp = "default-src 'none'; frame-ancestors 'none'; img-src 'self' data:";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Dashboard_Anonymous_Returns404()
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var client = factory.AnonymousClient();
        var viewed = Rows(JobsDashboardAuditMiddleware.ViewedAction);

        var response = await client.GetAsync(DashboardPath, Ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "the dashboard never confirms it exists to a caller who cannot use it (09 README D10)");
        Rows(JobsDashboardAuditMiddleware.ViewedAction).ShouldBe(
            viewed,
            "nothing was accessed, so nothing is audited");
    }

    [Fact]
    public async Task Dashboard_UserRole_Returns404()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.User);

        // A `user` cannot even mint the cookie: the login route refuses the role before the re-auth gate.
        using var api = factory.ClientFor(user.Id, PlatformRole.User);
        var login = await SendAsync(api, HttpMethod.Post, LoginPath, cookie: null, origin: ApiFactory.PublicOrigin);

        login.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a `user` must not learn that the dashboard exists");
        CookiePair(login, JobsCookieName).ShouldBeNull("no dashboard cookie is ever issued to a `user`");

        // And a ticket that somehow says `user` is refused by the filter as well: the role is checked on
        // every request, not only when the cookie was minted (defence in depth).
        var cookie = MintCookie(factory, user.Id, PlatformRole.User);

        var response = await SendAsync(api, HttpMethod.Get, DashboardPath, cookie, origin: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ActorRows(JobsDashboardAuditMiddleware.ViewedAction, user.Id)
            .ShouldBe(0, "a refused caller is never audited as an operator");
    }

    [Fact]
    public async Task Dashboard_Admin_IsReadOnly()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        // A real account: the filter re-validates the ticket against the live row, so a cookie for an
        // account that does not exist is refused like any other dead session (the deleted-user case).
        var admin = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Admin);
        var cookie = MintCookie(factory, admin.Id, PlatformRole.Admin);

        using var api = factory.AnonymousClient();

        var page = await SendAsync(api, HttpMethod.Get, DashboardPath, cookie, origin: null);
        page.StatusCode.ShouldBe(HttpStatusCode.OK, "an admin reads the dashboard");
        var antiforgery = await AntiforgeryAsync(page);

        // The stats poll is the one read-only POST of the dashboard; an admin keeps it (the page would
        // otherwise break), while every command is refused — with this surface's 404, before Hangfire's
        // own read-only mode could answer its 401.
        (await SendAsync(
                api,
                HttpMethod.Post,
                "/internal/jobs/stats",
                $"{cookie}; {antiforgery.Cookie}",
                OwnOrigin,
                antiforgery)).StatusCode
            .ShouldBe(HttpStatusCode.OK, "the metrics poll is a read, not a command");

        var retry = await SendAsync(
            api,
            HttpMethod.Post,
            "/internal/jobs/jobs/actions/requeue/1",
            $"{cookie}; {antiforgery.Cookie}",
            OwnOrigin,
            antiforgery);

        retry.StatusCode.ShouldBe(HttpStatusCode.NotFound, "an admin's command is refused: read-only means read-only");
        ActorRows(
                JobsDashboardAuditMiddleware.ActionAction,
                admin.Id,
                " AND data->>'status' = '404'"
                + " AND category = 'support' AND visibility = 'internal'")
            .ShouldBe(1, "the refused attempt is part of the audit trail, in the contract's category and visibility");
    }

    [Fact]
    public async Task Dashboard_Owner_CanRetry_AndIsAudited()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var owner = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Owner, twoFactorEnabled: true);
        var familyId = await MfaTestFlow.SeedFamilyAsync(Database, owner, clock.UtcNow, reauthenticatedAt: clock.UtcNow);

        using var api = factory.ClientFor(owner.Id, PlatformRole.Owner, sessionId: familyId);

        var login = await SendAsync(api, HttpMethod.Post, LoginPath, cookie: null, origin: ApiFactory.PublicOrigin);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent, "a fresh re-authentication mints the dashboard cookie");

        var jobsCookie = CookiePair(login, JobsCookieName).ShouldNotBeNull();
        var flags = SetCookieFlags(login);
        flags.ShouldContain("secure", Case.Insensitive);
        flags.ShouldContain("httponly", Case.Insensitive);
        flags.ShouldContain("samesite=strict", Case.Insensitive);
        flags.ShouldContain("path=/", Case.Insensitive);
        // No `Expires`/`Max-Age` attribute: the cookie is a session cookie on purpose — the 30-minute
        // absolute lifetime lives on the ticket and is enforced by the framework, a fact the
        // expired-ticket case of Dashboard_TamperedOrExpiredCookie_Returns404 proves end to end.

        // The dashboard's own page — and the antiforgery token every one of its POSTs must carry.
        var page = await SendAsync(api, HttpMethod.Get, DashboardPath, jobsCookie, origin: null);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var antiforgery = await AntiforgeryAsync(page);

        var jobId = SeedScheduledJob(factory);

        var retry = await SendAsync(
            api,
            HttpMethod.Post,
            $"/internal/jobs/jobs/actions/requeue/{jobId}",
            $"{jobsCookie}; {antiforgery.Cookie}",
            OwnOrigin,
            antiforgery);

        retry.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            $"the owner's retry reached Hangfire's dispatcher; body: {await retry.Content.ReadAsStringAsync(Ct)}");

        SqlProbe.ScalarText(Database, $"SELECT statename FROM hangfire.job WHERE id = '{jobId}'")
            .ShouldBe("Enqueued", "the retry really moved the job");

        // A second page and the metrics poll still answer 200 — and must not add a view row: the
        // session's one row is deduplicated on the cookie's jti.
        (await SendAsync(api, HttpMethod.Get, DashboardPath, jobsCookie, origin: null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(
                api,
                HttpMethod.Post,
                "/internal/jobs/stats",
                $"{jobsCookie}; {antiforgery.Cookie}",
                OwnOrigin,
                antiforgery)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        ActorRows(JobsDashboardAuditMiddleware.ViewedAction, owner.Id).ShouldBe(
            1,
            "one row per dashboard session, however many pages and /stats polls it loads");
        ActorRows(
                JobsDashboardAuditMiddleware.ViewedAction,
                owner.Id,
                " AND data->>'sessionId' <> ''"
                + " AND category = 'support' AND visibility = 'internal'")
            .ShouldBe(1, "the view row names the session it belongs to, with the contract's category and visibility");

        ActorRows(
                JobsDashboardAuditMiddleware.ActionAction,
                owner.Id,
                $" AND data->>'command' = '/jobs/actions/requeue'"
                + $" AND data->'ids'->>0 = '{jobId}'"
                + " AND data->>'status' = '204'"
                + " AND category = 'support' AND visibility = 'internal'")
            .ShouldBe(1, "the action row names the command, the job it acted on and the outcome");
    }

    [Fact]
    public async Task Dashboard_CookieWithoutRecentReauth_IsNotIssued()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var owner = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Owner, twoFactorEnabled: true);

        // A live family whose last re-authentication is older than the five-minute window.
        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            owner,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow - TimeSpan.FromMinutes(10));

        using var api = factory.ClientFor(owner.Id, PlatformRole.Owner, sessionId: familyId);

        var login = await SendAsync(api, HttpMethod.Post, LoginPath, cookie: null, origin: ApiFactory.PublicOrigin);

        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(login)).ShouldBe(ErrorCodes.ReauthRequired);
        CookiePair(login, JobsCookieName).ShouldBeNull("a stale session must not be handed a dashboard cookie");
    }

    [Fact]
    public async Task Dashboard_PostWithoutOriginCheck_IsRejected()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var owner = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Owner);
        var cookie = MintCookie(factory, owner.Id, PlatformRole.Owner);

        using var api = factory.AnonymousClient();

        var crossOrigin = await SendAsync(
            api,
            HttpMethod.Post,
            "/internal/jobs/jobs/actions/requeue/1",
            cookie,
            "https://evil.test");

        crossOrigin.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "a foreign Origin is refused before Hangfire's own antiforgery check could answer 403");

        // The dashboard's own antiforgery gate is armed as well: a same-origin command POST without the
        // token hangfire.js echoes from the page's meta tags is refused by Hangfire itself.
        var withoutToken = await SendAsync(
            api,
            HttpMethod.Post,
            "/internal/jobs/jobs/actions/requeue/1",
            cookie,
            OwnOrigin);

        withoutToken.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Dashboard_IpOutsideAllowList_Returns404()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock)
            .WithSetting($"{JobsDashboardOptions.SectionName}:{nameof(JobsDashboardOptions.AllowedCidrs)}:0", "10.0.0.0/8");

        var owner = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Owner);
        var cookie = MintCookie(factory, owner.Id, PlatformRole.Owner);

        using var outside = factory.PeerClient("192.168.1.5");
        var refused = await SendAsync(outside, HttpMethod.Get, DashboardPath, cookie, origin: null);

        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "an address outside the allow-list sees nothing");

        using var inside = factory.PeerClient("10.1.2.3");
        var allowed = await SendAsync(inside, HttpMethod.Get, DashboardPath, cookie, origin: null);

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, "an address inside the allow-list still reaches the dashboard");
    }

    [Fact]
    public async Task Dashboard_Login_Anonymous_IsRefused()
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var api = factory.AnonymousClient();

        // The mint route is an endpoint (RequireAuthorization), so the unauthenticated caller meets the
        // platform's own 401 — it never reaches the role check.
        var noOrigin = await SendAsync(api, HttpMethod.Post, LoginPath, cookie: null, origin: null);

        noOrigin.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        CookiePair(noOrigin, JobsCookieName).ShouldBeNull();

        // A caller with the access token of an owner but a foreign Origin is refused by the audit
        // middleware's CSRF gate before the endpoint can even look at the request: no cookie is minted
        // cross-origin either.
        var owner = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Owner, twoFactorEnabled: true);
        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            owner,
            DateTimeOffset.UtcNow,
            reauthenticatedAt: DateTimeOffset.UtcNow);

        using var ownerApi = factory.ClientFor(owner.Id, PlatformRole.Owner, sessionId: familyId);
        var crossOrigin = await SendAsync(ownerApi, HttpMethod.Post, LoginPath, cookie: null, origin: "https://evil.test");

        crossOrigin.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the CSRF gate refuses the mint route too");
        CookiePair(crossOrigin, JobsCookieName).ShouldBeNull();
    }

    [Fact]
    public async Task Dashboard_LoginPreflight_IsAnsweredByCors()
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var api = factory.AnonymousClient();

        // The browser's preflight of the credentialed mint route: the CORS middleware answers it itself,
        // before authorization, so it needs no access token — a browser sends none on a preflight.
        using var allowed = await PreflightAsync(api, LoginPath, ApiFactory.PublicOrigin);

        allowed.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "a route that requires authorization must still have its preflight answered by CORS, not challenged with 401");
        Header(allowed, AllowOrigin).ShouldBe(ApiFactory.PublicOrigin);
        Header(allowed, AllowCredentials).ShouldBe(
            "true",
            "a browser only stores the `__Host-nts-jobs` cookie of a credentialed cross-origin answer (BD3)");

        // A foreign origin is refused in the browser's own terms: the policy is identified, so the
        // preflight is answered, but it allows neither the origin nor the credentials.
        using var foreign = await PreflightAsync(api, LoginPath, ForeignOrigin);

        foreign.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the preflight never becomes a request of this API");
        Header(foreign, AllowOrigin).ShouldBeEmpty("the allow-list is exactly App:PublicOrigin");
        Header(foreign, AllowCredentials).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dashboard_Login_CrossOriginPost_CarriesCredentialedHeaders()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var owner = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Owner, twoFactorEnabled: true);
        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database, owner, clock.UtcNow, reauthenticatedAt: clock.UtcNow);

        using var api = factory.ClientFor(owner.Id, PlatformRole.Owner, sessionId: familyId);

        using var response = await SendAsync(
            api, HttpMethod.Post, LoginPath, cookie: null, origin: ApiFactory.PublicOrigin);

        response.StatusCode.ShouldBe(
            HttpStatusCode.NoContent, "a fresh re-authentication mints the dashboard cookie");
        CookiePair(response, JobsCookieName).ShouldNotBeNull();
        Header(response, AllowOrigin).ShouldBe(
            ApiFactory.PublicOrigin,
            "the SPA at App:PublicOrigin calls this route from another origin, so the mint carries the headers");
        Header(response, AllowCredentials).ShouldBe(
            "true",
            "a browser hands the SPA the `Set-Cookie` of a credentialed cross-origin answer only");
    }

    [Fact]
    public async Task Dashboard_Login_Admin_MintsCookie()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var admin = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Admin, twoFactorEnabled: true);
        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database, admin, clock.UtcNow, reauthenticatedAt: clock.UtcNow);

        using var api = factory.ClientFor(admin.Id, PlatformRole.Admin, sessionId: familyId);

        var login = await SendAsync(api, HttpMethod.Post, LoginPath, cookie: null, origin: ApiFactory.PublicOrigin);

        login.StatusCode.ShouldBe(HttpStatusCode.NoContent, "an admin holds a dashboard session too — read-only");
        CookiePair(login, JobsCookieName).ShouldNotBeNull();
    }

    [Fact]
    public async Task Dashboard_Login_ReauthAtTheWindowEdge_IsRefused()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var owner = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Owner, twoFactorEnabled: true);
        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            owner,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow - JobsDashboardAuthentication.ReauthWindow);

        using var api = factory.ClientFor(owner.Id, PlatformRole.Owner, sessionId: familyId);

        // The re-auth is exactly five minutes old — the guard's boundary is exclusive.
        var edge = await SendAsync(api, HttpMethod.Post, LoginPath, cookie: null, origin: ApiFactory.PublicOrigin);

        edge.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(edge)).ShouldBe(ErrorCodes.ReauthRequired);

        // One second inside the window the same family mints the cookie: the boundary is the only
        // thing between the two answers.
        SqlProbe.Execute(
            Database,
            "UPDATE refresh_tokens SET reauthenticated_at = reauthenticated_at + interval '1 second' "
            + $"WHERE family_id = '{familyId}'");

        var inside = await SendAsync(api, HttpMethod.Post, LoginPath, cookie: null, origin: ApiFactory.PublicOrigin);

        inside.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        CookiePair(inside, JobsCookieName).ShouldNotBeNull();
    }

    [Fact]
    public async Task Dashboard_TamperedOrExpiredCookie_Returns404()
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        var userId = Guid.CreateVersion7();
        using var api = factory.AnonymousClient();

        // A flipped character breaks the ticket's data-protection signature: the framework refuses it
        // before a claim is read, so the answer is the same 404 as for no cookie at all.
        var good = MintCookie(factory, userId, PlatformRole.Owner);
        var tampered = good[..^2] + (good[^2] == 'A' ? 'B' : 'A') + good[^1];

        (await SendAsync(api, HttpMethod.Get, DashboardPath, tampered, origin: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // An expired ticket is refused the same way — the 30-minute absolute lifetime is not advisory.
        var expired = MintCookie(
            factory,
            userId,
            PlatformRole.Owner,
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
                ExpiresUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
            });

        (await SendAsync(api, HttpMethod.Get, DashboardPath, expired, origin: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Dashboard_RefusedSession_ClearsTheCookie()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Admin);
        var cookie = MintCookie(factory, user.Id, PlatformRole.Admin);

        using var api = factory.AnonymousClient();

        // The ticket still says `admin`, but the account's live role no longer is one. When the stamp
        // check lands (09.3.2-T2) this request must fail the session check — and the task's failure
        // handling is explicit: a refused session's cookie is cleared, not left to be retried for the
        // rest of its 30-minute life.
        await AuthTestFlow.UpdateUserAsync(factory.Services, user.Id, user => user.Role = PlatformRole.User);

        var response = await SendAsync(api, HttpMethod.Get, DashboardPath, cookie, origin: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        SetCookieHeaders(response)
            .Any(header => header.StartsWith(JobsCookieName + "=;", StringComparison.Ordinal))
            .ShouldBeTrue("a refused session's cookie is cleared (09.2.6-T2 failure handling)");
    }

    [Fact]
    public async Task Dashboard_RotatedSecurityStamp_Returns404()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var owner = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Owner);
        var cookie = MintCookie(
            factory,
            owner.Id,
            PlatformRole.Owner,
            securityStamp: owner.SecurityStamp);

        using var api = factory.AnonymousClient();

        // The ticket carries the stamp the account holds right now, so the live-account check admits it.
        (await SendAsync(api, HttpMethod.Get, DashboardPath, cookie, origin: null))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "a ticket whose stamp still matches the account is live");

        // Rotating the account's stamp (a password reset, an MFA change) invalidates every ticket minted
        // with the old one: the very next request must fail, not wait out the ticket's 30 minutes.
        await AuthTestFlow.UpdateUserAsync(
            factory.Services,
            owner.Id,
            user => user.SecurityStamp = Guid.CreateVersion7().ToString("N"));

        var response = await SendAsync(api, HttpMethod.Get, DashboardPath, cookie, origin: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        SetCookieHeaders(response)
            .Any(header => header.StartsWith(JobsCookieName + "=;", StringComparison.Ordinal))
            .ShouldBeTrue("a rotated stamp ends the session and clears its cookie");
    }

    [Fact]
    public void Dashboard_MalformedAllowedCidrs_FailsTheBoot()
    {
        // A typo in the allow-list must never serve traffic (the options' start-up validation), and a
        // CIDR whose address has host bits set is malformed, not widened silently.
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.UseSetting(
                $"{JobsDashboardOptions.SectionName}:{nameof(JobsDashboardOptions.AllowedCidrs)}:0",
                "10.0.0.1/8"));

        var exception = Should.Throw<OptionsValidationException>(() => host.CreateClient());

        exception.Message.ShouldContain(
            $"{JobsDashboardOptions.SectionName}:{nameof(JobsDashboardOptions.AllowedCidrs)}",
            Case.Sensitive,
            "the failure names the configuration key, never the value");
    }


    [Fact]
    public async Task Dashboard_Page_CarriesTheDashboardCsp_AndOtherHtmlKeepsTheDocsOne()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock).MapEndpoints(ProbeEndpoints.Map);
        var admin = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Admin);
        var cookie = MintCookie(factory, admin.Id, PlatformRole.Admin);

        using var api = factory.AnonymousClient();

        var page = await SendAsync(api, HttpMethod.Get, DashboardPath, cookie, origin: null);

        page.StatusCode.ShouldBe(HttpStatusCode.OK);

        // `default-src 'none'` would block every one of Hangfire's own assets (F1): the policy has to
        // name the branch's stylesheet, its script bundle, its Glyphicons font, the `/stats` poll and
        // the `style="…"` attributes its pages carry, and nothing else.
        var dashboardCsp = Header(page, "Content-Security-Policy");
        dashboardCsp.ShouldContain("default-src 'none'", Case.Sensitive);
        dashboardCsp.ShouldContain("script-src 'self'", Case.Sensitive, "the dashboard's /js bundle must load");
        dashboardCsp.ShouldContain("style-src 'self' 'unsafe-inline'", Case.Sensitive, "the pages carry style attributes");
        dashboardCsp.ShouldContain("img-src 'self' data:", Case.Sensitive, "the layout's favicon is a data: URL");
        dashboardCsp.ShouldContain("font-src 'self'", Case.Sensitive, "bootstrap's Glyphicons font is served by the branch");
        dashboardCsp.ShouldContain("connect-src 'self'", Case.Sensitive, "hangfire.js polls /stats");
        dashboardCsp.ShouldContain("frame-ancestors 'none'", Case.Sensitive);
        dashboardCsp.ShouldNotContain("unsafe-eval", Case.Sensitive);
        dashboardCsp.ShouldNotBe(
            DocsCsp,
            "the dashboard is its own surface: the docs policy would break it");

        // Every other HTML answer keeps the docs policy — the dashboard's policy is not the Api's.
        using var other = await api.GetAsync(ProbeEndpoints.HtmlPath, Ct);

        Header(other, "Content-Security-Policy").ShouldBe(
            DocsCsp,
            "only the /internal/jobs branch gets the dashboard's policy");
    }

    [Fact]
    public async Task Dashboard_ConcurrentFirstViews_WriteOneRow()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var admin = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Admin);
        var cookie = MintCookie(factory, admin.Id, PlatformRole.Admin);

        using var api = factory.AnonymousClient();

        // Eight tabs of one dashboard session opening at the same moment: every one misses the cache
        // before any of them commits, so the dedup has to be single-flight (F3).
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => SendAsync(api, HttpMethod.Get, DashboardPath, cookie, origin: null)));

        try
        {
            responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        ActorRows(JobsDashboardAuditMiddleware.ViewedAction, admin.Id).ShouldBe(
            1,
            "one row per dashboard session, however many of its pages are opened at once");
    }

    [Fact]
    public async Task Dashboard_BatchCommand_RecordsTheWholeCountAndTheTruncation()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var admin = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Admin);
        var cookie = MintCookie(factory, admin.Id, PlatformRole.Admin);

        using var api = factory.AnonymousClient();

        var page = await SendAsync(api, HttpMethod.Get, DashboardPath, cookie, origin: null);
        var antiforgery = await AntiforgeryAsync(page);

        var fields = Enumerable.Range(1, 25)
            .Select(index => new KeyValuePair<string, string>(
                "jobs[]",
                index.ToString(CultureInfo.InvariantCulture)))
            .ToArray();

        var response = await SendAsync(
            api,
            HttpMethod.Post,
            "/internal/jobs/jobs/failed/requeue",
            $"{cookie}; {antiforgery.Cookie}",
            OwnOrigin,
            antiforgery,
            fields);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "an admin's batch is refused like any other command");

        // The payload keeps its 20-id cap, but a reader can see that the command named more
        // (SEC-0926T2-02): the total and the flag travel with it.
        ActorRows(
                JobsDashboardAuditMiddleware.ActionAction,
                admin.Id,
                " AND data->>'total' = '25'"
                + " AND data->>'truncated' = 'true'"
                + " AND jsonb_array_length(data->'ids') = 20")
            .ShouldBe(1, "the batch's audit says how many ids the command really named");
    }

    [Fact]
    public async Task Dashboard_CancelledCommandRequest_StillLeavesItsAuditRow()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var owner = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Owner);

        // The command's own token is the point: Hangfire has already committed the retry inside its
        // storage when the row is written, so a request the caller abandoned must still be audited
        // (SEC-0926T2-01). The middleware is driven directly because the disconnect would otherwise be
        // a race against the dispatch; the services and the row it writes are the host's own.
        using var scope = factory.Services.CreateScope();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var context = CommandContext(
            scope.ServiceProvider,
            owner.Id,
            "/internal/jobs/jobs/actions/requeue/1",
            cancelled.Token);

        await Middleware(scope.ServiceProvider).InvokeAsync(context);

        ActorRows(
                JobsDashboardAuditMiddleware.ActionAction,
                owner.Id,
                " AND data->>'command' = '/jobs/actions/requeue'"
                + " AND data->>'status' = '204'")
            .ShouldBe(1, "the audit outlives the request token");
    }

    [Fact]
    public async Task Dashboard_CommandThatThrew_IsAuditedAsFailed()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var owner = await AuthTestFlow.CreateUserAsync(factory.Services, PlatformRole.Owner);

        using var scope = factory.Services.CreateScope();
        var context = CommandContext(
            scope.ServiceProvider,
            owner.Id,
            "/internal/jobs/jobs/actions/requeue/1",
            CancellationToken.None);

        var middleware = new JobsDashboardAuditMiddleware(
            _ => throw new InvalidOperationException("the dashboard failed after dispatching"),
            scope.ServiceProvider.GetRequiredService<IOptions<AppOptions>>(),
            scope.ServiceProvider.GetRequiredService<IMemoryCache>(),
            scope.ServiceProvider.GetRequiredService<ILogger<JobsDashboardAuditMiddleware>>());

        // The audit runs from the middleware's finally, so it sees a command the dashboard answered
        // with an exception (SEC-0926T2-01); the throw itself is not swallowed.
        await Should.ThrowAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        ActorRows(
                JobsDashboardAuditMiddleware.ActionAction,
                owner.Id,
                " AND data->>'status' = '500'")
            .ShouldBe(1, "a command that met an unhandled failure is still recorded");
    }

    [Fact]
    public async Task Dashboard_DisposedHost_DoesNotTakeAnotherHostsHangfireLogger()
    {
        var clock = new FakeClock();
        var sink = new CapturingLoggerProvider();

        using var first = new ApiFactory(Database, clock);
        using var second = new ApiFactory(Database, clock).WithLoggerProvider(sink);

        // Resolving the global configuration is what installs each host's ILoggerFactory into
        // Hangfire's process-static provider (AddHangfire's own factory).
        first.Services.GetRequiredService<IGlobalConfiguration>();
        second.Services.GetRequiredService<IGlobalConfiguration>();

        await first.DisposeAsync();

        // Hangfire resolves its logger from the process-static provider, and its own
        // AutomaticRetryAttribute does it in the instance constructor: a disposed factory left
        // installed here would make the next host's first scheduled job fail (F-LOG). A bounded retry
        // keeps the assertion honest while parallel test classes install their own providers.
        var observed = false;

        for (var attempt = 0; attempt < 20 && !observed; attempt++)
        {
            HangfireLogProvider.GetLogger("nts.jobs.logger.probe")
                .Log(Hangfire.Logging.LogLevel.Warn, () => "probe", null);
            observed = sink.Lines.Any(line => line.StartsWith("nts.jobs.logger.probe", StringComparison.Ordinal));

            if (!observed)
            {
                await Task.Delay(50, Ct);
            }
        }

        observed.ShouldBeTrue(
            "the live host keeps its Hangfire logger after a sibling host is disposed");
    }

    // --- helpers ------------------------------------------------------------------------------------

    /// <summary>
    /// The browser's CORS preflight of one dashboard route from one origin: the <c>OPTIONS</c> a browser
    /// sends before the credentialed <c>POST /internal/jobs/login</c>, carrying no token of any kind.
    /// </summary>
    private static async Task<HttpResponseMessage> PreflightAsync(
        HttpClient client,
        string path,
        string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "Authorization");

        return await client.SendAsync(request, Ct);
    }

    /// <summary>One request carrying the dashboard cookie, an <c>Origin</c> and (for a command POST) the antiforgery token.</summary>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? cookie,
        string? origin,
        AntiforgeryToken? antiforgery = null,
        IReadOnlyList<KeyValuePair<string, string>>? fields = null)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            // Every dashboard POST is form-encoded (Hangfire's dispatcher and the antiforgery check both
            // read the form), so a command POST without one would be refused for the wrong reason.
            Content = method == HttpMethod.Post ? new FormUrlEncodedContent(fields ?? []) : null,
        };

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (antiforgery is not null)
        {
            request.Headers.Add(antiforgery.HeaderName, antiforgery.Token);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>
    /// A dashboard cookie for <paramref name="userId"/>, minted the way the login route mints it: the
    /// scheme's own data-protected ticket, in the <c>name=value</c> form a <c>Cookie</c> header carries
    /// (the same shape <see cref="CookiePair"/> reads back off a response). A <c>user</c> can never obtain
    /// one through the API, so this is how the filter's role check is exercised.
    /// </summary>
    private static string MintCookie(
        ApiFactory factory,
        Guid userId,
        PlatformRole role,
        AuthenticationProperties? properties = null,
        string? securityStamp = null)
    {
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(JobsDashboardAuthentication.SchemeName);

        var claims = new List<Claim>
        {
            new(JobsDashboardAuthentication.SubjectClaimType, userId.ToString()),
            new(JobsDashboardAuthentication.RoleClaimType, role.ToString().ToLowerInvariant()),
            new(JobsDashboardAuthentication.SessionClaimType, Guid.CreateVersion7().ToString("N")),
        };

        if (securityStamp is not null)
        {
            claims.Add(new Claim(JobsDashboardAuthentication.SecurityStampClaimType, securityStamp));
        }

        var identity = new ClaimsIdentity(claims, JobsDashboardAuthentication.SchemeName);

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            properties ?? new AuthenticationProperties(),
            JobsDashboardAuthentication.SchemeName);

        return $"{JobsCookieName}={options.TicketDataFormat.Protect(ticket)}";
    }

    /// <summary>Seeds one job in the scheduled state, so the retry route has something real to move.</summary>
    private static string SeedScheduledJob(ApiFactory factory) =>
        factory.Services.GetRequiredService<IBackgroundJobClient>().Create(
            Job.FromExpression(() => JobsDashboardProbeJob.RunAsync(CancellationToken.None)),
            new ScheduledState(TimeSpan.FromHours(1)));

    /// <summary>The antiforgery header name, token and cookie the dashboard page handed out.</summary>
    private static async Task<AntiforgeryToken> AntiforgeryAsync(HttpResponseMessage page)
    {
        var html = await page.Content.ReadAsStringAsync(Ct);

        var header = MetaContent(html, "csrf-header")
            .ShouldNotBeNull("the dashboard page must hand the antiforgery header name to its JavaScript");
        var token = MetaContent(html, "csrf-token")
            .ShouldNotBeNull("the dashboard page must hand the antiforgery token to its JavaScript");
        var cookie = SetCookiePairs(page)
            .FirstOrDefault(pair => pair.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase))
            .ShouldNotBeNull("the page's antiforgery cookie is what the token is validated against");

        return new AntiforgeryToken(header, token, cookie);
    }

    /// <summary>The value of one <c>&lt;meta name="…" content="…"&gt;</c> of the rendered page.</summary>
    private static string? MetaContent(string html, string name)
    {
        var match = Regex.Match(
            html,
            $"<meta name=\"{Regex.Escape(name)}\" content=\"([^\"]*)\">",
            RegexOptions.CultureInvariant);

        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The <c>name=value</c> of one cookie a response set, or null when it set none. A cookie the handler
    /// deletes is signalled with the empty value (<c>name=;</c> plus an expiry in the past), which is not
    /// a cookie handed out, so it reads as null here too.
    /// </summary>
    private static string? CookiePair(HttpResponseMessage response, string name)
    {
        var header = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal))
            : null;

        if (header is null)
        {
            return null;
        }

        var pair = header[..header.IndexOf(';', StringComparison.Ordinal)];

        return pair.Length == name.Length + 1 ? null : pair;
    }

    /// <summary>The whole <c>Set-Cookie</c> value of the dashboard cookie, flags included.</summary>
    private static string SetCookieFlags(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.First(value => value.StartsWith(JobsCookieName + "=", StringComparison.Ordinal))
            : string.Empty;

    /// <summary>Every cookie of a response as <c>name=value</c>.</summary>
    private static IReadOnlyList<string> SetCookiePairs(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Select(value => value[..value.IndexOf(';', StringComparison.Ordinal)])]
            : [];

    /// <summary>
    /// Every <c>Set-Cookie</c> header of a response, verbatim. A deletion is read off the raw header
    /// (<c>__Host-nts-jobs=;</c> plus an expiry in the past): <see cref="SetCookiePairs"/> truncates at
    /// the first <c>;</c>, which is exactly the character a deletion's empty value is followed by.
    /// </summary>
    private static IReadOnlyList<string> SetCookieHeaders(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values] : [];

    /// <summary>The audit rows of one action.</summary>
    private long Rows(string action) =>
        RowsWith(action, string.Empty);

    /// <summary>The audit rows of one action matching an extra <c>AND …</c> predicate.</summary>
    private long RowsWith(string action, string predicate) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE action = '{action}'{predicate}");

    /// <summary>
    /// The audit rows of one action written by one actor. One database per test *class* (09 README D13)
    /// is shared by every test of this class, so a count that is not scoped to the actor this test used
    /// would read the rows of the tests that ran before it.
    /// </summary>
    private long ActorRows(string action, Guid actorUserId, string predicate = "") =>
        RowsWith(action, $" AND actor_user_id = '{actorUserId}'{predicate}");

    /// <summary>The antiforgery material of one dashboard page.</summary>
    private sealed record AntiforgeryToken(string HeaderName, string Token, string Cookie);

    /// <summary>The value of one response header, or empty when the response carries none.</summary>
    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : string.Empty;

    /// <summary>
    /// A dashboard command POST as the middleware sees it after the dashboard ran: a form-encoded body,
    /// the session the authorization filter leaves, and a response status the branch produced.
    /// </summary>
    /// <remarks>
    /// The request's own cancellation token is what the test drives (SEC-0926T2-01); the pipeline is
    /// replaced by the caller's delegate, because the middleware's contract is what is under test — the
    /// services and the audit row are the host's own.
    /// </remarks>
    private static DefaultHttpContext CommandContext(
        IServiceProvider services,
        Guid actorUserId,
        string path,
        CancellationToken requestAborted)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
        };

        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream();
        context.RequestAborted = requestAborted;
        context.Response.Body = new MemoryStream();
        context.Response.StatusCode = StatusCodes.Status204NoContent;

        // The filter's item: without it the middleware audits nothing, whatever the request did.
        context.Items[JobsDashboardAuthentication.SessionItemKey] = new JobsDashboardSession(
            actorUserId,
            PlatformRole.Owner,
            Guid.CreateVersion7().ToString("N"),
            null);

        return context;
    }

    /// <summary>The audit middleware over a pipeline that did nothing (the command already dispatched).</summary>
    private static JobsDashboardAuditMiddleware Middleware(IServiceProvider services) =>
        new(
            _ => Task.CompletedTask,
            services.GetRequiredService<IOptions<AppOptions>>(),
            services.GetRequiredService<IMemoryCache>(),
            services.GetRequiredService<ILogger<JobsDashboardAuditMiddleware>>());

    /// <summary>Keeps every line a host rendered, with its category (the F-LOG probe reads them).</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];
        private readonly Lock _gate = new();

        internal IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private void Capture(string rendered)
        {
            lock (_gate)
            {
                _lines.Add(rendered);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                owner.Capture($"{categoryName}: {formatter(state, exception)}");
            }
        }
    }
}

/// <summary>
/// The job class the dashboard's retry test enqueues: public, so Hangfire can serialize it, and inert,
/// because nothing in the Api consumes the queue — the Api runs no Hangfire server (09 README D12).
/// </summary>
public static class JobsDashboardProbeJob
{
    /// <summary>The job body; the test only cares that the retry moved the job's state.</summary>
    /// <param name="ct">Hangfire's cancellation token.</param>
    public static Task RunAsync(CancellationToken ct) => Task.CompletedTask;
}
