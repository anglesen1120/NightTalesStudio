using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
/// <c>publication_updated.v1</c> beside the row in the same unit of work (06 README D13), and so do the upload
/// transitions that change what a consumer must do: <see cref="RescheduleUpload"/> and
/// <see cref="HoldForIncident"/> (back to <c>scheduled</c>, so the Worker schedules the retry at
/// <c>next_attempt_at</c>, YR3/UC4), <see cref="MarkProcessing"/>/<see cref="MarkUploadedExternal"/>
/// (<c>processing</c>) and <see cref="FailUpload"/> (<c>failed</c>). The writes that only move bookkeeping raise
/// nothing — <see cref="StartUpload"/> and its resume no-op (YR18), <see cref="AttachUploadSession"/>,
/// <see cref="MarkQuotaReserved"/> and <see cref="ClearQuotaReservation"/> — because no consumer acts on them.
/// The payload is ids, the status and the error code only — the type never exposes the title, the description or
/// the channel display name to a log line or an outbox row (06 README D17).
/// </para>
/// <para>
/// <b>Immutability.</b> Every setter is private, so the columns without a member of their own —
/// <c>published_at</c> (06.2.5-T1) and <c>veto_notice_sent_at</c> (06.2.2-T3) — are written by the tasks that own
/// those transitions through EF, while <c>external_post_id</c>, <c>external_url</c>, <c>next_attempt_at</c> and
/// <c>error_code</c> are written here by the upload transitions of 06.2.3-T2. <c>platform_payload</c> is reached
/// only through <see cref="PublicationPayload"/>, one key at a time, so a task's keys cannot collide.
/// </para>
/// </remarks>
public sealed class Publication : AggregateRoot, IUserOwned, IAuditable
{
    /// <summary>The request field the <see cref="Create"/> veto guard answers on.</summary>
    private const string VetoEndsField = "vetoEndsAt";

    /// <summary>The catalogued code of every <see cref="YouTubeMetadataRules"/> failure (09 §5.4).</summary>
    private const string ValidationFailedCode = "validation_failed";

    /// <summary>
    /// The <c>platform_payload</c> key naming the queued veto notice (06 README D7): the task that owns the
    /// document's other keys reads and writes its own and never this one.
    /// </summary>
    private const string VetoNoticeEmailIdKey = "vetoNoticeEmailId";

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
    /// The <em>protected</em> resumable session URI of the upload in progress
    /// (<c>platform_payload.uploadSessionEnc</c>, 06 README D4, plan §3.2), or <see langword="null"/> when no session
    /// is open. It is Data-Protection ciphertext the caller protected under <c>tokens.v1/{connectionId}</c>; the
    /// aggregate stores it and hands it back to the one caller that resumes the upload, and it never appears in a log
    /// line, an outbox payload, an audit row or an exception message.
    /// </summary>
    public string? UploadSessionEnc =>
        PublicationPayload.ReadString(PlatformPayloadJson, PublicationPayload.UploadSessionEncKey);

    /// <summary>
    /// The Pacific quota day the row holds a reserved unit on (<c>platform_payload.quotaReservedDay</c>, 06 README
    /// D10), or <see langword="null"/> when nothing is reserved. Its presence is what makes 06.2.6-T1's cancel
    /// release a unit.
    /// </summary>
    public DateOnly? QuotaReservedDay => PublicationPayload.ReadQuotaDay(PlatformPayloadJson);

    /// <summary>
    /// When the run that holds this row's reservation issued its <c>videos.insert</c> session create
    /// (<c>platform_payload.uploadCreateIssuedAt</c>, plan §3.2, YR19), or <see langword="null"/> when no create was
    /// issued against the current reservation. It is what makes the release decision survive the attempt that made the
    /// call: a later attempt sees it without ever having called Google itself.
    /// </summary>
    public DateTimeOffset? UploadCreateIssuedAt =>
        PublicationPayload.ReadInstant(PlatformPayloadJson, PublicationPayload.UploadCreateIssuedAtKey);

    /// <summary>
    /// Whether a create was issued against the row's current reservation (YR19's release predicate): either the marker
    /// is stamped, or a session is stored — a session URI only exists because an insert answered it.
    /// </summary>
    public bool HasUploadCreateIssued => UploadCreateIssuedAt is not null || UploadSessionEnc is not null;

    /// <summary>
    /// Until when one run owns this row's session create (<c>platform_payload.uploadSessionCreateLeaseUntil</c>, plan
    /// §Performance, SEC-8), or <see langword="null"/> when no claim is held. Compared against the caller's clock, never
    /// by itself: the Domain may not read one.
    /// </summary>
    public DateTimeOffset? UploadSessionLeaseUntil =>
        PublicationPayload.ReadInstant(PlatformPayloadJson, PublicationPayload.UploadSessionLeaseKey);

    /// <summary>
    /// The sticky upload plan the insert is built from (<c>platform_payload.upload</c>, plan §3 F13), or
    /// <see langword="null"/> when the row carries none — 06.2.4-T1 has not written it yet, so
    /// <see cref="EnsureUploadPlan"/> derives one from the row's own columns before the request is built.
    /// </summary>
    public PublicationPayload.UploadPlan? UploadPlan =>
        PublicationPayload.ReadUpload(PlatformPayloadJson);

    /// <summary>
    /// Which run owns this row's streaming window (<c>platform_payload.uploadStreamLeaseOwner</c>, SEC-4), or
    /// <see langword="null"/> when no run claims one.
    /// </summary>
    public Guid? UploadStreamLeaseOwner =>
        PublicationPayload.ReadGuid(PlatformPayloadJson, PublicationPayload.UploadStreamLeaseOwnerKey);

    /// <summary>
    /// Until when the run named by <see cref="UploadStreamLeaseOwner"/> owns this row's streaming window
    /// (<c>platform_payload.uploadStreamLeaseUntil</c>, SEC-4). Compared against the caller's clock, never by itself:
    /// a lease that has passed is stale and may be taken over.
    /// </summary>
    public DateTimeOffset? UploadStreamLeaseUntil =>
        PublicationPayload.ReadInstant(PlatformPayloadJson, PublicationPayload.UploadStreamLeaseUntilKey);

