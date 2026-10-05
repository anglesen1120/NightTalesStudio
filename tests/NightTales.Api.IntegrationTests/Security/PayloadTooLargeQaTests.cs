using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Features.Research;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Security;

/// <summary>
/// Independent QA for the 413 → <c>payload_too_large</c> problem (S21 follow-up): boundary (at the limit vs one byte
/// over), declared and chunked bodies, per-endpoint limits, problem shape, CORS, no echo, and the frozen bodiless
/// 413 of the internal fetcher listener.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PayloadTooLargeQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const int Mebibyte = 1024 * 1024;
    private const string Sentinel = "QA413-SENTINEL-BODY-BYTES";
    private const string ApprovePath = "/api/v1/episodes/00000000-0000-0000-0000-000000000001/story/approve";
    private const string StoryPath = "/api/v1/episodes/00000000-0000-0000-0000-000000000001/story";

    private static CancellationToken Ct => TestContextToken.Value;

    [Theory]
    [InlineData("POST", ApprovePath)]
    [InlineData("PUT", StoryPath)]
    [InlineData("POST", "/api/v1/auth/login")]
    public async Task DeclaredBodyOneByteOverTheCap_IsAProblem_AndExactlyAtTheCapPassesTheLimiter(string method, string path)
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var client = factory.AnonymousClient();

        using var atLimit = await client.SendAsync(Request(method, path, Mebibyte), Ct);
        using var over = await client.SendAsync(Request(method, path, Mebibyte + 1), Ct);

        atLimit.StatusCode.ShouldNotBe(HttpStatusCode.RequestEntityTooLarge, "the cap is inclusive");
        over.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        await AssertProblemAsync(over);
    }

    [Fact]
    public async Task ChunkedBodyOverTheCap_OnAPublicRoute_IsNeverASuccess_AndNeverAServerError()
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var client = factory.AnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new UnknownLengthJson(Padded(Mebibyte + 1)),
        };

        using var response = await client.SendAsync(request, Ct);

        // The test host is not Kestrel; if it does not enforce the streaming cap the handler sees a malformed
        // body (4xx). Either way it must not be 2xx or 5xx; when the cap fires it must be the problem.
        ((int)response.StatusCode).ShouldBeInRange(400, 499, await response.Content.ReadAsStringAsync(Ct));
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            await AssertProblemAsync(response);
        }
    }

    [Fact]
    public async Task RouteWithItsOwnLargerLimit_StillHonoursIt_AndRefusesOneByteOverIt()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.AnonymousClient();

        using var middle = await client.PostAsync(ProbeEndpoints.LargePostEchoPath, Octets(Mebibyte + 1), Ct);
        using var atOwn = await client.PostAsync(
            ProbeEndpoints.LargePostEchoPath, Octets((int)ProbeEndpoints.LargePostEchoLimit), Ct);
        using var overOwn = await client.PostAsync(
            ProbeEndpoints.LargePostEchoPath, Octets((int)ProbeEndpoints.LargePostEchoLimit + 1), Ct);

        middle.StatusCode.ShouldBe(HttpStatusCode.OK, "1 MiB + 1 is within this route's own 4 MiB cap");
        atOwn.StatusCode.ShouldBe(HttpStatusCode.OK);
        overOwn.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        await AssertProblemAsync(overOwn);
    }

    [Theory]
    [InlineData("POST", ApprovePath, false)]
    [InlineData("PUT", StoryPath, false)]
    [InlineData("POST", "/api/v1/auth/login", true)]
    public async Task Problem_CarriesCors_AndDoesNotEchoTheBody(string method, string path, bool credentials)
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var client = factory.AnonymousClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = Json(Padded(Mebibyte + 1)),
        };
        request.Headers.Add("Origin", ApiFactory.PublicOrigin);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(ApiFactory.PublicOrigin);
        response.Headers.TryGetValues("Access-Control-Allow-Credentials", out var allowCredentials)
            .ShouldBe(credentials, "credentials are allowed on /auth/* only");
        if (credentials)
        {
            allowCredentials!.ShouldHaveSingleItem().ShouldBe("true");
        }

        var text = await AssertProblemAsync(response);
        text.ShouldNotContain(Sentinel);
    }

    [Fact]
    public async Task InternalFetcherListener_DeclaredOversizedBody_IsStill413_WithoutAProblemBody()
    {
        new FetchTaskSeed(Database).ResetTasks();
        using var factory = new ApiFactory(Database, new FakeClock()).WithFetcherInternalApi(10_000);
        using var client = factory.InternalClient();

        using var response = await client.PostAsync(
            "/internal/fetcher/v1/tasks/lease", new ByteArrayContent(Padded(Mebibyte + 1)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType?.MediaType.ShouldNotBe("application/problem+json");
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty("the fetcher listener's 413 is frozen bodiless");
    }

    private static HttpRequestMessage Request(string method, string path, int bytes) =>
        new(new HttpMethod(method), path) { Content = Json(Padded(bytes)) };

    private static byte[] Padded(int bytes)
    {
        var buffer = new byte[bytes];
        Array.Fill(buffer, (byte)' ');
        Encoding.ASCII.GetBytes(Sentinel).CopyTo(buffer, 0);

        return buffer;
    }

    private static ByteArrayContent Json(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return content;
    }

    private static ByteArrayContent Octets(int bytes)
    {
        var content = new ByteArrayContent(new byte[bytes]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        return content;
    }

    private static async Task<string> AssertProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var document = JsonDocument.Parse(text);
        var problem = document.RootElement;
        problem.GetProperty("code").GetString().ShouldBe("payload_too_large");
        problem.GetProperty("status").GetInt32().ShouldBe(413);
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        Encoding.UTF8.GetByteCount(text).ShouldBeLessThan(1024);

        return text;
    }

    private sealed class UnknownLengthJson : HttpContent
    {
        private readonly byte[] _bytes;

        public UnknownLengthJson(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes.AsMemory()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }
}
