using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;
using ErrorOr;
using NightTales.Application.Features.Publishing.Channels.Commands.CompleteYouTubeConnect;
using NightTales.Infrastructure.Security;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Email;
using NightTales.TestKit.Google;
using NightTales.TestKit.Publishing;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Time;
using Shouldly;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The world one 06.1.2-T1 test needs: a <see cref="FakeGoogleServer"/>, a host pointed at it (the
/// <c>YouTube</c> options, the channel hand-off and the e-mail sender are the test seams), and a user who
/// passed every gate the connect route demands (a live refresh family, a re-authentication through
/// <c>POST /api/v1/auth/reauth</c>, the current legal acceptance and the <c>youtube_publish</c> flag).
/// </summary>
/// <remarks>
/// The flag row is written <em>before</em> the host is built, because the feature guard caches the flags
/// per host (09.9.2-T2, <c>RequireFeatureFilterTests</c>). Every probe is scoped to this test's own user,
/// so the class can share one database without reading another test's rows.
/// </remarks>
internal sealed class ConnectChannelHarness : IAsyncDisposable
{
    /// <summary>The connect route of the 06.1.2-T1 table (platform as the route parameter).</summary>
    internal const string ConnectRoute = "/api/v1/channels/youtube/connect";

    /// <summary>The anonymous callback route.</summary>
    internal const string CallbackRoute = "/api/v1/channels/youtube/callback";

    /// <summary>The flag both routes are behind.</summary>
    internal const string FeatureKey = "youtube_publish";

    /// <summary>The nonce cookie's name (YR10; <c>__Host-</c> requires Secure and Path=/).</summary>
    internal const string NonceCookie = "__Host-nts_oauth";

    /// <summary>The one audit action the success path writes.</summary>
    internal const string AuditAction = "publishing.channel_connected";

    /// <summary>The outbox event typing one connection change.</summary>
    internal const string OutboxEventType = "channel_connection_changed.v1";

    private const string LegalAcceptRoute = "/api/v1/me/legal/accept";

    private readonly FakeGoogleServer _google;
    private readonly ApiFactory _factory;

    private ConnectChannelHarness(
        TestDatabase database,
        FakeClock clock,
        FakeGoogleServer google,
        ApiFactory factory,
        AppUser user,
        Guid familyId,
        FakeConnectChannelState connection,
        FakeEmailSenderState email,
        HttpClient client,
        HttpClient anonymous)
    {
        Database = database;
        Clock = clock;
        _google = google;
        _factory = factory;
        User = user;
        FamilyId = familyId;
        Connection = connection;
        Email = email;
        Client = client;
        Anonymous = anonymous;
    }

    /// <summary>The class database, for the row assertions of the task file's acceptance checks.</summary>
    internal TestDatabase Database { get; }

    /// <summary>The host's clock, so a test can expire a state without sleeping.</summary>
    internal FakeClock Clock { get; }

    /// <summary>The fake Google endpoints the host is pointed at.</summary>
    internal FakeGoogleServer Google => _google;

    /// <summary>The test host, for a probe that needs a port out of its scope (a protector, the family service).</summary>
    internal ApiFactory Factory => _factory;

    /// <summary>The user every request runs as.</summary>
    internal AppUser User { get; }

    /// <summary>The user's refresh family: the token's <c>sid</c> and the callback's liveness check.</summary>
    internal Guid FamilyId { get; }

    /// <summary>The channel hand-off stand-in; <c>CallCount</c> is "one connection row" until T2 lands.</summary>
    internal FakeConnectChannelState Connection { get; }

    /// <summary>The e-mail sender stand-in, so a rollback can be asserted on a real row.</summary>
    internal FakeEmailSenderState Email { get; }

    /// <summary>An authenticated client of <see cref="User"/>'s family, presenting the accepted origin.</summary>
    internal HttpClient Client { get; }

    /// <summary>
    /// The browser of the callback navigation: anonymous, and it does not follow the <c>302</c> the callback
    /// answers (see <see cref="Browser"/>) — the redirect, its headers and the cleared nonce cookie are the
    /// assertion.
    /// </summary>
    internal HttpClient Anonymous { get; }

    /// <summary>
    /// Builds the world: flag row, the fake server, the host with its three seams, a real user, a live
    /// refresh family, the re-authentication and the legal acceptance.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="ct">Cancels the setup.</param>
    /// <param name="googleOptions">The fake's switches; a default instance when null.</param>
    /// <param name="flagOn">Whether <c>youtube_publish</c> is on; false is the 404 case.</param>
    /// <param name="reauthenticated">Whether the user re-authenticates through the real route first.</param>
    /// <param name="legal">Whether the current legal documents are accepted first.</param>
    /// <param name="logSink">A sink the host logs through, for the redaction check.</param>
    /// <param name="resolver">A resolver stand-in, for the two catalogue codes the connect route maps.</param>
    /// <param name="envelope">A callback-handler stand-in answering this, for the endpoint's envelope split.</param>
    /// <param name="connectChannelWriter">
    /// Whether the host gets the writer stand-in. False leaves <see cref="IConnectChannelConnection"/>
    /// unregistered, which is the production default until 06.1.2-T2 lands (D-2) and the case R13 covers.
    /// </param>
    internal static async Task<ConnectChannelHarness> StartAsync(
        TestDatabase database,
        CancellationToken ct,
        FakeGoogleOptions? googleOptions = null,
        bool flagOn = true,
        bool reauthenticated = true,
        bool legal = true,
        TestLogSink? logSink = null,
        FakeOAuthClientResolver? resolver = null,
        ErrorOr<ConnectYouTubeOutcome>? envelope = null,
        bool connectChannelWriter = true)
    {
        var clock = new FakeClock();

        // The class shares one database: this test's user owns no state row yet, and the flag row must be
        // right before the host (and its cached flags) exists.
        await database.ExecuteAsync("DELETE FROM oauth_states", ct);
        await database.ExecuteAsync($"DELETE FROM feature_flags WHERE key = '{FeatureKey}'", ct);
        if (flagOn)
        {
            await database.ExecuteAsync(
                "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
                + $"VALUES ('{FeatureKey}', true, '{{}}', now(), now())",
                ct);
        }

        var google = await FakeGoogleServer.StartAsync(googleOptions, ct);
        var connection = new FakeConnectChannelState();
        var email = new FakeEmailSenderState();

        var factory = new ApiFactory(database, clock)
            .WithFakeGoogle(google)
            .WithFakeEmailSender(email);

        if (connectChannelWriter)
        {
            factory = factory.WithFakeConnectChannel(connection);
        }

        if (logSink is not null)
        {
            factory.WithServices(services => services.AddSingleton<Serilog.Core.ILogEventSink>(logSink));
        }

        if (resolver is not null)
        {
            factory.WithFakeOAuthClientResolver(resolver);
        }

        if (envelope is not null)
        {
            factory.WithFakeCompleteYouTubeConnectHandler(envelope.Value);
        }

        // Resolving a service builds the host, so every setting above is already in its configuration.
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var familyId = await MfaTestFlow.SeedFamilyAsync(database, user, clock.UtcNow);

        var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);
        client.DefaultRequestHeaders.Add(AuthTestFlow.OriginHeaderName, AuthTestFlow.Origin);

        if (reauthenticated)
        {
            using var reauth = await client.PostAsJsonAsync(
                MfaTestFlow.ReauthPath,
                new { password = MfaTestFlow.Password },
                ct);

            reauth.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                "the ≤ 5 min re-authentication the connect route demands is the real route, not a seeded column");
        }

        if (legal)
        {
            var configuration = factory.Services.GetRequiredService<IConfiguration>();
            using var accepted = await client.PostAsJsonAsync(
                LegalAcceptRoute,
                new
                {
                    tos = configuration["LegalDocuments:Current:Tos"] ?? "1.0.0",
                    privacy = configuration["LegalDocuments:Current:Privacy"] ?? "1.0.0",
                },
                ct);

            accepted.StatusCode.ShouldBe(HttpStatusCode.OK, "the legal gate must be satisfied from real rows");
        }

        return new ConnectChannelHarness(
            database,
            clock,
            google,
            factory,
            user,
            familyId,
            connection,
            email,
            client,
            Browser(factory));
    }

    /// <summary>
    /// A browser-like anonymous client: it does <em>not</em> follow a redirect. The callback answers <c>302</c>
    /// to a fixed SPA path in every case, and the redirect, its cache/referrer headers and the nonce cookie it
    /// clears are what the callback tests assert; an auto-following client would replace them with the answer
    /// to the SPA path (an unmatched route in this host) instead.
    /// </summary>
    /// <param name="factory">The test host.</param>
    internal static HttpClient Browser(ApiFactory factory) => factory.AnonymousBrowser();

    /// <summary>Another browser-like client of this harness's host (the concurrency case needs two).</summary>
    internal HttpClient NewBrowser() => Browser(_factory);

    /// <summary>POSTs the connect route as the authenticated user, without asserting the answer.</summary>
    internal Task<HttpResponseMessage> PostConnectAsync(CancellationToken ct) =>
        Client.PostAsync(ConnectRoute, content: null, ct);

    /// <summary>POSTs the connect route without any bearer token (the anonymous case).</summary>
    internal Task<HttpResponseMessage> PostConnectAnonymousAsync(CancellationToken ct) =>
        Anonymous.PostAsync(ConnectRoute, content: null, ct);

    /// <summary>The 200 of the connect route, with the state and the nonce cookie it minted.</summary>
    internal async Task<ConnectResult> ConnectAsync(CancellationToken ct)
    {
        using var response = await PostConnectAsync(ct);

        return await ReadConnectAsync(response, ct);
    }

    /// <summary>Parses a connect 200: the authorization URL, its state and the nonce the cookie carries.</summary>
    /// <param name="response">The response to parse; it must be the 200.</param>
    /// <param name="ct">Cancels the read.</param>
    internal static async Task<ConnectResult> ReadConnectAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"the connect route answered {body}");

        using var json = JsonDocument.Parse(body);
        var url = json.RootElement.GetProperty("authorizationUrl").GetString();
        url.ShouldNotBeNullOrWhiteSpace();

        var cookiePair = SetCookieHeader(response, NonceCookie);
        cookiePair.ShouldNotBeNull("a 200 without the __Host-nts_oauth cookie cannot be finished in any browser");

        var state = Query(url!).GetValueOrDefault("state");
        state.ShouldNotBeNullOrWhiteSpace();

        return new ConnectResult(url!, state!, CookieValue(cookiePair!), cookiePair!);
    }

    /// <summary>Drives the fake authorization endpoint with the URL the connect response returned.</summary>
    /// <param name="authorizationUrl">The <c>authorizationUrl</c> of the connect response.</param>
    /// <param name="ct">Cancels the call.</param>
    internal Task<string> AuthorizeAsync(string authorizationUrl, CancellationToken ct) =>
        Google.AuthorizeAsync(authorizationUrl, ct);

    /// <summary>GETs the callback with a code and the nonce cookie, without asserting the answer.</summary>
    internal async Task<HttpResponseMessage> CallbackAsync(
        string code,
        string state,
        string? cookiePair,
        CancellationToken ct)
    {
        using var request = CallbackRequest(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["code"] = code, ["state"] = state },
            cookiePair);

        return await Anonymous.SendAsync(request, ct);
    }

    /// <summary>GETs the callback with Google's <c>error=access_denied</c> and the nonce cookie.</summary>
    internal async Task<HttpResponseMessage> CallbackDeniedAsync(
        string state,
        string? cookiePair,
        CancellationToken ct)
    {
        using var request = CallbackRequest(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["error"] = "access_denied", ["state"] = state },
            cookiePair);

        return await Anonymous.SendAsync(request, ct);
    }

    /// <summary>GETs the callback with an arbitrary query, as a named client (the concurrency case).</summary>
    internal Task<HttpResponseMessage> CallbackAsync(
        HttpClient client,
        string code,
        string state,
        string? cookiePair,
        CancellationToken ct)
        => CallbackRawAsync(code, state, error: null, cookiePair, ct, client);

    /// <summary>GETs the callback with a raw query, for the no-oracle and header checks.</summary>
    internal Task<HttpResponseMessage> CallbackRawAsync(
        string? code,
        string? state,
        string? error,
        string? cookiePair,
        CancellationToken ct,
        HttpClient? client = null)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (code is not null)
        {
            parameters["code"] = code;
        }

        if (state is not null)
        {
            parameters["state"] = state;
        }

        if (error is not null)
        {
            parameters["error"] = error;
        }

        return (client ?? Anonymous).SendAsync(CallbackRequest(parameters, cookiePair), ct);
    }

    /// <summary>The <c>Set-Cookie</c> value of a named cookie, flags included, or null.</summary>
    internal static string? SetCookieHeader(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal))
            : null;
    }

    /// <summary>
    /// The cookie pair's value: the part between the name and the first <c>;</c>, so the <c>Set-Cookie</c>
    /// attribute list (path, flags, Max-Age) is not part of the value a test hashes or compares.
    /// </summary>
    internal static string CookieValue(string cookiePair)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookiePair);

        var value = cookiePair[(cookiePair.IndexOf('=', StringComparison.Ordinal) + 1)..];
        var attributes = value.IndexOf(';', StringComparison.Ordinal);

        return attributes < 0 ? value : value[..attributes];
    }

    /// <summary>The parsed query of a URL, as the fake recorded and the test asserts it.</summary>
    internal static Dictionary<string, string?> Query(string url)
    {
        var parsed = QueryHelpers.ParseQuery(new Uri(url).Query);

        return parsed.ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString(), StringComparer.Ordinal);
    }

    /// <summary>How many <c>oauth_states</c> rows this test's user owns.</summary>
    internal long StateRows() =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM oauth_states WHERE user_id = '{User.Id}'");

    /// <summary>
    /// How many rows carry exactly this state's hash (one, unless the purge ate it).
    /// </summary>
    internal long StateRowsFor(string state) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM oauth_states WHERE state_hash = '{AuthTestFlow.Hash(state)}'");

    /// <summary>
    /// How many marker rows exist: the row <see cref="FakeConnectChannelConnection"/> writes in its
    /// <c>WriteBeforeAnswering</c> mode as the stand-in for a <c>channel_connections</c> row (R2). A failure
    /// after the claim must leave zero, which is only meaningful because the mode writes one first.
    /// </summary>
    internal long MarkerRows() =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM oauth_states WHERE state_hash = '{FakeConnectChannelState.MarkerStateHash}'");

    /// <summary>One column of the row this test's user owns, or empty for NULL.</summary>
    internal string StateValue(string column) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT {column} FROM oauth_states WHERE user_id = '{User.Id}' ORDER BY id LIMIT 1");

    /// <summary>One column of the row carrying <paramref name="state"/>'s hash.</summary>
    internal string StateValueFor(string column, string state) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT {column} FROM oauth_states WHERE state_hash = '{AuthTestFlow.Hash(state)}'");

    /// <summary>How many <c>publishing.channel_connected</c> rows this test's user owns.</summary>
    internal long AuditRows() =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{AuditAction}' AND subject_user_id = '{User.Id}'");

    /// <summary>The <c>data</c> of the user's connect audit row, as JSON text (empty when there is none).</summary>
    internal string AuditData() =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = '{AuditAction}' AND subject_user_id = '{User.Id}' LIMIT 1");

    /// <summary>
    /// How many outbox rows this test's user owns: the connect's own
    /// <c>channel_connection_changed.v1</c> row (06.1.2-T2 raises it) plus the <c>email_requested.v1</c> the
    /// connect e-mail raises. The payload is the only column that can name a user
    /// (<c>outbox_messages</c> is platform-wide), so the failure cases can assert zero about this user even
    /// though the table is shared and other rows exist.
    /// </summary>
    internal long OutboxRows() =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM outbox_messages WHERE type IN ('{OutboxEventType}', 'email_requested.v1') "
            + $"AND payload::text LIKE '%{User.Id}%'");

    /// <summary>How many <c>email_messages</c> rows this test's user owns.</summary>
    internal long EmailRows() =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{User.Id}'");

    /// <summary>Revokes the user's family through the real port, the way a logout would.</summary>
    /// <remarks>
    /// The store's statements run inside the command's transaction (09 README D5): a revocation issued
    /// outside one would hold no family lock, so the harness opens a unit of work exactly as the logout
    /// command does and commits it.
    /// </remarks>
    /// <param name="ct">Cancels the revocation.</param>
    internal async Task RevokeFamilyAsync(CancellationToken ct)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var refresh = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();

        await unitOfWork.BeginAsync(ct);
        var revoked = await refresh.RevokeFamilyAsync(FamilyId, RevocationReason.Logout.ToWire(), ct);

        revoked.IsError.ShouldBeFalse($"the family must be revocable: {revoked.FirstError}");
        (await unitOfWork.CommitAsync(ct)).IsError.ShouldBeFalse("the revocation must commit");
    }

    /// <summary>The protector the host registered, for reading a protected column back (A4).</summary>
    internal ITokenProtector Protector() => Factory.Services.GetRequiredService<ITokenProtector>();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Anonymous.Dispose();
        await _factory.DisposeAsync();
        await _google.DisposeAsync();
    }

    private static HttpRequestMessage CallbackRequest(
        Dictionary<string, string?> parameters,
        string? cookiePair)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            QueryHelpers.AddQueryString(CallbackRoute, parameters));

        if (cookiePair is not null)
        {
            request.Headers.Add("Cookie", cookiePair);
        }

        return request;
    }

    /// <summary>One connect 200: the URL to follow, its <c>state</c> and the nonce the cookie carries.</summary>
    /// <param name="AuthorizationUrl">The <c>authorizationUrl</c> of the body.</param>
    /// <param name="State">The <c>state</c> parameter of that URL.</param>
    /// <param name="Nonce">The value of the <c>__Host-nts_oauth</c> cookie.</param>
    /// <param name="NonceCookie">The whole cookie pair, as the callback must present it.</param>
    internal sealed record ConnectResult(string AuthorizationUrl, string State, string Nonce, string NonceCookie);
}
