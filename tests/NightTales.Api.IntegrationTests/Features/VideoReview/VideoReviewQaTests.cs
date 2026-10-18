using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.Http;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Common;
using NightTales.Domain.Assets;
using NightTales.Domain.Episodes;
using NightTales.Domain.Episodes.Events;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.VideoReview;

/// <summary>
/// Independent QA of 09.7.1-T1's two routes (09 README BD1/D7/D10/D15, WT3/WT56): the boundaries and
/// error paths the task's own acceptance tests leave open — the reject route's missing weak/blank/listed
/// <c>If-Match</c>, a duplicate rejection without the same key, an unreadable or oversized body, an
/// anonymous caller, the required-target discriminator, the re-stamp's episode scoping and an
/// approve/reject race on one row.
/// </summary>
/// <remarks>
/// <para>
/// Written by an agent that did not author the production code (QA gate of 09.7.1-T1, T3). Every test
/// drives the real host and PostgreSQL through the same harness as <see cref="ApproveVideoTests"/>; each
/// seeds its own user, so no count is global.
/// </para>
/// <para>
/// The tests assert what the task, plan §5 and the endpoints' own contracts say a client must see; a red
/// test here is a reported defect, never a flaky caveat.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class VideoReviewQaTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The audit action of an approval.</summary>
    private const string ApproveAction = "episode.video_approved";

    /// <summary>The audit action of a rejection.</summary>
    private const string RejectAction = "episode.video_rejected";

    private static readonly DateTimeOffset Start = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The two conflicts one lost race may answer with (D5's <c>xmin</c> mapping, or the state check).</summary>
    private static readonly string[] ConflictCodes = ["episode_invalid_state", "concurrency_conflict"];

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task RejectVideo_WithoutIfMatch_Returns428IfMatchRequired()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await RejectVideoAsync(
            client,
            world.EpisodeId,
            tag: null,
            StoryBody("Too dark at the start"),
            key: "qa-reject-no-tag");

        // D7/D15: the precondition filter answers before the body or the handler, and the key being
        // present does not stand in for the tag.
        await AssertProblemAsync(response, 428, "if_match_required");
        AssertUntouched(world);
    }

    [Fact]
    public async Task RejectVideo_WithStaleWeakOrListedIfMatch_Returns412EpisodeConflict()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var current = Etag(world.EpisodeId);

        var tags = new[]
        {
            "\"ep-999999\"",                       // a tag the row never had
            $"W/{current}",                        // a weak tag is not a lost-update proof
            $"{current}, \"ep-999999\"",           // a list is not the one tag the client read
        };

        foreach (var (tag, index) in tags.Select((value, position) => (value, position)))
        {
            using var response = await RejectVideoAsync(
                client,
                world.EpisodeId,
                tag,
                StoryBody("Too dark at the start"),
                key: $"qa-reject-tag-{index}");

            await AssertProblemAsync(response, 412, "episode_conflict");
        }

        AssertUntouched(world);
    }

    [Fact]
    public async Task RejectVideo_WhenTheVideoWasAlreadyDecided_Returns409EpisodeInvalidState()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var tag = Etag(world.EpisodeId);

        using (var first = await RejectVideoAsync(
                   client,
                   world.EpisodeId,
                   tag,
                   StoryBody("  Too dark at the start  "),
                   key: "qa-duplicate-first"))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        }

        // A client retry without the same key is a second decision, not a replay: the state check precedes
        // the ETag compare, so the stale tag the caller still holds answers the 409 it can act on rather
        // than a 412 it would retry.
        using (var second = await RejectVideoAsync(
                   client,
                   world.EpisodeId,
                   tag,
                   ReRenderBody("The lead is off model in every shot"),
                   key: "qa-duplicate-second"))
        {
            await AssertProblemAsync(second, 409, "episode_invalid_state");
        }

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || coalesce(video_reject_reason, '<null>') FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("story_review|Too dark at the start", "the second rejection changed nothing");
        VideoAuditCount(world).ShouldBe(1);
        OutboxCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task RejectVideo_WithMissingTarget_Returns400ValidationFailed()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var tag = Etag(world.EpisodeId);

        // A `null` target is an unreadable body: 400 under `target`.
        using (var nulled = await RejectVideoAsync(
                   client,
                   world.EpisodeId,
                   tag,
                   """{"reason":"Too dark at the start","target":null}""",
                   key: "qa-target-null"))
        {
            await AssertProblemAsync(nulled, 400, "validation_failed");
        }

        // A missing target is the same client mistake — the body contract names `reason` and `target` as
        // required, and an absent `reason` already answers 400 — so it must not silently pick `story`.
        using (var missing = await RejectVideoAsync(
                   client,
                   world.EpisodeId,
                   tag,
                   """{"reason":"Too dark at the start"}""",
                   key: "qa-target-missing"))
        {
            await AssertProblemAsync(missing, 400, "validation_failed");
        }

        AssertUntouched(world);
    }

    [Fact]
    public async Task VideoReview_AnonymousCallers_Return401()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var anonymous = factory.AnonymousClient();
        var tag = Etag(world.EpisodeId);

        using (var approve = await ApproveVideoAsync(anonymous, world.EpisodeId, tag))
        {
            approve.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await approve.Content.ReadAsStringAsync(Ct));
        }

        using (var reject = await RejectVideoAsync(
                   anonymous,
                   world.EpisodeId,
                   tag,
                   StoryBody("Too dark at the start"),
                   key: "qa-anonymous"))
        {
            reject.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await reject.Content.ReadAsStringAsync(Ct));
        }

        AssertUntouched(world);
    }

    [Fact]
    public async Task ApproveVideo_WithUnreadableBody_Returns400WithoutChangingTheEpisode()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var tag = Etag(world.EpisodeId);

        // A note that is not a string, an array root and truncated JSON: each is a coded 400 under the member
        // the client must fix, never a 500 and never an approval. A body the route's media type refuses is a
        // separate case - it never reaches these readers (see the theory below).
        using (var notAString = await ApproveVideoAsync(
                   client,
                   world.EpisodeId,
                   tag,
                   """{"note":5}"""))
        {
            var problem = await AssertProblemAsync(notAString, 400, "validation_failed");
            problem.Errors.ShouldNotBeNull().ShouldContainKey("note");
        }

        using (var arrayRoot = await ApproveVideoAsync(client, world.EpisodeId, tag, "[]"))
        {
            var problem = await AssertProblemAsync(arrayRoot, 400, "validation_failed");
            problem.Errors.ShouldNotBeNull().ShouldContainKey("body");
        }

        using (var truncated = await ApproveVideoAsync(client, world.EpisodeId, tag, """{"note":"""))
        {
            await AssertProblemAsync(truncated, 400, "validation_failed");
        }

        AssertUntouched(world);
    }

    [Fact]
    public async Task ApproveVideo_WithOversizedBody_Returns413PayloadTooLarge()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        // One byte over the host cap (BD12): the bounded reader answers the coded 413, not a 400 or a 500.
        var json = "{}" + new string(' ', checked((int)RequestLimitsOptions.DefaultMaxRequestBodySize) - 1);
        using var response = await ApproveVideoAsync(client, world.EpisodeId, Etag(world.EpisodeId), json);

        await AssertProblemAsync(response, 413, "payload_too_large");
        AssertUntouched(world);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task VideoReview_WithANonJsonContentType_AnswersTheDocumentedCoded400(string route)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var tag = Etag(world.EpisodeId);
        var body = route == "approve" ? """{"note":"Looks good"}""" : StoryBody("Too dark at the start");

        // Both routes document that any unreadable body is a coded 400 `validation_failed` under the member
        // the client must fix, and the story-approval route answers exactly that for this input. Observed
        // instead: the `Accepts<...>("application/json")` metadata answers the framework's 415 before the
        // handler and with an empty body, so a client branching on `code` gets nothing at all. Reported as
        // defect D4; this case is red until the routes agree with their own documentation (drop the media
        // type from `Accepts`, or document 415 - the plan's status list does not carry it either).
        using var response = await SendAsync(
            client,
            route == "approve" ? ApproveRoute(world.EpisodeId) : RejectRoute(world.EpisodeId),
            tag,
            body,
            key: $"qa-non-json-{route}",
            contentType: "text/plain");
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            $"{route} leg: status={(int)response.StatusCode}, body='{text}'");

        AssertUntouched(world);
    }

    [Fact]
    public async Task ApproveVideo_ReStampsOnlyItsOwnEpisodesLiveAssets()
    {
        var world = await SeedAsync();
        var neighbour = await SeedNeighbourEpisodeAsync(world);

        var ingestStamp = Start.AddDays(10);
        var approveStamp = Start.AddDays(30);
        var builder = Builder(world);

        var live = await builder.CreateAssetAsync(
            world.UserId,
            world.EpisodeId,
            kind: AssetKind.Video,
            retentionUntil: ingestStamp,
            ct: Ct);
        var deleted = await builder.CreateAssetAsync(
            world.UserId,
            world.EpisodeId,
            kind: AssetKind.Subtitle,
            retentionUntil: ingestStamp,
            deletedAt: Start.AddDays(-1),
            ct: Ct);
        var foreign = await builder.CreateAssetAsync(
            world.UserId,
            neighbour,
            kind: AssetKind.Video,
            retentionUntil: ingestStamp,
            ct: Ct);

        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveVideoAsync(client, world.EpisodeId, Etag(world.EpisodeId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        AssertStamp(live.Id, approveStamp, "the episode's own live asset is re-stamped (WT56)");
        AssertStamp(deleted.Id, ingestStamp, "a soft-deleted asset stays out of the set");
        AssertStamp(foreign.Id, ingestStamp, "another episode's asset is not the approved episode's");
    }

    [Fact]
    public async Task VideoReview_ConcurrentApproveAndReject_ExactlyOneTransitionWins()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var tag = Etag(world.EpisodeId);

        // Both callers hold the same tag and race on one row: the row lock serialises them, so the loser
        // must see the conflict (409), never a 500 and never a second transition.
        var approveTask = ApproveVideoAsync(client, world.EpisodeId, tag);
        var rejectTask = RejectVideoAsync(
            client,
            world.EpisodeId,
            tag,
            StoryBody("Too dark at the start"),
            key: "qa-concurrent-reject");

        using var approve = await approveTask;
        using var reject = await rejectTask;

        var approveWon = approve.StatusCode == HttpStatusCode.OK;
        approveWon.ShouldNotBe(
            reject.StatusCode == HttpStatusCode.OK,
            $"exactly one of the two racing decisions may win: approve={approve.StatusCode}, "
            + $"reject={reject.StatusCode}");

        var loser = approveWon ? reject : approve;
        var loserBody = await loser.Content.ReadAsStringAsync(Ct);
        loser.StatusCode.ShouldBe(HttpStatusCode.Conflict, loserBody);
        ConflictCodes.ShouldContain(LoserCode(loserBody) ?? "<no code>");

        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe(approveWon ? "ready" : "story_review", "the row holds the winner's transition");
        VideoAuditCount(world).ShouldBe(1, "only the winning command writes an audit row");
        OutboxCount(world).ShouldBe(1, "only the winning command raises an event");
    }

    [Fact]
    public async Task RejectVideo_AuditAndOutbox_CarryNoReasonTextOrKey()
    {
        var world = await SeedAsync();
        const string canary = "canary-qa-reason-quill";
        const string key = "qa-canary-key-7f3a";
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await RejectVideoAsync(
            client,
            world.EpisodeId,
            Etag(world.EpisodeId),
            ReRenderBody($"The lead is off model {canary}"),
            key);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));

        // BD15/BD16 and D6: the owner's words live on the episode only; the audit projection keeps ids and
        // flags, and the client's key is a secret that neither the trail nor the published message carries.
        var audit = SqlProbe.ScalarText(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = '{RejectAction}' AND subject_user_id = '{world.UserId}'");
        audit.ShouldNotContain(canary, Case.Sensitive, "the reason text is not audited");
        audit.ShouldNotContain(key, Case.Sensitive, "the idempotency key is not audited");
        audit.ShouldContain("re_render", Case.Sensitive);
        audit.ShouldContain("reasonPresent", Case.Sensitive);

        var payload = SqlProbe.ScalarText(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = 'video_rejected.v1' AND user_id = '{world.UserId}'");
        payload.ShouldNotContain(canary, Case.Sensitive, "the reason text is not published");
        payload.ShouldNotContain(key, Case.Sensitive);

        SqlProbe.ScalarText(
                Database,
                $"SELECT video_reject_reason FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe($"The lead is off model {canary}", "the episode row is where the reason belongs");
    }

    [Fact]
    public async Task VideoReviewEvents_ReachTheRealtimeConsumer()
    {
        var world = await SeedAsync();
        var notifier = new RecordingNotifier();

        var approved = await InsertOutboxAsync(
            world,
            "video_approved.v1",
            new VideoApproved(world.EpisodeId, world.SeriesId, world.UserId, AssetId: null, Start));
        var rejected = await InsertOutboxAsync(
            world,
            "video_rejected.v1",
            new VideoRejected(world.EpisodeId, world.SeriesId, world.UserId, RejectTarget.Story, Start));

        // The class shares one database with the route tests, whose rows are due as well: only this
        // delivery's two rows belong to this test, so the rest are settled instead of being delivered.
        await Database.ExecuteAsync(
            $"""
             UPDATE outbox_messages SET processed_at = '{Start:O}'
             WHERE type IN ('video_approved.v1', 'video_rejected.v1') AND processed_at IS NULL
             AND id <> '{approved}' AND id <> '{rejected}'
             """,
            Ct);

        await using var host = new WorkerHost(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000");
        host.ConfigureServices(services =>
        {
            services.RemoveAll<IRealtimeNotifier>();
            services.AddSingleton<IRealtimeNotifier>(notifier);
        });

        await host.StartAsync(Ct);
        await host.RunOutboxOnceAsync(Ct);

        Processed(approved).ShouldBeTrue("the approval consumer delivered and the row is processed (D11)");
        Processed(rejected).ShouldBeTrue("the rejection consumer delivered and the row is processed (D11)");

        var events = notifier.EventsFor(world.UserId);
        events
            .Count(entry => entry.Name == RealtimeEventName.EpisodeUpdated && entry.Ids.EpisodeId == world.EpisodeId)
            .ShouldBeGreaterThanOrEqualTo(2, "both video-review events invalidate the owner's episode page");
        events.All(entry => entry.Data is null).ShouldBeTrue("the invalidation carries ids only (BD11)");
    }

    /// <summary>The host of these tests: the real Api with the fixed clock.</summary>
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

    /// <summary>One owner with a series and one episode awaiting review, and nothing else.</summary>
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

    /// <summary>A second episode of the same owner, in a state the approve route never touches.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The neighbour's id.</returns>
    private async Task<Guid> SeedNeighbourEpisodeAsync(World world)
    {
        var episode = await Builder(world).CreateAsync(
            world.UserId,
            world.SeriesId,
            lengthTier: LengthTier.Short,
            status: EpisodeStatus.StoryReview,
            ct: Ct);

        return episode.Id;
    }

    /// <summary>A builder pinned to the world's owner.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The builder.</returns>
    private EpisodeBuilder Builder(World world) =>
        new(Database, new TestCurrentUser { Id = world.UserId });

    private static string ApproveRoute(Guid episodeId) => $"/api/v1/episodes/{episodeId}/video/approve";

    private static string RejectRoute(Guid episodeId) => $"/api/v1/episodes/{episodeId}/video/reject";

    private static Task<HttpResponseMessage> ApproveVideoAsync(
        HttpClient client,
        Guid episodeId,
        string? tag,
        string? body = null) =>
        SendAsync(client, ApproveRoute(episodeId), tag, body, key: null);

    private static Task<HttpResponseMessage> RejectVideoAsync(
        HttpClient client,
        Guid episodeId,
        string? tag,
        string body,
        string? key) =>
        SendAsync(client, RejectRoute(episodeId), tag, body, key);

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string route,
        string? tag,
        string? body,
        string? key,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        if (tag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        return await client.SendAsync(request, Ct);
    }

    private static string StoryBody(string reason) => $$"""{"reason":"{{reason}}","target":"story"}""";

    private static string ReRenderBody(string reason) => $$"""{"reason":"{{reason}}","target":"re_render"}""";

    private void AssertUntouched(World world)
    {
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || (ready_at IS NULL)::text || '|' || (video_reject_reason IS NULL)::text "
                + $"FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("video_review|true|true");
        VideoAuditCount(world).ShouldBe(0);
        OutboxCount(world).ShouldBe(0);
    }

    /// <summary>The one video-review audit row of the world's owner, whatever the command was.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The row count.</returns>
    private long VideoAuditCount(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action IN ('{ApproveAction}', '{RejectAction}') "
            + $"AND subject_user_id = '{world.UserId}'");

    /// <summary>The one video-review outbox row of the world's owner, whatever the event was.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The row count.</returns>
    private long OutboxCount(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM outbox_messages WHERE type IN ('video_approved.v1', 'video_rejected.v1') "
            + $"AND user_id = '{world.UserId}'");

    /// <summary>
    /// Writes one outbox row the way the interceptor would: the event's own payload under the outbox
    /// serializer, due immediately, no attempts spent.
    /// </summary>
    /// <typeparam name="TPayload">The domain event the row carries.</typeparam>
    /// <param name="world">The owner the delivery runs as.</param>
    /// <param name="type">The <c>[OutboxEvent]</c> name of the row.</param>
    /// <param name="payload">The event.</param>
    /// <returns>The row's id.</returns>
    private async Task<Guid> InsertOutboxAsync<TPayload>(World world, string type, TPayload payload)
        where TPayload : notnull
    {
        var id = Guid.CreateVersion7();
        var json = JsonSerializer.Serialize(payload, OutboxJson.Options);

        await Database.ExecuteAsync(
            $"""
             INSERT INTO outbox_messages (id, type, payload, user_id, occurred_at, next_attempt_at, attempts)
             VALUES ('{id}', '{type}', '{json}', '{world.UserId}', '{Start:O}', '{Start:O}', 0)
             """,
            Ct);

        return id;
    }

    /// <summary>Whether the dispatcher already marked one outbox row processed.</summary>
    /// <param name="messageId">The row to read.</param>
    /// <returns>The flag.</returns>
    private bool Processed(Guid messageId) =>
        SqlProbe.ScalarText(
                Database,
                $"SELECT (processed_at IS NOT NULL)::text FROM outbox_messages WHERE id = '{messageId}'")
            == "true";

    /// <summary>Asserts one asset's <c>retention_until</c> stamp.</summary>
    /// <param name="assetId">The asset to read.</param>
    /// <param name="expected">The instant the stamp must hold.</param>
    /// <param name="because">Why the stamp matters.</param>
    private void AssertStamp(Guid assetId, DateTimeOffset expected, string because) =>
        SqlProbe.ScalarText(
                Database,
                $"SELECT (retention_until = TIMESTAMPTZ '{expected:O}')::text FROM assets WHERE id = '{assetId}'")
            .ShouldBe("true", because);

    /// <summary>The episode's current strong ETag, read from <c>xmin</c> like the client's own copy.</summary>
    /// <param name="episodeId">The episode to read.</param>
    /// <returns>The quoted tag.</returns>
    private string Etag(Guid episodeId) =>
        ETags.Episode(uint.Parse(
            SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM episodes WHERE id = '{episodeId}'"),
            System.Globalization.CultureInfo.InvariantCulture));

    private static async Task<ProblemResponse> AssertProblemAsync(
        HttpResponseMessage response,
        int status,
        string code)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe((HttpStatusCode)status, text);

        var problem = JsonSerializer.Deserialize<ProblemResponse>(text, ProblemResponse.Options);
        problem.ShouldNotBeNull().Code.ShouldBe(code);

        return problem;
    }

    private static string? LoserCode(string body) =>
        JsonSerializer.Deserialize<ProblemResponse>(body, ProblemResponse.Options)?.Code;

    /// <summary>The notifier these tests run the Worker against: every delivery, with the user it ran as.</summary>
    private sealed class RecordingNotifier : IRealtimeNotifier
    {
        private readonly List<(Guid UserId, RealtimeEvent Event)> _events = [];

        /// <summary>The events one user's deliveries published, in delivery order.</summary>
        /// <param name="userId">The owner to read.</param>
        /// <returns>The events, ids only.</returns>
        public IReadOnlyList<RealtimeEvent> EventsFor(Guid userId)
        {
            lock (_events)
            {
                return [.. _events.Where(entry => entry.UserId == userId).Select(entry => entry.Event)];
            }
        }

        /// <inheritdoc />
        public Task NotifyAsync(Guid userId, RealtimeEvent realtimeEvent, CancellationToken cancellationToken)
        {
            lock (_events)
            {
                _events.Add((userId, realtimeEvent));
            }

            return Task.CompletedTask;
        }
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
