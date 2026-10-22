using NightTales.Domain.Publishing;

namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// The configured estimates plan §3.4 needs and neither the series nor the Domain owns: the upload lead time and
/// the minimum veto window from 06's <c>YouTubeOptions</c>, and the three placeholder estimates of
/// <c>Autopilot:*</c>. The Application binds them (config has no place in the Domain); a test passes them
/// literally.
/// </summary>
/// <remarks>
/// The two estimates are placeholders in the plan (<c>RenderEstimate:Minutes = 15</c>,
/// <c>StoryEstimate:Minutes = 10</c>) that must be replaced by the 05 render measurement and the M1 story p95
/// before the M1-autopilot gate (F16); nothing here hard-codes them. <see cref="MaxBufferDays"/> is the ceiling
/// the settings validator checks <c>buffer_days</c> against (plan §3.4: <c>Autopilot:MaxBufferDays = 7</c>) and
/// is not read by <see cref="AutopilotTiming.Compute"/>.
/// </remarks>
/// <param name="PublishLeadMinutes">The upload lead time before <c>slot_at</c> (<c>YouTubeOptions.PublishLeadMinutes</c>, 120).</param>
/// <param name="MinVetoWindowHours">The shortest veto window a late publication may have (<c>MinVetoWindowHours</c>, 2).</param>
/// <param name="PublicationCreateMarginMinutes">The notice-delivery margin before the publication is created.</param>
/// <param name="RenderEstimateMinutes">The render estimate used when the series sets no <c>render_lead_minutes</c>.</param>
/// <param name="StoryEstimateMinutes">The story estimate used when the series sets no <c>story_lead_minutes</c>.</param>
/// <param name="MaxBufferDays">The largest <c>buffer_days</c> a settings document may state.</param>
public sealed record AutopilotTimingOptions(
    int PublishLeadMinutes,
    int MinVetoWindowHours,
    int PublicationCreateMarginMinutes,
    int RenderEstimateMinutes,
    int StoryEstimateMinutes,
    int MaxBufferDays);

/// <summary>
/// The instants one slot's production must hit (plan §3.4). <see cref="StartAt"/> is where the run becomes due,
/// the other four are the milestones of the chain behind it.
/// </summary>
/// <param name="StartAt">The earliest safe start of the run: <c>min(story_start_at, slot_at − buffer_days)</c>.</param>
/// <param name="PublicationCreateAt">When the publication (and its veto notice) is created.</param>
/// <param name="UploadAt">When the private upload starts, <c>slot_at − PublishLead</c>.</param>
/// <param name="RenderStartAt">When the render must have started.</param>
/// <param name="StoryStartAt">When the story generation must have started.</param>
public sealed record AutopilotRunTiming(
    DateTimeOffset StartAt,
    DateTimeOffset PublicationCreateAt,
    DateTimeOffset UploadAt,
    DateTimeOffset RenderStartAt,
    DateTimeOffset StoryStartAt);

/// <summary>What the plan §3.4 missed-slot rule says about one run at one instant.</summary>
public enum VetoWindowDecision
{
    /// <summary>The publication may not be created yet: <c>now &lt; publication_create_at</c>.</summary>
    WaitUntil = 0,

    /// <summary>The publication may be created now; the veto window it gets is the series' configured one, clamped by the create command.</summary>
    Create = 1,

    /// <summary>
    /// <c>now ≥ upload_at − MinVetoWindowHours</c>: the slot is missed and the run ends <c>failed</c> with
    /// <c>pause_reason = slot_missed</c> (plan §3.4).
    /// </summary>
    SlotMissed = 2,
}

/// <summary>
/// The verdict of <see cref="AutopilotTiming.EvaluateVetoWindow"/>: which of the three branches the instant falls
/// in. It carries no veto hours — the window a created publication gets is the series' configured
/// <see cref="AutopilotSettings.VetoWindowHours"/> and belongs to <c>CreatePublicationCommand</c>, so a copy here
/// would be a second, discarded rule (STATUS S71-41).
/// </summary>
/// <param name="Decision">Whether to wait, create, or call the slot missed.</param>
public sealed record VetoWindowResult(VetoWindowDecision Decision)
{
    /// <summary>The publication is not due yet.</summary>
    public static VetoWindowResult WaitUntil { get; } = new(VetoWindowDecision.WaitUntil);

    /// <summary>The slot is gone.</summary>
    public static VetoWindowResult SlotMissed { get; } = new(VetoWindowDecision.SlotMissed);

    /// <summary>The publication may be created now.</summary>
    public static VetoWindowResult Create { get; } = new(VetoWindowDecision.Create);
}

