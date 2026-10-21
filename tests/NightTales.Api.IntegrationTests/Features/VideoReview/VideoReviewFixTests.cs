using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Common;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Assets;
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

namespace NightTales.Api.IntegrationTests.Features.VideoReview;

/// <summary>
/// Regression pins for the triaged review findings of 09.7.1-T1, over the real host and PostgreSQL:
/// SEC-1 (a NUL in the reject reason is a coded 400, never a raw 500), SEC-2 (<c>video_review → story_review</c>
/// clears the approval pointer exactly as <c>ReopenStory</c> does, <c>re_render</c> keeps it) and
/// C1/P1/SEC-3 (the approve retention re-stamp bounds the <em>newest</em> live assets, so the accepted cut is
/// always inside the cap).
/// </summary>
/// <remarks>
/// The harness is <see cref="ApproveVideoTests"/>. Every test seeds its own owner, so the shared class database
/// cannot leak rows; the class runs in parallel with the rest of the suite.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class VideoReviewFixTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The audit action of a rejection.</summary>
    private const string RejectAction = "episode.video_rejected";

    /// <summary>The outbox type of a rejection.</summary>
    private const string RejectEvent = "video_rejected.v1";

    private static readonly DateTimeOffset Start = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task RejectVideo_WithNulInReason_Returns400ValidationFailedAndLeavesTheRowUntouched()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        // The `\u0000` escape is what a JSON client sends for a NUL; it passes the reader's length checks, so
        // only the validator (and the aggregate behind it) can refuse it — as a coded 400, not a 500.
        using var response = await RejectAsync(
            client,
            world.EpisodeId,
            Etag(world.EpisodeId),
            """{"reason":"too dark\u0000at the start","target":"story"}""",
            "reject-nul");

        await AssertProblemAsync(response, 400, "validation_failed");

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || (video_reject_reason IS NULL)::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("video_review|true", "a refused reason never reaches the row");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = '{RejectEvent}' AND user_id = '{world.UserId}'")
            .ShouldBe(0);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = '{RejectAction}' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task RejectVideo_ToStoryTarget_ClearsApprovedStoryVersionAndApprovedAt()
    {
        var world = await SeedAsync();
        await SeedApprovedStoryAsync(world, Start.AddDays(-1));
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await RejectAsync(
            client,
            world.EpisodeId,
            Etag(world.EpisodeId),
            """{"reason":"The story needs another edit","target":"story"}""",
            "reject-story-clears");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        // SEC-2: `story_review` is the state `ReopenStory` reaches, so it clears the approval pointer and its
        // stamp — a consumer keying on the pointer alone must not read a version that is under review again.
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || coalesce(approved_story_version_id::text, 'null') || '|' "
                + $"|| coalesce(approved_at::text, 'null') FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("story_review|null|null");
    }

    [Fact]
    public async Task RejectVideo_ToReRender_KeepsApprovedStoryVersionAndApprovedAt()
    {
        var world = await SeedAsync();
        var versionId = await SeedApprovedStoryAsync(world, Start.AddDays(-1));
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await RejectAsync(
            client,
            world.EpisodeId,
            Etag(world.EpisodeId),
            """{"reason":"Render it again from the same story","target":"re_render"}""",
            "reject-rerender-keeps");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));

        // SEC-2: a re-render renders the approved story again, so its pointer and stamp survive the transition.
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || coalesce(approved_story_version_id::text, 'null') || '|' "
                + $"|| (approved_at = TIMESTAMPTZ '{Start.AddDays(-1):O}')::text "
                + $"FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe($"story_approved|{versionId:D}|true");
    }

    [Fact]
    public async Task ApproveVideo_WithMoreThanFiftyLiveAssets_ReStampsTheNewestFifty()
    {
        var world = await SeedAsync();
        var ingestStamp = Start.AddDays(20);
        var approveStamp = Start.AddDays(30);

        // The ingest stamps every render's assets with the unapproved window and never retires an earlier cut
        // (09.6.3-T2), so a long reject -> re-render history leaves many live assets on one episode.
        for (var index = 0; index < 52; index++)
        {
            await Builder(world).CreateAssetAsync(
                world.UserId,
                world.EpisodeId,
                kind: index % 2 == 0 ? AssetKind.Video : AssetKind.Subtitle,
                retentionUntil: ingestStamp,
                ct: Ct);
        }

        // The rows the handler must leave on the ingest stamp are the OLDEST by id (the ingest's own ordering),
        // read from PostgreSQL so the expectation matches the query the handler runs.
        var oldest = SqlProbe.QueryStrings(
            Database,
            $"SELECT id::text FROM assets WHERE episode_id = '{world.EpisodeId}' AND deleted_at IS NULL "
            + "ORDER BY id LIMIT 2");

        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world.EpisodeId, Etag(world.EpisodeId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var live = $"episode_id = '{world.EpisodeId}' AND deleted_at IS NULL";
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM assets WHERE {live} AND retention_until = TIMESTAMPTZ '{approveStamp:O}'")
            .ShouldBe(50, "the bound keeps the 50 newest rows, so the accepted cut is inside the cap");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM assets WHERE {live} AND retention_until = TIMESTAMPTZ '{ingestStamp:O}'")
            .ShouldBe(2, "only the two oldest rows fall outside the bound");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM assets WHERE {live} AND retention_until = TIMESTAMPTZ '{ingestStamp:O}' "
                + $"AND id IN ('{oldest[0]}', '{oldest[1]}')")
            .ShouldBe(2, "the two rows left on the ingest stamp are the oldest by id, not the newest");
    }

    /// <summary>
    /// The host of these tests: the real Api with the fixed clock, and — for the in-process legs — the caller
    /// pinned through <see cref="ICurrentUser"/>.
    /// </summary>
    /// <param name="actorId">The user an in-process scope runs as, or <see langword="null"/> for the request-scoped actor.</param>
    /// <returns>The factory to build a client from.</returns>
    private ApiFactory Factory(Guid? actorId = null) =>
        new ApiFactory(Database, new FakeClock(Start)).WithServices(services =>
        {
            if (actorId is { } userId)
            {
                services.RemoveAll<ICurrentUser>();
                services.AddSingleton<ICurrentUser>(new TestCurrentUser { Id = userId });
            }
        });

    /// <summary>One owner with a series, and one episode awaiting review (no asset; each test seeds its own).</summary>
    /// <returns>The seeded ids.</returns>
    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(
            user.Id,
            series.Id,
            lengthTier: LengthTier.Short,
            status: EpisodeStatus.VideoReview,
            ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary>A builder pinned to the world's owner, for the asset rows of one episode.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The builder.</returns>
    private EpisodeBuilder Builder(World world) =>
        new(Database, new TestCurrentUser { Id = world.UserId });

    /// <summary>
    /// Persists one story version of the episode and points <c>approved_story_version_id</c>/<c>approved_at</c>
    /// at it — the shape an approved, rendered episode has before it reaches <c>video_review</c>. The pointer
    /// is written with raw SQL because the builders carry no story-approval path (the write path that reaches
    /// it lives in the story commands).
    /// </summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="approvedAt">The approval stamp to write.</param>
    /// <returns>The approved story version id.</returns>
    private async Task<Guid> SeedApprovedStoryAsync(World world, DateTimeOffset approvedAt)
    {
        var draft = Draft();
        var version = StoryVersion.Create(
            world.UserId,
            world.EpisodeId,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Start);

        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(
            Database,
            $"UPDATE episodes SET approved_story_version_id = '{version.Id}', current_story_version_id = '{version.Id}', "
            + $"approved_at = TIMESTAMPTZ '{approvedAt:O}' WHERE id = '{world.EpisodeId}'");

        return version.Id;
    }

    private static StoryContentV1 Draft() => new(
        "Approved story",
        new StorySegmentV1("The hook.", ["hook first", "hook second"], []),
        [new StorySegmentV1("The first paragraph.", ["paragraph one"], [])],
        new StorySegmentV1("The ending.", ["ending last"], []),
        "A persisted story for the video-review regression pins.",
        [],
        [],
        new StoryStructureV1("ghost story", "old house", "a midnight call", "the narrator", "first_person"));

    private static async Task<HttpResponseMessage> ApproveAsync(HttpClient client, Guid episodeId, string? tag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/video/approve");
        if (tag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> RejectAsync(
        HttpClient client,
        Guid episodeId,
        string? tag,
        string body,
        string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/video/reject")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (tag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);

        return await client.SendAsync(request, Ct);
    }

    /// <summary>The episode's current strong ETag, read from <c>xmin</c> like the client's own copy.</summary>
    /// <param name="episodeId">The episode to read.</param>
    /// <returns>The quoted tag.</returns>
    private string Etag(Guid episodeId) =>
        ETags.Episode(uint.Parse(
            SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM episodes WHERE id = '{episodeId}'"),
            CultureInfo.InvariantCulture));

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        response.StatusCode.ShouldBe((HttpStatusCode)status, await response.Content.ReadAsStringAsync(Ct));
        var problem = JsonSerializer.Deserialize<ProblemResponse>(
            await response.Content.ReadAsStringAsync(Ct),
            ProblemResponse.Options);
        problem!.Code.ShouldBe(code);
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
