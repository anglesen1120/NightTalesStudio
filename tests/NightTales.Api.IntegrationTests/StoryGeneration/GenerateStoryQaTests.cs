using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories.Commands.GenerateStory;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
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
/// QA boundary and negative tests for <c>POST /api/v1/episodes/{id}/story/generate</c>
/// (02.2.2-T2, independent of the implementer's <see cref="GenerateStoryEndpointTests"/>): the states the
/// state machine names, the idempotency edge cases the store's claim protocol promises, the platform cap
/// counted across users, the keys a refused command may reuse, and the no-content guarantee of the audit
/// row and the outbox payload (02 D13/D19, plan §8).
/// </summary>
[Trait("Category", "Integration")]
public sealed class GenerateStoryQaTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private const string BriefMarker = "MARKER-9f4e-QA-brief-never-leaks";

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Generate_MissingEpisode_Returns404NotFound()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, Guid.CreateVersion7(), "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");
    }

    [Fact]
    public async Task Generate_StoryReviewEpisode_Returns202WithPreviousStatusStoryReview()
    {
        var world = await SeedAsync(EpisodeStatus.StoryReview);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var runId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("generationRunId").GetGuid();
        SqlProbe.ScalarText(
                Database,
                $"SELECT previous_episode_status FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("story_review");
    }

    [Fact]
    public async Task Generate_GeneratingEpisode_Returns409StoryGenerationInProgress()
    {
        var world = await SeedAsync(EpisodeStatus.StoryGenerating);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).Code.ShouldBe("story_generation_in_progress");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_generating");
    }

    [Fact]
    public async Task Generate_GeneratingEpisodeAtLimit_Returns409BeforeLimitCheck()
    {
        // Step order: the state/active-run checks (3/6) precede the rolling limit (5), so an episode
        // already generating answers the conflict, not the limit, even when the limit is spent.
        var world = await SeedAsync(EpisodeStatus.StoryGenerating);
        await SeedCountedRunsAsync(world, 5);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).Code.ShouldBe("story_generation_in_progress");
    }

    [Fact]
    public async Task Generate_TrueCrimeWithoutApprovedSheet_Returns409FactSheetRequired()
    {
        // The registered default is NullFactSheetGuard (02 CR-24): until 02.5.5-T1 lands it fails
        // closed, so a true-crime episode refuses with fact_sheet_required.
        var world = await SeedAsync(topic: Topic.TrueCrime);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("fact_sheet_required");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
        SqlProbe.Count(Database, $"SELECT count(*) FROM idempotency_keys WHERE user_id = '{world.UserId}'")
            .ShouldBe(0, "a refused command rolls its idempotency claim back");
    }

    [Fact]
    public async Task Generate_ConcurrentSameKey_TwinsReplayOneStoredBody()
    {
        // The claim protocol (09.1.3): the twin's insert waits on the uncommitted row, then reads the
        // stored response — both callers get the same body, and only one run ever existed.
        var world = await SeedAsync();
        using var factory = Factory();
        using var clientA = factory.ClientFor(world.UserId);
        using var clientB = factory.ClientFor(world.UserId);

        var calls = await Task.WhenAll(
            PostAsync(clientA, world.EpisodeId, "twin-key"),
            PostAsync(clientB, world.EpisodeId, "twin-key"));
        using var _ = calls[0];
        using var __ = calls[1];

        foreach (var call in calls)
        {
            call.StatusCode.ShouldBe(HttpStatusCode.Accepted, await call.Content.ReadAsStringAsync(Ct));
        }

        var bodies = await Task.WhenAll(calls.Select(call => call.Content.ReadAsStringAsync(Ct)));
        bodies[1].ShouldBe(bodies[0], "the twin replays the stored response verbatim");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_generation_requested.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Generate_KeyOfARefusedRequest_IsReusableAfterTheBlockLifts()
    {
        // An error answer stores nothing and rolls the claim back, so the same key may be resubmitted.
        var world = await SeedAsync(EpisodeStatus.StoryApproved);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var refused = await PostAsync(client, world.EpisodeId, "retry-key");
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        SqlProbe.Execute(Database, $"UPDATE episodes SET status = 'draft' WHERE id = '{world.EpisodeId}'");

        using var retried = await PostAsync(client, world.EpisodeId, "retry-key");
        retried.StatusCode.ShouldBe(HttpStatusCode.Accepted, await retried.Content.ReadAsStringAsync(Ct));
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
    }

    [Fact]
    public async Task Generate_SameKeyAcrossUsers_AreIndependentKeySpaces()
    {
        // idempotency_keys is unique on (user_id, key): another user's key space does not collide.
        var first = await SeedAsync();
        var second = await SeedAsync();
        using var factory = Factory();
        using var clientA = factory.ClientFor(first.UserId);
        using var clientB = factory.ClientFor(second.UserId);

        using var a = await PostAsync(clientA, first.EpisodeId, "shared-key");
        using var b = await PostAsync(clientB, second.EpisodeId, "shared-key");

        a.StatusCode.ShouldBe(HttpStatusCode.Accepted, await a.Content.ReadAsStringAsync(Ct));
        b.StatusCode.ShouldBe(HttpStatusCode.Accepted, await b.Content.ReadAsStringAsync(Ct));
        var runA = (await a.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("generationRunId").GetGuid();
        var runB = (await b.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("generationRunId").GetGuid();
        runB.ShouldNotBe(runA);
    }

    [Theory]
    [InlineData("")] // empty
    [InlineData("key with space")] // 0x20 is below the printable range
    [InlineData("khóa")] // non-ASCII
    public async Task Generate_MalformedKey_Returns400IdempotencyKeyRequired(string key)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, key);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("idempotency_key_required");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
    }

    [Fact]
    public async Task Generate_KeyOver128Chars_Returns400()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, new string('k', 129));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Code.ShouldBe("idempotency_key_required");
    }

    [Fact]
    public async Task Generate_KeyOfExactly128Chars_IsAccepted()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, new string('k', 128));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Generate_RequestBody_IsIgnored_TriggerAlwaysUser()
    {
        // The route takes no body (plan §4 U1): a forged {"trigger":"autopilot"} changes nothing and
        // the run records trigger user (plan §8).
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(
            client,
            world.EpisodeId,
            "body-key",
            JsonContent.Create(new { trigger = "autopilot" }));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var runId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("generationRunId").GetGuid();
        SqlProbe.ScalarText(Database, $"SELECT trigger FROM generation_runs WHERE id = '{runId}'").ShouldBe("user");
    }

    [Fact]
    public async Task Generate_OpenBoundsOfAnotherUser_CountTowardTheDailyCap()
    {
        // The cap is platform-wide: user A's queued run is an open bound on user B's check, so B's
        // 0.5 on top of A's 0.5 breaches a 0.9 cap (§4 U2, T61).
        var first = await SeedAsync();
        var second = await SeedAsync();
        using var factory = Factory(dailyCap: CapAbovePlatformBaseline(0.9m));
        using var clientA = factory.ClientFor(first.UserId);
        using var clientB = factory.ClientFor(second.UserId);

        using var a = await PostAsync(clientA, first.EpisodeId, "cap-a");
        a.StatusCode.ShouldBe(HttpStatusCode.Accepted, await a.Content.ReadAsStringAsync(Ct));

        using var b = await PostAsync(clientB, second.EpisodeId, "cap-b");

        b.StatusCode.ShouldBe((HttpStatusCode)429, await b.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(b)).Code.ShouldBe("spend_cap_reached");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{second.EpisodeId}'").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{second.EpisodeId}'").ShouldBe("draft");
    }

    [Fact]
    public async Task Generate_ExactlyAtDailyCap_IsAllowed_ThenNextIsRefused()
    {
        // The rule denies only when the total is greater than the cap: 0.5 + 0.5 = 1.0 stays open.
        var world = await SeedAsync();
        var second = await SeedEpisodeAsync(world.UserId, world.SeriesId);
        var third = await SeedEpisodeAsync(world.UserId, world.SeriesId);
        using var factory = Factory(dailyCap: CapAbovePlatformBaseline(1.0m));
        using var client = factory.ClientFor(world.UserId);

        using var first = await PostAsync(client, world.EpisodeId, "edge-1");
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync(Ct));

        using var at = await PostAsync(client, second, "edge-2");
        at.StatusCode.ShouldBe(HttpStatusCode.Accepted, await at.Content.ReadAsStringAsync(Ct));

        using var over = await PostAsync(client, third, "edge-3");
        over.StatusCode.ShouldBe((HttpStatusCode)429, await over.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(over)).Code.ShouldBe("spend_cap_reached");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{third}'").ShouldBe(0);
    }

    /// <summary>
    /// The shared class database accumulates open worst-case bounds and spend from earlier tests, and the guard sums
    /// the whole platform, so a cap under test is expressed as "headroom above what is already there" (the same
    /// approach as the implementer's <c>UsageBudgetGuardTests</c>).
    /// </summary>
    private string CapAbovePlatformBaseline(decimal headroom)
    {
        var open = SqlProbe.ScalarText(
            Database,
            "SELECT COALESCE(SUM(worst_case_usd), 0)::text FROM generation_runs WHERE status IN ('queued','running')");
        var spent = SqlProbe.ScalarText(
            Database,
            $"SELECT COALESCE(SUM(cost_usd), 0)::text FROM usage_records WHERE created_at >= '{Start.UtcDateTime.Date:yyyy-MM-dd}T00:00:00Z'");

        return (decimal.Parse(open, System.Globalization.CultureInfo.InvariantCulture)
                + decimal.Parse(spent, System.Globalization.CultureInfo.InvariantCulture)
                + headroom)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Generate_OverMonthlyCap_Returns429SpendCapReached()
    {
        var world = await SeedAsync();
        using var factory = Factory(monthlyCap: "0.4");
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe((HttpStatusCode)429, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("spend_cap_reached");
    }

    [Fact]
    public async Task Generate_SpendCapRefusal_LeavesNoRunNoOutboxNoAudit()
    {
        // The whole refusal rolls back in one transaction: no run, no outbox row, no audit row, no key.
        var world = await SeedAsync();
        using var factory = Factory(dailyCap: "0.0001");
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");
        response.StatusCode.ShouldBe((HttpStatusCode)429);

        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_generation_requested.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(0, "the outbox row commits only with the command");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.story_generation_requested' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(0, "a refused command audits nothing");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
    }

    [Fact]
    public async Task Generate_OutboxPayload_IsIdsOnly()
    {
        var world = await SeedAsync(brief: BriefMarker);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var runId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("generationRunId").GetGuid();

        var payload = SqlProbe.ScalarText(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = 'story_generation_requested.v1' AND user_id = '{world.UserId}'");
        payload.ShouldContain(world.EpisodeId.ToString());
        payload.ShouldContain(runId.ToString());
        payload.ShouldNotContain(BriefMarker, Case.Sensitive);
        var keys = SqlProbe.ScalarText(
            Database,
            "SELECT string_agg(k, ',' ORDER BY k) FROM jsonb_object_keys(("
            + $"SELECT payload::jsonb FROM outbox_messages WHERE type = 'story_generation_requested.v1' AND user_id = '{world.UserId}')) AS k");
        keys.ShouldBe("episodeId,generationRunId,occurredAt,seriesId,trigger,userId");
    }

    [Fact]
    public async Task Generate_AuditRow_HoldsIdsAndNumbersOnly_NoBrief()
    {
        var world = await SeedAsync(brief: BriefMarker);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));

        var keys = SqlProbe.ScalarText(
            Database,
            "SELECT string_agg(k, ',' ORDER BY k) FROM jsonb_object_keys(("
            + $"SELECT data FROM audit_logs WHERE action = 'episode.story_generation_requested' AND subject_user_id = '{world.UserId}')) AS k");
        keys.ShouldBe("episodeId,generationRunId,tier,trigger,worstCaseUsd");
        var data = SqlProbe.ScalarText(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = 'episode.story_generation_requested' AND subject_user_id = '{world.UserId}'");
        data.ShouldNotContain(BriefMarker, Case.Sensitive);
    }

    [Fact]
    public async Task Generate_UserTriggerSystem_Returns403()
    {
        // Step 2 refuses every non-user trigger outside a system scope — System like Autopilot.
        var world = await SeedAsync();
        using var factory = Factory().MapEndpoints(routes => routes.MapPost(
            "/probe/generate-system/{id:guid}",
            async (Guid id, ISender sender, HttpContext http, CancellationToken ct) =>
                (await sender.Send(new GenerateStoryCommand(id, GenerationTrigger.System, "probe-key"), ct))
                    .ToAcceptedResult(http)));
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.PostAsync($"/probe/generate-system/{world.EpisodeId}", content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(response)).Code.ShouldBe("forbidden");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
    }

    private ApiFactory Factory(string dailyCap = "1000", string monthlyCap = "100000") =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", dailyCap)
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", monthlyCap)
            .WithServices(services =>
            {
                services.RemoveAll<IStoryCostEstimator>();
                services.AddSingleton<IStoryCostEstimator, FixedEstimator>();
            });

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        Guid episodeId,
        string key,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/story/generate")
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);

        return client.SendAsync(request, Ct);
    }

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem.ShouldNotBeNull();

        return problem;
    }

    private async Task<World> SeedAsync(
        EpisodeStatus status = EpisodeStatus.Draft,
        Topic topic = Topic.Ghost,
        string? brief = null)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(
            user.Id,
            series.Id,
            brief: brief ?? EpisodeBuilder.DefaultBrief,
            status: status,
            ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    private async Task<Guid> SeedEpisodeAsync(Guid userId, Guid seriesId) =>
        (await new EpisodeBuilder(Database, new TestCurrentUser()).CreateAsync(userId, seriesId, ct: Ct)).Id;

    /// <summary>Seeds <paramref name="count"/> ended, counted user runs inside the rolling window.</summary>
    private async Task SeedCountedRunsAsync(World world, int count)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        for (var index = 0; index < count; index++)
        {
            var queuedAt = index == 0 ? Start - TimeSpan.FromHours(20) : Start - TimeSpan.FromHours(index);
            var run = GenerationRun.Queue(
                    world.UserId,
                    world.SeriesId,
                    world.EpisodeId,
                    GenerationKind.Story,
                    GenerationTrigger.User,
                    null,
                    EpisodeStatus.Draft,
                    null,
                    null,
                    0m,
                    queuedAt)
                .Value;
            run.Cancel(queuedAt);
            db.GenerationRuns.Add(run);
        }

        await db.SaveChangesAsync(Ct);
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    private sealed class FixedEstimator : IStoryCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));

        public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WriterWorstCaseAsync(tier, language, ct);

        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));
    }
}
