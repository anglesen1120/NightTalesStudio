using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// The isolation of the fetcher's internal listener (02.5.2-T2 acceptance checks): the routes exist only on port 8081, the
/// listener accepts nothing but the shared token (a user JWT is not one), a refusal is a bare <c>401</c>, and the Api
/// refuses to start when the listener is enabled without a usable <c>FETCHER_API_TOKEN</c>. The in-process test server has
/// no sockets, so "arrived on port N" is the connection's local port, exactly what the production pipeline switches on.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InternalListenerIsolationTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    private ApiFactory NewFactory() =>
        new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start)).WithFetcherInternalApi();

    private static StringContent LeaseJson() =>
        new("{\"workerId\":\"fetcher-1\",\"max\":4}", Encoding.UTF8, "application/json");

    // ------------------------------------------------------------------ the public port

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task InternalListener_LeaseRouteAbsentOnPublicPort(string method)
    {
        using var factory = NewFactory();
        using var client = factory.PublicPortClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.FetcherToken);
        using var request = new HttpRequestMessage(new HttpMethod(method), FetchTaskSeed.Prefix + "/lease")
        {
            Content = method == "POST" ? LeaseJson() : null,
        };

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the route exists only on :8081, even for the right token");
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("00000000-0000-0000-0000-000000000001/heartbeat")]
    [InlineData("00000000-0000-0000-0000-000000000001/result")]
    public async Task InternalListener_NoInternalRouteExistsOnPublicPort(string tail)
    {
        using var factory = NewFactory();
        using var client = factory.PublicPortClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.FetcherToken);

        using var response = await client.PostAsync($"{FetchTaskSeed.Prefix}/{tail}", LeaseJson(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InternalListener_PublicApiIsNotServedOnTheInternalPort()
    {
        var seed = new FetchTaskSeed(Database);
        var world = await seed.SeedWorldAsync();
        using var factory = NewFactory();
        using var client = factory.InternalClient(token: null);
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, world.UserId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, "user");

        using var response = await client.GetAsync($"/api/v1/episodes/{world.EpisodeId}/sources", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the internal pipeline authenticates the fetcher token only");
    }

    [Fact]
    public async Task InternalListener_PublicApiIsNotServedOnTheInternalPort_EvenWithTheFetcherToken()
    {
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        using var response = await client.GetAsync("/api/v1/episodes", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InternalListener_Disabled_NoInternalRouteExistsOnAnyPort()
    {
        using var factory = new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start));
        using var client = factory.InternalClient();

        using var response = await client.PostAsync(FetchTaskSeed.Prefix + "/lease", LeaseJson(), Ct);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("[");
    }

    // ------------------------------------------------------------------ authentication on the internal port

    [Fact]
    public async Task InternalListener_RejectsUserJwt()
    {
        var seed = new FetchTaskSeed(Database);
        seed.ResetTasks();
        var world = await seed.SeedWorldAsync();
        seed.AddQueued(world, "jwt.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient(token: UserJwt(world.UserId));
        // The test host's own user scheme is the default one; its headers must not authenticate here either.
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, world.UserId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, "admin");

        using var response = await client.PostAsync(FetchTaskSeed.Prefix + "/lease", LeaseJson(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        seed.LeasedCount().ShouldBe(0);
    }

    [Fact]
    public async Task InternalListener_NoToken_Returns401WithAnEmptyBody()
    {
        var seed = new FetchTaskSeed(Database);
        seed.ResetTasks();
        var world = await seed.SeedWorldAsync();
        seed.AddQueued(world, "notoken.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient(token: null);

        using var response = await client.PostAsync(FetchTaskSeed.Prefix + "/lease", LeaseJson(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        response.Content.Headers.ContentType.ShouldBeNull();
        seed.LeasedCount().ShouldBe(0);
    }

    [Theory]
    [InlineData("wrong-token-0123456789abcdef0123456789")]
    [InlineData("x")]
    [InlineData("test-fetcher-api-token-0123456789abcdef-and-more")]
    [InlineData("test-fetcher-api-token-0123456789abcde")]
    [InlineData("TEST-FETCHER-API-TOKEN-0123456789ABCDEF")]
    public async Task InternalListener_WrongToken_Returns401WithAnEmptyBody(string token)
    {
        using var factory = NewFactory();
        using var client = factory.InternalClient(token);

        using var response = await client.PostAsync(FetchTaskSeed.Prefix + "/lease", LeaseJson(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task InternalListener_HugeWrongToken_Returns401_NotA500()
    {
        using var factory = NewFactory();
        using var client = factory.InternalClient(new string('t', 6_000));

        using var response = await client.PostAsync(FetchTaskSeed.Prefix + "/lease", LeaseJson(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("Token")]
    [InlineData("")]
    public async Task InternalListener_CorrectTokenUnderAnotherScheme_Returns401(string scheme)
    {
        using var factory = NewFactory();
        using var client = factory.InternalClient(token: null);
        using var request = new HttpRequestMessage(HttpMethod.Post, FetchTaskSeed.Prefix + "/lease") { Content = LeaseJson() };
        request.Headers.TryAddWithoutValidation("Authorization", $"{scheme} {ApiFactory.FetcherToken}".Trim());

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InternalListener_TokenInTheQueryString_Returns401()
    {
        using var factory = NewFactory();
        using var client = factory.InternalClient(token: null);

        using var response = await client.PostAsync($"{FetchTaskSeed.Prefix}/lease?access_token={ApiFactory.FetcherToken}", LeaseJson(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InternalListener_AnUnknownPathWithoutAToken_Returns401_NotTheRoutingAnswer()
    {
        using var factory = NewFactory();
        using var anonymous = factory.InternalClient(token: null);
        using var authorised = factory.InternalClient();

        using var withoutToken = await anonymous.GetAsync("/internal/fetcher/v1/nothing", Ct);
        using var withToken = await authorised.GetAsync("/internal/fetcher/v1/nothing", Ct);

        withoutToken.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "an unauthenticated caller learns nothing about which routes exist");
        withToken.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InternalListener_CorrectToken_AuthenticatesAndLeases()
    {
        var seed = new FetchTaskSeed(Database);
        seed.ResetTasks();
        var world = await seed.SeedWorldAsync();
        var task = seed.AddQueued(world, "ok.example");
        using var factory = NewFactory();
        using var client = factory.InternalClient();

        var items = await FetchTaskSeed.LeaseItemsAsync(client);

        items.ShouldHaveSingleItem().GetProperty("taskId").GetGuid().ShouldBe(task.TaskId);
    }

    [Fact]
    public async Task InternalListener_WrongTokenResponseTime_DoesNotGrowWithTheTokenLength()
    {
        // Hash-then-compare makes the comparison independent of the presented length. A wall-clock test is tolerant by
        // design: the long token may cost its (linear, microsecond) hashing and header transfer, but a length-dependent
        // early-exit comparison over 6 KB would be orders of magnitude away, so a generous ratio still separates them.
        using var factory = NewFactory();
        using var shortClient = factory.InternalClient("a");
        using var longClient = factory.InternalClient(new string('a', 6_000));
        await TimeAsync(shortClient, 20);
        await TimeAsync(longClient, 20);

        var shortTicks = await TimeAsync(shortClient, 200);
        var longTicks = await TimeAsync(longClient, 200);

        longTicks.ShouldBeLessThan(Math.Max(shortTicks * 5, TimeSpan.FromMilliseconds(250).Ticks), "a 6000-char token must not be much slower to refuse than a 1-char one");
    }

    // ------------------------------------------------------------------ start-up

    [Fact]
    public void Api_Startup_FailsWithoutFetcherToken()
    {
        using var factory = new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start))
            .WithSetting("Fetcher:Internal:Enabled", "true")
            .WithSetting("FETCHER_API_TOKEN", null);

        var failure = Should.Throw<Exception>(() => factory.CreateClient());

        Flatten(failure).ShouldContain("FETCHER_API_TOKEN");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short-token")]
    [InlineData("0123456789012345678901234567890")]
    public void Api_Startup_FailsWithAnEmptyOrShortFetcherToken(string token)
    {
        using var factory = new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start))
            .WithSetting("Fetcher:Internal:Enabled", "true")
            .WithSetting("FETCHER_API_TOKEN", token);

        var failure = Should.Throw<Exception>(() => factory.CreateClient());

        Flatten(failure).ShouldContain("FETCHER_API_TOKEN");
    }

    [Theory]
    [InlineData("Fetcher:Internal:ListenAddress", "not-an-address", "Fetcher:Internal:ListenAddress")]
    [InlineData("Fetcher:Internal:MaxBodyBytes", "10", "Fetcher:Internal:MaxBodyBytes")]
    [InlineData("Fetcher:Internal:MaxRequestsPerMinutePerWorker", "0", "Fetcher:Internal:MaxRequestsPerMinutePerWorker")]
    [InlineData("Fetcher:LeaseSeconds", "5", "Fetcher:LeaseSeconds")]
    public void Api_Startup_FailsOnABadInternalApiSetting_WithTheKeyNamed(string key, string value, string named)
    {
        using var factory = NewFactory().WithSetting(key, value);

        var failure = Should.Throw<Exception>(() => factory.CreateClient());

        Flatten(failure).ShouldContain(named);
    }

    [Fact]
    public void Api_Startup_WithoutTheListenerEnabled_NeedsNoToken()
    {
        using var factory = new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start)).WithSetting("FETCHER_API_TOKEN", null);

        Should.NotThrow(() => factory.CreateClient().Dispose());
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<long> TimeAsync(HttpClient client, int rounds)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < rounds; i++)
        {
            using var response = await client.PostAsync(FetchTaskSeed.Prefix + "/lease", LeaseJson(), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        return (long)(System.Diagnostics.Stopwatch.GetElapsedTime(started).Ticks);
    }

    /// <summary>A structurally valid ES256 user access token (the test run's key, so even a signature check would pass).</summary>
    private static string UserJwt(Guid userId)
    {
        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"typ\":\"JWT\"}"));
        var payload = B64(Encoding.UTF8.GetBytes(
            $"{{\"sub\":\"{userId}\",\"role\":\"admin\",\"iss\":\"nts-Development\",\"aud\":\"nts-api\",\"iat\":{now},\"exp\":{now + 900}}}"));
        using var key = ECDsa.Create();
        key.ImportFromPem(TestSigningKey.Pem);
        var signature = B64(key.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256));

        return $"{header}.{payload}.{signature}";
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            messages.Add(current.Message);
            if (current.InnerException is null)
            {
                break;
            }
        }

        return string.Join(" | ", messages);
    }
}
