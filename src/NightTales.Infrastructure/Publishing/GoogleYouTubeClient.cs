using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorOr;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common.Errors;
using NightTales.Application.Publishing;
using NightTales.Domain.Common;
using GoogleHttpClientFactory = Google.Apis.Http.IHttpClientFactory;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The Google implementation of the authorization half of <see cref="IYouTubeClient"/> (06.1.2-T1, 06 README
/// D9): it builds the consent URL, exchanges and refreshes the authorization code through the token endpoint, and
/// reads the authenticated account's own channel through `Google.Apis.YouTube.v3`.
/// </summary>
/// <remarks>
/// <para>
/// Endpoints and scopes come from the bound <c>YouTube</c> section (README D9/D11), so staging and the
/// <c>FakeGoogleServer</c> of the integration suite replace them with configuration alone (README D19) — no
/// Google host is hard-coded here.
/// </para>
/// <para>
/// The three form posts run through the named client <see cref="OAuthHttpClientName"/> whose transport the host
/// configures (no redirects, pooled, gzip) and whose timeout is the configured
/// <c>YouTube:Http:TimeoutSeconds</c>. None of them is retried: an authorization code is single-use, so a second
/// attempt can only fail, and the caller decides whether the user restarts the flow.
/// </para>
/// <para>
/// The `channels.list` call runs through the named handler chain <see cref="ApiHttpClientName"/> the host
/// configures: the SDK builds its own <c>ConfigurableHttpClient</c> from a raw handler, so the handler — not a
/// <c>HttpClient</c> the SDK would own — is what this class asks <c>IHttpMessageHandlerFactory</c> for (Google's
/// `Initializer.HttpClientFactory` cannot take a `System.Net.Http.IHttpClientFactory` client).
/// </para>
/// <para>
/// Nothing here logs a body, a token, a code, a scope or a channel title: a line carries the operation name, the
/// HTTP status, and — for the one case that needs it — channel ids (README D17).
/// </para>
/// </remarks>
/// <param name="oauthHttpClients">The factory of the named client that posts the OAuth forms.</param>
/// <param name="apiHandlers">The factory of the named handler chain the Google Data API client transports through.</param>
/// <param name="options">The bound <c>YouTube</c> section: endpoints, scopes and the HTTP timeout.</param>
/// <param name="clock">The UTC clock the token expiry is computed on.</param>
/// <param name="logger">The logger of the operations below.</param>
public sealed partial class GoogleYouTubeClient(
    System.Net.Http.IHttpClientFactory oauthHttpClients,
    IHttpMessageHandlerFactory apiHandlers,
    IOptions<YouTubeOptions> options,
    IClock clock,
    ILogger<GoogleYouTubeClient> logger) : IYouTubeClient
{
    /// <summary>The named client the token, refresh and revoke form posts use (timeout <c>YouTube:Http:TimeoutSeconds</c>).</summary>
    public const string OAuthHttpClientName = "youtube-oauth";

    /// <summary>The named handler chain the `Google.Apis.YouTube.v3` client transports through.</summary>
    public const string ApiHttpClientName = "youtube-api";

    /// <summary>The application name the Google client reports in its user agent.</summary>
    private const string ApplicationName = "NightTales";

    /// <summary>The RFC 6749 grant type of the code exchange.</summary>
    private const string AuthorizationCodeGrant = "authorization_code";

    /// <summary>The RFC 6749 grant type of a refresh.</summary>
    private const string RefreshTokenGrant = "refresh_token";

    /// <summary>The only PKCE challenge method this client sends (RFC 7636, F12).</summary>
    private const string CodeChallengeMethodS256 = "S256";

    /// <summary>
    /// The lifetime assumed when Google answers a token without `expires_in` (RFC 6749 §5.1 makes the member
    /// recommended, not required). A response without an `access_token` is a failure, never a fallback.
    /// </summary>
    private const int DefaultTokenLifetimeSeconds = 3600;

    /// <inheritdoc />
    public string BuildAuthorizationUrl(
        OAuthClient client,
        OAuthAuthorizationRequest request,
        string state,
        string? codeChallenge)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        var o = options.Value;

        var query = new StringBuilder(capacity: 512);
        Append(query, "client_id", client.ClientId);
        Append(query, "redirect_uri", request.RedirectUri);
        Append(query, "response_type", "code");
        Append(query, "scope", string.Join(' ', o.EffectiveScopes()));
        Append(query, "access_type", "offline");
        Append(query, "prompt", "consent");
        Append(query, "include_granted_scopes", "false");
        Append(query, "state", state);

        if (codeChallenge is not null)
        {
            Append(query, "code_challenge", codeChallenge);
            Append(query, "code_challenge_method", CodeChallengeMethodS256);
        }

        return $"{o.AuthorizationEndpoint}?{query}";
    }

    /// <inheritdoc />
    public async Task<ErrorOr<OAuthTokens>> ExchangeCodeAsync(
        OAuthClient client,
        string code,
        string? codeVerifier,
        string redirectUri,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        var form = new List<KeyValuePair<string, string>>(capacity: 6)
        {
            new("grant_type", AuthorizationCodeGrant),
            new("code", code),
            new("client_id", client.ClientId),
            new("client_secret", client.ClientSecret),
            new("redirect_uri", redirectUri),
        };

        if (codeVerifier is not null)
        {
            form.Add(new("code_verifier", codeVerifier));
        }

        return await PostTokenAsync(OperationExchange, form, ct);
    }

    /// <inheritdoc />
    public Task<ErrorOr<OAuthTokens>> RefreshAsync(OAuthClient client, string refreshToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        return PostTokenAsync(
            OperationRefresh,
            [
                new("grant_type", RefreshTokenGrant),
                new("refresh_token", refreshToken),
                new("client_id", client.ClientId),
                new("client_secret", client.ClientSecret),
            ],
            ct);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<RevokeOutcome>> RevokeAsync(string token, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]);

        var response = await PostFormAsync(OperationRevoke, options.Value.RevokeEndpoint, content, ct);
        if (response.IsError)
        {
            return response.Errors;
        }

        if (response.Value.StatusCode == HttpStatusCode.OK)
        {
            return RevokeOutcome.Revoked;
        }

        // Google answers `invalid_token` for a token it no longer knows: the caller asked for the token to be gone
        // and it is, so the outcome is the idempotent one rather than a failure (06.4.1-T1 retries this path).
        if ((int)response.Value.StatusCode is >= 400 and < 500
            && string.Equals(ErrorCodeOf(response.Value.Body), "invalid_token", StringComparison.Ordinal))
        {
            return RevokeOutcome.AlreadyRevoked;
        }

        return IsServerFailure(response.Value.StatusCode)
            ? ProviderUnavailable(OperationRevoke)
            : ProviderRefused(OperationRevoke);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<GoogleChannel>> GetMyChannelAsync(string accessToken, CancellationToken ct)
    {
        var o = options.Value;

        try
        {
            // The service owns the wrapper it was built from, not the pooled handler: the factory keeps one handler
            // chain per host, and the per-call credential initializer carries this call's bearer token.
            using var service = new YouTubeService(new BaseClientService.Initializer
            {
                ApplicationName = ApplicationName,
                BaseUri = o.ApiBaseUrl,
                HttpClientFactory = new ApiHttpClientFactory(apiHandlers, TimeSpan.FromSeconds(o.Http.TimeoutSeconds)),
                HttpClientInitializer = GoogleCredential.FromAccessToken(accessToken),
                // A 503 must reach the caller as one failed attempt: the connect flow reports a failure to the user
                // instead of silently replaying a request whose answer the SDK would swallow behind a back-off.
                DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
            });

            var request = service.Channels.List("snippet");
            request.Mine = true;

            var response = await request.ExecuteAsync(ct);
            LogApiResponse(logger, OperationChannels, (int)HttpStatusCode.OK);

            var items = response.Items;

            if (items is null || items.Count == 0)
            {
                return Error.NotFound(ErrorCodes.NotFound, "The Google account has no YouTube channel.");
            }

            if (items.Count > 1)
            {
                // `mine=true` answers the authenticated account's channel; a second one is a Brand Account
                // (failure handling, 06.1.2-T1). Ids only: a title never reaches a log line (README D17).
                LogMultipleChannels(
                    logger, OperationChannels, items.Count, string.Join(',', items.Select(item => item.Id)));
            }

            return new GoogleChannel(items[0].Id, items[0].Snippet.Title);
        }
        catch (GoogleApiException ex)
        {
            LogApiResponse(logger, OperationChannels, (int)ex.HttpStatusCode);

            return (int)ex.HttpStatusCode >= 500
                ? ProviderUnavailable(OperationChannels)
                : ProviderRefused(OperationChannels);
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            LogApiUnreachable(logger, OperationChannels, ex.GetType().Name);

            return ProviderUnavailable(OperationChannels);
        }
    }

    /// <summary>
    /// Exchanges or refreshes through the token endpoint and parses the answer. The code, the client secret and the
    /// refresh token travel in the form body only; a response never survives in a log line.
    /// </summary>
    private async Task<ErrorOr<OAuthTokens>> PostTokenAsync(
        string operation,
        List<KeyValuePair<string, string>> form,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);

        var response = await PostFormAsync(operation, options.Value.TokenEndpoint, content, ct);
        if (response.IsError)
        {
            return response.Errors;
        }

        if (!IsSuccess(response.Value.StatusCode))
        {
            return IsServerFailure(response.Value.StatusCode)
                ? ProviderUnavailable(operation)
                : ProviderRefused(operation);
        }

        TokenPayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(response.Value.Body);
        }
        catch (JsonException)
        {
            return ProviderRefused(operation);
        }

        if (payload?.AccessToken is not { Length: > 0 } accessToken)
        {
            return ProviderRefused(operation);
        }

        var lifetime = payload.ExpiresInSeconds is > 0 ? payload.ExpiresInSeconds.Value : DefaultTokenLifetimeSeconds;
        var scope = string.IsNullOrWhiteSpace(payload.Scope) ? string.Join(' ', options.Value.EffectiveScopes()) : payload.Scope;

        return new OAuthTokens(accessToken, payload.RefreshToken, clock.UtcNow.AddSeconds(lifetime), scope);
    }

    /// <summary>
    /// Posts one form and hands the status and the body back; a transport failure, a timeout and a cancellation
    /// driven by the caller are mapped here, so every caller checks one shape. The body of a failure is read only
    /// to tell apart the responses that need it (revocation) and is never logged or described.
    /// </summary>
    private async Task<ErrorOr<FormResponse>> PostFormAsync(
        string operation,
        string endpoint,
        FormUrlEncodedContent content,
        CancellationToken ct)
    {
        try
        {
            using var response = await oauthHttpClients
                .CreateClient(OAuthHttpClientName)
                .PostAsync(endpoint, content, ct);

            LogOAuthResponse(logger, operation, (int)response.StatusCode);

            return new FormResponse(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            // A timeout surfaces as an OperationCanceledException; only the caller's own token is rethrown.
            LogOAuthUnreachable(logger, operation, ex.GetType().Name);

            return ProviderUnavailable(operation);
        }
    }

    /// <summary>The `error` member of an OAuth error body, or <c>null</c> when the body has none (never logged).</summary>
    private static string? ErrorCodeOf(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        }
        catch (JsonException)
        {
            // A body that is not JSON has already answered through its status; the classification does not need it.
            return null;
        }
    }

    /// <summary>Appends one URL-encoded query parameter; the value is never interpolated into the URL unescaped.</summary>
    private static void Append(StringBuilder query, string name, string value)
    {
        if (query.Length > 0)
        {
            query.Append('&');
        }

        query.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static bool IsServerFailure(HttpStatusCode status) => (int)status >= 500;

    /// <summary>
    /// Google refused the request itself (<c>invalid_grant</c>, <c>invalid_client</c>, <c>unauthorized_client</c>,
    /// a 4xx). The body is dropped here: only the status class decides the error, so no reason, no description and
    /// no token can travel out of this client (06 README D17).
    /// </summary>
    private static Error ProviderRefused(string operation) => Error.Failure(
        ErrorCodes.OauthExchangeFailed,
        $"The Google endpoint refused the {operation} request.");

    /// <summary>Google was unreachable, timed out or answered 5xx; the attempt is over and the user restarts.</summary>
    private static Error ProviderUnavailable(string operation) => Error.Failure(
        ErrorCodes.ServiceUnavailable,
        $"The Google endpoint did not answer the {operation} request.");

    /// <summary>
    /// The transport the `Google.Apis.YouTube.v3` client runs on (README D9/D19). The SDK builds a
    /// <c>ConfigurableHttpClient</c> from a raw <see cref="HttpMessageHandler"/> and applies the credential
    /// initializer to the client it gets back, so this hands it the host's named handler chain (no redirects,
    /// pooled, gzip) and the configured timeout — one chain for every token, and no <c>HttpClient</c> of its own.
    /// </summary>
    /// <param name="handlers">The host's handler factory.</param>
    /// <param name="timeout">The configured <c>YouTube:Http:TimeoutSeconds</c>.</param>
    private sealed class ApiHttpClientFactory(IHttpMessageHandlerFactory handlers, TimeSpan timeout) : GoogleHttpClientFactory
    {
        /// <inheritdoc />
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
        {
            var handler = new ConfigurableMessageHandler(handlers.CreateHandler(ApiHttpClientName))
            {
                ApplicationName = args.ApplicationName,
            };

            var client = new ConfigurableHttpClient(handler)
            {
                Timeout = timeout,
            };

            foreach (var initializer in args.Initializers)
            {
                initializer.Initialize(client);
            }

            return client;
        }
    }

    /// <summary>The status and the body of one form post, as the endpoint answered them.</summary>
    /// <param name="StatusCode">The HTTP status.</param>
    /// <param name="Body">The response body, parsed by the caller that needs a member of it and never logged.</param>
    private sealed record FormResponse(HttpStatusCode StatusCode, string Body);

    /// <summary>The members of an RFC 6749 §5.1 token response this client reads.</summary>
    private sealed record TokenPayload
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public long? ExpiresInSeconds { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }
    }

    private const string OperationExchange = "exchange";
    private const string OperationRefresh = "refresh";
    private const string OperationRevoke = "revoke";
    private const string OperationChannels = "channels.list";

    [LoggerMessage(EventId = 2372, Level = LogLevel.Information, Message = "youtube.oauth.response operation={Operation} status={StatusCode}")]
    private static partial void LogOAuthResponse(ILogger logger, string operation, int statusCode);

    [LoggerMessage(EventId = 2373, Level = LogLevel.Warning, Message = "youtube.oauth.unreachable operation={Operation} reason={Reason}")]
    private static partial void LogOAuthUnreachable(ILogger logger, string operation, string reason);

    [LoggerMessage(EventId = 2374, Level = LogLevel.Information, Message = "youtube.api.response operation={Operation} status={StatusCode}")]
    private static partial void LogApiResponse(ILogger logger, string operation, int statusCode);

    [LoggerMessage(EventId = 2375, Level = LogLevel.Warning, Message = "youtube.api.unreachable operation={Operation} reason={Reason}")]
    private static partial void LogApiUnreachable(ILogger logger, string operation, string reason);

    [LoggerMessage(EventId = 2376, Level = LogLevel.Warning, Message = "youtube.api.multiple-channels operation={Operation} channelCount={ChannelCount} channelIds={ChannelIds}")]
    private static partial void LogMultipleChannels(ILogger logger, string operation, int channelCount, string channelIds);
}
