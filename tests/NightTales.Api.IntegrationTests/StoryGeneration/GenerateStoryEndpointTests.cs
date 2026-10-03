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
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.Http;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories.Commands.GenerateStory;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
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
/// <c>POST /api/v1/episodes/{id}/story/generate</c> against the real host and a real PostgreSQL 17 (02.2.2-T2, and the
/// four HTTP tests 02.4.2-T1 left open): the 202 contract, idempotency, the tenant 404, the FR-02-7 limit, the
/// autopilot path and the platform spend cap. The cost estimator is a fixed fake, so the tests do not depend on the
/// shipped prompts or the price versions; the spend cap and the limit are the real services.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GenerateStoryEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Generate_DraftEpisode_Returns202AndCommitsOutbox()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/episodes/{world.EpisodeId}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetString().ShouldBe("queued");
        body.GetProperty("stage").GetString().ShouldBe("queued");
        var runId = body.GetProperty("generationRunId").GetGuid();

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || stage || '/' || previous_episode_status || '/' || counts_toward_limit::text "
                + $"FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("queued/queued/draft/true");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_generating");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_generation_requested.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);

        // The audit row (D13/D19) holds ids and numbers only.
        SqlProbe.ScalarText(
                Database,
                $"SELECT data->>'generationRunId' || '|' || (data->>'trigger') || '|' || (data->>'tier') "
                + $"FROM audit_logs WHERE action = 'episode.story_generation_requested' AND subject_user_id = '{world.UserId}'")
            .ShouldBe($"{runId}|user|standard");
    }

    [Fact]
    public async Task Generate_SameKeySameRequest_ReturnsTheSameBody_NoSecondRun()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var first = await PostAsync(client, world.EpisodeId, "same-key");
        var firstBody = await first.Content.ReadAsStringAsync(Ct);
        using var replay = await PostAsync(client, world.EpisodeId, "same-key");
        var replayBody = await replay.Content.ReadAsStringAsync(Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, firstBody);
        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted, replayBody);
        replayBody.ShouldBe(firstBody);
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_generation_requested.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Generate_SameKeyDifferentEpisode_Returns422IdempotencyKeyReused()
    {
        var world = await SeedAsync();
        var other = await SeedEpisodeAsync(world.UserId, world.SeriesId);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var first = await PostAsync(client, world.EpisodeId, "reused");
        using var second = await PostAsync(client, other, "reused");

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadProblemAsync(second)).Code.ShouldBe("idempotency_key_reused");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{other}'").ShouldBe(0);
    }

    [Fact]
    public async Task Generate_WithoutKey_Returns400()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.PostAsync($"/api/v1/episodes/{world.EpisodeId}/story/generate", content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Code.ShouldBe("idempotency_key_required");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
    }

    [Fact]
    public async Task Generate_AnotherTenantsEpisode_Returns404()
    {
        var owner = await SeedAsync();
        var stranger = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(stranger.UserId);

        using var response = await PostAsync(client, owner.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{owner.EpisodeId}'").ShouldBe(0);
    }

    [Fact]
    public async Task Generate_ApprovedEpisode_Returns409EpisodeInvalidState()
    {
        var world = await SeedAsync(EpisodeStatus.StoryApproved);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).Code.ShouldBe("episode_invalid_state");
    }

    [Fact]
    public async Task Generate_ConcurrentTwoCalls_OnlyOneRun()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var clientA = factory.ClientFor(world.UserId);
        using var clientB = factory.ClientFor(world.UserId);

        var calls = await Task.WhenAll(
            PostAsync(clientA, world.EpisodeId, "race-a"),
            PostAsync(clientB, world.EpisodeId, "race-b"));
        using var _ = calls[0];
        using var __ = calls[1];

        calls.Count(call => call.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
        var loser = calls.Single(call => call.StatusCode != HttpStatusCode.Accepted);
        loser.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(loser)).Code.ShouldBe("story_generation_in_progress");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_generation_requested.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Generate_SixthIn24h_Returns429StoryGenerationLimitWithRetryAfter()
    {
        var world = await SeedAsync();
        await SeedCountedRunsAsync(world, 5);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "sixth");

        response.StatusCode.ShouldBe((HttpStatusCode)429, await response.Content.ReadAsStringAsync(Ct));
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("story_generation_limit");
        problem.Metadata!["retryAfterSeconds"].GetInt32().ShouldBe(4 * 3600);
        problem.Metadata["limit"].GetInt32().ShouldBe(5);
        response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromHours(4));
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}' AND status = 'queued'").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
    }

    [Fact]
    public async Task Generate_FifthIn24h_StillAccepted_SixthRejected()
    {
        var world = await SeedAsync();
        await SeedCountedRunsAsync(world, 4);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var fifth = await PostAsync(client, world.EpisodeId, "fifth");
        fifth.StatusCode.ShouldBe(HttpStatusCode.Accepted, await fifth.Content.ReadAsStringAsync(Ct));
        var runId = (await fifth.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("generationRunId").GetGuid();

        // The fifth run ends (cancelled keeps counting) and the episode is back in draft, so only the limit can refuse.
        await CancelRunAsync(world.UserId, runId);
        SqlProbe.Execute(Database, $"UPDATE episodes SET status = 'draft' WHERE id = '{world.EpisodeId}'");

        using var sixth = await PostAsync(client, world.EpisodeId, "sixth");

        sixth.StatusCode.ShouldBe((HttpStatusCode)429);
        (await ReadProblemAsync(sixth)).Code.ShouldBe("story_generation_limit");
    }

    [Fact]
    public async Task Generate_BySystemContextWithAutopilotTrigger_IsAcceptedWithoutLimit()
    {
        var world = await SeedAsync();
        await SeedCountedRunsAsync(world, 5);
        using var factory = Factory();

        ErrorOr<GenerateStoryAccepted> result;
        using (var scope = factory.Services.CreateScope())
        {
            // ISystemContext.For before any DbContext is resolved, exactly as the autopilot job runs it.
            scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            result = await sender.Send(
                new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.Autopilot, $"autopilot:{world.SeriesId}:{Start:O}:story"),
                Ct);
        }

        result.IsError.ShouldBeFalse(string.Join(";", result.ErrorsOrEmptyList.Select(error => error.Code)));
        SqlProbe.ScalarText(
                Database,
                $"SELECT trigger FROM generation_runs WHERE id = '{result.Value.GenerationRunId}'")
            .ShouldBe("autopilot");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_generating");
    }

    [Fact]
    public async Task Generate_ByUserWithAutopilotTrigger_Returns403()
    {
        var world = await SeedAsync();
        using var factory = Factory().MapEndpoints(routes => routes.MapPost(
            "/probe/generate-autopilot/{id:guid}",
            async (Guid id, ISender sender, HttpContext http, CancellationToken ct) =>
                (await sender.Send(new GenerateStoryCommand(id, GenerationTrigger.Autopilot, "probe-key"), ct))
                    .ToAcceptedResult(http)));
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.PostAsync($"/probe/generate-autopilot/{world.EpisodeId}", content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(response)).Code.ShouldBe("forbidden");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
    }

    [Fact]
    public async Task Generate_OverDailyCap_Returns429SpendCapReached()
    {
        var world = await SeedAsync();
        using var factory = Factory(dailyCap: "0.0001");
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe((HttpStatusCode)429, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("spend_cap_reached");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM idempotency_keys WHERE user_id = '{world.UserId}'")
            .ShouldBe(0, "a refused command rolls its idempotency claim back");
    }

    [Fact]
    public async Task Generate_Anonymous_Returns401()
    {
        using var factory = Factory();
        using var client = factory.AnonymousClient();

        using var response = await PostAsync(client, Guid.CreateVersion7(), "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private ApiFactory Factory(string dailyCap = "1000") =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", dailyCap)
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<IStoryCostEstimator>();
                services.AddSingleton<IStoryCostEstimator, FixedEstimator>();
            });

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid episodeId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/story/generate");
        request.Headers.Add("Idempotency-Key", key);

        return client.SendAsync(request, Ct);
    }

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem.ShouldNotBeNull();

        return problem;
    }

    private async Task<World> SeedAsync(EpisodeStatus status = EpisodeStatus.Draft)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    private async Task<Guid> SeedEpisodeAsync(Guid userId, Guid seriesId) =>
        (await new EpisodeBuilder(Database, new TestCurrentUser()).CreateAsync(userId, seriesId, ct: Ct)).Id;

    /// <summary>
    /// Seeds <paramref name="count"/> ended, counted user runs inside the window; the oldest was queued 20 h before the
    /// fake clock, so a refused call answers <c>Retry-After</c> 4 h.
    /// </summary>
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

    private async Task CancelRunAsync(Guid userId, Guid runId)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        var run = await db.GenerationRuns.SingleAsync(candidate => candidate.Id == runId, Ct);
        run.Cancel(Start);
        await db.SaveChangesAsync(Ct);
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    private sealed class FixedEstimator : IStoryCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));
    }
}
