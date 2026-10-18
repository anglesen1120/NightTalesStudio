using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Application.Publishing;
using NightTales.Domain.Common;
using GoogleHttpClientFactory = Google.Apis.Http.IHttpClientFactory;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The Google implementation of <see cref="IYouTubeClient"/> (06 README D9): the authorization half
/// (06.1.2-T1) builds the consent URL, exchanges and refreshes the authorization code through the token endpoint,
/// and reads the authenticated account's own channel through `Google.Apis.YouTube.v3`; the upload half
/// (06.2.3-T1) creates a resumable session without content, streams the object in chunks, probes an open session
/// and reads the processing state of videos.
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
/// The `channels.list` and `videos.list` calls run through the named handler chain
/// <see cref="ApiHttpClientName"/> the host configures: the SDK builds its own <c>ConfigurableHttpClient</c> from a
/// raw handler, so the handler — not a <c>HttpClient</c> the SDK would own — is what this class asks
/// <c>IHttpMessageHandlerFactory</c> for (Google's `Initializer.HttpClientFactory` cannot take a
/// `System.Net.Http.IHttpClientFactory` client).
/// </para>
/// <para>
/// Session create, chunk and probe speak the documented resumable protocol on the named client
/// <see cref="UploadHttpClientName"/> (06.2.3-T1 protocol decision 5): the SDK's <c>ResumableUpload</c> does not
/// give exact control of <c>Content-Range</c>, the per-chunk retry budget or the drop-after-the-last-chunk case,
/// so those three members are raw HTTP. That client carries <em>no</em> shared timeout — each call arms its own
/// linked <see cref="CancellationTokenSource"/> from <see cref="YouTubeUploadOptions"/> — and follows no redirect.
/// The bearer travels in the <c>Authorization</c> header of every request, never in a URL, and the session URI is
/// accepted only on the configured upload host (SSRF rule 4).
/// </para>
/// <para>
/// Nothing here logs a body, a token, a code, a scope, a title, a description, an exception message or a session
/// URI: a line carries the operation name, the HTTP status, the sanitised Google reason, elapsed milliseconds and
/// byte counts only (README D17).
/// </para>
/// </remarks>
/// <param name="httpClients">The factory of the named clients that carry this class's HTTP calls.</param>
/// <param name="apiHandlers">The factory of the named handler chain the Google Data API client transports through.</param>
/// <param name="options">The bound <c>YouTube</c> section: endpoints, scopes, the HTTP timeout and the upload tuning.</param>
/// <param name="clock">The UTC clock the token expiry is computed on.</param>
/// <param name="logger">The logger of the operations below.</param>
public sealed partial class GoogleYouTubeClient(
    System.Net.Http.IHttpClientFactory httpClients,
    IHttpMessageHandlerFactory apiHandlers,
    IOptions<YouTubeOptions> options,
    IClock clock,
    ILogger<GoogleYouTubeClient> logger) : IYouTubeClient
{
    /// <summary>The named client the token, refresh and revoke form posts use (timeout <c>YouTube:Http:TimeoutSeconds</c>).</summary>
    public const string OAuthHttpClientName = "youtube-oauth";

    /// <summary>The named handler chain the `Google.Apis.YouTube.v3` client transports through.</summary>
    public const string ApiHttpClientName = "youtube-api";

    /// <summary>
    /// The named client the session-create, chunk and probe requests use (06.2.3-T1): no shared timeout — each
    /// call arms its own deadline from <see cref="YouTubeUploadOptions"/> — a bounded response body and no
    /// automatic redirect, so a session URI can never be replayed to another host.
    /// </summary>
    public const string UploadHttpClientName = "youtube-upload";

    /// <summary>
    /// The largest response body either client buffers. A token answer, a revocation answer and a `channels.list`
    /// payload are each a few hundred bytes, so the bound never bites a working flow; it keeps a compromised or
    /// misconfigured endpoint from making a callback buffer an arbitrary body inside the timeout. Applied to both
    /// clients: the named OAuth client in the composition root and the SDK's own <c>ConfigurableHttpClient</c> here.
    /// </summary>
    public const int MaxResponseContentBytes = 64 * 1024;

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

        return await ExchangeTokensAsync(form, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The failures are the upload half's typed ones (06.2.3-T1): a dead grant is
    /// <see cref="YouTubeErrorCodes.ReauthRequired"/>, a refused OAuth client
    /// <see cref="YouTubeErrorCodes.ClientBroken"/>, and an outage, a timeout or an unreadable answer
    /// <see cref="YouTubeErrorCodes.Transport"/>; the code exchange keeps its own
    /// <c>oauth_exchange_failed</c>/<c>service_unavailable</c> answer because the connect callback maps those.
    /// </remarks>
    public Task<ErrorOr<OAuthTokens>> RefreshAsync(OAuthClient client, string refreshToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        return RefreshTokensAsync(
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

        var attempt = await PostFormAsync(OperationRevoke, options.Value.RevokeEndpoint, content, ct);
        if (attempt.Status is null)
        {
            // No answer at all: the same `service_unavailable` the revoke has always answered.
            return ProviderUnavailable(OperationRevoke);
        }

        var status = attempt.Status.Value;

        if (status == HttpStatusCode.OK)
        {
            return RevokeOutcome.Revoked;
        }

        // Google answers `invalid_token` for a token it no longer knows: the caller asked for the token to be gone
        // and it is, so the outcome is the idempotent one rather than a failure (06.4.1-T1 retries this path).
        if ((int)status is >= 400 and < 500
            && string.Equals(ReasonOf(attempt.Body), "invalid_token", StringComparison.Ordinal))
        {
            return RevokeOutcome.AlreadyRevoked;
        }

        return IsServerFailure(status)
            ? ProviderUnavailable(OperationRevoke)
            : ProviderRefused(OperationRevoke);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<GoogleChannel>> GetMyChannelAsync(string accessToken, CancellationToken ct)
    {
        var o = options.Value;

        try
        {
            using var service = CreateService(o, accessToken);

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

    /// <inheritdoc />
    public async Task<ErrorOr<Uri>> StartUploadAsync(
        string accessToken,
        VideoInsertRequest request,
        long length,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        // The caller's total is the authority (`assets.size_bytes`, CR-18) and the record's own length is the
        // cross-check it already made: a disagreement is refused before any Google call, never sent.
        if (length <= 0 || request.Length != length)
        {
            return UploadRefusedBeforeTheCall();
        }

        if (!TryGetUploadBaseUri(out var uploadBase))
        {
            return UploadRefusedBeforeTheCall();
        }

        var upload = options.Value.Upload;
        var endpoint = new Uri(uploadBase, SessionCreatePath);
        var body = BuildVideoInsertJson(request);

        var response = await SendUploadAsync(
            () =>
            {
                var message = NewUploadRequest(HttpMethod.Post, endpoint, accessToken);
                var json = new ByteArrayContent(body);
                json.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = Encoding.UTF8.WebName,
                };

                message.Content = json;

                // A content-less session create carries the object's size and type in Google's own headers; the
                // `Content-Range: bytes 0-0/<total>` the task file sketched belongs to a request WITH content and
                // is not valid here (deviation recorded in the hand-over).
                message.Headers.TryAddWithoutValidation("X-Upload-Content-Type", request.MimeType);
                message.Headers.TryAddWithoutValidation(
                    "X-Upload-Content-Length",
                    length.ToString(CultureInfo.InvariantCulture));

                return message;
            },
            TimeoutOf(upload.StartTimeoutSeconds),
            OperationStart,
            bytes: 0,
            ct);

        if (response.IsError)
        {
            return response.Errors;
        }

        var answer = response.Value;

        if (!IsSuccess(answer.Status))
        {
            return MapStatusFailure(answer.Status, answer.Reason, sessionScoped: false);
        }

        var location = answer.Location;

        // SSRF (rule 4): the session URI is taken from the answer only when it is absolute and lives on the
        // configured upload host; the bearer must never be sent anywhere else.
        if (location is null || !location.IsAbsoluteUri || !IsAllowedSessionUri(location, uploadBase))
        {
            return YouTubeError(YouTubeErrorCodes.Transport, answer.Reason, (int)answer.Status, isTransport: true);
        }

        return location;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<UploadResult>> ResumeUploadAsync(
        string accessToken,
        Uri sessionUri,
        Stream content,
        long length,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessionUri);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        if (length <= 0)
        {
            return UploadRefusedBeforeTheCall();
        }

        if (!TryGetUploadBaseUri(out var uploadBase) || !IsAllowedSessionUri(sessionUri, uploadBase))
        {
            // The caller persisted and decrypted this URI: an off-host value means tampering, so the request is
            // refused without sending the bearer anywhere (rule 4).
            return UploadRefusedBeforeTheCall();
        }

        var resume = await ResolveResumeStartAsync(accessToken, sessionUri, length, content, ct);
        if (resume.IsError)
        {
            return resume.Errors;
        }

        if (resume.Value.VideoId is { Length: > 0 } completedVideoId)
        {
            return new UploadResult(completedVideoId);
        }

        var upload = options.Value.Upload;

        // The validator refuses a zero or non-multiple chunk and a retry count outside 1..10, so these clamps never
        // bite a valid section; they keep a bad one from hanging a job (a zero chunk would loop without progress).
        var chunkSize = Math.Max(1, upload.ChunkSizeBytes);
        var maxRetries = Math.Max(0, upload.MaxChunkRetries);
        var chunkTimeout = TimeoutOf(upload.ChunkTimeoutSeconds);

        // ONE rented buffer for the whole call, sized to the largest chunk this upload can need. The slice below is
        // always the chunk length, never the pooled array's possibly larger size, and the stream is never asked for
        // more than ChunkSizeBytes at once. Returned in `finally`.
        var bufferBytes = checked((int)Math.Min((long)chunkSize, length - resume.Value.Offset));
        var buffer = ArrayPool<byte>.Shared.Rent(bufferBytes);

        try
        {
            var sent = resume.Value.Offset;
            var reported = 0L;

            ReportProgress(progress, sent, ref reported);

            while (sent < length)
            {
                var remaining = length - sent;
                var chunkLength = remaining >= chunkSize ? chunkSize : checked((int)remaining);

                var filled = await FillAsync(content, buffer, chunkLength, ct);
                if (filled.IsError)
                {
                    return filled.Errors;
                }

                if (filled.Value < chunkLength)
                {
                    // The stored object is shorter than the length the caller declared: the row and the object
                    // disagree and no retry can fix it, so the answer is the one the job treats as unrecoverable.
                    return YouTubeError(YouTubeErrorCodes.Transport, null, null, isTransport: true);
                }

                var chunkEnd = sent + chunkLength - 1;
                var resendFrom = sent;
                var attempt = 0;

                while (true)
                {
                    var from = resendFrom;
                    var to = chunkEnd;
                    var offset = checked((int)(from - sent));
                    var count = checked((int)(to - from + 1));

                    var response = await SendUploadAsync(
                        () => ChunkRequest(sessionUri, accessToken, buffer, offset, count, from, to, length),
                        chunkTimeout,
                        OperationResume,
                        count,
                        ct);

                    if (response.IsError)
                    {
                        if (attempt >= maxRetries)
                        {
                            return response.Errors;
                        }

                        await RetryAsync(OperationResume, attempt, ct);
                        attempt++;

                        continue;
                    }

                    var answer = response.Value;

                    if (IsSuccess(answer.Status))
                    {
                        if (to != length - 1)
                        {
                            // A completion that does not cover the last byte cannot be trusted: no video id is
                            // ever reported for a partial object.
                            return YouTubeError(
                                YouTubeErrorCodes.Transport, answer.Reason, (int)answer.Status, isTransport: true);
                        }

                        var videoId = VideoIdOf(answer.Body);
                        if (videoId is not { Length: > 0 })
                        {
                            return YouTubeError(
                                YouTubeErrorCodes.Transport, answer.Reason, (int)answer.Status, isTransport: true);
                        }

                        // The terminal 2xx confirms every byte of the object, so the last chunk's bytes reach the
                        // caller's progress before the result: a consumer must not see a finished upload below 100 %.
                        ReportProgress(progress, length, ref reported);

                        return new UploadResult(videoId);
                    }

                    if ((int)answer.Status == ResumeIncompleteStatus)
                    {
                        if (!TryParseResumeRange(answer.Range, length, out var next))
                        {
                            return YouTubeError(
                                YouTubeErrorCodes.Transport, answer.Reason, (int)answer.Status, isTransport: true);
                        }

                        if (next == chunkEnd + 1)
                        {
                            sent = next;
                            ReportProgress(progress, sent, ref reported);

                            if (sent == length)
                            {
                                // Every byte arrived but the answer carried no video id (the F02 lost-response
                                // shape): ask the session once. A 308 here is never a success.
                                var probed = await ProbeUploadAsync(accessToken, sessionUri, length, ct);

                                return !probed.IsError && probed.Value is UploadProbe.Complete complete
                                    ? new UploadResult(complete.VideoId)
                                    : YouTubeError(YouTubeErrorCodes.Transport, null, null, isTransport: true);
                            }

                            break;
                        }

                        if (next >= sent && next <= chunkEnd)
                        {
                            // The session holds only part of what this request carried: resend the remainder of
                            // the SAME buffer, without touching the source stream again.
                            ReportProgress(progress, next, ref reported);

                            if (next > resendFrom)
                            {
                                resendFrom = next;
                                attempt = 0;

                                continue;
                            }

                            // The session reported bytes it already reported: a failed attempt, so the retry
                            // budget bounds the loop.
                            if (attempt >= maxRetries)
                            {
                                return YouTubeError(
                                    YouTubeErrorCodes.Transport,
                                    answer.Reason,
                                    (int)answer.Status,
                                    isTransport: true);
                            }

                            await RetryAsync(OperationResume, attempt, ct);
                            attempt++;

                            continue;
                        }

                        // The session forgot bytes this run sent, or holds bytes this run never sent: neither is
                        // recoverable in this session.
                        return YouTubeError(
                            YouTubeErrorCodes.Transport, answer.Reason, (int)answer.Status, isTransport: true);
                    }

                    if (IsRetryable(answer.Status) && attempt < maxRetries)
                    {
                        await RetryAsync(OperationResume, attempt, ct);
                        attempt++;

                        continue;
                    }

                    return MapStatusFailure(answer.Status, answer.Reason, sessionScoped: true);
                }
            }

            // Unreachable: the loop returns on the completion answer that covers the last byte.
            return YouTubeError(YouTubeErrorCodes.Transport, null, null, isTransport: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<UploadProbe>> ProbeUploadAsync(
        string accessToken,
        Uri sessionUri,
        long length,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessionUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        if (length <= 0)
        {
            return UploadRefusedBeforeTheCall();
        }

        if (!TryGetUploadBaseUri(out var uploadBase) || !IsAllowedSessionUri(sessionUri, uploadBase))
        {
            return UploadRefusedBeforeTheCall();
        }

        var response = await SendUploadAsync(
            () =>
            {
                var message = NewUploadRequest(HttpMethod.Put, sessionUri, accessToken);
                var empty = new ByteArrayContent(Array.Empty<byte>());
                empty.Headers.ContentRange = new ContentRangeHeaderValue(length);
                message.Content = empty;

                return message;
            },
            TimeoutOf(options.Value.Upload.ProbeTimeoutSeconds),
            OperationProbe,
            bytes: 0,
            ct);

        if (response.IsError)
        {
            return response.Errors;
        }

        var answer = response.Value;

        if ((int)answer.Status == ResumeIncompleteStatus)
        {
            return TryParseResumeRange(answer.Range, length, out var next)
                ? new UploadProbe.Partial(next)
                : YouTubeError(YouTubeErrorCodes.Transport, answer.Reason, (int)answer.Status, isTransport: true);
        }

        if (IsSuccess(answer.Status))
        {
            return VideoIdOf(answer.Body) is { Length: > 0 } videoId
                ? new UploadProbe.Complete(videoId)
                : YouTubeError(YouTubeErrorCodes.Transport, answer.Reason, (int)answer.Status, isTransport: true);
        }

        if (answer.Status == HttpStatusCode.NotFound)
        {
            return new UploadProbe.Expired();
        }

        return MapStatusFailure(answer.Status, answer.Reason, sessionScoped: true);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<IReadOnlyList<YouTubeVideoStatus>>> GetVideosAsync(
        string accessToken,
        IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        if (ids.Count == 0 || ids.Count > MaxVideoIdsPerCall || ids.Any(id => !IsWellFormedVideoId(id)))
        {
            // The caller batches (D18) and every id is a YouTube id: one it did not send is absent from the
            // answer, and an id that is not a YouTube id is refused without a Google call rather than escaped
            // into a query whose shape this class does not control.
            return YouTubeError(YouTubeErrorCodes.UploadFailed, null, null, isTransport: false);
        }

        try
        {
            using var service = CreateService(options.Value, accessToken);

            var request = service.Videos.List("status");
            request.Id = new Google.Apis.Util.Repeatable<string>(ids);

            var response = await request.ExecuteAsync(ct);
            LogApiResponse(logger, OperationVideos, (int)HttpStatusCode.OK);

            var items = response.Items;
            var statuses = new List<YouTubeVideoStatus>(items?.Count ?? 0);

            if (items is not null)
            {
                foreach (var item in items)
                {
                    if (item.Id is not { Length: > 0 } videoId || item.Status is null)
                    {
                        continue;
                    }

                    statuses.Add(new YouTubeVideoStatus(
                        videoId,
                        item.Status.UploadStatus ?? string.Empty,
                        item.Status.FailureReason,
                        item.Status.RejectionReason,
                        item.Status.PrivacyStatus));
                }
            }

            return statuses;
        }
        catch (GoogleApiException ex)
        {
            LogApiResponse(logger, OperationVideos, (int)ex.HttpStatusCode);

            return MapStatusFailure(ex.HttpStatusCode, FirstReasonOf(ex), sessionScoped: false);
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            LogApiUnreachable(logger, OperationVideos, ex.GetType().Name);

            return YouTubeError(YouTubeErrorCodes.Transport, null, null, isTransport: true);
        }
    }

    /// <summary>
    /// Builds the one `Google.Apis.YouTube.v3` service a call uses (the channel read and `videos.list`). The
    /// service owns the wrapper it was built from, not the pooled handler: the factory keeps one handler chain per
    /// host, and the per-call credential initializer carries this call's bearer token — supplied as a string, so
    /// nothing is read from storage here.
    /// </summary>
    /// <param name="o">The bound section: the API root and the HTTP timeout.</param>
    /// <param name="accessToken">The bearer token of this call; never logged.</param>
    private YouTubeService CreateService(YouTubeOptions o, string accessToken) => new(new BaseClientService.Initializer
    {
        ApplicationName = ApplicationName,
        BaseUri = o.ApiBaseUrl,
        HttpClientFactory = new ApiHttpClientFactory(apiHandlers, TimeSpan.FromSeconds(o.Http.TimeoutSeconds)),
        HttpClientInitializer = GoogleCredential.FromAccessToken(accessToken),
        // A 503 must reach the caller as one failed attempt: the consumer reports a failure instead of silently
        // replaying a request whose answer the SDK would swallow behind a back-off.
        DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
    });

    /// <summary>
    /// Determines the byte the first chunk starts at. A seekable stream answers for itself — the caller positioned
    /// it with <c>UploadStreamPositioning.EnsureAtAsync</c> — while a stream that cannot seek (an S3 response body)
    /// carries no position this class could read, so the session is asked once with an empty probe, which is also
    /// how a session that already holds every byte answers its video id.
    /// </summary>
    private async Task<ErrorOr<ResumeStart>> ResolveResumeStartAsync(
        string accessToken,
        Uri sessionUri,
        long length,
        Stream content,
        CancellationToken ct)
    {
        if (content.CanSeek && content.Position < length)
        {
            return new ResumeStart(content.Position, null);
        }

        var probe = await ProbeUploadAsync(accessToken, sessionUri, length, ct);
        if (probe.IsError)
        {
            return probe.Errors;
        }

        switch (probe.Value)
        {
            case UploadProbe.Complete complete:
                return new ResumeStart(0, complete.VideoId);
            case UploadProbe.Partial held when held.NextByte < length:
                return new ResumeStart(held.NextByte, null);
            case UploadProbe.Expired:
                return SessionExpiredFailure();
            default:
                return YouTubeError(YouTubeErrorCodes.Transport, null, null, isTransport: true);
        }
    }

    /// <summary>Fills <paramref name="count"/> bytes of the buffer, one read at a time, never past the chunk.</summary>
    private static async Task<ErrorOr<int>> FillAsync(Stream content, byte[] buffer, int count, CancellationToken ct)
    {
        var filled = 0;

        try
        {
            while (filled < count)
            {
                var read = await content.ReadAsync(buffer.AsMemory(filled, count - filled), ct);
                if (read == 0)
                {
                    break;
                }

                filled += read;
            }
        }
        catch (IOException)
        {
            // A storage stream that fails mid-read is a transport answer for the caller's retry policy.
            return YouTubeError(YouTubeErrorCodes.Transport, null, null, isTransport: true);
        }

        return filled;
    }

    /// <summary>One chunk request: a fresh message (never a reused one) over the SAME rented buffer.</summary>
    private static HttpRequestMessage ChunkRequest(
        Uri sessionUri,
        string accessToken,
        byte[] buffer,
        int offset,
        int count,
        long from,
        long to,
        long total)
    {
        var message = NewUploadRequest(HttpMethod.Put, sessionUri, accessToken);
        var chunk = new ByteArrayContent(buffer, offset, count);
        chunk.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
        message.Content = chunk;

        return message;
    }

    /// <summary>A fresh request carrying this call's bearer in the header only — never in the URI.</summary>
    private static HttpRequestMessage NewUploadRequest(HttpMethod method, Uri endpoint, string accessToken)
    {
        var message = new HttpRequestMessage(method, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return message;
    }

    /// <summary>
    /// Parses Google's <c>Range</c> answer of a <c>308</c>: <c>bytes=0-N</c> means the session holds
    /// <c>0 .. N</c>, so the next byte is <c>N + 1</c>; a missing header means it holds nothing. Anything else —
    /// a malformed value, a second range, or an end that is not inside the object — is not usable.
    /// </summary>
    private static bool TryParseResumeRange(string? range, long total, out long next)
    {
        next = 0;

        if (range is null)
        {
            return true;
        }

        var match = ResumeRangeRegex().Match(range);

        if (!match.Success
            || !long.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            || last >= total)
        {
            return false;
        }

        next = last + 1;

        return true;
    }

    /// <summary>
    /// The typed answer of one upload or `videos.list` status (06.2.3-T1 error model). A session-scoped 404 means
    /// the session is gone; elsewhere it is a refusal of the request.
    /// </summary>
    private static Error MapStatusFailure(HttpStatusCode status, string? reason, bool sessionScoped)
    {
        var code = (int)status;

        if (status == HttpStatusCode.Forbidden
            && (Is(reason, "quotaExceeded") || Is(reason, "uploadLimitExceeded")))
        {
            return YouTubeError(YouTubeErrorCodes.QuotaExceeded, reason, code, isTransport: false);
        }

        if (status == HttpStatusCode.Unauthorized)
        {
            // A plain 401 stays transport: the job retries once instead of marking the connection expired on an
            // answer that never named a dead grant.
            var reauth = Is(reason, "invalid_grant")
                || Is(reason, "unauthorized")
                || Is(reason, "authError")
                || Is(reason, "invalid_token");

            return YouTubeError(
                reauth ? YouTubeErrorCodes.ReauthRequired : YouTubeErrorCodes.Transport,
                reason,
                code,
                isTransport: !reauth);
        }

        if (status == HttpStatusCode.NotFound && sessionScoped)
        {
            return YouTubeError(YouTubeErrorCodes.SessionExpired, reason, code, isTransport: false);
        }

        if (code >= 500 || code == 429)
        {
            return YouTubeError(YouTubeErrorCodes.Transport, reason, code, isTransport: true);
        }

        if (code is >= 400 and < 500)
        {
            return YouTubeError(YouTubeErrorCodes.UploadFailed, reason, code, isTransport: false);
        }

        return YouTubeError(YouTubeErrorCodes.Transport, reason, code, isTransport: true);
    }

    /// <summary>Sends one upload request under its own deadline and hands the status, headers and bounded body back.</summary>
    private async Task<ErrorOr<UploadResponse>> SendUploadAsync(
        Func<HttpRequestMessage> createRequest,
        TimeSpan timeout,
        string operation,
        long bytes,
        CancellationToken ct)
    {
        // The named client carries no shared timeout, so this linked source is the only deadline and it also
        // honours the caller's token. A caller-driven cancellation is rethrown, never mapped.
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        var started = Stopwatch.GetTimestamp();

        try
        {
            using var request = createRequest();
            using var response = await httpClients
                .CreateClient(UploadHttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);

            var body = await ReadBoundedBodyAsync(response.Content, linked.Token);
            var reason = ReasonOf(body);

            var elapsedMs = ElapsedMs(started);

            LogUploadResponse(logger, operation, (int)response.StatusCode, reason, elapsedMs, bytes);

            var range = response.Headers.TryGetValues(RangeHeaderName, out var values)
                ? string.Join(',', values)
                : null;

            return new UploadResponse(response.StatusCode, response.Headers.Location, range, body, reason);
        }
        catch (Exception ex) when ((ex is HttpRequestException or IOException or OperationCanceledException)
            && !ct.IsCancellationRequested)
        {
            // A timeout surfaces as an OperationCanceledException; only the caller's own token is rethrown. The
            // type name is the only thing about the exception that may travel: a message can carry a URL.
            var elapsedMs = ElapsedMs(started);

            LogUploadUnreachable(logger, operation, ex.GetType().Name, elapsedMs);

            return YouTubeError(YouTubeErrorCodes.Transport, null, null, isTransport: true);
        }
    }

    /// <summary>
    /// Reads a response body up to <see cref="MaxResponseContentBytes"/>; <c>null</c> means it was larger, which is
    /// already an unusable answer. Nothing unbounded is buffered whatever the peer sends.
    /// </summary>
    private static async Task<string?> ReadBoundedBodyAsync(HttpContent? content, CancellationToken ct)
    {
        if (content is null)
        {
            return string.Empty;
        }

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(BodyReadBufferBytes);

        try
        {
            long total = 0;
            int read;

            while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct)) > 0)
            {
                total += read;

                if (total > MaxResponseContentBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Google's machine <c>reason</c> from an error body (never logged raw, never described): the data API's
    /// <c>error.errors[0].reason</c>, or the OAuth endpoints' <c>error</c> string. <c>null</c> for anything else.
    /// </summary>
    private static string? ReasonOf(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error))
            {
                return null;
            }

            if (error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }

            return error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0
                && errors[0].ValueKind == JsonValueKind.Object
                && errors[0].TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null;
        }
        catch (JsonException)
        {
            // A body that is not JSON has already answered through its status; the classification does not need it.
            return null;
        }
    }

    /// <summary>The <c>id</c> member of a completed upload's answer, or <c>null</c> when it carries none.</summary>
    private static string? VideoIdOf(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The <c>videos.insert</c> metadata of a session create (06 README G3/G5) written with
    /// <see cref="Utf8JsonWriter"/>, so the wire names are exact and <c>publishAt</c> is omitted when null. The
    /// title and description are user content: they travel in this body and nowhere else.
    /// </summary>
    private static byte[] BuildVideoInsertJson(VideoInsertRequest request)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("snippet");
            writer.WriteString("title", request.Title);
            writer.WriteString("description", request.Description);
            writer.WriteStartArray("tags");

            foreach (var tag in request.Tags)
            {
                writer.WriteStringValue(tag);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("status");
            writer.WriteString("privacyStatus", request.PrivacyStatus);

            if (request.PublishAt is { } publishAt)
            {
                writer.WriteString(
                    "publishAt",
                    publishAt.ToUniversalTime().ToString(PublishAtFormat, CultureInfo.InvariantCulture));
            }

            writer.WriteBoolean("containsSyntheticMedia", request.ContainsSyntheticMedia);
            writer.WriteBoolean("selfDeclaredMadeForKids", request.SelfDeclaredMadeForKids);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The configured upload root as an absolute <c>http(s)</c> URI with a trailing slash and no user-info, query
    /// or fragment. A value that is anything else is a configuration problem, so the member refuses instead of
    /// sending the bearer to an address it cannot vouch for.
    /// </summary>
    private bool TryGetUploadBaseUri(out Uri uploadBase)
    {
        uploadBase = null!;

        if (!Uri.TryCreate(options.Value.UploadBaseUrl, UriKind.Absolute, out var parsed)
            || parsed.UserInfo.Length > 0
            || parsed.Query.Length > 0
            || parsed.Fragment.Length > 0
            || !(string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        uploadBase = parsed.AbsolutePath.EndsWith('/')
            ? parsed
            : new Uri(parsed.AbsoluteUri + "/", UriKind.Absolute);

        return true;
    }

    /// <summary>
    /// Whether <paramref name="sessionUri"/> may receive this call's bearer: absolute, no user-info, and exactly
    /// the configured upload host's scheme, host and port. The session URI is opaque otherwise — its shape is
    /// Google's to choose.
    /// </summary>
    private static bool IsAllowedSessionUri(Uri sessionUri, Uri uploadBase) =>
        sessionUri.IsAbsoluteUri
        && sessionUri.UserInfo.Length == 0
        && string.Equals(sessionUri.Scheme, uploadBase.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(sessionUri.Host, uploadBase.Host, StringComparison.OrdinalIgnoreCase)
        && sessionUri.Port == uploadBase.Port;

    /// <summary>Reports a confirmed byte count once, and only forward: the caller's progress never moves back.</summary>
    private static void ReportProgress(IProgress<long>? progress, long confirmed, ref long reported)
    {
        if (progress is null || confirmed <= reported)
        {
            return;
        }

        reported = confirmed;
        progress.Report(confirmed);
    }

    /// <summary>Waits before re-sending a chunk: 250 ms doubling per attempt, cancellable by the caller.</summary>
    private async Task RetryAsync(string operation, int attempt, CancellationToken ct)
    {
        LogUploadRetry(logger, operation, attempt + 1);

        await Task.Delay(
            TimeSpan.FromMilliseconds(ChunkRetryBaseDelayMs << Math.Min(attempt, 8)),
            ct);
    }

    private static bool IsRetryable(HttpStatusCode status) => (int)status is 429 or >= 500;

    /// <summary>Whether <paramref name="reason"/> is exactly <paramref name="value"/> (Google's own casing).</summary>
    private static bool Is(string? reason, string value) => string.Equals(reason, value, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="id"/> can be one query's id: a non-empty run of YouTube-id characters. Anything
    /// else is refused before a Google call instead of being escaped into a query this class does not build.
    /// </summary>
    private static bool IsWellFormedVideoId(string? id)
    {
        if (id is not { Length: > 0 and <= 64 })
        {
            return false;
        }

        foreach (var character in id)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The first machine reason of an SDK error, or <c>null</c> (never the message).</summary>
    private static string? FirstReasonOf(GoogleApiException ex) =>
        ex.Error?.Errors is { Count: > 0 } errors && errors[0].Reason is { Length: > 0 } reason ? reason : null;

    private static long ElapsedMs(long startedTimestamp) =>
        (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

    /// <summary>The per-call deadline of one upload request, bounded so no configured value can throw or hang.</summary>
    private static TimeSpan TimeoutOf(int seconds) =>
        TimeSpan.FromSeconds(Math.Clamp(seconds, 1, MaxUploadTimeoutSeconds));

    /// <summary>
    /// One typed YouTube error: the internal code, a fixed description that names nothing the call handled, and
    /// the sanitised <see cref="YouTubeFailure"/> metadata the classifier reads (06.2.3-T3).
    /// </summary>
    private static Error YouTubeError(string code, string? reason, int? httpStatus, bool isTransport) =>
        Error.Failure(
            code,
            DescriptionOf(code),
            new YouTubeFailure(YouTubeFailure.SanitizeReason(reason), httpStatus, isTransport).ToMetadata());

    /// <summary>The one description per code; none of them can carry a value this client was given.</summary>
    private static string DescriptionOf(string code) => code switch
    {
        YouTubeErrorCodes.QuotaExceeded => "Google's YouTube upload quota or upload limit is exhausted.",
        YouTubeErrorCodes.ReauthRequired => "The YouTube grant is no longer usable; the channel must be reconnected.",
        YouTubeErrorCodes.ClientBroken => "The configured YouTube OAuth client was refused.",
        YouTubeErrorCodes.SessionExpired => "The YouTube upload session no longer exists.",
        YouTubeErrorCodes.UploadFailed => "Google refused the YouTube upload request.",
        YouTubeErrorCodes.Transport => "The YouTube request received no usable answer.",
        _ => "The YouTube request failed.",
    };

    /// <summary>
    /// The upload half's answer when the call must not reach Google — a length disagreement, an unusable upload
    /// root, or a session URI off the configured host: <c>youtube_upload_failed</c> with no HTTP status.
    /// </summary>
    private static Error UploadRefusedBeforeTheCall() =>
        YouTubeError(YouTubeErrorCodes.UploadFailed, null, null, isTransport: false);

    private static Error SessionExpiredFailure() =>
        YouTubeError(YouTubeErrorCodes.SessionExpired, null, null, isTransport: false);

    /// <summary>
    /// Exchanges an authorization code through the token endpoint. The code, the client secret and the refresh
    /// token travel in the form body only; a response never survives in a log line. The returned scope is Google's
    /// granted <c>scope</c> verbatim — empty when the answer carried none — so a caller can require the scopes a
    /// connect needs instead of assuming the requested ones were granted. The failure codes are this member's own
    /// (06.1.2-T1): the connect callback maps <c>oauth_exchange_failed</c>/<c>service_unavailable</c>.
    /// </summary>
    private async Task<ErrorOr<OAuthTokens>> ExchangeTokensAsync(
        List<KeyValuePair<string, string>> form,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);

        var attempt = await PostFormAsync(OperationExchange, options.Value.TokenEndpoint, content, ct);
        if (attempt.Status is null)
        {
            return ProviderUnavailable(OperationExchange);
        }

        var status = attempt.Status.Value;

        if (!IsSuccess(status))
        {
            return IsServerFailure(status)
                ? ProviderUnavailable(OperationExchange)
                : ProviderRefused(OperationExchange);
        }

        var tokens = ParseTokens(attempt.Body);
        if (tokens is null)
        {
            return ProviderRefused(OperationExchange);
        }

        return tokens;
    }

    /// <summary>
    /// Refreshes through the token endpoint with the upload half's typed failures (06.2.3-T1): a body
    /// <c>{"error":"invalid_grant"}</c> is <see cref="YouTubeErrorCodes.ReauthRequired"/>,
    /// <c>invalid_client</c>/<c>unauthorized_client</c> is <see cref="YouTubeErrorCodes.ClientBroken"/>, and every
    /// other non-2xx, an unreadable answer, a 5xx or a timeout is <see cref="YouTubeErrorCodes.Transport"/>.
    /// </summary>
    private async Task<ErrorOr<OAuthTokens>> RefreshTokensAsync(
        List<KeyValuePair<string, string>> form,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);

        var attempt = await PostFormAsync(OperationRefresh, options.Value.TokenEndpoint, content, ct);
        if (attempt.Status is null)
        {
            return YouTubeError(YouTubeErrorCodes.Transport, null, null, isTransport: true);
        }

        var status = attempt.Status.Value;

        if (IsSuccess(status))
        {
            return ParseTokens(attempt.Body) is { } tokens
                ? tokens
                : YouTubeError(YouTubeErrorCodes.Transport, null, (int)status, isTransport: true);
        }

        var reason = ReasonOf(attempt.Body);

        if (IsServerFailure(status))
        {
            return YouTubeError(YouTubeErrorCodes.Transport, reason, (int)status, isTransport: true);
        }

        if (Is(reason, "invalid_grant"))
        {
            return YouTubeError(YouTubeErrorCodes.ReauthRequired, reason, (int)status, isTransport: false);
        }

        if (Is(reason, "invalid_client") || Is(reason, "unauthorized_client"))
        {
            return YouTubeError(YouTubeErrorCodes.ClientBroken, reason, (int)status, isTransport: false);
        }

        return YouTubeError(YouTubeErrorCodes.Transport, reason, (int)status, isTransport: true);
    }

    /// <summary>The tokens of an RFC 6749 §5.1 answer, or <c>null</c> when the body carries no usable token.</summary>
    private OAuthTokens? ParseTokens(string body)
    {
        TokenPayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload?.AccessToken is not { Length: > 0 } accessToken)
        {
            return null;
        }

        var lifetime = payload.ExpiresInSeconds is > 0 ? payload.ExpiresInSeconds.Value : DefaultTokenLifetimeSeconds;

        return new OAuthTokens(
            accessToken,
            payload.RefreshToken,
            clock.UtcNow.AddSeconds(lifetime),
            payload.Scope ?? string.Empty);
    }

    /// <summary>
    /// Posts one form and hands the status and the body back — with a <c>null</c> status when no answer arrived —
    /// so each caller maps its own failure codes. The body of a failure is read only to tell apart the responses
    /// that need it (revocation, a typed refresh) and is never logged or described. A caller-driven cancellation is
    /// rethrown, never mapped.
    /// </summary>
    private async Task<FormAttempt> PostFormAsync(
        string operation,
        string endpoint,
        HttpContent content,
        CancellationToken ct)
    {
        try
        {
            using var response = await httpClients
                .CreateClient(OAuthHttpClientName)
                .PostAsync(endpoint, content, ct);

            LogOAuthResponse(logger, operation, (int)response.StatusCode);

            return new FormAttempt(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            // A timeout surfaces as an OperationCanceledException; only the caller's own token is rethrown.
            LogOAuthUnreachable(logger, operation, ex.GetType().Name);

            return new FormAttempt(null, string.Empty);
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
    /// pooled, gzip), the configured timeout and the same bounded response body as the OAuth client
    /// (<see cref="MaxResponseContentBytes"/>) — one chain for every token, and no <c>HttpClient</c> of its own.
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
                MaxResponseContentBufferSize = MaxResponseContentBytes,
            };

            foreach (var initializer in args.Initializers)
            {
                initializer.Initialize(client);
            }

            return client;
        }
    }

    /// <summary>The status and the body of one form post; a <c>null</c> status means no answer arrived.</summary>
    /// <param name="Status">The HTTP status, or null when the endpoint was unreachable or timed out.</param>
    /// <param name="Body">The response body, parsed by the caller that needs a member of it and never logged.</param>
    private sealed record FormAttempt(HttpStatusCode? Status, string Body);

    /// <summary>
    /// One upload request's answer, copied out of the response before it is disposed: the status, the
    /// <c>Location</c> of a session create, the <c>Range</c> of a <c>308</c>, the bounded body, and the sanitised
    /// reason parsed from it for the log line.
    /// </summary>
    private sealed record UploadResponse(
        HttpStatusCode Status,
        Uri? Location,
        string? Range,
        string? Body,
        string? Reason);

    /// <summary>
    /// Where a resume starts: the offset the first chunk begins at, or the video id when the session already holds
    /// every byte (a stream that cannot seek, or one already at the end, learns this from a probe).
    /// </summary>
    private readonly record struct ResumeStart(long Offset, string? VideoId);

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

    /// <summary>The path of the content-less session create, appended to <c>YouTube:UploadBaseUrl</c>.</summary>
    private const string SessionCreatePath = "videos?uploadType=resumable&part=snippet,status";

    /// <summary>The response header Google repeats the session's received range in.</summary>
    private const string RangeHeaderName = "Range";

    /// <summary>Google's <c>308 Resume Incomplete</c> status of a resumable session.</summary>
    private const int ResumeIncompleteStatus = 308;

    /// <summary>The largest id list one <c>videos.list</c> call accepts (D18: one quota unit per call).</summary>
    private const int MaxVideoIdsPerCall = 50;

    /// <summary>The read size of the bounded body reader; the body itself is capped at <see cref="MaxResponseContentBytes"/>.</summary>
    private const int BodyReadBufferBytes = 8 * 1024;

    /// <summary>The first chunk-retry delay; it doubles per attempt (protocol decision 2).</summary>
    private const int ChunkRetryBaseDelayMs = 250;

    /// <summary>The largest per-call upload deadline a configuration value can ask for.</summary>
    private const int MaxUploadTimeoutSeconds = 3600;

    /// <summary>The wire format of <c>status.publishAt</c>: ISO 8601 in UTC, whole seconds.</summary>
    private const string PublishAtFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private const string OperationExchange = "exchange";
    private const string OperationRefresh = "refresh";
    private const string OperationRevoke = "revoke";
    private const string OperationChannels = "channels.list";
    private const string OperationStart = "start";
    private const string OperationResume = "resume";
    private const string OperationProbe = "probe";
    private const string OperationVideos = "videos.list";

    /// <summary>The strict shape of a session's <c>Range</c> header: one range that starts at zero.</summary>
    [GeneratedRegex("^bytes=0-(\\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ResumeRangeRegex();

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

    [LoggerMessage(EventId = 2377, Level = LogLevel.Information, Message = "youtube.upload.response operation={Operation} status={StatusCode} reason={Reason} elapsedMs={ElapsedMs} bytes={Bytes}")]
    private static partial void LogUploadResponse(ILogger logger, string operation, int statusCode, string? reason, long elapsedMs, long bytes);

    [LoggerMessage(EventId = 2378, Level = LogLevel.Warning, Message = "youtube.upload.unreachable operation={Operation} reason={Reason} elapsedMs={ElapsedMs}")]
    private static partial void LogUploadUnreachable(ILogger logger, string operation, string reason, long elapsedMs);

    [LoggerMessage(EventId = 2379, Level = LogLevel.Warning, Message = "youtube.upload.retry operation={Operation} attempt={Attempt}")]
    private static partial void LogUploadRetry(ILogger logger, string operation, int attempt);
}
