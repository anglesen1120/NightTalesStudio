using ErrorOr;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Upload;

/// <summary>
/// What one Google failure means for the publication, the connection and the quota (06.2.3-T2 plan §7, 06 README D9):
/// the single table the upload path consults, so the meaning of a Google answer lives in one place and 06.2.3-T3's
/// classifier can take the same decisions over.
/// </summary>
/// <remarks>
/// <para>
/// <b>One row per failure.</b> <see cref="Outcome"/> is the verdict this run reports; <see langword="null"/> means the
/// row keeps <c>uploading</c> and the attempt is retried (<see cref="Retryable"/>), which is the F02 lost-response and
/// Google-outage case. <see cref="ErrorCode"/> is always a <see cref="PublicationErrorCodes"/> value and never a Google
/// reason: the raw reason is diagnostic and travels separately into <c>platform_payload.lastGoogleReason</c>.
/// </para>
/// <para>
/// <b>The three side effects are named, not implied.</b> <see cref="ReleaseReservation"/> is the one decision that
/// releases <em>even though</em> a session create was issued in the same run: the quota classes, where Google itself
/// says the upload was refused. Every other failure releases only under the handler's own rule — this run reserved the
/// unit and nothing was sent (YR19 corrected, QA D2) — so a 401 or `invalid_client` that answers an upload already in
/// flight keeps the unit Google may have counted. <see cref="ExpireConnection"/> is the reauth rule (the grant is
/// dead, the owner must reconnect, and <c>channel_connection_changed.v1</c> tells the rest of the platform);
/// <see cref="RaiseIncident"/> is the <c>invalid_client</c> rule — a problem of the OAuth client, not of this owner's
/// grant, so the row is held under a platform incident instead of being failed. The handler applies them, which keeps
/// the quota ledger and the publication row inside one transaction.
/// </para>
/// <para>
/// <b>What is not a Google failure.</b> <see cref="Map(Error)"/> answers <see langword="null"/> for an error it does
/// not own (a domain refusal, a storage outage): a caller must not turn a local problem into a platform verdict, so the
/// handler decides those itself.
/// </para>
/// </remarks>
/// <param name="Outcome">The verdict of the run, or <see langword="null"/> when the row keeps <c>uploading</c>.</param>
/// <param name="ErrorCode">The <see cref="PublicationErrorCodes"/> value to write, if any.</param>
/// <param name="ReleaseReservation">
/// Whether this run's unit goes back even when a session create was issued: only the quota classes say yes, because
/// the handler already releases whenever nothing was sent in the run (YR19).
/// </param>
/// <param name="RaiseIncident">Whether a platform incident must be raised and the row held under it.</param>
/// <param name="ExpireConnection">Whether the connection must move to <c>expired</c> before the row is failed.</param>
/// <param name="Retryable">Whether another attempt is the right answer (the row keeps <c>uploading</c>).</param>
public sealed record UploadFailureDecision(
    UploadOutcome? Outcome,
    string? ErrorCode,
    bool ReleaseReservation = false,
    bool RaiseIncident = false,
    bool ExpireConnection = false,
    bool Retryable = false)
{
    /// <summary>Whether the row keeps its <c>uploading</c> state and nothing is written.</summary>
    public bool KeepsUploading => Outcome is null;

    /// <summary>The verdict of a failure no rule classifies: another attempt, nothing written.</summary>
    public static UploadFailureDecision Retry { get; } = new(null, null, Retryable: true);
}

/// <summary>The 06.2.3-T2 failure table (plan §7): the one place a Google failure becomes a publication decision.</summary>
public static class UploadFailureMap
{
    /// <summary>
    /// Classifies a failed upload result — the design's entry point, kept for the callers that hold the client's own
    /// answer.
    /// </summary>
    /// <param name="failure">The failed <c>ErrorOr</c> the client answered.</param>
    /// <returns>The decision, or <see langword="null"/> for an error this map does not own.</returns>
    public static UploadFailureDecision? Map(ErrorOr<UploadResult> failure)
    {
        if (!failure.IsError)
        {
            return null;
        }

        return Map(failure.FirstError);
    }

