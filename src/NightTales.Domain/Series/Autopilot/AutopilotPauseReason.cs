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

/// <summary>
/// The stored spelling of <see cref="AutopilotPauseReason"/> (02.8.3 plan §3.6), for the one writer that lives in the
/// Domain: <see cref="Series.PauseAutopilot"/> stamps <c>series.autopilot_pause_reason</c>, a text column, so the
/// aggregate has to spell the value itself.
/// </summary>
/// <remarks>
/// <para>
/// The members are spelled out one by one rather than derived from the member name, because one of them is not the
/// snake_case of its name: <see cref="AutopilotPauseReason.ChannelUnsupported"/> stores
/// <c>autopilot_channel_unsupported</c> (plan §3.6 row 6b), which is also why the persistence converter carries a
/// table of its own. Both tables must list the same values; the closed set is the
/// <c>ck_autopilot_runs_pause_reason</c> check constraint (02.8.3-T1).
/// </para>
/// <para>
/// A member this table does not name throws: a reason added to the enum without its stored form must fail loudly at
/// the call site rather than write a value the <c>CHECK</c> would refuse at commit (SEC-8's rule, 02.8.2-T1).
/// </para>
/// </remarks>
public static class AutopilotPauseReasons
{
    /// <summary>The stored form of one reason.</summary>
    /// <param name="reason">The reason to spell.</param>
    /// <returns>The snake_case wire value the column and its check constraint carry.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not a declared member.</exception>
    public static string ToWire(this AutopilotPauseReason reason) => reason switch
    {
        AutopilotPauseReason.GateFailedStreak => "gate_failed_streak",
        AutopilotPauseReason.SpendCapReached => "spend_cap_reached",
        AutopilotPauseReason.RunCostCapReached => "run_cost_cap_reached",
        AutopilotPauseReason.ProviderFundsExhausted => "provider_funds_exhausted",
        AutopilotPauseReason.ChannelNotActive => "channel_not_active",
        AutopilotPauseReason.ChannelUnsupported => "autopilot_channel_unsupported",
        AutopilotPauseReason.OauthClientIncident => "oauth_client_incident",
        AutopilotPauseReason.YoutubeQuotaExhausted => "youtube_quota_exhausted",
        AutopilotPauseReason.UploadFailed => "upload_failed",
        AutopilotPauseReason.VideoRejected => "video_rejected",
        AutopilotPauseReason.VetoNoticeUndelivered => "veto_notice_undelivered",
        AutopilotPauseReason.RepeatedRunFailures => "repeated_run_failures",
        AutopilotPauseReason.SlotMissed => "slot_missed",
        AutopilotPauseReason.PreAuditPrivateLimit => "pre_audit_private_limit",
        AutopilotPauseReason.ContentReported => "content_reported",
        AutopilotPauseReason.OwnerPaused => "owner_paused",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown autopilot pause reason."),
    };
}
