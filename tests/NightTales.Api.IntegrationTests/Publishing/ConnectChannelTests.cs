using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.WebUtilities;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using NightTales.TestKit.Telemetry;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// 06.1.2-T1 over the real host and a real PostgreSQL: the connect route mints a single-use state and a
/// PKCE verifier behind the flag, the bearer, the ≤ 5 min re-authentication and the legal acceptance; the
/// anonymous callback claims that state atomically, exchanges the code with the client pinned to it, and
/// answers one of two fixed relative redirects.
/// </summary>
/// <remarks>
/// <para>
/// Every test drives <see cref="ConnectChannelHarness"/>: a <see cref="FakeGoogleServer"/> Kestrel, the
/// <c>YouTube</c> options pointed at it, the channel hand-off and the e-mail sender replaced, and a user
/// who passed every gate. No live Google call exists anywhere in this file, and every probe is scoped to
/// this test's own user (the class shares one database, so nothing reads another test's rows).
/// </para>
/// <para>
/// The task file's Tests table fixes the names below; the extra rows it does not carry are the post-claim
/// failure cases it lists under failure handling (a queue failure and a channel conflict, both writing their
/// rows before they fail), a writer that throws mid-unit, the positive control that shows those same rows
/// commit on the success path, a host whose writer registration is absent (R13/F2 — 06.1.2-T2 registers the
/// real one, so the port has to be removed to model that host), the reconnect whose audit row records
/// <c>reconnected = true</c> (06 CR-22), and the anonymous connect of acceptance check 2.
/// </para>
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
public sealed class ConnectChannelTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The two scopes the authorization URL must ask for, and exactly those two (06 README D2).</summary>
    private static readonly string[] ExpectedScopes = [FakeGoogleHosting.UploadScope, FakeGoogleHosting.ReadonlyScope];

    /// <summary>The two documents the legal gate names in <c>errors.documents</c> (09 README D6).</summary>
    private static readonly string[] ExpectedLegalDocuments = ["tos", "privacy"];

    /// <summary>The members of the success path's <c>publishing.channel_connected</c> audit payload, and only those.</summary>
    private static readonly string[] ExpectedConnectionAuditMembers =
        ["externalAccountId", "oauthClientId", "platform", "reconnected"];

    /// <summary>The two members of the connect response body; the nonce travels in the cookie only (YR10).</summary>
    private static readonly string[] ExpectedConnectResponseMembers = ["authorizationUrl", "expiresAt"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connect_FlagOff_Returns404FeatureDisabled()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct, flagOn: false);

        using var connect = await harness.PostConnectAsync(Ct);
        connect.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AuthTestFlow.ProblemCodeAsync(connect)).ShouldBe(ErrorCodes.FeatureDisabled);

        // The callback is behind the same flag, and it is the anonymous route: the guard answers before any
        // state row could be read.
        using var callback = await harness.CallbackRawAsync(
            "canary-code",
            "canary-state",
            error: null,
            cookiePair: null,
            ct: Ct);
        callback.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AuthTestFlow.ProblemCodeAsync(callback)).ShouldBe(ErrorCodes.FeatureDisabled);

        harness.StateRows().ShouldBe(0);
    }

    [Fact]
    public async Task Connect_WithoutBearer_Returns401()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);

        using var response = await harness.PostConnectAnonymousAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        harness.StateRows().ShouldBe(0);
    }

    [Fact]
    public async Task Connect_WithoutReauth_Returns401ReauthRequired()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct, reauthenticated: false);

        using var response = await harness.PostConnectAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.ReauthRequired);
        harness.StateRows().ShouldBe(0, "a refused gate writes no state");
    }

    [Fact]
    public async Task Connect_WithoutLegalAcceptance_Returns409WithDocuments()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct, legal: false);

        using var response = await harness.PostConnectAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ProblemAsync(response);
        problem.Code.ShouldBe(ErrorCodes.LegalAcceptanceRequired);
        problem.Errors.ShouldNotBeNull();
        problem.Errors!["documents"].ShouldBe(ExpectedLegalDocuments, ignoreOrder: true);
        harness.StateRows().ShouldBe(0);
    }

    [Fact]
    public async Task Connect_ReturnsAuthorizationUrlWithPkceAndOfflineAccess()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);

        var connect = await harness.ConnectAsync(Ct);

        // The URL points at the configured authorization endpoint: in production that is
        // accounts.google.com, here it is the fake's loopback address, which is the property a test can hold.
        var uri = new Uri(connect.AuthorizationUrl);
        uri.Authority.ShouldBe(harness.Google.AuthorizationEndpoint.Authority);
        uri.AbsolutePath.ShouldBe(harness.Google.AuthorizationEndpoint.AbsolutePath);

        var query = ConnectChannelHarness.Query(connect.AuthorizationUrl);
        query["client_id"].ShouldBe(harness.Google.Options.ActiveClientId);
        query["redirect_uri"].ShouldBe(FakeGoogleHosting.RedirectUri);
        query["response_type"].ShouldBe("code");
        query["access_type"].ShouldBe("offline");
        query["prompt"].ShouldBe("consent");
        query["include_granted_scopes"].ShouldBe("false");
        query["code_challenge_method"].ShouldBe("S256");
        query["code_challenge"]!.Length.ShouldBe(43);
        query["state"].ShouldBe(connect.State);
        connect.State.Length.ShouldBe(43);
        query["scope"]!.Split(' ').ShouldBe(ExpectedScopes, ignoreOrder: true);
    }

    [Fact]
    public async Task Connect_StoresStateHashAndProtectedVerifierOnly()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);

        var connect = await harness.ConnectAsync(Ct);

        harness.StateValue("state_hash").ShouldBe(AuthTestFlow.Hash(connect.State));

        var projected = harness.StateValue("code_verifier_protected");
        projected.ShouldNotBeNullOrWhiteSpace();
        harness.Protector().TryUnprotect("oauth.v1", projected, out var verifier).ShouldBeTrue();
        verifier.ShouldNotBeNull();
        harness.Protector().TryUnprotect("tokens.v1", projected, out _)
            .ShouldBeFalse("the verifier is protected under the oauth.v1 purpose only");

        var challenge = ConnectChannelHarness.Query(connect.AuthorizationUrl)["code_challenge"];
        WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier!))).ShouldBe(challenge);

        // Neither plaintext is anywhere in the row - and there is no column that could hold one.
        SqlProbe.Count(Database, $"SELECT count(*) FROM oauth_states WHERE state_hash = '{connect.State}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM oauth_states WHERE code_verifier_protected = '{verifier}'").ShouldBe(0);
        SqlProbe.Count(
                Database,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'oauth_states' "
                + "AND column_name IN ('state', 'code_verifier', 'browser_nonce')")
            .ShouldBe(0);
    }

    [Fact]
    public async Task Connect_SecondCall_KeepsTheFirstStateUsable()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);

        var first = await harness.ConnectAsync(Ct);
        var second = await harness.ConnectAsync(Ct);

        second.State.ShouldNotBe(first.State);

        // The purge removes finished and expired rows only, so the first, unused state keeps its own row: the
        // task file's "one row" after the second connect is that row (a second connect mints a second state,
        // which is why the first URL can still work).
        harness.StateRowsFor(first.State).ShouldBe(1);
        harness.StateValueFor("used_at", first.State).ShouldBeNullOrEmpty();
        harness.StateValueFor("used_at", second.State).ShouldBeNullOrEmpty();

        var code = await harness.AuthorizeAsync(first.AuthorizationUrl, Ct);
        using var response = await harness.CallbackAsync(code, first.State, first.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?connected=youtube");
        harness.Connection.CallCount.ShouldBe(1, "the first URL still worked after the second connect");
    }

    [Fact]
    public async Task Callback_ValidState_RedirectsAndConnectsChannel()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?connected=youtube");

        harness.Google.TokenRequestCount.ShouldBe(1);
        harness.Google.ChannelRequestCount.ShouldBe(1);

        var token = harness.Google.Calls.Single(call => call.Is("POST", FakeGoogleServer.TokenPath));
        token.FormValue("grant_type").ShouldBe("authorization_code");
        token.FormValue("code").ShouldBe(code);
        token.FormValue("client_id").ShouldBe(harness.Google.Options.ActiveClientId);
        token.FormValue("redirect_uri").ShouldBe(FakeGoogleHosting.RedirectUri);
        token.FormValue("code_verifier").ShouldNotBeNullOrWhiteSpace();

        var channels = harness.Google.Calls.Single(call => call.Is("GET", FakeGoogleServer.ChannelsPath));
        channels.QueryValue("part").ShouldBe("snippet");
        string.Equals(channels.QueryValue("mine"), "true", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();

        // One connection row: channel_connections arrives with 06.1.2-T2, so the recorded hand-off is the row.
        harness.Connection.CallCount.ShouldBe(1);
        var handed = harness.Connection.Calls[0];
        handed.UserId.ShouldBe(harness.User.Id);
        handed.Platform.ToWire().ShouldBe("youtube");
        handed.Channel.Id.ShouldBe(harness.Google.Options.Channel.Id);
        handed.Channel.Title.ShouldBe(harness.Google.Options.Channel.Title);
        handed.OAuthClientId.ShouldBe(harness.Google.Options.ActiveClientId);
        handed.Now.ShouldBe(harness.Clock.UtcNow);
        harness.Google.IssuedAccessTokens.ShouldContain(handed.Tokens.AccessToken);
        handed.Tokens.RefreshToken.ShouldNotBeNullOrEmpty();

        harness.StateValueFor("used_at", connect.State).ShouldNotBeNullOrEmpty();

        harness.AuditRows().ShouldBe(1, "the success path writes exactly one publishing.channel_connected row");
        using var data = JsonDocument.Parse(harness.AuditData());
        data.RootElement.GetProperty("platform").GetString().ShouldBe("youtube");
        data.RootElement.GetProperty("oauthClientId").GetString().ShouldBe(harness.Google.Options.ActiveClientId);
        data.RootElement.GetProperty("externalAccountId").GetString().ShouldBe(harness.Google.Options.Channel.Id);
        data.RootElement.GetProperty("reconnected").GetBoolean().ShouldBeFalse(
            "this connect inserted a row, and the audit records the writer's own answer — a first connect is "
            + "never a reconnect (06 CR-22)");
        data.RootElement.EnumerateObject().Select(property => property.Name).Order().ShouldBe(
            ExpectedConnectionAuditMembers);

        // No outbox assertion on the success path: D13's channel_connection_changed.v1 row is raised by the
        // ChannelConnection aggregate's [OutboxEvent] through the interceptor during T2's hand-off, and no
        // such event type exists on this branch (src/NightTales.Domain has none). It is a 06.1.2-T2 check,
        // like the connection row itself, which this suite substitutes with harness.Connection.CallCount.
        // The failed paths keep asserting zero, so a writer that leaves a row behind after a failed attempt
        // still fails this suite. The connect e-mail is T2's step as well (the stand-in only queues it when a
        // test switches it on), so it is asserted on the failure paths, not here.
    }

    [Fact]
    public async Task Callback_Reconnect_RedirectsAndTheAuditRecordsReconnectedTrue()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);

        // 06 CR-22: `data.reconnected` is the writer's own answer, not a value the callback assumes — the
        // stand-in answers what 06.1.2-T2's writer answers when this account already has a live row for the
        // channel (a second callback for the same channel reconnects it), and the audit row must carry that
        // answer. A callback that hard-coded `false` (the shape before the port change) reddens here. The
        // writer's own insert-versus-reconnect decision is asserted in ConnectChannelConnectionHandlerTests.
        harness.Connection.Reconnected = true;

        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?connected=youtube");
        harness.Connection.CallCount.ShouldBe(1);
        harness.AuditRows().ShouldBe(1);

        using var data = JsonDocument.Parse(harness.AuditData());
        data.RootElement.GetProperty("reconnected").GetBoolean().ShouldBeTrue(
            "a reconnect is the writer's answer, and the audit row records it instead of a hard-coded false");
        data.RootElement.GetProperty("externalAccountId").GetString().ShouldBe(harness.Google.Options.Channel.Id);
    }

    [Fact]
    public async Task Callback_ReplayedState_RedirectsOauthStateInvalid()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        using var first = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);
        await AssertRedirectAsync(first, "/channels?connected=youtube");

        using var replay = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(replay, "/channels?error=oauth_state_invalid");
        harness.Google.TokenRequestCount.ShouldBe(1, "a spent state never exchanges a second time");
        harness.Connection.CallCount.ShouldBe(1);
        harness.AuditRows().ShouldBe(1);
    }

    [Fact]
    public async Task Callback_ExpiredState_RedirectsOauthStateInvalid()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        harness.Clock.Advance(TimeSpan.FromMinutes(11));

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?error=oauth_state_invalid");
        harness.Google.TokenRequestCount.ShouldBe(0);
        harness.StateValueFor("used_at", connect.State).ShouldBeNullOrEmpty("the claim's expires_at predicate refused it");
    }

    [Fact]
    public async Task Callback_RevokedFamily_RedirectsOauthStateInvalid()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        await harness.RevokeFamilyAsync(Ct);

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?error=oauth_state_invalid");
        harness.Google.TokenRequestCount.ShouldBe(0);
        harness.Connection.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Callback_UserDenied_RedirectsOauthDenied()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);

        using var response = await harness.CallbackDeniedAsync(connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?error=oauth_denied");
        harness.Google.TokenRequestCount.ShouldBe(0, "a refusal must not be exchanged");
        harness.StateValueFor("used_at", connect.State).ShouldNotBeNullOrEmpty("the claimed state stays spent");
    }

    [Fact]
    public async Task Callback_InvalidClient_RedirectsOauthExchangeFailed()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        harness.Google.Options.TokenError = "invalid_client";
        harness.Google.Options.TokenStatusCode = HttpStatusCode.Unauthorized;

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?error=oauth_exchange_failed");
        harness.Google.TokenRequestCount.ShouldBe(1, "no retry of a single-use code");
        AssertSpentWithoutRows(harness, connect.State);
    }

    [Fact]
    public async Task Callback_ConcurrentSameState_ExchangesOnce()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        using var left = harness.NewBrowser();
        using var right = harness.NewBrowser();

        var responses = await Task.WhenAll(
            harness.CallbackAsync(left, code, connect.State, connect.NonceCookie, Ct),
            harness.CallbackAsync(right, code, connect.State, connect.NonceCookie, Ct));

        var locations = responses.Select(response => response.Headers.Location?.ToString()).ToList();
        foreach (var response in responses)
        {
            response.Dispose();
        }

        locations.Count(location => location == "/channels?connected=youtube").ShouldBe(1);
        locations.Count(location => location == "/channels?error=oauth_state_invalid").ShouldBe(1);
        harness.Google.TokenRequestCount.ShouldBe(1, "the claim is one atomic UPDATE: exactly one exchange");
        harness.Connection.CallCount.ShouldBe(1, "exactly one connection row");
        harness.AuditRows().ShouldBe(1);
    }

    [Fact]
    public async Task Callback_UsesClientPinnedToState()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);

        harness.StateValue("oauth_client_id").ShouldBe(
            harness.Google.Options.ActiveClientId,
            "a fresh state pins the running deployment's active client");

        // The deployment that minted the state had the secondary client active, and the operator has since
        // moved to the primary one: the row is the authority for the exchange, never YouTube:ActiveClientId.
        // The authorization round-trip is driven with the same client id, so the fake's own client check
        // (id and secret pair) stays a real one and passing it proves the pinned client was used.
        await harness.Database.ExecuteAsync(
            "UPDATE oauth_states SET oauth_client_id = "
            + $"'{harness.Google.Options.SecondClientId}' WHERE user_id = '{harness.User.Id}'",
            Ct);

        var authorizationUrl = connect.AuthorizationUrl.Replace(
            $"client_id={harness.Google.Options.ActiveClientId}",
            $"client_id={harness.Google.Options.SecondClientId}",
            StringComparison.Ordinal);
        authorizationUrl.ShouldNotBe(connect.AuthorizationUrl);

        var code = await harness.AuthorizeAsync(authorizationUrl, Ct);

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?connected=youtube");

        var token = harness.Google.Calls.Single(call => call.Is("POST", FakeGoogleServer.TokenPath));
        token.FormValue("client_id").ShouldBe(harness.Google.Options.SecondClientId);
        token.FormValue("client_id").ShouldNotBe(
            harness.Google.Options.ActiveClientId,
            "the exchange reads the client pinned to the state row, not the current active id");
        token.FormValue("client_secret").ShouldBe(harness.Google.Options.SecondClientSecret);
        harness.Connection.Calls[0].OAuthClientId.ShouldBe(harness.Google.Options.SecondClientId);
    }

    [Fact]
    public async Task Callback_NoTokensOrCodeInLogs()
    {
        var sink = new TestLogSink();
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct, logSink: sink);

        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);
        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?connected=youtube");

        harness.Protector()
            .TryUnprotect("oauth.v1", harness.StateValue("code_verifier_protected"), out var verifier)
            .ShouldBeTrue();

        var text = sink.AllText();
        text.ShouldNotBeNullOrWhiteSpace("the sink must have seen the host's lines, or the search proves nothing");
        text.ShouldNotContain(code);
        text.ShouldNotContain(connect.State);
        text.ShouldNotContain(connect.Nonce);
        text.ShouldNotContain(verifier!);
        text.ShouldNotContain(harness.Google.Options.ActiveClientSecret);
        text.ShouldNotContain(harness.Google.Options.SecondClientSecret);
        text.ShouldNotContain("client_secret", Case.Insensitive);
        text.ShouldNotContain("code_verifier", Case.Insensitive);

        var handed = harness.Connection.Calls[0];
        text.ShouldNotContain(handed.Tokens.AccessToken);
        handed.Tokens.RefreshToken.ShouldNotBeNullOrEmpty();
        text.ShouldNotContain(handed.Tokens.RefreshToken!);
    }

    [Fact]
    public async Task Connect_SetsHostNonceCookieWithRequiredFlags()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);

        using var response = await harness.PostConnectAsync(Ct);
        var connect = await ConnectChannelHarness.ReadConnectAsync(response, Ct);

        var cookie = ConnectChannelHarness.SetCookieHeader(response, ConnectChannelHarness.NonceCookie);
        cookie.ShouldNotBeNull("the 200 must set the nonce cookie");
        cookie!.ShouldStartWith(ConnectChannelHarness.NonceCookie + "=", Case.Sensitive);
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("samesite=lax", Case.Insensitive);
        cookie.ShouldContain("path=/", Case.Insensitive);
        cookie.ShouldContain("max-age=600", Case.Insensitive);

        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(connect.Nonce, Case.Sensitive);
        body.ShouldNotContain("nonce", Case.Insensitive);

        using var json = JsonDocument.Parse(body);
        json.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(
            ExpectedConnectResponseMembers);
        json.RootElement.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(
            harness.Clock.UtcNow + TimeSpan.FromMinutes(10),
            "the state's ten-minute TTL is what the body reports");
    }

    [Fact]
    public async Task Connect_StoresNonceHashNotTheNonce()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);

        var connect = await harness.ConnectAsync(Ct);

        harness.StateValue("browser_nonce_hash").ShouldBe(AuthTestFlow.Hash(connect.Nonce));
        SqlProbe.Count(Database, $"SELECT count(*) FROM oauth_states WHERE browser_nonce_hash = '{connect.Nonce}'")
            .ShouldBe(0, "the plaintext nonce is nowhere in the database");
    }

    [Fact]
    public async Task Callback_WithoutNonceCookie_RedirectsOauthStateInvalid()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        using var response = await harness.CallbackAsync(code, connect.State, cookiePair: null, ct: Ct);

        await AssertRedirectAsync(response, "/channels?error=oauth_state_invalid");
        harness.Google.TokenRequestCount.ShouldBe(0, "no exchange without the browser nonce");
        harness.Connection.CallCount.ShouldBe(0);
        harness.StateValueFor("used_at", connect.State)
            .ShouldNotBeNullOrEmpty("the claim ran first, so the state stays spent");
    }

    [Fact]
    public async Task Callback_WithForeignBrowserNonce_RedirectsOauthStateInvalid()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var first = await harness.ConnectAsync(Ct);
        var second = await harness.ConnectAsync(Ct);
        first.Nonce.ShouldNotBe(second.Nonce);

        var code = await harness.AuthorizeAsync(first.AuthorizationUrl, Ct);

        // The nonce of the second connect, the state of the first: indistinguishable from any other failure.
        using var response = await harness.CallbackAsync(code, first.State, second.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?error=oauth_state_invalid");
        harness.Google.TokenRequestCount.ShouldBe(0);
        harness.Connection.CallCount.ShouldBe(0);
        harness.AuditRows().ShouldBe(0);
    }

    [Theory]
    [InlineData(RedirectOutcome.Connected)]
    [InlineData(RedirectOutcome.OauthStateInvalid)]
    [InlineData(RedirectOutcome.OauthDenied)]
    [InlineData(RedirectOutcome.OauthExchangeFailed)]
    [InlineData(RedirectOutcome.ChannelAlreadyConnected)]
    public async Task Callback_ClearsNonceCookieOnSuccessAndOnFailure(RedirectOutcome outcome)
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);

        using var response = await CallbackForAsync(harness, connect, outcome);

        var cookie = ConnectChannelHarness.SetCookieHeader(response, ConnectChannelHarness.NonceCookie);
        cookie.ShouldNotBeNull("every outcome clears the nonce cookie");
        cookie!.ShouldContain("max-age=0", Case.Insensitive);
        cookie.ShouldContain("path=/", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=lax", Case.Insensitive);
    }

    [Fact]
    public async Task Callback_EmailQueueFailureAfterTheWriterWrote_RedirectsOauthExchangeFailedAndRollsBack()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        // 06.1.2-T2's hand-off writes the connection and queues the connect e-mail inside the caller's unit.
        // Both stand-ins therefore leave their rows behind before the failure: the marker row, the e-mail row
        // and its outbox event exist when the queue answers its error, so the zero counts below prove unit of
        // work #2 was rolled back rather than that nothing was attempted (R2).
        harness.Connection.QueueConnectEmail = true;
        harness.Connection.WriteBeforeAnswering = true;
        harness.Email.Fail = true;
        harness.Email.WriteBeforeFailing = true;

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?error=oauth_exchange_failed");
        harness.Email.CallCount.ShouldBe(1, "the queue was attempted, so the zero counts below are evidence");
        harness.Email.Calls[0].UserId.ShouldBe(harness.User.Id);
        harness.Connection.CallCount.ShouldBe(1);
        AssertSpentWithoutRows(harness, connect.State);
    }

    [Fact]
    public async Task Callback_ConnectedWithWriterRows_KeepsTheMarkerAndTheEmailRow()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        // The positive control of the rollback cases (N3): the same writer mode that leaves rows behind before
        // a failure must leave them committed when the flow succeeds, otherwise their zero assertions would
        // pass whether or not the stand-in ever wrote anything.
        harness.Connection.WriteBeforeAnswering = true;

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?connected=youtube");
        harness.MarkerRows().ShouldBe(
            1,
            "the writer's marker row — the connection stand-in until channel_connections exists — commits with the flow");
        harness.EmailRows().ShouldBe(1, "and so does the connect e-mail row it queues");
        harness.OutboxRows().ShouldBe(1, "the e-mail row's email_requested.v1 event commits with it");
        harness.AuditRows().ShouldBe(1);
        harness.StateValueFor("used_at", connect.State).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Callback_WriterRefusedAfterWritingItsRows_RedirectsChannelAlreadyConnectedAndRollsBack()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        // The writer writes what a real one writes and then refuses: the refusal is an ErrorOr code, so the
        // handler answers it as an error and unit of work #2 rolls its own rows back (R1/R2/F1).
        harness.Connection.Outcome = FakeConnectOutcome.Conflict;
        harness.Connection.WriteBeforeAnswering = true;

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?error=channel_already_connected");
        harness.Connection.CallCount.ShouldBe(1);
        harness.Email.CallCount.ShouldBe(0, "the conflict is answered before any e-mail is queued");
        AssertSpentWithoutRows(harness, connect.State);
    }

    [Fact]
    public async Task Callback_WriterThrowsMidUnit_RedirectsOauthExchangeFailedAndClearsTheCookie()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        // A writer that throws after writing its rows: the pipeline rolls unit of work #2 back, the endpoint
        // still answers the fixed redirect (a spent state must never reach the browser as a 500), and only the
        // exception's type could have been logged.
        harness.Connection.WriteBeforeAnswering = true;
        harness.Connection.Throw = true;

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?error=oauth_exchange_failed");
        harness.Connection.CallCount.ShouldBe(1);
        var cleared = ConnectChannelHarness.SetCookieHeader(response, ConnectChannelHarness.NonceCookie);
        cleared.ShouldNotBeNull("every outcome clears the nonce cookie, a thrown one included").ShouldContain(
            "max-age=0",
            Case.Insensitive);
        AssertSpentWithoutRows(harness, connect.State);
    }

    [Fact]
    public async Task Callback_NoWriterRegistered_RedirectsOauthExchangeFailedBeforeTheExchange()
    {
        // A host whose port is absent — 06.1.2-T2 registers the production writer, so this has to remove the
        // registration rather than rely on its absence: the handler resolves its writer before it spends the
        // code, so such a host answers the fixed redirect and never calls Google (R13/F2). The state is
        // already spent by then: the claim is unit of work #1 and cannot be undone.
        await using var harness = await ConnectChannelHarness.StartAsync(
            Database,
            Ct,
            channelWriter: ConnectChannelHarness.ConnectChannelWriter.None);
        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?error=oauth_exchange_failed");
        harness.Google.TokenRequestCount.ShouldBe(
            0,
            "a missing writer must be discovered before the code is spent at Google (F2)");
        harness.Google.ChannelRequestCount.ShouldBe(0);
        harness.Connection.CallCount.ShouldBe(0);
        AssertSpentWithoutRows(harness, connect.State);
    }

    [Fact]
    public async Task Connect_UnavailableClientSecret_Returns503ServiceUnavailable()
    {
        var resolver = new FakeOAuthClientResolver
        {
            NextResult = Error.Unexpected("oauth_client_secret_unavailable", "canary resolver detail"),
        };
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct, resolver: resolver);

        using var response = await harness.PostConnectAsync(Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ProblemAsync(response)).Code.ShouldBe(ErrorCodes.ServiceUnavailable);

        // The resolver's own catalogue codes are internal: only the Api's normalised code reaches the client.
        body.ShouldNotContain("oauth_client_secret_unavailable", Case.Sensitive);
        body.ShouldNotContain("oauth_client_unknown", Case.Sensitive);
        harness.StateRows().ShouldBe(0);
        resolver.CallCount.ShouldBe(1, "the connect asks the resolver exactly once, and only after every gate");
        resolver.Calls.ShouldContain(harness.Google.Options.ActiveClientId);
    }

    [Fact]
    public async Task Connect_UnknownOAuthClient_Returns404NotFound()
    {
        var resolver = new FakeOAuthClientResolver
        {
            NextResult = Error.NotFound("oauth_client_unknown", "canary resolver detail"),
        };
        await using var harness = await ConnectChannelHarness.StartAsync(Database, Ct, resolver: resolver);

        using var response = await harness.PostConnectAsync(Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ProblemAsync(response)).Code.ShouldBe(ErrorCodes.NotFound);
        body.ShouldNotContain("oauth_client_unknown", Case.Sensitive);
        harness.StateRows().ShouldBe(0);
        resolver.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Callback_EnvelopeStateInvalid_RedirectsOauthStateInvalid()
    {
        // An ErrorOr pipeline error carrying the claim's own code: the endpoint maps it to the state-invalid
        // constant, and every other code to oauth_exchange_failed (the next test). The real handler would have
        // connected this channel if it had run, so a failure redirect is also proof that the replacement, not
        // the real handler, answered. If this test ever reddens with `oauth_exchange_failed`, check
        // [AllowAnonymousRequest] on the command first: a pipeline authorization denial answers `unauthorized`,
        // which the split maps to the exchange-failed constant.
        await using var harness = await ConnectChannelHarness.StartAsync(
            Database,
            Ct,
            envelope: Error.NotFound(ErrorCodes.OauthStateInvalid, "canary claim refusal"));

        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?error=oauth_state_invalid");
        harness.Google.TokenRequestCount.ShouldBe(0, "the replaced handler never reached Google");
        harness.Connection.CallCount.ShouldBe(0);
        harness.AuditRows().ShouldBe(0);
        harness.StateValueFor("used_at", connect.State).ShouldBeNullOrEmpty("the real claim never ran");

        var cleared = ConnectChannelHarness.SetCookieHeader(response, ConnectChannelHarness.NonceCookie);
        cleared.ShouldNotBeNull().ShouldContain("max-age=0", Case.Insensitive);
    }

    [Fact]
    public async Task Callback_EnvelopeConflict_RedirectsOauthExchangeFailed()
    {
        // R6: `concurrency_conflict` is the code a commit mapping of unit of work #2 answers, where the state
        // was valid and is already spent, so it belongs to the generic exchange failure and not to the
        // state-invalid bucket (which would tell the user a used link was invalid).
        await using var harness = await ConnectChannelHarness.StartAsync(
            Database,
            Ct,
            envelope: Error.Conflict(ErrorCodes.ConcurrencyConflict, "canary commit conflict"));

        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?error=oauth_exchange_failed");
        harness.Google.TokenRequestCount.ShouldBe(0, "the replaced handler never reached Google");
        harness.AuditRows().ShouldBe(0);
        harness.StateValueFor("used_at", connect.State).ShouldBeNullOrEmpty("the real claim never ran");
    }

    [Fact]
    public async Task Callback_EnvelopeFailure_RedirectsOauthExchangeFailed()
    {
        await using var harness = await ConnectChannelHarness.StartAsync(
            Database,
            Ct,
            envelope: Error.Failure(ErrorCodes.ServiceUnavailable, "canary envelope failure"));

        var connect = await harness.ConnectAsync(Ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        using var response = await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);

        await AssertRedirectAsync(response, "/channels?error=oauth_exchange_failed");
        harness.Google.TokenRequestCount.ShouldBe(0);
        harness.StateValueFor("used_at", connect.State).ShouldBeNullOrEmpty("the real claim never ran");
    }

    /// <summary>A callback in the answer <paramref name="outcome"/> names, for the cookie-clearing theory.</summary>
    private static async Task<HttpResponseMessage> CallbackForAsync(
        ConnectChannelHarness harness,
        ConnectChannelHarness.ConnectResult connect,
        RedirectOutcome outcome)
    {
        switch (outcome)
        {
            case RedirectOutcome.OauthStateInvalid:
                return await harness.CallbackAsync("canary-code", "canary-state-not-minted", connect.NonceCookie, Ct);
            case RedirectOutcome.OauthDenied:
                return await harness.CallbackDeniedAsync(connect.State, connect.NonceCookie, Ct);
            case RedirectOutcome.OauthExchangeFailed:
                harness.Google.Options.TokenError = "invalid_grant";
                break;
            case RedirectOutcome.ChannelAlreadyConnected:
                harness.Connection.Outcome = FakeConnectOutcome.Conflict;
                break;
            default:
                break;
        }

        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, Ct);

        return await harness.CallbackAsync(code, connect.State, connect.NonceCookie, Ct);
    }

    /// <summary>
    /// The assertions every post-claim failure owes: the state stays spent, and the connection marker, the
    /// audit, the outbox and the e-mail rows the failed attempt would have written are gone. The marker probe
    /// is only meaningful in the cases that switch <c>WriteBeforeAnswering</c> on — there the writer really
    /// added it before failing, so this is evidence about unit of work #2's rollback and not about a row
    /// nothing ever wrote (R2).
    /// </summary>
    private static void AssertSpentWithoutRows(ConnectChannelHarness harness, string state)
    {
        harness.StateValueFor("used_at", state)
            .ShouldNotBeNullOrEmpty("a claimed state stays spent: the code is single-use and never retried");
        harness.AuditRows().ShouldBe(0);
        harness.OutboxRows().ShouldBe(0);
        harness.EmailRows().ShouldBe(0);
        harness.MarkerRows().ShouldBe(0);
    }

    /// <summary>A 302 with the fixed relative target, the no-store/no-referrer pair and an empty body.</summary>
    private static async Task AssertRedirectAsync(HttpResponseMessage response, string location)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldBe(location);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        response.Headers.GetValues("Referrer-Policy").ShouldContain("no-referrer");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty("a redirect carries no body and no oracle");
    }

    private static async Task<ProblemResponse> ProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);

        return problem.ShouldNotBeNull();
    }

    /// <summary>One of the five answers a callback can end in, for the cookie-clearing theory.</summary>
    public enum RedirectOutcome
    {
        /// <summary>The connection was written.</summary>
        Connected,

        /// <summary>No claimable row, an expired state, a revoked family, a missing or foreign nonce.</summary>
        OauthStateInvalid,

        /// <summary>Google answered <c>error=access_denied</c>.</summary>
        OauthDenied,

        /// <summary>The token exchange failed.</summary>
        OauthExchangeFailed,

        /// <summary>The platform account is already connected elsewhere.</summary>
        ChannelAlreadyConnected,
    }
}
