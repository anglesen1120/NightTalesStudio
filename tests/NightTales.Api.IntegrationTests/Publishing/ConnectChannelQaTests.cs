using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common.Errors;
using NightTales.Application.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Email;
using NightTales.TestKit.Google;
using NightTales.TestKit.Publishing;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// Independent QA probes of 06.1.2-T1 that the task file's own suite does not carry: the state TTL
/// boundary, parameter abuse (duplicate, oversized, non-ASCII, redirect-shaped values), cross-site
/// navigation headers, non-GET verbs, an unserved platform, log redaction on <em>failure</em> paths,
/// tenant isolation of the purge, the purge of a finished row, ten parallel navigations, the rate-limit
/// rejection shape, and <c>YouTube:Pkce=false</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every case reuses <see cref="ConnectChannelHarness"/>; the two cases that need host configuration the
/// harness does not expose (a disabled rate limiter, <c>YouTube:Pkce=false</c>) build their own world with
/// <see cref="ApiFactory"/> and the same seams.
/// </para>
/// <para>
/// The class shares one database with no other class (the per-class <c>TestDatabase</c> fixture) and every
/// start wipes <c>oauth_states</c>, exactly as the task suite's harness does, so no probe reads a row of
/// another probe.
/// </para>
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
public sealed class ConnectChannelQaTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The two fixed <c>302</c> targets of the callback (06.1 plan §5.1).</summary>
    private const string ConnectedRedirect = "/channels?connected=youtube";

    private static string InvalidRedirect => "/channels?error=" + ErrorCodes.OauthStateInvalid;

    [Fact]
    public async Task Callback_StateAtTheExactTtlBoundary_IsRefused_AndOneSecondEarlierConnects()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);

        var first = await harness.ConnectAsync(Ct);
        var firstCode = await harness.AuthorizeAsync(first.AuthorizationUrl, Ct);

        // `expires_at > @now` is strict: at exactly +10 minutes the state is already dead.
        harness.Clock.Advance(TimeSpan.FromMinutes(10));

        using (var refused = await harness.CallbackAsync(firstCode, first.State, first.NonceCookie, Ct))
        {
            await AssertStateInvalidAsync(refused);
        }

        harness.Google.TokenRequestCount.ShouldBe(0, "a state exactly at its TTL exchanges nothing");
        harness.StateValueFor("used_at", first.State).ShouldBeNullOrEmpty();

        // One second inside the TTL still connects: the boundary is the expiry instant, not one second before.
        // The connect route also demands a <= 5 min re-auth, so the caller re-authenticates as production does.
        await AssertReauthenticatedAsync(harness);
        var second = await harness.ConnectAsync(Ct);
        var secondCode = await harness.AuthorizeAsync(second.AuthorizationUrl, Ct);

        harness.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));

        using var connected = await harness.CallbackAsync(secondCode, second.State, second.NonceCookie, Ct);

        await AssertConnectedAsync(connected);
        harness.Google.TokenRequestCount.ShouldBe(1);
        harness.Connection.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Callback_WithoutStateOrWithABlankState_IsRefusedWithoutAnExchange()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        // `code` alone, no `state` at all (the task file's own failure row), then an empty `state`.
        using (var missing = await harness.CallbackRawAsync(code, state: null, error: null, connect.NonceCookie, Ct))
        {
            await AssertStateInvalidAsync(missing);
        }

        using (var blank = await harness.CallbackRawAsync(code, state: string.Empty, error: null, connect.NonceCookie, Ct))
        {
            await AssertStateInvalidAsync(blank);
        }

        harness.Google.TokenRequestCount.ShouldBe(0);
        harness.Connection.CallCount.ShouldBe(0);
        harness.AuditRows().ShouldBe(0);
        harness.EmailRows().ShouldBe(0);
    }

    [Fact]
    public async Task Callback_WithBothARefusalAndACode_AnswersDeniedAndNeverExchanges()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        // Google only ever sends one of the two, but a hand-written request may send both: the refusal is
        // checked first, so the code is never exchanged (no half-consumed authorization).
        using var response = await harness.CallbackRawAsync(
            code,
            connect.State,
            error: "access_denied",
            connect.NonceCookie,
            Ct);

        await AssertRedirectAsync(response, "/channels?error=" + ErrorCodes.OauthDenied);
        harness.Google.TokenRequestCount.ShouldBe(0);
        harness.StateValueFor("used_at", connect.State).ShouldNotBeNullOrEmpty("the claimed state stays spent");
    }

    [Theory]
    [InlineData("canary-code&state=a&state=b")]
    [InlineData("canary-code&state=0000000000000000000000000000000000000000000")]
    [InlineData("canary-code&state=%C3%A9tat-%E2%80%8B-%F0%9F%98%80")]
    public async Task Callback_HostileParameters_StayOnTheFixedRedirectWithoutAnOracle(string rawQuery)
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);

        using var request = RawCallback(rawQuery, cookiePair: null);
        using var response = await harness.Anonymous.SendAsync(request, Ct);

        await AssertStateInvalidAsync(response);
        harness.Google.TokenRequestCount.ShouldBe(0);
        harness.StateRows().ShouldBe(0, "a hostile query writes nothing");
    }

    [Fact]
    public async Task Callback_AnOversizedState_IsRefusedWithoutTruncatingIntoAValidOne()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);

        // The real state, padded: a truncating implementation would accept it as the minted one.
        using var request = RawCallback(
            "code=canary-code&state=" + connect.State + new string('x', 4096),
            cookiePair: connect.NonceCookie);
        using var response = await harness.Anonymous.SendAsync(request, Ct);

        await AssertStateInvalidAsync(response);
        harness.Google.TokenRequestCount.ShouldBe(0);
        harness.StateRows().ShouldBe(1, "the minted row stays untouched and unused");
        harness.StateValueFor("used_at", connect.State).ShouldBeNullOrEmpty();
    }

    [Fact]
    public async Task Callback_ValuesShapedLikeARedirect_TaintNoHeader()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);

        var probes = new[]
        {
            "code=%2F%2Fevil.example&state=%2F%2Fevil.example",
            "code=https%3A%2F%2Fevil.example&state=https%3A%2F%2Fevil.example",
            "code=canary%0D%0ALocation%3A%20https%3A%2F%2Fevil.example&state=canary",
            "code=canary&state=canary&error=%2F%2Fevil.example",
        };

        foreach (var probe in probes)
        {
            using var request = RawCallback(probe, cookiePair: null);
            using var response = await harness.Anonymous.SendAsync(request, Ct);

            response.StatusCode.ShouldBe(
                HttpStatusCode.Found,
                $"'{probe}' must stay on the fixed redirect rather than answer {response.StatusCode}");

            var location = response.Headers.Location!.ToString();
            location.ShouldBeOneOf(ConnectedRedirect, InvalidRedirect);
            location.ShouldNotContain("evil", Case.Insensitive);
            response.Headers.ToString().ShouldNotContain("evil", Case.Insensitive);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("evil", Case.Insensitive);
        }

        harness.Google.TokenRequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task Callback_CrossSiteNavigationHeaders_DoNotBlockTheConnect()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        // Google's redirect is a cross-site top-level navigation: the callback must not demand a same-origin
        // request, and the Referer must not decide the answer (yr23: the redirect is the security answer).
        using var request = RawCallback(
            $"code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(connect.State)}",
            connect.NonceCookie);
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Dest", "document");
        request.Headers.Referrer = new Uri("https://evil.example/start");

        using var response = await harness.Anonymous.SendAsync(request, Ct);

        await AssertConnectedAsync(response);
        harness.Google.TokenRequestCount.ShouldBe(1);
        harness.Connection.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Callback_HeadAndOptions_SpendNothingAndLeaveTheStateUsable()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        foreach (var method in new[] { HttpMethod.Head, HttpMethod.Options })
        {
            using var request = new HttpRequestMessage(
                method,
                QueryHelpers.AddQueryString(
                    ConnectChannelHarness.CallbackRoute,
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["code"] = code,
                        ["state"] = connect.State,
                    }));
            request.Headers.Add("Cookie", connect.NonceCookie);

            using var response = await harness.Anonymous.SendAsync(request, Ct);

            response.StatusCode.ShouldNotBe(
                HttpStatusCode.Found,
                $"{method} is not the GET the callback route serves ({(int)response.StatusCode})");
            harness.Google.TokenRequestCount.ShouldBe(0, $"{method} must not exchange");
        }

        harness.StateValueFor("used_at", connect.State)
            .ShouldBeNullOrEmpty("a verb the route does not serve must not spend the single-use state");

        // The real navigation, afterwards, still works: the state was not burned by the probes.
        using var navigation = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertConnectedAsync(navigation);
        harness.Google.TokenRequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Callback_UnknownPlatform_AndUnservedConnectPlatform_Answer404WithoutSideEffects()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        using var callback = new HttpRequestMessage(
            HttpMethod.Get,
            QueryHelpers.AddQueryString(
                "/api/v1/channels/tiktok/callback",
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["code"] = code,
                    ["state"] = connect.State,
                }));
        callback.Headers.Add("Cookie", connect.NonceCookie);

        using (var response = await harness.Anonymous.SendAsync(callback, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.NotFound);
        }

        harness.Google.TokenRequestCount.ShouldBe(0);
        harness.StateValueFor("used_at", connect.State)
            .ShouldBeNullOrEmpty("an unserved platform must not claim the youtube state");

        // The same platform segment on connect: the validator lets it through and the handler answers 404.
        using (var unserved = await harness.Client.PostAsync("/api/v1/channels/tiktok/connect", null, Ct))
        {
            unserved.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await AuthTestFlow.ProblemCodeAsync(unserved)).ShouldBe(ErrorCodes.NotFound);
        }

        harness.StateRows().ShouldBe(1, "no second row for an unserved platform");

        // The documented mapping ignores case on the path segment (PlatformExtensions.TryParse).
        using var cased = await harness.Client.PostAsync("/api/v1/channels/YouTube/connect", null, Ct);

        cased.StatusCode.ShouldBe(HttpStatusCode.OK);
        harness.StateRows().ShouldBe(2);
    }

    [Fact]
    public async Task FailurePaths_LogNoCodeStateNonceOrClientSecret()
    {
        var sink = new TestLogSink();
        var googleOptions = new FakeGoogleOptions();
        var resolver = new FakeOAuthClientResolver
        {
            ActiveClientId = googleOptions.ActiveClientId,
            NextResult = new OAuthClient(googleOptions.ActiveClientId, googleOptions.ActiveClientSecret),
        };

        await using var harness = await ConnectChannelHarness.StartAsync(
            Database,
            Ct,
            googleOptions: googleOptions,
            logSink: sink,
            resolver: resolver);

        // (1) an unknown state with a canary code, carrying the minted cookie.
        var first = await harness.ConnectAsync(Ct);
        var firstCode = await harness.AuthorizeAsync(first.AuthorizationUrl, Ct);

        using (var unknown = await harness.CallbackRawAsync("canary-code-unknown", "canary-state-unknown", null, first.NonceCookie, Ct))
        {
            await AssertStateInvalidAsync(unknown);
        }

        // (2) the same code with no cookie: the claim spends the state, then the nonce check refuses it.
        using (var noCookie = await harness.CallbackAsync(firstCode, first.State, cookiePair: null, ct: Ct))
        {
            await AssertStateInvalidAsync(noCookie);
        }

        // (3) Google's refusal.
        var second = await harness.ConnectAsync(Ct);

        using (var denied = await harness.CallbackDeniedAsync(second.State, second.NonceCookie, Ct))
        {
            await AssertRedirectAsync(denied, "/channels?error=" + ErrorCodes.OauthDenied);
        }

        // (4) a failed exchange.
        var third = await harness.ConnectAsync(Ct);
        var thirdCode = await harness.AuthorizeAsync(third.AuthorizationUrl, Ct);
        harness.Google.Options.TokenError = "invalid_client";
        harness.Google.Options.TokenStatusCode = HttpStatusCode.Unauthorized;

        using (var failed = await harness.CallbackAsync(thirdCode, third.State, third.NonceCookie, Ct))
        {
            await AssertRedirectAsync(failed, "/channels?error=" + ErrorCodes.OauthExchangeFailed);
        }

        // (5) a refused connect (the unreadable secret, 503).
        resolver.NextResult = Error.Unexpected(
            "oauth_client_secret_unavailable",
            $"canary resolver detail carrying {googleOptions.ActiveClientSecret}");

        using (var refused = await harness.PostConnectAsync(Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }

        var text = sink.AllText();
        text.ShouldNotBeNullOrWhiteSpace("the sink must have seen the host's lines, or the search proves nothing");
        text.ShouldNotContain(firstCode);
        text.ShouldNotContain(first.State);
        text.ShouldNotContain(first.Nonce);
        text.ShouldNotContain(thirdCode);
        text.ShouldNotContain(third.State);
        text.ShouldNotContain(third.Nonce);
        text.ShouldNotContain(googleOptions.ActiveClientSecret);
        text.ShouldNotContain(googleOptions.SecondClientSecret);
        text.ShouldNotContain("canary-state-unknown");
        text.ShouldNotContain("canary-code-unknown");
        // The internal token "oauth_client_secret_unavailable" is a code and is logged as the request Outcome on
        // purpose; what must never appear is a secret/key spelling (a name=value pair or a JSON key).
        text.ShouldNotContain("client_secret=", Case.Insensitive);
        text.ShouldNotContain("\"client_secret\"", Case.Insensitive);
        text.ShouldNotContain("code_verifier=", Case.Insensitive);
        text.ShouldNotContain("\"code_verifier\"", Case.Insensitive);
    }

    [Fact]
    public async Task Connect_ASecondUsersConnect_LeavesTheFirstUsersStateLive()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var first = await harness.ConnectAsync(Ct);

        var other = await AuthTestFlow.CreateUserAsync(harness.Factory.Services);
        var otherFamily = await MfaTestFlow.SeedFamilyAsync(Database, other, harness.Clock.UtcNow);
        using var otherClient = await SignedInClientAsync(harness.Factory, other, otherFamily, Ct);

        using var response = await otherClient.PostAsync(ConnectChannelHarness.ConnectRoute, null, Ct);
        var second = await ConnectChannelHarness.ReadConnectAsync(response, Ct);

        second.State.ShouldNotBe(first.State);
        harness.StateRowsFor(first.State)
            .ShouldBe(1, "the purge deletes the caller's own finished rows only, never another user's live state");
        harness.StateValueFor("used_at", first.State).ShouldBeNullOrEmpty();
        SqlProbe.Count(Database, $"SELECT count(*) FROM oauth_states WHERE user_id = '{other.Id}'")
            .ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM oauth_states WHERE user_id = '{harness.User.Id}'")
            .ShouldBe(1);

        // The second user's own flow completes, and the first user's state is still live afterwards.
        var code = await harness.AuthorizeAsync(second.AuthorizationUrl, Ct);

        using var connected = await harness.CallbackAsync(code, second.State, second.NonceCookie, Ct);

        await AssertConnectedAsync(connected);
        harness.StateRowsFor(first.State).ShouldBe(1);
        harness.StateValueFor("used_at", first.State).ShouldBeNullOrEmpty();
    }

    [Fact]
    public async Task Connect_AfterAFinishedFlow_PurgesOnlyTheFinishedRow()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);

        var first = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(first.AuthorizationUrl, Ct);

        using (var connected = await harness.CallbackAsync(code, first.State, first.NonceCookie, Ct))
        {
            await AssertConnectedAsync(connected);
        }

        harness.StateRows().ShouldBe(1);
        harness.StateValueFor("used_at", first.State).ShouldNotBeNullOrEmpty("the finished row is spent");

        var second = await harness.ConnectAsync(Ct);

        harness.StateRows().ShouldBe(1, "the next connect purges the caller's own spent row");
        harness.StateRowsFor(first.State).ShouldBe(0, "a spent row is dead weight and is removed");
        harness.StateRowsFor(second.State).ShouldBe(1);
        harness.StateValueFor("used_at", second.State).ShouldBeNullOrEmpty("the fresh row is live");
    }

    [Fact]
    public async Task Callback_TenParallelNavigations_ExchangeExactlyOnce()
    {
        // The rate limiter is not the subject here: ten navigations in one minute would be refused by the
        // `auth` budget, so this host runs with it off (the limiter's own shape has its own probe).
        await using var world = await QaWorld.StartAsync(Database, Ct, ("RateLimiting:Enabled", "false"));

        using var connectResponse = await world.Client.PostAsync(ConnectChannelHarness.ConnectRoute, null, Ct);
        var connect = await ConnectChannelHarness.ReadConnectAsync(connectResponse, Ct);
        var code = await world.Google.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        var browsers = Enumerable.Range(0, 10).Select(_ => world.Factory.AnonymousBrowser()).ToList();
        try
        {
            var responses = await Task.WhenAll(
                browsers.Select(browser => SendCallbackAsync(browser, code, connect, Ct)));

            var locations = responses.Select(response => response.Headers.Location?.ToString()).ToList();
            foreach (var response in responses)
            {
                response.Dispose();
            }

            locations.Count(location => location == ConnectedRedirect)
                .ShouldBe(1, "exactly one of ten parallel claims wins");
            locations.Count(location => location == InvalidRedirect)
                .ShouldBe(9, "the losers see the same answer a replay sees");
        }
        finally
        {
            foreach (var browser in browsers)
            {
                browser.Dispose();
            }
        }

        world.Google.TokenRequestCount.ShouldBe(1, "the claim is one atomic UPDATE: exactly one exchange");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'publishing.channel_connected' "
                + $"AND subject_user_id = '{world.User.Id}'")
            .ShouldBe(1, "exactly one audit row");
    }

    [Fact]
    public async Task Callback_IsBehindTheAuthBudget_AndTheRejectionCarriesTheCatalogueShape()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);

        HttpResponseMessage? rejected = null;
        var before = 0;

        for (var attempt = 0; attempt < 30 && rejected is null; attempt++)
        {
            var response = await harness.CallbackRawAsync(
                $"canary-code-{attempt}",
                $"canary-state-{attempt}",
                error: null,
                cookiePair: null,
                ct: Ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response;
            }
            else
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Found);
                response.Dispose();
                before++;
            }
        }

        rejected.ShouldNotBeNull("the anonymous callback carries the `auth` policy, so a burst must be refused");
        before.ShouldBeGreaterThan(0, "the first requests of the burst are served before the budget runs out");

        var problem = await rejected!.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem.ShouldNotBeNull();
        problem!.Code.ShouldBe(ErrorCodes.RateLimited);
        problem.Status.ShouldBe(429);
        problem.Metadata.ShouldNotBeNull()["retryAfterSeconds"].GetInt32().ShouldBe(60);
        rejected.Dispose();
    }

    [Fact]
    public async Task Connect_WithPkceDisabled_OmitsBothPkceParameters()
    {
        await using var world = await QaWorld.StartAsync(Database, Ct, ("YouTube:Pkce", "false"));

        using var response = await world.Client.PostAsync(ConnectChannelHarness.ConnectRoute, null, Ct);
        var connect = await ConnectChannelHarness.ReadConnectAsync(response, Ct);

        var query = ConnectChannelHarness.Query(connect.AuthorizationUrl);
        query.ShouldNotContainKey("code_challenge");
        query.ShouldNotContainKey("code_challenge_method");
        query["state"].ShouldBe(connect.State);

        world.Factory.Services.GetRequiredService<IOptions<YouTubeOptions>>().Value.Pkce
            .ShouldBeFalse("the switch is configuration, not a code edit");

        // The fake's authorization endpoint requires the two PKCE parameters, so it refuses this URL with
        // 400: that is the independent evidence that the plain flow really omits them.
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var raw = new HttpClient(handler);
        using var authorization = await raw.GetAsync(connect.AuthorizationUrl, Ct);

        authorization.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await authorization.Content.ReadAsStringAsync(Ct)).ShouldContain("code_challenge");

        // Known deviation from the task file's step 6/8 note: the protected verifier is still stored (the
        // column is NOT NULL), so the switch omits the two URL parameters only.
        SqlProbe.Count(Database, $"SELECT count(*) FROM oauth_states WHERE user_id = '{world.User.Id}'")
            .ShouldBe(1);
        SqlProbe.ScalarText(
                Database,
                $"SELECT code_verifier_protected FROM oauth_states WHERE user_id = '{world.User.Id}'")
            .ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>Drives one callback navigation for <paramref name="code"/> on <paramref name="browser"/>.</summary>
    private static async Task<HttpResponseMessage> SendCallbackAsync(
        HttpClient browser,
        string code,
        ConnectChannelHarness.ConnectResult connect,
        CancellationToken ct)
    {
        using var request = RawCallback(
            $"code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(connect.State)}",
            connect.NonceCookie);

        return await browser.SendAsync(request, ct);
    }

    /// <summary>A callback request with a raw query, so duplicates and hostile spellings survive.</summary>
    private static HttpRequestMessage RawCallback(string rawQuery, string? cookiePair)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            ConnectChannelHarness.CallbackRoute + "?" + rawQuery);

        if (cookiePair is not null)
        {
            request.Headers.Add("Cookie", cookiePair);
        }

        return request;
    }

    /// <summary>A signed-in client of <paramref name="user"/> that passed the re-auth and legal gates.</summary>
    private static async Task<HttpClient> SignedInClientAsync(
        ApiFactory factory,
        AppUser user,
        Guid familyId,
        CancellationToken ct)
    {
        var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);
        client.DefaultRequestHeaders.Add(AuthTestFlow.OriginHeaderName, AuthTestFlow.Origin);

        using var reauth = await client.PostAsJsonAsync(
            MfaTestFlow.ReauthPath,
            new { password = MfaTestFlow.Password },
            ct);

        reauth.StatusCode.ShouldBe(HttpStatusCode.OK, "the connect route demands the real ≤ 5 min re-auth");

        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        using var accepted = await client.PostAsJsonAsync(
            "/api/v1/me/legal/accept",
            new
            {
                tos = configuration["LegalDocuments:Current:Tos"] ?? "1.0.0",
                privacy = configuration["LegalDocuments:Current:Privacy"] ?? "1.0.0",
            },
            ct);

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, "the legal gate must be satisfied from real rows");

        return client;
    }

    private static async Task AssertStateInvalidAsync(HttpResponseMessage response)
        => await AssertRedirectAsync(response, InvalidRedirect);

    /// <summary>The connect route's re-auth window is ≤ 5 min, so a test that advances the clock re-authenticates.</summary>
    private static async Task AssertReauthenticatedAsync(ConnectChannelHarness harness)
    {
        using var reauth = await harness.Client.PostAsJsonAsync(
            MfaTestFlow.ReauthPath,
            new { password = MfaTestFlow.Password },
            Ct);

        reauth.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "a clock advance past the ≤ 5 min re-auth window needs the real POST /auth/reauth before the next connect");
    }

    private static async Task AssertConnectedAsync(HttpResponseMessage response)
        => await AssertRedirectAsync(response, ConnectedRedirect);

    /// <summary>A <c>302</c> to the fixed target, with an empty body and the no-store/no-referrer pair.</summary>
    private static async Task AssertRedirectAsync(HttpResponseMessage response, string location)
    {
        response.StatusCode.ShouldBe(
            HttpStatusCode.Found,
            $"expected the {location} redirect, got {(int)response.StatusCode}");

        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldBe(location);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        response.Headers.GetValues("Referrer-Policy").ShouldContain("no-referrer");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty("a redirect carries no body and no oracle");
    }

    /// <summary>
    /// The smallest host the two probes the harness cannot express need: the same seams
    /// (<c>YouTube</c> options pointed at <see cref="FakeGoogleServer"/>, the channel hand-off and the e-mail
    /// sender replaced) plus the extra settings the probe passes in, and a user who passed every gate.
    /// </summary>
    private sealed class QaWorld(
        ApiFactory factory,
        FakeGoogleServer google,
        AppUser user,
        HttpClient client) : IAsyncDisposable
    {
        internal ApiFactory Factory => factory;

        internal FakeGoogleServer Google => google;

        internal AppUser User => user;

        internal HttpClient Client => client;

        internal static async Task<QaWorld> StartAsync(
            TestDatabase database,
            CancellationToken ct,
            params (string Key, string Value)[] settings)
        {
            var clock = new FakeClock();

            await database.ExecuteAsync("DELETE FROM oauth_states", ct);
            await database.ExecuteAsync($"DELETE FROM feature_flags WHERE key = '{ConnectChannelHarness.FeatureKey}'", ct);
            await database.ExecuteAsync(
                "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
                + $"VALUES ('{ConnectChannelHarness.FeatureKey}', true, '{{}}', now(), now())",
                ct);

            var google = await FakeGoogleServer.StartAsync(cancellationToken: ct);
            var factory = new ApiFactory(database, clock)
                .WithFakeGoogle(google)
                .WithFakeConnectChannel(new FakeConnectChannelState())
                .WithFakeEmailSender(new FakeEmailSenderState());

            foreach (var (key, value) in settings)
            {
                factory.WithSetting(key, value);
            }

            // Resolving a service builds the host, so every setting above is already in its configuration.
            var user = await AuthTestFlow.CreateUserAsync(factory.Services);
            var family = await MfaTestFlow.SeedFamilyAsync(database, user, clock.UtcNow);
            var client = await SignedInClientAsync(factory, user, family, ct);

            return new QaWorld(factory, google, user, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            await Google.DisposeAsync();
        }
    }
}
