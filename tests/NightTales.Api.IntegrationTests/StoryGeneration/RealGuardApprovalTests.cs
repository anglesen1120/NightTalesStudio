using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>
/// The real <c>FactSheetGuard</c> on the approve path (02.5.5-T1, 02 README D8/D21/F12/F13): the gate compares
/// the story's pinned <c>fact_sheet_id</c> with the sheet the registered guard returns, and the F13 distinct
/// source count is computed from the facts the guard hands over — so a fact an edit dropped stops counting, and
/// a source of another tenant never counts at all.
/// </summary>
/// <remarks>
/// Real <c>fact_sheets</c> and <c>story_sources</c> rows, the real gate and the registered real guard: this is
/// the hand-over 02.3.3-T1 left open ("the real-guard path stays unproven until 02.5.5-T1 lands"). The stories
/// are seeded through <see cref="StoryVersion.Create"/> with the pinned sheet, exactly as the generation job
/// writes them.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RealGuardApprovalTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Clean = """{"flagged":false,"categories":[],"blocking":[],"stale":false,"unavailable":false}""";

    private static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task ApproveStory_PinnedToASupersededSheet_Returns409StoryFactSheetOutdated()
    {
        var world = await SeedAsync();
        var first = await RealGuardFixtures.SeedFetchedSourceAsync(Database, world.UserId, world.EpisodeId, new string('a', 64), Start, Ct);
        var second = await RealGuardFixtures.SeedFetchedSourceAsync(Database, world.UserId, world.EpisodeId, new string('b', 64), Start, Ct);
        var superseded = await RealGuardFixtures.SheetAsync(
            Database, world.UserId, world.EpisodeId, 1, [], RealGuardFixtures.Aliases(), FactSheetStatus.Superseded, Start, Ct);
        var approved = await RealGuardFixtures.SheetAsync(
            Database,
            world.UserId,
            world.EpisodeId,
            2,
            [RealGuardFixtures.CitedFact("f1", "One.", first.Id, "s1"), RealGuardFixtures.CitedFact("f2", "Two.", second.Id, "s2")],
            RealGuardFixtures.Aliases(("s1", first.Id), ("s2", second.Id)),
            FactSheetStatus.Approved,
            Start,
            Ct);

        // The story was written from the sheet version 1 that the re-approval replaced.
        PinSheet(world, superseded.Id);
        using (var factory = Factory())
        using (var client = factory.ClientFor(world.UserId))
        using (var stale = await ApproveAsync(client, world))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, await stale.Content.ReadAsStringAsync(Ct));
            (await ProblemAsync(stale)).Code.ShouldBe("story_fact_sheet_outdated");
            AssertNothingApproved(world);
        }

        // The same story pinned to the sheet the guard returns approves: the pin, not the guard, was the refusal,
        // so the approved row really is resolvable and the run of the first attempt changed nothing.
        PinSheet(world, approved.Id);
        using (var factory = Factory())
        using (var client = factory.ClientFor(world.UserId))
        using (var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}"""))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }

        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_approved");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApproveStory_DroppedFactOnADistinctSource_IsNotCountedByF13(bool dropped)
    {
        var world = await SeedAsync();
        var first = await RealGuardFixtures.SeedFetchedSourceAsync(Database, world.UserId, world.EpisodeId, new string('a', 64), Start, Ct);
        var second = await RealGuardFixtures.SeedFetchedSourceAsync(Database, world.UserId, world.EpisodeId, new string('b', 64), Start, Ct);

        // The draft the edit started from cites both distinct sources; the approval that follows drops the fact
        // citing the second one, which is what "a dropped fact" means: it is not in ApprovedFactSheet.Facts.
        await RealGuardFixtures.SheetAsync(
            Database,
            world.UserId,
            world.EpisodeId,
            1,
            [RealGuardFixtures.CitedFact("f1", "One.", first.Id, "s1"), RealGuardFixtures.CitedFact("f2", "Two.", second.Id, "s2")],
            RealGuardFixtures.Aliases(("s1", first.Id), ("s2", second.Id)),
            FactSheetStatus.Draft,
            Start,
            Ct);
        FactItem[] keptFacts = dropped
            ? [RealGuardFixtures.CitedFact("f1", "One.", first.Id, "s1")]
            : [RealGuardFixtures.CitedFact("f1", "One.", first.Id, "s1"), RealGuardFixtures.CitedFact("f2", "Two.", second.Id, "s2")];
        IReadOnlyDictionary<string, Guid> keptAliases = dropped
            ? RealGuardFixtures.Aliases(("s1", first.Id))
            : RealGuardFixtures.Aliases(("s1", first.Id), ("s2", second.Id));
        var approved = await RealGuardFixtures.SheetAsync(
            Database, world.UserId, world.EpisodeId, 2, keptFacts, keptAliases, FactSheetStatus.Approved, Start, Ct);
        PinSheet(world, approved.Id);

        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");

        if (dropped)
        {
            // The dropped fact's source is fetched and distinct, but no kept fact cites it: F13 counts one.
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
            (await ProblemAsync(response)).Code.ShouldBe("story_sources_required");
            AssertNothingApproved(world);
            return;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_approved");
    }

    [Fact]
    public async Task ApproveStory_AliasToAForeignTenantsSource_IsNeverCountedByF13()
    {
        var stranger = await SeedAsync();
        var hidden = await RealGuardFixtures.SeedFetchedSourceAsync(Database, stranger.UserId, stranger.EpisodeId, new string('c', 64), Start, Ct);
        var world = await SeedAsync();

        // The approved row of this episode names another tenant's source id: the tenant filter hides that row, so
        // the guard treats it like a deleted source and the gate's own read can never count it (F13 stays at 0).
        var sheet = await RealGuardFixtures.SheetAsync(
            Database,
            world.UserId,
            world.EpisodeId,
            1,
            [RealGuardFixtures.CitedFact("f1", "A fact.", hidden.Id, "s1")],
            RealGuardFixtures.Aliases(("s1", hidden.Id)),
            FactSheetStatus.Approved,
            Start,
            Ct);
        PinSheet(world, sheet.Id);

        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ProblemAsync(response)).Code.ShouldBe("story_sources_required", "another tenant's source never counts toward F13");
        AssertNothingApproved(world);
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{world.UserId}'").ShouldBe(0);
    }

    /// <summary>Pins the current version to a real sheet row, with a passing stored score so only the sheet rules decide.</summary>
    private void PinSheet(World world, Guid sheetId) => SqlProbe.Execute(Database,
        $"UPDATE story_versions SET fact_sheet_id = '{sheetId}', similarity_score = 0 WHERE id = '{world.VersionId}'");

    private void AssertNothingApproved(World world)
    {
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_review");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM episodes WHERE id = '{world.EpisodeId}' AND (approved_story_version_id IS NOT NULL OR approved_at IS NOT NULL)")
            .ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE type = 'story_approved.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(0);
    }

    private ApiFactory Factory() =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000");

    private static async Task<HttpResponseMessage> ApproveAsync(HttpClient client, World world, string? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{world.EpisodeId}/story/approve");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        request.Headers.TryAddWithoutValidation("If-Match", StoryEtag.For(world.VersionId));

        return await client.SendAsync(request, Ct);
    }

    private static async Task<ProblemResponse> ProblemAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<ProblemResponse>(
            await response.Content.ReadAsStringAsync(Ct),
            ProblemResponse.Options)!;

    /// <summary>A true-crime series, a <c>story_review</c> episode and its one current version, unpinned.</summary>
    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: Topic.TrueCrime, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(
            user.Id,
            series.Id,
            lengthTier: LengthTier.Short,
            status: EpisodeStatus.StoryReview,
            ct: Ct);
        var draft = Draft();
        var version = StoryVersion.Create(
            user.Id,
            episode.Id,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            "model-x",
            "rel=1",
            null,
            null,
            null,
            Audit(),
            null,
            Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'");

        return new World(user.Id, series.Id, episode.Id, version.Id);
    }

    /// <summary>The clean <c>audit.v1</c> report of an approved-looking story (02.3.3-T1's shape).</summary>
    private static string Audit() =>
        $$$"""{"schema_version":"audit.v1","local":{"issues":[]},"moderation":{{{Clean}}},"model_review":{"scores":{"overall":7.5},"stale":true},"revision":{"rounds":2},"similarity":{"score":null,"n":0}}""";

    /// <summary>
    /// The approve fixture: every segment keyword is a keyword-rules phrase of 3 to 12 words, because the
    /// approve path runs <c>RenderKeywordLinter</c> over the version being approved (03.2.2-T2).
    /// </summary>
    private static StoryContentV1 Draft() => new(
        "The house by the river",
        new StorySegmentV1("The night was quiet.", ["house by the river", "quiet night by the river"], []),
        [new StorySegmentV1("A knock woke the narrator.", ["wooden door of the old house"], [], "setup")],
        new StorySegmentV1("Nobody stood outside.", ["empty path by the river"], []),
        "A quiet true-crime story.", ["case", "night"], ["documentary"],
        new StoryStructureV1("case", "old house", "a knock", "the narrator", "first_person"));

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId, Guid VersionId);
}
