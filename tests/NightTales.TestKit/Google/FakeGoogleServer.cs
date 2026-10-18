using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NightTales.TestKit.Google;

/// <summary>
/// One request the fake received: the method, the path, the parsed query and (for a form post) the
/// parsed form, so a test can assert <em>which</em> client id was used, that the exchange carried the
/// right <c>code_verifier</c>, and that no second exchange happened (README D19).
/// </summary>
/// <param name="Method">The HTTP method, as sent.</param>
/// <param name="Path">The request path, for example <c>/token</c>.</param>
/// <param name="Query">The query values, by name.</param>
/// <param name="Form">The form values for an <c>application/x-www-form-urlencoded</c> post, else null.</param>
public sealed record RecordedGoogleCall(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string>? Form)
{
    /// <summary>Whether this call is <paramref name="method"/> <paramref name="path"/>.</summary>
    public bool Is(string method, string path) =>
        string.Equals(Method, method, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path, path, StringComparison.Ordinal);

    /// <summary>One query value, or null.</summary>
    public string? QueryValue(string name) => Query.TryGetValue(name, out var value) ? value : null;

    /// <summary>One form value, or null.</summary>
    public string? FormValue(string name) =>
        Form is not null && Form.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A hand-written Google stand-in (README D19), Kestrel on an ephemeral loopback port: the authorization
/// endpoint that records the query and issues one-shot codes, the token endpoint (authorization code
/// with PKCE S256 verification, and refresh), revocation, and <c>channels.list?part=snippet&amp;mine=true</c>.
/// </summary>
/// <remarks>
/// <para>
/// No mocking library and no live Google: the host is pointed at this listener through the
/// <c>YouTube:*</c> endpoints, which is why <c>GoogleYouTubeClient</c> takes every base URL from
/// options. Every request is recorded in <see cref="Calls"/>, so an assertion can name the client id a
/// request carried and count the exchanges.
/// </para>
/// <para>
/// A code is single-use: the authorization endpoint stores it, the token endpoint removes it, so a replay
/// answers <c>invalid_grant</c> exactly as Google would. The PKCE check recomputes
/// <c>BASE64URL(SHA256(code_verifier))</c> and compares it with the challenge recorded when the code was
/// issued; a mismatched verifier is <c>invalid_grant</c>.
/// </para>
/// </remarks>
public sealed class FakeGoogleServer : IAsyncDisposable
{
    /// <summary>The path of the authorization endpoint (Google's own).</summary>
    public const string AuthorizationPath = "/o/oauth2/v2/auth";

    /// <summary>The path of the token endpoint.</summary>
    public const string TokenPath = "/token";

    /// <summary>The path of the revocation endpoint.</summary>
    public const string RevokePath = "/revoke";

    /// <summary>The path of the channel list resource.</summary>
    public const string ChannelsPath = "/youtube/v3/channels";

    private static readonly string[] RequiredAuthorizationParameters =
    [
        "client_id", "redirect_uri", "response_type", "scope", "state",
        "access_type", "prompt", "include_granted_scopes",
        "code_challenge", "code_challenge_method",
    ];

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<RecordedGoogleCall> _calls = new();
    private readonly ConcurrentDictionary<string, PendingAuthorization> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _revoked = new(StringComparer.Ordinal);
    private readonly List<string> _issuedAccessTokens = [];
    private int _tokens;

    private FakeGoogleServer(WebApplication app, FakeGoogleOptions options)
    {
        _app = app;
        Options = options;
    }

    /// <summary>The fake's behaviour and client identities; mutable while the server runs.</summary>
    public FakeGoogleOptions Options { get; }

    /// <summary>The listener's base URL, e.g. <c>http://127.0.0.1:53123/</c>.</summary>
    public Uri BaseUrl { get; private set; } = null!;

    /// <summary>The value for <c>YouTube:AuthorizationEndpoint</c>.</summary>
    public Uri AuthorizationEndpoint => new(BaseUrl, AuthorizationPath.TrimStart('/'));

    /// <summary>The value for <c>YouTube:TokenEndpoint</c>.</summary>
    public Uri TokenEndpoint => new(BaseUrl, TokenPath.TrimStart('/'));

