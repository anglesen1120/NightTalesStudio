using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Api.IntegrationTests.StoryGeneration;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Time;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Stories;

/// <summary>
/// The 03 D10 keyword lint on the approve path (03.2.2-T2): <c>POST /api/v1/episodes/{id}/story/approve</c>
/// answers 409 <c>keywords_flagged</c> with an <c>errors[]</c> row when a keyword repeats a two-token name run
/// of the episode's own research, and the refusal sits behind the 412 and before the moderation refresh.
/// </summary>
/// <remarks>
/// The real <c>StoryApprovalGate</c> and the real <c>NameCorpusProvider</c> run here over real
/// <c>story_versions</c>, <c>story_sources</c>, <c>fact_sheets</c> and episode rows
/// (<see cref="RealGuardFixtures"/>, the 02.5.5-T1 shape), so the 409 is produced by the production chain and
/// not by a scripted gate. Only the moderation provider is a fake: the fixture that proves the lint runs
/// <em>before</em> stage 7 queues a blocking verdict and asserts it was never reached.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ApproveStory_KeywordsFlaggedTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The clean draft moderation verdict (02 D12): non-stale, so stage 7 needs no refresh.</summary>
    private const string Clean = """{"flagged":false,"categories":[],"blocking":[],"stale":false,"unavailable":false}""";

    /// <summary>The true-crime attestation body (02 D8 3e), accepted under the configured text version.</summary>
    private const string Attestation = """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""";

    /// <summary>An approved fact statement that names two people (the D11 corpus's main name source).</summary>
    private const string NamingStatement = "Vụ án Nguyễn Văn An";

    /// <summary>A valid keyword whose folded tokens repeat the statement's <c>nguyen van</c> run.</summary>
    private const string NamingKeyword = "nguyen van at the market";

    /// <summary>A valid keyword with no name run, for the fiction case.</summary>
    private const string PlaceKeyword = "Da lat hillside market";

    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Approve_NameFromApprovedFact_Returns409KeywordsFlagged()
    {
        var world = await SeedAsync(Topic.TrueCrime, NamingKeyword, namingSheet: true, staleModeration: true);
        var moderator = new FakeContentModerator().EnqueueFlagged(ModerationStage.Draft, "illicit/violent");
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world, Attestation);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        var text = await response.Content.ReadAsStringAsync(Ct);

        // The body is read as JSON, never through ProblemResponse: this one code's `errors` member is an array
        // of rows, not the field groups that DTO describes (03 D10).
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("code").GetString().ShouldBe("keywords_flagged");
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(409);
        document.RootElement.GetProperty("type").GetString().ShouldBe("https://nighttales.app/errors/keywords_flagged");
        var flags = document.RootElement.GetProperty("errors").EnumerateArray().ToArray();
        flags.Length.ShouldBe(1);
        flags[0].GetProperty("segment").GetString().ShouldBe("p:1");
        flags[0].GetProperty("index").GetInt32().ShouldBe(0);
        flags[0].GetProperty("rule").GetString().ShouldBe(RenderKeywordLinter.NameDetected);
        string.Join(',', flags[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal))
            .ShouldBe("index,rule,segment");

        // A row is an address, an index and a rule id: neither the keyword nor the person's name, and no part
        // of the statement, reaches the wire (03 §8, 02 D19).
        text.ShouldNotContain(NamingKeyword);
        text.ShouldNotContain("nguyen");
        text.ShouldNotContain("market");
        text.ShouldNotContain(NamingStatement);

        // The lint refuses before stage 7: the queued blocking verdict was never asked for, so this 409 is the
        // keyword check's and not the moderation refresh's.
        moderator.Calls.ShouldBe(0);
        AssertNothingApproved(world);
    }

    [Fact]
    public async Task Approve_FictionWithCapitalisedPlace_NotFlagged()
    {
        var world = await SeedAsync(Topic.Ghost, PlaceKeyword, namingSheet: false, staleModeration: false);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world, Attestation);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        SqlProbe.ScalarText(Database, $"SELECT status || '|' || approved_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe($"story_approved|{world.VersionId}");
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{world.UserId}' AND action = 'episode.true_crime_attested'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task Approve_StaleIfMatch_Returns412_NotKeywordsFlagged()
    {
        var world = await SeedAsync(Topic.TrueCrime, NamingKeyword, namingSheet: true, staleModeration: true);
        var moderator = new FakeContentModerator().EnqueueFlagged(ModerationStage.Draft, "illicit/violent");
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);

        // The same world that answers 409 with the current tag (the test above): a stale tab still gets its 412,
        // never the keyword verdict, so the lint cannot hide a lost update behind a 409.
        using var response = await ApproveAsync(client, world, Attestation, tag: StoryEtag.For(Guid.CreateVersion7()));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, await response.Content.ReadAsStringAsync(Ct));
        (await ProblemAsync(response)).Code.ShouldBe("story_version_conflict");
        moderator.Calls.ShouldBe(0);
        AssertNothingApproved(world);
    }

    [Fact]
    public async Task Approve_NameCorpusLoadFails_Returns503()
    {
        var world = await SeedAsync(Topic.TrueCrime, NamingKeyword, namingSheet: true, staleModeration: false);
        using var factory = Factory(corpus: new UnavailableCorpusProvider());
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world, Attestation);

        // The check is never skipped: a transient read failure is the documented 503 (09 README D5), and the
        // episode keeps its state.
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync(Ct));
        var problem = await ProblemAsync(response);
        problem.Code.ShouldBe("service_unavailable");
        problem.Type.ShouldBe("https://nighttales.app/errors/service_unavailable");
        AssertNothingApproved(world);
    }

    private ApiFactory Factory(FakeContentModerator? moderator = null, INameCorpusProvider? corpus = null) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                if (moderator is not null)
                {
                    services.RemoveAll<IContentModerator>();
                    services.AddSingleton<IContentModerator>(moderator);
                }

                if (corpus is not null)
                {
                    services.RemoveAll<INameCorpusProvider>();
                    services.AddSingleton(corpus);
                }
            });

    /// <summary>
    /// One owner, a series of <paramref name="topic"/>, a <c>story_review</c> episode and its current version.
    /// With <paramref name="namingSheet"/> the episode carries two fetched sources and the approved fact sheet
    /// the version is pinned to (so the real gate's 3b/3c pass), and the statement names two people.
    /// </summary>
    /// <param name="topic">The series topic; only true crime builds a corpus.</param>
    /// <param name="paragraphKeyword">The second paragraph's keyword, the linter's input.</param>
    /// <param name="namingSheet">Whether to seed the naming approved sheet and its two sources.</param>
    /// <param name="staleModeration">Whether the stored verdict needs a refresh (stage 7 would run).</param>
    /// <returns>The seeded ids.</returns>
    private async Task<World> SeedAsync(Topic topic, string paragraphKeyword, bool namingSheet, bool staleModeration)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(
            user.Id, series.Id, lengthTier: LengthTier.Short, status: EpisodeStatus.StoryReview, ct: Ct);

        Guid? sheetId = null;
        decimal? similarity = null;
        if (namingSheet)
        {
            var first = await RealGuardFixtures.SeedFetchedSourceAsync(Database, user.Id, episode.Id, new string('a', 64), Start, Ct);
            var second = await RealGuardFixtures.SeedFetchedSourceAsync(Database, user.Id, episode.Id, new string('b', 64), Start, Ct);
            var sheet = await RealGuardFixtures.SheetAsync(
                Database,
                user.Id,
                episode.Id,
                1,
                [
                    RealGuardFixtures.CitedFact("f1", NamingStatement, first.Id, "s1"),
                    RealGuardFixtures.CitedFact("f2", "One more detail of the case.", second.Id, "s2"),
                ],
                RealGuardFixtures.Aliases(("s1", first.Id), ("s2", second.Id)),
                FactSheetStatus.Approved,
                Start,
                Ct);
            sheetId = sheet.Id;

            // A stored, passing score: 3d must not need the similarity corpus on this path.
            similarity = 0m;
        }

        var draft = Draft(paragraphKeyword);
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
            sheetId,
            Audit(staleModeration),
            similarity,
            Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'");

        return new World(user.Id, series.Id, episode.Id, version.Id);
    }

    /// <summary>Nothing was approved and nothing downstream was written.</summary>
    /// <param name="world">The world the refusal belongs to.</param>
    private void AssertNothingApproved(World world)
    {
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_review");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM episodes WHERE id = '{world.EpisodeId}' AND (approved_story_version_id IS NOT NULL OR approved_at IS NOT NULL)")
            .ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE type = 'story_approved.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{world.UserId}'").ShouldBe(0);
    }

    /// <summary>The clean <c>audit.v1</c> report of a reviewable version, optionally stale for stage 7.</summary>
    /// <param name="stale">Whether the stored moderation verdict needs one fresh check.</param>
    /// <returns>The stored report JSON.</returns>
    private static string Audit(bool stale)
    {
        var report = JsonNode.Parse($$$"""{"schema_version":"audit.v1","local":{"issues":[]},"moderation":{{{Clean}}},"model_review":{"scores":{"overall":7.5},"stale":true},"revision":{"rounds":2},"similarity":{"score":null,"n":0}}""")!.AsObject();
        if (stale)
        {
            report["moderation"]!["stale"] = true;
        }

        return report.ToJsonString();
    }

    /// <summary>
    /// The story the linter reads: one keyword per segment, all of them valid, and the second paragraph's
    /// keyword carrying the name run the corpus stores.
    /// </summary>
    /// <param name="paragraphKeyword">The second paragraph's keyword.</param>
    /// <returns>The content document the version stores.</returns>
    private static StoryContentV1 Draft(string paragraphKeyword) => new(
        "The house by the river",
        new StorySegmentV1("The night was quiet.", ["quiet riverside morning"], []),
        [
            new StorySegmentV1("A knock woke the narrator.", ["empty corridor at dawn"], [], "setup"),
            new StorySegmentV1("Nobody stood outside.", [paragraphKeyword], [], "turn"),
        ],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet true-crime story.",
        ["case", "night"],
        ["documentary"],
        new StoryStructureV1("case", "old house", "a knock", "the narrator", "first_person"));

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, World world, string? body, string? tag = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{world.EpisodeId}/story/approve");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        request.Headers.TryAddWithoutValidation("If-Match", tag ?? StoryEtag.For(world.VersionId));

        return client.SendAsync(request, Ct);
    }

    private static async Task<ProblemResponse> ProblemAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<ProblemResponse>(await response.Content.ReadAsStringAsync(Ct), ProblemResponse.Options)!;

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId, Guid VersionId);

    /// <summary>The corpus read of a database that answers with a transient failure (SQLSTATE 40P01).</summary>
    private sealed class UnavailableCorpusProvider : INameCorpusProvider
    {
        /// <inheritdoc />
        public ValueTask<NameCorpus> LoadAsync(Guid episodeId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            throw new PostgresException("The name corpus read failed.", "ERROR", "ERROR", "40P01");
        }
    }
}
