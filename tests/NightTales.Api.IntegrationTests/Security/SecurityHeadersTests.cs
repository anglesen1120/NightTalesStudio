using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Security;

/// <summary>
/// The hardening response headers of 09 README BD12 through the real host (09.9.4-T1): the three
/// headers are on every response — including an error one — HSTS is on outside Development only, and
/// the CSP follows the content type: a JSON answer never carries one, and an HTML answer carries the
/// policy of the surface that produced it. The probe routes stand in for the docs page
/// (<see cref="DocsCsp"/>); the jobs dashboard's own policy is asserted by <c>JobsDashboardTests</c>
/// (09.2.6-T2), which is the Api's second HTML surface.
/// </summary>
/// <remarks>
/// The header-writing middleware and the exception handler interact: <c>UseExceptionHandler</c> clears
/// the response it catches, which would drop headers written before the handler ran, so the headers are
/// written again from <c>OnStarting</c> — which is what <c>Headers_survive_the_exception_handler</c>
/// proves, while <c>Headers_on_error_response</c> covers the ordinary 400 path.
/// </remarks>
public sealed class SecurityHeadersTests(TestDatabase database) : IntegrationTest(database)
{
    private const string ContentTypeOptions = "X-Content-Type-Options";
    private const string ReferrerPolicy = "Referrer-Policy";
    private const string FrameOptions = "X-Frame-Options";
    private const string Hsts = "Strict-Transport-Security";
    private const string Csp = "Content-Security-Policy";
    private const string DocsCsp = "default-src 'none'; frame-ancestors 'none'; img-src 'self' data:";

    /// <summary>
    /// An HTTPS host: HSTS is only ever sent over HTTPS and never to a localhost host, so a plain
    /// <c>CreateClient()</c> could never observe it (the production host in the test is not Kestrel).
    /// </summary>
    private static readonly WebApplicationFactoryClientOptions HttpsClientOptions = new()
    {
        BaseAddress = new Uri("https://api.nighttales.test"),
        AllowAutoRedirect = false,
    };

    [Fact]
    public async Task Headers_on_success_response()
    {
        await using var factory = ApiFactory.ProductionHost(
            Database.ConnectionString,
            ProductionWithProbes);

        using var client = factory.CreateClient(HttpsClientOptions);
        using var response = await client.GetAsync(ProbeEndpoints.HtmlPath, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ShouldCarryHardeningHeaders(response);

        var hsts = Header(response, Hsts);
        hsts.ShouldNotBeEmpty("HSTS is sent on an HTTPS response outside Development (BD12)");
        hsts.ShouldContain("max-age=31536000");
        hsts.ShouldContain("includeSubDomains");
    }

    [Fact]
    public async Task Headers_on_error_response()
    {
        await using var factory = ApiFactory.ProductionHost(
            Database.ConnectionString,
            ProductionWithProbes);

        using var client = factory.CreateClient(HttpsClientOptions);
        using var response = await client.GetAsync(ProbeEndpoints.BadRequestPath, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ShouldCarryHardeningHeaders(response);
        Header(response, Hsts).ShouldNotBeEmpty("a client error carries the same headers as a success");
    }

    [Fact]
    public async Task Headers_survive_the_exception_handler()
    {
        await using var factory = ApiFactory.ProductionHost(
            Database.ConnectionString,
            ProductionWithProbes);

        using var client = factory.CreateClient(HttpsClientOptions);
        using var response = await client.GetAsync(ProbeEndpoints.ThrowPath, TestContextToken.Value);

        // The exception handler clears the response it catches (`Response.Clear()` resets the status and
        // drops the headers), so this only passes because the hardening middleware writes its three
        // headers again from `OnStarting` — a callback the clear does not drop (the handler registers its
        // own the same way).
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        ShouldCarryHardeningHeaders(response);

        // HSTS is deliberately not asserted here: `UseHsts` writes its header before the handler runs
        // (the framework's middleware has no `OnStarting` of its own), so the clear of a 500 loses it.
        // The spec's error case is the 400 of `Headers_on_error_response`, which carries it (plan
        // §Acceptance checks: "a 200 and a 400 response both carry Strict-Transport-Security").
    }

    [Fact]
    public async Task Hsts_absent_in_development()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);

        using var client = factory.CreateClient(HttpsClientOptions);
        using var response = await client.GetAsync(ProbeEndpoints.HtmlPath, TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Even over HTTPS: the daily dev path is plain HTTP, so HSTS must not pin the browser there.
        Header(response, Hsts).ShouldBeEmpty();
        ShouldCarryHardeningHeaders(response);
    }

    [Fact]
    public async Task Docs_page_has_csp()
    {
        await using var factory = ApiFactory.ProductionHost(
            Database.ConnectionString,
            ProductionWithProbes);

        using var client = factory.CreateClient(HttpsClientOptions);
        using var response = await client.GetAsync(ProbeEndpoints.HtmlPath, TestContextToken.Value);

        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        Header(response, Csp).ShouldBe(
            DocsCsp, "an HTML answer outside /internal/jobs carries the docs policy (BD12)");
    }

    [Fact]
    public async Task Json_response_has_no_csp()
    {
        await using var factory = ApiFactory.ProductionHost(
            Database.ConnectionString,
            ProductionWithProbes);

        using var client = factory.CreateClient(HttpsClientOptions);
        using var response = await client.GetAsync(ProbeEndpoints.WhoAmIPath, TestContextToken.Value);

        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        Header(response, Csp).ShouldBeEmpty("the CSP belongs to the HTML answers alone");
        ShouldCarryHardeningHeaders(response);
    }

    /// <summary>Production and the probe routes, the two additions this class needs on the real host.</summary>
    private static void ProductionWithProbes(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(
            new MapEndpointsStartupFilter(ProbeEndpoints.Map)));
    }

    private static void ShouldCarryHardeningHeaders(HttpResponseMessage response)
    {
        Header(response, ContentTypeOptions).ShouldBe("nosniff");
        Header(response, ReferrerPolicy).ShouldBe("no-referrer");
        Header(response, FrameOptions).ShouldBe("DENY");
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : string.Empty;
}
