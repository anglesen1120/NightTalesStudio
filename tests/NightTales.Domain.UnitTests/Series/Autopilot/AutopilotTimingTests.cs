using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Autopilot;

/// <summary>
/// Plan §3.4 timing: the five milestones of a slot, the ready buffer winning over the story start, and the
/// missed-slot rule (a late publication gets a shortened veto of at least two hours, then the slot is gone).
/// </summary>
/// <remarks>
/// Pure tests: the slot is the plan's worked example instant and every later reading is a literal, so nothing
/// here reads the clock or touches a database.
/// </remarks>
public sealed class AutopilotTimingTests
{
    /// <summary>The plan §3.4 worked example's slot: Thu 2026-10-08 19:00 +07.</summary>
    private static readonly DateTimeOffset Slot = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>06's <c>PublishLeadMinutes</c> 120 and <c>MinVetoWindowHours</c> 2, plus the plan's placeholders.</summary>
    private static readonly AutopilotTimingOptions Options = new(
        PublishLeadMinutes: 120,
        MinVetoWindowHours: 2,
        PublicationCreateMarginMinutes: 10,
        RenderEstimateMinutes: 15,
        StoryEstimateMinutes: 10,
        MaxBufferDays: 7);

    [Fact]
    public void AutopilotTiming_WorkedExample_MatchesPlanSection34()
    {
        // The plan's table, instant by instant: slot 12:00Z, upload 10:00Z, publication 22:00Z the day before,
        // a 15-minute render plus a 10-minute margin before it, a 10-minute story before that, and a 2-day
        // buffer that wins over the story start.
        var timing = AutopilotTiming.Compute(Slot, Settings(), Options);

        timing.UploadAt.ShouldBe(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
        timing.PublicationCreateAt.ShouldBe(new DateTimeOffset(2026, 10, 7, 22, 0, 0, TimeSpan.Zero));
        timing.RenderStartAt.ShouldBe(new DateTimeOffset(2026, 10, 7, 21, 35, 0, TimeSpan.Zero));
        timing.StoryStartAt.ShouldBe(new DateTimeOffset(2026, 10, 7, 21, 25, 0, TimeSpan.Zero));
        timing.StartAt.ShouldBe(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

        // The chain is ordered: start_at ≤ story ≤ render ≤ publication create ≤ upload ≤ slot.
        timing.StartAt.ShouldBeLessThanOrEqualTo(timing.StoryStartAt);
        timing.StoryStartAt.ShouldBeLessThan(timing.RenderStartAt);
        timing.RenderStartAt.ShouldBeLessThan(timing.PublicationCreateAt);
        timing.PublicationCreateAt.ShouldBeLessThan(timing.UploadAt);
        timing.UploadAt.ShouldBeLessThan(Slot);
    }

    [Fact]
    public void Timing_StartAt_UsesBufferWhenEarlier()
    {
        // The acceptance check's name (plan §3.4): with buffer_days = 2 the buffer start (Tue 12:00Z) is
        // earlier than the story start (Wed 21:25Z), so start_at is the buffer start.
        var timing = AutopilotTiming.Compute(Slot, Settings(bufferDays: 2), Options);

        timing.StartAt.ShouldBe(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        timing.StartAt.ShouldBe(Slot.AddDays(-2));
        timing.StartAt.ShouldBeLessThan(timing.StoryStartAt);
        timing.StartAt.ShouldNotBe(timing.StoryStartAt);
    }

    [Fact]
    public void AutopilotTiming_StartAt_FallsBackToStoryStart_WhenBufferTooLate()
    {
        // A 48 h veto window and a 1-day buffer: slot − 1 day (Thu 12:00Z) is later than the story start, so the
        // buffer cannot pull the run earlier and start_at is story_start_at — the tight production window.
        var settings = Settings(vetoWindowHours: 48, bufferDays: 1);

        var timing = AutopilotTiming.Compute(Slot, settings, Options);

        timing.PublicationCreateAt.ShouldBe(new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero));
        timing.StoryStartAt.ShouldBe(new DateTimeOffset(2026, 10, 6, 9, 25, 0, TimeSpan.Zero));
        timing.StartAt.ShouldBe(timing.StoryStartAt);
        timing.StartAt.ShouldBeLessThan(Slot.AddDays(-1));
    }

    [Fact]
    public void AutopilotTiming_MinVetoWindow_ShortensButNeverBelow2h()
    {
        var settings = Settings();
        var timing = AutopilotTiming.Compute(Slot, settings, Options);

        // Before the publication may be created at all.
        AutopilotTiming.EvaluateVetoWindow(timing.PublicationCreateAt.AddMinutes(-1), timing, Options, settings)
            .ShouldBe(VetoWindowResult.WaitUntil);

        // Created exactly on time: the full configured window.
        AutopilotTiming.EvaluateVetoWindow(timing.PublicationCreateAt, timing, Options, settings)
            .ShouldBe(VetoWindowResult.Create(12));

        // Created an hour late: 11 h remain before the upload, so the veto is shortened to 11 h.
        AutopilotTiming.EvaluateVetoWindow(timing.PublicationCreateAt.AddHours(1), timing, Options, settings)
            .ShouldBe(VetoWindowResult.Create(11));

        // Three hours before the upload: 3 h remain.
        AutopilotTiming.EvaluateVetoWindow(timing.UploadAt.AddHours(-3), timing, Options, settings)
            .ShouldBe(VetoWindowResult.Create(3));

        // Two and a half hours before the upload: floored at the 2 h minimum, never below it.
        AutopilotTiming.EvaluateVetoWindow(timing.UploadAt.AddMinutes(-150), timing, Options, settings)
            .ShouldBe(VetoWindowResult.Create(2));

        // One hour before the upload: the slot is missed and the run ends failed/slot_missed.
        AutopilotTiming.EvaluateVetoWindow(timing.UploadAt.AddHours(-1), timing, Options, settings)
            .ShouldBe(VetoWindowResult.SlotMissed);
    }

    [Fact]
    public void AutopilotTiming_SlotMissedCutoff_IsUploadMinusMinVeto()
    {
        var settings = Settings();
        var timing = AutopilotTiming.Compute(Slot, settings, Options);

        var cutoff = AutopilotTiming.SlotMissedCutoff(timing, Options);

        cutoff.ShouldBe(timing.UploadAt.AddHours(-Options.MinVetoWindowHours));
        cutoff.ShouldBe(new DateTimeOffset(2026, 10, 8, 8, 0, 0, TimeSpan.Zero));

        // One minute before the cutoff the publication may still be created, with the 2 h minimum.
        AutopilotTiming.TryComputeVetoWindow(cutoff.AddMinutes(-1), timing, Options, settings, out var before)
            .ShouldBeTrue();
        before.ShouldBe(VetoWindowResult.Create(2));

        // At the cutoff and after it, the slot is missed.
        AutopilotTiming.TryComputeVetoWindow(cutoff, timing, Options, settings, out var atCutoff).ShouldBeFalse();
        atCutoff.ShouldBe(VetoWindowResult.SlotMissed);

        AutopilotTiming.TryComputeVetoWindow(cutoff.AddMinutes(1), timing, Options, settings, out var afterCutoff)
            .ShouldBeFalse();
        afterCutoff.Decision.ShouldBe(VetoWindowDecision.SlotMissed);
    }

    [Fact]
    public void AutopilotTiming_UsesConfiguredLeadMinutesOverTheEstimates()
    {
        // A series that states its own leads overrides Autopilot:RenderEstimate:Minutes / StoryEstimate:Minutes.
        var timing = AutopilotTiming.Compute(
            Slot,
            Settings(renderLeadMinutes: 30, storyLeadMinutes: 20),
            Options);

        timing.RenderStartAt.ShouldBe(timing.PublicationCreateAt.AddMinutes(-Options.PublicationCreateMarginMinutes - 30));
        timing.StoryStartAt.ShouldBe(timing.RenderStartAt.AddMinutes(-20));
        timing.StoryStartAt.ShouldBe(new DateTimeOffset(2026, 10, 7, 21, 0, 0, TimeSpan.Zero));
    }

    private static AutopilotSettings Settings(
        int vetoWindowHours = 12,
        int bufferDays = 2,
        int? renderLeadMinutes = null,
        int? storyLeadMinutes = null) =>
        new(
            SchemaVersion: 1,
            Cadence: new AutopilotCadence(
                [DayOfWeek.Monday, DayOfWeek.Thursday],
                new TimeOnly(19, 0),
                "Asia/Ho_Chi_Minh"),
            DailyCap: 1,
            BufferDays: bufferDays,
            Privacy: PrivacyStatus.Private,
            VetoWindowHours: vetoWindowHours,
            PreAuditMode: PreAuditMode.KeepCadencePrivate,
            PreAuditPauseAfter: 3,
            GateThresholdsRef: "default",
            Tone: "atmospheric, restrained, no gore, first person",
            BannedThemes: [],
            RenderLeadMinutes: renderLeadMinutes,
            StoryLeadMinutes: storyLeadMinutes);
}
