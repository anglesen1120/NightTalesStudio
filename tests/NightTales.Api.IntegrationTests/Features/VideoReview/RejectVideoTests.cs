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
using NightTales.Application.Features.VideoReview.Commands.RejectVideo;
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
/// <c>POST /api/v1/episodes/{id}/video/reject</c> against the real host and PostgreSQL (09.7.1-T1, 09 README
/// BD1/BD5/D10/D15, WT3): the stored trimmed reason, the two hand-off targets (<c>story_review</c> as 200,
/// <c>story_approved</c> as 202 with no <c>Location</c>), the <c>Idempotency-Key</c> replay, the validation
/// refusals and the tenant-isolated 404.
/// </summary>
/// <remarks>
/// The outbox row this route raises is the Worker's re-render trigger; it is asserted here as the wire-visible
/// half (one row, ids and a snake_case-lower <c>target</c>, never the reason text) because
/// <c>video_rejected.v1</c> carries no content. <see cref="VideoReRenderOutboxTests"/> drives the consumer.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RejectVideoTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The audit action of a rejection.</summary>
    private const string Action = "episode.video_rejected";

    /// <summary>The outbox type of a rejection.</summary>
    private const string Event = "video_rejected.v1";

    /// <summary>A phrase only the reason carries, so a content-free column can be proven free of it.</summary>
    private const string Canary = "canary-reason-lantern";

    private static readonly DateTimeOffset Start = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task RejectVideo_ToStoryTarget_Returns200AndStoresReason()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await RejectVideoAsync(
            client,
            world.EpisodeId,
            Etag(world.EpisodeId),
            StoryBody("  Too dark at the start  "),
            key: "reject-story-1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.Location.ShouldBeNull("the story hand-off creates no resource");

        var after = Etag(world.EpisodeId);
        var body = await BodyAsync(response);
        body.GetProperty("id").GetGuid().ShouldBe(world.EpisodeId);
        body.GetProperty("status").GetString().ShouldBe("story_review");
        body.GetProperty("etag").GetString().ShouldBe(after);
        response.Headers.ETag!.Tag.ShouldBe(after);

        // The stored reason is trimmed: the editor's padding is not part of the reviewer's words.
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || coalesce(video_reject_reason, '<null>') FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("story_review|Too dark at the start");
    }

    [Fact]
    public async Task RejectVideo_ToStoryTarget_RaisesVideoRejectedV1_WithTargetStory()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await RejectVideoAsync(
            client,
            world.EpisodeId,
            Etag(world.EpisodeId),
            StoryBody($"Too dark at the start {Canary}"),
            key: "reject-story-outbox");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        OutboxCount(world).ShouldBe(1);

        var payload = JsonNode.Parse(OutboxPayload(world))!.AsObject();
        payload.Select(pair => pair.Key).Order(StringComparer.Ordinal).ShouldBe(
            ["episodeId", "occurredAt", "seriesId", "target", "userId"],
            Case.Sensitive,
            "the outbox carries ids and the target only");
        payload["episodeId"]!.GetValue<string>().ShouldBe(world.EpisodeId.ToString("D"));
        payload["seriesId"]!.GetValue<string>().ShouldBe(world.SeriesId.ToString("D"));
        payload["userId"]!.GetValue<string>().ShouldBe(world.UserId.ToString("D"));
        payload["target"]!.GetValue<string>().ShouldBe("story", "the enum is a snake_case-lower wire value");
        payload["occurredAt"]!.GetValue<DateTimeOffset>().ShouldBe(Start);

        OutboxPayload(world).ShouldNotContain(Canary, Case.Sensitive, "the reason text is not published");
        AuditCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task RejectVideo_ToReRender_Returns202AndLeavesStoryApproved()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await RejectVideoAsync(
            client,
            world.EpisodeId,
            Etag(world.EpisodeId),
            ReRenderBody("The lead is off model in every shot"),
            key: "reject-rerender-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.Location.ShouldBeNull(
            "the render job is created later by the outbox consumer, so there is no resource to point at yet");

        var after = Etag(world.EpisodeId);
        var body = await BodyAsync(response);
        body.GetProperty("status").GetString().ShouldBe("story_approved", "WT3: re_render goes back to the approved story");
        body.GetProperty("etag").GetString().ShouldBe(after);

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || coalesce(video_reject_reason, '<null>') FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("story_approved|The lead is off model in every shot");
        OutboxCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task RejectVideo_ToReRender_IsIdempotent_WhenTheKeyRepeats()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var body = ReRenderBody($"The lead is off model {Canary}");
        const string key = "reject-rerender-repeat";

        var etag = Etag(world.EpisodeId);
        using var first = await RejectVideoAsync(client, world.EpisodeId, etag, body, key);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync(Ct));
        var firstBody = await first.Content.ReadAsStringAsync(Ct);

        // A network retry resends the same request (same key, same If-Match): it replays the stored 202
        // instead of rejecting a second time, so nothing moved, no second event was raised and no second
        // audit row was written. (The request hash covers If-Match, so a retry with a different tag is a
        // different request and is refused as `idempotency_key_reused`, like every idempotent command.)
        using var second = await RejectVideoAsync(client, world.EpisodeId, etag, body, key);

        second.StatusCode.ShouldBe(HttpStatusCode.Accepted, await second.Content.ReadAsStringAsync(Ct));
        (await second.Content.ReadAsStringAsync(Ct)).ShouldBe(firstBody, "the stored response is replayed verbatim");
        OutboxCount(world).ShouldBe(1);
        AuditCount(world).ShouldBe(1);
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || coalesce(video_reject_reason, '<null>') FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe($"story_approved|The lead is off model {Canary}", "the row holds the first rejection and was not re-written");
    }

    [Fact]
    public async Task RejectVideo_WithoutIdempotencyKey_Returns400()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await RejectVideoAsync(
            client,
            world.EpisodeId,
            Etag(world.EpisodeId),
            StoryBody("Too dark at the start"));

        await AssertProblemAsync(response, 400, "idempotency_key_required");
        AssertUntouched(world);
    }

    [Fact]
    public async Task RejectVideo_WithEmptyOr501CharReason_Returns400ValidationFailed()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var tag = Etag(world.EpisodeId);

        using (var empty = await RejectVideoAsync(client, world.EpisodeId, tag, StoryBody(string.Empty), "reason-empty"))
        {
            await AssertProblemAsync(empty, 400, "validation_failed");
        }

        using (var blank = await RejectVideoAsync(client, world.EpisodeId, tag, StoryBody("   "), "reason-blank"))
        {
            await AssertProblemAsync(blank, 400, "validation_failed");
        }

        using (var absent = await RejectVideoAsync(
                   client,
                   world.EpisodeId,
                   tag,
                   """{"target":"story"}""",
                   "reason-absent"))
        {
            await AssertProblemAsync(absent, 400, "validation_failed");
        }

        using (var tooLong = await RejectVideoAsync(
                   client,
                   world.EpisodeId,
                   tag,
                   StoryBody(new string('r', 501)),
                   "reason-501"))
        {
            await AssertProblemAsync(tooLong, 400, "validation_failed");
        }

        AssertUntouched(world);

        // 500 characters is the boundary the column contract allows: a long note still rejects.
        using (var accepted = await RejectVideoAsync(
                   client,
                   world.EpisodeId,
                   tag,
                   StoryBody(new string('r', 500)),
                   "reason-500"))
        {
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(Ct));
        }

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || length(video_reject_reason)::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("story_review|500");
    }

    [Fact]
    public async Task RejectVideo_WithUnknownTargetValue_Returns400()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await RejectVideoAsync(
            client,
            world.EpisodeId,
            Etag(world.EpisodeId),
            """{"reason":"Too dark at the start","target":"sideways"}""",
            key: "unknown-target");

        await AssertProblemAsync(response, 400, "validation_failed");
        AssertUntouched(world);
    }

    [Fact]
    public async Task RejectVideo_OfAnotherUsersEpisode_Returns404()
    {
        var owner = await SeedAsync();
        var stranger = await SeedAsync();
        using var factory = Factory(actorId: stranger.UserId);
        using var client = factory.ClientFor(stranger.UserId);

        foreach (var body in new[] { StoryBody("Too dark at the start"), ReRenderBody("The lead is off model") })
        {
            using var foreign = await RejectVideoAsync(
                client,
                owner.EpisodeId,
                Etag(owner.EpisodeId),
                body,
                key: $"foreign-{body.Length}");

            await AssertProblemAsync(foreign, 404, "not_found");
        }

        using var absent = await RejectVideoAsync(
            client,
            Guid.CreateVersion7(),
            "\"ep-1\"",
            StoryBody("Too dark at the start"),
            key: "absent-id");

        await AssertProblemAsync(absent, 404, "not_found");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var sent = await scope.ServiceProvider.GetRequiredService<ISender>().Send(
                new RejectVideoCommand(owner.EpisodeId, "Too dark at the start", RejectTarget.Story, null, "foreign-command"),
                Ct);

            sent.FirstError.Code.ShouldBe("not_found");
        }

        AssertUntouched(owner);
        AuditCount(owner).ShouldBe(0);
    }

    /// <summary>
    /// The host of these tests: the real Api with the fixed clock, and — for the in-process leg — the caller
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

    /// <summary>One owner with a series, one episode awaiting review and one live render asset.</summary>
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
        await new EpisodeBuilder(Database, new TestCurrentUser { Id = user.Id }).CreateAssetAsync(
            user.Id,
            episode.Id,
            kind: AssetKind.Video,
            ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    private static Task<HttpResponseMessage> RejectVideoAsync(
        HttpClient client,
        Guid episodeId,
        string? tag,
        string body,
        string? key = null) =>
        SendAsync(client, $"/api/v1/episodes/{episodeId}/video/reject", tag, body, key);

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string route,
        string? tag,
        string body,
        string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (tag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Ct);
    }

    private static string StoryBody(string reason) => $$"""{"reason":"{{reason}}","target":"story"}""";

    private static string ReRenderBody(string reason) => $$"""{"reason":"{{reason}}","target":"re_render"}""";

    private void AssertUntouched(World world)
    {
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || (video_reject_reason IS NULL)::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("video_review|true");
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
            System.Globalization.CultureInfo.InvariantCulture));

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return document.RootElement.Clone();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe((HttpStatusCode)status, text);
        JsonSerializer.Deserialize<ProblemResponse>(text, ProblemResponse.Options)!.Code.ShouldBe(code);
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
