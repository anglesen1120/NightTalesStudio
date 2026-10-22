using ErrorOr;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Events;

namespace NightTales.Domain.Series;

/// <summary>
/// A series: one channel's consistent style (00 overview §6 <c>series</c>). The shell — the columns of
/// plan §3 — is 09.2.2-T1's; the behaviour (<see cref="Create"/>, <see cref="Update"/> and
/// <see cref="Archive"/>) is this task's.
/// </summary>
/// <remarks>
/// <para>
/// The rules the aggregate owns are the two <c>ErrorOr</c> outcomes a <c>201</c>/<c>200</c> request can
/// still hit: an archived series is never updated (409 <c>series_archived</c>) and a topic change is
/// refused while autopilot is enabled (409 <c>series_topic_locked</c>, 09 README D16/00 §6). Everything
/// the wire can check first — the 1–120 title, the voice language, the style-topic policy — is the
/// Application handlers' and their validator's (09.4.1 plan §4 U1/U2).
/// </para>
/// <para>
/// 02.8.3-T2 adds the autopilot state the aggregate owns: the settings document
/// (<see cref="ApplyAutopilotSettings"/>), the flag (<see cref="EnableAutopilot"/>,
/// <see cref="DisableAutopilot"/>), the current-authorization pointer
/// (<see cref="AcceptAutopilotAuthorization"/>, <see cref="SetCurrentAutopilotAuthorization"/>) and the
/// implicit disable of <see cref="Archive"/>, plus the pause state the owner's two buttons write
/// (<see cref="PauseAutopilot"/>, <see cref="ResumeAutopilot"/>). The revocations, audits and run moves those
/// calls imply are the Application commands' — this type owns only the state of the series row.
/// </para>
/// </remarks>
public sealed class Series : AggregateRoot, IUserOwned, IAuditable
{
    /// <summary>
    /// The catalogued code of "the series is archived" (09 README D17, 409). The catalogue constant is
    /// <c>NightTales.Application.Common.Errors.ErrorCodes.SeriesArchived</c>, which the Domain may not
    /// reference; the unit test <c>Series_Codes_MatchTheErrorCatalogue</c> keeps the two equal.
    /// </summary>
    public const string ArchivedCode = "series_archived";

    /// <summary>
    /// The catalogued code of "the topic cannot change while autopilot is enabled" (09 README D17,
    /// 409; the Domain half of <c>ErrorCodes.SeriesTopicLocked</c>).
    /// </summary>
    public const string TopicLockedCode = "series_topic_locked";

    /// <summary>
    /// The catalogued code of "the settings document is missing or invalid" (400; the Domain half of
    /// <c>ErrorCodes.AutopilotSettingsInvalid</c>). The <c>PUT /series/{id}/autopilot</c> route answers it from
    /// <see cref="Autopilot.AutopilotSettingsValidator"/>; this constant is the code
    /// <see cref="EnableAutopilot"/> answers when there is nothing to enable.
    /// </summary>
    public const string AutopilotSettingsInvalidCode = "autopilot_settings_invalid";

    private Series()
    {
    }

    public Guid UserId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public Topic Topic { get; private set; }

    public Language Language { get; private set; }

    public Aspect Aspect { get; private set; }

    /// <summary>FK <c>voices</c> (the 04 catalog).</summary>
    public string VoiceId { get; private set; } = string.Empty;

    /// <summary>FK <c>style_templates</c>.</summary>
    public Guid StyleTemplateId { get; private set; }

    public string? StoryPreset { get; private set; }

