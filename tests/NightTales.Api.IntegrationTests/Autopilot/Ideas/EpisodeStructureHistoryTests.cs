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

    /// <summary>
    /// 02.8.4-T1: the window the variety guard reads is the newest approved episodes only — a draft episode has
    /// no <c>approved_story_version_id</c> and never enters it — ordered by the approved version's creation
    /// (the plan's proxy for the approval instant), newest first.
    /// </summary>
    [Fact]
    public async Task EpisodeStructureHistory_ReturnsNewestApprovedOnly()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var older = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        var newer = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        var draft = await SeriesBibleHarness.SeedDraftEpisodeAsync(Database, world);

        // Both seeded versions share the fixture clock, so pin their ordering instant to make the order exact.
        SetVersionCreatedAt(Database, older, SeriesBibleHarness.Start);
        SetVersionCreatedAt(Database, newer, SeriesBibleHarness.Start.AddHours(1));

        var history = await History(world.UserId).GetRecentAsync(world.SeriesId, max: 50, SeriesBibleHarness.Ct);

        history.Select(row => row.EpisodeId)
            .ShouldBe([newer, older], "story_versions.created_at DESC: the newest approved episode is first");
        history.ShouldNotContain(row => row.EpisodeId == draft);
        history.Count.ShouldBe(2, "an episode with no approved story version is not history");
    }

    /// <summary>
    /// 02.8.4-T1 R3: the reference's signature is the approved version's body paragraph beats in order, joined
    /// with <c>&gt;</c> — the value the template-repeat rule compares.
    /// </summary>
    [Fact]
    public async Task EpisodeStructureHistory_BeatSignatureFromParagraphBeats()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episodeId = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);

        var history = await History(world.UserId).GetRecentAsync(world.SeriesId, max: 50, SeriesBibleHarness.Ct);

        var row = history.ShouldHaveSingleItem();
        row.EpisodeId.ShouldBe(episodeId);
        row.Structure.Beats.ShouldBe(["setup", "twist", "reveal"], "the beats are the paragraphs' own, in order");
        row.BeatSignature.ShouldBe("setup>twist>reveal", "the signature is the beats joined with '>'");
    }

    /// <summary>
    /// 02.8.4-T1 failure table: an unmapped structure is kept exactly as stored, and a version with no paragraph
    /// beats yields an empty signature, which R3 (template repeat) can never match — the rule fails open on
    /// missing data instead of failing closed.
    /// </summary>
    [Fact]
    public async Task EpisodeStructureHistory_UnmappedStructure_EmptySignatureDoesNotMatch()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episodeId = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        SqlProbe.Execute(
            Database,
            $$"""
              UPDATE story_versions
              SET content = jsonb_set(
                  jsonb_set(content, '{structure,sub_genre}', '"other:nhà ma"'),
                  '{paragraphs}', '[]'::jsonb)
              WHERE episode_id = '{{episodeId.ToString("D", CultureInfo.InvariantCulture)}}'
              """);

        var history = await History(world.UserId).GetRecentAsync(world.SeriesId, max: 50, SeriesBibleHarness.Ct);

        var row = history.ShouldHaveSingleItem();
        row.Structure.SubGenre.ShouldBe("other:nhà ma", "an unmapped tag is compared exactly as stored");
        row.Structure.Beats.ShouldBeEmpty();
        row.BeatSignature.ShouldBeEmpty("R3 needs a non-empty exact match, so an empty signature never fires");
    }

    /// <summary>Rewrites one approved version's ordering instant, the only order the projection has.</summary>
    private static void SetVersionCreatedAt(TestDatabase database, Guid episodeId, DateTimeOffset createdAt) =>
        SqlProbe.Execute(
            database,
            string.Create(
                CultureInfo.InvariantCulture,
                $"UPDATE story_versions SET created_at = '{createdAt:O}' WHERE episode_id = '{episodeId:D}'"));
}