    /// <summary>Classifies the failed probe, session-create, resume, token or videos.list error.</summary>
    /// <param name="error">One error of a Google answer.</param>
    /// <returns>
    /// The decision, or <see langword="null"/> for an error this map does not own (a domain refusal, a storage or
    /// database failure), which the caller must decide itself.
    /// </returns>
    public static UploadFailureDecision? Map(Error error)
    {
        return error.Code switch
        {
            // Google's daily quota or upload limit: YouTube never received the upload, so the unit goes back and the
            // row waits for the next Pacific day (YR19 corrected: releasing is right because nothing was sent).
            YouTubeErrorCodes.QuotaExceeded => new UploadFailureDecision(
                UploadOutcome.Rescheduled,
                PublicationErrorCodes.QuotaExhausted,
                ReleaseReservation: true),

            // The grant is dead: the owner must reconnect, so the row is terminal for this row and the connection is
            // moved to `expired` by the handler (the client's own 06.2.3-T1 answer names the case). No release of its
            // own: a 401 can arrive after the insert was issued, so only the handler's "nothing was sent in this run"
            // rule decides whether the unit goes back (YR19 corrected, QA D2).
            YouTubeErrorCodes.ReauthRequired => new UploadFailureDecision(
                UploadOutcome.ReauthRequired,
                PublicationErrorCodes.ChannelReauthRequired,
                ExpireConnection: true),

            // The OAuth client itself is refused: a host problem. Nothing is failed and nothing is released beyond
            // this run's untouched unit; the row is held under the incident so the owner's row survives a fix. Like the
            // reauth row, no release of its own — `invalid_client` can answer a session that was already opened.
            YouTubeErrorCodes.ClientBroken => new UploadFailureDecision(
                UploadOutcome.HeldByIncident,
                PublicationErrorCodes.PlatformIncident,
                RaiseIncident: true),

            // The session is gone: a fresh run probes, reserves again and opens a new one (YR27-3).
            YouTubeErrorCodes.SessionExpired => UploadFailureDecision.Retry,

            // No usable answer: the attempt is retried and the row keeps its session and its unit.
            YouTubeErrorCodes.Transport => UploadFailureDecision.Retry,

            // Google refused the upload itself: no retry can fix it (metadata, file or channel policy).
            YouTubeErrorCodes.UploadFailed => new UploadFailureDecision(
                UploadOutcome.Failed,
                PublicationErrorCodes.UploadFailed),

            // Google refused the finished video.
            YouTubeErrorCodes.Rejected => new UploadFailureDecision(
                UploadOutcome.Failed,
                PublicationErrorCodes.Rejected),

            // `ITokenService`'s uniform dead end: no row, a revoked row, or an unreadable cipher. The connection is
            // already unusable, so only the row is failed — the expiry path is the refresh failure's, and the unit
            // follows the handler's "nothing was sent" rule like every other reauth.
            ErrorCodes.ChannelReauthRequired => new UploadFailureDecision(
                UploadOutcome.ReauthRequired,
                PublicationErrorCodes.ChannelReauthRequired),

            _ => null,
        };
    }

    /// <summary>
    /// The raw, already sanitised Google <c>reason</c> of an error, or <see langword="null"/> when the error carries
    /// none. It is diagnostic only: it lands in <c>platform_payload.lastGoogleReason</c> and nowhere else (D17).
    /// </summary>
    /// <param name="error">One error of a Google answer.</param>
    /// <returns>The sanitised reason, or <see langword="null"/>.</returns>
    public static string? GoogleReason(Error error)
    {
        return YouTubeFailure.From(error)?.GoogleReason;
    }
}
