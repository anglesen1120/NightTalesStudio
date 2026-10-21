using NightTales.Application.Abstractions;
using NightTales.Domain.Series.Autopilot;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The gate-failure streak over real rows (02.8.2 plan §3.5, task 02.8.2-T1): consecutive <c>gate_failed</c>
/// outcomes, the window cap, and what resets the count — plus the slot ordering and the wire form of the pause
/// reason 02.8.3-T1 added to the same reader.
/// </summary>
/// <remarks>
/// The run rows go through <see cref="AutopilotRunBuilder"/>, which plans them with the production factory and then
/// seeds the machine position the scenario needs; the user, the series and the episode go through their own
/// builders, so every foreign key is the row production writes. The reader under test is the production one, and it
/// now orders by <c>slot_at</c> (STATUS S67-6), which the tests below vary independently of <c>started_at</c>.
/// </remarks>
/// <param name="database">The class's own migrated database, from <see cref="IntegrationTest"/>.</param>
public sealed class GateFailureStreakReaderTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Two consecutive gate failures are a streak of two (the acceptance check's case).</summary>
    [Fact]
    public async Task GateStreak_TwoConsecutiveGateFailures_ReturnsTwo()
    {
        var world = await SeedWorldAsync();

        await SeedRunAsync(world, Start, AutopilotRunOutcome.GateFailed);
        await SeedRunAsync(world, Start.AddMinutes(15), AutopilotRunOutcome.GateFailed);

        var reader = Reader(world);

        (await reader.GetStreakAsync(world.SeriesId, CancellationToken.None)).ShouldBe(2);
    }

    /// <summary>
    /// The window is the plan's <c>LIMIT 2</c>: a third failure in a row still answers 2, and the snapshot of
    /// <see cref="IGateFailureStreakReader.GetLatestAsync"/> names the newest run and its stored verdict.
    /// </summary>
    [Fact]
    public async Task GateFailure_TwoRunsInARow_StreakIsTwo()
    {
        var world = await SeedWorldAsync();

        await SeedRunAsync(world, Start, AutopilotRunOutcome.GateFailed);
        await SeedRunAsync(world, Start.AddMinutes(15), AutopilotRunOutcome.GateFailed);
        var newest = await SeedRunAsync(world, Start.AddMinutes(30), AutopilotRunOutcome.GateFailed);

        var reader = Reader(world);

        (await reader.GetStreakAsync(world.SeriesId, CancellationToken.None)).ShouldBe(GateFailureStreakReader.StreakWindow);

        var latest = await reader.GetLatestAsync(world.SeriesId, CancellationToken.None);
        latest.ShouldNotBeNull();
        latest.RunId.ShouldBe(newest);
        latest.EpisodeId.ShouldBe(world.EpisodeId);
    }

    /// <summary>A terminal run that did not fail ends the count: a published slot in between answers 0.</summary>
    [Fact]
    public async Task PublishedRunInBetween_ResetsTheStreak()
    {
        var world = await SeedWorldAsync();

        await SeedRunAsync(world, Start, AutopilotRunOutcome.GateFailed);
        await SeedRunAsync(world, Start.AddMinutes(15), AutopilotRunOutcome.Published);
        await SeedRunAsync(world, Start.AddMinutes(30), AutopilotRunOutcome.GateFailed);

        (await Reader(world).GetStreakAsync(world.SeriesId, CancellationToken.None)).ShouldBe(0);
    }

    /// <summary>A run that has not stopped (<c>outcome IS NULL</c>) is not counted as a failure.</summary>
    [Fact]
    public async Task NonTerminalRun_IsIgnored()
    {
        var world = await SeedWorldAsync();

        await SeedRunAsync(world, Start, AutopilotRunOutcome.GateFailed);
        await SeedRunAsync(world, Start.AddMinutes(15), outcome: null);

        (await Reader(world).GetStreakAsync(world.SeriesId, CancellationToken.None)).ShouldBe(1);
    }

    /// <summary>
    /// A run that stopped without finishing — a buffered slot waiting for the owner's consent, or the whole series
    /// paused — is not part of the two-row window either, although 02.8.3 writes its <c>outcome</c>: the terminal
    /// marker is <c>finished_at</c>, and a newer open slot must not hide the two gate failures behind it.
    /// </summary>
    [Fact]
    public async Task StoppedButOpenRunsNewerThanTwoFailures_KeepTheStreak()
    {
        var world = await SeedWorldAsync();

        await SeedRunAsync(world, Start, AutopilotRunOutcome.GateFailed);
        await SeedRunAsync(world, Start.AddMinutes(15), AutopilotRunOutcome.GateFailed);

        // Both are newer than the failures on `slot_at`, which is the column the reader orders by.
        await SeedRunAsync(
            world,
            Start.AddMinutes(30),
            AutopilotRunOutcome.WaitingOwner,
            state: AutopilotRunState.WaitingOwner);
        await SeedRunAsync(
            world,
            Start.AddMinutes(45),
            AutopilotRunOutcome.Paused,
            state: AutopilotRunState.Paused,
            pauseReason: AutopilotPauseReason.OwnerPaused);

        (await Reader(world).GetStreakAsync(world.SeriesId, CancellationToken.None))
            .ShouldBe(GateFailureStreakReader.StreakWindow);
    }

    /// <summary>
    /// <see cref="IGateFailureStreakReader.GetLatestAsync"/> answers the newest <b>slot</b>, not the row planned last
    /// (STATUS S67-6), and reports the pause trigger as its snake_case wire value — including the one member whose
    /// wire value the plan fixes separately from its name (<c>autopilot_channel_unsupported</c>, 02.8.3 plan §3.6 row
    /// 6b).
    /// </summary>
    [Fact]
    public async Task Latest_OrdersBySlot_AndReportsTheWirePauseReason()
    {
        var world = await SeedWorldAsync();

        // The later slot is planned first and the earlier slot last: only `slot_at` order names the later one.
        var laterSlot = await SeedRunAsync(
            world,
            Start.AddHours(2),
            AutopilotRunOutcome.Paused,
            state: AutopilotRunState.Paused,
            pauseReason: AutopilotPauseReason.ChannelUnsupported,
            now: Start);
        await SeedRunAsync(
            world,
            Start.AddHours(1),
            AutopilotRunOutcome.Paused,
            state: AutopilotRunState.Paused,
            pauseReason: AutopilotPauseReason.OwnerPaused,
            now: Start.AddHours(3));

        var latest = await Reader(world).GetLatestAsync(world.SeriesId, CancellationToken.None);

        latest.ShouldNotBeNull();
        latest.RunId.ShouldBe(laterSlot);
        latest.Outcome.ShouldBe(AutopilotRunOutcome.Paused);
        latest.PauseReason.ShouldBe("autopilot_channel_unsupported");
    }

    /// <summary>
    /// The reader refuses an anonymous scope: the tenant filter (09 README D10) would hide every row and answer the
    /// streak as 0, which is exactly the answer that lets a series keep publishing after two gate failures.
    /// </summary>
    [Fact]
    public async Task AnonymousScope_IsRefused()
    {
        var world = await SeedWorldAsync();
        await SeedRunAsync(world, Start, AutopilotRunOutcome.GateFailed);

        var anonymous = new GateFailureStreakReader(Database.CreateAppDbContext(new TestCurrentUser()));

        await Should.ThrowAsync<InvalidOperationException>(
            () => anonymous.GetStreakAsync(world.SeriesId, CancellationToken.None).AsTask());
    }

    private GateFailureStreakReader Reader(World world) =>
        new(Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }));

    private async Task<World> SeedWorldAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: CancellationToken.None);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, title: "Gate series");
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(user.Id, series.Id, title: "Tập cổng", ct: CancellationToken.None);

        return new World(user.Id, series.Id, episode.Id, actor);
    }

    /// <summary>
    /// One <c>autopilot_runs</c> row of the world's series; <paramref name="slotAt"/> is both the slot and the
    /// planned instant unless <paramref name="now"/> says otherwise, and <paramref name="outcome"/> null leaves the
    /// run in flight.
    /// </summary>
    private async Task<Guid> SeedRunAsync(
        World world,
        DateTimeOffset slotAt,
        AutopilotRunOutcome? outcome,
        AutopilotRunState? state = null,
        AutopilotPauseReason? pauseReason = null,
        DateTimeOffset? now = null)
    {
        var seededState = state ?? outcome switch
        {
            null => AutopilotRunState.Planned,
            AutopilotRunOutcome.GateFailed => AutopilotRunState.GateFailed,
            AutopilotRunOutcome.Published => AutopilotRunState.Published,
            _ => AutopilotRunState.Planned,
        };

        var run = await new AutopilotRunBuilder(Database, world.Actor).CreateAsync(
            world.UserId,
            world.SeriesId,
            slotAt: slotAt,
            startAt: slotAt.AddDays(-2),
            now: now ?? slotAt,
            episodeId: world.EpisodeId,
            state: seededState,
            outcome: outcome,
            pauseReason: pauseReason,
            ct: CancellationToken.None);

        return run.Id;
    }

    /// <summary>The series, its owner and its episode, as the seeded rows name them.</summary>
    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId, TestCurrentUser Actor);
}
