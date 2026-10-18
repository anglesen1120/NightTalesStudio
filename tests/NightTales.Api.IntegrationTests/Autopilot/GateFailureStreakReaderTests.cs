using NightTales.Application.Abstractions;
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
/// outcomes, the window cap, and what resets the count.
/// </summary>
/// <remarks>
/// The rows are seeded with raw SQL because <c>autopilot_runs</c> has no Domain factory yet: 02.8.3-T1 adds the run
/// state machine and with it the write path (the same reason <c>CoreSchemaBoundaryTests</c> inserts a run that way).
/// Everything else — the user, the series, the episode — goes through the real builders, so the foreign keys the
/// composite constraints require are the ones production writes, and the reader under test is the production one.
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

        await SeedRunAsync(world, Start, "gate_failed");
        await SeedRunAsync(world, Start.AddMinutes(15), "gate_failed");

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

        await SeedRunAsync(world, Start, "gate_failed");
        await SeedRunAsync(world, Start.AddMinutes(15), "gate_failed");
        var newest = await SeedRunAsync(world, Start.AddMinutes(30), "gate_failed");

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

        await SeedRunAsync(world, Start, "gate_failed");
        await SeedRunAsync(world, Start.AddMinutes(15), "published");
        await SeedRunAsync(world, Start.AddMinutes(30), "gate_failed");

        (await Reader(world).GetStreakAsync(world.SeriesId, CancellationToken.None)).ShouldBe(0);
    }

    /// <summary>A run that has not stopped (<c>outcome IS NULL</c>) is not counted as a failure.</summary>
    [Fact]
    public async Task NonTerminalRun_IsIgnored()
    {
        var world = await SeedWorldAsync();

        await SeedRunAsync(world, Start, "gate_failed");
        await SeedRunAsync(world, Start.AddMinutes(15), outcome: null);

        (await Reader(world).GetStreakAsync(world.SeriesId, CancellationToken.None)).ShouldBe(1);
    }

    /// <summary>
    /// The reader refuses an anonymous scope: the tenant filter (09 README D10) would hide every row and answer the
    /// streak as 0, which is exactly the answer that lets a series keep publishing after two gate failures.
    /// </summary>
    [Fact]
    public async Task AnonymousScope_IsRefused()
    {
        var world = await SeedWorldAsync();
        await SeedRunAsync(world, Start, "gate_failed");

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

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary>One <c>autopilot_runs</c> row; <paramref name="outcome"/> is the stored snake_case wire value.</summary>
    private async Task<Guid> SeedRunAsync(World world, DateTimeOffset startedAt, string? outcome)
    {
        var outcomeSql = outcome is null ? "NULL" : $"'{outcome}'";
        var runId = Guid.CreateVersion7();

        await Database.ExecuteAsync(
            "INSERT INTO autopilot_runs (id, user_id, series_id, episode_id, started_at, outcome, created_at, updated_at) "
            + $"VALUES ('{runId}', '{world.UserId}', '{world.SeriesId}', '{world.EpisodeId}', "
            + $"'{startedAt:O}', {outcomeSql}, now(), now())");

        return runId;
    }

    /// <summary>The series, its owner and its episode, as the seeded rows name them.</summary>
    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
