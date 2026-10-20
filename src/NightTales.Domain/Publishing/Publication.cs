using System.Diagnostics.CodeAnalysis;
using ErrorOr;
using NightTales.Domain.Common;

// Two members below are named after the enums of this namespace (`Platform`, `ConsentSource`), and a member
// shadows a type of the same name inside the class body; these aliases name the enums where the aggregate needs
// their wire values, so the row, the CHECK and the `platform`/`consent_source` columns cannot drift.
using ConsentSourceType = NightTales.Domain.Publishing.ConsentSource;
using PlatformType = NightTales.Domain.Publishing.Platform;

namespace NightTales.Domain.Publishing;

/// <summary>
/// One YouTube publication of one episode (06 README D6): the metadata that is uploaded, the consent that allows
/// the upload, the veto window an autopilot publication can still be cancelled in, and the state machine the
/// upload, the poller and the go-live job drive — <c>awaiting_consent → scheduled → uploading → processing →
/// published</c>, with <c>failed</c> and <c>cancelled</c> as the two exits.
/// </summary>
/// <remarks>
/// <para>
/// The row is <see cref="IUserOwned"/>, so 09 README D10's tenant filter applies to every read: a client can
/// neither see nor publish another owner's episode. Exactly one publication per <c>(episode_id, platform)</c> may
/// be active at a time — the partial unique <c>ux_publications_active_episode_platform</c> excludes
/// <c>failed</c>/<c>cancelled</c> — so "publish this episode again" is a new row after the previous one left those
/// two states, never a revival of it.
/// </para>
/// <para>
/// <b>Constructing one.</b> <see cref="Create"/> is the only factory. It mints the UUIDv7 id, pins
/// <c>contains_synthetic_media</c> to <see langword="true"/> (06 README D6: the platform has no API field to turn
/// the declaration off, so a Night Tales video always declares it) and leaves <see cref="SelfDeclaredMadeForKids"/>
/// computed <see langword="false"/>. It stores the metadata it is given: the handler has already appended the music
/// attribution (C8), validated every YouTube limit through <see cref="YouTubeMetadataRules"/> and resolved the
/// <em>effective</em> privacy of 06 README D12 (the pre-audit override), so nothing here re-decides a rule the
/// command owns.
/// </para>
/// <para>
/// <b>Consent.</b> <see cref="Consent"/> is the owner's consent on the consent route and
/// <see cref="ConsentFromAuthorization"/> the autopilot job's (06 README D7/D8); both need
/// <c>awaiting_consent</c>, both record who acted in <see cref="ConsentSource"/> and both write the version of the
/// consent text that was shown, which is why <see cref="Consent"/> compares it and refuses a stale one (D7).
/// <see cref="StartUpload"/> refuses a row without <see cref="ConsentAt"/> (D9 defence in depth) and is a no-op
/// "resume" while the row is already <c>uploading</c> (YR18), so the lost-response retry of F02 reaches the
/// probe/resume step instead of failing the job. <see cref="IsUserConsentedAs"/> is the read that lets the
/// consent route answer a retried request with the answer it already gave (F04); it writes nothing, which is why
/// <see cref="Consent"/> keeps refusing every row that is no longer <c>awaiting_consent</c>.
/// </para>
/// <para>
/// <b>Events.</b> <see cref="Create"/>, <see cref="Consent"/>, <see cref="ConsentFromAuthorization"/> and
/// <see cref="Cancel"/> raise <see cref="PublicationUpdated"/>, which the interceptor stores as
/// <c>publication_updated.v1</c> beside the row in the same unit of work (06 README D13). The upload transitions
/// raise nothing: no consumer acts on them yet (06.2.3-T2 schedules the job off the consent event, YR3). The
/// payload is ids, the status and the error code only — the type never exposes the title, the description or the
/// channel display name to a log line or an outbox row (06 README D17).
/// </para>
/// <para>
/// <b>Immutability.</b> Every setter is private, so the members without a method here — <c>external_post_id</c>,
/// <c>external_url</c>, <c>published_at</c>, <c>next_attempt_at</c>, <c>error_code</c> and
/// <c>veto_notice_sent_at</c> — are written by the tasks that own those transitions (06.2.3-T2, 06.2.5-T1,
/// 06.2.2-T2/T3) through EF, which is what keeps this file the state machine of the create/consent/cancel half of
/// the row.
/// </para>
/// </remarks>
public sealed class Publication : AggregateRoot, IUserOwned, IAuditable
{
    /// <summary>The request field the <see cref="Create"/> veto guard answers on.</summary>
    private const string VetoEndsField = "vetoEndsAt";

