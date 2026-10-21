using System.Net;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Security;

/// <summary>
/// The two CORS policies of 09 README BD12 through the real host (09.9.4-T1): the allow-list is exactly
/// <c>App:PublicOrigin</c>, credentials are allowed on <c>/api/v1/auth/*</c> only, and the preflight
/// admits <c>Authorization</c> plus the API's own headers [RULED — README §B10 WT42].
/// </summary>
/// <remarks>
/// The policies are applied as endpoint metadata (<c>RequireCors</c>) on the real route groups, so the
/// requests go to real routes: <c>/api/v1/episodes</c> carries <c>spa</c> and
/// <c>/api/v1/auth/login</c> carries <c>spa-auth</c>. A preflight is answered by the CORS middleware
/// before authorization, so no request here needs a token — and an actual cross-origin request gets its
/// <c>Access-Control-Allow-Origin</c> whatever status the handler produces.
/// </remarks>
public sealed class CorsTests(TestDatabase database) : IntegrationTest(database)
{
    private const string AllowOrigin = "Access-Control-Allow-Origin";
    private const string AllowCredentials = "Access-Control-Allow-Credentials";
    private const string AllowHeaders = "Access-Control-Allow-Headers";
    private const string OtherOrigin = "https://evil.example";

    [Fact]
    public async Task Allowed_origin_gets_acao()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        using var response = await GetWithOriginAsync(client, "/api/v1/episodes", ApiFactory.PublicOrigin);

        // The route itself answers 401 (no token): the CORS headers are written by the middleware
        // before the handler, so they must survive whatever the endpoint produces.
        response.Headers.TryGetValues(AllowOrigin, out var origins).ShouldBeTrue();
        origins!.ShouldHaveSingleItem().ShouldBe(ApiFactory.PublicOrigin);
    }

    [Fact]
    public async Task Other_origin_gets_no_acao()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        using var response = await GetWithOriginAsync(client, "/api/v1/episodes", OtherOrigin);

        response.Headers.TryGetValues(AllowOrigin, out _).ShouldBeFalse(
            "the allow-list is exactly App:PublicOrigin, never a wildcard and never a reflected origin");
    }

    [Fact]
    public async Task Credentials_only_on_auth_routes()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        using var auth = await PreflightAsync(client, "/api/v1/auth/login", "POST", "Authorization");
        using var api = await PreflightAsync(client, "/api/v1/episodes", "GET", "Authorization");

        auth.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Header(auth, AllowCredentials).ShouldBe("true", "the refresh cookie needs credentials on the auth routes (BD3)");
        api.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Header(api, AllowCredentials).ShouldBeEmpty("the versioned API group never allows credentials");
    }

    [Fact]
    public async Task Preflight_allows_if_match_and_idempotency_key()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        using var response = await PreflightAsync(
            client,
            "/api/v1/episodes",
            "GET",
            "If-Match, Idempotency-Key");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var allowed = Header(response, AllowHeaders);
        allowed.ShouldContain("If-Match");
        allowed.ShouldContain("Idempotency-Key");
    }

    [Fact]
    public async Task Preflight_allows_authorization_header()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        using var response = await PreflightAsync(client, "/api/v1/episodes", "GET", "Authorization");

        response.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "without Authorization in the allow-list the preflight of every authenticated call would fail");
        Header(response, AllowHeaders).ShouldContain("Authorization");
        Header(response, AllowOrigin).ShouldBe(ApiFactory.PublicOrigin);
    }

    [Fact]
    public async Task Null_origin_gets_no_acao()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        // `Origin: null` is what a sandboxed iframe or a `data:`/`file:` page presents. It is a literal
        // the browser sends, never a configured origin, so the allow-list must not reflect it.
        using var response = await GetWithOriginAsync(client, "/api/v1/episodes", "null");

        response.Headers.TryGetValues(AllowOrigin, out _).ShouldBeFalse(
            "the allow-list is exactly App:PublicOrigin; the literal null origin is not it");
    }

    [Fact]
    public async Task Evil_origin_preflight_gets_no_cors_headers()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        // The preflight of the credentialed auth route from a foreign origin: the allow-list must refuse
        // it in every direction (no origin, no credentials, no allowed headers).
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        request.Headers.Add("Origin", OtherOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "Authorization");

        using var response = await client.SendAsync(request, TestContextToken.Value);

        Header(response, AllowOrigin).ShouldBeEmpty();
        Header(response, AllowCredentials).ShouldBeEmpty(
            "credentials are only for the allow-listed origin on the auth routes");
        Header(response, AllowHeaders).ShouldBeEmpty();
    }

    [Fact]
    public async Task Credentialed_request_to_non_auth_route_gets_no_acac()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        // A real (non-preflight) request that carries a cookie: the `spa` policy answers with the origin
        // but never with `Access-Control-Allow-Credentials`, whatever status the route produces. The
        // route itself answers 401 (no token), so the header set is the middleware's, not the handler's.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/episodes");
        request.Headers.Add("Origin", ApiFactory.PublicOrigin);
        request.Headers.Add("Cookie", "nts_probe=1");

        using var response = await client.SendAsync(request, TestContextToken.Value);

        Header(response, AllowOrigin).ShouldBe(ApiFactory.PublicOrigin);
        Header(response, AllowCredentials).ShouldBeEmpty("the versioned API group never allows credentials");
    }

    private static async Task<HttpResponseMessage> GetWithOriginAsync(
        HttpClient client,
        string path,
        string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", origin);

        return await client.SendAsync(request, TestContextToken.Value);
    }

    private static async Task<HttpResponseMessage> PreflightAsync(
        HttpClient client,
        string path,
        string method,
        string requestHeaders)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", ApiFactory.PublicOrigin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", requestHeaders);

        return await client.SendAsync(request, TestContextToken.Value);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : string.Empty;
}
