using ErrorOr;

namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// Every Google call the YouTube posting feature makes (06 README D9): the authorization half declared
/// here by 06.1.2-T1 (<see cref="BuildAuthorizationUrl"/>, <see cref="ExchangeCodeAsync"/>,
/// <see cref="RefreshAsync"/>, <see cref="RevokeAsync"/>, <see cref="GetMyChannelAsync"/>), the upload
/// half added to this same port by 06.2.3-T1.
/// </summary>
/// <remarks>
/// <para>
/// One port for one external system: no caller declares a second interface for a Google call, and no
/// implementation logs a body, a token, a code or a scope echo (06 README D17).
/// </para>
/// <para>
/// Every member that can fail answers an <see cref="ErrorOr{T}"/> instead of throwing, so a Google
/// outage, a refusal and a malformed answer all become the caller's outcome — for the callback,
/// <c>oauth_exchange_failed</c>. The code exchange is never retried: an authorization code is single use.
/// </para>
/// </remarks>
public interface IYouTubeClient
{
    /// <summary>
    /// Builds the authorization URL of 06 README D2: the two scopes, <c>access_type=offline</c>,
    /// <c>prompt=consent</c>, <c>include_granted_scopes=false</c>, the state, and — when
    /// <see cref="OAuthAuthorizationRequest.Pkce"/> is set — the PKCE S256 challenge.
    /// </summary>
    /// <param name="client">The client the state row is pinned to; supplies <c>client_id</c>.</param>
    /// <param name="request">The redirect URI and the PKCE flag.</param>
    /// <param name="state">The opaque 43-character state minted for this attempt; never logged.</param>
    /// <param name="codeChallenge">
    /// The base64url S256 digest of the verifier, or <see langword="null"/> when the request is not PKCE —
    /// the two PKCE parameters are omitted in that case.
    /// </param>
    /// <returns>The absolute authorization URL to hand to the browser.</returns>
    string BuildAuthorizationUrl(
        OAuthClient client,
        OAuthAuthorizationRequest request,
        string state,
        string? codeChallenge);

    /// <summary>Exchanges an authorization code for tokens (the <c>POST /token</c> of the flow).</summary>
    /// <param name="client">The client the state row pinned — never the active id (F19).</param>
    /// <param name="code">The authorization code; single use and never logged.</param>
    /// <param name="codeVerifier">The PKCE verifier, or <see langword="null"/> for a non-PKCE flow.</param>
    /// <param name="redirectUri">The redirect URI the code was issued for; must repeat it verbatim.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>The tokens, or an error the callback maps to <c>oauth_exchange_failed</c>.</returns>
    Task<ErrorOr<OAuthTokens>> ExchangeCodeAsync(
        OAuthClient client,
        string code,
        string? codeVerifier,
        string redirectUri,
        CancellationToken ct);

    /// <summary>Refreshes an access token (consumed by 06.2.3-T2's upload and 06.4.2-T1's health job).</summary>
    /// <param name="client">The client the connection is pinned to.</param>
    /// <param name="refreshToken">The stored refresh token; never logged.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>The fresh tokens, or an error the caller classifies (401 <c>invalid_grant</c> → expired).</returns>
    Task<ErrorOr<OAuthTokens>> RefreshAsync(OAuthClient client, string refreshToken, CancellationToken ct);

    /// <summary>
    /// Revokes one token at Google (consumed by 06.4.1-T1's disconnect; declared here once).
    /// </summary>
    /// <param name="token">The access or refresh token to revoke; never logged.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>
    /// <see cref="RevokeOutcome.Revoked"/>, <see cref="RevokeOutcome.AlreadyRevoked"/> for a
    /// <c>4xx</c> <c>invalid_token</c> (an idempotent retry), or an error for a transport or server
    /// failure.
    /// </returns>
    Task<ErrorOr<RevokeOutcome>> RevokeAsync(string token, CancellationToken ct);

    /// <summary>
    /// Reads the channel of the account that granted access (<c>channels.list?part=snippet&amp;mine=true</c>).
    /// </summary>
    /// <param name="accessToken">The token of the grant being connected.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>The channel, or an error when the account has none or the call is refused.</returns>
    Task<ErrorOr<GoogleChannel>> GetMyChannelAsync(string accessToken, CancellationToken ct);

    /// <summary>
    /// Creates a resumable upload session <em>without content</em> (06.2.3-T1, G7): one <c>POST</c> that carries the
    /// metadata and the total length and answers the session URI. Never retried by the client: the call costs 1600
    /// quota units, so a replay belongs to the caller's decision.
    /// </summary>
    /// <param name="accessToken">The access token the caller obtained from <c>ITokenService</c>; never read from storage here and never logged.</param>
    /// <param name="request">The snippet and status of the video; <see cref="VideoInsertRequest.Length"/> must equal <paramref name="length"/>.</param>
    /// <param name="length">The object's total size in bytes (<c>assets.size_bytes</c>, CR-18).</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The opaque session URI (Google's <c>Location</c> header, never logged), or a typed YouTube error.</returns>
    Task<ErrorOr<Uri>> StartUploadAsync(string accessToken, VideoInsertRequest request, long length, CancellationToken ct);

    /// <summary>
    /// Streams <paramref name="content"/> to an open session in chunks that are a multiple of 256 KB except the last
    /// (G7), from the stream's <em>current</em> position, which the caller positioned with
    /// <c>UploadStreamPositioning.EnsureAtAsync</c>. The stream is never buffered whole and is not disposed here.
    /// </summary>
    /// <param name="accessToken">The access token; never logged.</param>
    /// <param name="sessionUri">The URI <see cref="StartUploadAsync"/> returned; opaque, never logged.</param>
    /// <param name="content">The object stream, positioned at the resume offset.</param>
    /// <param name="length">The object's total size in bytes.</param>
    /// <param name="progress">Receives the cumulative bytes Google confirmed, monotonically non-decreasing.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The video id once Google answers <c>200/201</c> with one, or a typed error (<c>youtube_session_expired</c> for a 404).</returns>
    Task<ErrorOr<UploadResult>> ResumeUploadAsync(
        string accessToken,
        Uri sessionUri,
        Stream content,
        long length,
        IProgress<long>? progress,
        CancellationToken ct);

    /// <summary>Asks an open session how much it holds: an empty <c>PUT</c> with <c>Content-Range: bytes */total</c> (G7).</summary>
    /// <param name="accessToken">The access token; never logged.</param>
    /// <param name="sessionUri">The session URI; opaque, never logged.</param>
    /// <param name="length">The object's total size in bytes.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns><see cref="UploadProbe.Complete"/>, <see cref="UploadProbe.Partial"/>, <see cref="UploadProbe.Expired"/>, or a typed error.</returns>
    Task<ErrorOr<UploadProbe>> ProbeUploadAsync(string accessToken, Uri sessionUri, long length, CancellationToken ct);

    /// <summary>
    /// Reads the processing state of up to 50 videos (<c>videos.list?part=status</c>, 1 quota unit per call). The
    /// caller batches; a longer list is refused without a Google call.
    /// </summary>
    /// <param name="accessToken">The access token; never logged.</param>
    /// <param name="ids">One to 50 video ids.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>One snapshot per id Google knows (an unknown id is absent), or a typed error.</returns>
    Task<ErrorOr<IReadOnlyList<YouTubeVideoStatus>>> GetVideosAsync(
        string accessToken,
        IReadOnlyList<string> ids,
        CancellationToken ct);
}
