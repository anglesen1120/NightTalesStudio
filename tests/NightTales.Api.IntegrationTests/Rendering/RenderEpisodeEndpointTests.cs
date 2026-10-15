using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Usage;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// <c>POST /api/v1/episodes/{id}/render</c> against the real host and PostgreSQL (09 README BD5): the 202
/// contract, the replay of an <c>Idempotency-Key</c>, the two refusal families and the 409 backstop of the
/// partial unique index.
/// </summary>
/// <remarks>
/// The credit ledger is a recording fake, because the acceptance check asserts the job row's
/// <c>reservation_id</c> is exactly the <c>ReserveAsync</c> result and the replay asserts the reserve ran once.
/// Every other port is the production implementation over the rows this class seeds: the approved story version,
/// the series voice and style template, an active price version, and the tenant-filtered reads of the host.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RenderEpisodeEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Action = "render.requested";
    private const string RenderRequested = "render_requested.v1";

    /// <summary>A word only the seeded story text carries, so a content-free column can be proven free of it.</summary>
    private const string Canary = "spindlewick";

    /// <summary>A two-word keyword: the 02 rules fail it, so the render is blocked before any image is planned.</summary>
    private const string BlockedKeyword = "amber spindle";

    /// <summary>The paragraph keyword of an episode the linter lets through.</summary>
    private const string CleanKeyword = "empty wooden pier at dusk";

    private static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task RenderEpisode_Returns202_And_WritesOutbox()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);
        using var response = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "render-202");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var body = await BodyAsync(response);
        var renderJobId = body.GetProperty("renderJobId").GetGuid();
        body.GetProperty("status").GetString().ShouldBe("queued");
        response.Headers.Location!.ToString().ShouldBe($"/render-jobs/{renderJobId}");

        // One row, in the state the aggregate starts in, paying the reservation the ledger returned.
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE user_id='{world.UserId}'").ShouldBe(1);
        SqlProbe.ScalarText(Database,
                $"SELECT status || '|' || stage || '|' || percent || '|' || attempt || '|' || reservation_id::text FROM render_jobs WHERE user_id='{world.UserId}'")
            .ShouldBe($"queued|{RenderJob.QueuedStage}|0|1|{Id(ledger.Reservation)}");
        SqlProbe.Count(Database,
                $"SELECT count(*) FROM render_jobs WHERE user_id='{world.UserId}' AND images_needed IS NOT NULL AND images_needed = (params->'image_plan'->>'needed')::int")
            .ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT params->>'schema' FROM render_jobs WHERE user_id='{world.UserId}'")
            .ShouldBe("render-params.v1");
        SqlProbe.ScalarText(Database, $"SELECT params::text FROM render_jobs WHERE user_id='{world.UserId}'")
            .ShouldNotContain(Canary);
        ledger.ReserveCalls.ShouldBe(1);
        ledger.Operations.ShouldHaveSingleItem().ShouldBe(CreditOperation.Render);

        // One outbox row, ids only.
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE type='{RenderRequested}' AND user_id='{world.UserId}'").ShouldBe(1);
        var payload = JsonNode.Parse(SqlProbe.ScalarText(Database,
            $"SELECT payload::text FROM outbox_messages WHERE type='{RenderRequested}' AND user_id='{world.UserId}'"))!.AsObject();
        Keys(payload).ShouldBe("episodeId,occurredAt,renderJobId,userId");
        payload["renderJobId"]!.GetValue<string>().ShouldBe(Id(renderJobId));
        payload["episodeId"]!.GetValue<string>().ShouldBe(Id(world.EpisodeId));
        payload["userId"]!.GetValue<string>().ShouldBe(Id(world.UserId));
        payload["occurredAt"]!.GetValue<DateTimeOffset>().ShouldBe(Start);

        // The episode renders, its approved pointer is untouched, and the audit row carries ids only.
        SqlProbe.ScalarText(Database, $"SELECT status || '|' || approved_story_version_id::text FROM episodes WHERE id='{world.EpisodeId}'")
            .ShouldBe($"rendering|{world.VersionId}");
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE action='{Action}' AND subject_user_id='{world.UserId}'").ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT entity_type || '|' || entity_id::text FROM audit_logs WHERE action='{Action}' AND subject_user_id='{world.UserId}'")
            .ShouldBe($"episode|{Id(world.EpisodeId)}");
        var audit = JsonNode.Parse(SqlProbe.ScalarText(Database,
            $"SELECT data::text FROM audit_logs WHERE action='{Action}' AND subject_user_id='{world.UserId}'"))!.AsObject();
        Keys(audit).ShouldBe("episodeId,renderJobId");
        audit["episodeId"]!.GetValue<string>().ShouldBe(Id(world.EpisodeId));
        audit["renderJobId"]!.GetValue<string>().ShouldBe(Id(renderJobId));
    }

    [Fact]
    public async Task RenderEpisode_Replay_SameKey_ReturnsStoredResponse()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);
        var tag = Etag(world.EpisodeId);

        using var first = await RenderAsync(client, world.EpisodeId, tag, "render-replay");
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync(Ct));
        var stored = (await BodyAsync(first)).GetRawText();

        using var repeat = await RenderAsync(client, world.EpisodeId, tag, "render-replay");
        repeat.StatusCode.ShouldBe(HttpStatusCode.Accepted, await repeat.Content.ReadAsStringAsync(Ct));
        (await BodyAsync(repeat)).GetRawText().ShouldBe(stored);

        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE user_id='{world.UserId}'").ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE type='{RenderRequested}' AND user_id='{world.UserId}'").ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE action='{Action}' AND subject_user_id='{world.UserId}'").ShouldBe(1);
        ledger.ReserveCalls.ShouldBe(1);
    }

    [Fact]
    public async Task RenderEpisode_SameKeyOtherEpisode_Returns422()
    {
        var world = await SeedAsync();
        var otherEpisodeId = await SeedSecondEpisodeAsync(world);
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);

        using var first = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "render-reused");
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync(Ct));

        using var reuse = await RenderAsync(client, otherEpisodeId, Etag(otherEpisodeId), "render-reused");
        await AssertProblemAsync(reuse, 422, "idempotency_key_reused");

        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE user_id='{world.UserId}'").ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id='{otherEpisodeId}'").ShouldBe("story_approved");
        ledger.ReserveCalls.ShouldBe(1);
    }

    [Fact]
    public async Task RenderEpisode_ActiveJobExists_Returns409()
    {
        var world = await SeedAsync();
        var activeJobId = SeedActiveJob(world);
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);

        // A second render of an episode that still reads as approved reaches the insert, and the partial
        // unique index is the backstop: nothing is written, so no orphan reservation survives.
        using var second = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "render-second");
        await AssertProblemAsync(second, 409, "concurrency_conflict");
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id='{world.EpisodeId}'").ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT id::text FROM render_jobs WHERE episode_id='{world.EpisodeId}'")
            .ShouldBe(Id(activeJobId));
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE type='{RenderRequested}' AND user_id='{world.UserId}'").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id='{world.EpisodeId}'").ShouldBe("story_approved");

        // An episode already rendering is refused by the state gate, before anything is reserved.
        var rendering = await SeedAsync(EpisodeStatus.Rendering);
        using var renderingClient = factory.ClientFor(rendering.UserId);
        using var refused = await RenderAsync(renderingClient, rendering.EpisodeId, Etag(rendering.EpisodeId), "render-rendering");
        await AssertProblemAsync(refused, 409, "episode_invalid_state");
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE user_id='{rendering.UserId}'").ShouldBe(0);
        ledger.ReserveCalls.ShouldBe(1);
    }

    [Fact]
    public async Task RenderEpisode_OtherUsersEpisode_Returns404()
    {
        var owner = await SeedAsync();
        var stranger = await SeedAsync();
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(stranger.UserId);

        using var foreign = await RenderAsync(client, owner.EpisodeId, Etag(owner.EpisodeId), "render-foreign");
        await AssertProblemAsync(foreign, 404, "not_found");

        using var absent = await RenderAsync(client, Guid.CreateVersion7(), Etag(owner.EpisodeId), "render-absent");
        await AssertProblemAsync(absent, 404, "not_found");

        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id='{owner.EpisodeId}'").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id='{owner.EpisodeId}'").ShouldBe("story_approved");
        ledger.ReserveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RenderEpisode_NoAuth_Returns401()
    {
        var world = await SeedAsync();
        using var factory = Factory(new RecordingLedger());
        using var client = factory.AnonymousClient();
        using var request = Request(world.EpisodeId, Etag(world.EpisodeId), "render-anonymous");
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RenderEpisode_NoIdempotencyKey_Returns400()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);
        using var request = Request(world.EpisodeId, Etag(world.EpisodeId), key: null);
        using var response = await client.SendAsync(request, Ct);

        await AssertProblemAsync(response, 400, "idempotency_key_required");
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE user_id='{world.UserId}'").ShouldBe(0);
        ledger.ReserveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RenderEpisode_MissingIfMatch_Returns428()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);
        using var request = Request(world.EpisodeId, tag: null, key: "render-no-if-match");
        using var response = await client.SendAsync(request, Ct);

        await AssertProblemAsync(response, 428, "if_match_required");
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE user_id='{world.UserId}'").ShouldBe(0);
        ledger.ReserveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RenderEpisode_StaleIfMatch_Returns412()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);
        using var response = await RenderAsync(client, world.EpisodeId, "\"ep-999999\"", "render-stale");

        await AssertProblemAsync(response, 412, "episode_conflict");
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE user_id='{world.UserId}'").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id='{world.EpisodeId}'").ShouldBe("story_approved");
        ledger.ReserveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RenderEpisode_BlockingKeyword_Returns409WithFlagRows()
    {
        var world = await SeedAsync(paragraphKeyword: BlockedKeyword);
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);
        using var response = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "render-flagged");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        var body = await BodyAsync(response);
        body.GetProperty("code").GetString().ShouldBe("keywords_flagged");
        var flags = body.GetProperty("errors").EnumerateArray().ToArray();
        flags.Length.ShouldBe(1);
        flags[0].GetProperty("segment").GetString().ShouldBe("p:0");
        flags[0].GetProperty("index").GetInt32().ShouldBe(0);
        flags[0].GetProperty("rule").GetString().ShouldBe(StoryDraftValidator.KeywordInvalid);
        string.Join(',', flags[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal))
            .ShouldBe("index,rule,segment");

        // A flag is an address and a rule, never the keyword, the segment text or any other word of the story.
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.ShouldNotContain("amber");
        text.ShouldNotContain("spindle");
        text.ShouldNotContain("riverside");
        text.ShouldNotContain("pier");
        text.ShouldNotContain(Canary);
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE user_id='{world.UserId}'").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id='{world.EpisodeId}'").ShouldBe("story_approved");
        ledger.ReserveCalls.ShouldBe(0);
    }

    private ApiFactory Factory(ICreditLedger ledger) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<ICreditLedger>();
                services.AddSingleton(ledger);
            });

    /// <summary>
    /// One owner with a series, an episode and (unless <paramref name="status"/> says otherwise) an approved
    /// story version of short, clean text, plus the price version every paid step needs.
    /// </summary>
    /// <param name="status">The state the episode is left in; only <see cref="EpisodeStatus.StoryApproved"/> gets a version.</param>
    /// <param name="paragraphKeyword">The paragraph's keyword: the linter's input.</param>
    /// <returns>The seeded ids.</returns>
    private async Task<World> SeedAsync(
        EpisodeStatus status = EpisodeStatus.StoryApproved,
        string paragraphKeyword = CleanKeyword)
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
        await SeedPriceVersionAsync(user.Id);

        var versionId = status == EpisodeStatus.StoryApproved
            ? await SeedApprovedVersionAsync(user.Id, episode.Id, paragraphKeyword)
            : (Guid?)null;

        return new World(user.Id, series.Id, episode.Id, versionId);
    }

    /// <summary>A second approved episode of the same owner, for the reused-key refusal.</summary>
    /// <param name="world">The world whose series the episode belongs to.</param>
    /// <returns>The new episode's id.</returns>
    private async Task<Guid> SeedSecondEpisodeAsync(World world)
    {
        var actor = new TestCurrentUser { Id = world.UserId };
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(
            world.UserId,
            world.SeriesId,
            lengthTier: LengthTier.Short,
            ct: Ct);
        await SeedApprovedVersionAsync(world.UserId, episode.Id, CleanKeyword);

        return episode.Id;
    }

    private async Task<Guid> SeedApprovedVersionAsync(Guid userId, Guid episodeId, string paragraphKeyword)
    {
        var draft = Draft(paragraphKeyword);
        var version = StoryVersion.Create(userId, episodeId, 1, StoryVersionSource.Generated, draft,
            StoryContentJson.Write(draft, new JsonArray()), "model-x", "rel=1", null, null, null, null, null, Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start)))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database,
            $"UPDATE episodes SET status='story_approved', approved_story_version_id='{version.Id}', approved_at='{Start:O}' WHERE id='{episodeId}'");

        return version.Id;
    }

    private async Task SeedPriceVersionAsync(Guid userId)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));
        if (db.PriceVersions.Any(version => version.Version == 1))
        {
            return;
        }

        var rates = new PriceVersionRates(
            new Dictionary<string, PriceRates> { ["gpt-6-luna"] = new(0.10m, 0.01m, 0.125m, 0.50m) },
            new Dictionary<string, ImagePriceRate>(),
            null,
            TextCostCalculator.DefaultSurchargeThresholdTokens).ToJson();
        db.PriceVersions.Add(PriceVersion.Create(
            1,
            Start.AddDays(-1),
            rates,
            PriceVersionJson.WriteLimits(PriceLimits.D4Defaults),
            "render-endpoint-price-version",
            Start.AddDays(-1)));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// A queued <c>render_jobs</c> row of the episode, written with raw SQL: the point is that PostgreSQL's
    /// partial unique index refuses the command's own insert, which an aggregate-built row would not reach.
    /// </summary>
    /// <param name="world">The approved world the row belongs to.</param>
    /// <returns>The row's id.</returns>
    private Guid SeedActiveJob(World world)
    {
        var jobId = Guid.CreateVersion7();
        SqlProbe.Execute(Database,
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, percent, params, attempt) "
            + $"VALUES ('{jobId}', '{world.UserId}', '{world.EpisodeId}', '{world.VersionId}', '{Guid.CreateVersion7()}', "
            + $"'queued', '{RenderJob.QueuedStage}', 0, '{{\"schema\":\"render-params.v1\",\"music\":null}}'::jsonb, 1)");

        return jobId;
    }

    /// <summary>The story the render reads: one keyword per segment, and no name for the corpus to match.</summary>
    /// <param name="paragraphKeyword">The paragraph's keyword, valid or deliberately not.</param>
    /// <returns>The content document the version stores.</returns>
    private static StoryContentV1 Draft(string paragraphKeyword) => new(
        "The pier at dawn",
        new StorySegmentV1($"The {Canary} mist drifted over the riverside.", ["quiet riverside morning", "grey mist over water"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", [paragraphKeyword], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    /// <summary>The episode's current strong ETag, read from <c>xmin</c> like the client's own copy.</summary>
    /// <param name="episodeId">The episode to read.</param>
    /// <returns>The quoted tag.</returns>
    private string Etag(Guid episodeId) =>
        ETags.Episode(uint.Parse(
            SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM episodes WHERE id='{episodeId}'"),
            CultureInfo.InvariantCulture));

    private static async Task<HttpResponseMessage> RenderAsync(HttpClient client, Guid episodeId, string? tag, string? key)
    {
        using var request = Request(episodeId, tag, key);

        return await client.SendAsync(request, Ct);
    }

    private static HttpRequestMessage Request(Guid episodeId, string? tag, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/render");
        if (tag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return request;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return document.RootElement.Clone();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        response.StatusCode.ShouldBe((HttpStatusCode)status, await response.Content.ReadAsStringAsync(Ct));
        var problem = JsonSerializer.Deserialize<ProblemResponse>(await response.Content.ReadAsStringAsync(Ct), ProblemResponse.Options);
        problem!.Code.ShouldBe(code);
    }

    /// <summary>The member names of a stored JSON document, so an ids-only payload is asserted as a whole.</summary>
    /// <param name="node">The document.</param>
    /// <returns>The names, ordinal-sorted and comma-joined.</returns>
    private static string Keys(JsonObject node) =>
        string.Join(',', node.Select(pair => pair.Key).Order(StringComparer.Ordinal));

    private static string Id(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId, Guid? VersionId);

    /// <summary>
    /// The credit ledger the render endpoint tests run against: it returns one known reservation id, so the
    /// persisted <c>render_jobs.reservation_id</c> can be asserted, and counts the reserves, so a replayed
    /// command can be proven not to have reserved twice.
    /// </summary>
    private sealed class RecordingLedger : ICreditLedger
    {
        private readonly Guid _reservation = Guid.CreateVersion7();

        /// <summary>The id every reserve returns.</summary>
        public Guid Reservation => _reservation;

        /// <summary>How often the command reserved credits.</summary>
        public int ReserveCalls { get; private set; }

        /// <summary>The operations reserved, in call order.</summary>
        public List<CreditOperation> Operations { get; } = [];

        /// <inheritdoc />
        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReserveCalls++;
            Operations.Add(op);

            return ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(_reservation));
        }

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }
}