    /// <summary>Placeholder FK to <c>channel_connections</c>, whose constraint 06 adds.</summary>
    public Guid? DefaultChannelId { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool AutopilotEnabled { get; private set; }

    /// <summary>The <c>autopilot_settings</c> JSON document (02.8.3-T1 owns its shape).</summary>
    public string? AutopilotSettings { get; private set; }

    public Guid? CurrentAutopilotAuthorizationId { get; private set; }

    public DateTimeOffset? AutopilotPausedAt { get; private set; }

    public string? AutopilotPauseReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a series for <paramref name="userId"/> and raises <see cref="SeriesCreated"/>
    /// (09 README D16).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The voice, the style template and their cross-checks (language match, <c>StyleTopicPolicy</c>)
    /// are the caller's, because they read other rows: <c>CreateSeriesHandler</c> refuses them with the
    /// documented 400s before this factory runs (09.4.1 plan §4 U1, 03 CR-2).
    /// </para>
    /// <para>
    /// <c>Language</c> is written once, here: it cannot change afterwards (<see cref="SeriesPatch"/>
    /// carries no language, [DECISION] 09.4.1 plan §3).
    /// </para>
    /// </remarks>
    /// <param name="userId">The owner; the tenant filter of 09 README D10 compares it on every read.</param>
    /// <param name="draft">The validated request values.</param>
    /// <param name="now">The command's clock reading; the domain never reads the clock itself.</param>
    /// <returns>The new aggregate, tracked by the caller's context.</returns>
    public static Series Create(Guid userId, SeriesDraft draft, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var series = new Series
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Title = draft.Title.Trim(),
            Topic = draft.Topic,
            Language = draft.Language,
            Aspect = draft.Aspect,
            VoiceId = draft.VoiceId,
            StyleTemplateId = draft.StyleTemplateId,
            StoryPreset = draft.StoryPreset,
            CreatedAt = now,
            UpdatedAt = now,
        };

        series.Raise(new SeriesCreated(series.Id, userId, now));

        return series;
    }

    /// <summary>
    /// Applies the present fields of <paramref name="patch"/> (absent means unchanged, and a
    /// <see langword="null"/> <c>StoryPreset</c> clears the column).
    /// </summary>
    /// <remarks>
    /// The two outcomes this method can answer, in this order: an archived series is never updated
    /// (409 <see cref="ArchivedCode"/>), and a topic change is refused while autopilot is enabled
    /// (409 <see cref="TopicLockedCode"/>). The Application handler re-checks the voice and the style
    /// template after this call only for the fields the patch carries (09.4.1 plan §4 U2).
    /// </remarks>
    /// <param name="patch">The values the client sent; every member may be absent.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns><c>Success</c>, or the 409 the request must be answered with.</returns>
    public ErrorOr<Success> Update(SeriesPatch patch, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(patch);

        if (ArchivedAt is not null)
        {
            return Error.Conflict(ArchivedCode, "An archived series cannot be changed.");
        }

        if (patch.Topic is { } topic && topic != Topic && AutopilotEnabled)
        {
            return Error.Conflict(
                TopicLockedCode,
                "The topic cannot change while autopilot is enabled; turn autopilot off first.");
        }

        if (patch.Title is { } title)
        {
            Title = title.Trim();
        }

        if (patch.Topic is { } newTopic)
        {
            Topic = newTopic;
        }

        if (patch.Aspect is { } aspect)
        {
            Aspect = aspect;
        }

        if (patch.VoiceId is { } voiceId)
        {
            VoiceId = voiceId;
        }

        if (patch.StyleTemplateId is { } styleTemplateId)
        {
            StyleTemplateId = styleTemplateId;
        }

        // [DECISION] A null story preset clears the column; there is no "absent" for it on the wire
        // (09.4.1 plan §5: `null` for `storyPreset` clears it).
        StoryPreset = patch.StoryPreset;

        UpdatedAt = now;

        return Result.Success;
    }

    /// <summary>
    /// Archives the series: <c>archived_at</c> is set (00 overview §6), it disappears from
    /// <c>GET /series</c>, and its episodes stay readable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent: a second call answers <c>Success</c> without raising a second
    /// <see cref="SeriesArchived"/>, exactly like the <c>DELETE</c> the client can repeat.
    /// </para>
    /// <para>
    /// An archived series cannot publish, so archiving it with autopilot on disables it first — the same state
    /// <see cref="DisableAutopilot"/> leaves (§3.7: "DELETE /series/{id} with autopilot on implicitly disables
    /// it"). The authorization row of the series is revoked by the archive handler in the same transaction
    /// (<see cref="Autopilot.AutopilotAuthorizationRevokeReason.Disabled"/>); no such row exists for a series
    /// that was never enabled and nothing different is raised for it, so the event stream of a plain archive is
    /// unchanged.
    /// </para>
    /// </remarks>
    /// <param name="now">The command's clock reading.</param>
    /// <returns>Always <c>Success</c>: there is no state an archive can be refused from.</returns>
    public ErrorOr<Success> Archive(DateTimeOffset now)
    {
        if (ArchivedAt is not null)
        {
            return Result.Success;
        }

        DisableAutopilot(now);
        ArchivedAt = now;
        UpdatedAt = now;

        Raise(new SeriesArchived(Id, UserId, now));

        return Result.Success;
    }

