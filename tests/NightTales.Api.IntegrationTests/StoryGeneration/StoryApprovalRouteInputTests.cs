using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightTales.Api.Http;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
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
/// Approval/reopen HTTP glue against the real command pipeline and PostgreSQL, with ApiFactory's deterministic
/// moderator. These tests exercise request shape, the optional fiction body, tenant/auth guards and story ETags.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StoryApprovalRouteInputTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"attestation\":null}")]
    [InlineData("{\"attestation\":{\"accepted\":true,\"textVersion\":\"ta.v1\"}}")]
    [InlineData("{\"attestation\":{\"accepted\":false,\"textVersion\":\"unknown-version\"}}")]
    [InlineData("{\"attestation\":{\"accepted\":false,\"textVersion\":\"\"}}")]
    public async Task FictionApproval_AbsentOrWellShapedAttestation_SucceedsWithoutTrueCrimeAudit(string? json)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await PostAsync(client, world, "approve", json);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        await AssertStoryResponseAsync(response, world, "story_approved");
        SqlProbe.ScalarText(Database,
                $"SELECT status || '|' || approved_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe($"story_approved|{world.VersionId}");
        SqlProbe.Count(Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.true_crime_attested' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(0);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("\"not an object\"")]
    [InlineData("{\"attestation\":true}")]
    [InlineData("{\"attestation\":[]}")]
    [InlineData("{\"attestation\":{}}")]
    [InlineData("{\"attestation\":{\"accepted\":\"true\",\"textVersion\":\"ta.v1\"}}")]
    [InlineData("{\"attestation\":{\"accepted\":null,\"textVersion\":\"ta.v1\"}}")]
    [InlineData("{\"attestation\":{\"accepted\":true}}")]
    [InlineData("{\"attestation\":{\"textVersion\":\"ta.v1\"}}")]
    [InlineData("{\"attestation\":{\"accepted\":true,\"textVersion\":1}}")]
    [InlineData("{\"attestation\":{\"accepted\":true,\"textVersion\":null}}")]
    [InlineData("{\"attestation\":{\"accepted\":true,\"textVersion\":\"\\uD800\"}}")]
    public async Task Approval_MalformedOrIncompatibleJson_Returns400WithoutSaving(string json)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await PostAsync(client, world, "approve", json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        await AssertProblemCodeAsync(response, "story_attestation_required");
        AssertUnchanged(world);
    }

    [Fact]
    public async Task Approval_ClientApprovalSource_IsNeverBound()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await PostAsync(client, world, "approve",
            """{"source":"autopilot","approvalSource":"autopilot"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        SqlProbe.ScalarText(Database,
                "SELECT data->>'approval_source' FROM audit_logs WHERE action = 'episode.story_approved' "
                + $"AND subject_user_id = '{world.UserId}'")
            .ShouldBe("user");
    }

    [Fact]
    public async Task Approval_NonJsonContentType_Returns400WithoutSaving()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var request = Request(world, "approve", StoryEtag.For(world.VersionId));
        request.Content = new StringContent("{}", Encoding.UTF8, "text/plain");
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        AssertUnchanged(world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approval_OversizedDeclaredOrStreamedBody_Retains413WithoutSaving(bool streamed)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var request = Request(world, "approve", StoryEtag.For(world.VersionId));
        var json = "{}" + new string(' ', checked((int)RequestLimitsOptions.DefaultMaxRequestBodySize) - 1);
        request.Content = streamed
            ? new StreamedJsonContent(Encoding.UTF8.GetBytes(json))
            : new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
        await AssertProblemCodeAsync(response, "payload_too_large");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        problem.RootElement.GetProperty("type").GetString().ShouldBe(ErrorCatalogue.ProblemType("payload_too_large"));
        problem.RootElement.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(413);
        problem.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        AssertUnchanged(world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approval_BodyExactlyAtGlobalLimit_IsAccepted(bool streamed)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var request = Request(world, "approve", StoryEtag.For(world.VersionId));
        var json = "{}" + new string(' ', checked((int)RequestLimitsOptions.DefaultMaxRequestBodySize) - 2);
        request.Content = streamed
            ? new StreamedJsonContent(Encoding.UTF8.GetBytes(json))
            : new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        await AssertStoryResponseAsync(response, world, "story_approved");
    }

    [Theory]
    [InlineData(4095, false)]
    [InlineData(4096, false)]
    [InlineData(4097, false)]
    [InlineData(4095, true)]
    [InlineData(4096, true)]
    [InlineData(4097, true)]
    public async Task Approval_BodyAroundInitialBufferCapacity_IsAccepted(int byteLength, bool streamed)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var request = Request(world, "approve", StoryEtag.For(world.VersionId));
        var json = "{}" + new string(' ', byteLength - 2);
        request.Content = streamed
            ? new StreamedJsonContent(Encoding.UTF8.GetBytes(json))
            : new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        await AssertStoryResponseAsync(response, world, "story_approved");
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reopen")]
    public async Task ApprovalRoutes_MissingIfMatchInRequiredState_Return428(string action)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        if (action == "reopen")
        {
            using var approved = await PostAsync(client, world, "approve");
            approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        }

        using var request = Request(world, action, null);
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe((HttpStatusCode)428, await response.Content.ReadAsStringAsync(Ct));
        await AssertProblemCodeAsync(response, "if_match_required");
        if (action == "reopen")
        {
            SqlProbe.ScalarText(Database,
                    $"SELECT status || '|' || approved_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
                .ShouldBe($"story_approved|{world.VersionId}");
            SqlProbe.Count(Database,
                    $"SELECT count(*) FROM audit_logs WHERE action = 'episode.story_reopened' AND subject_user_id = '{world.UserId}'")
                .ShouldBe(0);
        }
        else
        {
            AssertUnchanged(world);
        }
    }

    [Fact]
    public async Task Approval_StaleStoryIfMatch_Return412WithoutSaving()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var request = Request(world, "approve", StoryEtag.For(Guid.CreateVersion7()));
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, await response.Content.ReadAsStringAsync(Ct));
        await AssertProblemCodeAsync(response, "story_version_conflict");
        AssertUnchanged(world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reopen_ReviewState_Return409BeforeEtagValidation(bool hasIfMatch)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var request = Request(world, "reopen", hasIfMatch ? StoryEtag.For(Guid.CreateVersion7()) : null);
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        await AssertProblemCodeAsync(response, "episode_invalid_state");
        AssertUnchanged(world);
    }

    [Theory]
    [InlineData("approve", false)]
    [InlineData("approve", true)]
    [InlineData("reopen", false)]
    [InlineData("reopen", true)]
    public async Task ApprovalRoutes_Anonymous_Return401BeforeEtagValidation(string action, bool hasIfMatch)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.AnonymousClient();
        using var request = Request(world, action, hasIfMatch ? StoryEtag.For(world.VersionId) : null);
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.Content.ReadAsStringAsync(Ct));
        AssertUnchanged(world);
    }

    [Theory]
    [InlineData("approve", false)]
    [InlineData("approve", true)]
    [InlineData("reopen", false)]
    [InlineData("reopen", true)]
    public async Task ApprovalRoutes_ForeignEpisode_Return404BeforeStateAndEtagValidation(string action, bool hasIfMatch)
    {
        var world = await SeedAsync();
        var stranger = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        using var factory = Factory();
        using var client = factory.ClientFor(stranger.Id);
        using var request = Request(world, action, hasIfMatch ? StoryEtag.For(world.VersionId) : null);
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        AssertUnchanged(world);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reopen")]
    public async Task ApprovalRoutes_UnknownEpisodeWithoutIfMatch_Return404(string action)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var request = Request(world with { EpisodeId = Guid.CreateVersion7() }, action, null);
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        AssertUnchanged(world);
    }

    [Fact]
    public async Task ApproveThenBodylessReopen_ReturnsStoryVersionEtagInBothBodiesAndHeaders()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var approved = await PostAsync(client, world, "approve");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        await AssertStoryResponseAsync(approved, world, "story_approved");
        using var reopened = await PostAsync(client, world, "reopen");

        reopened.StatusCode.ShouldBe(HttpStatusCode.OK, await reopened.Content.ReadAsStringAsync(Ct));
        await AssertStoryResponseAsync(reopened, world, "story_review");
        SqlProbe.ScalarText(Database,
                $"SELECT status || '|' || coalesce(approved_story_version_id::text, 'null') || '|' "
                + $"|| coalesce(approved_at::text, 'null') FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("story_review|null|null");
    }

    private ApiFactory Factory() => new(Database, new FakeClock(Start));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, World world, string action, string? json = null)
    {
        using var request = Request(world, action, StoryEtag.For(world.VersionId));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }

    private static HttpRequestMessage Request(World world, string action, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{world.EpisodeId}/story/{action}");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    private static async Task AssertStoryResponseAsync(HttpResponseMessage response, World world, string status)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var body = document.RootElement;
        body.GetProperty("episodeId").GetGuid().ShouldBe(world.EpisodeId);
        body.GetProperty("storyVersionId").GetGuid().ShouldBe(world.VersionId);
        body.GetProperty("status").GetString().ShouldBe(status);
        body.GetProperty("etag").GetString().ShouldBe(StoryEtag.For(world.VersionId));
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag.Tag.ShouldBe(StoryEtag.For(world.VersionId));
        if (status == "story_approved")
        {
            body.GetProperty("approvedAt").GetDateTimeOffset().ShouldBe(Start);
        }
    }

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, string code)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        document.RootElement.GetProperty("code").GetString().ShouldBe(code);
    }

    private void AssertUnchanged(World world)
    {
        SqlProbe.ScalarText(Database,
                $"SELECT status || '|' || coalesce(approved_story_version_id::text, 'null') FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("story_review|null");
        SqlProbe.Count(Database,
                $"SELECT count(*) FROM audit_logs WHERE action IN ('episode.story_approved', 'episode.story_reopened') AND subject_user_id = '{world.UserId}'")
            .ShouldBe(0);
        SqlProbe.Count(Database,
                $"SELECT count(*) FROM outbox_messages WHERE type IN ('story_approved.v1', 'story_reopened.v1') AND user_id = '{world.UserId}'")
            .ShouldBe(0);
    }

    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(user.Id, series.Id, lengthTier: LengthTier.Short, status: EpisodeStatus.StoryReview, ct: Ct);
        var draft = new StoryContentV1(
            "The house by the river",
            new StorySegmentV1("An old house stood by the river.", ["old house"], []),
            [new StorySegmentV1("A visitor heard footsteps in the empty hallway.", ["empty hallway"], [], "setup")],
            new StorySegmentV1("At dawn the visitor left the house.", ["dawn"], []),
            "A fictional story.", ["ghost"], ["supernatural"],
            new StoryStructureV1("ghost", "old house", "footsteps", "visitor", "first_person"));
        var version = StoryVersion.Create(user.Id, episode.Id, 1, StoryVersionSource.Generated,
            draft, StoryContentJson.Write(draft, new JsonArray()), "model-test", "rel=1", null, null, null,
            """{"moderation":{"flagged":false,"categories":[],"blocking":[],"stale":false,"unavailable":false}}""",
            null, Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'");
        return new World(user.Id, episode.Id, version.Id);
    }

    private sealed record World(Guid UserId, Guid EpisodeId, Guid VersionId);

    /// <summary>HTTP content with no declared length, exercising the server's bounded streaming path.</summary>
    private sealed class StreamedJsonContent : HttpContent
    {
        private readonly byte[] _payload;

        public StreamedJsonContent(byte[] payload)
        {
            _payload = payload;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_payload).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
