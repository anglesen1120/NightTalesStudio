using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Research;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Research;

/// <summary>
/// <c>POST /api/v1/episodes/{id}/fact-sheet/generate</c> against the real host and a real PostgreSQL 17
/// (02.5.3-T2, plan §4.1 U4.1/§5): the 202 contract, the run row (D2: <c>counts_toward_limit = false</c>), the
/// outbox row, idempotency, the tenant 404, the true-crime and state gates, the fetched-source gate and the
/// platform spend cap. The cost estimator is a fixed fake; the spend cap is the real service. The job itself,
/// its DI and its end-to-end path belong to 02.5.3-T2 slice D.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FactSheetGenerateEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task PostFactSheetGenerate_WithAFetchedSource_Returns202AndCommitsTheRunAndTheOutboxRow()
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
        body.GetProperty("factSheetId").ValueKind.ShouldBe(JsonValueKind.Null);
        var runId = body.GetProperty("runId").GetGuid();

        SqlProbe.ScalarText(
                Database,
                "SELECT kind || '/' || trigger || '/' || status || '/' || counts_toward_limit::text "
                + $"|| '/' || (reservation_id IS NOT NULL)::text FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("fact_sheet/user/queued/false/true");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("draft", "a fact sheet has no episode state of its own");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'fact_sheet_generation_requested.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);

        // The audit row (D13/D19) holds ids and numbers only.
        SqlProbe.ScalarText(
                Database,
                $"SELECT data->>'generationRunId' FROM audit_logs WHERE action = 'episode.fact_sheet_generation_requested' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(runId.ToString("D"));
    }

    [Fact]
    public async Task PostFactSheetGenerate_SameKeySameRequest_ReturnsTheSameBody_NoSecondRun()
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
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task PostFactSheetGenerate_SameKeyDifferentEpisode_Returns422IdempotencyKeyReused()
    {
        var world = await SeedAsync();
        var other = await SeedEpisodeAsync(world);
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
    public async Task PostFactSheetGenerate_WithoutKey_Returns400()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.PostAsync(
            $"/api/v1/episodes/{world.EpisodeId}/fact-sheet/generate",
            content: null,
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Code.ShouldBe("idempotency_key_required");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task PostFactSheetGenerate_Anonymous_Returns401()
    {
        using var factory = Factory();
        using var client = factory.AnonymousClient();

        using var response = await PostAsync(client, Guid.CreateVersion7(), "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostFactSheetGenerate_AnotherTenantsEpisode_Returns404()
    {
        var owner = await SeedAsync();
        var stranger = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(stranger.UserId);

        using var response = await PostAsync(client, owner.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{owner.EpisodeId}'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task PostFactSheetGenerate_NoFetchedSource_Returns409SourcesInsufficient()
    {
        var world = await SeedAsync(fetchedNote: false);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).Code.ShouldBe("fact_sheet_sources_insufficient");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task PostFactSheetGenerate_Twice_SecondReturns409FactSheetGenerationInProgress()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var first = await PostAsync(client, world.EpisodeId, "k1");
        using var second = await PostAsync(client, world.EpisodeId, "k2");

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync(Ct));
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict, await second.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(second)).Code.ShouldBe("fact_sheet_generation_in_progress");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task PostFactSheetGenerate_TwoConcurrentPosts_OneAcceptedOneConflicts_AndOneRunIsCommitted()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        var responses = await Task.WhenAll(
            PostAsync(client, world.EpisodeId, "k1"),
            PostAsync(client, world.EpisodeId, "k2"));
        using var first = responses[0];
        using var second = responses[1];

        new[] { first.StatusCode, second.StatusCode }.Count(code => code == HttpStatusCode.Accepted).ShouldBe(1);
        new[] { first.StatusCode, second.StatusCode }.Count(code => code == HttpStatusCode.Conflict).ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task PostFactSheetGenerate_GhostSeries_Returns409EpisodeInvalidState()
    {
        var world = await SeedAsync(topic: Topic.Ghost);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).Code.ShouldBe("episode_invalid_state");
    }

    [Fact]
    public async Task PostFactSheetGenerate_ApprovedEpisode_Returns409EpisodeInvalidState()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryApproved);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).Code.ShouldBe("episode_invalid_state");
    }

    [Fact]
    public async Task PostFactSheetGenerate_OverDailyCap_Returns429SpendCapReached()
    {
        var world = await SeedAsync();
        using var factory = Factory(dailyCap: "0.0001");
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe((HttpStatusCode)429, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("spend_cap_reached");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM idempotency_keys WHERE user_id = '{world.UserId}'")
            .ShouldBe(0, "a refused command rolls its idempotency claim back");
    }

    [Fact]
    public async Task PostFactSheetGenerate_RunCommittedAfterThePreCheck_Returns409FactSheetGenerationInProgress()
    {
        // CODE-10: the pre-check inside the command runs under the episode's row lock (FOR NO KEY UPDATE, which
        // does not exclude a foreign-key read), so a second writer can commit an active fact-sheet run for the
        // same episode between that check and this handler's insert. The insert then loses on
        // `ux_generation_runs_active_episode` (23505) and the unit of work must answer with the same 409 the
        // pre-check would have returned - the run row must never surface as a 500.
        var world = await SeedAsync();
        var competing = new CompetingRunInterceptor(Database, world);
        using var factory = Factory(configure: services =>
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(competing)));
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "k1");

        competing.RowsInserted.ShouldBe(1, "the competing run must claim the episode before this handler's insert");
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("fact_sheet_generation_in_progress");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1, "only the competing row survives; the refused insert rolled back");
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE type = 'fact_sheet_generation_requested.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(0, "the refused command commits no outbox row");
        SqlProbe.Count(Database, $"SELECT count(*) FROM idempotency_keys WHERE user_id = '{world.UserId}'")
            .ShouldBe(0, "a refused command rolls its idempotency claim back");

        // The competing row is this test's only trace, and an active run row left behind in a database another
        // test also queries can move that test's query plan: remove it again. The delete names the whole episode
        // as well as the id, so it cannot miss the row that was just asserted. (The refused insert left nothing.)
        SqlProbe.Execute(
            Database,
            $"DELETE FROM generation_runs WHERE id = '{competing.InsertedRunId}' "
            + $"OR episode_id = '{world.EpisodeId}'");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0);
    }

    private ApiFactory Factory(string dailyCap = "1000", Action<IServiceCollection>? configure = null) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", dailyCap)
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<IFactSheetCostEstimator>();
                services.AddSingleton<IFactSheetCostEstimator, FixedEstimator>();
                configure?.Invoke(services);
            });

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid episodeId, string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/episodes/{episodeId}/fact-sheet/generate");
        request.Headers.Add("Idempotency-Key", key);

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
        Topic topic = Topic.TrueCrime,
        bool fetchedNote = true)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        if (fetchedNote)
        {
            await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id });
            db.StorySources.Add(StorySource.CreateNote(
                Guid.CreateVersion7(),
                user.Id,
                episode.Id,
                "A true-crime note kept by the owner.",
                Start).Value);
            await db.SaveChangesAsync(Ct);
        }

        return new World(user.Id, series.Id, episode.Id);
    }

    private async Task<Guid> SeedEpisodeAsync(World world)
    {
        var actor = new TestCurrentUser { Id = world.UserId };

        return (await new EpisodeBuilder(Database, actor).CreateAsync(world.UserId, world.SeriesId, ct: Ct)).Id;
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    private sealed class FixedEstimator : IFactSheetCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));
    }

    /// <summary>
    /// The deterministic seam of the (episode, kind) race of CODE-10: when this handler's own run insert is being
    /// saved, the hook commits a competing active fact-sheet run for the same episode from a second connection -
    /// the state a request that shared the episode's row lock just before this one would have left behind. The
    /// partial unique index then refuses this handler's insert, and the unit of work has to map the 23505 to the
    /// same 409 the pre-check returns.
    /// </summary>
    private sealed class CompetingRunInterceptor(TestDatabase database, World world) : SaveChangesInterceptor
    {
        /// <summary>How many competing rows the hook wrote; exactly one per test.</summary>
        public int RowsInserted { get; private set; }

        /// <summary>The id of the competing row, so the test can take it away again.</summary>
        public Guid InsertedRunId { get; private set; }

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(eventData);

            var queuesItsRun = eventData.Context?.ChangeTracker
                .Entries<GenerationRun>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.EpisodeId == world.EpisodeId) == true;

            if (RowsInserted > 0 || !queuesItsRun)
            {
                return result;
            }

            RowsInserted++;

            await using var competing = database.CreateAppDbContext(
                new TestCurrentUser { Id = world.UserId },
                new FakeClock(Start));
            var run = GenerationRun.Queue(
                world.UserId,
                world.SeriesId,
                world.EpisodeId,
                GenerationKind.FactSheet,
                GenerationTrigger.User,
                idempotencyKey: null,
                previousEpisodeStatus: EpisodeStatus.Draft,
                inputFactSheetId: null,
                reservationId: Guid.CreateVersion7(),
                worstCaseUsd: 0.5m,
                Start,
                countsTowardLimit: false).Value;
            competing.GenerationRuns.Add(run);
            await competing.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            InsertedRunId = run.Id;

            return result;
        }
    }
}
