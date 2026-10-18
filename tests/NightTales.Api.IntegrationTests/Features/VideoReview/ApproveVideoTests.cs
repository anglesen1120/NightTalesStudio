using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Common;
using NightTales.Application.Features.VideoReview.Commands.ApproveVideo;
using NightTales.Domain.Assets;
using NightTales.Domain.Episodes;
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
/// <c>POST /api/v1/episodes/{id}/video/approve</c> against the real host and PostgreSQL (09.7.1-T1,
/// 09 README BD1/D10/D15/WT56): the 200 with the post-commit episode ETag, the <c>If-Match</c> precondition
/// family, the tenant-isolated 404, the WT56 re-stamp of every live asset, the bounded audit row, the one
/// <c>video_approved.v1</c> event and the 02.8.2 autopilot call shape.
/// </summary>
/// <remarks>
/// <para>
/// Every test seeds its own owner, so the shared class database cannot leak rows: the route's tenant filter
/// restricts each read to the caller, and every audit/outbox count is scoped to the seeded user as well
/// (the class runs in parallel with the rest of the suite).
/// </para>
/// <para>
/// The clock is the <see cref="FakeClock"/> the host resolves, so <c>ready_at</c> and the re-stamped
/// <c>assets.retention_until</c> are exact instants rather than a range.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ApproveVideoTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The audit action of an approval (09.7.1 plan §Design).</summary>
    private const string Action = "episode.video_approved";

    /// <summary>The outbox type of an approval.</summary>
    private const string Event = "video_approved.v1";

    /// <summary>A phrase only the note carries, so a content-free column can be proven free of it.</summary>
    private const string Canary = "canary-note-quill";

    private static readonly DateTimeOffset Start = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task ApproveVideo_FromVideoReview_Returns200WithNewEtag()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var before = Etag(world.EpisodeId);

        using var response = await ApproveVideoAsync(client, world.EpisodeId, before);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        // D15: the tag is read after the commit, so it is the row version the approve wrote — and the body
        // member and the header are that same strong tag.
        var after = Etag(world.EpisodeId);
        after.ShouldNotBe(before, "the approved row is a new version");
        var body = await BodyAsync(response);
        body.GetProperty("id").GetGuid().ShouldBe(world.EpisodeId);
        body.GetProperty("status").GetString().ShouldBe("ready");
        body.GetProperty("etag").GetString().ShouldBe(after);
        response.Headers.ETag!.Tag.ShouldBe(after);

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || (ready_at = TIMESTAMPTZ '{Start:O}')::text || '|' || (video_reject_reason IS NULL)::text "
                + $"FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("ready|true|true", "the transition stamps ready_at from the command's clock");
    }

    [Fact]
    public async Task ApproveVideo_WithoutIfMatch_Returns428IfMatchRequired()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await ApproveVideoAsync(client, world.EpisodeId, tag: null);

        await AssertProblemAsync(response, 428, "if_match_required");
        AssertUntouched(world);
    }

    [Fact]
    public async Task ApproveVideo_WithStaleIfMatch_Returns412EpisodeConflict()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        // A tag the row never had, and the wildcard ETags.Matches deliberately refuses: neither is the value
        // the client could have read, so both are a lost-update refusal rather than an approval.
        using var stale = await ApproveVideoAsync(client, world.EpisodeId, "\"ep-999999\"");
        await AssertProblemAsync(stale, 412, "episode_conflict");

        using var wildcard = await ApproveVideoAsync(client, world.EpisodeId, "*");
        await AssertProblemAsync(wildcard, 412, "episode_conflict");

        AssertUntouched(world);
    }

    [Fact]
    public async Task ApproveVideo_WhenNotVideoReview_Returns409EpisodeInvalidState()
    {
        var world = await SeedAsync(EpisodeStatus.StoryReview);
        using var factory = Factory(actorId: world.UserId);
        using var client = factory.ClientFor(world.UserId);

        // The state check precedes the ETag compare (task §Failure handling, "Duplicate rejection"): a stale
        // tag on an episode that left video_review is a 409, not a 412 that a client would retry.
        using var stale = await ApproveVideoAsync(client, world.EpisodeId, "\"ep-999999\"");
        await AssertProblemAsync(stale, 409, "episode_invalid_state");

        // The in-process caller (the autopilot) skips only the ETag compare: the state check still applies.
        await using var scope = factory.Services.CreateAsyncScope();
        var sent = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new ApproveVideoCommand(world.EpisodeId, null, null), Ct);
        sent.IsError.ShouldBeTrue();
        sent.FirstError.Code.ShouldBe("episode_invalid_state");

        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("story_review");
        AuditCount(world).ShouldBe(0);
        OutboxCount(world).ShouldBe(0);
    }

    [Fact]
    public async Task ApproveVideo_OfAnotherUsersEpisode_Returns404()
    {
        var owner = await SeedAsync();
        var stranger = await SeedAsync();
        using var factory = Factory(actorId: stranger.UserId);
        using var client = factory.ClientFor(stranger.UserId);

        using var foreign = await ApproveVideoAsync(client, owner.EpisodeId, Etag(owner.EpisodeId));
        await AssertProblemAsync(foreign, 404, "not_found");

        using var absent = await ApproveVideoAsync(client, Guid.CreateVersion7(), "\"ep-1\"");
        await AssertProblemAsync(absent, 404, "not_found");

        // The same two answers in process: another tenant's row is invisible to the tenant filter, never 403.
        await using var scope = factory.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        (await sender.Send(new ApproveVideoCommand(owner.EpisodeId, null, null), Ct)).FirstError.Code
            .ShouldBe("not_found");
        (await sender.Send(new ApproveVideoCommand(Guid.CreateVersion7(), null, null), Ct)).FirstError.Code
            .ShouldBe("not_found");

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || (ready_at IS NULL)::text FROM episodes WHERE id = '{owner.EpisodeId}'")
            .ShouldBe("video_review|true", "the refused request left the row exactly as it was");
        AuditCount(owner).ShouldBe(0);
        OutboxCount(owner).ShouldBe(0);
    }

    [Fact]
    public async Task ApproveVideo_ReStampsRetentionUntil30Days_OnEveryLiveAsset()
    {
        var world = await SeedAsync();

        // The ingest's own stamp (09.6.3-T2, `Assets:UnapprovedRetentionDays`) is already on the row: approve
        // must overwrite it, not leave it, or the asset is swept 30 days after the render instead of 30 days
        // after the approval (WT56).
        var ingestStamp = Start.AddDays(20);
        var approveStamp = Start.AddDays(30);
        var video = await Builder(world).CreateAssetAsync(
            world.UserId,
            world.EpisodeId,
            kind: AssetKind.Video,
            retentionUntil: ingestStamp,
            ct: Ct);
        var subtitles = await Builder(world).CreateAssetAsync(
            world.UserId,
            world.EpisodeId,
            kind: AssetKind.Subtitle,
            retentionUntil: ingestStamp,
            ct: Ct);
        var deleted = await Builder(world).CreateAssetAsync(
            world.UserId,
            world.EpisodeId,
            kind: AssetKind.Thumbnail,
            retentionUntil: ingestStamp,
            deletedAt: Start.AddDays(-1),
            ct: Ct);

        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveVideoAsync(client, world.EpisodeId, Etag(world.EpisodeId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var live = $"episode_id = '{world.EpisodeId}' AND deleted_at IS NULL";
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM assets WHERE {live} AND retention_until = TIMESTAMPTZ '{approveStamp:O}'")
            .ShouldBe(2, "every live asset is re-stamped with Assets:RetentionDays from the approve instant");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM assets WHERE {live} AND retention_until = TIMESTAMPTZ '{ingestStamp:O}'")
            .ShouldBe(0, "the ingest's unapproved stamp does not survive the approval");
        SqlProbe.ScalarText(
                Database,
                $"SELECT (count(*) = 2 AND bool_and(deleted_at IS NULL))::text FROM assets WHERE id IN ('{video.Id}', '{subtitles.Id}')")
            .ShouldBe("true", "approve keeps both cut rows live; only their stamp moves");
        SqlProbe.ScalarText(
                Database,
                $"SELECT (retention_until = TIMESTAMPTZ '{ingestStamp:O}')::text FROM assets WHERE id = '{deleted.Id}'")
            .ShouldBe("true", "a soft-deleted asset is not part of the episode's live set and keeps its stamp");
    }

    [Fact]
    public async Task ApproveVideo_WritesEpisodeVideoApprovedAuditRow_WithNoReasonText()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await ApproveVideoAsync(
            client,
            world.EpisodeId,
            Etag(world.EpisodeId),
            Note($"the cut ends too early {Canary}"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        AuditCount(world).ShouldBe(1, "one command writes one audit row");
        SqlProbe.ScalarText(
                Database,
                $"SELECT entity_type || '|' || entity_id::text FROM audit_logs WHERE action = '{Action}' AND subject_user_id = '{world.UserId}'")
            .ShouldBe($"episode|{world.EpisodeId}");

        // The projection is ids and flags only (BD15): the owner's note never reaches the row, nor does a URL.
        var audit = SqlProbe.ScalarText(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = '{Action}' AND subject_user_id = '{world.UserId}'");
        audit.ShouldNotContain(Canary, Case.Sensitive, "the note text is not audited");
        audit.ShouldContain(Id(world.EpisodeId), Case.Sensitive);
        audit.ShouldContain("notePresent", Case.Sensitive);

        // The event payload is ids only as well, so the note cannot leak through the outbox either.
        OutboxPayload(world).ShouldNotContain(Canary, Case.Sensitive);
    }

    [Fact]
    public async Task ApproveVideo_RaisesVideoApprovedV1_ExactlyOnce()
    {
        var world = await SeedAsync();
        var video = await Builder(world).CreateAssetAsync(
            world.UserId,
            world.EpisodeId,
            kind: AssetKind.Video,
            ct: Ct);
        await Builder(world).AttachFinalVideoAsync(world.UserId, world.EpisodeId, video.Id, Ct);

        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveVideoAsync(client, world.EpisodeId, Etag(world.EpisodeId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        OutboxCount(world).ShouldBe(1);

        var payload = JsonNode.Parse(OutboxPayload(world))!.AsObject();
        payload.Select(pair => pair.Key).Order(StringComparer.Ordinal).ShouldBe(
            ["assetId", "episodeId", "occurredAt", "seriesId", "userId"],
            Case.Sensitive,
            "the outbox carries ids only");
        payload["episodeId"]!.GetValue<string>().ShouldBe(Id(world.EpisodeId));
        payload["seriesId"]!.GetValue<string>().ShouldBe(Id(world.SeriesId));
        payload["userId"]!.GetValue<string>().ShouldBe(Id(world.UserId));
        payload["assetId"]!.GetValue<string>().ShouldBe(Id(video.Id));
        payload["occurredAt"]!.GetValue<DateTimeOffset>().ShouldBe(Start);

        // A second approval is refused, so the event really is one per transition.
        using var repeat = await ApproveVideoAsync(client, world.EpisodeId, Etag(world.EpisodeId));
        await AssertProblemAsync(repeat, 409, "episode_invalid_state");
        OutboxCount(world).ShouldBe(1);
        AuditCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task ApproveVideo_AsAutopilot_AcceptsNullIfMatch()
    {
        var world = await SeedAsync();
        using var factory = Factory();

        // The 02.8.2 pinned call shape: two arguments, no `If-Match`, in a scope pinned to the run's user
        // exactly as an outbox delivery or the autopilot job pins it (ISystemContext.For, D10).
        await using var scope = factory.Services.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);

        var sent = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new ApproveVideoCommand(world.EpisodeId, "autopilot"), Ct);

        sent.IsError.ShouldBeFalse(
            string.Join(", ", sent.Errors.Select(error => error.Code)));
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || (ready_at = TIMESTAMPTZ '{Start:O}')::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("ready|true", "a null If-Match skips only the ETag compare, never the transition");
        AuditCount(world).ShouldBe(1);
        OutboxCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task ApproveVideo_NoteOver500Chars_Returns400ValidationFailed()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var refused = await ApproveVideoAsync(
            client,
            world.EpisodeId,
            Etag(world.EpisodeId),
            Note(new string('n', 501)));

        await AssertProblemAsync(refused, 400, "validation_failed");
        AssertUntouched(world);

        // The bound is inclusive: the longest note the column contract allows still approves.
        using var accepted = await ApproveVideoAsync(
            client,
            world.EpisodeId,
            Etag(world.EpisodeId),
            Note(new string('n', 500)));

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(Ct));
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("ready");
    }

    /// <summary>
    /// The host of these tests: the real Api with the fixed clock, and — for the in-process legs — the
    /// caller pinned through <see cref="ICurrentUser"/> so a handler that never went over HTTP still reads
    /// this test's own rows.
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

    /// <summary>One owner with a series and one episode of <paramref name="status"/>, and nothing else.</summary>
    /// <param name="status">The state the episode starts in; the video-review routes need <see cref="EpisodeStatus.VideoReview"/>.</param>
    /// <returns>The seeded ids.</returns>
    private async Task<World> SeedAsync(EpisodeStatus status = EpisodeStatus.VideoReview)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(
            user.Id,
            series.Id,
            lengthTier: LengthTier.Short,
            status: status,
            ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary>A builder pinned to the world's owner, for the asset rows of one episode.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The builder.</returns>
    private EpisodeBuilder Builder(World world) =>
        new(Database, new TestCurrentUser { Id = world.UserId });

    private static async Task<HttpResponseMessage> ApproveVideoAsync(
        HttpClient client,
        Guid episodeId,
        string? tag,
        string? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/video/approve");
        if (tag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }

    private static string Note(string text) => $$"""{"note":"{{text}}"}""";

    private void AssertUntouched(World world)
    {
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || (ready_at IS NULL)::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("video_review|true");
        AuditCount(world).ShouldBe(0);
        OutboxCount(world).ShouldBe(0);
    }

    private long AuditCount(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{Action}' AND subject_user_id = '{world.UserId}'");

    private long OutboxCount(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM outbox_messages WHERE type = '{Event}' AND user_id = '{world.UserId}'");

    private string OutboxPayload(World world) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = '{Event}' AND user_id = '{world.UserId}'");

    /// <summary>The episode's current strong ETag, read from <c>xmin</c> like the client's own copy.</summary>
    /// <param name="episodeId">The episode to read.</param>
    /// <returns>The quoted tag.</returns>
    private string Etag(Guid episodeId) =>
        ETags.Episode(uint.Parse(
            SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM episodes WHERE id = '{episodeId}'"),
            CultureInfo.InvariantCulture));

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return document.RootElement.Clone();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        response.StatusCode.ShouldBe((HttpStatusCode)status, await response.Content.ReadAsStringAsync(Ct));
        var problem = JsonSerializer.Deserialize<ProblemResponse>(
            await response.Content.ReadAsStringAsync(Ct),
            ProblemResponse.Options);
        problem!.Code.ShouldBe(code);
    }

    private static string Id(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
