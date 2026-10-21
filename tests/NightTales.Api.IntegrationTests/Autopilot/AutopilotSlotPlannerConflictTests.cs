using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Features.Autopilot.Planning;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// What one <c>AutopilotSlotPlanner</c> tick reports when the slot index refuses its flush (02.8.3 plan §3.2): the
/// series created no row, because the provider rolled the whole per-series batch back.
/// </summary>
/// <remarks>
/// The losing side of the race is made deterministic with a trigger that refuses every insert with the same
/// <c>23505</c> the unique index raises, so the planner's catch runs without a second host. Metrics are
/// process-global, so this class joins the serial collection; the trigger is dropped in a <c>finally</c>.
/// </remarks>
/// <param name="database">The class's own migrated database, from <see cref="IntegrationTest"/>.</param>
[Collection("Process-global host state")]
public sealed class AutopilotSlotPlannerConflictTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>Tuesday 20/10/2026 10:00 in Ho Chi Minh City (UTC+7, no DST).</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    private const string RefuseSlotTrigger = "autopilot_runs_refuse_slot";

    /// <summary>
    /// A flush refused with <c>23505</c> leaves no row behind, so the tick reports zero created rows — not the
    /// count it had accumulated before the rollback — and still logs the collision as "already planned".
    /// </summary>
    [Fact]
    public async Task PlanAsync_UniqueViolationOnTheFlush_ReportsNoCreatedRow()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var harness = await AutopilotTickHarness.StartAsync(Database, new FakeClock(Start), ct: ct);
        var owner = await harness.CreateOwnerAsync(ct);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Monday, DayOfWeek.Thursday]),
            ct: ct);

        await Database.ExecuteAsync(
            $"""
             CREATE FUNCTION {RefuseSlotTrigger}() RETURNS trigger LANGUAGE plpgsql AS $$
             BEGIN
                 RAISE EXCEPTION 'slot_at already planned' USING ERRCODE = '23505';
             END $$;
             CREATE TRIGGER {RefuseSlotTrigger} BEFORE INSERT ON autopilot_runs
                 FOR EACH ROW EXECUTE FUNCTION {RefuseSlotTrigger}();
             """,
            ct);

        int created;
        try
        {
            using var scope = harness.Host.CreateScope();

            created = await scope.ServiceProvider
                .GetRequiredService<AutopilotSlotPlanner>()
                .PlanAsync(harness.Clock.UtcNow, ct);
        }
        finally
        {
            await Database.ExecuteAsync(
                $"DROP TRIGGER IF EXISTS {RefuseSlotTrigger} ON autopilot_runs; "
                + $"DROP FUNCTION IF EXISTS {RefuseSlotTrigger}();",
                ct);
        }

        created.ShouldBe(0);
        (await harness.RunsAsync(owner, world.SeriesId, ct)).ShouldBeEmpty();
        harness.Logs.AllText().ShouldContain("autopilot_plan_already_planned");
    }
}