    /// <summary>
    /// Announces the content version a manual save wrote (02.6.3 plan §5): <see cref="SeriesBibleUpdated"/> is
    /// raised so <c>series_bible_updated.v1</c> lands in the outbox of the save's own transaction, exactly as
    /// <see cref="SeriesCreated"/> does for a new series. No state of the aggregate changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The save is announced by this aggregate rather than by the bible row: <c>SeriesBible</c> is an
    /// <see cref="Entity"/> and the outbox interceptor collects the events of an <see cref="AggregateRoot"/>
    /// only. The bible row is already stored when the caller announces it, so no precondition of the save's
    /// own — the ETag, the limits, the approved episodes — is re-checked here.
    /// </para>
    /// <para>
    /// The two refusals are caller bugs, not states: the version of a saved bible is 1-based, and a save
    /// always has the user who made it.
    /// </para>
    /// </remarks>
    /// <param name="version">The version the save produced (<c>series_bibles.version</c>).</param>
    /// <param name="changedBy">The user who performed the save; the owner in phase P.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns>
    /// <c>Success</c>; a validation error when <paramref name="version"/> is below 1 or
    /// <paramref name="changedBy"/> is empty.
    /// </returns>
    public ErrorOr<Success> RaiseSeriesBibleUpdated(int version, Guid changedBy, DateTimeOffset now)
    {
        if (InvalidBibleAnnouncement(version, changedBy) is { } invalid)
        {
            return invalid;
        }

        Raise(new SeriesBibleUpdated(Id, UserId, version, changedBy, now));

        return Result.Success;
    }

    /// <summary>
    /// Stores the settings document the owner wrote (plan §3.1), unchanged.
    /// </summary>
    /// <remarks>
    /// The aggregate is a carrier here, exactly as it is for <c>title</c>: the shape and the ranges are
    /// <see cref="Autopilot.AutopilotSettingsValidator"/>'s single decision, taken by the command before this
    /// call, and re-serialising the document here would hide the document the owner actually wrote (its key
    /// order and any member this build does not know yet). Applying settings neither enables the autopilot nor
    /// revokes anything: the covered-field diff of plan §3.7 is the command's, because it also has to move the
    /// series' runs.
    /// </remarks>
    /// <param name="settingsJson">The validated document, stored verbatim.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <exception cref="ArgumentException"><paramref name="settingsJson"/> is empty or whitespace.</exception>
    public void ApplyAutopilotSettings(string settingsJson, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsJson);

