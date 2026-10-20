namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// Why a series stopped producing (02.8.3 plan §3.6): stored in <c>autopilot_runs.pause_reason</c> and
/// <c>series.autopilot_pause_reason</c> as its snake_case wire value, guarded by the tables' pause-reason
/// <c>CHECK</c>s.
/// </summary>
/// <remarks>
/// <para>
/// Each member carries the trigger of the plan's table in its wire value and maps one-to-one to an alert copy key
/// <c>autopilot.pause.&lt;wire value&gt;</c>; <see cref="ChannelUnsupported"/> is the one exception, because plan
/// §3.6 row 6b names its copy key <c>autopilot.pause.channel_unsupported</c> while its wire value is
/// <c>autopilot_channel_unsupported</c>. The persistence configuration therefore maps that member explicitly: the
/// snake_case converter would derive <c>channel_unsupported</c>.
/// </para>
/// <para>
/// A run row records the reason of its own pause (<see cref="AutopilotRun.Pause"/>), and
/// <see cref="AutopilotRun.Fail"/> records <see cref="SlotMissed"/> on a missed slot (plan §3.4, §3.6 row 13).
/// The triggers themselves are 02.8.3-T4's.
/// </para>
/// </remarks>
public enum AutopilotPauseReason
{
    /// <summary>Two gate failures in a row for the series (row 1, <c>gate_failed_streak</c>, 02.8.2's streak reader).</summary>
    GateFailedStreak,

    /// <summary>The daily or monthly platform spend cap refused a dispatch (rows 2 and 3, <c>spend_cap_reached</c>).</summary>
    SpendCapReached,

    /// <summary>The run's own cost passed <c>Autopilot:MaxCostUsdPerRun</c> (row 4, <c>run_cost_cap_reached</c>).</summary>
    RunCostCapReached,

    /// <summary>The generation provider reports no funds (row 5, <c>provider_funds_exhausted</c>).</summary>
    ProviderFundsExhausted,

    /// <summary>The authorization's channel connection is not <c>active</c> (row 6, <c>channel_not_active</c>).</summary>
    ChannelNotActive,

    /// <summary>
    /// The authorization's channel connection is a TikTok one, which cannot schedule a publication (row 6b, wire
    /// <c>autopilot_channel_unsupported</c>).
    /// </summary>
    ChannelUnsupported,

    /// <summary>
    /// The OAuth client itself is refused, not the user's grant (row 7, <c>oauth_client_incident</c>); it clears
    /// when the incident clears.
    /// </summary>
    OauthClientIncident,

    /// <summary>The day's upload quota is spent; the owner resumes on or after the next PT midnight (row 8, <c>youtube_quota_exhausted</c>).</summary>
    YoutubeQuotaExhausted,

    /// <summary>The upload failed for a reason 06 does not retry (row 9, <c>upload_failed</c>).</summary>
    UploadFailed,

    /// <summary>The platform rejected the video (row 10, <c>video_rejected</c>).</summary>
    VideoRejected,

    /// <summary>The veto notice never reached the owner, so no consent can be written (row 11, <c>veto_notice_undelivered</c>).</summary>
    VetoNoticeUndelivered,

    /// <summary>Two consecutive runs ended <c>failed</c> for the same non-platform reason (row 12, <c>repeated_run_failures</c>).</summary>
    RepeatedRunFailures,

    /// <summary>
    /// Two consecutive slots were missed, which points at wrong estimates (row 13, <c>slot_missed</c>). The missed
    /// slot's own run records it on its row through <see cref="AutopilotRun.Fail"/>.
    /// </summary>
    SlotMissed,

    /// <summary>A pre-audit series reached its private-video limit (row 14, <c>pre_audit_private_limit</c>).</summary>
    PreAuditPrivateLimit,

    /// <summary>A report targets one of the series' episodes or publications (row 15, <c>content_reported</c>).</summary>
    ContentReported,

    /// <summary>The owner pressed pause; no alert is sent, because the owner is the one who did it (row 16, <c>owner_paused</c>).</summary>
    OwnerPaused,
}
