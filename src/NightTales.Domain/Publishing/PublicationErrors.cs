using ErrorOr;

namespace NightTales.Domain.Publishing;

/// <summary>
/// The typed errors of a publication (06 README D6/D7, 06 CR-9/YR5). Each code constant equals the catalogue
/// constant of <c>NightTales.Application.Common.Errors.ErrorCodes</c>, which the Domain may not reference
/// (ErrorOr is its only package); the Domain unit test <c>PublicationErrors_Codes_MatchTheErrorCatalogue</c> keeps
/// the two equal, and <c>NightTales.Api.Http.ErrorCatalogue</c> gives each code its status.
/// </summary>
/// <remarks>
/// The aggregate itself returns only <see cref="InvalidState"/>, <see cref="ConsentTextOutdated"/> and
/// <see cref="ConsentRequired"/>; the other four are the codes the create/consent/PATCH handlers must answer with
/// when the <em>world</em> around the row says no (a second active publication, a stale <c>If-Match</c>, a
/// connection that is no longer active, an episode that is not <c>ready</c>, a locked pre-audit privacy), so they
/// live here rather than being spelled in the Application layer.
/// </remarks>
public static class PublicationErrors
{
    /// <summary>The catalogued code of "the transition is not allowed in this state" (409).</summary>
    public const string InvalidStateCode = "publication_invalid_state";

    /// <summary>The catalogued code of "an <c>If-Match</c> that no longer names the current row" (412).</summary>
    public const string ConflictCode = "publication_conflict";

    /// <summary>The catalogued code of "the channel connection is not <c>active</c>" (409).</summary>
    public const string ChannelNotActiveCode = "channel_not_active";

    /// <summary>
    /// The catalogued code of "the episode is not in a state that may be published" (409). It is the same code
    /// <c>Episode.InvalidStateCode</c> spells, because a client has one rule for both aggregates.
    /// </summary>
    public const string EpisodeInvalidStateCode = "episode_invalid_state";

    /// <summary>The catalogued code of "the consent was sent with an outdated <c>pc.v1</c> text version" (400, D7).</summary>
    public const string ConsentTextOutdatedCode = "consent_text_outdated";

    /// <summary>The catalogued code of "an upload was started for a publication nobody consented to" (409, D9).</summary>
    public const string ConsentRequiredCode = "publication_consent_required";

    /// <summary>The catalogued code of "a privacy change on a pre-audit row" (409, 06.2.2-T3).</summary>
    public const string PrivacyLockedCode = "publication_privacy_locked";

    /// <summary>
    /// The metadata key under which <see cref="ConsentTextOutdated"/> names the request-body field the client
    /// sent; the ProblemDetails writer groups validation errors by it (<c>errors.consentTextVersion</c>).
    /// </summary>
    private const string FieldKey = "field";

    /// <summary>The request-body field <see cref="ConsentTextOutdated"/> answers on (06 README D7).</summary>
    private const string ConsentTextField = "consentTextVersion";

    /// <summary>
    /// The 409 of a transition the row's state refuses — a consent on a row that is no longer
    /// <c>awaiting_consent</c>, or a cancel outside <c>awaiting_consent</c>/<c>scheduled</c>.
    /// </summary>
    /// <returns>The conflict error.</returns>
    public static Error InvalidState() => Error.Conflict(
        InvalidStateCode,
        "The publication cannot do that in its current state.");

    /// <summary>The 412 of a consent, PATCH or DELETE whose <c>If-Match</c> no longer names the current row (D7).</summary>
    /// <returns>The conflict error.</returns>
    public static Error Conflict() => Error.Conflict(
        ConflictCode,
        "The publication changed since you read it. Reload it and try again.");

    /// <summary>The 409 of a create or a consent on a channel connection that is <c>revoked</c>/<c>expired</c>/<c>error</c>.</summary>
    /// <returns>The conflict error.</returns>
    public static Error ChannelNotActive() => Error.Conflict(
        ChannelNotActiveCode,
        "The YouTube channel connection is not active. Reconnect the channel and try again.");

    /// <summary>The 409 of a create on an episode that is not in the state that may be published (D12/F15).</summary>
    /// <returns>The conflict error.</returns>
    public static Error EpisodeInvalidState() => Error.Conflict(
        EpisodeInvalidStateCode,
        "The episode cannot be published in its current state.");

    /// <summary>
    /// The 400 of a consent sent with a text version other than <c>YouTube:ConsentTextVersion</c> (D7): the owner
    /// consented to a text they can no longer see, so the write is refused and the consent screen is re-fetched.
    /// </summary>
    /// <returns>The validation error, carrying the request field it answers on.</returns>
    public static Error ConsentTextOutdated() => Error.Validation(
        ConsentTextOutdatedCode,
        "The consent text changed. Reload the consent and confirm the current version.",
        new Dictionary<string, object>(StringComparer.Ordinal) { [FieldKey] = ConsentTextField });

    /// <summary>
    /// The 409 of an upload started for a publication without <c>consent_at</c>: the 06 README D9 defence in depth
    /// behind the consent route, asserted by the upload job before it touches Google.
    /// </summary>
    /// <returns>The conflict error.</returns>
    public static Error ConsentRequired() => Error.Conflict(
        ConsentRequiredCode,
        "The publication has no consent yet.");

    /// <summary>The 409 of a privacy change on a row whose <c>platform_payload.preAudit</c> is set (06.2.2-T3, YR20).</summary>
    /// <returns>The conflict error.</returns>
    public static Error PrivacyLocked() => Error.Conflict(
        PrivacyLockedCode,
        "A pre-audit publication stays private; its privacy cannot be changed.");
}
