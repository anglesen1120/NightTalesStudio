using System.Globalization;

namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// The plan §3.2 slot arithmetic: a cadence's weekdays, local publish time and IANA time zone become the UTC
/// instants of the series' next publications, DST included.
/// </summary>
/// <remarks>
/// <para>
/// Pure and deterministic — no clock, no I/O, no package beyond the BCL (decision 12: <see cref="TimeZoneInfo"/>,
/// never NodaTime). The caller passes <c>now</c> from its <c>IClock</c>; nothing here reads the wall clock.
/// </para>
/// <para>
/// <b>DST.</b> A local publish time that falls in the spring-forward gap does not exist: the slot is pushed
/// forward minute by minute to the first valid minute (bounded to <see cref="MaxDstGapShiftMinutes"/>, which is
/// far more than any real gap) and reported as <see cref="SlotDstAdjustment.GapShifted"/>, so the run still
/// happens, one hour later in wall-clock terms. A local publish time that occurs twice on the fall-back day is
/// resolved with the <em>larger</em> offset — <c>GetAmbiguousTimeOffsets(local).Max()</c>, the pre-transition
/// daylight offset — which is the earlier UTC instant (New York 01:30 is 05:30Z, not 06:30Z), reported as
/// <see cref="SlotDstAdjustment.OverlapEarlier"/>.
/// </para>
/// <para>
/// <b>Daily cap.</b> One publish time per cadence yields at most one slot per local day, so the cap can never
/// fire inside <see cref="PlanSlots"/>; the cap that matters is the channel-wide one of plan §3.2 — several
/// series share a channel and together may publish at most <c>daily_cap</c> times per local day. That cap
/// belongs to the planner, which passes every series' slot of the channel plus the channel's already occupied
/// <c>slot_at</c> instants to <see cref="ApplyDailyCap"/>. The cap is still applied inside
/// <see cref="PlanSlots"/> (with no occupied slots) so a caller that forgets it cannot silently over-plan.
/// </para>
/// </remarks>
public sealed class SlotCalculator : ISlotCalculator
{
    /// <summary>The largest forward shift a spring-forward gap may cause before the calculator calls it a bug.</summary>
    public const int MaxDstGapShiftMinutes = 240;

    /// <summary>The largest horizon the planner may ask for; the plan's own is <c>buffer_days + interval ≤ 14</c>.</summary>
    public const int MaxHorizonDays = 366;

    /// <inheritdoc />
    public IReadOnlyList<DateTimeOffset> NextSlots(
        AutopilotCadence cadence,
        DateTimeOffset now,
        int horizonDays,
        int dailyCap) =>
        PlanSlots(cadence, now, horizonDays, dailyCap)
            .Select(static slot => slot.At)
            .ToArray();

    /// <inheritdoc />
    public IReadOnlyList<PlannedSlot> PlanSlots(
        AutopilotCadence cadence,
        DateTimeOffset now,
        int horizonDays,
        int dailyCap)
    {
        ArgumentNullException.ThrowIfNull(cadence);
        ArgumentOutOfRangeException.ThrowIfNegative(horizonDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(horizonDays, MaxHorizonDays);
        ArgumentOutOfRangeException.ThrowIfLessThan(dailyCap, 1);

        // An unknown id throws here on purpose: the settings validator is the only place allowed to turn it into
        // `autopilot_settings_invalid` (plan §3.1), so a throw at this point means an invalid document got through.
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(cadence.TimeZoneId);

        if (cadence.DaysOfWeek.Count == 0)
        {
            return [];
        }

        var weekdays = cadence.DaysOfWeek.ToHashSet();
        var startDate = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        var endDate = TimeZoneInfo.ConvertTime(now.AddDays(horizonDays), timeZone).Date;

        var slots = new List<PlannedSlot>();
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            if (!weekdays.Contains(date.DayOfWeek))
            {
                continue;
            }

            var slot = BuildSlot(date, cadence.PublishTime, timeZone);
            if (slot.At <= now)
            {
                // Already due or past: the planner records why it did not plan it, this type only plans ahead.
                continue;
            }

            slots.Add(slot);
        }

        slots.Sort(static (left, right) => left.At.CompareTo(right.At));

        return ApplyDailyCapToPlannedSlots(slots, timeZone, dailyCap);
    }

