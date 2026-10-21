using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories.Jobs;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Review;
using NightTales.Domain.Usage;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>QA (02.2.2-T3): duplicate delivery, unknown run and a hostile review through the real Worker graph and PostgreSQL.</summary>
[Trait("Category", "Integration")]
public sealed class GenerateStoryJobQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string StoryMarker = "QASTORYMARKERIBIS";

    private static readonly DateTimeOffset Start = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task QA_DuplicateJobDelivery_OneVersion_OneStoryGenerated_WriterCalledOnce()
    {
        var world = await SeedAsync();
        var writer = new Writer();
        var sink = new TestLogSink();
        await using var worker = await StartWorkerAsync(writer, reviewEnabled: false, sink);

        var runId = await GenerateAsync(world);
        await MeterAsync(world, runId);
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBeGreaterThan(0);
        await worker.Jobs.RunPendingAsync(Ct);
        worker.Jobs.Failures.ShouldBeEmpty();

        // At-least-once outbox: the same run id arrives again.
        worker.Scheduler.Enqueue<GenerateStoryJob>((job, token) => job.RunAsync(runId, token));
        await worker.Jobs.RunPendingAsync(Ct);
        worker.Jobs.Failures.ShouldBeEmpty();
        await worker.RunOutboxOnceAsync(Ct);

        writer.Calls.ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_generated.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("succeeded");
        sink.AllText().ShouldNotContain(StoryMarker);
    }

    [Fact]
    public async Task QA_JobForUnknownRunId_ReturnsWithoutFailure()
    {
        var world = await SeedAsync();
        await using var worker = await StartWorkerAsync(new Writer(), reviewEnabled: false, sink: null);

        var unknown = Guid.CreateVersion7();
        worker.Scheduler.Enqueue<GenerateStoryJob>((job, token) => job.RunAsync(unknown, token));
        await worker.Jobs.RunPendingAsync(Ct);

        worker.Jobs.Failures.ShouldBeEmpty();
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
    }

    [Fact]
    public async Task QA_HostileReviewText_IsCleaned_AndReadableFromJsonb()
    {
        var world = await SeedAsync();
        var writer = new Writer { ReviewEvidence = "ev\0il\uD800 x\u0001y" };
        await using var worker = await StartWorkerAsync(writer, reviewEnabled: true, sink: null);

        var runId = await GenerateAsync(world);
        await MeterAsync(world, runId);
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBeGreaterThan(0);
        await worker.Jobs.RunPendingAsync(Ct);
        worker.Jobs.Failures.ShouldBeEmpty();

        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("succeeded");
        var evidence = SqlProbe.ScalarText(
            Database,
            $"SELECT audit_report->'model_review'->'issues'->0->>'evidence' FROM story_versions WHERE episode_id = '{world.EpisodeId}'");
        evidence.Contains('\0').ShouldBeFalse();
        evidence.ShouldStartWith("ev");
        SqlProbe.ScalarText(
                Database,
                $"SELECT models->>'review_enabled' FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("true");
    }

    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: Topic.Ghost, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, brief: "Mot tap thu nghiem.", ct: Ct);

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }, new FakeClock(Start));
        if (!db.PriceVersions.Any(version => version.Version == 1))
        {
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
                "qa-price-version",
                Start.AddDays(-1)));
            await db.SaveChangesAsync(Ct);
        }

        return new World(user.Id, series.Id, episode.Id);
    }

    private async Task<Guid> GenerateAsync(World world)
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<IStoryCostEstimator>();
                services.AddSingleton<IStoryCostEstimator, FixedEstimator>();
            });
        using var client = factory.ClientFor(world.UserId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{world.EpisodeId}/story/generate");
        request.Headers.Add("Idempotency-Key", $"qa-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        return body.GetProperty("generationRunId").GetGuid();
    }

    private async Task MeterAsync(World world, Guid runId)
    {
        var reservation = Guid.Parse(SqlProbe.ScalarText(Database, $"SELECT reservation_id::text FROM generation_runs WHERE id = '{runId}'"));

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(Start));
        db.UsageRecords.Add(UsageRecord.Record(
            world.UserId,
            UsageOperation.Story,
            "openai",
            0.25m,
            Start,
            episodeId: world.EpisodeId,
            seriesId: world.SeriesId,
            generationRunId: runId,
            reservationId: reservation));
        await db.SaveChangesAsync(Ct);
    }

    private async Task<WorkerHost> StartWorkerAsync(Writer writer, bool reviewEnabled, TestLogSink? sink)
    {
        var host = new WorkerHost(Database, new FakeClock(Start))
            .WithSetting("Story:Review:Enabled", reviewEnabled ? "true" : "false")
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStoryWriter>();
                services.AddSingleton<IStoryWriter>(writer);
                services.RemoveAll<IContentModerator>();
                services.AddSingleton<IContentModerator>(new FakeContentModerator());
                services.RemoveAll<IPromptAssembler>();
                services.AddSingleton<IPromptAssembler, StubAssembler>();
                services.RemoveAll<IRealtimeNotifier>();
                services.AddSingleton<IRealtimeNotifier, NullNotifier>();
                if (sink is not null)
                {
                    services.AddSingleton<ILogEventSink>(sink);
                }
            });
        if (sink is not null)
        {
            host.WithTelemetry();
        }

        await host.StartAsync(Ct);

        return host;
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    private sealed class NullNotifier : IRealtimeNotifier
    {
        public Task NotifyAsync(Guid userId, RealtimeEvent realtimeEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Writer : IStoryWriter
    {
        public int Calls { get; private set; }

        public string ReviewEvidence { get; init; } = "evidence";

        public ValueTask<TextCallResult<StoryContentV1>> WriteAsync(StoryWriteRequest req, CancellationToken ct)
        {
            Calls++;

            return ValueTask.FromResult(TextCallResult<StoryContentV1>.Ok(Draft(), "gpt-test", null, "req-qa"));
        }

        public ValueTask<TextCallResult<StoryReviewV1>> ReviewAsync(StoryReviewRequest req, CancellationToken ct)
        {
            var review = new StoryReviewV1(
                new ReviewScores(4, 4, 4, 4, 4, null, 4),
                [new ReviewIssueV1("i1", "pacing_issue", "advisory", "p:0", ReviewEvidence, "impact", "repair")],
                [.. req.EarRuleQuestions.Select(question => new EarRuleAnswer(question.QuestionId, true, "meta", string.Empty))],
                null,
                [],
                "summary");

            return ValueTask.FromResult(TextCallResult<StoryReviewV1>.Ok(review, "gpt-review", null, "req-review"));
        }

        public ValueTask<TextCallResult<StoryContentV1>> ReviseAsync(StoryRevisionRequest req, CancellationToken ct) =>
            throw new InvalidOperationException("The revision stage is disabled in these tests.");
    }

    private static StoryContentV1 Draft() => new(
        $"Tieu de {StoryMarker}",
        new StorySegmentV1($"Mo dau {StoryMarker}.", ["k1"], []),
        [new StorySegmentV1($"Doan mot {StoryMarker}.", ["k2"], [])],
        new StorySegmentV1($"Ket {StoryMarker}.", ["k3"], []),
        "Mo ta.",
        ["tag"],
        [],
        new StoryStructureV1("a", "b", "c", "d", "e"));

    private sealed class StubAssembler : IPromptAssembler
    {
        public AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext ctx) => new("system", "{}", "pv-qa", "cache");

        public ErrorOr<AssembledPrompt> Review(StoryReviewRequest req) => new AssembledPrompt("system", "{}", "pv-review", "cache");

        public ErrorOr<AssembledPrompt> Revise(StoryRevisionRequest req) => new AssembledPrompt("system", "{}", "pv-revise", "cache");
    }

    private sealed class FixedEstimator : IStoryCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));

        public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WorstCaseAsync(tier, language, ct);

        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WorstCaseAsync(tier, language, ct);

        public Task<ErrorOr<UsageBound>> RevisionWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WorstCaseAsync(tier, language, ct);
    }
}
