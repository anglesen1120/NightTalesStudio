using NightTales.Application.Common.Errors;
using NightTales.Application.Publishing;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The closed code vocabulary of the render and publish steps (plan §3.4–§3.6, §7.3): the error code, pause-reason
/// wire value or step name a <see cref="StepResult"/> carries for logs, metrics and the run's own row.
/// </summary>
/// <remarks>
/// Codes are the values the orchestrator's metrics and the run log tag on, so a free-text code would fall outside
/// every closed vocabulary there. Two of them are <see cref="AutopilotRun.SlotMissedReason"/> and the
/// <see cref="AutopilotPauseReason"/> wire values the plan's tables use, aliased here so a step never spells a
/// reason the alert copy does not know.
/// </remarks>
public static class AutopilotStepCodes
{
    /// <summary>The episode a run names is not in the caller's scope.</summary>
    public const string EpisodeMissing = "autopilot_episode_missing";

    /// <summary>
    /// The run's authorization names no channel, or names one this scope cannot read (S66-7 leaves
    /// <c>series_autopilot_authorizations.channel_connection_id</c> nullable).
    /// </summary>
    public const string ChannelMissing = "autopilot_channel_missing";

    /// <summary>An episode state the step cannot act on (plan §7.3 "409 → run failed").</summary>
    public const string EpisodeInvalidState = ErrorCodes.EpisodeInvalidState;

    /// <summary>The run's single render failed; a run never renders twice (plan §3.6, task §Per-run caps).</summary>
    public const string RenderFailed = "render_failed";

    /// <summary>
    /// 03.2.2-T2's keyword lint refused the render before a job was queued. `rendering` has no hold exit in plan
    /// §3.5, so the run ends <c>failed</c> with this diagnostic code (TL open item: a `rendering → waiting_owner`
    /// row would let the owner fix the story inside the run instead).
    /// </summary>
    public const string RenderKeywordsFlagged = "render_keywords_flagged";

    /// <summary>The video gate failed a check in live mode (plan §3.5 row <c>video_gate → gate_failed</c>).</summary>
    public const string GateFailed = "gate_failed";

    /// <summary>The run created no publication, or the row it attached cannot be read (plan §7.3).</summary>
    public const string PublicationMissing = "autopilot_publication_missing";

    /// <summary>A create the publication command refused for a reason a retry cannot fix (plan §7.3).</summary>
    public const string PublicationCreateFailed = "publication_create_failed";

    /// <summary>The publication was cancelled while the run still tracked it (plan §3.5 row into <c>veto_window</c>).</summary>
    public const string PublicationCancelled = "publication_cancelled";

    /// <summary>A publication that failed without one of the catalogued 06 codes.</summary>
    public const string PublicationFailed = "publication_failed";

    /// <summary>Plan §3.4's missed slot: the run ends <c>failed</c> with <c>pause_reason = slot_missed</c>.</summary>
    public const string SlotMissed = AutopilotRun.SlotMissedReason;

    /// <summary>Plan §3.6 row 6: the authorization's connection is not <c>active</c>, so the run pauses.</summary>
    public const string ChannelNotActive = ErrorCodes.ChannelNotActive;

    /// <summary>Plan §3.6 row 6b: the connection's platform cannot schedule (wire <c>autopilot_channel_unsupported</c>).</summary>
    public const string ChannelUnsupported = "autopilot_channel_unsupported";

    /// <summary>Plan §3.6 row 11: no veto notice within 30 minutes of the create, so the run pauses.</summary>
    public const string VetoNoticeUndelivered = "veto_notice_undelivered";

    /// <summary>The publication went live (plan §3.5 row <c>scheduled → published</c>).</summary>
    public const string Published = "published";

    /// <summary>The publication went live but stays private because the channel is pre-audit (plan §7.4).</summary>
    public const string PublishedPrivateLocked = "published_private_locked";

    /// <summary>The owner cancelled a publication the run still tracked (plan §3.5 row <c>veto_window → cancelled_by_owner</c>).</summary>
    public const string CancelledByOwner = "cancelled_by_owner";

    /// <summary>Plan §3.6 row 9: the upload failed for a reason 06 does not retry.</summary>
    public const string UploadFailed = "upload_failed";