    /// <summary>
    /// The id of the open <see cref="PlatformIncident"/> holding the row (<c>platform_payload.heldByIncidentId</c>,
    /// YR24), or <see langword="null"/> when the row is not held.
    /// </summary>
    public Guid? HeldByIncidentId =>
        PublicationPayload.ReadGuid(PlatformPayloadJson, PublicationPayload.HeldByIncidentIdKey);

    /// <summary>
    /// The raw Google <c>reason</c> of the last failure (<c>platform_payload.lastGoogleReason</c>, plan §3.2), already
    /// sanitised by the caller: diagnostic bookkeeping, never a client-facing code.
    /// </summary>
    public string? LastGoogleReason =>
        PublicationPayload.ReadString(PlatformPayloadJson, PublicationPayload.LastGoogleReasonKey);

    /// <summary>
    /// The processing block <c>{ startedAt, deadlineAt, polls }</c> 06.2.5-T1's poller claims a row on
    /// (<c>platform_payload.processing</c>, plan §3.2), or <see langword="null"/> before the upload finished.
    /// </summary>
    public PublicationPayload.Processing? Processing => PublicationPayload.ReadProcessing(PlatformPayloadJson);

    /// <summary>
    /// The catalogued reason a <c>failed</c> publication gave up (<c>platform_incident</c>,
    /// <c>youtube_go_live_unconfirmed</c>, …); <see langword="null"/> in every other state. It is also copied into
    /// <c>publication_updated.v1</c>, so a client learns why without a second request (D13).
    /// </summary>
    public string? ErrorCode { get; private set; }

