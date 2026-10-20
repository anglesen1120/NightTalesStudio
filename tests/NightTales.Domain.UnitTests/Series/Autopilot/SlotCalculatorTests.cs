using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Autopilot;

/// <summary>
/// Plan §3.2 slot arithmetic (README §10 T7): the cadence → UTC instants mapping, DST gap and overlap, the
/// horizon that always covers the next slot, and the channel-wide daily cap the planner applies.
/// </summary>
/// <remarks>
/// Pure tests: no clock (every <c>now</c> is a literal), no I/O, no containers. They run on any machine whose
/// runtime carries the IANA tz database, which is the requirement <see cref="AutopilotTimeZones.SelfCheck"/>
/// exists for; the zones used are <c>Asia/Ho_Chi_Minh</c> (no DST) and <c>America/New_York</c> (DST).
/// </remarks>
public sealed class SlotCalculatorTests
{
    private static readonly SlotCalculator Calculator = new();

    [Fact]
    public void SlotCalculator_MonThuCadence_ReturnsBothWeekdays()
    {
        var cadence = Cadence([DayOfWeek.Monday, DayOfWeek.Thursday], 19, 0, "Asia/Ho_Chi_Minh");

        // Monday 2026-10-05 07:00 in Ho Chi Minh City; a 7-day horizon ends on Monday 2026-10-12.
        var slots = Calculator.NextSlots(cadence, new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), 7, 1);

        slots.Count.ShouldBe(3);
        slots[0].ShouldBe(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        slots[1].ShouldBe(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        slots[2].ShouldBe(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        slots.ShouldAllBe(slot => slot.Offset == TimeSpan.Zero);
    }

    [Fact]
    public void SlotCalculator_DstGap_ShiftsForward()
    {
        // America/New_York springs forward on 2027-03-14: 02:30 does not exist, so the slot is the first valid
        // minute — 03:00 EDT, which is 07:00Z.
        var cadence = Cadence([DayOfWeek.Sunday], 2, 30, "America/New_York");

        var slots = Calculator.PlanSlots(cadence, new DateTimeOffset(2027, 3, 13, 0, 0, 0, TimeSpan.Zero), 2, 1);

        var slot = slots.ShouldHaveSingleItem();
        slot.At.ShouldBe(new DateTimeOffset(2027, 3, 14, 7, 0, 0, TimeSpan.Zero));
        slot.Dst.ShouldBe(SlotDstAdjustment.GapShifted);
    }

    [Fact]
    public void SlotCalculator_DstOverlap_PicksEarlierInstant()
    {
        // America/New_York falls back on 2026-11-01: 01:30 happens twice. The larger offset (−04:00, daylight)
        // is the earlier UTC instant, 05:30Z; the second occurrence (−05:00) would be 06:30Z.
        var cadence = Cadence([DayOfWeek.Sunday], 1, 30, "America/New_York");

        var slots = Calculator.PlanSlots(cadence, new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), 2, 1);

        var slot = slots.ShouldHaveSingleItem();
        slot.At.ShouldBe(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero));
        slot.Dst.ShouldBe(SlotDstAdjustment.OverlapEarlier);
    }

    [Fact]
    public void SlotCalculator_NoDstZone_IsStable()
    {
        // Vietnam has observed no DST since 1975: every 19:00 slot of the fortnight is 12:00Z, no adjustment.
        var cadence = Cadence([DayOfWeek.Monday, DayOfWeek.Thursday], 19, 0, "Asia/Ho_Chi_Minh");

        // A fortnight, stated explicitly: the planner's own horizon (buffer 2 + interval 4 = 6 days) only reaches
        // Thursday 2026-12-31, and the formula itself is asserted in SlotCalculator_HorizonDays_IncludesNextSlot.
        var slots = Calculator.PlanSlots(
            cadence,
            new DateTimeOffset(2026, 12, 28, 0, 0, 0, TimeSpan.Zero),
            horizonDays: 14,
            dailyCap: 1);

        slots.Count.ShouldBe(5);
        slots.ShouldAllBe(slot => slot.At.Hour == 12 && slot.At.Minute == 0 && slot.At.Offset == TimeSpan.Zero);
        slots.ShouldAllBe(slot => slot.Dst == SlotDstAdjustment.None);
        slots.Select(slot => slot.At).ShouldBe(
            [
                new DateTimeOffset(2026, 12, 28, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2027, 1, 4, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2027, 1, 7, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2027, 1, 11, 12, 0, 0, TimeSpan.Zero),
            ]);
    }

