namespace NightTales.Application.Features.Publishing.Upload;

/// <summary>
/// The internal codes the YouTube client returns as <c>ErrorOr</c> errors (06.2.3-T1). They are not wire codes: the
/// five values written to <c>publications.error_code</c> are chosen from these by 06.2.3-T2/plan.md §7 and the
/// classifier of 06.2.3-T3. Every error carrying one of these also carries a <c>YouTubeFailure</c> in its metadata.
/// </summary>
public static class YouTubeErrorCodes
{
    /// <summary>Google's daily quota or upload limit is spent (403 <c>quotaExceeded</c> / <c>uploadLimitExceeded</c>): reschedule.</summary>
    public const string QuotaExceeded = "youtube_quota_exceeded";

    /// <summary>The grant is dead (<c>invalid_grant</c>, or a 401 whose reason says the token is unusable): the user reconnects.</summary>
    public const string ReauthRequired = "youtube_reauth_required";

    /// <summary>The OAuth client itself is refused (<c>invalid_client</c>, <c>unauthorized_client</c>): a host problem, not the user's grant.</summary>
    public const string ClientBroken = "youtube_client_broken";

    /// <summary>The resumable session is gone (404): the caller creates a new one.</summary>
    public const string SessionExpired = "youtube_session_expired";

    /// <summary>No usable answer (timeout, connection drop, 5xx, a plain 401, an unreadable body, a short stream): the job retries.</summary>
    public const string Transport = "youtube_transport";

    /// <summary>Google refused the upload for a reason a retry cannot fix (400/403 with a reason, a length mismatch before any call).</summary>
    public const string UploadFailed = "youtube_upload_failed";

    /// <summary>Google rejected the finished video (<c>uploadStatus = rejected</c>).</summary>
    public const string Rejected = "youtube_rejected";
}
