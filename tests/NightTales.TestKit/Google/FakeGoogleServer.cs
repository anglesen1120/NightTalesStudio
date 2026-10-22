using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
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
/// One revocation request (<c>POST /revoke</c>), recorded for 06.4.1-T1's disconnect: the method, the path with its
/// raw query string, the <c>Content-Type</c> and the parsed form. The query is kept <em>with</em> the path so a test
/// can assert the token travelled in the form body and never in the URL (Google's revoke form post takes it in the
/// body only, and a token in a URL would reach a proxy log).
/// </summary>
/// <param name="Method">The HTTP method, as sent.</param>
/// <param name="PathAndQuery">The path and its raw query string, exactly as they arrived (e.g. <c>/revoke</c>).</param>
/// <param name="ContentType">The request's <c>Content-Type</c> header, or null when it carried none.</param>
/// <param name="Form">The form values of an <c>application/x-www-form-urlencoded</c> post; empty otherwise.</param>
public sealed record RevokeRequest(
    string Method,
    string PathAndQuery,
    string? ContentType,
    IReadOnlyDictionary<string, string> Form)
{
    /// <summary>One form value, or null.</summary>
    public string? FormValue(string name) =>
        Form.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A hand-written Google stand-in (README D19), Kestrel on an ephemeral loopback port: the authorization
/// endpoint that records the query and issues one-shot codes, the token endpoint (authorization code
/// with PKCE S256 verification, and refresh), revocation, <c>channels.list?part=snippet&amp;mine=true</c>,
/// and — added by 06.2.3-T1 — the resumable-upload surface: session create, chunk <c>PUT</c> with
/// <c>308</c> + <c>Range</c>, the <c>bytes */total</c> probe, <c>404</c> expiry, the drop-after-last-chunk
/// fault and <c>videos.list?part=status</c>.
/// </summary>
/// <remarks>
/// <para>
/// No mocking library and no live Google: the host is pointed at this listener through the
/// <c>YouTube:*</c> endpoints, which is why <c>GoogleYouTubeClient</c> takes every base URL from
/// options. Every request is recorded in <see cref="Calls"/>, so an assertion can name the client id a
/// request carried and count the exchanges. The revoke route additionally records each call's method, raw
/// path and query, <c>Content-Type</c> and parsed form in <see cref="RevokeRequests"/>, and its answer is switched
/// through <see cref="FakeGoogleOptions.RevokeStatusCode"/>.
/// </para>
/// <para>
/// A code is single-use: the authorization endpoint stores it, the token endpoint removes it, so a replay
/// answers <c>invalid_grant</c> exactly as Google would. The PKCE check recomputes
/// <c>BASE64URL(SHA256(code_verifier))</c> and compares it with the challenge recorded when the code was
/// issued; a mismatched verifier is <c>invalid_grant</c>.
/// </para>
/// <para>
/// The upload surface keeps only the byte counts and the <c>Content-Range</c> headers it was given
/// (<see cref="Chunks"/>, <see cref="SessionCreates"/>) — never a body, a token or a title — so a 25 MB
/// payload can be streamed through it in constant memory and still be asserted chunk by chunk. Every
/// fault is configured through <see cref="UploadScenario"/>: a scenario a test did not set never changes
/// an answer.
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

    /// <summary>The path of the video list resource (<c>videos.list?part=status</c>).</summary>
    public const string VideosPath = "/youtube/v3/videos";

    /// <summary>The path that creates a resumable session (<c>videos.insert?uploadType=resumable</c>).</summary>
    public const string UploadVideosPath = "/upload/youtube/v3/videos";

    /// <summary>
    /// The prefix of a session resource: the upload <c>PUT</c>s address <c>{prefix}{id}</c>. It lives under the
    /// upload root (<see cref="UploadVideosPath"/>'s directory), the way Google's own session URI is the create
    /// request's path plus <c>upload_id</c>, so a client that requires the session to stay under the configured
    /// upload root is exercised rather than accommodated.
    /// </summary>
    public const string UploadSessionPrefix = "/upload/youtube/v3/session/";

    /// <summary>The <c>Content-Range</c> value of a probe: <c>bytes */&lt;total&gt;</c>.</summary>
    public const string ProbeContentRangePrefix = "bytes */";

    /// <summary>The query key of the session handle a session URI carries, so a test can assert it is never logged.</summary>
    public const string UploadIdQueryKey = "upload_id";

    private static readonly string[] RequiredAuthorizationParameters =
    [
        "client_id", "redirect_uri", "response_type", "scope", "state",
        "access_type", "prompt", "include_granted_scopes",
        "code_challenge", "code_challenge_method",
    ];

    private const int BodyChunkBytes = 64 * 1024;

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<RecordedGoogleCall> _calls = new();
    private readonly ConcurrentDictionary<string, PendingAuthorization> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _revoked = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RevokeRequest> _revokeRequests = new();
    private readonly List<string> _issuedAccessTokens = [];
    private readonly ConcurrentDictionary<string, FakeUploadSession> _uploadSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<FakeUploadChunk> _chunks = new();
    private readonly ConcurrentQueue<FakeUploadSessionCreate> _sessionCreates = new();
    private readonly ConcurrentQueue<IReadOnlyList<string>> _videoListBatches = new();
    private int _tokens;
    private int _sessions;
    private int _sessionCreateCalls;
    private int _chunkRequests;
    private int _revokeCalls;
    private int _probeCalls;
    private int _goneSessionProbes;
    private int _videoListCalls;
    private int _serverErrorsRemaining;
    private readonly ConcurrentQueue<TaskCompletionSource> _chunkGates = new();
    private readonly SemaphoreSlim _heldChunkSignal = new(0);
    private volatile bool _holdChunks;
    private int _heldChunks;
    private int _acceptedChunks;
    private int _authErrorChunksRemaining;
    private string? _lastVideoId;
    private UploadScenario _scenario = UploadScenario.None;

    private FakeGoogleServer(WebApplication app, FakeGoogleOptions options)
    {
        _app = app;
        Options = options;
    }

    /// <summary>The fake's behaviour and client identities; mutable while the server runs.</summary>
    public FakeGoogleOptions Options { get; }

    /// <summary>
    /// The upload fault switches, set from a test before the request under test. Assigning a scenario also arms
    /// its <see cref="UploadScenario.ChunkServerErrors"/> count, so "the next two chunk requests fail" means
    /// exactly the next two after the assignment.
    /// </summary>
    public UploadScenario Scenario
    {
        get => _scenario;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _scenario = value;
            Interlocked.Exchange(
                ref _serverErrorsRemaining,
                value.ServerErrorsPersistently ? int.MaxValue : value.ChunkServerErrors);
            Interlocked.Exchange(ref _acceptedChunks, 0);
            Interlocked.Exchange(ref _authErrorChunksRemaining, value.AuthErrorChunkRequest);
        }
    }

    /// <summary>
    /// Holds every chunk <c>PUT</c> from here on until the test releases it one by one (PERF-3): the handler test that
    /// needs a run parked mid-stream, with another run racing it, drives it through this.
    /// </summary>
    public void HoldChunks() => _holdChunks = true;

    /// <summary>Releases the oldest held chunk request, and only that one.</summary>
    public void ReleaseChunk()
    {
        if (_chunkGates.TryDequeue(out var gate))
        {
            gate.TrySetResult();
        }
    }

    /// <summary>Stops holding chunks and releases every request parked on the gate.</summary>
    public void ReleaseChunks()
    {
        _holdChunks = false;

        while (_chunkGates.TryDequeue(out var gate))
        {
            gate.TrySetResult();
        }
    }

    /// <summary>How many chunk <c>PUT</c>s have been parked on the hold gate so far.</summary>
    public int HeldChunks => Volatile.Read(ref _heldChunks);

    /// <summary>Completes once at least <paramref name="count"/> chunk <c>PUT</c>s have parked on the hold gate.</summary>
    /// <param name="count">How many held chunks to wait for.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task that completes when the count is reached.</returns>
    public async Task WaitForHeldChunksAsync(int count, CancellationToken ct = default)
    {
        while (HeldChunks < count)
        {
            await _heldChunkSignal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Parks one chunk request on the hold gate when the test armed <see cref="HoldChunks"/>.</summary>
    /// <returns>A task the caller awaits before answering the request.</returns>
    private async Task HoldChunkIfAskedAsync()
    {
        if (!_holdChunks)
        {
            return;
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _chunkGates.Enqueue(gate);
        Interlocked.Increment(ref _heldChunks);
        _heldChunkSignal.Release();

        await gate.Task.ConfigureAwait(false);
    }

    /// <summary>The listener's base URL, e.g. <c>http://127.0.0.1:53123/</c>.</summary>
    public Uri BaseUrl { get; private set; } = null!;

    /// <summary>The value for <c>YouTube:UploadBaseUrl</c>: the resumable endpoint root of this listener.</summary>
    public Uri UploadBaseUrl => new(BaseUrl, "upload/youtube/v3/");

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

    /// <summary>How many resumable sessions were created (<c>POST …?uploadType=resumable</c>).</summary>
    public int SessionCreateCalls => Volatile.Read(ref _sessionCreateCalls);

    /// <summary>How many data-range <c>PUT</c>s arrived (a probe is not one; a retry is).</summary>
    public int ChunkRequests => Volatile.Read(ref _chunkRequests);

    /// <summary>How many <c>bytes */total</c> probes arrived.</summary>
    public int ProbeCalls => Volatile.Read(ref _probeCalls);

    /// <summary>
    /// How many <c>bytes */total</c> probes named a session this listener never created — the shape of a stored URI
    /// whose session Google has already dropped (the <c>404</c> the restart path turns into a fresh session).
    /// </summary>
    public int GoneSessionProbes => Volatile.Read(ref _goneSessionProbes);

    /// <summary>How many <c>videos.list</c> reads arrived.</summary>
    public int VideoListCalls => Volatile.Read(ref _videoListCalls);

    /// <summary>
    /// How many <c>POST /token</c> requests carried <c>grant_type=refresh_token</c> — the refresh count, read
    /// from the recorded forms (the token endpoint itself is 06.1.2-T1's).
    /// </summary>
    public int RefreshCalls => Calls.Count(call =>
        call.Is("POST", TokenPath)
        && string.Equals(call.FormValue("grant_type"), "refresh_token", StringComparison.Ordinal));

    /// <summary>
    /// Every request the revocation route received, in arrival order, with its method, the path and raw query it
    /// addressed, its <c>Content-Type</c> and its parsed form — so a test can assert that the disconnect sent
    /// exactly one revoke, with the token in the form body and nothing in the URL (06.4.1-T1). A retried revoke is
    /// one more entry, which is what makes the retry count assertable.
    /// </summary>
    public IReadOnlyList<RevokeRequest> RevokeRequests => [.. _revokeRequests];

    /// <summary>
    /// How many requests the revocation route received — the "exactly one revoke" count. It counts arrivals, not
    /// successes, so under a configured failure answer (<see cref="FakeGoogleOptions.RevokeStatusCode"/>) every entry
    /// above the first is a retry.
    /// </summary>
    public int RevokeCount => Volatile.Read(ref _revokeCalls);

    /// <summary>The video id of the last completed session, or null while nothing has completed.</summary>
    public string? LastVideoId => Volatile.Read(ref _lastVideoId);

    /// <summary>
    /// Every upload request that carried a <c>Content-Range</c>, in arrival order: the byte counts and the header
    /// only, so a multi-megabyte payload is asserted without having been held in memory.
    /// </summary>
    public IReadOnlyList<FakeUploadChunk> Chunks => [.. _chunks];

    /// <summary>Every <c>Content-Range</c> header value the upload surface received, in arrival order.</summary>
    public IReadOnlyList<string> ContentRanges => [.. _chunks.Select(chunk => chunk.ContentRange)];

    /// <summary>Every session-create request, in arrival order.</summary>
    public IReadOnlyList<FakeUploadSessionCreate> SessionCreates => [.. _sessionCreates];

    /// <summary>The last session-create request, or null when none arrived.</summary>
    public FakeUploadSessionCreate? LastSessionCreate => _sessionCreates.LastOrDefault();

    /// <summary>Every set of ids a <c>videos.list</c> read asked for, in arrival order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> VideoListIdBatches => [.. _videoListBatches];

    /// <summary>How many bytes the session of <paramref name="sessionUri"/> holds, or null when it is unknown.</summary>
    /// <param name="sessionUri">The session URI a create answered with.</param>
    public long? HeldBytes(Uri sessionUri)
    {
        ArgumentNullException.ThrowIfNull(sessionUri);

        return _uploadSessions.TryGetValue(SessionId(sessionUri), out var session) ? session.Held : null;
    }

    /// <summary>The session id a session URI addresses (its last path segment).</summary>
    /// <param name="sessionUri">The session URI a create answered with.</param>
    public static string SessionId(Uri sessionUri)
    {
        ArgumentNullException.ThrowIfNull(sessionUri);

        return sessionUri.AbsolutePath[UploadSessionPrefix.Length..];
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
            case VideosPath:
                await VideosAsync(context, query).ConfigureAwait(false);

                return;
            case UploadVideosPath:
                await CreateUploadSessionAsync(context, query).ConfigureAwait(false);

                return;
            default:
                if (path.StartsWith(UploadSessionPrefix, StringComparison.Ordinal))
                {
                    await UploadSessionAsync(context, path[UploadSessionPrefix.Length..]).ConfigureAwait(false);

                    return;
                }

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

                await WriteTokensAsync(context, scope: null, omitRefreshToken: false, tokenPrefix: "ya29.")
                    .ConfigureAwait(false);

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

    /// <summary>
    /// The tokens an answer carries, recorded in <see cref="IssuedAccessTokens"/>. The prefix is the caller's:
    /// a refresh rotates to a fresh <c>ya29.</c>-prefixed pair, the shape a real Google access token has (so a
    /// test that searches a log for a leaked token searches for a realistic one), while the authorization-code
    /// exchange keeps the plain canary prefix the connect tests derive their refresh token from.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="scope">The granted scope to echo, when the answer names one.</param>
    /// <param name="omitRefreshToken">Whether the answer leaves out <c>refresh_token</c>.</param>
    /// <param name="tokenPrefix">The prefix of both rotated values.</param>
    private async Task WriteTokensAsync(
        HttpContext context,
        string? scope,
        bool omitRefreshToken,
        string tokenPrefix = "canary-")
    {
        var index = Interlocked.Increment(ref _tokens);
        var accessToken = $"{tokenPrefix}access-token-{index}";
        lock (_issuedAccessTokens)
        {
            _issuedAccessTokens.Add(accessToken);
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["access_token"] = accessToken,
            ["expires_in"] = Options.ExpiresInSeconds,
            // Granular consent: Options.GrantedScope lets a test answer less than the request asked for (or
            // nothing at all) without touching the authorization request itself.
            ["scope"] = Options.GrantedScope ?? scope ?? DefaultScopes,
            ["token_type"] = "Bearer",
        };

        if (!omitRefreshToken)
        {
            body["refresh_token"] = $"{tokenPrefix}refresh-token-{index}";
        }

        await WriteJsonAsync(context, StatusCodes.Status200OK, body).ConfigureAwait(false);
    }

    /// <summary>
    /// Revocation: 200, and a later <c>channels.list</c> with that token is 401. The request is recorded in
    /// <see cref="RevokeRequests"/> first, whatever the answer will be, so a test can count the attempts, name the
    /// form field the token travelled in and prove the URL carried none. <see cref="FakeGoogleOptions.RevokeStatusCode"/>
    /// picks the answer and its body: <c>200</c> (the default) is the success this route has always answered and
    /// revokes the token, a <c>4xx</c> is Google's already-revoked <c>{"error":"invalid_token"}</c>, anything else is
    /// a server-shaped failure (<c>500</c> for the retry-then-<c>googleRevoke = "failed"</c> case). A configured
    /// failure leaves the token usable, so "a revoke that failed did not revoke" is assertable.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="form">The parsed form; the token travels in <c>token</c>.</param>
    private async Task RevokeAsync(HttpContext context, IReadOnlyDictionary<string, string> form)
    {
        Interlocked.Increment(ref _revokeCalls);
        _revokeRequests.Enqueue(new RevokeRequest(
            context.Request.Method,
            $"{context.Request.Path.Value}{context.Request.QueryString.Value}",
            context.Request.ContentType,
            form));

        var status = (int)Options.RevokeStatusCode;

        if (status == StatusCodes.Status200OK)
        {
            var token = form.GetValueOrDefault("token");
            if (!string.IsNullOrWhiteSpace(token))
            {
                _revoked[token] = 0;
            }

            await WriteJsonAsync(context, StatusCodes.Status200OK, new { success = true }).ConfigureAwait(false);

            return;
        }

        if (status is >= 400 and < 500)
        {
            await WriteJsonAsync(context, status, new { error = "invalid_token" }).ConfigureAwait(false);

            return;
        }

        await WriteJsonAsync(
                context,
                status,
                new { error = new { code = status, message = "canary revoke failure" } })
            .ConfigureAwait(false);
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

    /// <summary>
    /// Answers <c>videos.list?part=status</c>: one row per known id, the unknown ids simply absent (Google's own
    /// shape), and the requested batches recorded so a batching test can assert how many calls the client made.
    /// A revoked or unknown bearer token is 401, like <c>channels.list</c>.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="query">The parsed query; <c>id</c> is a comma-joined list.</param>
    private async Task VideosAsync(HttpContext context, IReadOnlyDictionary<string, string> query)
    {
        Interlocked.Increment(ref _videoListCalls);

        var token = BearerToken(context.Request.Headers["Authorization"].ToString());
        if (token is null || _revoked.ContainsKey(token))
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    new { error = new { code = 401, message = "Invalid Credentials" } })
                .ConfigureAwait(false);

            return;
        }

        var ids = (query.GetValueOrDefault("id") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _videoListBatches.Enqueue(ids);

        var items = ids
            .Where(Options.VideoStatuses.ContainsKey)
            .Select(id =>
            {
                var status = Options.VideoStatuses[id];

                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = "youtube#video",
                    ["id"] = id,
                    ["status"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["uploadStatus"] = status.UploadStatus,
                        // Google omits what does not apply: an unfailed row carries no failureReason, so a client
                        // that defaults it must not be papered over by the fake inventing one.
                        ["failureReason"] = status.FailureReason,
                        ["rejectionReason"] = status.RejectionReason,
                        ["privacyStatus"] = status.PrivacyStatus,
                    },
                };
            })
            .ToList();

        await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = "youtube#videoListResponse",
                    ["items"] = items,
                })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a resumable session: no content is stored, the metadata in the body is recorded (never the raw
    /// JSON), and the answer is Google's — 200 with a <c>Location</c> naming the session resource. The session
    /// knows the total from <c>X-Upload-Content-Length</c> (falling back to a body <c>Content-Range</c>), so a
    /// re-upload of the same session can answer "complete" without a chunk ever arriving.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="query">The parsed query: <c>uploadType</c> and <c>part</c>.</param>
    private async Task CreateUploadSessionAsync(HttpContext context, IReadOnlyDictionary<string, string> query)
    {
        Interlocked.Increment(ref _sessionCreateCalls);

        var token = BearerToken(context.Request.Headers["Authorization"].ToString());
        if (token is null || _revoked.ContainsKey(token))
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    new { error = new { code = 401, message = "Invalid Credentials" } })
                .ConfigureAwait(false);

            return;
        }

        var json = await ReadBodyTextAsync(context).ConfigureAwait(false);
        var declaredLength = ParseLong(context.Request.Headers["X-Upload-Content-Length"].ToString())
            ?? (TryParseContentRange(context.Request.Headers["Content-Range"].ToString(), out var createRange)
                ? createRange.Total
                : null);

        // The insert is refused after it was issued (a token Google no longer accepts): the shape a release predicate
        // must not treat as "nothing was sent".
        if (Scenario.AuthFailureAtSessionCreate)
        {
            await WriteUploadFailureAsync(context, UploadScenario.Reasons.AuthError).ConfigureAwait(false);

            return;
        }

        var index = Interlocked.Increment(ref _sessions);
        var sessionId = $"canary-session-{index}";
        var videoId = $"canary-video-{index}";
        var location = new Uri(
            BaseUrl,
            $"{UploadSessionPrefix.TrimStart('/')}{sessionId}?{UploadIdQueryKey}={sessionId}");

        _sessionCreates.Enqueue(ParseSessionCreate(json, sessionId, videoId, location, declaredLength, query, context));
        _uploadSessions[sessionId] = new FakeUploadSession(sessionId, videoId) { Total = declaredLength ?? 0 };

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["Location"] = location.AbsoluteUri;
    }

    /// <summary>
    /// The session resource: a chunk <c>PUT</c> accumulates bytes and answers 308 + <c>Range</c> until the total
    /// is reached, a <c>bytes */total</c> probe reports where the session stands, and a session the test expired
    /// is 404. The body is counted in a pooled buffer, never held, so a 25 MB payload costs the fake nothing.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="sessionId">The session id the request path addressed.</param>
    private async Task UploadSessionAsync(HttpContext context, string sessionId)
    {
        if (Scenario.ExpireSession)
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    new { error = new { code = 404, message = "canary session expired" } })
                .ConfigureAwait(false);

            return;
        }

        if (Scenario.UploadFailureReason is { } reason)
        {
            await WriteUploadFailureAsync(context, reason).ConfigureAwait(false);

            return;
        }

        var token = BearerToken(context.Request.Headers["Authorization"].ToString());
        if (token is null || _revoked.ContainsKey(token))
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    new { error = new { code = 401, message = "Invalid Credentials" } })
                .ConfigureAwait(false);

            return;
        }

        var header = context.Request.Headers["Content-Range"].ToString();
        if (!TryParseContentRange(header, out var range))
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new { error = new { code = 400, message = "canary invalid Content-Range" } })
                .ConfigureAwait(false);

            return;
        }

        if (!_uploadSessions.TryGetValue(sessionId, out var session))
        {
            if (range.IsProbe)
            {
                Interlocked.Increment(ref _goneSessionProbes);
            }

            await WriteJsonAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    new { error = new { code = 404, message = "canary unknown session" } })
                .ConfigureAwait(false);

            return;
        }

        if (range.IsProbe)
        {
            await AnswerProbeAsync(context, session, header, range).ConfigureAwait(false);

            return;
        }

        // PERF-3: a test can park the stream here, which is what a duplicate run races against.
        await HoldChunkIfAskedAsync().ConfigureAwait(false);

        var received = await CountBodyAsync(context).ConfigureAwait(false);
        var declared = range.End - range.Start + 1;
        _chunks.Enqueue(new FakeUploadChunk(header, received, declared, range.Start, range.End, range.Total, false, session.Id));
        Interlocked.Increment(ref _chunkRequests);

        // PERF-5: the token expired mid-stream, so exactly one chunk request is refused as unauthorized.
        if (Interlocked.Decrement(ref _authErrorChunksRemaining) == 0)
        {
            await WriteUploadFailureAsync(context, UploadScenario.Reasons.AuthError).ConfigureAwait(false);

            return;
        }

        // PERF-2: the session keeps a prefix and every later chunk dies, which is the state a resume continues from.
        if (Scenario.AcceptChunksBeforeDrop > 0
            && Interlocked.Increment(ref _acceptedChunks) > Scenario.AcceptChunksBeforeDrop)
        {
            context.Abort();

            return;
        }

        if (Scenario.DropLastChunk && range.End + 1 >= range.Total)
        {
            // The "connection dropped after the last chunk" fault (plan §"Failure handling"): the bytes are read,
            // no answer follows and the connection dies — for every attempt, until the test clears the scenario.
            context.Abort();

            return;
        }

        if (Interlocked.Decrement(ref _serverErrorsRemaining) >= 0)
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    new { error = new { code = 503, message = "canary backend error" } })
                .ConfigureAwait(false);

            return;
        }

        string? completedId = null;
        var gap = false;
        long held;

        lock (session.Sync)
        {
            if (range.Start == session.Held)
            {
                // SEC-5: a scenario can hold the session back to a bounded append, the hostile peer that answers a
                // 308 one byte further along however much the request carried.
                var limit = Scenario.ChunkProgressBytes;
                session.Held = range.Start + (limit > 0 ? Math.Min(received, limit) : received);
            }
            else if (range.End >= session.Held)
            {
                // A gap: a start the session is not at yet is not resumable, and Google refuses it.
                gap = true;
            }

            // Otherwise the request resends bytes the session already holds: no append, and the answer below
            // reports where the session really is.
            if (!gap)
            {
                session.Total = Math.Max(session.Total, range.Total);

                // The request declared the end of the object, so Google answers the terminal shape. A short body
                // leaves the session incomplete and carries no video id — the failure the client maps to
                // Transport, which is also how the real API behaves when the stream is shorter than the total.
                if (range.End + 1 >= range.Total && session.Held >= range.Total && received >= declared)
                {
                    completedId = session.VideoId;
                }
            }

            held = session.Held;
        }

        if (gap)
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new { error = new { code = 400, message = "canary invalid Content-Range" } })
                .ConfigureAwait(false);

            return;
        }

        if (completedId is not null)
        {
            Volatile.Write(ref _lastVideoId, completedId);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new { id = completedId }).ConfigureAwait(false);

            return;
        }

        // The request declared the end of the object but the session does not hold all of it: Google answers the
        // session's real position, never a completion. A body shorter than the range it declared is what leaves it
        // incomplete, which is why a 2xx without an id is the shape the client maps to Transport.
        await WriteRangeAsync(context, held).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers <c>bytes */total</c>: 200 + the video id when the session holds everything, 308 + <c>Range</c> when
    /// it holds part of it, and 308 with no <c>Range</c> when it holds nothing (the caller starts at 0).
    /// </summary>
    /// <param name="context">The probe.</param>
    /// <param name="session">The session it addressed.</param>
    /// <param name="header">The raw <c>Content-Range</c> value, recorded verbatim.</param>
    /// <param name="range">The parsed probe.</param>
    private async Task AnswerProbeAsync(HttpContext context, FakeUploadSession session, string header, ParsedRange range)
    {
        Interlocked.Increment(ref _probeCalls);
        _chunks.Enqueue(new FakeUploadChunk(header, 0, 0, range.Start, range.End, range.Total, true, session.Id));

        long held;
        bool complete;

        lock (session.Sync)
        {
            if (range.Total > 0)
            {
                session.Total = range.Total;
            }

            held = session.Held;
            complete = session.Total > 0 && held >= session.Total;
        }

        if (complete)
        {
            Volatile.Write(ref _lastVideoId, session.VideoId);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new { id = session.VideoId }).ConfigureAwait(false);

            return;
        }

        if (held > 0)
        {
            await WriteRangeAsync(context, held).ConfigureAwait(false);

            return;
        }

        context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
    }

    /// <summary>Google's upload refusal body: the quota, the instant, and the unauthorized answer.</summary>
    private static async Task WriteUploadFailureAsync(HttpContext context, string reason)
    {
        var status = reason switch
        {
            "quotaExceeded" or "uploadLimitExceeded" => StatusCodes.Status403Forbidden,
            "invalidPublishAt" => StatusCodes.Status400BadRequest,
            UploadScenario.Reasons.AuthError => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status400BadRequest,
        };

        await WriteJsonAsync(
                context,
                status,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["error"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = status,
                        ["message"] = $"canary {reason}",
                        ["errors"] = new List<Dictionary<string, object?>>(1)
                        {
                            new(StringComparer.Ordinal)
                            {
                                ["domain"] = "youtube.common",
                                ["reason"] = reason,
                                ["message"] = $"canary {reason}",
                            },
                        },
                    },
                })
            .ConfigureAwait(false);
    }

    /// <summary>308 with the <c>Range</c> of what the session holds so far (nothing held is no <c>Range</c>).</summary>
    private static Task WriteRangeAsync(HttpContext context, long held)
    {
        if (held <= 0)
        {
            context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;

            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
        context.Response.Headers["Range"] = $"bytes=0-{held - 1}";

        return Task.CompletedTask;
    }

    /// <summary>The request body as text; only ever used for the small session metadata document.</summary>
    private static async Task<string> ReadBodyTextAsync(HttpContext context)
    {
        using var reader = new StreamReader(
            context.Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: BodyChunkBytes,
            leaveOpen: true);

        return await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts and discards a chunk body through one pooled buffer: the fake must never hold a chunk, or the
    /// "streamed, not buffered" guarantee would be untestable against it.
    /// </summary>
    private static async Task<long> CountBodyAsync(HttpContext context)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BodyChunkBytes);
        try
        {
            long total = 0;
            int read;
            while ((read = await context.Request.Body
                       .ReadAsync(buffer.AsMemory(0, BodyChunkBytes), context.RequestAborted)
                       .ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Parses <c>bytes a-b/total</c> (a <c>Range</c>) or <c>bytes */total</c> (a probe).</summary>
    private static bool TryParseContentRange(string? header, out ParsedRange range)
    {
        const string Prefix = "bytes ";

        range = default;
        if (header is null || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var span = header.AsSpan(Prefix.Length);
        var slash = span.IndexOf('/');
        if (slash <= 0 || !long.TryParse(span[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var total))
        {
            return false;
        }

        var rangeText = span[..slash];
        if (rangeText.SequenceEqual("*"))
        {
            range = new ParsedRange(0, total - 1, total, true);

            return true;
        }

        var dash = rangeText.IndexOf('-');
        if (dash <= 0
            || !long.TryParse(rangeText[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || !long.TryParse(rangeText[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var end)
            || end < start
            || total <= end)
        {
            return false;
        }

        range = new ParsedRange(start, end, total, false);

        return true;
    }

    /// <summary>The recorded session-create request: the metadata the client actually put on the wire.</summary>
    private static FakeUploadSessionCreate ParseSessionCreate(
        string json,
        string sessionId,
        string videoId,
        Uri location,
        long? declaredLength,
        IReadOnlyDictionary<string, string> query,
        HttpContext context)
    {
        bool? containsSyntheticMedia = null;
        bool? selfDeclaredMadeForKids = null;
        string? privacyStatus = null;
        string? publishAt = null;
        string? title = null;
        int? tagCount = null;

        if (!string.IsNullOrWhiteSpace(json))
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("status", out var status))
            {
                containsSyntheticMedia = ReadBool(status, "containsSyntheticMedia");
                selfDeclaredMadeForKids = ReadBool(status, "selfDeclaredMadeForKids");
                privacyStatus = ReadString(status, "privacyStatus");
                publishAt = ReadString(status, "publishAt");
            }

            if (root.TryGetProperty("snippet", out var snippet))
            {
                title = ReadString(snippet, "title");
                tagCount = snippet.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                    ? tags.GetArrayLength()
                    : null;
            }
        }

        return new FakeUploadSessionCreate(
            sessionId,
            videoId,
            location.AbsoluteUri,
            query.GetValueOrDefault("uploadType"),
            query.GetValueOrDefault("part"),
            context.Request.ContentType,
            context.Request.Headers["X-Upload-Content-Type"].ToString(),
            declaredLength,
            containsSyntheticMedia,
            selfDeclaredMadeForKids,
            privacyStatus,
            publishAt,
            title,
            tagCount);
    }

    private static bool? ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

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

    /// <summary>The session state: only how many bytes it holds and the total it was told about.</summary>
    /// <param name="id">The session id.</param>
    /// <param name="videoId">The video id a completion of this session answers with.</param>
    private sealed class FakeUploadSession(string id, string videoId)
    {
        /// <summary>The monitor guarding <see cref="Held"/> and <see cref="Total"/>.</summary>
        public object Sync { get; } = new();

        /// <summary>The session id.</summary>
        public string Id { get; } = id;

        /// <summary>The video id a completion answers with.</summary>
        public string VideoId { get; } = videoId;

        /// <summary>How many bytes the session holds.</summary>
        public long Held { get; set; }

        /// <summary>The total the session was told about (0 while unknown).</summary>
        public long Total { get; set; }
    }

    /// <summary>A parsed <c>Content-Range</c>: a data range, or a probe's <c>bytes */total</c>.</summary>
    /// <param name="Start">The first byte position, or 0 for a probe.</param>
    /// <param name="End">The last byte position, or <paramref name="Total"/> - 1 for a probe.</param>
    /// <param name="Total">The total of the object.</param>
    /// <param name="IsProbe">Whether the header was a probe.</param>
    private readonly record struct ParsedRange(long Start, long End, long Total, bool IsProbe);
}

/// <summary>One upload request that carried a <c>Content-Range</c>: a data chunk or a probe.</summary>
/// <param name="ContentRange">The header value, verbatim — the thing the client is asserted on.</param>
/// <param name="Received">The body bytes that actually arrived (0 for a probe).</param>
/// <param name="Declared">The bytes the header declared (0 for a probe).</param>
/// <param name="Start">The first byte position the header named.</param>
/// <param name="End">The last byte position the header named.</param>
/// <param name="Total">The total the header named.</param>
/// <param name="IsProbe">Whether the request was a <c>bytes */total</c> probe instead of a chunk.</param>
/// <param name="SessionId">The session the request addressed.</param>
public sealed record FakeUploadChunk(
    string ContentRange,
    long Received,
    long Declared,
    long Start,
    long End,
    long Total,
    bool IsProbe,
    string SessionId);

/// <summary>
/// The recorded session-create request: the metadata the client actually put on the wire. The title and the body
/// are recorded as data (a test asserts what travelled); nothing here is ever written to a log or a file.
/// </summary>
/// <param name="SessionId">The session id the answer's <c>Location</c> names.</param>
/// <param name="VideoId">The video id a completion of the session answers with.</param>
/// <param name="Location">The absolute <c>Location</c> value the fake answered with.</param>
/// <param name="UploadType">The <c>uploadType</c> query value.</param>
/// <param name="Part">The <c>part</c> query value.</param>
/// <param name="ContentType">The request's <c>Content-Type</c>.</param>
/// <param name="UploadContentType">The <c>X-Upload-Content-Type</c> header (the asset's content type).</param>
/// <param name="DeclaredLength">The total the request declared, from <c>X-Upload-Content-Length</c>.</param>
/// <param name="ContainsSyntheticMedia">The <c>status.containsSyntheticMedia</c> the request carried (D8).</param>
/// <param name="SelfDeclaredMadeForKids">The <c>status.selfDeclaredMadeForKids</c> the request carried.</param>
/// <param name="PrivacyStatus">The <c>status.privacyStatus</c> the request carried.</param>
/// <param name="PublishAt">The <c>status.publishAt</c> the request carried, when it carried one.</param>
/// <param name="Title">The <c>snippet.title</c> the request carried (asserted to be absent from the log).</param>
/// <param name="TagCount">How many <c>snippet.tags</c> the request carried.</param>
public sealed record FakeUploadSessionCreate(
    string SessionId,
    string VideoId,
    string Location,
    string? UploadType,
    string? Part,
    string? ContentType,
    string? UploadContentType,
    long? DeclaredLength,
    bool? ContainsSyntheticMedia,
    bool? SelfDeclaredMadeForKids,
    string? PrivacyStatus,
    string? PublishAt,
    string? Title,
    int? TagCount);
