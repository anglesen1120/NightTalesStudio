using System.Globalization;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Api.IntegrationTests.SeriesBible;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Structure;
using NightTales.Infrastructure.Autopilot;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// 02.8.4-T1 plan §"Steering and verification wiring": <c>StructurePlanProvider</c> — the story-time
/// <c>structure_plan</c> built from the episode's own <c>used</c> idea and the series' <em>current</em> history.
/// Real PostgreSQL, real EF reads, real tenant filter; the only fake is the clock the rows were seeded with (and
/// the counting history reader of the one test that asserts the read is bounded).
/// </summary>
[Trait("Category", "Integration")]
public sealed class StructurePlanProviderTests(TestDatabase database) : IntegrationTest(database)
{
    private const string HistorySubGenre = "truyện ma tâm linh";
    private const string HistorySetting = "ngôi nhà cũ";
    private const string IdeaSubGenre = "truyện ma";
    private const string IdeaSetting = "ngôi nhà cũ";
    private const string OtherSetting = "căn hộ mới";
    private const string IdeaOpening = "một cuộc gọi lúc nửa đêm";
    private const string IdeaTwist = "kẻ trốn trong nhà";

    private static readonly string[] IdeaBeats = ["setup", "clue", "twist"];

    private static CancellationToken Ct => SeriesBibleHarness.Ct;

    private static VarietyOptions Options(
        int windowK = 10,
        int templateWindowK = 5,
        decimal tagShareMax = 0.40m,
        int rotationMinCount = 3,
        bool enabled = true) =>
        new(windowK, templateWindowK, tagShareMax, rotationMinCount, enabled);

    [Fact]
    public async Task StructurePlanProvider_UserEpisodeWithoutIdea_ReturnsNull()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episode = await new EpisodeBuilder(Database, new TestCurrentUser { Id = world.UserId })
            .CreateAsync(world.UserId, world.SeriesId, ct: Ct);

        var plan = await Provider(world.UserId).GetForEpisodeAsync(episode.Id, Ct);