    /// <summary>When the row was created; the factory stamps it from the command's clock (09 W3 CR-38).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// When the row last changed; every transition here moves it to the command's clock reading. The payload-only
    /// writes (<see cref="AttachUploadSession"/>, <see cref="ClearUploadSession"/>, <see cref="MarkQuotaReserved"/>,
    /// <see cref="ClearQuotaReservation"/>) leave it alone, exactly as <see cref="SetVetoNoticeEmailId"/> does: the
    /// stamp belongs to the transition that owns the run.
    /// </summary>
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
    /// Records the <c>email_messages.id</c> of the veto notice 06.2.2-T2 queued for this autopilot publication
    /// (06 README D7): one key of <see cref="PlatformPayloadJson"/>, so 06.2.2-T3 can poll the notice's delivery
    /// without a column of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The notice is queued inside the create's own unit of work, on the tracked, still-unsaved row, which is why
    /// this method writes the document in place instead of asking the caller for a second update: the create's one
    /// <c>SaveChanges</c> stores the row, the <c>email_messages</c> row and both outbox events together, and the
    /// <c>xmin</c> the 201 answer carries is the one of that single write.
    /// </para>
    /// <para>
    /// <b>Only the one key.</b> The document belongs to 06.2.1-T1 (<c>preAudit</c>, <c>requestedPrivacy</c>) and
    /// to the later tasks that add their own keys (<c>uploadSessionEnc</c>, 06.2.3-T2), so it is parsed, one
    /// property is set and the rest is written back untouched. A document nobody can parse — only a hand-edited
    /// row reaches that state — is replaced rather than thrown out of: an aggregate never throws for the shape of
    /// a column it did not write.
    /// </para>
    /// <para>
    /// <b>Not the delivery stamp.</b> <see cref="VetoNoticeSentAt"/> stays <see langword="null"/>: 06.2.2-T3
    /// writes it when the message's delivery status says it was sent (D8), and this key only names the message.
    /// </para>
    /// </remarks>
    /// <param name="emailMessageId">The id <c>IEmailSender.QueueAsync</c> answered for the veto notice.</param>
    /// <returns>
    /// <c>Success</c>; or 409 <c>publication_invalid_state</c> for a row that is not an unsent autopilot
    /// publication — a manual row (no authority, no notice), a row without a veto window (D6/YR1: nobody could
    /// read the deadline) or one that already left <see cref="PublicationStatus.AwaitingConsent"/>.
    /// </returns>
    public ErrorOr<Success> SetVetoNoticeEmailId(Guid emailMessageId)
    {
        if (Status != PublicationStatus.AwaitingConsent
            || AutopilotAuthorizationId is null
            || VetoEndsAt is null)
        {
            return PublicationErrors.InvalidState();
        }

        PlatformPayloadJson = PublicationPayload.WithKey(
            PlatformPayloadJson,
            VetoNoticeEmailIdKey,
            emailMessageId.ToString());

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
    /// <b>The window ends when the upload starts.</b> The idempotent answer holds only while the row is
    /// <see cref="PublicationStatus.Scheduled"/>: once the Worker moves it to
    /// <see cref="PublicationStatus.Uploading"/> or later, the repeat is no longer answered here, and the status depends
    /// on the tag the retry carried. A lost-response retry still sends the <em>pre-consent</em> <c>If-Match</c>, which
    /// names no version of the row any more, so the handler's ETag comparison answers <c>412 publication_conflict</c>.
    /// Only a retry that carries the row's <em>current</em> tag reaches <see cref="Consent"/> and gets
    /// <c>409 publication_invalid_state</c> from there. Neither is a lost first consent — the row already carries it —
    /// so 06.2.2-T4 documents the client handling: it refetches the publication and treats the post-consent row as the
    /// success it is looking for.
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
    /// <remarks>
    /// <c>cancelled</c> is the idempotent answer (06.2.6-T1): a <c>DELETE</c> retried after a lost response finds
    /// the row already cancelled and must answer the same success, so this method writes nothing on that path — no
    /// status move, no <c>updated_at</c> bump, no second <see cref="PublicationUpdated"/>. The other callers are
    /// 06.4.1-T1's disconnect (which cancels the rows of a disconnected channel) and 06.2.2-T2's cancel-by-link; the
    /// upload job never calls this method, it refuses a row that is not <c>scheduled</c> in
    /// <see cref="StartUpload"/>. The no-write property is what keeps a repeat from telling the outbox that anything
    /// changed.
    /// </remarks>
    /// <param name="now">The command's clock reading; ignored when the row is already <c>cancelled</c>.</param>
    /// <returns>
    /// <c>Success</c> — including the repeat of a row already <c>cancelled</c> — or the 409 of a row that already
    /// left the two cancellable states.
    /// </returns>
    public ErrorOr<Success> Cancel(DateTimeOffset now)
    {
        // The lost-response repeat: the row already holds exactly the state this call would write, so it is a
        // success that writes nothing (no updated_at bump, no event) — the same shape StartUpload uses below.
        if (Status == PublicationStatus.Cancelled)
        {
            return Result.Success;
        }

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

    /// <summary>
    /// Stores the <em>already protected</em> resumable session URI of the upload in progress (plan §3.1, 06 README
    /// D4): one key of <see cref="PlatformPayloadJson"/>, written before the first byte leaves the process so a run
    /// whose last response was lost resumes from the probe instead of creating a second video (F02).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ciphertext in, ciphertext out.</b> The caller protects the URI through <c>ITokenProtector</c> under
    /// <c>tokens.v1/{connectionId}</c> and passes the result; this member never sees the plaintext, never logs the
    /// value, and nothing here can echo it into an exception message. A repeated call replaces the stored value, which
    /// is what a session-expired restart does (plan §7: a 404 on probe reserves a fresh unit and opens a new session).
    /// </para>
    /// <para>
    /// <b>Bookkeeping, not a transition.</b> The status stays <c>uploading</c>, so — like
    /// <see cref="SetVetoNoticeEmailId"/> and <see cref="StartUpload"/>'s resume no-op — no domain event is raised and
    /// no stamp moves: the row's <c>updated_at</c> belongs to the transition that owns the run.
    /// </para>
    /// </remarks>
    /// <param name="protectedSessionUri">The Data-Protection ciphertext of the resumable session URI; never the URI.</param>
    /// <returns><c>Success</c>; or 409 <c>publication_invalid_state</c> for a row that is not <c>uploading</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="protectedSessionUri"/> is <see langword="null"/> or blank.</exception>
    public ErrorOr<Success> AttachUploadSession(string protectedSessionUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedSessionUri);

        if (Status != PublicationStatus.Uploading)
        {
            return PublicationErrors.InvalidState();
        }

        PlatformPayloadJson = PublicationPayload.WithKey(
            PlatformPayloadJson,
            PublicationPayload.UploadSessionEncKey,
            protectedSessionUri);

        // The create's outcome is now recorded, so the claim that preceded it ends in the same write (SEC-8).
        ClearUploadSessionLease();

        return Result.Success;
    }

    /// <summary>
    /// Claims this row's next <c>videos.insert</c> session create for one run (plan §Performance, SEC-8): stamps
    /// <c>platform_payload.uploadCreateIssuedAt</c> — the persistent "a create has been issued against this
    /// reservation" fact the release rule reads — and a lease until <paramref name="leaseUntil"/>, so a second run
    /// backs off instead of opening a second session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a lock.</b> The two keys are written in a short transaction that commits <em>before</em> the Google call
    /// and hold nothing across it: the row lock of a transaction is not involved, and a run that dies holding the lease
    /// blocks nobody for longer than the create's own timeout plus the caller's margin.
    /// </para>
    /// <para>
    /// <b>The caller reads the lease.</b> <see cref="UploadSessionLeaseUntil"/> is compared against the caller's clock;
    /// this member only writes what the caller computed, because the Domain may not read a timeout option.
    /// </para>
    /// </remarks>
    /// <param name="now">The job's clock reading: the stamp of the marker.</param>
    /// <param name="leaseUntil">When the claim expires if its outcome is never observed.</param>
    /// <returns>
    /// <c>Success</c>; or 409 <c>publication_invalid_state</c> for a row that is not <c>uploading</c>.
    /// </returns>
    public ErrorOr<Success> ClaimUploadSessionCreate(DateTimeOffset now, DateTimeOffset leaseUntil)
    {
        if (Status != PublicationStatus.Uploading)
        {
            return PublicationErrors.InvalidState();
        }

        var payload = PublicationPayload.WithInstant(
            PlatformPayloadJson,
            PublicationPayload.UploadCreateIssuedAtKey,
            now);

        PlatformPayloadJson = PublicationPayload.WithInstant(
            payload,
            PublicationPayload.UploadSessionLeaseKey,
            leaseUntil);

        return Result.Success;
    }

    /// <summary>
    /// Gives up this row's claim on the session create (<c>platform_payload.uploadSessionCreateLeaseUntil</c>):
    /// the outcome of the create was observed — attached, refused or lost — so the next attempt may claim again.
    /// </summary>
    /// <remarks>
    /// <b>The marker stays.</b> Ending the lease says "nobody is calling Google right now"; it says nothing about whether
    /// a call was made, which is what <see cref="UploadCreateIssuedAt"/> keeps until the reservation is given up.
    /// Removing an absent key leaves the document byte-identical.
    /// </remarks>
    public void ClearUploadSessionLease()
    {
        PlatformPayloadJson = PublicationPayload.WithoutKey(
            PlatformPayloadJson,
            PublicationPayload.UploadSessionLeaseKey);
    }

    /// <summary>
    /// Removes the stored resumable session URI (<c>platform_payload.uploadSessionEnc</c>) of a session that can no
    /// longer be resumed — the session expired and no new one replaced it, or the row reached a state in which
    /// nothing will ever probe it again. A resume that opens a new session calls
    /// <see cref="AttachUploadSession"/> instead, which overwrites the value.
    /// </summary>
    /// <remarks>
    /// The key is removed rather than stored as <c>null</c>, the same rule <see cref="QuotaReservedDay"/> follows.
    /// Bookkeeping only: no status move, no event, no stamp.
    /// </remarks>
    public void ClearUploadSession()
    {
        PlatformPayloadJson = PublicationPayload.WithoutKey(
            PlatformPayloadJson,
            PublicationPayload.UploadSessionEncKey);
    }

    /// <summary>
    /// The sticky plan the insert is built from (<c>platform_payload.upload</c>, plan §3 F13): the one already stored,
    /// or — while 06.2.4-T1 is not shipped and the row carries none — one derived from the row's own columns and
    /// <em>persisted here</em>, so every later attempt of this row sends the same request the first one planned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a plan at all.</b> The privacy and the scheduled instant are decided when the row is planned, not when
    /// the bytes flow: a pre-audit row goes to YouTube as <c>private</c> with no <c>publishAt</c>, and an approval
    /// that lands afterwards must not turn the same row into a public one mid-flight. Reading the live
    /// <see cref="Privacy"/>/<see cref="PublishAt"/> columns would do exactly that.
    /// </para>
    /// <para>
    /// <b>The fallback rule is 06.2.4-T1's, copied not invented</b> (its <c>PlanUpload</c>, 06.2.4 plan §4 UC1):
    /// pre-audit ⇒ <c>private</c> and no instant; <c>public</c> with an instant more than one minute ahead ⇒
    /// <c>private</c> plus that instant (YouTube's own scheduled-publishing shape); anything else ⇒ the column's
    /// privacy and no instant. When 06.2.4-T1 ships it writes the key at consent time and this derivation never
    /// runs again for that row.
    /// </para>
    /// <para>
    /// <b>Bookkeeping, not a transition.</b> No event, no stamp: the plan is an input of the upload, and the write
    /// rides in the caller's locked transaction.
    /// </para>
    /// </remarks>
    /// <param name="now">The job's clock reading, the instant the one-minute lead is measured from.</param>
    /// <returns>
    /// The plan to build the request from; or 409 <c>publication_invalid_state</c> when the row carries none and is
    /// not <c>scheduled</c>/<c>uploading</c>.
    /// </returns>
    public ErrorOr<PublicationPayload.UploadPlan> EnsureUploadPlan(DateTimeOffset now)
    {
        if (UploadPlan is { } stored)
        {
            return stored;
        }

        if (Status is not (PublicationStatus.Scheduled or PublicationStatus.Uploading))
        {
            return PublicationErrors.InvalidState();
        }

        var preAudit = PublicationPayload.ReadFlag(PlatformPayloadJson, PublicationPayload.PreAuditKey);
        var planned = PlanUploadPayload(preAudit, now);

        PlatformPayloadJson = PublicationPayload.WithUpload(PlatformPayloadJson, planned);

        return planned;
    }

    /// <summary>
    /// Whether a run other than the caller's currently owns this row's streaming window
    /// (<c>platform_payload.uploadStreamLeaseUntil</c>, SEC-4). A lease whose instant has passed is stale and reads
    /// as "nobody", which is how a run that died releases its window without writing anything.
    /// </summary>
    /// <param name="now">The caller's clock reading.</param>
    /// <returns>Whether a live lease is held.</returns>
    public bool HasLiveUploadStreamLease(DateTimeOffset now) =>
        UploadStreamLeaseUntil is { } until && until > now;

    /// <summary>
    /// Claims this row's streaming window for one run (plan §Performance, SEC-4): the owner id and the instant the
    /// claim expires, written together, so a second run can see that bytes are already flowing into this row's
    /// session and write nothing instead of streaming into the same session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refused while another run's lease is live.</b> A lease that has passed is stale — the run that held it
    /// stopped renewing — and is taken over by the caller, which is the only recovery a crashed run needs.
    /// </para>
    /// <para>
    /// <b>Not a lock.</b> The two keys are written in a short transaction that holds no row lock across the stream;
    /// the caller stamps this in the same transaction that claims the session create, before the stream opens.
    /// </para>
    /// </remarks>
    /// <param name="owner">The calling run's own id, the value every later renew and release compares against.</param>
    /// <param name="now">The job's clock reading.</param>
    /// <param name="leaseUntil">When the claim expires if it is never renewed.</param>
    /// <returns>
    /// <c>Success</c>; or 409 <c>publication_invalid_state</c> for a row that is not <c>uploading</c> and for a live
    /// lease another run holds.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="owner"/> is <see cref="Guid.Empty"/>.</exception>
    public ErrorOr<Success> ClaimUploadStreamLease(Guid owner, DateTimeOffset now, DateTimeOffset leaseUntil)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(owner, Guid.Empty);

        if (Status != PublicationStatus.Uploading)
        {
            return PublicationErrors.InvalidState();
        }

        if (UploadStreamLeaseOwner is { } holder && holder != owner && HasLiveUploadStreamLease(now))
        {
            return PublicationErrors.InvalidState();
        }

        var payload = PublicationPayload.WithKey(
            PlatformPayloadJson,
            PublicationPayload.UploadStreamLeaseOwnerKey,
            owner.ToString("D", CultureInfo.InvariantCulture));

        PlatformPayloadJson = PublicationPayload.WithInstant(
            payload,
            PublicationPayload.UploadStreamLeaseUntilKey,
            leaseUntil);

        return Result.Success;
    }

