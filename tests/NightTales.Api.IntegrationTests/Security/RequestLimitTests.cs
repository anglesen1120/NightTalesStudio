using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NightTales.Api.Http;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Security;

/// <summary>
/// The 1 MiB request-body cap of 09 README BD12 through the real host (09.9.4-T1): a body over the
/// default is refused with 413 before the handler runs, and an endpoint that declares its own larger
/// <c>RequestSizeLimitAttribute</c> accepts its own size. Because the CORS middleware runs before the
/// cap, a refused cross-origin request still carries <c>Access-Control-Allow-Origin</c>.
/// </summary>
/// <remarks>
/// The test host is not Kestrel, so the server-level cap alone would be unobservable here: the API's own
/// request-limit middleware sets the same number on the per-request
/// <c>IHttpMaxRequestBodySizeFeature</c> and refuses an oversized <c>Content-Length</c> before the
/// handler — which is the mechanism these tests exercise.
/// </remarks>
public sealed class RequestLimitTests(TestDatabase database) : IntegrationTest(database)
{
    private const int Mebibyte = 1024 * 1024;

    [Fact]
    public async Task Default_endpoint_rejects_2mib_body_with_413()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        using var response = await client.PostAsync(
            ProbeEndpoints.PostEchoPath,
            Body(2 * Mebibyte),
            TestContextToken.Value);

        response.StatusCode.ShouldBe(
            HttpStatusCode.RequestEntityTooLarge,
            "the host default is 1 MiB and the probe route declares nothing larger");
        await AssertPayloadTooLargeProblemAsync(response);
    }

    [Fact]
    public async Task Endpoint_override_accepts_its_limit()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        using var response = await client.PostAsync(
            ProbeEndpoints.LargePostEchoPath,
            Body(2 * Mebibyte),
            TestContextToken.Value);

        // The endpoint's own metadata wins over the host default: the handler runs and reads the body.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var echo = await response.Content.ReadFromJsonAsync<ProbeEndpoints.PostEcho>(TestContextToken.Value);
        echo.ShouldNotBeNull();
        echo.Bytes.ShouldBe(2 * Mebibyte);
    }

    [Fact]
    public async Task Body_exactly_at_the_cap_is_accepted()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        using var response = await client.PostAsync(
            ProbeEndpoints.PostEchoPath,
            Body(Mebibyte),
            TestContextToken.Value);

        // The cap is inclusive: a body of exactly 1 MiB is *at* the limit, not over it, and the handler
        // reads it whole.
        response.StatusCode.ShouldBe(HttpStatusCode.OK, "1 MiB is at the cap, not above it");
        var echo = await response.Content.ReadFromJsonAsync<ProbeEndpoints.PostEcho>(TestContextToken.Value);
        echo.ShouldNotBeNull();
        echo.Bytes.ShouldBe(Mebibyte);
    }

    [Fact]
    public async Task Body_one_byte_over_the_cap_is_rejected_with_413()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        using var response = await client.PostAsync(
            ProbeEndpoints.PostEchoPath,
            Body(Mebibyte + 1),
            TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);

        // The small problem replaces the echo: the handler never runs on an oversized declared body.
        await AssertPayloadTooLargeProblemAsync(response);
    }

    [Fact]
    public async Task Oversized_body_is_413_that_carries_cors_allow_origin()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        // The auth route carries the `spa-auth` CORS policy (credentials) and the request is a real
        // cross-origin POST, not a preflight, so the CORS headers come from the actual-request path. The
        // content type has to be JSON: with an unmatched content type routing selects the framework's
        // contentless 415 endpoint, which carries no CORS metadata.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonBody(2 * Mebibyte),
        };
        request.Headers.Add("Origin", ApiFactory.PublicOrigin);

        using var response = await client.SendAsync(request, TestContextToken.Value);

        response.StatusCode.ShouldBe(
            HttpStatusCode.RequestEntityTooLarge,
            "the cap is still enforced before the handler");

        // The CORS middleware runs before the body cap, so it executes even though the limiter
        // short-circuits: without it a browser on App:PublicOrigin could not read the 413 at all.
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins).ShouldBeTrue(
            "a 413 refused by the body cap must still carry the CORS allow origin");
        origins!.ShouldHaveSingleItem().ShouldBe(ApiFactory.PublicOrigin);
        response.Headers.TryGetValues("Access-Control-Allow-Credentials", out var credentials).ShouldBeTrue();
        credentials!.ShouldHaveSingleItem().ShouldBe("true");
        response.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeOptions).ShouldBeTrue();
        contentTypeOptions!.ShouldHaveSingleItem().ShouldBe("nosniff");
        await AssertPayloadTooLargeProblemAsync(response);
    }

    private static async Task AssertPayloadTooLargeProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
        var json = await response.Content.ReadAsByteArrayAsync(TestContextToken.Value);
        json.Length.ShouldBeLessThan(1024, "the refusal must stay small regardless of request size");
        using var document = JsonDocument.Parse(json);
        var problem = document.RootElement;
        problem.GetProperty("code").GetString().ShouldBe("payload_too_large");
        problem.GetProperty("type").GetString().ShouldBe(ErrorCatalogue.ProblemType("payload_too_large"));
        problem.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        problem.GetProperty("status").GetInt32().ShouldBe(413);
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    private static ByteArrayContent Body(int bytes)
    {
        var content = new ByteArrayContent(new byte[bytes]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        return content;
    }

    private static ByteArrayContent JsonBody(int bytes)
    {
        var content = new ByteArrayContent(new byte[bytes]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return content;
    }
}