    /// <summary>The value for <c>YouTube:RevokeEndpoint</c>.</summary>
    public Uri RevokeEndpoint => new(BaseUrl, RevokePath.TrimStart('/'));

    /// <summary>The value for <c>YouTube:ApiBaseUrl</c>.</summary>
    public Uri ApiBaseUrl => BaseUrl;

    /// <summary>Every request the listener received, in arrival order.</summary>
    public IReadOnlyList<RecordedGoogleCall> Calls => [.. _calls];

    /// <summary>How many <c>POST /token</c> requests arrived — the "exactly one exchange" count.</summary>
    public int TokenRequestCount => Calls.Count(call => call.Is("POST", TokenPath));

    /// <summary>How many <c>channels.list</c> requests arrived.</summary>
    public int ChannelRequestCount => Calls.Count(call => call.Is("GET", ChannelsPath));

    /// <summary>Every access token the token endpoint issued, in order.</summary>
    public IReadOnlyList<string> IssuedAccessTokens
    {
        get
        {
            lock (_issuedAccessTokens)
            {
                return [.. _issuedAccessTokens];
            }
        }
    }

    /// <summary>Starts the listener on an ephemeral loopback port.</summary>
    /// <param name="options">The fake's behaviour; a default instance when null.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static async Task<FakeGoogleServer> StartAsync(
        FakeGoogleOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));

        var app = builder.Build();
        var server = new FakeGoogleServer(app, options ?? new FakeGoogleOptions());

        app.Run(server.HandleAsync);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel reported no bound address.");

        server.BaseUrl = new Uri($"{addresses.Addresses.Single().TrimEnd('/')}/");

        return server;
    }

    /// <summary>
    /// Drives the authorization endpoint with the URL the connect response returned, the way a browser
    /// would, and returns the one-shot code the redirect carries. The URL must address this listener:
    /// a template that interpolated another origin, or a test pointed at another fake, would otherwise
    /// be driven against the wrong server and pass for the wrong reason.
    /// </summary>
    /// <param name="authorizationUrl">The <c>authorizationUrl</c> of the connect response.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The <c>code</c> of the redirect this fake answered with.</returns>
    /// <exception cref="InvalidOperationException">The URL is not this listener's, or the endpoint answered anything but the code redirect.</exception>
    public async Task<string> AuthorizeAsync(string authorizationUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationUrl);

        if (!authorizationUrl.StartsWith(BaseUrl.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the authorization URL does not address this fake listener.");
        }

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(authorizationUrl, cancellationToken).ConfigureAwait(false);

        var location = response.Headers.Location;
        if (location is null)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException(
                $"the fake authorization endpoint answered {(int)response.StatusCode} without a Location: {body}");
        }

        var query = QueryHelpers.ParseQuery(location.Query);
        var code = query.TryGetValue("code", out var value) ? value.ToString() : null;

        return string.IsNullOrWhiteSpace(code)
            ? throw new InvalidOperationException(
                $"the fake authorization redirect carried no code: {location}")
            : code;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        var path = request.Path.Value ?? "/";
        var query = request.Query.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.Ordinal);

        IReadOnlyDictionary<string, string>? form = null;
        if (HttpMethods.IsPost(request.Method) && request.HasFormContentType)
        {
            var read = await request.ReadFormAsync(request.HttpContext.RequestAborted).ConfigureAwait(false);
            form = read.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
        }

        _calls.Enqueue(new RecordedGoogleCall(request.Method, path, query, form));

        switch (path)
        {
            case AuthorizationPath:
                await AuthorizeEndpointAsync(context, query).ConfigureAwait(false);

                return;
            case TokenPath:
                await TokenAsync(context, form ?? new Dictionary<string, string>(StringComparer.Ordinal))
                    .ConfigureAwait(false);

                return;
            case RevokePath:
                await RevokeAsync(context, form ?? new Dictionary<string, string>(StringComparer.Ordinal))
                    .ConfigureAwait(false);

                return;
            case ChannelsPath:
                await ChannelsAsync(context).ConfigureAwait(false);

                return;
            default:
                await WriteJsonAsync(context, StatusCodes.Status404NotFound, new { error = "not_found" })
                    .ConfigureAwait(false);

                return;
        }
    }

    /// <summary>
    /// Records the authorization request and answers Google's code redirect. The required parameters are
    /// asserted here (a missing one is a 400), so a test that reaches the redirect proved the URL shape
    /// anyway; the code is stored with its challenge, client and redirect URI for the token endpoint.
    /// </summary>
    private Task AuthorizeEndpointAsync(HttpContext context, Dictionary<string, string> query)
    {
        var missing = RequiredAuthorizationParameters
            .Where(name => !query.ContainsKey(name) || string.IsNullOrWhiteSpace(query[name]))
            .ToList();

        if (missing.Count > 0
            || !string.Equals(query.GetValueOrDefault("response_type"), "code", StringComparison.Ordinal)
            || !string.Equals(query.GetValueOrDefault("code_challenge_method"), "S256", StringComparison.Ordinal))
        {
            return WriteJsonAsync(
                context,
                StatusCodes.Status400BadRequest,
                new { error = "invalid_request", missing });
        }

        var code = $"canary-authorization-code-{Interlocked.Increment(ref _tokens)}";
        _pending[code] = new PendingAuthorization(
            query["state"],
            query["client_id"],
            query["redirect_uri"],
            query["code_challenge"],
            query["scope"]);

        var redirect = QueryHelpers.AddQueryString(
            query["redirect_uri"],
            new Dictionary<string, string?>
            {
                ["code"] = code,
                ["state"] = query["state"],
                ["scope"] = query["scope"],
            });

        context.Response.StatusCode = StatusCodes.Status302Found;
        context.Response.Headers.Location = redirect;

        return Task.CompletedTask;
    }

    /// <summary>
    /// The token endpoint: a configured error first, then client authentication, then the one-shot
    /// authorization-code exchange (with the PKCE verifier check) or a refresh.
    /// </summary>
    private async Task TokenAsync(HttpContext context, IReadOnlyDictionary<string, string> form)
    {
        if (Options.TokenError is { } error)
        {
            await WriteJsonAsync(context, (int)Options.TokenStatusCode, new { error }).ConfigureAwait(false);

            return;
        }

        var clientId = form.GetValueOrDefault("client_id");
        var secret = Options.SecretFor(clientId);
        if (clientId is null
            || secret is null
            || !string.Equals(secret, form.GetValueOrDefault("client_secret"), StringComparison.Ordinal))
        {
            await WriteJsonAsync(context, StatusCodes.Status401Unauthorized, new { error = "invalid_client" })
                .ConfigureAwait(false);

            return;
        }

        switch (form.GetValueOrDefault("grant_type"))
        {
            case "authorization_code":
                await ExchangeAsync(context, form, clientId).ConfigureAwait(false);

                return;
            case "refresh_token":
                if (string.IsNullOrWhiteSpace(form.GetValueOrDefault("refresh_token")))
                {
                    await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { error = "invalid_grant" })
                        .ConfigureAwait(false);

                    return;
                }

                await WriteTokensAsync(context, scope: null, omitRefreshToken: false).ConfigureAwait(false);

                return;
            default:
                await WriteJsonAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        new { error = "unsupported_grant_type" })
                    .ConfigureAwait(false);

                return;
        }
    }

    private async Task ExchangeAsync(
        HttpContext context,
        IReadOnlyDictionary<string, string> form,
        string clientId)
    {
        var code = form.GetValueOrDefault("code");
        if (string.IsNullOrWhiteSpace(code) || !_pending.TryRemove(code, out var pending))
        {
            // One-shot: a replayed or unknown code is invalid_grant, exactly as Google answers (D2).
            await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { error = "invalid_grant" })
                .ConfigureAwait(false);

            return;
        }

        if (!string.Equals(pending.ClientId, clientId, StringComparison.Ordinal)
            || !string.Equals(pending.RedirectUri, form.GetValueOrDefault("redirect_uri"), StringComparison.Ordinal)
            || !VerifierMatches(form.GetValueOrDefault("code_verifier"), pending.CodeChallenge))
        {
            await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { error = "invalid_grant" })
                .ConfigureAwait(false);

            return;
        }

        await WriteTokensAsync(context, pending.Scope, Options.OmitRefreshToken).ConfigureAwait(false);
    }

    /// <summary>Whether the presented verifier is the S256 pre-image of the recorded challenge.</summary>
    private static bool VerifierMatches(string? verifier, string challenge)
    {
        if (string.IsNullOrWhiteSpace(verifier))
        {
            return false;
        }

        var digest = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(digest),
            Encoding.ASCII.GetBytes(challenge));
    }

    /// <summary>The scope pair Google echoes when the test does not override it.</summary>
    private const string DefaultScopes =
        "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";

    private async Task WriteTokensAsync(HttpContext context, string? scope, bool omitRefreshToken)
    {
        var index = Interlocked.Increment(ref _tokens);
        var accessToken = $"canary-access-token-{index}";
        lock (_issuedAccessTokens)
        {
            _issuedAccessTokens.Add(accessToken);
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["access_token"] = accessToken,
            ["expires_in"] = 3600,
            // Granular consent: Options.GrantedScope lets a test answer less than the request asked for (or
            // nothing at all) without touching the authorization request itself.
            ["scope"] = Options.GrantedScope ?? scope ?? DefaultScopes,
            ["token_type"] = "Bearer",
        };

        if (!omitRefreshToken)
        {
            body["refresh_token"] = $"canary-refresh-token-{index}";
        }

        await WriteJsonAsync(context, StatusCodes.Status200OK, body).ConfigureAwait(false);
    }

    /// <summary>Revocation: 200, and a later <c>channels.list</c> with that token is 401.</summary>
    private async Task RevokeAsync(HttpContext context, IReadOnlyDictionary<string, string> form)
    {
        var token = form.GetValueOrDefault("token");
        if (!string.IsNullOrWhiteSpace(token))
        {
            _revoked[token] = 0;
        }

        await WriteJsonAsync(context, StatusCodes.Status200OK, new { success = true }).ConfigureAwait(false);
    }

    /// <summary>The configured channel, Google's 401 for a revoked or unknown token, or a configured status.</summary>
    private async Task ChannelsAsync(HttpContext context)
    {
        if (Options.ChannelStatusCode != HttpStatusCode.OK)
        {
            await WriteJsonAsync(
                    context,
                    (int)Options.ChannelStatusCode,
                    new { error = new { code = (int)Options.ChannelStatusCode, message = "canary channel failure" } })
                .ConfigureAwait(false);

            return;
        }

        var token = BearerToken(context.Request.Headers.Authorization.ToString());
        if (token is null || _revoked.ContainsKey(token))
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    new { error = new { code = 401, message = "Invalid Credentials" } })
                .ConfigureAwait(false);

            return;
        }

        var items = Enumerable.Range(0, Math.Max(0, Options.ChannelCount))
            .Select(index => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = "youtube#channel",
                ["id"] = index == 0 ? Options.Channel.Id : $"{Options.Channel.Id}-{index}",
                ["snippet"] = new Dictionary<string, object?>
                {
                    ["title"] = index == 0 ? Options.Channel.Title : $"{Options.Channel.Title} {index}",
                },
            })
            .ToList();

        await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = "youtube#channelListResponse",
                    ["items"] = items,
                })
            .ConfigureAwait(false);
    }

    private static string? BearerToken(string? header) =>
        header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;

    private static async Task WriteJsonAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";

        await context.Response
            .WriteAsync(JsonSerializer.Serialize(body), context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>One issued authorization code and the parameters the token endpoint must match.</summary>
    /// <param name="State">The <c>state</c> the code belongs to.</param>
    /// <param name="ClientId">The client the authorization request carried.</param>
    /// <param name="RedirectUri">The redirect URI the authorization request carried.</param>
    /// <param name="CodeChallenge">The recorded S256 challenge the exchange's verifier must produce.</param>
    /// <param name="Scope">The scope the request asked for.</param>
    private sealed record PendingAuthorization(
        string State,
        string ClientId,
        string RedirectUri,
        string CodeChallenge,
        string Scope);
}