        plan.ShouldBeNull("a user-created episode has no used idea, so the writer data object omits structure_plan");
    }

    [Fact]
    public async Task StructurePlanProvider_AutopilotEpisode_PlanReflectsCurrentHistory()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episodeId = await SeedUsedIdeaAsync(Database, world, OwnTags(IdeaSetting), IdeaBeats);

        // No history yet (R5): the idea's own structure is the only steering, and nothing is forbidden.
        var before = await Provider(world.UserId).GetForEpisodeAsync(episodeId, Ct);
        before.ShouldNotBeNull();
        before.Required.Setting.ShouldBe(IdeaSetting, "the guard has no suggestion, so the idea's own value steers");
        before.Forbidden.Setting.ShouldBeEmpty("no history, so nothing is over the tag-share cap");

        // The history grows between the idea and the story: the plan is rebuilt from the live window, never reused.
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);

        var after = await Provider(world.UserId).GetForEpisodeAsync(episodeId, Ct);
        after.ShouldNotBeNull();
        after.Forbidden.Setting.ShouldContain(IdeaSetting, "the idea's own setting is now above the cap");
        after.Required.Setting.ShouldBeNull("the same value is forbidden, so it is never also required");
        after.Forbidden.SubGenre.ShouldContain(HistorySubGenre);

        // Nothing was written back to the idea: the row still carries the structure it was created with (the plan
        // is derived, not persisted — task [DECISION]).
        StoredStructureField(Database, episodeId, "setting").ShouldBe(IdeaSetting);
    }

    [Fact]
    public async Task StructurePlanProvider_MergesTheIdeasOwnStructure_WhenTheGuardHasNoSuggestion()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episodeId = await SeedUsedIdeaAsync(Database, world, OwnTags(IdeaSetting), IdeaBeats);

        var plan = await Provider(world.UserId).GetForEpisodeAsync(episodeId, Ct);

        plan.ShouldNotBeNull();
        plan.Required.ShouldBe(new StructureRequirements(IdeaSubGenre, IdeaSetting, IdeaOpening, IdeaTwist));
        plan.Forbidden.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task StructurePlanProvider_GuardSuggestion_OutranksTheIdeasOwnStructure()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        var episodeId = await SeedUsedIdeaAsync(Database, world, OwnTags(OtherSetting), IdeaBeats);

        // Nothing is over the cap (TagShareMax = 1) and the window's only values are below the rotation floor, so
        // the guard requires them: the idea's own values must not overwrite a suggestion it does have.
        var plan = await Provider(world.UserId, Options(tagShareMax: 1m, rotationMinCount: 5))
            .GetForEpisodeAsync(episodeId, Ct);

        plan.ShouldNotBeNull();
        plan.Required.Setting.ShouldBe(HistorySetting, "the guard's suggestion wins over the idea's own setting");
        plan.Required.SubGenre.ShouldBe(HistorySubGenre);
        plan.Required.Setting.ShouldNotBe(OtherSetting);
    }

    [Fact]
    public async Task StructurePlanProvider_ForbiddenOwnValue_IsNotMerged()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        var episodeId = await SeedUsedIdeaAsync(Database, world, OwnTags(IdeaSetting), IdeaBeats);

        // The idea's own setting repeats the saturated history value, so R2 forbids it (and R4, which never
        // requires a forbidden value, suggests nothing for that dimension). The merge must not re-add it: the
        // writer would otherwise be told to use and avoid the same value at once.
        var plan = await Provider(world.UserId).GetForEpisodeAsync(episodeId, Ct);

        plan.ShouldNotBeNull();
        plan.Forbidden.Setting.ShouldContain(IdeaSetting);
        plan.Required.Setting.ShouldBeNull("a forbidden value is never also required");
        plan.Required.SubGenre.ShouldBe(IdeaSubGenre, "a dimension the plan does not forbid still merges the idea's own value");
    }

    [Fact]
    public async Task StructurePlanProvider_Disabled_StillSteers()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        _ = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        var episodeId = await SeedUsedIdeaAsync(Database, world, OwnTags(OtherSetting), IdeaBeats);

        // Autopilot:Variety:Enabled=false disables the idea step's rejection and penalty only: the story-time
        // plan (the batch assessment, R4's rotation and the forbidden set) is still produced — steering only.
        var plan = await Provider(
                world.UserId,
                Options(tagShareMax: 1m, rotationMinCount: 5, enabled: false))
            .GetForEpisodeAsync(episodeId, Ct);

        plan.ShouldNotBeNull();
        plan.Required.Setting.ShouldBe(HistorySetting);
    }

    [Fact]
    public async Task StructurePlanProvider_NothingToSteer_ReturnsNull()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episodeId = await SeedUsedIdeaAsync(Database, world, StoryStructureTags.Empty, []);

        var plan = await Provider(world.UserId).GetForEpisodeAsync(episodeId, Ct);

        plan.ShouldBeNull("an empty structure with no history steers nothing, so structure_plan is omitted");
    }

    [Fact]
    public async Task StructurePlanProvider_AnotherUsersEpisode_ReturnsNull()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var other = await SeriesBibleHarness.SeedAsync(Database);
        var episodeId = await SeedUsedIdeaAsync(Database, world, OwnTags(IdeaSetting), IdeaBeats);

        // Precondition: the owner sees their own plan, so the null below is the tenant filter and not an absence.
        (await Provider(world.UserId).GetForEpisodeAsync(episodeId, Ct)).ShouldNotBeNull();

        var foreign = await Provider(other.UserId).GetForEpisodeAsync(episodeId, Ct);

        foreign.ShouldBeNull("the read model's tenant filter hides another owner's episode and idea (D10)");
    }

    [Fact]
    public async Task StructurePlanProvider_ReadsTheHistoryOnce_WithTheWiderWindow()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episodeId = await SeedUsedIdeaAsync(Database, world, OwnTags(IdeaSetting), IdeaBeats);
        var history = new CountingHistory([]);

        var plan = await Provider(world.UserId, Options(windowK: 4, templateWindowK: 5), history)
            .GetForEpisodeAsync(episodeId, Ct);

        plan.ShouldNotBeNull();
        history.Calls.ShouldBe(1, "one bounded read, never a query per episode");
        history.LastMax.ShouldBe(5, "max(WindowK, TemplateWindowK)");
        history.LastSeriesId.ShouldBe(world.SeriesId, "the read is scoped to the idea's own series");
    }

    /// <summary>
    /// The window is clamped to the idea context's own history cap (<c>IdeaContextProvider.HistoryCount</c>): a
    /// wider <c>WindowK</c> cannot reach here through the boot validator, but a hand-built options instance must
    /// still not turn the bounded read into an unbounded one.
    /// </summary>
    [Fact]
    public async Task StructurePlanProvider_WindowWiderThanTheIdeaContextCap_IsClampedToTheCap()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episodeId = await SeedUsedIdeaAsync(Database, world, OwnTags(IdeaSetting), IdeaBeats);
        var history = new CountingHistory([]);

        _ = await Provider(world.UserId, Options(windowK: 1_000_000, templateWindowK: 5), history)
            .GetForEpisodeAsync(episodeId, Ct);

        history.Calls.ShouldBe(1, "one bounded read, never a query per episode");
        history.LastMax.ShouldBe(IdeaContextProvider.HistoryCount, "the read is clamped to the idea context history cap");
    }

    /// <summary>The provider over the class database, scoped to one owner exactly as a request scope is.</summary>
    private StructurePlanProvider Provider(Guid userId, VarietyOptions? options = null, IEpisodeStructureHistory? history = null) =>
        new(
            Database.CreateReadDbContext(new TestCurrentUser { Id = userId }),
            history ?? new EpisodeStructureHistory(Database.CreateReadDbContext(new TestCurrentUser { Id = userId })),
            options ?? Options());

    private static StoryStructureTags OwnTags(string setting) =>
        new(IdeaSubGenre, setting, IdeaOpening, IdeaTwist, "first_person", IdeaBeats);

    /// <summary>
    /// Seeds one <c>used</c> idea and the episode it became: the run row, the episode and the idea's own
    /// structure (plan §"Steering and verification wiring"; the caller owns the run, as in production).
    /// </summary>
    private static async Task<Guid> SeedUsedIdeaAsync(
        TestDatabase database,
        World world,
        StoryStructureTags tags,
        IReadOnlyList<string> beats)
    {
        var actor = new TestCurrentUser { Id = world.UserId };
        var clock = new FakeClock(SeriesBibleHarness.Start);

        var reservationId = Guid.CreateVersion7();
        var queued = GenerationRun.Queue(
            world.UserId,
            world.SeriesId,
            episodeId: null,
            GenerationKind.Ideas,
            GenerationTrigger.Autopilot,
            string.Create(CultureInfo.InvariantCulture, $"autopilot:{world.SeriesId:D}:{SeriesBibleHarness.Start:O}:idea"),
            previousEpisodeStatus: null,
            inputFactSheetId: null,
            reservationId,
            worstCaseUsd: 0.25m,
            SeriesBibleHarness.Start,
            countsTowardLimit: false);
        queued.IsError.ShouldBeFalse();
        await using (var context = database.CreateAppDbContext(actor, clock))
        {
            context.GenerationRuns.Add(queued.Value);
            await context.SaveChangesAsync(Ct);
        }

        var episode = await new EpisodeBuilder(database, actor)
            .CreateAsync(world.UserId, world.SeriesId, ct: Ct);

        var candidate = new IdeaCandidate(
            "Tập của ý tưởng",
            "Một lời tóm tắt cho kiểm thử.",
            "Một móc nối cho kiểm thử.",
            "Một chỗ ở cho kiểm thử.",
            tags,
            beats);
        var idea = EpisodeIdea.CreateProposed(world.SeriesId, world.UserId, queued.Value.Id, candidate, clock);
        idea.MarkPicked(0.5m, clock);
        idea.MarkUsed(episode.Id, clock).IsError.ShouldBeFalse();

        await using (var context = database.CreateAppDbContext(actor, clock))
        {
            context.EpisodeIdeas.Add(idea);
            await context.SaveChangesAsync(Ct);
        }

        return episode.Id;
    }

    /// <summary>Reads one field of a stored <c>episode_ideas.structure</c> document, proving no plan was persisted.</summary>
    private static string StoredStructureField(TestDatabase database, Guid episodeId, string key) =>
        SqlProbe.ScalarText(
            database,
            string.Create(
                CultureInfo.InvariantCulture,
                $"SELECT structure->>'{key}' FROM episode_ideas WHERE episode_id = '{episodeId:D}'"));

    /// <summary>A hand-written history reader that counts its calls and records the window it was asked for.</summary>
    private sealed class CountingHistory(IReadOnlyList<EpisodeStructureRef> rows) : IEpisodeStructureHistory
    {
        /// <summary>How many times the provider read the history.</summary>
        public int Calls { get; private set; }

        /// <summary>The <c>max</c> of the last read.</summary>
        public int LastMax { get; private set; }

        /// <summary>The series of the last read.</summary>
        public Guid LastSeriesId { get; private set; }

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<EpisodeStructureRef>> GetRecentAsync(Guid seriesId, int max, CancellationToken ct)
        {
            Calls++;
            LastMax = max;
            LastSeriesId = seriesId;

            return ValueTask.FromResult(rows);
        }
    }
}
