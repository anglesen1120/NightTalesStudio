using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Features.Autopilot.Jobs;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The <c>AutopilotPlanJob</c> cases of 02.8.3-T1 §Tests: one tick turns each eligible series' cadence into one run
/// per slot, and a second tick creates nothing (the <c>UNIQUE (series_id, slot_at)</c> index is the arbiter).
/// </summary>
/// <remarks>
/// <see cref="AutopilotTickHarness.Settings(AutopilotTickHarnessSettings)"/>'s stated defaults are the plan §3.1
/// minimum, so the slot arithmetic of every case is the production one, not a test copy: the horizon is
/// <c>buffer_days + max(cadence interval, 1)</c> local days, and a slot's <c>start_at</c> is
/// <c>min(story_start, slot − buffer_days)</c>, which is the buffer edge for a buffer wider than the lead time.
/// Metrics are process-global, so this class joins the serial collection.
/// </remarks>
[Collection("Process-global host state")]
public sealed class AutopilotPlanJobTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>Tuesday 20/10/2026 10:00 in Ho Chi Minh City (UTC+7, no DST).</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    /// <summary>Thursday 22/10/2026 19:00 in Ho Chi Minh City.</summary>
    private static readonly DateTimeOffset ThursdaySlot = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Monday 26/10/2026 19:00 in Ho Chi Minh City.</summary>
    private static readonly DateTimeOffset MondaySlot = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PlanJob_CreatesOneRunPerSlot_AndIsIdempotent()
    {
        await using var harness = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            ct: TestContext.Current.CancellationToken);
        var owner = await harness.CreateOwnerAsync(TestContext.Current.CancellationToken);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(
                daysOfWeek: [DayOfWeek.Monday, DayOfWeek.Thursday],
                publishTime: new TimeOnly(19, 0)),
            ct: TestContext.Current.CancellationToken);

        await harness.TickAsync(TestContext.Current.CancellationToken);

        var runs = await harness.RunsAsync(owner, world.SeriesId, TestContext.Current.CancellationToken);
        runs.Select(run => run.SlotAt).ShouldBe([ThursdaySlot, MondaySlot]);
        runs.ShouldAllBe(run => run.StartAt == run.SlotAt.AddDays(-2));
        runs.ShouldAllBe(run => run.AuthorizationId == world.AuthorizationId);
        runs.ShouldAllBe(run => run.SeriesId == world.SeriesId && run.UserId == owner);

        await harness.TickAsync(TestContext.Current.CancellationToken);

        (await harness.RunsAsync(owner, world.SeriesId, TestContext.Current.CancellationToken)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task PlanJob_UniqueSlotIndex_RejectsADuplicateRow()
    {
        await using var harness = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            ct: TestContext.Current.CancellationToken);
        var owner = await harness.CreateOwnerAsync(TestContext.Current.CancellationToken);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]),
            ct: TestContext.Current.CancellationToken);

        await harness.TickAsync(TestContext.Current.CancellationToken);
        var planned = await harness.RunsAsync(owner, world.SeriesId, TestContext.Current.CancellationToken);

        // A Thursday-only cadence is planned for `buffer_days + 7` local days (plan §3.2), which holds the next two
        // Thursdays: the index, not the horizon, is what this case is about.
        planned.Count.ShouldBe(2);

        var stamp = Start.ToString("O", CultureInfo.InvariantCulture);
        var duplicate =
            "INSERT INTO autopilot_runs (id, user_id, series_id, slot_at, start_at, started_at, state, "
            + "last_transition_at, created_at, updated_at) VALUES "
            + $"('{Guid.CreateVersion7()}', '{owner}', '{world.SeriesId}', "
            + $"'{planned[0].SlotAt.ToString("O", CultureInfo.InvariantCulture)}', '{stamp}', '{stamp}', "
            + $"'planned', '{stamp}', '{stamp}', '{stamp}')";

        SqlProbe.SqlState(Database, duplicate).ShouldBe("23505");
    }

    [Fact]
    public async Task PlanJob_RegistersRecurringJobOnlyWhenFlagOn()
    {
        await using (var enabled = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            ct: TestContext.Current.CancellationToken))
        {
            enabled.Host.Jobs.RecurringUpserts
                .ShouldContain(call => call.RecurringId == "autopilot-plan"
                    && call.JobType == nameof(AutopilotPlanJob)
                    && call.Cron == "*/15 * * * *");
            enabled.Host.Jobs.RecurringRemovals.ShouldNotContain("autopilot-plan");
        }

        await using var disabled = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            autopilotFlagEnabled: false,
            ct: TestContext.Current.CancellationToken);

        disabled.Host.Jobs.RecurringUpserts
            .ShouldNotContain(call => call.RecurringId == "autopilot-plan");
        disabled.Host.Jobs.RecurringRemovals.ShouldContain("autopilot-plan");
    }

    [Fact]
    public async Task PlanJob_RespectsDailyCapPerChannel()
    {
        await using var harness = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            ct: TestContext.Current.CancellationToken);
        var owner = await harness.CreateOwnerAsync(TestContext.Current.CancellationToken);
        var channel = await harness.CreateChannelAsync(owner, ct: TestContext.Current.CancellationToken);
        var settings = AutopilotTickHarness.Settings(dailyCap: 1);

        var first = await harness.CreateAuthorizedSeriesAsync(owner, settings, channel, ct: TestContext.Current.CancellationToken);
        var second = await harness.CreateAuthorizedSeriesAsync(owner, settings, channel, ct: TestContext.Current.CancellationToken);

        await harness.TickAsync(TestContext.Current.CancellationToken);

        var runs = await harness.RunsAsync(owner, ct: TestContext.Current.CancellationToken);

        // The horizon is `buffer_days + interval` local days and both ends are included (plan §3.2: "each local date
        // in [now in tz, now + horizonDays]"), so three days of horizon from 10:00 local reach four 19:00 slots:
        // Tue 20 … Fri 23. `daily_cap = 1` admits exactly one of them per local day, all on their first series.
        runs.Count.ShouldBe(4);
        runs.Select(run => run.SlotAt.Date).Distinct().Count().ShouldBe(4);
        runs.Select(run => run.SeriesId).Distinct().Count().ShouldBe(1);
        runs.ShouldAllBe(run => run.SeriesId == first.SeriesId || run.SeriesId == second.SeriesId);
    }

    [Fact]
    public async Task PlanJob_SkipsPausedAndArchivedSeries()
    {
        await using var harness = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            ct: TestContext.Current.CancellationToken);
        var owner = await harness.CreateOwnerAsync(TestContext.Current.CancellationToken);
        var settings = AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]);

        var live = await harness.CreateAuthorizedSeriesAsync(owner, settings, ct: TestContext.Current.CancellationToken);
        var paused = await harness.CreateAuthorizedSeriesAsync(owner, settings, ct: TestContext.Current.CancellationToken);
        var archived = await harness.CreateAuthorizedSeriesAsync(owner, settings, ct: TestContext.Current.CancellationToken);

        await harness.UpdateSeriesAsync(
            paused.SeriesId,
            $"autopilot_paused_at = '{Start.AddHours(-1).ToString("O", CultureInfo.InvariantCulture)}'",
            TestContext.Current.CancellationToken);
        await harness.UpdateSeriesAsync(
            archived.SeriesId,
            $"archived_at = '{Start.AddHours(-1).ToString("O", CultureInfo.InvariantCulture)}'",
            TestContext.Current.CancellationToken);

        await harness.TickAsync(TestContext.Current.CancellationToken);

        // A Thursday-only cadence spans `buffer_days + 7` local days (plan §3.2), so the live series holds the next
        // two Thursdays — 19:00 Asia/Ho_Chi_Minh is 12:00Z — while the paused and the archived one hold nothing.
        (await harness.RunsAsync(owner, live.SeriesId, TestContext.Current.CancellationToken))
            .Select(run => run.SlotAt)
            .ShouldBe([ThursdaySlot, ThursdaySlot.AddDays(7)]);
        (await harness.RunsAsync(owner, paused.SeriesId, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await harness.RunsAsync(owner, archived.SeriesId, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task PlanJob_SkipsDisabledAndUnauthorizedSeries()
    {
        await using var harness = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            ct: TestContext.Current.CancellationToken);
        var owner = await harness.CreateOwnerAsync(TestContext.Current.CancellationToken);
        var settings = AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]);

        var live = await harness.CreateAuthorizedSeriesAsync(owner, settings, ct: TestContext.Current.CancellationToken);
        var disabled = await harness.CreateSeriesAsync(
            owner,
            settings,
            autopilotEnabled: false,
            ct: TestContext.Current.CancellationToken);
        var unauthorized = await harness.CreateSeriesAsync(owner, settings, authorize: false, ct: TestContext.Current.CancellationToken);

        await harness.TickAsync(TestContext.Current.CancellationToken);

        // Same horizon as the paused case: a Thursday-only cadence plans the next two Thursdays.
        (await harness.RunsAsync(owner, live.SeriesId, TestContext.Current.CancellationToken))
            .Select(run => run.SlotAt)
            .ShouldBe([ThursdaySlot, ThursdaySlot.AddDays(7)]);
        (await harness.RunsAsync(owner, disabled.SeriesId, TestContext.Current.CancellationToken)).ShouldBeEmpty();

        // No authorization is not a pause (plan §3.5 row `planned → awaiting_authorization`): the slots exist and
        // wait for the owner's accept, so the series keeps its cadence instead of silently losing it.
        var awaitingAuthorization = await harness.RunsAsync(
            owner,
            unauthorized.SeriesId,
            TestContext.Current.CancellationToken);
        awaitingAuthorization.Count.ShouldBe(2);
        awaitingAuthorization.ShouldAllBe(run => run.State == AutopilotRunState.AwaitingAuthorization);
        awaitingAuthorization.ShouldAllBe(
            run => run.Step == AutopilotRunStep.Authorization && run.AuthorizationId == null);
    }

    [Fact]
    public async Task PlanJob_SettingsInvalid_SkipsTheSeriesAndCountsTheSkip()
    {
        using var skipped = AutopilotMetricProbe.Start(
            AutopilotRunMetrics.SlotSkippedTotalName,
            AutopilotRunMetrics.ReasonTagName);

        await using var harness = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            ct: TestContext.Current.CancellationToken);
        var owner = await harness.CreateOwnerAsync(TestContext.Current.CancellationToken);

        var broken = await new NightTales.TestKit.Builders.SeriesBuilder(
                Database,
                new NightTales.TestKit.Auth.TestCurrentUser { Id = owner },
                harness.Clock)
            .CreateAsync(owner, autopilotEnabled: true, autopilotSettings: "{\"schema_version\":1}", ct: TestContext.Current.CancellationToken);

        await harness.UpdateSeriesAsync(
            broken.Id,
            $"default_channel_id = '{await harness.CreateChannelAsync(owner, ct: TestContext.Current.CancellationToken)}'",
            TestContext.Current.CancellationToken);

        await harness.TickAsync(TestContext.Current.CancellationToken);

        (await harness.RunsAsync(owner, broken.Id, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        skipped.Count(AutopilotRunMetrics.SettingsInvalidReason).ShouldBe(1);
        harness.Logs.AllText().ShouldContain("autopilot_plan_settings_invalid");
    }

    [Fact]
    public async Task PlanJob_AdjustsTheSlotAcrossTheDstGap()
    {
        using var adjusted = AutopilotMetricProbe.Start(
            AutopilotRunMetrics.SlotDstAdjustedTotalName,
            AutopilotRunMetrics.DstKindTagName);

        // Sunday 14/03/2027 02:30 does not exist in New York: the slot moves forward by the gap.
        var beforeGap = new DateTimeOffset(2027, 3, 10, 0, 0, 0, TimeSpan.Zero);
        await using var harness = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(beforeGap),
            ct: TestContext.Current.CancellationToken);
        var owner = await harness.CreateOwnerAsync(TestContext.Current.CancellationToken);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(
                daysOfWeek: [DayOfWeek.Sunday],
                publishTime: new TimeOnly(2, 30),
                timeZoneId: "America/New_York"),
            ct: TestContext.Current.CancellationToken);

        await harness.TickAsync(TestContext.Current.CancellationToken);

        var runs = await harness.RunsAsync(owner, world.SeriesId, TestContext.Current.CancellationToken);
        runs.Count.ShouldBe(1);

        // Plan §3.2 and `SlotCalculator_DstGap_ShiftsForward`: the local time does not exist, so the slot is the
        // first minute that does — 02:30 EST would be 07:30Z, the gap's end 03:00 EDT is 07:00Z.
        runs[0].SlotAt.ShouldBe(new DateTimeOffset(2027, 3, 14, 7, 0, 0, TimeSpan.Zero));
        adjusted.Count(AutopilotRunMetrics.DstGapKind).ShouldBe(1);
    }
}
