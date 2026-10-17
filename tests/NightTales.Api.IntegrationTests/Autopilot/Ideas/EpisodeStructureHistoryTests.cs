using System.Globalization;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Api.IntegrationTests.SeriesBible;
using NightTales.Infrastructure.Autopilot;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// 02.8.1-T1 plan §3.5: <c>EpisodeStructureHistory</c> — the approved-episode projection the empty-bible idea
/// data object and 02.8.4-T1's variety guard read. Real PostgreSQL, real EF query, real tenant filter; the only
/// fake is the clock the rows were seeded with.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EpisodeStructureHistoryTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task GetRecentAsync_ReturnsApprovedEpisodesWithTheirDerivedStructure_AndSkipsDrafts()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var first = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        var second = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        var draft = await SeriesBibleHarness.SeedDraftEpisodeAsync(Database, world);

        var history = await History(world.UserId).GetRecentAsync(world.SeriesId, max: 50, SeriesBibleHarness.Ct);

        history.Count.ShouldBe(2, "an episode with no approved story version is not history");
        history.Select(row => row.EpisodeId).ShouldBe([first, second], ignoreOrder: true);
        history.Select(row => row.EpisodeId).ShouldNotContain(draft);

        foreach (var candidate in history)
        {
            candidate.ShouldSatisfyAllConditions(
                row => row.Title.ShouldBe("Căn nhà cuối hành lang", "the title comes from the approved story version"),
                row => row.Structure.SubGenre.ShouldBe("truyện ma tâm linh"),
                row => row.Structure.Beats.ShouldBe(["setup", "twist", "reveal"], "the outline is the paragraph beats in order"),
                row => row.BeatSignature.ShouldNotBeNullOrWhiteSpace(),
                row => row.ApprovedAt.ShouldBe(SeriesBibleHarness.Start));
        }
    }

    [Fact]
    public async Task GetRecentAsync_TenantFilter_AnotherUsersSeriesHasNoHistory()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);

        // Precondition: the owner sees the row, so the empty read below is meaningful.
        (await History(world.UserId).GetRecentAsync(world.SeriesId, max: 50, SeriesBibleHarness.Ct)).Count.ShouldBe(1);

        var other = await History(Guid.Parse("0197a000-0000-7000-8000-0000000000ff")).GetRecentAsync(
            world.SeriesId,
            max: 50,
            SeriesBibleHarness.Ct);

        other.ShouldBeEmpty("the read model's tenant filter hides another owner's episodes");
    }

    [Fact]
    public async Task GetRecentAsync_TakesAtMostMax()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);

        var history = await History(world.UserId).GetRecentAsync(world.SeriesId, max: 1, SeriesBibleHarness.Ct);

        history.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetRecentAsync_ADamagedVersion_IsSkippedInsteadOfThrowing()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episodeId = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        var versionId = SqlProbe.ScalarText(
            Database,
            $"SELECT id FROM story_versions WHERE episode_id = '{episodeId.ToString("D", CultureInfo.InvariantCulture)}'");

        // Precondition: the projection does read this row while its version is intact.
        (await History(world.UserId).GetRecentAsync(world.SeriesId, max: 50, SeriesBibleHarness.Ct)).Count.ShouldBe(1);

        // A hand-edited or pre-D3 row: valid JSON that is not a readable story.v1 document.
        SqlProbe.Execute(
            Database,
            $"UPDATE story_versions SET content = '{{\"schema_version\":\"story.v1\"}}' WHERE id = '{versionId}'");

        var history = await History(world.UserId).GetRecentAsync(world.SeriesId, max: 50, SeriesBibleHarness.Ct);

        history.ShouldBeEmpty("one damaged old version must not fail the whole series");
    }

    [Fact]
    public async Task GetRecentAsync_NonPositiveMax_Throws()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await History(Guid.NewGuid()).GetRecentAsync(Guid.NewGuid(), max: 0, SeriesBibleHarness.Ct));
    }

    /// <summary>The history reader over the class database, scoped to one owner exactly as a request scope is.</summary>
    private EpisodeStructureHistory History(Guid userId) =>
        new(Database.CreateReadDbContext(new TestCurrentUser { Id = userId }));
}