/// <summary>
/// Plan §3.4's timing formulas, pure: one slot instant plus a settings document becomes the five milestones the
/// run state machine schedules, and the missed-slot rule becomes a closed verdict.
/// </summary>
/// <remarks>
/// <para>
/// The chain is: <c>upload_at = slot_at − PublishLead</c> (through <see cref="PublicationQueueTime.For"/>, which
/// clamps a slot already inside the lead window to the caller's clock reading),
/// <c>publication_create_at = upload_at − veto_window_hours</c>,
/// <c>render_deadline = publication_create_at − PublicationCreateMargin</c>,
/// <c>render_start_at = render_deadline − render_lead</c>,
/// <c>story_start_at = render_start_at − story_lead</c>,
/// <c>start_at = min(story_start_at, slot_at − buffer_days)</c>. The ready buffer usually wins, which is what
/// leaves the episode sitting <c>ready</c> for about 1.5 days before its publication exists; a sparse cadence
/// whose veto window is longer than the buffer falls back to <c>story_start_at</c> and a tight ~35 minute
/// production window.
/// </para>
/// <para>
/// <b>Missed slot.</b> A publication is created only while
/// <c>publication_create_at ≤ now &lt; upload_at − MinVetoWindowHours</c>; it never goes out before
/// <c>publication_create_at</c>. At or after the cutoff the slot is missed and the run ends <c>failed</c> with
/// <c>pause_reason = slot_missed</c> (two consecutive misses count as two failures for the pause streak, §3.6).
/// The window a created publication then gets is the series' configured one, decided by
/// <c>CreatePublicationCommand</c>: this type answers only which branch the instant falls in.
/// </para>
/// </remarks>
public static class AutopilotTiming
{
    /// <summary>Computes the five milestones of one slot.</summary>
    /// <param name="slotAt">The slot's publish instant (plan §3.2).</param>
    /// <param name="settings">The series' settings: veto window, buffer depth and the two optional lead overrides.</param>
    /// <param name="options">The configured estimates and lead times.</param>
    /// <param name="now">
    /// The caller's clock reading. It only matters for a slot already inside its lead window (a series enabled late,
    /// a backfill): <see cref="PublicationQueueTime.For"/> then answers <paramref name="now"/>, so the chain starts
    /// at the present instead of an upload instant that has already passed.
    /// </param>
    /// <returns>The milestones, all in UTC like <paramref name="slotAt"/>.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    public static AutopilotRunTiming Compute(
        DateTimeOffset slotAt,
        AutopilotSettings settings,
        AutopilotTimingOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);

        // The upload instant is D12's, derived through its one owner rather than spelled again here (06 README
        // YR3; STATUS S71-41), which is also where the "never in the past" clamp lives.
        var uploadAt = PublicationQueueTime.For(slotAt, options.PublishLeadMinutes, now);
        var publicationCreateAt = uploadAt.AddHours(-settings.VetoWindowHours);
        var renderDeadline = publicationCreateAt.AddMinutes(-options.PublicationCreateMarginMinutes);
        var renderStartAt = renderDeadline.AddMinutes(-(settings.RenderLeadMinutes ?? options.RenderEstimateMinutes));
        var storyStartAt = renderStartAt.AddMinutes(-(settings.StoryLeadMinutes ?? options.StoryEstimateMinutes));
        var bufferStart = slotAt.AddDays(-settings.BufferDays);

        return new AutopilotRunTiming(
            StartAt: storyStartAt <= bufferStart ? storyStartAt : bufferStart,
            PublicationCreateAt: publicationCreateAt,
            UploadAt: uploadAt,
            RenderStartAt: renderStartAt,
            StoryStartAt: storyStartAt);
    }

    /// <summary>The instant at which the slot can no longer be produced: <c>upload_at − MinVetoWindowHours</c>.</summary>
    /// <param name="timing">The slot's milestones.</param>
    /// <param name="options">The configured minimum veto window.</param>
    /// <returns>The cutoff; at it, and after it, the slot is missed.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    public static DateTimeOffset SlotMissedCutoff(AutopilotRunTiming timing, AutopilotTimingOptions options)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(options);

        return timing.UploadAt.AddHours(-options.MinVetoWindowHours);
    }

    /// <summary>The missed-slot rule of plan §3.4 as one verdict at the instant <paramref name="now"/>.</summary>
    /// <param name="now">The caller's clock reading.</param>
    /// <param name="timing">The slot's milestones.</param>
    /// <param name="options">The configured minimum veto window.</param>
    /// <returns>Wait, create, or the slot is missed.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    public static VetoWindowResult EvaluateVetoWindow(
        DateTimeOffset now,
        AutopilotRunTiming timing,
        AutopilotTimingOptions options)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(options);

        if (now < timing.PublicationCreateAt)
        {
            return VetoWindowResult.WaitUntil;
        }

        if (now >= SlotMissedCutoff(timing, options))
        {
            return VetoWindowResult.SlotMissed;
        }

        return VetoWindowResult.Create;
    }

    /// <summary>
    /// The same verdict as <see cref="EvaluateVetoWindow"/>, in the shape a guard wants: <see langword="true"/>
    /// only when the publication may be created now.
    /// </summary>
    /// <param name="now">The caller's clock reading.</param>
    /// <param name="timing">The slot's milestones.</param>
    /// <param name="options">The configured minimum veto window.</param>
    /// <param name="result">The verdict, in every case — the caller needs the reason when this answers <see langword="false"/>.</param>
    /// <returns>Whether a publication may be created now.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    public static bool TryComputeVetoWindow(
        DateTimeOffset now,
        AutopilotRunTiming timing,
        AutopilotTimingOptions options,
        out VetoWindowResult result)
    {
        result = EvaluateVetoWindow(now, timing, options);

        return result.Decision == VetoWindowDecision.Create;
    }
}