    /// <summary>Plan §3.6 row 10: the platform rejected the video.</summary>
    public const string VideoRejected = "video_rejected";

    /// <summary>Plan §3.6 row 8: the day's upload quota is spent.</summary>
    public const string YoutubeQuotaExhausted = "youtube_quota_exhausted";
}

/// <summary>
/// Whether the channel a run publishes through is still unaudited, which is what pre-audit mode turns on
/// (plan §3.1 <c>pre_audit_mode</c>, §7.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a port.</b> 06 stores the audit status once per project (<c>YouTube:AuditStatus</c>, bound by
/// <see cref="YouTubeOptions"/>) and nowhere per channel, so a per-channel status has no row to read
/// yet. This port keeps the run's decision behind one seam: today the only implementation answers with the
/// project-wide value, and the day 06.2.5-T1 records an audit status per channel that implementation replaces it
/// without touching a step.
/// </para>
/// <para>
/// The publication command applies the same override itself (06 README D12: while the project is unaudited every
/// publication is stored <c>private</c> and the submitted value travels in <c>platform_payload</c>), so the run's
/// own choice only matters once the audit is approved — which is exactly the case this port answers differently.
/// </para>
/// </remarks>
public interface IChannelAuditPolicy
{
    /// <summary>Whether publications of this channel are still in the pre-audit regime.</summary>
    /// <param name="channelConnectionId">The connection the run publishes through.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> while the audit status is not <c>approved</c>.</returns>
    ValueTask<bool> IsPreAuditAsync(Guid channelConnectionId, CancellationToken ct);
}

/// <summary>
/// The project-wide audit status of <see cref="YouTubeOptions.AuditStatus"/> as the answer for every
/// channel (06 README D11: one Google client, one audit status; <c>pending</c> and <c>rejected</c> both behave as
/// pre-audit, plan §7.4's decision).
/// </summary>
/// <param name="options">The bound <c>YouTube</c> section, read per resolution so a configuration reload is seen.</param>
internal sealed class PlatformChannelAuditPolicy(YouTubeOptions options) : IChannelAuditPolicy
{
    /// <inheritdoc />
    public ValueTask<bool> IsPreAuditAsync(Guid channelConnectionId, CancellationToken ct) =>
        new(!string.Equals(
            options.AuditStatus,
            YouTubeAuditStatuses.Approved,
            StringComparison.Ordinal));
}

/// <summary>
/// Whether the owner's veto window closed without a veto, i.e. whether the autopilot consent of plan §3.5's row
/// <c>veto_window → scheduled</c> was written for a publication.
/// </summary>
/// <remarks>
/// <b>Why a port.</b> The write belongs to 06.2.2-T3's consent job, which does not exist yet (plan §7.2 leaves the
/// transition event-driven by <c>PublicationUpdated</c>). The run only observes it, so the observation sits behind
/// this seam: the shipped implementation reads the row's own <c>consent_at</c>, and 06.2.2-T3 replaces it with the
/// job's own check without touching the step.
/// </remarks>
public interface IAutopilotConsentGate
{
    /// <summary>Whether consent was written for a publication the run created.</summary>
    /// <param name="publication">The publication as the step read it.</param>
    /// <param name="now">The tick's clock reading.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> when the publication may be treated as <c>scheduled</c>.</returns>
    ValueTask<bool> IsConsentedAsync(
        AutopilotPublicationSnapshot publication,
        DateTimeOffset now,
        CancellationToken ct);
}

/// <summary>
/// Consent read from the publication row itself: the two consent verbs of 06.2.2-T1
/// (<see cref="Publication.Consent"/> and
/// <see cref="Publication.ConsentFromAuthorization"/>) both write <c>consent_at</c> and move the
/// row to <c>scheduled</c> in the same call, so the timestamp is the fact "consent was written" and the status is
/// its consequence.
/// </summary>
internal sealed class PublicationConsentGate : IAutopilotConsentGate
{
    /// <inheritdoc />
    public ValueTask<bool> IsConsentedAsync(
        AutopilotPublicationSnapshot publication,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(publication);

        return new(publication.ConsentAt is not null);
    }
}