    /// <summary>The catalogued code of every <see cref="YouTubeMetadataRules"/> failure (09 §5.4).</summary>
    private const string ValidationFailedCode = "validation_failed";

    private Publication()
    {
    }

    /// <summary>The owner; 09 README D10's tenant filter compares it on every read.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The episode being published; a soft-cancelled or foreign episode is refused before this exists.</summary>
    public Guid EpisodeId { get; private set; }

    /// <summary>The channel connection the upload uses; the foreign key is <c>RESTRICT</c>, so the channel survives it.</summary>
    public Guid ChannelConnectionId { get; private set; }

    /// <summary>
    /// The publishing platform, always <c>youtube</c> in this task (07 adds <c>tiktok</c>): the column's text value,
    /// which is also the value the partial unique <c>(episode_id, platform)</c> keys on. It is computed from
    /// <see cref="Platform.YouTube"/> rather than typed twice, so the enum, the column and its <c>CHECK</c> cannot
    /// drift.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "A per-row column value (the partial unique keys on it); 07 adds tiktok rows, so it stays an instance member.")]
    public string Platform => PlatformType.YouTube.ToWire();

    /// <summary>The row's state; see <see cref="PublicationStatus"/> for the transitions each member allows.</summary>
    public PublicationStatus Status { get; private set; }

    /// <summary>The video title, already within the G4 limits (1–100 characters, no angle brackets).</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>The description as uploaded, the music attribution included; at most 5,000 UTF-8 bytes (F08).</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>
    /// The tags in submitted order; at most 15, G4-counted length at most 500 (F09). The property is a
    /// <see cref="List{T}"/> rather than a read-only view because the column is a PostgreSQL <c>text[]</c> and the
    /// repository maps a primitive collection only through <c>List&lt;T&gt;</c> (<c>Voice.Tags</c>,
    /// <c>MusicTrack.Moods</c>); the private setter, not the collection type, is what keeps this aggregate the only
    /// writer.
    /// </summary>
    public List<string> Tags { get; private set; } = [];

    /// <summary>
    /// The visibility YouTube is asked for — the <em>effective</em> one, not the submitted one (06 README D12):
    /// <c>private</c> whenever the video was created while its channel's audit status was not approved, and the
    /// owner's choice otherwise. A pre-audit row keeps this value for good (YR20).
    /// </summary>
    public string Privacy { get; private set; } = string.Empty;

    /// <summary>
    /// When the owner wants the video live, or <see langword="null"/> for "as soon as it is processed". At least
    /// <c>YouTubeOptions.PublishLeadMinutes</c> ahead of the create (D6/D12).
    /// </summary>
    public DateTimeOffset? PublishAt { get; private set; }

    /// <summary>
    /// When the upload (or its next retry) is due: the queue time of D12 at consent, and the retry schedule after a
    /// transient failure. Owned by 06.2.3-T2; read by <c>ix_publications_status_next_attempt_at</c>.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>
    /// The instant the autopilot veto window closes (YR1): <c>min(now + veto_window_hours, (publishAt ??
    /// now) − PublishLeadMinutes)</c>, and <see langword="null"/> on a manual publication — the owner who created it
    /// is the one who may cancel it. Immutable after <see cref="Create"/> (06.2.2-T3 enforces the same on PATCH).
    /// </summary>
    public DateTimeOffset? VetoEndsAt { get; private set; }