    /// <summary>
    /// Applies plan §3.2's channel-wide daily cap: at most <paramref name="dailyCap"/> slots per local calendar
    /// day, counting the channel's already occupied slots, keeping the earliest candidates of each day.
    /// </summary>
    /// <remarks>
    /// This is the half of the cap a single series cannot see and the <c>AutopilotPlanJob</c> calls: it feeds
    /// every candidate slot of the channel (one per series per day) plus the <c>autopilot_runs.slot_at</c>
    /// instants the channel already holds in a non-cancelled state. Skipped candidates are simply absent from the
    /// answer — the planner logs them with <c>autopilot_slot_skipped_total{reason="daily_cap"}</c> and creates no
    /// run for them (no noise in the run log).
    /// </remarks>
    /// <param name="candidateSlots">The candidate instants, any order; the earliest of a day win.</param>
    /// <param name="timeZoneId">The channel's IANA time zone the local days are read in.</param>
    /// <param name="dailyCap">The largest number of slots one local calendar day may hold, 1 or more.</param>
    /// <param name="existingOccupiedSlots">The instants the channel already plans on those local days.</param>
    /// <returns>The admitted candidates, ascending.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="timeZoneId"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dailyCap"/> is below 1.</exception>
    /// <exception cref="TimeZoneNotFoundException"><paramref name="timeZoneId"/> is not an id the runtime resolves.</exception>
    public static IReadOnlyList<DateTimeOffset> ApplyDailyCap(
        IEnumerable<DateTimeOffset> candidateSlots,
        string timeZoneId,
        int dailyCap,
        IReadOnlyCollection<DateTimeOffset> existingOccupiedSlots)
    {
        ArgumentNullException.ThrowIfNull(candidateSlots);
        ArgumentNullException.ThrowIfNull(existingOccupiedSlots);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        ArgumentOutOfRangeException.ThrowIfLessThan(dailyCap, 1);

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        var counts = new Dictionary<DateOnly, int>();
        foreach (var occupied in existingOccupiedSlots)
        {
            var day = LocalDate(occupied, timeZone);
            counts[day] = counts.TryGetValue(day, out var already) ? already + 1 : 1;
        }

        var admitted = new List<DateTimeOffset>();
        foreach (var candidate in candidateSlots.OrderBy(static slot => slot))
        {
            var day = LocalDate(candidate, timeZone);
            if (counts.TryGetValue(day, out var count) && count >= dailyCap)
            {
                continue;
            }

            counts[day] = count + 1;
            admitted.Add(candidate);
        }

        return admitted;
    }

    /// <summary>Builds the one slot of a local calendar date, resolving a DST gap or overlap.</summary>
    private static PlannedSlot BuildSlot(DateTime localDate, TimeOnly publishTime, TimeZoneInfo timeZone)
    {
        var local = localDate.Add(publishTime.ToTimeSpan());
        var resolved = ShiftOutOfGap(local, timeZone, out var gapShifted);

        if (timeZone.IsAmbiguousTime(resolved))
        {
            // The larger offset is the pre-transition (daylight) one and therefore the earlier UTC instant.
            var offset = timeZone.GetAmbiguousTimeOffsets(resolved).Max();

            return new PlannedSlot(
                new DateTimeOffset(resolved, offset),
                gapShifted ? SlotDstAdjustment.GapShifted : SlotDstAdjustment.OverlapEarlier);
        }

        return new PlannedSlot(
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(resolved, timeZone)),
            gapShifted ? SlotDstAdjustment.GapShifted : SlotDstAdjustment.None);
    }

    /// <summary>Advances a local time that does not exist in its zone to the first minute that does.</summary>
    private static DateTime ShiftOutOfGap(DateTime local, TimeZoneInfo timeZone, out bool shifted)
    {
        var candidate = local;
        var elapsed = 0;

        while (timeZone.IsInvalidTime(candidate))
        {
            if (elapsed >= MaxDstGapShiftMinutes)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The local time {local:O} in '{timeZone.Id}' is still invalid after {MaxDstGapShiftMinutes} minutes; a spring-forward gap that large is a broken tz database, not a slot."));
            }

            candidate = candidate.AddMinutes(1);
            elapsed++;
        }

        shifted = elapsed > 0;

        return candidate;
    }

    /// <summary>The channel-wide cap, on the typed slots <see cref="PlanSlots"/> returns.</summary>
    private static List<PlannedSlot> ApplyDailyCapToPlannedSlots(
        List<PlannedSlot> slots,
        TimeZoneInfo timeZone,
        int dailyCap)
    {
        var counts = new Dictionary<DateOnly, int>();
        var admitted = new List<PlannedSlot>(slots.Count);

        foreach (var slot in slots)
        {
            var day = LocalDate(slot.At, timeZone);
            if (counts.TryGetValue(day, out var count) && count >= dailyCap)
            {
                continue;
            }

            counts[day] = count + 1;
            admitted.Add(slot);
        }

        return admitted;
    }

    /// <summary>The local calendar date of an instant in the channel's zone.</summary>
    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);
}

/// <summary>
/// The start-up check plan §3.2 asks for: <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> resolves an IANA
/// id only while the runtime carries the tz database, and a container image built with
/// <c>InvariantGlobalization=true</c> does not. The Api and the Worker call <see cref="SelfCheck"/> once at
/// start-up so that misconfiguration fails loudly instead of turning every cadence into a 500 later.
/// </summary>
public static class AutopilotTimeZones
{
    /// <summary>The ids the self-check resolves: UTC plus one zone that actually observes DST.</summary>
    private static readonly string[] Probes = ["UTC", "America/New_York"];

    /// <summary>Resolves the probe zones and throws if this runtime cannot.</summary>
    /// <exception cref="InvalidOperationException">
    /// A probe id did not resolve: the runtime is missing the ICU/IANA tz database, which
    /// <c>InvariantGlobalization=true</c> causes.
    /// </exception>
    public static void SelfCheck()
    {
        foreach (var id in Probes)
        {
            Resolve(id);
        }
    }

    /// <summary>Resolves one id, translating the time-zone exceptions into the configuration failure they are.</summary>
    private static void Resolve(string id)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new InvalidOperationException(Message(id), exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new InvalidOperationException(Message(id), exception);
        }
    }

    private static string Message(string id) => string.Create(
        CultureInfo.InvariantCulture,
        $"The IANA time zone '{id}' did not resolve on this runtime, so autopilot cadences cannot be computed. The runtime is missing the ICU/IANA tz database: do not set InvariantGlobalization=true (plan §3.2, README §10 T7).");
}
