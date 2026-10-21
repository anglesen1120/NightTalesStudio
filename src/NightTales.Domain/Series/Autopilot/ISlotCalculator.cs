namespace NightTales.Domain.Series.Autopilot;

/// <summary>How a slot's local publish time was moved onto a real UTC instant (plan §3.2).</summary>
public enum SlotDstAdjustment
{
    /// <summary>The local time exists exactly once; no adjustment.</summary>
    None = 0,

    /// <summary>
    /// The local time falls in the spring-forward gap and does not exist: the slot was pushed forward to the
    /// first valid minute (typically one hour later in wall-clock terms).
    /// </summary>
    GapShifted = 1,

    /// <summary>
    /// The local time occurs twice on the fall-back day: the earlier instant (the larger, pre-transition offset)
    /// was chosen.
    /// </summary>
    OverlapEarlier = 2,
}

/// <summary>One planned publish instant plus the DST decision that produced it.</summary>
/// <param name="At">The instant, in UTC.</param>
/// <param name="Dst">Whether the local time had to be moved, and how.</param>
public sealed record PlannedSlot(DateTimeOffset At, SlotDstAdjustment Dst);

/// <summary>
/// Turns a series' cadence into the UTC instants its publications are planned for (plan §3.2, README §10 T7).
/// </summary>
/// <remarks>
/// <para>
/// Both methods are pure: no clock, no I/O, no randomness — the caller passes <c>now</c> from its
/// <c>IClock</c>. The local publish time is resolved through the frame's IANA <see cref="TimeZoneInfo"/>, DST
/// included: a time that does not exist is pushed forward to the first valid minute (bounded to four hours) and
/// a time that exists twice resolves to the earlier UTC instant. The returned offsets are always UTC
/// (<see cref="TimeSpan.Zero"/>), because <c>autopilot_runs.slot_at</c> is a <c>timestamptz</c>.
/// </para>
/// <para>
/// <b>Only future slots.</b> Both methods return strictly the instants after <paramref name="now"/>: a slot
/// whose instant has already passed is not planned (the planner counts that slot as skipped/missed instead of
/// creating a run for a publication the upload job can no longer make). A slot exactly equal to
/// <paramref name="now"/> is also excluded — it is past.
/// </para>
/// <para>
/// <b>Time zone resolution.</b> An unknown or unusable id throws
/// <see cref="TimeZoneNotFoundException"/>/<see cref="InvalidTimeZoneException"/> from here, on purpose: the
/// settings validator is the one place that turns a bad id into a 400 <c>autopilot_settings_invalid</c>, so a
/// document that reached this type is expected to be valid and a throw here is a bug made visible.
/// </para>
/// </remarks>
public interface ISlotCalculator
{
    /// <summary>The instants of the slots the cadence produces in the horizon, UTC, ascending.</summary>
    /// <param name="cadence">The weekdays, local publish time and time zone.</param>
    /// <param name="now">The caller's clock reading.</param>
    /// <param name="horizonDays">How many days ahead of <paramref name="now"/> to plan, inclusive.</param>
    /// <param name="dailyCap">The largest number of slots one local calendar day may keep, 1 or more.</param>
    /// <returns>The instants strictly after <paramref name="now"/>, UTC, ascending.</returns>
    IReadOnlyList<DateTimeOffset> NextSlots(AutopilotCadence cadence, DateTimeOffset now, int horizonDays, int dailyCap);

    /// <summary>
    /// The same slots as <see cref="NextSlots"/>, each carrying the DST decision it came from, so the planner
    /// can count <c>autopilot_slot_dst_adjusted_total{zone,kind}</c> without recomputing anything.
    /// </summary>
    /// <param name="cadence">The weekdays, local publish time and time zone.</param>
    /// <param name="now">The caller's clock reading.</param>
    /// <param name="horizonDays">How many days ahead of <paramref name="now"/> to plan, inclusive.</param>
    /// <param name="dailyCap">The largest number of slots one local calendar day may keep, 1 or more.</param>
    /// <returns>The slots strictly after <paramref name="now"/>, UTC, ascending.</returns>
    IReadOnlyList<PlannedSlot> PlanSlots(AutopilotCadence cadence, DateTimeOffset now, int horizonDays, int dailyCap);
}