    [Fact]
    public void SlotCalculator_HorizonDays_IncludesNextSlot()
    {
        // A single-weekday cadence: the buffer alone (1 day) finds nothing on a Wednesday, while
        // HorizonDays = buffer_days + 7 reaches the next Monday. That is why plan §3.2 adds the interval.
        var cadence = Cadence([DayOfWeek.Monday], 19, 0, "Asia/Ho_Chi_Minh");
        var settings = Settings(cadence, bufferDays: 1);
        var now = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

        AutopilotSettingsValidator.HorizonDays(settings).ShouldBe(8);
        Calculator.NextSlots(cadence, now, horizonDays: 1, dailyCap: 1).ShouldBeEmpty();

        var slots = Calculator.NextSlots(cadence, now, AutopilotSettingsValidator.HorizonDays(settings), 1);

        slots.ShouldHaveSingleItem().ShouldBe(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void SlotCalculator_DailyCap_SkipsSecondSlotOfTheSameLocalDay()
    {
        // One publish_time yields at most one slot per local day, so the cap that can actually fire is the
        // channel-wide one: two series sharing a channel on the same local day. The planner feeds every
        // candidate plus the channel's occupied slots to ApplyDailyCap.
        const string Zone = "Asia/Ho_Chi_Minh";
        var morning = new DateTimeOffset(2026, 10, 8, 1, 0, 0, TimeSpan.Zero);   // Thu 08:00 +07
        var evening = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);  // Thu 19:00 +07
        var nextWeek = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

        var capped = SlotCalculator.ApplyDailyCap(
            [evening, morning, nextWeek],
            Zone,
            dailyCap: 1,
            existingOccupiedSlots: []);

        capped.Count.ShouldBe(2);
        capped[0].ShouldBe(morning);
        capped[1].ShouldBe(nextWeek);

        // An already occupied slot of the channel consumes the day's capacity: the second series' Thursday is
        // dropped even though it was never admitted in this call.
        var withOccupied = SlotCalculator.ApplyDailyCap(
            [morning, nextWeek],
            Zone,
            dailyCap: 1,
            existingOccupiedSlots: [evening]);

        withOccupied.ShouldHaveSingleItem().ShouldBe(nextWeek);

        // A cap of two admits both Thursdays.
        SlotCalculator.ApplyDailyCap([morning, evening], Zone, dailyCap: 2, existingOccupiedSlots: [])
            .Count.ShouldBe(2);
    }

    [Fact]
    public void SlotCalculator_SkipsSlotsInThePast()
    {
        var cadence = Cadence([DayOfWeek.Monday, DayOfWeek.Thursday], 19, 0, "Asia/Ho_Chi_Minh");

        // Monday 2026-10-05 20:00 local: today's 19:00 slot has passed and is not planned.
        var now = new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero);

        Calculator.NextSlots(cadence, now, horizonDays: 0, dailyCap: 1).ShouldBeEmpty();

        var slots = Calculator.NextSlots(cadence, now, horizonDays: 3, dailyCap: 1);

        slots.ShouldHaveSingleItem().ShouldBe(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void AutopilotTimeZones_SelfCheck_ResolvesTheProbeZones()
    {
        // The start-up check of plan §3.2: UTC and one DST zone must resolve, or the runtime is missing the
        // IANA tz database (InvariantGlobalization=true).
        Should.NotThrow(() => AutopilotTimeZones.SelfCheck());
    }

    private static AutopilotCadence Cadence(
        IReadOnlyList<DayOfWeek> daysOfWeek,
        int hour,
        int minute,
        string timeZoneId) =>
        new(daysOfWeek, new TimeOnly(hour, minute), timeZoneId);

    private static AutopilotSettings Settings(AutopilotCadence cadence, int bufferDays = 2) =>
        new(
            SchemaVersion: 1,
            Cadence: cadence,
            DailyCap: 1,
            BufferDays: bufferDays,
            Privacy: PrivacyStatus.Private,
            VetoWindowHours: 12,
            PreAuditMode: PreAuditMode.KeepCadencePrivate,
            PreAuditPauseAfter: 3,
            GateThresholdsRef: "default",
            Tone: "atmospheric, restrained, no gore, first person",
            BannedThemes: [],
            RenderLeadMinutes: null,
            StoryLeadMinutes: null);
}