    /// <summary>
    /// When consent was given, or <see langword="null"/> while the row is <c>awaiting_consent</c>. Its absence is
    /// what <see cref="StartUpload"/> refuses (D9): no upload may start before it.
    /// </summary>
    public DateTimeOffset? ConsentAt { get; private set; }

    /// <summary>
    /// The version of the consent text that was shown (<c>pc.v1</c>, D7), written with
    /// <see cref="ConsentAt"/>; <see langword="null"/> until then.
    /// </summary>
    public string? ConsentTextVersion { get; private set; }

    /// <summary>
    /// Who consented, as the wire string of <see cref="ConsentSource"/> (<c>user</c>/<c>autopilot</c>);
    /// <see langword="null"/> until a consent, and the value the consent templates key on (YR9).
    /// </summary>
    public string? ConsentSource { get; private set; }

    /// <summary>
    /// The authorization an autopilot publication was created under, written again by
    /// <see cref="ConsentFromAuthorization"/>; <see langword="null"/> on a manual publication.
    /// </summary>
    public Guid? AutopilotAuthorizationId { get; private set; }

    /// <summary>
    /// When the veto notice was confirmed as delivered (06.2.2-T2/T3). The autopilot consent job does not consent a
    /// row whose notice was never sent (D8). Written by those tasks, never here.
    /// </summary>
    public DateTimeOffset? VetoNoticeSentAt { get; private set; }

    /// <summary>
    /// Whether the video is declared as synthetic media. Always <see langword="true"/>: D6 makes the declaration a
    /// product decision, and YouTube has no API field that turns it off.
    /// </summary>
    public bool ContainsSyntheticMedia { get; private set; }

    /// <summary>
    /// Whether the video is declared as made for kids: always <see langword="false"/> (D6 — Night Tales content is
    /// not directed at children), so the declaration can never be flipped by a client.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "A per-row column value mapped through a shadow property; it must stay an instance member so a client can never write it.")]
    public bool SelfDeclaredMadeForKids => false;

    /// <summary>
    /// YouTube's video id, written with <see cref="ExternalUrl"/> by 06.2.3-T2 after a successful upload, and
    /// rewritten by <c>TokenHealthJob</c> when it re-reads the video (D16).
    /// </summary>
    public string? ExternalPostId { get; private set; }

    /// <summary>
    /// The watch URL built from <see cref="ExternalPostId"/>, so no DTO mapper has to build one (06 CR-1); written
    /// by 06.2.3-T2.
    /// </summary>
    public string? ExternalUrl { get; private set; }

    /// <summary>
    /// When the video went live, written by 06.2.5-T1 at the <c>→ published</c> transition. It is what
    /// <c>PublicationsSummaryDto.LastPublishedAt</c> reads; a derived <c>updated_at</c> would drift, because
    /// <c>TokenHealthJob</c> rewrites <see cref="ExternalPostId"/> (D16).
    /// </summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>
    /// The platform-owned bookkeeping of this row (06 README D12/YR20): <c>{"preAudit": true,
    /// "requestedPrivacy": "&lt;submitted&gt;"}</c> when the create overrode the privacy, and then whichever keys the
    /// later tasks own — <c>uploadSessionEnc</c> (06.2.3-T2) and <c>vetoNoticeEmailId</c> (06.2.2-T2). Never
    /// surfaces through a DTO, a log line or the outbox (D4/D17).
    /// </summary>
    public string? PlatformPayloadJson { get; private set; }

    /// <summary>
    /// The catalogued reason a <c>failed</c> publication gave up (<c>platform_incident</c>,
    /// <c>youtube_go_live_unconfirmed</c>, …); <see langword="null"/> in every other state. It is also copied into
    /// <c>publication_updated.v1</c>, so a client learns why without a second request (D13).
    /// </summary>
    public string? ErrorCode { get; private set; }