        AutopilotSettings = settingsJson;
        UpdatedAt = now;
    }

    /// <summary>
    /// Switches autopilot on (plan §3.7) — the last step of <c>PUT /series/{id}/autopilot</c> with
    /// <c>enabled = true</c>, after that route has checked the legal acceptance, the re-auth, the settings, the
    /// current authorization and the channel.
    /// </summary>
    /// <remarks>
    /// The one state the aggregate refuses on its own is a series with no settings document at all: there is
    /// nothing to publish on, and answering <c>true</c> would leave a flag the planner has to re-check on every
    /// tick. Turning on an already enabled series changes nothing, not even <c>updated_at</c>, so a repeated
    /// <c>PUT enabled=true</c> does not bump the row's <c>xmin</c> (the ETag of the series, README D9).
    /// </remarks>
    /// <param name="now">The command's clock reading.</param>
    /// <returns><c>Success</c>, or 400 <see cref="AutopilotSettingsInvalidCode"/> when no settings are stored.</returns>
    public ErrorOr<Success> EnableAutopilot(DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(AutopilotSettings))
        {
            return Error.Validation(
                AutopilotSettingsInvalidCode,
                "Autopilot needs a settings document before it can be enabled.");
        }

        if (AutopilotEnabled)
        {
            return Result.Success;
        }

        AutopilotEnabled = true;
        UpdatedAt = now;

        return Result.Success;
    }

    /// <summary>
    /// Switches autopilot off and forgets everything that only matters while it is on: the current
    /// authorization pointer and the pause state (plan §3.6, §3.7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent, and it touches nothing when there is nothing to clear, so a repeated <c>PUT enabled=false</c>
    /// does not bump the row's <c>xmin</c> (the ETag of the series, README D9).
    /// </para>
    /// <para>
    /// Revoking the authorization <em>row</em> is the caller's step, in the same transaction: the row records
    /// why it stopped being current (<see cref="Autopilot.AutopilotAuthorizationRevokeReason.Disabled"/>), and
    /// the aggregate does not reach across to it. <see cref="Archive"/> calls this method for the same reason
    /// (an archived series cannot publish), so the archive handler revokes in the same transaction too.
    /// </para>
    /// </remarks>
    /// <param name="now">The command's clock reading.</param>
    public void DisableAutopilot(DateTimeOffset now)
    {
        if (!AutopilotEnabled
            && CurrentAutopilotAuthorizationId is null
            && AutopilotPausedAt is null
            && AutopilotPauseReason is null)
        {
            return;
        }

        AutopilotEnabled = false;
        CurrentAutopilotAuthorizationId = null;
        AutopilotPausedAt = null;
        AutopilotPauseReason = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Points the series at the authorization it currently runs on, or clears the pointer with
    /// <see langword="null"/> when the caller revoked it.
    /// </summary>
    /// <param name="id">The live <c>series_autopilot_authorizations</c> row, or <see langword="null"/>.</param>
    /// <param name="now">The command's clock reading.</param>
    public void SetCurrentAutopilotAuthorization(Guid? id, DateTimeOffset now)
    {
        CurrentAutopilotAuthorizationId = id;
        UpdatedAt = now;
    }

    /// <summary>
    /// Holds the series (<c>POST /series/{id}/autopilot/pause</c>, plan §3.6 row 15): <c>autopilot_paused_at</c> and
    /// <c>autopilot_pause_reason</c> are stamped, so the next tick's readiness check stops the series before it
    /// reserves anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Who owns what.</b> This method writes the two columns of the series row and nothing else. Pausing the
    /// series' open runs (<see cref="AutopilotRun.Pause"/>) and the <c>autopilot.paused</c> audit entry are the
    /// Application command's, because both reach rows this aggregate does not own; the audit keys themselves are
    /// <b>02.8.3-T4's</b> (§3.6: "Pause/resume keys are T4's"), so T2 writes no audit for this call.
    /// </para>
    /// <para>
    /// <b>Idempotent.</b> A repeated pause with the reason the row already carries changes nothing — not even
    /// <c>updated_at</c> — so a double click does not bump the row's <c>xmin</c>, which is the ETag of the
    /// autopilot (02 README D9). A pause with a <em>different</em> reason replaces the stored one: the last trigger
    /// that fired is the one the owner's banner explains (plan §3.6).
    /// </para>
    /// </remarks>
    /// <param name="reason">The plan §3.6 trigger; the owner's own button passes <see cref="Autopilot.AutopilotPauseReason.OwnerPaused"/>.</param>
    /// <param name="now">The command's clock reading.</param>
    public void PauseAutopilot(Autopilot.AutopilotPauseReason reason, DateTimeOffset now)
    {
        var wire = Autopilot.AutopilotPauseReasons.ToWire(reason);

        if (AutopilotPausedAt is not null
            && string.Equals(AutopilotPauseReason, wire, StringComparison.Ordinal))
        {
            return;
        }

        AutopilotPausedAt = now;
        AutopilotPauseReason = wire;
        UpdatedAt = now;
    }

    /// <summary>
    /// Lifts a pause (<c>POST /series/{id}/autopilot/resume</c>, plan §3.6 "Resume"): both pause columns are cleared,
    /// which is what lets the next tick plan slots again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The triggers are not re-checked here.</b> Plan §3.6 requires every row of its table to be re-evaluated
    /// before a resume is accepted, and that ladder is <b>02.8.3-T4's</b>: it needs the gate streak, the budget
    /// guard, the channel's status and 06's publication rows, none of which this aggregate may read. T2's resume
    /// command checks only what its own task lists — a current authorization and an active channel — and answers
    /// 409 <c>autopilot_pause_reason_active</c> from T4 once the ladder exists. This method therefore assumes the
    /// caller already decided the resume may happen.
    /// </para>
    /// <para>
    /// Idempotent: resuming a series that is not paused changes nothing, not even <c>updated_at</c>.
    /// </para>
    /// </remarks>
    /// <param name="now">The command's clock reading.</param>
    public void ResumeAutopilot(DateTimeOffset now)
    {
        if (AutopilotPausedAt is null && AutopilotPauseReason is null)
        {
            return;
        }

        AutopilotPausedAt = null;
        AutopilotPauseReason = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Accepts one authorization for the series (plan §3.7): the pointer moves to
    /// <paramref name="authorization"/> and <see cref="AutopilotAuthorizationAccepted"/> is raised so the
    /// acceptance lands in the outbox of the same transaction. No other state of the series changes —
    /// accepting the text is not enabling the autopilot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Revoking the row this one supersedes is the caller's step (the command knows the previous version and
    /// writes <see cref="Autopilot.AutopilotAuthorizationRevokeReason.Superseded"/>), for the reason
    /// <see cref="DisableAutopilot"/> gives: the aggregate does not reach across to a row it does not own.
    /// </para>
    /// <para>
    /// A row of another series or another owner is a caller bug, not a state: the accept command reads the
    /// previous version and the owner inside the series' own tenant filter, so a mismatch means the caller
    /// built the row from the wrong request.
    /// </para>
    /// </remarks>
    /// <param name="auth">The row the caller just created for this series and owner.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <exception cref="ArgumentNullException"><paramref name="auth"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The row belongs to another series or another owner, or it is already revoked.
    /// </exception>
    public void AcceptAutopilotAuthorization(
        Autopilot.SeriesAutopilotAuthorization auth,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(auth);

        if (auth.SeriesId != Id || auth.UserId != UserId)
        {
            throw new ArgumentException(
                "An accepted authorization must belong to this series and its owner.",
                nameof(auth));
        }

        if (auth.IsRevoked)
        {
            throw new ArgumentException(
                "A revoked authorization cannot become the current one.",
                nameof(auth));
        }

        CurrentAutopilotAuthorizationId = auth.Id;
        UpdatedAt = now;

        Raise(new AutopilotAuthorizationAccepted(UserId, Id, auth.Id, auth.Version, now));
    }

    /// <summary>
    /// The validation every bible announcement shares: a version below 1 or an empty actor is a caller bug,
    /// not a series state, so it is a validation error rather than a conflict.
    /// </summary>
    /// <param name="version">The announced version.</param>
    /// <param name="changedBy">The announced actor.</param>
    /// <returns>The validation error, or <c>null</c> when the announcement is well formed.</returns>
    private static Error? InvalidBibleAnnouncement(int version, Guid changedBy)
    {
        if (version < 1)
        {
            return Error.Validation("series_bible_version_invalid", "A series-bible version is 1-based.");
        }

        if (changedBy == Guid.Empty)
        {
            return Error.Validation("series_bible_changed_by_required", "A bible save needs the user who made it.");
        }

        return null;
    }
}

/// <summary>
/// The creation values of <see cref="Series.Create"/> (09.4.1 plan §3). Language is part of the draft
/// and not of <see cref="SeriesPatch"/>: it cannot change after creation ([DECISION] plan §3).
/// </summary>
/// <param name="Title">The series title, 1–120 characters after trimming (00 §6, 09 README D16).</param>
/// <param name="Topic">The narrative topic; a true-crime series needs a true-crime style template.</param>
/// <param name="Language">The content language of the whole series; immutable after creation.</param>
/// <param name="Aspect">The video aspect every episode inherits.</param>
/// <param name="VoiceId">The 04 catalog voice; its language must equal <paramref name="Language"/>.</param>
/// <param name="StyleTemplateId">The 03 catalog style template, checked against the topic.</param>
/// <param name="StoryPreset">The 02 prompt-catalog preset key, or <see langword="null"/>.</param>
public sealed record SeriesDraft(
    string Title,
    Topic Topic,
    Language Language,
    Aspect Aspect,
    string VoiceId,
    Guid StyleTemplateId,
    string? StoryPreset);

/// <summary>
/// The JSON-merge patch of <c>PATCH /series/{id}</c> (09.4.1 plan §3): an absent member leaves the
/// column as it is, and the one member that must be clearable — <c>storyPreset</c> — treats
/// <see langword="null"/> as "clear it".
/// </summary>
/// <param name="Title">The new title, or <see langword="null"/> to keep the current one.</param>
/// <param name="Topic">The new topic, or <see langword="null"/> to keep the current one.</param>
/// <param name="Aspect">The new aspect, or <see langword="null"/> to keep the current one.</param>
/// <param name="VoiceId">The new voice, or <see langword="null"/> to keep the current one.</param>
/// <param name="StyleTemplateId">The new style template, or <see langword="null"/> to keep the current one.</param>
/// <param name="StoryPreset">The new preset key; <see langword="null"/> clears the column.</param>
public sealed record SeriesPatch(
    string? Title,
    Topic? Topic,
    Aspect? Aspect,
    string? VoiceId,
    Guid? StyleTemplateId,
    string? StoryPreset);
