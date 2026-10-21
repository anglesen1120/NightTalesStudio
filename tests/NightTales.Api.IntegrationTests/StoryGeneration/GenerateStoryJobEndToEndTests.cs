using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
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

/// <summary>
/// The whole 02.2.2 chain on a real PostgreSQL 17 (02.2.2-T3): <c>POST /story/generate</c> on the Api host writes the
/// run, the episode status and the outbox row; the Worker host delivers that row (the new outbox handler enqueues
/// <c>GenerateStoryJob</c>), the inline scheduler runs the job through the real <c>StoryGenerationPipeline</c> and
/// <c>StoryGenerationProgress</c>, and the follow-up outbox rows reach the realtime handlers. The provider edge is
/// scripted (writer, moderator, prompt assembler); everything else — persistence, the budget guard, the price
/// catalog, the progress writer, the outbox — is the production graph.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GenerateStoryJobEndToEndTests(TestDatabase database) : IntegrationTest(database)
{
    private const string BriefMarker = "BRIEFMARKERZEBRA";
    private const string StoryMarker = "STORYMARKERQUOKKA";

    private static readonly DateTimeOffset Start = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task GenerateStory_EndToEnd_HappyPath_SavesOneGeneratedVersionAndMovesEpisodeToReview()
    {
        var world = await SeedAsync($"{BriefMarker} một tập thử nghiệm.");
        var notifier = new RecordingNotifier();
        var sink = new TestLogSink();
        await using var worker = await StartWorkerAsync(new ScriptedWriter(), new FakeContentModerator(), notifier, sink);

        var runId = await GenerateAsync(world);
        await MeterAsync(world, runId);
        await DeliverAndRunAsync(worker);

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || stage || '/' || (cost_usd > 0)::text || '/' || counts_toward_limit::text "
                + $"FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("succeeded/done/true/true");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_review");
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
        SqlProbe.ScalarText(
                Database,
                $"SELECT source || '/' || generation_run_id::text FROM story_versions WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe($"generated/{runId}");
        SqlProbe.ScalarText(
                Database,
                $"SELECT current_story_version_id = (SELECT id FROM story_versions WHERE episode_id = '{world.EpisodeId}') "
                + $"FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("True");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_generated.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);

        // D19: no brief and no story text in any log line (ids, codes and counts only).
        sink.AllText().ShouldNotContain(BriefMarker);
        sink.AllText().ShouldNotContain(StoryMarker);
    }

    [Fact]
    public async Task Pipeline_StageChanges_EmitEpisodeUpdatedPerTransition()
    {
        var world = await SeedAsync("Một tập thử nghiệm.");
        var notifier = new RecordingNotifier();
        await using var worker = await StartWorkerAsync(new ScriptedWriter(), new FakeContentModerator(), notifier, sink: null);

        var runId = await GenerateAsync(world);
        await MeterAsync(world, runId);
        await DeliverAndRunAsync(worker);

        // Review is off: writing, checking, scoring, saving. `checking` is entered twice (validate, moderate) and the
        // episode drops the repeat, so exactly four transitions are written — then once more for StoryGenerated.
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_generation_stage_changed.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(4);

        var updates = notifier.Events.Where(item => item.Event.Name == RealtimeEventName.EpisodeUpdated).ToList();
        updates.Count.ShouldBe(5, "four stage transitions and the StoryGenerated outcome");
        updates.ShouldAllBe(item => item.UserId == world.UserId && item.Event.Ids.EpisodeId == world.EpisodeId);
        updates.ShouldAllBe(item => item.Event.Data == null, "ids only, never a payload");
    }

    [Fact]
    public async Task Pipeline_BriefFlagged_FailsGenerationAndLeavesNoBriefTextInLogsOrErrorDetail()
    {
        var world = await SeedAsync($"{BriefMarker} nội dung bị gắn cờ.");
        var moderator = new FakeContentModerator().EnqueueFlagged(ModerationStage.Brief, "violence");
        var notifier = new RecordingNotifier();
        var sink = new TestLogSink();
        var writer = new ScriptedWriter();
        await using var worker = await StartWorkerAsync(writer, moderator, notifier, sink);

        var runId = await GenerateAsync(world);
        await DeliverAndRunAsync(worker);

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || error_code || '/' || error_detail || '/' || counts_toward_limit::text "
                + $"FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("failed/story_moderated/brief_flagged/true");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
        writer.Calls.ShouldBe(0, "a flagged brief never reaches a paid call");

        // D19 / Acceptance: the brief reaches neither error_detail nor any log line.
        SqlProbe.ScalarText(Database, $"SELECT coalesce(error_detail, '') FROM generation_runs WHERE id = '{runId}'")
            .ShouldNotContain(BriefMarker);
        sink.AllText().ShouldNotContain(BriefMarker);
    }

    /// <summary>Seeds a user, a series and a <c>draft</c> episode with <paramref name="brief"/>.</summary>
    private async Task<World> SeedAsync(string brief)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: Topic.Ghost, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, brief: brief, ct: Ct);

        // The active price version at queued_at: the Worker's request factory refuses to build without one.
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
                "e2e-price-version",
                Start.AddDays(-1)));
            await db.SaveChangesAsync(Ct);
        }

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary><c>POST /story/generate</c> on the real Api host; returns the queued run's id.</summary>
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
        request.Headers.Add("Idempotency-Key", $"e2e-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        return body.GetProperty("generationRunId").GetGuid();
    }

    /// <summary>
    /// The scripted writer meters nothing (the real one meters inside <c>OpenAiStoryWriter</c>), so the run's cost line
    /// is one usage record against the run's reservation — what <c>CompleteAsync</c> sums into <c>cost_usd</c>.
    /// </summary>
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

    /// <summary>
    /// Delivers the request row (enqueues the job), runs the job, then delivers what the job wrote (stage events,
    /// <c>StoryGenerated</c> or <c>StoryGenerationFailed</c>) to the realtime handlers. No sleeps, no polling.
    /// </summary>
    private static async Task DeliverAndRunAsync(WorkerHost worker)
    {
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBeGreaterThan(0);
        worker.Jobs.Enqueued.ShouldContain(job => job.JobType == "GenerateStoryJob");

        await worker.Jobs.RunPendingAsync(Ct);
        worker.Jobs.Failures.ShouldBeEmpty();

        await worker.RunOutboxOnceAsync(Ct);
    }

    private async Task<WorkerHost> StartWorkerAsync(
        ScriptedWriter writer,
        FakeContentModerator moderator,
        RecordingNotifier notifier,
        TestLogSink? sink)
    {
        var host = new WorkerHost(Database, new FakeClock(Start))
            .WithSetting("Story:Review:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStoryWriter>();
                services.AddSingleton<IStoryWriter>(writer);
                services.RemoveAll<IContentModerator>();
                services.AddSingleton<IContentModerator>(moderator);
                services.RemoveAll<IPromptAssembler>();
                services.AddSingleton<IPromptAssembler, StubAssembler>();
                services.RemoveAll<IRealtimeNotifier>();
                services.AddSingleton<IRealtimeNotifier>(notifier);
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

    private sealed class RecordingNotifier : IRealtimeNotifier
    {
        private readonly List<(Guid UserId, RealtimeEvent Event)> _events = [];

        public IReadOnlyList<(Guid UserId, RealtimeEvent Event)> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public Task NotifyAsync(Guid userId, RealtimeEvent realtimeEvent, CancellationToken cancellationToken)
        {
            lock (_events)
            {
                _events.Add((userId, realtimeEvent));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedWriter : IStoryWriter
    {
        public int Calls { get; private set; }

        public ValueTask<TextCallResult<StoryContentV1>> WriteAsync(StoryWriteRequest req, CancellationToken ct)
        {
            Calls++;

            return ValueTask.FromResult(TextCallResult<StoryContentV1>.Ok(Draft(), "gpt-test", null, "req-e2e"));
        }

        public ValueTask<TextCallResult<StoryReviewV1>> ReviewAsync(StoryReviewRequest req, CancellationToken ct) =>
            throw new InvalidOperationException("The review stage is disabled in these tests.");

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
        public AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext ctx) => new("system", "{}", "pv-e2e", "cache");

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