    /// <summary>
    /// Pushes this row's streaming lease out while the bytes still flow (SEC-4): a run that is streaming keeps the
    /// window, and a run that stopped renewing lets it expire.
    /// </summary>
    /// <param name="owner">The calling run's own id.</param>
    /// <param name="leaseUntil">The new expiry, as the caller computed it.</param>
    /// <returns>
    /// Whether the write happened: a lease another run now owns is left alone, which is what stops a late writer
    /// from re-taking a window it lost.
    /// </returns>
    public bool RenewUploadStreamLease(Guid owner, DateTimeOffset leaseUntil)
    {
        if (UploadStreamLeaseOwner != owner)
        {
            return false;
        }

        PlatformPayloadJson = PublicationPayload.WithInstant(
            PlatformPayloadJson,
            PublicationPayload.UploadStreamLeaseUntilKey,
            leaseUntil);

        return true;
    }

    /// <summary>
    /// Gives up this row's streaming window (<see cref="UploadStreamLeaseUntil"/>) when the calling run still owns
    /// it: the outcome of the stream is being recorded in the same transaction, so the next attempt may stream.
    /// </summary>
    /// <param name="owner">The calling run's own id.</param>
    public void ReleaseUploadStreamLease(Guid owner)
    {
        if (UploadStreamLeaseOwner != owner)
        {
            return;
        }

        ClearUploadStreamLease();
    }

    /// <summary>
    /// Removes the streaming lease (<c>uploadStreamLeaseOwner</c> and <c>uploadStreamLeaseUntil</c>) whatever it
    /// says. Used by the transitions a stream cannot outlive — every outcome of the row — so a late release by a run
    /// that already lost the lease cannot leave the keys behind on a terminal row.
    /// </summary>
    public void ClearUploadStreamLease()
    {
        var payload = PublicationPayload.WithoutKey(
            PlatformPayloadJson,
            PublicationPayload.UploadStreamLeaseOwnerKey);

        PlatformPayloadJson = PublicationPayload.WithoutKey(
            payload,
            PublicationPayload.UploadStreamLeaseUntilKey);
    }

