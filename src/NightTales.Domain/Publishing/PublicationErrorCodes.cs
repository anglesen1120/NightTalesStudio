namespace NightTales.Domain.Publishing;

/// <summary>
/// The <c>publications.error_code</c> vocabulary of the upload half of the state machine (06 README C11, plan §3.1
/// and §7, YR27-6): the reason a row is <c>scheduled</c> with a retry, or <c>failed</c>, spelled once here instead of
/// as a string literal in the job, the failure map and the aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same strings as the catalogue.</b> Each constant equals the constant of
/// <c>NightTales.Application.Common.Errors.ErrorCodes</c> that spells the same value
/// (<c>YoutubeQuotaExhausted</c>, <c>ChannelReauthRequired</c>, <c>PlatformIncident</c>,
/// <c>YoutubeUploadFailed</c>, <c>YoutubeRejected</c>), which the Domain may not reference because ErrorOr is its only
/// package. The Domain unit test <c>PublicationErrorCodes_MatchTheErrorCatalogue</c> keeps the two sets equal, exactly
/// as <c>PublicationErrors_Codes_MatchTheErrorCatalogue</c> does for the error <em>codes</em> of a refused command.
/// </para>
/// <para>
/// <b>Not a classification of Google's answer.</b> These are the values the row <em>stores</em>; the mapping from a
/// <c>YouTubeFailure</c> to one of them (and to a status, an <c>next_attempt_at</c> and an incident) is
/// <c>UploadFailureMap</c>'s single table (06.2.3-T2 §Design, 06.2.3-T3 CR: one mapping, two adapters). Nothing in
/// the aggregate decides which one applies.
/// </para>
/// <para>
/// <b>No secret, no title, no reason.</b> Every value is a fixed vocabulary token, which is what makes it safe in the
/// <c>publication_updated.v1</c> outbox payload (06 README D13) and in a client's publication row; the raw Google
/// <c>reason</c> never travels in this column's place but in <c>platform_payload.lastGoogleReason</c> (D4/D17).
/// </para>
/// <para>
/// <b>Deliberately absent: <c>user_upload_limit_reached</c>.</b> The plan's ST1 per-user slot and its reschedule code
/// belong to 06.3.2, which was dropped: the README has no per-user port and the catalogue has no such value, so only
/// the global <c>IQuotaLedger</c> reservation exists and this set is closed at five. Do not add the constant back
/// without a port and a catalogue entry.
/// </para>
/// </remarks>
public static class PublicationErrorCodes
{
    /// <summary>
    /// The daily YouTube quota of the project is spent (plan §7, 06 README D10): the row returns to
    /// <c>scheduled</c> with <c>next_attempt_at</c> at the next Pacific midnight, and the reservation is released
    /// because YouTube never received the upload. Value <c>youtube_quota_exhausted</c>.
    /// </summary>
    public const string QuotaExhausted = "youtube_quota_exhausted";

    /// <summary>
    /// The channel connection can no longer be used and the owner must reconnect it (plan §7, 06 README D9): the row
    /// is <c>failed</c> for good, because a new consent is the owner's decision, not a retry. Value
    /// <c>channel_reauth_required</c>.
    /// </summary>
    public const string ChannelReauthRequired = "channel_reauth_required";

    /// <summary>
    /// An open <see cref="PlatformIncident"/> holds the row (plan §7, UC4, YR24): the row returns to
    /// <c>scheduled</c> with a deferred <c>next_attempt_at</c> and <c>platform_payload.heldByIncidentId</c> set, so it
    /// re-checks the incident on its own instead of failing for a problem of the OAuth client. Value
    /// <c>platform_incident</c>.
    /// </summary>
    public const string PlatformIncident = "platform_incident";

    /// <summary>
    /// The upload itself failed (plan §7): a transport failure that outlived the Hangfire attempts, or a Google
    /// rejection the map does not classify more narrowly. The raw reason is kept in
    /// <c>platform_payload.lastGoogleReason</c>. Value <c>youtube_upload_failed</c>.
    /// </summary>
    public const string UploadFailed = "youtube_upload_failed";

    /// <summary>
    /// YouTube refused the video itself — its metadata, its file or the owner's channel policy (plan §7) — so no
    /// retry can succeed and the row is <c>failed</c>. Value <c>youtube_rejected</c>.
    /// </summary>
    public const string Rejected = "youtube_rejected";
}
