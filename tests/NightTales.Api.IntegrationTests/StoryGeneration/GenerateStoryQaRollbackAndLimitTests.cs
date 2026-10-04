using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Mediator;
using Microsoft.Extensions.Options;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Behaviors;
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
/// QA tests for <c>POST /api/v1/episodes/{id}/story/generate</c> (02.2.2-T2): the transaction rollback when a step
/// after the row lock fails, the per-episode and per-trigger scope of the FR-02-7 window, the pipeline order the
/// command is closed over and the paid-endpoint rate limit (plan §8).
/// </summary>
[Trait("Category", "Integration")]
public sealed class GenerateStoryQaRollbackAndLimitTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public void GenerateStoryCommand_IsClosedOverTheCanonicalPipelineInOrder()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();

        var resolved = scope.ServiceProvider
            .GetServices<IPipelineBehavior<GenerateStoryCommand, ErrorOr<GenerateStoryAccepted>>>()
            .Select(behavior => behavior.GetType())
            .ToArray();
        var expected = MediatorPipeline.Behaviors
            .Select(behavior => behavior.MakeGenericType(typeof(GenerateStoryCommand), typeof(ErrorOr<GenerateStoryAccepted>)))
            .ToArray();

        resolved.ShouldBe(expected, "Logging → Authorization → Validation → UnitOfWork → Idempotency → Audit");
    }

    [Fact]
    public async Task Generate_LedgerRefusesAfterBudgetCheck_RollsBackEverythingAndReleasesTheLock()
    {
        var world = await SeedAsync();
        var ledger = new FlakyLedger { Mode = FlakyMode.Refuse };
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);

        using var refused = await PostAsync(client, world.EpisodeId, "ledger-key");

        ((int)refused.StatusCode).ShouldBeGreaterThanOrEqualTo(400);
        AssertNothingCommitted(world);

        // The row lock and the idempotency claim died with the transaction: the same key now succeeds.
        ledger.Mode = FlakyMode.Allow;
        using var retried = await PostAsync(client, world.EpisodeId, "ledger-key");
        retried.StatusCode.ShouldBe(HttpStatusCode.Accepted, await retried.Content.ReadAsStringAsync(Ct));
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
    }

    [Fact]
    public async Task Generate_LedgerThrowsAfterBudgetCheck_Returns500AndRollsBackEverything()
    {
        var world = await SeedAsync();
        var ledger = new FlakyLedger { Mode = FlakyMode.Throw };
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);

        using var failed = await PostAsync(client, world.EpisodeId, "throw-key");

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        AssertNothingCommitted(world);

        ledger.Mode = FlakyMode.Allow;
        using var retried = await PostAsync(client, world.EpisodeId, "throw-key");
        retried.StatusCode.ShouldBe(HttpStatusCode.Accepted, await retried.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Generate_FiveCountedRunsOnAnotherEpisode_DoNotBlockThisOne()
    {
        // FR-02-7 is per episode (02.4.2-T1): the limit of episode A is not episode B's.
        var world = await SeedAsync();
        var other = await SeedEpisodeAsync(world.UserId, world.SeriesId);
        await SeedRunsAsync(world.UserId, world.SeriesId, other, 5, GenerationTrigger.User, ageHours: i => i);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "scope-key");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Generate_FiveAutopilotRuns_AreNotCountedAgainstTheUser()
    {
        var world = await SeedAsync();
        await SeedRunsAsync(world.UserId, world.SeriesId, world.EpisodeId, 5, GenerationTrigger.Autopilot, ageHours: i => i);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "autopilot-not-counted");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Generate_FiveCountedRunsOlderThanTheWindow_DoNotBlock()
    {
        // The window is computed in SQL with now(); a run queued two days ago is outside any 24 h window
        // regardless of the fake clock the host uses.
        var world = await SeedAsync();
        await SeedRunsAsync(world.UserId, world.SeriesId, world.EpisodeId, 5, GenerationTrigger.User, ageHours: i => 48 + i);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "old-runs");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Generate_LimitRefusal_ProblemBodyAndHeaderAgree()
    {
        var world = await SeedAsync();
        await SeedRunsAsync(world.UserId, world.SeriesId, world.EpisodeId, 5, GenerationTrigger.User, ageHours: i => 1 + i);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "limit-agree");

        response.StatusCode.ShouldBe((HttpStatusCode)429, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem!.Code.ShouldBe("story_generation_limit");
        problem.Status.ShouldBe(429);
        response.Headers.RetryAfter.ShouldNotBeNull();
        problem.Metadata!["retryAfterSeconds"].GetInt32().ShouldBe((int)response.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
        response.Headers.RetryAfter.Delta!.Value.TotalSeconds.ShouldBeGreaterThanOrEqualTo(1);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Generate_Replay_WritesNoSecondAuditRowNorSecondOutboxRow()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var first = await PostAsync(client, world.EpisodeId, "replay-audit");
        using var replay = await PostAsync(client, world.EpisodeId, "replay-audit");

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        replay.Headers.Location.ShouldBe(first.Headers.Location, "the replay answers the same Location");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.story_generation_requested' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(1, "Idempotency short-circuits before Audit");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_generation_requested.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Generate_ReplayAfterTheRunFinished_ReturnsStoredBodyAndCreatesNothing()
    {
        // Failure-handling row: "Idempotency replay after the run finished — the stored response is returned;
        // no new run, no second reservation".
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var first = await PostAsync(client, world.EpisodeId, "after-finish");
        var firstBody = await first.Content.ReadAsStringAsync(Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, firstBody);
        var runId = JsonDocument.Parse(firstBody).RootElement.GetProperty("generationRunId").GetGuid();
        SqlProbe.Execute(
            Database,
            $"UPDATE generation_runs SET status = 'cancelled', finished_at = now() WHERE id = '{runId}'; "
            + $"UPDATE episodes SET status = 'draft' WHERE id = '{world.EpisodeId}'");

        using var replay = await PostAsync(client, world.EpisodeId, "after-finish");

        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted, await replay.Content.ReadAsStringAsync(Ct));
        (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(firstBody);
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
    }

    [Fact]
    public async Task Generate_NonGuidEpisodeId_Returns404()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/episodes/not-a-guid/story/generate");
        request.Headers.Add("Idempotency-Key", "k1");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Generate_ThirtyFirstCallInAMinute_Returns429RateLimited()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        var statuses = new List<HttpStatusCode>();
        for (var index = 0; index < 31; index++)
        {
            using var response = await PostAsync(client, world.EpisodeId, $"burst-{index}");
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldContain((HttpStatusCode)429, "the paid policy allows 30 requests per minute per user");
    }

    [Fact]
    public async Task Generate_ArchivedSeries_Returns409SeriesArchived_AndCreatesNoRun()
    {
        // SEC-3: archiving a series must stop new paid runs for its episodes.
        var world = await SeedAsync();
        SqlProbe.Execute(Database, $"UPDATE series SET archived_at = now() WHERE id = '{world.SeriesId}'");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world.EpisodeId, "archived-key");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        problem.RootElement.GetProperty("code").GetString().ShouldBe("series_archived");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
    }

    [Fact]
    public async Task Generate_WhileASeriesWriterHoldsTheRow_WaitsForItBeforeReadingTheTopic()
    {
        // SEC-2: the generate call takes FOR SHARE on the series row before it reads Topic, so a concurrent
        // topic edit (UPDATE, exclusive) is either fully before it or fully after it - never in between.
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        await using var writer = new NpgsqlConnection(Database.ConnectionString);
        await writer.OpenAsync(Ct);
        await using var transaction = await writer.BeginTransactionAsync(Ct);
        await using (var update = new NpgsqlCommand(
            $"UPDATE series SET topic = 'true_crime' WHERE id = '{world.SeriesId}'",
            writer,
            transaction))
        {
            await update.ExecuteNonQueryAsync(Ct);
        }

        var pending = PostAsync(client, world.EpisodeId, "series-lock-key");
        var finishedEarly = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(2), Ct)) == pending;
        finishedEarly.ShouldBeFalse("the generate call must block on the series row the writer holds");

        await transaction.CommitAsync(Ct);

        using var response = await pending;
        // The topic it saw is the committed true_crime one: the fact-sheet guard is therefore consulted with it
        // (an unapproved sheet is a 409; it must never be an accepted run on a stale topic read).
        SqlProbe.ScalarText(Database, $"SELECT topic FROM series WHERE id = '{world.SeriesId}'").ShouldBe("true_crime");
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            SqlProbe.Count(
                    Database,
                    $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}' AND input_fact_sheet_id IS NOT NULL")
                .ShouldBe(1, "a true_crime run is accepted only with its approved fact sheet pinned");
        }
        else
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public void Budget_DisabledOutsideDevelopment_RefusesToStart()
    {
        // SEC-4: the kill-switch is Development-only (WebApplicationFactory hosts default to Development).
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder =>
            {
                builder.UseSetting("environment", "Production");
                builder.UseSetting("Budget:Enabled", "false");
            });

        Should.Throw<OptionsValidationException>(() => host.CreateClient())
            .Message.ShouldContain("Budget:Enabled");
    }

    private void AssertNothingCommitted(World world)
    {
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_generation_requested.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(0);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.story_generation_requested' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM idempotency_keys WHERE user_id = '{world.UserId}'").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
    }

    private ApiFactory Factory(ICreditLedger? ledger = null) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "1000000")
            .WithServices(services =>
            {
                services.RemoveAll<IStoryCostEstimator>();
                services.AddSingleton<IStoryCostEstimator, FixedEstimator>();
                if (ledger is not null)
                {
                    services.RemoveAll<ICreditLedger>();
                    services.AddSingleton(ledger);
                }
            });

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid episodeId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/story/generate");
        request.Headers.Add("Idempotency-Key", key);

        return client.SendAsync(request, Ct);
    }

    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    private async Task<Guid> SeedEpisodeAsync(Guid userId, Guid seriesId) =>
        (await new EpisodeBuilder(Database, new TestCurrentUser()).CreateAsync(userId, seriesId, ct: Ct)).Id;

    private async Task SeedRunsAsync(
        Guid userId,
        Guid seriesId,
        Guid episodeId,
        int count,
        GenerationTrigger trigger,
        Func<int, int> ageHours)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        for (var index = 0; index < count; index++)
        {
            var queuedAt = Start - TimeSpan.FromHours(ageHours(index)) - TimeSpan.FromMinutes(5);
            var run = GenerationRun.Queue(
                    userId,
                    seriesId,
                    episodeId,
                    GenerationKind.Story,
                    trigger,
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

    private enum FlakyMode
    {
        Allow,
        Refuse,
        Throw,
    }

    private sealed class FlakyLedger : ICreditLedger
    {
        public FlakyMode Mode { get; set; }

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct) =>
            Mode switch
            {
                FlakyMode.Throw => throw new InvalidOperationException("QA: ledger exploded after the budget check."),
                FlakyMode.Refuse => ValueTask.FromResult<ErrorOr<ReservationId>>(Error.Conflict("qa_ledger_refused", "QA refusal.")),
                _ => ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(Guid.CreateVersion7())),
            };

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }

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