    /// <summary>
    /// The plan one row implies when nothing has planned it yet: 06.2.4-T1's <c>PlanUpload</c> rule, applied to the
    /// columns this aggregate already holds (see <see cref="EnsureUploadPlan"/> for why it lives here).
    /// </summary>
    /// <param name="preAudit">Whether 06.2.1-T1 stamped the row pre-audit.</param>
    /// <param name="now">The instant the one-minute lead is measured from.</param>
    /// <returns>The plan to store.</returns>
    private PublicationPayload.UploadPlan PlanUploadPayload(bool preAudit, DateTimeOffset now)
    {
        if (preAudit)
        {
            // An unaudited API project only publishes private videos, and a scheduled instant is refused: the row is
            // private whatever the submitted privacy was (F13, 06 README D12).
            return new PublicationPayload.UploadPlan(UploadPlanPrivate, null, PreAudit: true);
        }

        if (!string.Equals(Privacy, UploadPlanPublic, StringComparison.Ordinal))
        {
            // Private/unlisted: YouTube takes the visibility as it is, and a scheduled instant only means something
            // for a public video (06.2.4-T1's G3 fix).
            return new PublicationPayload.UploadPlan(Privacy, null, PreAudit: false);
        }

        if (PublishAt is { } publishAt && publishAt > now.Add(UploadPlanLeadTime))
        {
            // YouTube's scheduled publishing: private now, public at the instant. An instant closer than the lead
            // time is not a schedule anymore, so it is dropped instead of refused at insert time.
            return new PublicationPayload.UploadPlan(UploadPlanPrivate, publishAt, PreAudit: false);
        }

        return new PublicationPayload.UploadPlan(Privacy, null, PreAudit: false);
    }

    /// <summary>The <c>public</c> wire value of <c>publications.privacy</c> (G6).</summary>
    private const string UploadPlanPublic = "public";

    /// <summary>The <c>private</c> wire value of <c>publications.privacy</c> (G6), and the one a scheduled insert uses.</summary>
    private const string UploadPlanPrivate = "private";

    /// <summary>
    /// How far ahead a scheduled instant must be to travel with the insert (06.2.4-T1's boundary): an instant this
    /// close is "as soon as possible", which YouTube expresses by omitting <c>publishAt</c>.
    /// </summary>
    private static readonly TimeSpan UploadPlanLeadTime = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Records the Pacific quota day the row's unit is reserved on (<c>platform_payload.quotaReservedDay</c>, plan
    /// §3.2, 06 README D10) — the key 06.2.6-T1's cancel reads to give the unit back, and the signal a resumed run
    /// uses to skip a second reservation (YR27-2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Server-only, one day.</b> The value is the ISO <c>yyyy-MM-dd</c> Pacific calendar date the caller computed
    /// (<c>PacificQuotaDay.Day</c>); it is never derived here, because the Domain may not read a time zone of the
    /// platform's quota. The key is written at the reserve step, in the same transaction as the
    /// <c>scheduled → uploading</c> move (STATUS S72-1), and removed only by <see cref="MarkProcessing"/> (the upload
    /// reached the platform) or <see cref="ClearQuotaReservation"/> (the handler's release branch) — never rewritten to
    /// another day. <see cref="FailUpload"/> deliberately <em>keeps</em> it: the unit belongs to the handler's release
    /// branch, the only place that knows whether this run reserved it (YR19/YR27-2).
    /// </para>
    /// <para>
    /// <b>Bookkeeping, not a transition.</b> No event and no stamp: the reserve never changes what a consumer does,
    /// and the row is about to move through <see cref="StartUpload"/> in the same commit anyway.
    /// </para>
    /// </remarks>
    /// <param name="quotaDay">The Pacific calendar date of the reservation, as the ledger recorded it.</param>
    /// <returns>
    /// <c>Success</c>; or 409 <c>publication_invalid_state</c> for a row that can no longer reserve — anything but
    /// <c>scheduled</c> (the reserve that precedes the upload) or <c>uploading</c> (the restart that reserves again,
    /// YR27-3).
    /// </returns>
    public ErrorOr<Success> MarkQuotaReserved(DateOnly quotaDay)
    {
        if (Status is not (PublicationStatus.Scheduled or PublicationStatus.Uploading))
        {
            return PublicationErrors.InvalidState();
        }

        PlatformPayloadJson = PublicationPayload.WithKey(
            PlatformPayloadJson,
            PublicationPayload.QuotaReservedDayKey,
            PublicationPayload.FormatQuotaDay(quotaDay));

        // A fresh unit buys a fresh insert: nothing was issued against it yet, and no run holds its claim (YR27-3's
        // restart reserves again before it opens the new session).
        ClearQuotaReservationMarker();
        ClearUploadSessionLease();
        ClearUploadStreamLease();

        return Result.Success;
    }

    /// <summary>
    /// Gives up the row's claim on a reserved quota unit (<c>platform_payload.quotaReservedDay</c>): the unit was
    /// released to the ledger, or the upload finished, so the key is <em>removed</em> and a later cancel can no longer
    /// release the same unit twice (06.2.6-T1's contract, STATUS S72-1).
    /// </summary>
    /// <remarks>
    /// Unguarded on purpose: the release runs on the failure, quota and cancel paths alike, and a row with nothing
    /// reserved is exactly the state this call produces. Removing an absent key leaves the document byte-identical.
    /// </remarks>
    public void ClearQuotaReservation()
    {
        var payload = PublicationPayload.WithoutKey(
            PlatformPayloadJson,
            PublicationPayload.QuotaReservedDayKey);

        // The marker describes one reservation ("a create was issued against this unit"), so it goes with it (YR19):
        // a row that holds no day must never look like one whose insert Google counted.
        PlatformPayloadJson = PublicationPayload.WithoutKey(
            payload,
            PublicationPayload.UploadCreateIssuedAtKey);

        // The reservation and the streaming window end together: the run that released the unit is done with the row.
        ClearUploadStreamLease();
    }

    /// <summary>Removes the <c>uploadCreateIssuedAt</c> marker alone, without touching the day that names the unit.</summary>
    private void ClearQuotaReservationMarker()
    {
        PlatformPayloadJson = PublicationPayload.WithoutKey(
            PlatformPayloadJson,
            PublicationPayload.UploadCreateIssuedAtKey);
    }

