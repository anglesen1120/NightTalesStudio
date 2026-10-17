using System.Globalization;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Api.IntegrationTests.SeriesBible;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Structure;
using NightTales.Infrastructure.Autopilot;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// 02.8.1-T2 task Design: <c>IdeaCorpusProvider</c> — the newest approved episodes of the series and its live
/// pending ideas, both read through the real read model (real PostgreSQL, real EF query, real tenant filter) with
/// the limit, the order and the JSON projection in SQL. The only fake is the clock the rows were seeded with.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IdeaCorpusProviderTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task GetAsync_ReturnsTheNewestApprovedEpisodes_ExcludesDrafts_AndTruncatesTheSummary()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var older = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        var newer = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        var draft = await SeriesBibleHarness.SeedDraftEpisodeAsync(Database, world);

        SetVersion(Database, older, Start, new string('x', IdeaCorpusProviderTests.SummaryOverflow));
        SetVersion(Database, newer, Start.AddHours(1), "The newest summary.");

        // Precondition: the draft has no approved version, so the join can never reach it.
        draft.ShouldNotBe(Guid.Empty);

        var corpus = await Provider(world.UserId, corpusEpisodes: 100).GetAsync(world.SeriesId, [], Ct);

        corpus.Episodes.Select(episode => episode.EpisodeId).ShouldBe([newer, older], "story_versions.created_at DESC");
        corpus.Episodes.ShouldNotContain(episode => episode.EpisodeId == draft);
        corpus.Episodes[0].Summary.ShouldBe("The newest summary.");

        // Task Design / D19: at most 200 characters of `content.description` ever leave the database.
        corpus.Episodes[1].Summary.Length.ShouldBe(200);
        corpus.Episodes[1].Summary.ShouldBe(new string('x', 200));
        corpus.Episodes[0].Title.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetAsync_TakesAtMostTheCorpusCap()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);

        var corpus = await Provider(world.UserId, corpusEpisodes: 1).GetAsync(world.SeriesId, [], Ct);

        corpus.Episodes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_AnotherSeriesOrOwner_IsNeverReturned()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var other = await SeriesBibleHarness.SeedAsync(Database);
        var mine = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        var theirs = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, other);

        var run = await SeedRunAsync(Database, other.UserId, other.SeriesId);
        var theirIdea = await SeedIdeaAsync(Database, other.UserId, other.SeriesId, run, "Their pending idea", Start);

        // Precondition: the owner sees their own row, so the empty reads below are meaningful.
        (await Provider(world.UserId, 100).GetAsync(world.SeriesId, [], Ct)).Episodes.ShouldHaveSingleItem().EpisodeId.ShouldBe(mine);

        var corpus = await Provider(world.UserId, 100).GetAsync(world.SeriesId, [], Ct);

        corpus.Episodes.ShouldNotContain(episode => episode.EpisodeId == theirs);
        corpus.PendingIdeas.ShouldNotContain(idea => idea.IdeaId == theirIdea);

        // The other owner's own view is empty for this series too: both reads are series-scoped and tenant-filtered.
        var foreign = await Provider(other.UserId, 100).GetAsync(world.SeriesId, [], Ct);
        foreign.Episodes.ShouldBeEmpty();
        foreign.PendingIdeas.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAsync_PendingIdeas_AreTheProposedAndPickedOnes_MinusTheBatchOwnIds()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var run = await SeedRunAsync(Database, world.UserId, world.SeriesId);

        var proposed = await SeedIdeaAsync(Database, world.UserId, world.SeriesId, run, "An earlier proposed idea", Start.AddMinutes(1));
        var picked = await SeedIdeaAsync(Database, world.UserId, world.SeriesId, run, "An earlier picked idea", Start.AddMinutes(2));
        var rejected = await SeedIdeaAsync(Database, world.UserId, world.SeriesId, run, "An earlier rejected idea", Start.AddMinutes(3));
        SqlProbe.Execute(Database, $"UPDATE episode_ideas SET status = 'picked' WHERE id = '{Pg(picked)}'");
        SqlProbe.Execute(Database, $"UPDATE episode_ideas SET status = 'rejected', rejected_reason = 'not_best' WHERE id = '{Pg(rejected)}'");

        // newest first, `rejected` (history the episode corpus covers) left out
        var corpus = await Provider(world.UserId, 100).GetAsync(world.SeriesId, [], Ct);

        corpus.PendingIdeas.Select(idea => idea.IdeaId).ShouldBe([picked, proposed]);
        corpus.PendingIdeas[0].Title.ShouldBe("An earlier picked idea");
        corpus.PendingIdeas[0].Logline.ShouldNotBeNullOrWhiteSpace();

        // The batch's own rows are `proposed` too, so the caller passes their ids and they drop out.
        var withoutOwn = await Provider(world.UserId, 100).GetAsync(world.SeriesId, [proposed, picked], Ct);

        withoutOwn.PendingIdeas.ShouldBeEmpty();
    }

    /// <summary>The reader over the class database, scoped to one owner exactly as a request scope is.</summary>
    private IdeaCorpusProvider Provider(Guid userId, int corpusEpisodes) =>
        new(
            Database.CreateReadDbContext(new TestCurrentUser { Id = userId }),
            new IdeaNoveltyOptions { CorpusEpisodes = corpusEpisodes });

    /// <summary>
    /// Rewrites one approved version's ordering instant and description. The provider projects only
    /// <c>content-&gt;&gt;'description'</c>, so a minimal document is all the corpus can see.
    /// </summary>
    private static void SetVersion(TestDatabase database, Guid episodeId, DateTimeOffset createdAt, string description) =>
        SqlProbe.Execute(
            database,
            string.Create(
                CultureInfo.InvariantCulture,
                $"UPDATE story_versions SET created_at = '{createdAt:O}', content = jsonb_build_object('schema_version', 'story.v1', 'description', '{description}') WHERE episode_id = '{Pg(episodeId)}'"));

    /// <summary>The <c>generation_runs</c> row the ideas of one series hang off (the same-owner composite FK).</summary>
    private static async Task<Guid> SeedRunAsync(TestDatabase database, Guid userId, Guid seriesId)
    {
        var reservationId = Guid.CreateVersion7();
        var queued = GenerationRun.Queue(
            userId,
            seriesId,
            episodeId: null,
            GenerationKind.Ideas,
            GenerationTrigger.Autopilot,
            string.Create(CultureInfo.InvariantCulture, $"autopilot:{seriesId:D}:{Start:O}:idea"),
            previousEpisodeStatus: null,
            inputFactSheetId: null,
            reservationId,
            worstCaseUsd: 0.25m,
            Start,
            countsTowardLimit: false);
        queued.IsError.ShouldBeFalse();

        await using (var context = database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start)))
        {
            context.GenerationRuns.Add(queued.Value);
            await context.SaveChangesAsync(Ct);
        }

        return queued.Value.Id;
    }

    /// <summary>One <c>proposed</c> idea row of the series, written through the Domain factory.</summary>
    private static async Task<Guid> SeedIdeaAsync(
        TestDatabase database,
        Guid userId,
        Guid seriesId,
        Guid runId,
        string title,
        DateTimeOffset createdAt)
    {
        var clock = new FakeClock(createdAt);
        var candidate = new IdeaCandidate(
            title,
            "A logline for the corpus.",
            "A hook for the corpus.",
            "An old house.",
            new StoryStructureTags("truyện ma", "ngôi nhà cũ", "một cuộc điện thoại", "người kể là nguyên nhân", "first_person", ["setup", "clue", "twist"]),
            ["setup", "clue", "twist"]);
        var idea = EpisodeIdea.CreateProposed(seriesId, userId, runId, candidate, clock);

        await using (var context = database.CreateAppDbContext(new TestCurrentUser { Id = userId }, clock))
        {
            context.EpisodeIdeas.Add(idea);
            await context.SaveChangesAsync(Ct);
        }

        return idea.Id;
    }

    private static string Pg(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private const int SummaryOverflow = 500;
}
