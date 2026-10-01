using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Domain.Assets;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Npgsql;
using Shouldly;
using Xunit;

// The test namespace's last segment is `EpisodeEndpoints`, never `Episodes` — that namespace would shadow
// `NightTales.Domain.Episodes` for this whole assembly (09.4.1-T1's rule, which the 09.5.3-T1 task file's
// path `…/Episodes/GetEpisodeDetailTests.cs` predates). The routes are spelled out rather than read from
// `EpisodeEndpoints`, because the endpoint type's simple name is not usable from here.
namespace NightTales.Api.IntegrationTests.EpisodeEndpoints;

/// <summary>
/// <c>GET /api/v1/episodes/{id}</c> against the real host and a real PostgreSQL 17 (09.5.3 plan §4 UC3–UC5,
/// §9): the episode page in one call, the D15 ETag with its <c>304</c>, the D10 tenant isolation, the two
/// <c>nextActions</c> contracts and the two-command budget of BD16/WT46.
/// </summary>
/// <remarks>
/// <para>
/// The render block is asserted to be <c>null</c> and to cost no query, because the <c>render_jobs</c>
/// table does not exist in this wave: 09 README §B10 WT2 rules the soft edge to 09.6.1-T1 that way, and the
/// latest-by-<c>id DESC</c> test arrives with that migration (WT49). Everything else the task file lists is
/// asserted against the real Api.
/// </para>
/// <para>
/// The command-counting tests replace exactly one registration of the host's read model — they add an
/// interceptor to it (EF Core applies every <c>IDbContextOptionsConfiguration&lt;ReadDbContext&gt;</c>, so
/// the production connection string, naming convention and tracking behaviour all stay in place). No test
/// sleeps: every state is seeded directly through the TestKit builders (D13).
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class GetEpisodeDetailTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>A fixed instant, so every seeded row and the ETag stamps are deterministic.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The wire options: camelCase members, read case-insensitively (09 README D14).</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string EpisodesRoute = "/api/v1/episodes";

    // CA1861: a constant array handed to a repeatedly-called method belongs in a static readonly field,
    // not in a `new[] { … }` argument. These are the domain of 00 §7 and the D14 wire contract.
    private static readonly string[] VideoReviewActions = new[] { "approve_video", "reject_video", "download", "cancel" };
    private static readonly string[] StoryApprovedActions = new[] { "render", "reopen_story", "cancel" };
    private static readonly string[] ReadyActions = new[] { "publish", "download", "cancel" };
    private static readonly string[] StoryGeneratingActions = new[] { "cancel" };
    private static readonly string[] DetailMemberNames = new[]
    {
        "id", "seriesId", "title", "brief", "lengthTier", "status", "nextActions", "etag",
        "renderJob", "finalVideo", "publications",
    };
    private static readonly string[] FinalVideoMemberNames = new[]
    {
        "assetId", "sizeBytes", "sha256", "contentType", "durationSec", "retentionUntil",
    };

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Returns_All_Blocks_For_Own_Episode()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);

        var episodeId = await SeedEpisodeAsync(
            userId,
            seriesId,
            title: "  Tập một  ",
            brief: "Nội dung tập một.",
            status: EpisodeStatus.VideoReview,
            lengthTier: LengthTier.Long);
        var video = await SeedAssetAsync(
            userId,
            episodeId,
            AssetKind.Video,
            sizeBytes: 2_048L,
            durationSec: 12.5m,
            sha256: new string('b', 64),
            retentionUntil: Start.AddDays(30));
        await SeedAssetAsync(userId, episodeId, AssetKind.Subtitle);
        await Episodes(userId).AttachFinalVideoAsync(userId, episodeId, video.Id, Ct);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var (status, etag, body, json) = await ReadDetailAsync(client, episodeId);

        status.ShouldBe(HttpStatusCode.OK);
        body.Id.ShouldBe(episodeId);
        body.SeriesId.ShouldBe(seriesId);
        body.Title.ShouldBe("  Tập một  ", "the read returns the stored row as it is");
        body.Brief.ShouldBe("Nội dung tập một.");
        body.LengthTier.ShouldBe("long");
        body.Status.ShouldBe("video_review");

        body.NextActions.ShouldBe(VideoReviewActions);

        // The block order and the member list are the contract 10.5.2/10.6.x consume: the pages' fields,
        // then the three blocks. `publications` sits last and stays null (06 owns the table).
        MemberNames(json).ShouldBe(DetailMemberNames);

        body.Etag.ShouldBe(etag, "D15: one value in the header and the body");

        body.RenderJob.ShouldBeNull(
            "09 README §B10 WT2: the render block stays null until 09.6.1-T1 lands `render_jobs`");

        body.FinalVideo.ShouldNotBeNull();
        body.FinalVideo!.AssetId.ShouldBe(
            video.Id,
            "only the asset `episodes.final_video_asset_id` names is reported, not the episode's other assets");
        body.FinalVideo.SizeBytes.ShouldBe(2_048L);
        body.FinalVideo.Sha256.ShouldBe(new string('b', 64));
        body.FinalVideo.ContentType.ShouldBe(EpisodeBuilder.DefaultContentType);
        body.FinalVideo.DurationSec.ShouldBe(12.5);
        body.FinalVideo.RetentionUntil.ShouldBe(Start.AddDays(30));

        body.Publications.ShouldBeNull();

        // The video block is metadata only: no URL, no storage key, and no story or note text anywhere
        // (09.5.3 plan §Acceptance; the signed URL is 09.7.2-T1's own route).
        AssertNoForbiddenMembers(json, "url", "storageKey", "storage_key", "mediaUrl");
        MemberNames(json.GetProperty("finalVideo")).ShouldBe(FinalVideoMemberNames);
    }

    [Fact]
    public async Task Render_Job_Block_Is_Null_When_Never_Rendered()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId, status: EpisodeStatus.StoryApproved);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var (status, _, body, _) = await ReadDetailAsync(client, episodeId);

        status.ShouldBe(HttpStatusCode.OK);
        body.RenderJob.ShouldBeNull("an episode that never rendered reports no job, never a placeholder");
        body.NextActions.ShouldBe(StoryApprovedActions);
    }

    [Fact]
    public async Task Render_Job_Block_Is_Null_Until_Render_Jobs_Land()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId, status: EpisodeStatus.VideoReview);
        var video = await SeedAssetAsync(userId, episodeId, AssetKind.Video);
        await Episodes(userId).AttachFinalVideoAsync(userId, episodeId, video.Id, Ct);

        // WT2's soft edge has two halves: the block is null, and the page pays nothing for a table that is
        // not there — the request issues no command naming `render_jobs` at all.
        var interceptor = new CountingDbCommandInterceptor();

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var host = CountingHost(factory, interceptor);
        using var client = AuthenticatedClient(host, userId);

        interceptor.Reset();
        var (status, _, body, _) = await ReadDetailAsync(client, episodeId);

        status.ShouldBe(HttpStatusCode.OK);
        body.RenderJob.ShouldBeNull();
        interceptor.Texts.ShouldNotContain(
            text => text.Contains("render_jobs", StringComparison.OrdinalIgnoreCase),
            "nothing may read a table this wave does not have");
    }

    [Fact]
    public async Task Final_Video_Block_Is_Null_Without_Asset()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId, status: EpisodeStatus.Ready);

        // The episode has assets, but none of them is its final video.
        await SeedAssetAsync(userId, episodeId, AssetKind.Image);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var (status, _, body, _) = await ReadDetailAsync(client, episodeId);

        status.ShouldBe(HttpStatusCode.OK, "an episode without a video is still a page");
        body.FinalVideo.ShouldBeNull();
        body.NextActions.ShouldBe(ReadyActions);
    }

    [Fact]
    public async Task Publications_Block_Is_Null_In_P()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var (status, etag, body, json) = await ReadDetailAsync(client, episodeId);

        status.ShouldBe(HttpStatusCode.OK);
        body.Publications.ShouldBeNull(
            "06 owns `publications`; the placeholder stays null in P and adds no query (BD16)");

        // Its absence does not move, drop or reorder any other member (09.5.3 plan §Acceptance).
        json.GetProperty("publications").ValueKind.ShouldBe(JsonValueKind.Null);
        MemberNames(json).Count.ShouldBe(11);
    }

    [Fact]
    public async Task Status_And_NextActions_Are_Snake_Case()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId, status: EpisodeStatus.StoryGenerating);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var (_, _, body, _) = await ReadDetailAsync(client, episodeId);

        body.Status.ShouldBe("story_generating", "D14: enums travel snake_case, never PascalCase");
        body.LengthTier.ShouldBe("standard");
        body.NextActions.ShouldBe(StoryGeneratingActions);

        // Every member is the snake_case wire name of an EpisodeNextAction, never PascalCase.
        body.NextActions.ShouldAllBe(action =>
            string.Equals(action, action.ToLowerInvariant(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(EpisodeStatus.Draft, "draft", new[] { "generate_story", "cancel" })]
    [InlineData(EpisodeStatus.StoryGenerating, "story_generating", new[] { "cancel" })]
    [InlineData(
        EpisodeStatus.StoryReview,
        "story_review",
        new[] { "edit_story", "approve_story", "reopen_story", "cancel" })]
    [InlineData(EpisodeStatus.StoryApproved, "story_approved", new[] { "render", "reopen_story", "cancel" })]
    [InlineData(EpisodeStatus.Rendering, "rendering", new[] { "cancel" })]
    [InlineData(
        EpisodeStatus.VideoReview,
        "video_review",
        new[] { "approve_video", "reject_video", "download", "cancel" })]
    [InlineData(EpisodeStatus.Ready, "ready", new[] { "publish", "download", "cancel" })]
    [InlineData(EpisodeStatus.Scheduled, "scheduled", new[] { "publish", "cancel" })]
    [InlineData(EpisodeStatus.Published, "published", new string[0])]
    [InlineData(EpisodeStatus.Failed, "failed", new[] { "retry_render", "cancel" })]
    public async Task NextActions_Match_Domain_For_Status(
        EpisodeStatus status,
        string wireStatus,
        string[] expectedActions)
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId, status: status);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var (httpStatus, _, body, _) = await ReadDetailAsync(client, episodeId);

        httpStatus.ShouldBe(HttpStatusCode.OK);
        body.Status.ShouldBe(wireStatus);
        body.NextActions.AsEnumerable().ShouldBe(
            expectedActions,
            $"the wire array for {wireStatus} must equal the domain table of 00 §7");
    }

    [Fact]
    public async Task Other_Users_Episode_Returns_404_NotFound()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(owner);
        var episodeId = await SeedEpisodeAsync(owner, seriesId);
        var video = await SeedAssetAsync(owner, episodeId, AssetKind.Video);
        await Episodes(owner).AttachFinalVideoAsync(owner, episodeId, video.Id, Ct);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(stranger);

        using var response = await client.GetAsync($"{EpisodesRoute}/{episodeId}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "D10: another user's row is 404, never 403");
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");
    }

    [Fact]
    public async Task Unknown_Id_Returns_404_NotFound()
    {
        var userId = await CreateUserAsync();

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync($"{EpisodesRoute}/{Guid.CreateVersion7()}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");
    }

    [Fact]
    public async Task Cancelled_Episode_Returns_404_NotFound()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var cancelledId = await SeedEpisodeAsync(userId, seriesId, status: EpisodeStatus.Cancelled);
        var liveId = await SeedEpisodeAsync(userId, seriesId, createdAt: Start.AddMinutes(1));

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var cancelled = await client.GetAsync($"{EpisodesRoute}/{cancelledId}", Ct);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var cancelledProblem = await ReadProblemAsync(cancelled);

        using var unknown = await client.GetAsync($"{EpisodesRoute}/{Guid.CreateVersion7()}", Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var unknownProblem = await ReadProblemAsync(unknown);

        using var live = await client.GetAsync($"{EpisodesRoute}/{liveId}", Ct);
        live.StatusCode.ShouldBe(HttpStatusCode.OK, "only the cancelled row is hidden (F14)");

        // The 404 body names no reason: "unknown" and "cancelled" are one indistinguishable answer (D10).
        cancelledProblem.Code.ShouldBe("not_found");
        cancelledProblem.Code.ShouldBe(unknownProblem.Code);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-00000000000z")]
    [InlineData("12345678-1234-1234-1234-1234567890123")]
    public async Task Malformed_Id_IsNotFound_ByTheRouteConstraint(string malformed)
    {
        var userId = await CreateUserAsync();

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync($"{EpisodesRoute}/{malformed}", Ct);

        // The route is `/{id:guid}` (09.5.1-T1): a value the constraint refuses means the endpoint is not a
        // match at all, and ASP.NET Core's own guidance is explicit — "Don't use constraints for input
        // validation. If constraints are used for input validation, invalid input results in a 404"
        // (Routing in ASP.NET Core). The handler is never entered, so no validation_failed can be produced
        // here; the 400 the task file expected would require dropping the constraint, which would make the
        // answer depend on RouteHandlerOptions.ThrowOnBadRequest and change 09.5.1-T1's frozen route.
        response.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "a value the `:guid` constraint refuses is a route miss, never a 400 and never a 500");
        response.StatusCode.ShouldNotBe(HttpStatusCode.BadRequest);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Etag_Has_Ep_Prefix_And_Matches_Xmin()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var (_, etag, body, _) = await ReadDetailAsync(client, episodeId);

        etag.ShouldMatch("^\"ep-[0-9]+\"$", "D15: the episode ETag is \"ep-<xmin>\"");
        body.Etag.ShouldBe(etag);

        var xmin = await ReadXminAsync(episodeId);
        etag.ShouldBe($"\"ep-{xmin}\"", "the suffix is the row's own xmin, not a proxy for it");
    }

    [Fact]
    public async Task IfNoneMatch_Current_Etag_Returns_304_Without_Body()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);

        var interceptor = new CountingDbCommandInterceptor();

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var host = CountingHost(factory, interceptor);
        using var client = AuthenticatedClient(host, userId);

        var current = await ReadETagAsync(client, episodeId);

        // The episode has no final video, so a 200 costs exactly one command; the conditional request must
        // not add a second one to answer (09.5.3 plan §Acceptance, BD16).
        interceptor.Reset();
        using var response = await ConditionalGetAsync(client, episodeId, current);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty("a 304 carries no body");
        response.Headers.ETag!.ToString().ShouldBe(current, "the 304 repeats the tag the client sent");
        response.Headers.CacheControl!.ToString().ShouldBe("no-cache", "the page is revalidated every time");
        response.Headers.Vary.ShouldContain("Authorization");
        interceptor.Count.ShouldBe(1, "one episode projection, and no asset read for an episode without a video");
    }

    [Fact]
    public async Task IfNoneMatch_Stale_Etag_Returns_200_With_New_Etag()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var before = await ReadETagAsync(client, episodeId);

        // A tag the client invents (or a tag from long ago) is a miss: 200 with the current tag, which is
        // what the page must then keep.
        using var stale = await ConditionalGetAsync(client, episodeId, "\"ep-1\"");
        stale.StatusCode.ShouldBe(HttpStatusCode.OK);
        stale.Headers.ETag!.ToString().ShouldBe(before);

        // The episode changes (the ingest attaches its video) → new xmin → new tag, so the client's cached
        // copy revalidates as a miss next time.
        var video = await SeedAssetAsync(userId, episodeId, AssetKind.Video);
        await Episodes(userId).AttachFinalVideoAsync(userId, episodeId, video.Id, Ct);

        using var changed = await ConditionalGetAsync(client, episodeId, before);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, "the cached tag is stale now");
        changed.Headers.ETag!.ToString().ShouldNotBe(before);
        (await changed.Content.ReadFromJsonAsync<DetailBody>(Ct))!.FinalVideo!.AssetId.ShouldBe(video.Id);
    }

    [Fact]
    public async Task IfNoneMatch_WeakOrMultiValue_IsAMiss()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var current = await ReadETagAsync(client, episodeId);

        // D7 compares the exact strong strings: a weak form and a multi-value list both miss, which the
        // header's own string form already means.
        using var weak = await ConditionalGetAsync(client, episodeId, $"W/{current}");
        using var multi = await ConditionalGetAsync(client, episodeId, $"{current}, \"ep-1\"");

        weak.StatusCode.ShouldBe(HttpStatusCode.OK);
        weak.Headers.ETag!.ToString().ShouldBe(current);
        multi.StatusCode.ShouldBe(HttpStatusCode.OK);
        multi.Headers.ETag!.ToString().ShouldBe(current);
    }

    [Fact]
    public async Task IfNoneMatch_Unquoted_OrCaseChanged_Value_IsAMiss()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var current = await ReadETagAsync(client, episodeId);

        // More boundaries of the exact comparison (D7/D15): the bare value without its quotes and a tag
        // whose only difference is the case of `ep-` both miss, so a client cannot get a 304 from a tag it
        // never received — and no trimming or case folding happens on the way in.
        using var unquoted = await ConditionalGetAsync(client, episodeId, current.Trim('"'));
        using var caseChanged = await ConditionalGetAsync(
            client,
            episodeId,
            current.Replace("ep-", "EP-", StringComparison.Ordinal));

        unquoted.StatusCode.ShouldBe(HttpStatusCode.OK, "the strong tag is quoted; the bare value is another string");
        unquoted.Headers.ETag!.ToString().ShouldBe(current);
        caseChanged.StatusCode.ShouldBe(HttpStatusCode.OK, "a strong comparison is case-sensitive");
        caseChanged.Headers.ETag!.ToString().ShouldBe(current);
    }

    [Fact]
    public async Task IfNoneMatch_Wildcard_IsAMiss_UnderTheExactStrongComparison()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var current = await ReadETagAsync(client, episodeId);

        // RFC 9110 §13.1.2 defines `*` as "any current representation", but the pinned D7/D15 comparison is
        // the exact strong string, so the wildcard is a miss and answers the page it asks about (200 with
        // the current tag). Recorded so the behaviour is explicit and can be ruled on: no page consumer
        // sends `*`, and honouring it would only save the body the 200 already carries.
        using var wildcard = await ConditionalGetAsync(client, episodeId, "*");

        wildcard.StatusCode.ShouldBe(HttpStatusCode.OK);
        wildcard.Headers.ETag!.ToString().ShouldBe(current);
    }

    [Fact]
    public async Task IfNoneMatch_Current_EtagWithFinalVideo_StillAnswers_304()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId, status: EpisodeStatus.VideoReview);
        var video = await SeedAssetAsync(userId, episodeId, AssetKind.Video);
        await Episodes(userId).AttachFinalVideoAsync(userId, episodeId, video.Id, Ct);

        var interceptor = new CountingDbCommandInterceptor();

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var host = CountingHost(factory, interceptor);
        using var client = AuthenticatedClient(host, userId);

        var current = await ReadETagAsync(client, episodeId);

        // Plan §5 [DECISION]: the handler always computes the full DTO before the endpoint compares, so a
        // revalidation of a page with a video still pays its asset read — two commands, episode then asset
        // — and the "read `xmin` alone, then the blocks only on a miss" optimisation is not taken. This
        // pins that decision: a 304 here is served after the asset query, not before it.
        interceptor.Reset();
        using var response = await ConditionalGetAsync(client, episodeId, current);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        response.Headers.ETag!.ToString().ShouldBe(current);
        interceptor.Count.ShouldBe(
            2,
            "the endpoint compares only after the handler has read the episode and its video asset");
        interceptor.Commands[0].Text.ShouldContain("episodes", Case.Insensitive);
        interceptor.Commands[1].Text.ShouldContain("assets", Case.Insensitive);
    }

    [Fact]
    public async Task Other_Users_Episode_IsIndistinguishable_FromUnknown_AndCancelled()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(owner);
        var episodeId = await SeedEpisodeAsync(owner, seriesId);
        var cancelledId = await SeedEpisodeAsync(owner, seriesId, status: EpisodeStatus.Cancelled);
        var unknownId = Guid.CreateVersion7();

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var strangerClient = factory.ClientFor(stranger);
        using var ownerClient = factory.ClientFor(owner);

        var foreign = await ReadProblemFingerprintAsync(strangerClient, episodeId);
        var cancelled = await ReadProblemFingerprintAsync(ownerClient, cancelledId);
        var unknown = await ReadProblemFingerprintAsync(ownerClient, unknownId);

        foreign.Status.ShouldBe(HttpStatusCode.NotFound, "D10: another user's row is 404, never 403");
        foreign.Status.ShouldNotBe(HttpStatusCode.Forbidden);
        cancelled.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);

        // The three 404s differ only by `traceId` (ProblemDetailsWriter.TraceId: the request identifier),
        // so a caller cannot tell "not mine" from "unknown" from "cancelled" (D10, F14). Every other
        // member — type, title, status, code, errors, metadata — must be the same string.
        foreign.Fingerprint.ShouldContain("code=\"not_found\"");
        foreign.Fingerprint.ShouldBe(unknown.Fingerprint, "a foreign id must not read differently from an unknown one");
        cancelled.Fingerprint.ShouldBe(unknown.Fingerprint, "a cancelled id must not read differently either");
    }

    [Fact]
    public async Task Deleted_Episode_Returns_404_NotFound()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        // A real delete through the route under test: the owner cancels their own episode...
        using var delete = await client.DeleteAsync($"{EpisodesRoute}/{episodeId}", Ct);
        delete.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "If-Match is optional on DELETE and an absent header is accepted");

        // ...and the detail route then hides the cancelled row exactly like an unknown id (D16: DELETE →
        // `cancelled`, reads answer 404). No ETag is offered on the 404, so nothing is cacheable from it.
        using var afterDelete = await client.GetAsync($"{EpisodesRoute}/{episodeId}", Ct);
        afterDelete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(afterDelete)).Code.ShouldBe("not_found");
        afterDelete.Headers.ETag.ShouldBeNull("a 404 must not hand the client a tag to cache");
    }

    [Fact]
    public async Task Uses_Two_Database_Commands_Only()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId, status: EpisodeStatus.VideoReview);
        var video = await SeedAssetAsync(userId, episodeId, AssetKind.Video);
        await SeedAssetAsync(userId, episodeId, AssetKind.Subtitle);
        await Episodes(userId).AttachFinalVideoAsync(userId, episodeId, video.Id, Ct);

        var interceptor = new CountingDbCommandInterceptor();

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var host = CountingHost(factory, interceptor);
        using var client = AuthenticatedClient(host, userId);

        interceptor.Reset();
        var (status, _, body, _) = await ReadDetailAsync(client, episodeId);

        status.ShouldBe(HttpStatusCode.OK);
        body.FinalVideo.ShouldNotBeNull();

        // Exactly two round trips (BD16): one episode projection plus one asset read. The episode's second
        // asset must not add a third command — that would be an N+1.
        interceptor.Count.ShouldBe(2, string.Join(" | ", interceptor.Texts));
        interceptor.Texts.ShouldContain(text => text.Contains("episodes", StringComparison.OrdinalIgnoreCase));
        interceptor.Texts.ShouldContain(text => text.Contains("assets", StringComparison.OrdinalIgnoreCase));
        interceptor.Texts.ShouldNotContain(text => text.Contains("render_jobs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Uses_One_Database_Command_When_No_Final_Video()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);

        var interceptor = new CountingDbCommandInterceptor();

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var host = CountingHost(factory, interceptor);
        using var client = AuthenticatedClient(host, userId);

        interceptor.Reset();
        var (status, _, _, _) = await ReadDetailAsync(client, episodeId);

        status.ShouldBe(HttpStatusCode.OK);
        interceptor.Count.ShouldBe(1, "without `final_video_asset_id` there is nothing else to read");
    }

    [Fact]
    public async Task Queries_RunSequentially_OnOneDbContext_NoWhenAll()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);
        var video = await SeedAssetAsync(userId, episodeId, AssetKind.Video);
        await Episodes(userId).AttachFinalVideoAsync(userId, episodeId, video.Id, Ct);

        var interceptor = new CountingDbCommandInterceptor();

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var host = CountingHost(factory, interceptor);
        using var client = AuthenticatedClient(host, userId);

        interceptor.Reset();
        var (status, _, _, _) = await ReadDetailAsync(client, episodeId);

        status.ShouldBe(HttpStatusCode.OK);

        // WT46: the reads run one after the other on the single read context. A Task.WhenAll/`Task` pair
        // would have shown up as a command that started while another was still in flight.
        interceptor.ObservedConcurrentExecution.ShouldBeFalse(
            $"no two commands may overlap: {string.Join(" | ", interceptor.Texts)}");
        interceptor.Commands.Count.ShouldBe(2);
        interceptor.Commands.ShouldAllBe(command => command.ActiveWhenStarted == 0);
        interceptor.Commands[0].Text.ShouldContain("episodes", Case.Insensitive);
        interceptor.Commands[1].Text.ShouldContain("assets", Case.Insensitive);
    }

    [Fact]
    public async Task Requires_Authentication()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var anonymous = factory.AnonymousClient();

        using var response = await anonymous.GetAsync($"{EpisodesRoute}/{episodeId}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "every message is denied by default (D4)");
    }

    [Fact]
    public async Task Dtos_Do_Not_Contain_Media_Url()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId, status: EpisodeStatus.VideoReview);
        var video = await SeedAssetAsync(userId, episodeId, AssetKind.Video);
        await Episodes(userId).AttachFinalVideoAsync(userId, episodeId, video.Id, Ct);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var (_, _, _, json) = await ReadDetailAsync(client, episodeId);

        // The only signed media URL is `GET /episodes/{id}/video-url` (09.7.2-T1, 15 min, audited), and the
        // storage key never leaves the server: a body a browser holds must carry neither.
        AssertNoForbiddenMembers(json, "url", "videoUrl", "storageKey", "storage_key", "mediaUrl", "signedUrl");
    }

    /// <summary>
    /// Reads the detail route once and returns everything a test asserts on: the status, the <c>ETag</c>
    /// header, the parsed body and the raw JSON. The response and the <see cref="JsonDocument"/> are
    /// disposed here — the element is cloned so it outlives the document.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string ETag, DetailBody Body, JsonElement Json)> ReadDetailAsync(
        HttpClient client,
        Guid episodeId)
    {
        using var response = await client.GetAsync($"{EpisodesRoute}/{episodeId}", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        var body = JsonSerializer.Deserialize<DetailBody>(text, JsonOptions);
        body.ShouldNotBeNull();

        JsonElement json;
        using (var document = JsonDocument.Parse(text))
        {
            json = document.RootElement.Clone();
        }

        return (response.StatusCode, response.Headers.ETag?.ToString() ?? string.Empty, body, json);
    }

    /// <summary>A GET carrying <c>If-None-Match</c>, whose value the client controls byte for byte.</summary>
    private static Task<HttpResponseMessage> ConditionalGetAsync(HttpClient client, Guid episodeId, string ifNoneMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{EpisodesRoute}/{episodeId}");
        request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

        return client.SendAsync(request, Ct);
    }

    private static async Task<string> ReadETagAsync(HttpClient client, Guid episodeId)
    {
        using var response = await client.GetAsync($"{EpisodesRoute}/{episodeId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();

        return response.Headers.ETag!.ToString();
    }

    /// <summary>The row's <c>xmin</c>, so the ETag can be compared with the value it claims to carry.</summary>
    private async Task<long> ReadXminAsync(Guid episodeId)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(Ct);

        await using var command = new NpgsqlCommand(
            "SELECT xmin::text::bigint FROM episodes WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", episodeId);

        var xmin = await command.ExecuteScalarAsync(Ct);

        return xmin is long value
            ? value
            : throw new InvalidOperationException($"The episode {episodeId} has no row to read xmin from.");
    }

    /// <summary>
    /// A factory whose read model also reports its commands through <paramref name="interceptor"/>. It adds
    /// one options configuration to the production registration, so the host keeps its connection string,
    /// its naming convention and its no-tracking behaviour.
    /// </summary>
    /// <remarks>
    /// Generic over the entry point on purpose: both hosts of the solution compile a top-level
    /// <c>Program</c>, and this test assembly references both, so naming <c>Program</c> here is CS0433. The
    /// type parameter is inferred from <paramref name="factory"/> instead (the same reason <see
    /// cref="ApiFactory"/> keeps the entry point type inside TestKit).
    /// </remarks>
    private static WebApplicationFactory<TEntryPoint> CountingHost<TEntryPoint>(
        WebApplicationFactory<TEntryPoint> factory,
        CountingDbCommandInterceptor interceptor)
        where TEntryPoint : class =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<ReadDbContext>((_, options) => options.AddInterceptors(interceptor))));

    /// <summary>An authenticated client for a host that <see cref="ApiFactory.ClientFor"/> cannot reach.</summary>
    private static HttpClient AuthenticatedClient<TEntryPoint>(
        WebApplicationFactory<TEntryPoint> host,
        Guid userId)
        where TEntryPoint : class
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, TestAuthHandler.RoleClaimValue(PlatformRole.User));

        return client;
    }

    private static IReadOnlyList<string> MemberNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name)];

    private static void AssertNoForbiddenMembers(JsonElement element, params string[] forbidden)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    forbidden.ShouldNotContain(
                        property.Name,
                        $"the response must not carry '{property.Name}'");
                    AssertNoForbiddenMembers(property.Value, forbidden);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertNoForbiddenMembers(item, forbidden);
                }

                break;
        }
    }

    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe((int)response.StatusCode);

        return problem;
    }

    /// <summary>
    /// GETs an episode and returns the 404 body as a member-joined string with <c>traceId</c> removed, so
    /// two answers that differ only by their request identifier can be compared for an exact match (D10).
    /// </summary>
    private static async Task<(HttpStatusCode Status, string Fingerprint)> ReadProblemFingerprintAsync(
        HttpClient client,
        Guid episodeId)
    {
        using var response = await client.GetAsync($"{EpisodesRoute}/{episodeId}", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        using var document = JsonDocument.Parse(text);
        var members = document.RootElement.EnumerateObject()
            .Where(property => !string.Equals(property.Name, "traceId", StringComparison.Ordinal))
            .Select(property => $"{property.Name}={property.Value.GetRawText()}");

        return (response.StatusCode, string.Join(";", members));
    }

    private async Task<Guid> CreateUserAsync()
        => (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct)).Id;

    /// <summary>The series builder, over the class database and the fixed clock.</summary>
    private SeriesBuilder Series() => new(Database, new TestCurrentUser(), new FakeClock(Start));

    /// <summary>
    /// The episode builder, bound to <paramref name="userId"/>: unlike a plain insert, the builder's
    /// <c>AttachFinalVideoAsync</c> queries, so the D10 tenant filter needs the actual owner.
    /// </summary>
    private EpisodeBuilder Episodes(Guid userId) =>
        new(Database, new TestCurrentUser { Id = userId }, new FakeClock(Start));

    private async Task<Guid> SeedSeriesAsync(Guid userId, string title = "Chuỗi thử nghiệm")
    {
        var voice = await Series().EnsureVoiceAsync(ct: Ct);
        var template = await Series().EnsureStyleTemplateAsync(ct: Ct);

        var series = await Series().CreateAsync(
            userId,
            title: title,
            voiceId: voice.Id,
            styleTemplateId: template.Id,
            createdAt: Start,
            ct: Ct);

        return series.Id;
    }

    private async Task<Guid> SeedEpisodeAsync(
        Guid userId,
        Guid seriesId,
        string brief = EpisodeBuilder.DefaultBrief,
        string? title = null,
        LengthTier lengthTier = LengthTier.Standard,
        EpisodeStatus status = EpisodeStatus.Draft,
        DateTimeOffset? createdAt = null)
        => (await Episodes(userId).CreateAsync(
            userId,
            seriesId,
            brief: brief,
            title: title,
            lengthTier: lengthTier,
            status: status,
            createdAt: createdAt ?? Start,
            ct: Ct)).Id;

    private Task<Asset> SeedAssetAsync(
        Guid userId,
        Guid episodeId,
        AssetKind kind,
        long sizeBytes = EpisodeBuilder.DefaultSizeBytes,
        decimal? durationSec = null,
        string? sha256 = null,
        DateTimeOffset? retentionUntil = null)
        => Episodes(userId).CreateAssetAsync(
            userId,
            episodeId,
            kind,
            retentionUntil: retentionUntil,
            sizeBytes: sizeBytes,
            durationSec: durationSec,
            sha256: sha256,
            ct: Ct);

    /// <summary>The <c>GET /episodes/{id}</c> body (09.5.3 plan §5, snake_case enums per D14).</summary>
    private sealed record DetailBody(
        Guid Id,
        Guid SeriesId,
        string? Title,
        string Brief,
        string LengthTier,
        string Status,
        List<string> NextActions,
        string Etag,
        RenderJobBody? RenderJob,
        FinalVideoBody? FinalVideo,
        PublicationsBody? Publications);

    /// <summary>
    /// The latest-render-job block: null in P until 09.6.1-T1 lands <c>render_jobs</c> (WT2), listed here so
    /// the contract the block must carry is visible beside the tests that assert its absence.
    /// </summary>
    private sealed record RenderJobBody(
        Guid RenderJobId,
        string Status,
        string? Stage,
        int Percent,
        int Attempt,
        int ImagesNeeded,
        DateTimeOffset? SubmittedAt,
        DateTimeOffset? FinishedAt,
        string? ErrorCode,
        List<string> Hints);

    /// <summary>The standalone video block: metadata only, no URL.</summary>
    private sealed record FinalVideoBody(
        Guid AssetId,
        long SizeBytes,
        string Sha256,
        string ContentType,
        double? DurationSec,
        DateTimeOffset? RetentionUntil);

    /// <summary>The publications placeholder, which 06.2.1-T1 fills (null in P).</summary>
    private sealed record PublicationsBody(int Published, int Scheduled, DateTimeOffset? LastPublishedAt);

    /// <summary>The ProblemDetails members these tests assert on (09 README D7).</summary>
    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);
}