    /// <summary>
    /// Moves an uploaded video into <see cref="PublicationStatus.Processing"/> (plan §3.1, 06 README D9/D12): YouTube
    /// accepted the upload and is transcoding it, so the row names the video, drops every retry stamp and hands the
    /// poller its claim window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it writes.</b> <c>external_post_id</c> and the watch URL built from it (06 CR-1),
    /// <c>next_attempt_at = NULL</c>, <c>error_code = NULL</c>, the processing block
    /// <c>{ startedAt: now, deadlineAt: now + maxMinutes, polls: 0 }</c> 06.2.5-T1's poller claims the row on, and the
    /// <em>removal</em> of <c>quotaReservedDay</c> — the upload was received, so the unit is spent — and of
    /// <c>heldByIncidentId</c>, because a row that finished its upload is no longer held. The session URI is kept:
    /// nothing here needs it any more, and dropping it is not this transition's decision.
    /// </para>
    /// <para>
    /// <b>Why <paramref name="maxMinutes"/> comes in.</b> The deadline is <c>YouTube:Poll:MaxMinutes</c>, an option
    /// the Domain may not read, so the caller passes it; the Domain owns only the addition.
    /// </para>
    /// <para>
    /// <b>Exactly once.</b> The guard is strict — <c>uploading</c> only — so a duplicate enqueue of the same run
    /// cannot rewrite the deadline of a row the poller is already watching; that duplicate leaves through the job's
    /// own status guard. The reconcile path (a run whose last response was lost) is
    /// <see cref="MarkUploadedExternal"/>, which is idempotent for the same video id.
    /// </para>
    /// </remarks>
    /// <param name="videoId">YouTube's id of the uploaded video.</param>
    /// <param name="now">The job's clock reading: the processing start and the event's stamp.</param>
    /// <param name="maxMinutes"><c>YouTube:Poll:MaxMinutes</c>: how long the poller may claim the row.</param>
    /// <returns>
    /// <c>Success</c>; or 409 <c>publication_invalid_state</c> from any state but <c>uploading</c> (including the
    /// terminal states and <c>processing</c> itself).
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="videoId"/> is <see langword="null"/> or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxMinutes"/> is not positive.</exception>
    public ErrorOr<Success> MarkProcessing(string videoId, DateTimeOffset now, int maxMinutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMinutes);

        if (Status != PublicationStatus.Uploading)
        {
            return PublicationErrors.InvalidState();
        }

        MoveToProcessing(videoId, now, maxMinutes);