    /// <summary>When the row was created; the factory stamps it from the command's clock (09 W3 CR-38).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the row last changed; every mutation here moves it to the command's clock reading.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates the publication of one <c>ready</c> episode on one <c>active</c> channel connection, in
    /// <see cref="PublicationStatus.AwaitingConsent"/>, and raises <see cref="PublicationUpdated"/> (06 README D6,
    /// D13: the outbox row's status is <c>awaiting_consent</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler owns everything that needs the world: the episode and connection reads, the attribution, the
    /// YouTube limits, the YR1 veto window and the pre-audit privacy override (06.2.1-T1 §Design Application
    /// steps 1–8). This factory stores their result and adds nothing to it — in particular it does not touch the
    /// episode: the <c>ready → scheduled</c> call is <c>Episode.MarkScheduled</c> in 06.2.2-T1 after the consent
    /// (D12/F15).
    /// </para>
    /// <para>
    /// Both veto inputs come from the caller because YR1's formula needs the series' <c>veto_window_hours</c> and
    /// <c>YouTubeOptions</c>, neither of which the Domain may read. The one thing this factory <em>does</em> decide
    /// is the D6 invariant the formula rests on: a manual publication has no veto window, so passing
    /// <paramref name="vetoEndsAt"/> without <paramref name="autopilotAuthorizationId"/> is refused rather than
    /// stored as a row nobody will ever consent.
    /// </para>
    /// </remarks>
    /// <param name="userId">The owner; the tenant filter of 09 README D10 compares it on every read.</param>
    /// <param name="episodeId">The episode to publish.</param>
    /// <param name="channelConnectionId">The connection the upload will run through.</param>
    /// <param name="metadata">The already validated metadata, with the effective privacy of D12.</param>
    /// <param name="autopilotAuthorizationId">The authorization an autopilot create runs under, or <see langword="null"/>.</param>
    /// <param name="vetoEndsAt">The YR1 instant, or <see langword="null"/> on a manual publication.</param>
    /// <param name="platformPayloadJson">
    /// The pre-audit stamp of D12 (<c>{"preAudit": true, "requestedPrivacy": "public"}</c>), or
    /// <see langword="null"/> when the submitted privacy was committed as it was.
    /// </param>
    /// <param name="now">The command's clock reading; the domain never reads the clock itself.</param>
    /// <returns>
    /// The new aggregate, tracked by the caller's context; or the 400 of a manual publication that was handed a
    /// veto end.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    public static ErrorOr<Publication> Create(
        Guid userId,
        Guid episodeId,
        Guid channelConnectionId,
        PublicationMetadata metadata,
        Guid? autopilotAuthorizationId,
        DateTimeOffset? vetoEndsAt,
        string? platformPayloadJson,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (autopilotAuthorizationId is null && vetoEndsAt is not null)
        {
            return Error.Validation(
                ValidationFailedCode,
                "A manual publication has no veto window; the owner who creates it is the one who may cancel it.",
                new Dictionary<string, object>(StringComparer.Ordinal) { ["field"] = VetoEndsField });
        }

