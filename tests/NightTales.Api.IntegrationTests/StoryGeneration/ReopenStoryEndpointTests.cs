using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Render;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>Reopen preconditions and persisted approve/reopen/edit handoff through the real command pipeline.</summary>
[Trait("Category", "Integration")]
public sealed class ReopenStoryEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly string[] AuditKeys = ["episode_id", "story_version_id"];
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task ReopenStory_FromApproved_ReturnsToReviewAndClearsApproval()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var approved = await PostAsync(client, world, "approve", StoryEtag.For(world.VersionId));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        SqlProbe.ScalarText(Database, $"SELECT approved_at::text FROM episodes WHERE id = '{world.EpisodeId}'").ShouldNotBeNullOrWhiteSpace();

        using var response = await PostAsync(client, world, "reopen", StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await ReadBodyAsync(response);
        body.GetProperty("episodeId").GetGuid().ShouldBe(world.EpisodeId);
        body.GetProperty("storyVersionId").GetGuid().ShouldBe(world.VersionId);
        body.GetProperty("status").GetString().ShouldBe("story_review");
        body.GetProperty("etag").GetString().ShouldBe(StoryEtag.For(world.VersionId));
        response.Headers.ETag!.Tag.ShouldBe(StoryEtag.For(world.VersionId));
        AssertReviewWithoutApproval(world);
        AuditCount(world, "episode.story_reopened").ShouldBe(1);
        EventCount(world, "story_reopened.v1").ShouldBe(1);
        var audit = SqlProbe.ScalarText(Database,
            $"SELECT data::text FROM audit_logs WHERE action = 'episode.story_reopened' AND subject_user_id = '{world.UserId}'");
        using var document = JsonDocument.Parse(audit!);
        document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .ShouldBe(AuditKeys);
        document.RootElement.GetProperty("episode_id").GetGuid().ShouldBe(world.EpisodeId);
        document.RootElement.GetProperty("story_version_id").GetGuid().ShouldBe(world.VersionId);

        using var repeat = await PostAsync(client, world, "reopen", StoryEtag.For(world.VersionId));
        repeat.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        AuditCount(world, "episode.story_reopened").ShouldBe(1);
        EventCount(world, "story_reopened.v1").ShouldBe(1);
    }

    [Theory]
    [InlineData(EpisodeStatus.Draft, null)]
    [InlineData(EpisodeStatus.StoryReview, null)]
    [InlineData(EpisodeStatus.StoryGenerating, null)]
    [InlineData(EpisodeStatus.Rendering, null)]
    [InlineData(EpisodeStatus.Rendering, "malformed")]
    public async Task ReopenStory_ForbiddenState_Returns409BeforeCheckingEtag(EpisodeStatus status, string? tag)
    {
        var world = await SeedAsync(status);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await PostAsync(client, world, "reopen", tag);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("episode_invalid_state");
        AuditCount(world, "episode.story_reopened").ShouldBe(0);
        EventCount(world, "story_reopened.v1").ShouldBe(0);
    }

    [Theory]
    [InlineData(null, 428, "if_match_required")]
    [InlineData("\"sv-11111111-1111-7111-8111-111111111111\"", 412, "story_version_conflict")]
    [InlineData("W/\"sv-11111111-1111-7111-8111-111111111111\"", 412, "story_version_conflict")]
    [InlineData("not-an-etag", 412, "story_version_conflict")]
    public async Task ReopenStory_MissingOrStaleEtag_LeavesApprovalUnchanged(string? tag, int status, string code)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var approved = await PostAsync(client, world, "approve", StoryEtag.For(world.VersionId));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var response = await PostAsync(client, world, "reopen", tag);

        response.StatusCode.ShouldBe((HttpStatusCode)status, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe(code);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_approved");
        SqlProbe.ScalarText(Database, $"SELECT approved_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe(world.VersionId.ToString());
        SqlProbe.ScalarText(Database, $"SELECT approved_at::text FROM episodes WHERE id = '{world.EpisodeId}'").ShouldNotBeNullOrWhiteSpace();
        AuditCount(world, "episode.story_reopened").ShouldBe(0);
        EventCount(world, "story_reopened.v1").ShouldBe(0);
    }

    [Fact]
    public async Task ForeignEpisode_ReopenReturns404BeforeStateOrEtag()
    {
        var owner = await SeedAsync(EpisodeStatus.Rendering);
        var stranger = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(stranger.UserId);
        using var response = await PostAsync(client, owner, "reopen", null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        AuditCount(owner, "episode.story_reopened").ShouldBe(0);
        EventCount(owner, "story_reopened.v1").ShouldBe(0);
    }

    [Fact]
    public async Task ReopenStory_ApprovedPointerMissing_UsesCurrentVersionFallback()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var approved = await PostAsync(client, world, "approve", StoryEtag.For(world.VersionId));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        SqlProbe.Execute(Database, $"UPDATE episodes SET approved_story_version_id = NULL WHERE id = '{world.EpisodeId}'");

        using var response = await PostAsync(client, world, "reopen", StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        AssertReviewWithoutApproval(world);
        EventCount(world, "story_reopened.v1").ShouldBe(1);
    }

    [Fact]
    public async Task ReopenStory_TwoTabs_OnlyOneTransitionAuditAndEvent()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var clientA = factory.ClientFor(world.UserId);
        using var clientB = factory.ClientFor(world.UserId);
        using var approved = await PostAsync(clientA, world, "approve", StoryEtag.For(world.VersionId));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var responses = await Task.WhenAll(
            PostAsync(clientA, world, "reopen", StoryEtag.For(world.VersionId)),
            PostAsync(clientB, world, "reopen", StoryEtag.For(world.VersionId)));
        using var first = responses[0];
        using var second = responses[1];

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Single(response => response.StatusCode != HttpStatusCode.OK).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        AssertReviewWithoutApproval(world);
        AuditCount(world, "episode.story_reopened").ShouldBe(1);
        EventCount(world, "story_reopened.v1").ShouldBe(1);
    }

    [Fact]
    public async Task ReopenStory_MatchesApprovedPointerButRespondsWithCurrentVersionEtag()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var approved = await PostAsync(client, world, "approve", StoryEtag.For(world.VersionId));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var draft = Draft() with { Title = "A newer current version" };
        var newer = StoryVersion.Create(world.UserId, world.EpisodeId, 2, StoryVersionSource.Edited, draft,
            StoryContentJson.Write(draft, new JsonArray()), null, null, null, world.VersionId, null, null, null, Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }))
        {
            db.StoryVersions.Add(newer);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id = '{newer.Id}' WHERE id = '{world.EpisodeId}'");
        using var wrong = await PostAsync(client, world, "reopen", StoryEtag.For(newer.Id));
        wrong.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        using var response = await PostAsync(client, world, "reopen", StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.ETag!.Tag.ShouldBe(StoryEtag.For(newer.Id));
        (await ReadBodyAsync(response)).GetProperty("storyVersionId").GetGuid().ShouldBe(newer.Id);
        AssertReviewWithoutApproval(world);
        SqlProbe.ScalarText(Database,
            $"SELECT data->>'story_version_id' FROM audit_logs WHERE action = 'episode.story_reopened' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(world.VersionId.ToString());
    }

    [Fact]
    public async Task ReopenStory_AuditFailure_RollsBackStateAndOutbox()
    {
        var world = await SeedAsync();
        using (var approveFactory = Factory())
        using (var client = approveFactory.ClientFor(world.UserId))
        using (var approved = await PostAsync(client, world, "approve", StoryEtag.For(world.VersionId)))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var factory = Factory().WithServices(services =>
        {
            services.RemoveAll<IAuditWriter>();
            services.AddScoped<IAuditWriter>(_ => new FailingAuditWriter());
        });
        using var reopeningClient = factory.ClientFor(world.UserId);
        try
        {
            using var response = await PostAsync(reopeningClient, world, "reopen", StoryEtag.For(world.VersionId));
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        catch (ForcedAuditFailureException)
        {
            // TestServer may surface the scripted exception directly instead of an HTTP 500.
        }

        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_approved");
        SqlProbe.ScalarText(Database, $"SELECT approved_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe(world.VersionId.ToString());
        SqlProbe.ScalarText(Database, $"SELECT approved_at::text FROM episodes WHERE id = '{world.EpisodeId}'").ShouldNotBeNullOrWhiteSpace();
        AuditCount(world, "episode.story_reopened").ShouldBe(0);
        EventCount(world, "story_reopened.v1").ShouldBe(0);
    }

    [Fact]
    public async Task ApproveStory_ThenRenderHandoff_UsesApprovedBodyAndKeywords()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var approved = await PostAsync(client, world, "approve", StoryEtag.For(world.VersionId));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        var approvedId = Guid.Parse(SqlProbe.ScalarText(Database,
            $"SELECT approved_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")!);
        approvedId.ShouldBe(world.VersionId);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }))
        {
            var input = await new StoryRenderInputProvider(db).GetAsync(approvedId, Ct);
            input.IsError.ShouldBeFalse();
            input.Value.StoryVersionId.ShouldBe(approvedId);
            input.Value.Body.ShouldBe(world.Body);
            input.Value.Keywords.ShouldBe(world.Keywords);
            input.Value.Title.ShouldBe(Draft().Title);
            input.Value.Language.ShouldBe("vi");
            input.Value.Tier.ShouldBe(LengthTier.Short);
            input.Value.Segments.Count.ShouldBe(5);
        }

        using var reopened = await PostAsync(client, world, "reopen", StoryEtag.For(approvedId));
        reopened.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertReviewWithoutApproval(world);
        // 09.6.1 is not implemented: prove its persisted input pointer is absent, not a pretend consumer call.
    }

    [Fact]
    public async Task ApproveThenReopenThenEdit_SecondApproveUsesTheEditedVersion()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var firstApproval = await PostAsync(client, world, "approve", StoryEtag.For(world.VersionId));
        firstApproval.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var reopened = await PostAsync(client, world, "reopen", StoryEtag.For(world.VersionId));
        reopened.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertReviewWithoutApproval(world);
        var edited = Draft() with { Title = "Căn nhà ven sông" };
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/episodes/{world.EpisodeId}/story")
        {
            Content = new StringContent(JsonSerializer.Serialize(edited, StoryContentJson.SerializerOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", StoryEtag.For(world.VersionId));
        using var saved = await client.SendAsync(request, Ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        var editedId = (await ReadBodyAsync(saved)).GetProperty("versionId").GetGuid();
        editedId.ShouldNotBe(world.VersionId);
        SqlProbe.ScalarText(Database,
            $"SELECT coalesce(approved_story_version_id::text, 'null') FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("null");

        using var secondApproval = await PostAsync(client, world, "approve", StoryEtag.For(editedId));

        secondApproval.StatusCode.ShouldBe(HttpStatusCode.OK, await secondApproval.Content.ReadAsStringAsync(Ct));
        secondApproval.Headers.ETag!.Tag.ShouldBe(StoryEtag.For(editedId));
        (await ReadBodyAsync(secondApproval)).GetProperty("storyVersionId").GetGuid().ShouldBe(editedId);
        SqlProbe.ScalarText(Database,
            $"SELECT approved_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe(editedId.ToString());
        AuditCount(world, "episode.story_approved").ShouldBe(2);
        EventCount(world, "story_approved.v1").ShouldBe(2);
        AuditCount(world, "episode.story_reopened").ShouldBe(1);
        EventCount(world, "story_reopened.v1").ShouldBe(1);
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var render = await new StoryRenderInputProvider(db).GetAsync(editedId, Ct);
        render.IsError.ShouldBeFalse();
        render.Value.Title.ShouldBe(edited.Title);
    }

    private ApiFactory Factory() => new ApiFactory(Database, new FakeClock(Start)).WithServices(services =>
    {
        services.RemoveAll<IContentModerator>();
        services.AddSingleton<IContentModerator>(new FakeContentModerator());
    });

    private void AssertReviewWithoutApproval(World world) =>
        SqlProbe.ScalarText(Database,
            "SELECT status || '|' || coalesce(approved_story_version_id::text, 'null') || '|' || coalesce(approved_at::text, 'null') "
            + $"FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_review|null|null");

    private long AuditCount(World world, string action) => SqlProbe.Count(Database,
        $"SELECT count(*) FROM audit_logs WHERE action = '{action}' AND subject_user_id = '{world.UserId}'");

    private long EventCount(World world, string type) => SqlProbe.Count(Database,
        $"SELECT count(*) FROM outbox_messages WHERE type = '{type}' AND user_id = '{world.UserId}'");

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, World world, string action, string? tag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{world.EpisodeId}/story/{action}");
        if (tag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<ProblemResponse>(await response.Content.ReadAsStringAsync(Ct), ProblemResponse.Options)!;

    private async Task<World> SeedAsync(EpisodeStatus status = EpisodeStatus.StoryReview)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(user.Id, series.Id, lengthTier: LengthTier.Short, status: status, ct: Ct);
        var draft = Draft();
        var version = StoryVersion.Create(user.Id, episode.Id, 1, StoryVersionSource.Generated, draft,
            StoryContentJson.Write(draft, new JsonArray()), "model-x", "rel=1", null, null, null,
            """{"schema_version":"audit.v1","moderation":{"flagged":false,"categories":[],"blocking":[],"stale":false,"unavailable":false}}""",
            null, Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'");
        return new World(user.Id, episode.Id, version.Id, version.Body, version.Keywords.ToArray());
    }

    private static StoryContentV1 Draft()
    {
        var hook = new StorySegmentV1("Đêm ấy người kể ngồi một mình trong căn nhà cũ.", ["old house by the river", "stormy night over the river"], []);
        StorySegmentV1[] paragraphs =
        [
            new("Chủ nhà dặn đừng mở cánh cửa cuối hành lang.", ["empty chair in the hallway"], [], "setup"),
            new("Tiếng gõ vang lên từ chiếc ghế vẫn còn ấm.", ["rain on window"], [], "twist"),
            new("Người hàng xóm im lặng nhìn ra sân.", ["figure at door"], [], "reveal"),
        ];
        var ending = new StorySegmentV1("Anh trả nhà ngay hôm sau.", ["letter on table"], []);
        var count = Words(hook.Text) + Words(ending.Text) + paragraphs.Sum(segment => Words(segment.Text));
        paragraphs[^1] = paragraphs[^1] with
        {
            Text = paragraphs[^1].Text + " " + string.Join(' ', Enumerable.Repeat("rồi", 220 - count)),
        };
        return new StoryContentV1("Căn nhà cuối hành lang", hook, paragraphs, ending,
            "Một chuyện ma nhẹ nhàng.", ["truyện ma", "tâm linh"], ["supernatural"],
            new StoryStructureV1("truyện ma tâm linh", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person"));
    }

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    private sealed record World(Guid UserId, Guid EpisodeId, Guid VersionId, string Body, IReadOnlyList<string> Keywords);
    private sealed class ForcedAuditFailureException : Exception
    {
    }
    private sealed class FailingAuditWriter : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken) => throw new ForcedAuditFailureException();
    }
}