        return Result.Success;
    }

    /// <summary>
    /// Records that YouTube already holds the video of this row (plan §3.1, 06 README D9/F02): the reconcile path of a
    /// run that found <c>external_post_id</c> set, or whose probe answered <c>Complete(videoId)</c>, and therefore
    /// must never upload a second time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Same write as <see cref="MarkProcessing"/>, wider door.</b> A row that was <c>uploading</c> when its last
    /// response was lost is reconciled here, and so is one that is already <c>processing</c> — the latter is a no-op
    /// success when the id matches (a redelivered job must not extend the poller's deadline) and a refusal when it
    /// does not, because a different id means two videos and that is not a state a retry may paper over.
    /// </para>
    /// <para>
    /// Terminal rows are refused, never silently accepted: a <c>failed</c>/<c>published</c>/<c>cancelled</c> row that
    /// suddenly claims an upload would break D13's promise that those states are final for this row.
    /// </para>
    /// </remarks>
    /// <param name="videoId">YouTube's id of the video the platform already holds.</param>
    /// <param name="now">The job's clock reading: the processing start and the event's stamp.</param>
    /// <param name="maxMinutes"><c>YouTube:Poll:MaxMinutes</c>: how long the poller may claim the row.</param>
    /// <returns>
    /// <c>Success</c> (including the idempotent repeat on a <c>processing</c> row with the same id); or 409
    /// <c>publication_invalid_state</c> for a terminal row or a <c>processing</c> row naming another video.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="videoId"/> is <see langword="null"/> or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxMinutes"/> is not positive.</exception>
    public ErrorOr<Success> MarkUploadedExternal(string videoId, DateTimeOffset now, int maxMinutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMinutes);

        if (Status == PublicationStatus.Processing)
        {
            return string.Equals(ExternalPostId, videoId, StringComparison.Ordinal)
                ? Result.Success
                : PublicationErrors.InvalidState();
        }

        if (Status != PublicationStatus.Uploading)
        {
            return PublicationErrors.InvalidState();
        }

        MoveToProcessing(videoId, now, maxMinutes);

        return Result.Success;
    }

    /// <summary>
    /// Sends the row back to <see cref="PublicationStatus.Scheduled"/> with the reason and the instant of its next
    /// attempt (plan §3.1, 06 README D10, YR3): the quota-exhausted reschedule of UC1 step 6, and any other failure the
    /// failure map classifies as retryable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It keeps what a retry needs.</b> <c>platform_payload.uploadSessionEnc</c> survives — an open session must
    /// resume instead of re-uploading (plan §Failure handling: "quota exhausted with a session already open") — and so
    /// does <c>quotaReservedDay</c>, because releasing the unit is the handler's decision, made through
    /// <see cref="ClearQuotaReservation"/> on exactly the branches D10 allows.
    /// </para>
    /// <para>
    /// <b>It wakes the row.</b> The raised <see cref="PublicationUpdated"/> carries <c>scheduled</c>, and
    /// <c>PublicationScheduledForUploadHandler</c> re-reads <c>next_attempt_at</c> off this row and schedules
    /// <c>PublishYouTubeJob</c> there (UC4). Without that path a rescheduled row would sit <c>scheduled</c> with
    /// nothing to wake it (TL-flagged 2026-09-25).
    /// </para>
    /// <para>
    /// <b>The Google reason is diagnostic.</b> <paramref name="googleReason"/> lands in
    /// <c>platform_payload.lastGoogleReason</c> and nowhere else — never in <c>error_code</c> (that is
    /// <paramref name="errorCode"/>'s vocabulary), never in the outbox payload. A <see langword="null"/> or blank
    /// reason leaves the stored one as it was, so an attempt Google answered nothing to cannot erase the last reason
    /// it did give.
    /// </para>
    /// </remarks>
    /// <param name="errorCode">The catalogued reason the attempt failed, from <see cref="PublicationErrorCodes"/>.</param>
    /// <param name="nextAttemptAt">
    /// The instant the retry is due, jittered by the caller (plan §7: the next Pacific midnight plus 0–10 minutes);
    /// stored as given so a retry does not re-jitter into a different slot.
    /// </param>
    /// <param name="googleReason">The raw, already sanitised Google <c>reason</c>, or <see langword="null"/>.</param>
    /// <param name="now">The job's clock reading: the stamp and the event's time.</param>
    /// <returns><c>Success</c>; or 409 <c>publication_invalid_state</c> for a row that is not <c>uploading</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="errorCode"/> is <see langword="null"/> or blank.</exception>
    public ErrorOr<Success> RescheduleUpload(
        string errorCode,
        DateTimeOffset nextAttemptAt,
        string? googleReason,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        if (Status != PublicationStatus.Uploading)
        {
            return PublicationErrors.InvalidState();
        }

        Status = PublicationStatus.Scheduled;
        ErrorCode = errorCode;
        NextAttemptAt = AsUtcInstant(nextAttemptAt);
        PlatformPayloadJson = WithGoogleReason(googleReason);
        UpdatedAt = now;

        // The run that failed here is no longer streaming, so its window ends with the verdict (SEC-4).
        ClearUploadStreamLease();

        Raise(new PublicationUpdated(Id, EpisodeId, UserId, Status, ErrorCode, now));

        return Result.Success;
    }

    /// <summary>
    /// Ends the publication in <see cref="PublicationStatus.Failed"/> (plan §3.1, §7): the reason no retry can fix —
    /// <see cref="PublicationErrorCodes.ChannelReauthRequired"/>, <see cref="PublicationErrorCodes.Rejected"/>, or
    /// <see cref="PublicationErrorCodes.UploadFailed"/> once the attempts are spent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Terminal for this row.</b> <c>next_attempt_at</c> is cleared and <c>heldByIncidentId</c> goes with it. The
    /// reserved day is <em>kept</em>: the unit belongs to the handler's release branch, the only place that knows
    /// whether this run reserved it (YR19) — a run that failed on a resume never reserved one, so dropping the key here
    /// would hand the next run a second reservation for a session that already holds the first (YR27-2). The row leaves
    /// the partial unique, so publishing the episode again is a new row, never a revival of this one.
    /// </para>
    /// <para>
    /// <b>The repeat is a no-op success.</b> A retried run whose commit was lost reports the same failure again; when
    /// the row is already <c>failed</c> with the same <paramref name="errorCode"/>, this writes nothing — no stamp, no
    /// second <see cref="PublicationUpdated"/>. A <em>different</em> code on a failed row is a refusal: the first
    /// verdict stands.
    /// </para>
    /// </remarks>
    /// <param name="errorCode">The catalogued reason, from <see cref="PublicationErrorCodes"/>.</param>
    /// <param name="googleReason">The raw, already sanitised Google <c>reason</c>, or <see langword="null"/>.</param>
    /// <param name="now">The job's clock reading: the stamp and the event's time.</param>
    /// <returns>
    /// <c>Success</c> (including the repeat of the same verdict); or 409 <c>publication_invalid_state</c> for a row
    /// that is not <c>uploading</c> and is not already failed with that code.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="errorCode"/> is <see langword="null"/> or blank.</exception>
    public ErrorOr<Success> FailUpload(string errorCode, string? googleReason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        if (Status == PublicationStatus.Failed)
        {
            return string.Equals(ErrorCode, errorCode, StringComparison.Ordinal)
                ? Result.Success
                : PublicationErrors.InvalidState();
        }

        if (Status != PublicationStatus.Uploading)
        {
            return PublicationErrors.InvalidState();
        }

        var payload = WithGoogleReason(googleReason);
        payload = PublicationPayload.WithoutKey(payload, PublicationPayload.HeldByIncidentIdKey);

        Status = PublicationStatus.Failed;
        ErrorCode = errorCode;
        NextAttemptAt = null;
        PlatformPayloadJson = payload;
        UpdatedAt = now;

        // Nothing will stream this row again, so the streaming window ends with it (SEC-4).
        ClearUploadStreamLease();

        Raise(new PublicationUpdated(Id, EpisodeId, UserId, Status, ErrorCode, now));

        return Result.Success;
    }

    /// <summary>
    /// Holds the row while an open <see cref="PlatformIncident"/> blocks its OAuth client (plan §3.1 UC4, YR24): back
    /// to <see cref="PublicationStatus.Scheduled"/> with <see cref="PublicationErrorCodes.PlatformIncident"/>, the
    /// incident's id in <c>platform_payload.heldByIncidentId</c> and a <em>deferred</em> next attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deferred, never <c>null</c>.</b> YR24 fixed a hot loop here: with no <c>next_attempt_at</c> the re-enqueue
    /// handler read the row as a fresh consent and scheduled it for roughly "now", so the job held it again
    /// immediately. The caller passes <c>now + 5 × Poll:IntervalSeconds</c>, and the raised
    /// <see cref="PublicationUpdated"/> makes <c>PublicationScheduledForUploadHandler</c> schedule the re-check at that
    /// instant — a bounded lazy poll that either finds the incident cleared or holds again with a fresh instant.
    /// </para>
    /// <para>
    /// <b>Nothing is released.</b> A hold is not a failure: the ledger is untouched here, the open session survives so
    /// the resumed run continues from its offset, and so does <c>quotaReservedDay</c>. A hold is also per row, on the
    /// row the caller already holds the lock on — there is no sweep of the client's other publications.
    /// </para>
    /// </remarks>
    /// <param name="incidentId">The id <c>IPlatformIncidentStore.RaiseAsync</c> answered.</param>
    /// <param name="nextAttemptAt">The deferred instant the row re-checks the incident at; never <c>null</c>.</param>
    /// <param name="now">The job's clock reading: the stamp and the event's time.</param>
    /// <returns><c>Success</c>; or 409 <c>publication_invalid_state</c> for a row that is not <c>uploading</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="incidentId"/> is <see cref="Guid.Empty"/>.</exception>
    public ErrorOr<Success> HoldForIncident(Guid incidentId, DateTimeOffset nextAttemptAt, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(incidentId, Guid.Empty);

        if (Status != PublicationStatus.Uploading)
        {
            return PublicationErrors.InvalidState();
        }

        Status = PublicationStatus.Scheduled;
        ErrorCode = PublicationErrorCodes.PlatformIncident;
        NextAttemptAt = AsUtcInstant(nextAttemptAt);
        PlatformPayloadJson = PublicationPayload.WithKey(
            PlatformPayloadJson,
            PublicationPayload.HeldByIncidentIdKey,
            incidentId.ToString());
        UpdatedAt = now;

        // The hold ends this run's stream; the held row is re-run by its deferred wake, which claims a new window.
        ClearUploadStreamLease();

        Raise(new PublicationUpdated(Id, EpisodeId, UserId, Status, ErrorCode, now));

        return Result.Success;
    }

    /// <summary>
    /// Releases the row from an incident hold (plan §3.1, YR27-4): clears <c>error_code</c> and
    /// <c>platform_payload.heldByIncidentId</c>, sets the next attempt the caller chose — a jittered instant for a
    /// <c>scheduled</c> row, <c>now</c> for a <c>processing</c> one — and raises <see cref="PublicationUpdated"/> with
    /// the status the row keeps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the event matters.</b> 06.2.3-T3's clear command calls this on tracked entities instead of a set-based
    /// <c>UPDATE</c>: a raw statement raises no domain event, and without the event the outbox would never schedule
    /// the wake-up the clear is meant to cause (F6).
    /// </para>
    /// <para>
    /// <b>Only a held row.</b> A row without <c>heldByIncidentId</c> is refused rather than "released": the
    /// quota-reschedule path owns <c>scheduled</c> rows that carry another error code, and letting a clear command
    /// clear their reason and pull their retry forward would be a bug this guard turns into a 409. The status is not
    /// changed — the row is where the hold left it, and the poller keeps watching a <c>processing</c> row.
    /// </para>
    /// </remarks>
    /// <param name="nextAttemptAt">The instant the row is due again (jittered for <c>scheduled</c>, <c>now</c> for <c>processing</c>).</param>
    /// <param name="now">The command's clock reading: the stamp and the event's time.</param>
    /// <returns>
    /// <c>Success</c>; or 409 <c>publication_invalid_state</c> when the row is not held, or is in a state a hold never
    /// leaves it in.
    /// </returns>
    public ErrorOr<Success> ReleaseHold(DateTimeOffset nextAttemptAt, DateTimeOffset now)
    {
        if (HeldByIncidentId is null
            || Status is not (PublicationStatus.Scheduled or PublicationStatus.Processing))
        {
            return PublicationErrors.InvalidState();
        }

        PlatformPayloadJson = PublicationPayload.WithoutKey(
            PlatformPayloadJson,
            PublicationPayload.HeldByIncidentIdKey);
        ErrorCode = null;
        NextAttemptAt = AsUtcInstant(nextAttemptAt);
        UpdatedAt = now;

        Raise(new PublicationUpdated(Id, EpisodeId, UserId, Status, ErrorCode, now));

        return Result.Success;
    }

    /// <summary>
    /// The one write of the <c>uploading → processing</c> transition, shared by
    /// <see cref="MarkProcessing"/> and <see cref="MarkUploadedExternal"/>: the video id and its watch URL, every
    /// retry stamp cleared, the processing block written for the poller, and the reserved-day and hold keys removed.
    /// </summary>
    /// <param name="videoId">YouTube's id of the video.</param>
    /// <param name="now">The job's clock reading.</param>
    /// <param name="maxMinutes"><c>YouTube:Poll:MaxMinutes</c>.</param>
    private void MoveToProcessing(string videoId, DateTimeOffset now, int maxMinutes)
    {
        var payload = PublicationPayload.WithoutKey(PlatformPayloadJson, PublicationPayload.QuotaReservedDayKey);
        payload = PublicationPayload.WithoutKey(payload, PublicationPayload.HeldByIncidentIdKey);
        payload = PublicationPayload.WithoutKey(payload, PublicationPayload.UploadCreateIssuedAtKey);
        payload = PublicationPayload.WithoutKey(payload, PublicationPayload.UploadSessionLeaseKey);
        payload = PublicationPayload.WithoutKey(payload, PublicationPayload.UploadStreamLeaseUntilKey);
        payload = PublicationPayload.WithoutKey(payload, PublicationPayload.UploadStreamLeaseOwnerKey);

        ExternalPostId = videoId;
        ExternalUrl = WatchUrl(videoId);
        NextAttemptAt = null;
        ErrorCode = null;
        Status = PublicationStatus.Processing;
        PlatformPayloadJson = PublicationPayload.WithProcessing(
            payload,
            now,
            now.AddMinutes(maxMinutes),
            polls: 0);
        UpdatedAt = now;

        Raise(new PublicationUpdated(Id, EpisodeId, UserId, Status, ErrorCode, now));
    }

    /// <summary>
    /// The <c>next_attempt_at</c> value of one caller-supplied instant, in the form the column stores.
    /// </summary>
    /// <remarks>
    /// The instant is preserved and only its offset is dropped (<c>timestamp with time zone</c> refuses a non-zero
    /// offset). Callers compute the instant from a time-zone rule — the Pacific quota day's next midnight carries the
    /// offset in force at that instant — so the value must keep the instant it names, never be reinterpreted as local
    /// wall-clock time.
    /// </remarks>
    /// <param name="instant">The instant a caller named.</param>
    /// <returns>The same instant with a zero offset.</returns>
    private static DateTimeOffset AsUtcInstant(DateTimeOffset instant) => instant.ToUniversalTime();

    /// <summary>
    /// Writes <c>platform_payload.lastGoogleReason</c> when the caller has one, and leaves the document as it was when
    /// it has not: the key is the last reason Google gave, so an attempt with nothing to report must not erase it.
    /// </summary>
    /// <param name="googleReason">The raw, already sanitised Google <c>reason</c>, or <see langword="null"/>.</param>
    /// <returns>The document to store.</returns>
    private string? WithGoogleReason(string? googleReason) =>
        string.IsNullOrWhiteSpace(googleReason)
            ? PlatformPayloadJson
            : PublicationPayload.WithKey(PlatformPayloadJson, PublicationPayload.LastGoogleReasonKey, googleReason);

    /// <summary>The watch URL of a video id (06 CR-1: the row carries it so no DTO mapper builds one).</summary>
    /// <param name="videoId">YouTube's video id.</param>
    /// <returns>The public watch URL.</returns>
    private static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";
}