        var publication = new Publication
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            EpisodeId = episodeId,
            ChannelConnectionId = channelConnectionId,
            Status = PublicationStatus.AwaitingConsent,
            Title = metadata.Title,
            Description = metadata.Description,
            // Copied, so the stored row cannot change under the caller's hands when it reuses the metadata object.
            Tags = [.. metadata.Tags],
            Privacy = metadata.Privacy,
            PublishAt = metadata.PublishAt,
            VetoEndsAt = vetoEndsAt,
            AutopilotAuthorizationId = autopilotAuthorizationId,
            ContainsSyntheticMedia = true,
            PlatformPayloadJson = platformPayloadJson,
            CreatedAt = now,
            UpdatedAt = now,
        };

        publication.Raise(new PublicationUpdated(
            publication.Id,
            publication.EpisodeId,
            publication.UserId,
            publication.Status,
            publication.ErrorCode,
            now));

        return publication;
    }

    /// <summary>
    /// Records the owner's consent (06 README D7): the row becomes <see cref="PublicationStatus.Scheduled"/> under
    /// the version of the consent text that was shown, and raises <see cref="PublicationUpdated"/> so the Worker can
    /// queue the upload (YR3).
    /// </summary>
    /// <param name="textVersion">The version the client consented to.</param>
    /// <param name="currentTextVersion"><c>YouTube:ConsentTextVersion</c> as the host has it (D7).</param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns>
    /// <c>Success</c>; or 409 <c>publication_invalid_state</c> when the row is not <c>awaiting_consent</c>, or 400
    /// <c>consent_text_outdated</c> when <paramref name="textVersion"/> is not the current one.
    /// </returns>
    public ErrorOr<Success> Consent(string textVersion, string currentTextVersion, DateTimeOffset now)
    {
        if (Status != PublicationStatus.AwaitingConsent)
        {
            return PublicationErrors.InvalidState();
        }

        // D7: the owner confirmed a text they can no longer be shown again, so the write is refused and the consent
        // screen re-fetched — the consent that is recorded must name the text that was actually read.
        if (!string.Equals(textVersion, currentTextVersion, StringComparison.Ordinal))
        {
            return PublicationErrors.ConsentTextOutdated();
        }

        ConsentAt = now;
        ConsentTextVersion = textVersion;
        ConsentSource = ConsentSourceType.User.ToWire();
        Status = PublicationStatus.Scheduled;
        UpdatedAt = now;

        Raise(new PublicationUpdated(Id, EpisodeId, UserId, Status, ErrorCode, now));

        return Result.Success;
    }

    /// <summary>
    /// Records the autopilot consent (06 README D8): the same write as <see cref="Consent"/> — the row becomes
    /// <see cref="PublicationStatus.Scheduled"/> and raises <see cref="PublicationUpdated"/> — with
    /// <c>consent_source = autopilot</c> and the authorization that consented, and without the text-version
    /// comparison, because the job passes the version the host currently holds rather than a client's answer
    /// (06.2.2-T3 reuses this verbatim).
    /// </summary>
    /// <param name="textVersion">The consent text version the job consented under.</param>
    /// <param name="authorizationId">The authorization that is consenting for the owner.</param>
    /// <param name="now">The job's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of a row that is not <c>awaiting_consent</c>.</returns>
    public ErrorOr<Success> ConsentFromAuthorization(string textVersion, Guid authorizationId, DateTimeOffset now)
    {
        if (Status != PublicationStatus.AwaitingConsent)
        {
            return PublicationErrors.InvalidState();
        }

        ConsentAt = now;
        ConsentTextVersion = textVersion;
        ConsentSource = ConsentSourceType.Autopilot.ToWire();
        AutopilotAuthorizationId = authorizationId;
        Status = PublicationStatus.Scheduled;
        UpdatedAt = now;

        Raise(new PublicationUpdated(Id, EpisodeId, UserId, Status, ErrorCode, now));

        return Result.Success;
    }

    /// <summary>
    /// Whether this row already carries exactly the consent a repeated request would write: it is
    /// <see cref="PublicationStatus.Scheduled"/>, consented by the owner (<c>consent_source = user</c>) under
    /// <paramref name="textVersion"/> (06 README D7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the read the consent handler answers a retried request with. A client whose response was lost
    /// (F04's lost-response retry) resends the same body with the <em>pre-consent</em> <c>If-Match</c>, which no
    /// longer names the row's <c>xmin</c> — so the handler asks this question <em>before</em> it compares the
    /// ETag, and answers the same DTO/ETag it would have answered the first time, writing nothing: no second
    /// <c>audit_log</c> row, no second <c>publication_updated.v1</c>, no episode change.
    /// </para>
    /// <para>
    /// It is deliberately narrow, because it is what turns a 409 into a success: an <c>autopilot</c>-consented row
    /// is <em>not</em> "already consented by the owner" — D7 says the two consent paths are never silently merged,
    /// and 06.2.2-T4's "approve now" is the path that decides otherwise — and a different text version is a stale
    /// client that must refetch the consent text. Both fall through to the normal path.
    /// </para>
    /// <para>
    /// A read only: it writes nothing and raises no event, so the no-op answer is indistinguishable from the first
    /// answer on the row's own evidence. <see cref="Consent"/> itself is unchanged and still refuses any row that is
    /// no longer <c>awaiting_consent</c> — the shortcut lives in the handler, which is the only caller that has an
    /// ETag to compare.
    /// </para>
    /// </remarks>
    /// <param name="textVersion">The version the retried request consented to (<c>pc.v1</c>), compared ordinally.</param>
    /// <returns>
    /// <see langword="true"/> when this row is already the row that request would produce, so the command can answer
    /// 200 without a write.
    /// </returns>
    public bool IsUserConsentedAs(string textVersion) =>
        Status == PublicationStatus.Scheduled
        && string.Equals(ConsentSource, ConsentSourceType.User.ToWire(), StringComparison.Ordinal)
        && string.Equals(ConsentTextVersion, textVersion, StringComparison.Ordinal);

    /// <summary>
    /// Cancels the publication (06 README D7): only <see cref="PublicationStatus.AwaitingConsent"/> and
    /// <see cref="PublicationStatus.Scheduled"/> may cancel, because once the upload runs there is a video to take
    /// down rather than a job to stop. Raises <see cref="PublicationUpdated"/> with the <c>cancelled</c> status.
    /// </summary>
    /// <param name="now">The command's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 of a row that already left the two cancellable states.</returns>
    public ErrorOr<Success> Cancel(DateTimeOffset now)
    {
        if (Status is not (PublicationStatus.AwaitingConsent or PublicationStatus.Scheduled))
        {
            return PublicationErrors.InvalidState();
        }

        Status = PublicationStatus.Cancelled;
        UpdatedAt = now;

        Raise(new PublicationUpdated(Id, EpisodeId, UserId, Status, ErrorCode, now));

        return Result.Success;
    }

    /// <summary>
    /// Moves a consented publication into <see cref="PublicationStatus.Uploading"/> at the start of the upload job,
    /// and is a no-op "resume" while the row is already <c>uploading</c> (06 YR18) — the F02 lost-response retry
    /// then reaches the probe/resume step instead of failing the job on its own first attempt.
    /// </summary>
    /// <remarks>
    /// The consent check is D9's defence in depth: no upload may start for a row without
    /// <see cref="ConsentAt"/>, whatever the job believes. Neither path raises a domain event — the consent event
    /// already told the Worker to schedule this upload (YR3).
    /// </remarks>
    /// <param name="now">The job's clock reading; the stamp of the <c>scheduled → uploading</c> transition only.</param>
    /// <returns>
    /// <c>Success</c>; or 409 <c>publication_consent_required</c> without a consent, or 409
    /// <c>publication_invalid_state</c> from any other state.
    /// </returns>
    public ErrorOr<Success> StartUpload(DateTimeOffset now)
    {
        if (ConsentAt is null)
        {
            return PublicationErrors.ConsentRequired();
        }

        // YR18: an upload that is already running is the retry of one whose response was lost, so "resume" is a
        // success that writes nothing — no state move, no updated_at bump, no event.
        if (Status == PublicationStatus.Uploading)
        {
            return Result.Success;
        }

        if (Status != PublicationStatus.Scheduled)
        {
            return PublicationErrors.InvalidState();
        }

        Status = PublicationStatus.Uploading;
        UpdatedAt = now;

        return Result.Success;
    }
}
