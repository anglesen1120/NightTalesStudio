using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories.Review;
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
using NightTales.TestKit.Text;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Stories;

/// <summary>
/// 02.6.2-T1 step 5 (02 README D21) through the real Worker graph and PostgreSQL 17: the pipeline decides
/// with <c>RevisionPolicy</c>, makes exactly one <c>ReviseAsync</c> call, re-runs steps 2–4 once on the
/// revision (review #2, hints only), drift-checks the result and saves a <c>revised</c> row beside the
/// <c>generated</c> one — metered as <c>story_revision</c> + a second <c>story_review</c>. The provider
/// edge is the real <c>OpenAiStoryWriter</c> over a scripted <see cref="FakeTextClient"/> (so metering,
/// the D1 bounds and the D5 prompt assembly are the production ones); the moderator and the cost
/// estimator are scripted. Nothing but the scripted provider edge is replaced.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RevisionStepTests(TestDatabase database) : IntegrationTest(database)
{
    private const string StoryMarker = "STORYMARKERREVISIONFENNEC";

    private static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task GenerateStory_BlockingObjective_RevisesExactlyOnceAndSavesRevisedRow()
    {
        var world = await SeedAsync();
        var sink = new TestLogSink();
        var text = Script(
            WriterOk(),
            ReviewOk(BlockingHook()),
            RevisionOk(RevisedDraft()),
            ReviewOk());

        await using var worker = await StartWorkerAsync(text, sink: sink);
        var runId = await GenerateAsync(world);
        await DeliverAndRunAsync(worker);

        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("succeeded");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_review");
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{world.EpisodeId}'").ShouldBe(2);

        // SQL-level: the generated row comes first with a null parent, the revised row descends from it.
        var generatedId = IdOfVersion(world.EpisodeId, 1);
        VersionRows(world.EpisodeId).ShouldBe($"generated|null,revised|{generatedId}");
        SqlProbe.ScalarText(
                Database,
                $"SELECT coalesce(parent_version_id::text, 'null') FROM story_versions WHERE episode_id = '{world.EpisodeId}' AND version = 2")
            .ShouldBe(generatedId);

        // GET /episodes/{id}: the revised row is the current version.
        using var detail = await ReadEpisodeAsync(world);
        detail.RootElement.GetProperty("currentStory").GetProperty("versionId").GetString()
            .ShouldBe(IdOfVersion(world.EpisodeId, 2));

        // The audit records the applied revision and the issue it was asked to fix.
        RevisionValue(world.EpisodeId, "->>'applied'").ShouldBe("true");
        RevisionValue(world.EpisodeId, "->>'parent_version_id'").ShouldBe(generatedId);
        SqlProbe.ScalarText(
                Database,
                "SELECT (audit_report->'revision'->'fixed_issue_ids' ? 'i1')::text FROM story_versions "
                + $"WHERE episode_id = '{world.EpisodeId}' AND source = 'revised'")
            .ShouldBe("True");
        SqlProbe.ScalarText(
                Database,
                "SELECT (SELECT count(*) FROM jsonb_object_keys(audit_report->'revision'->'review_after'->'scores'))::text "
                + $"FROM story_versions WHERE episode_id = '{world.EpisodeId}' AND source = 'revised'")
            .ShouldBe("7", "review #2 is merged as review_after with the seven D16 scores");

        // Exactly one revision call and two review calls (review #1 and review #2).
        RevisionCalls(text).ShouldBe(1);
        ReviewCalls(text).ShouldBe(2);

        // D19: no story text in any log line.
        sink.AllText().ShouldNotContain(StoryMarker);
    }

    [Fact]
    public async Task GenerateStory_TasteOnly_SavesGeneratedRowWithHints()
    {
        var world = await SeedAsync();
        var text = Script(WriterOk(), ReviewOk(TastePacing()));

        await using var worker = await StartWorkerAsync(text);
        var runId = await GenerateAsync(world);
        await DeliverAndRunAsync(worker);

        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("succeeded");
        VersionRows(world.EpisodeId).ShouldBe("generated|null", "a taste-only review never revises");
        RevisionCalls(text).ShouldBe(0);
        ReviewCalls(text).ShouldBe(1, "review #2 runs only after a revision");

        // The taste hint stays in model_review; no revision block is written.
        SqlProbe.ScalarText(
                Database,
                $"SELECT (audit_report ? 'revision')::text FROM story_versions WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe("False");
        SqlProbe.ScalarText(
                Database,
                $"SELECT audit_report->'model_review'->'issues'->0->>'id' FROM story_versions WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe("i2");
    }

    [Fact]
    public async Task GenerateStory_SecondReviewStillBlocking_SavesAndStops()
    {
        var world = await SeedAsync();
        var text = Script(
            WriterOk(),
            ReviewOk(BlockingHook()),
            RevisionOk(RevisedDraft()),
            ReviewOk(BlockingEnding()));

        await using var worker = await StartWorkerAsync(text);
        var runId = await GenerateAsync(world);
        await DeliverAndRunAsync(worker);

        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("succeeded");
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{world.EpisodeId}'").ShouldBe(2);
        RevisionCalls(text).ShouldBe(1, "D21: a single pass, review #2 never triggers a second revision");
        ReviewCalls(text).ShouldBe(2);

        // The still-blocking review #2 hint is carried into review_after.issues, advisory to the run.
        SqlProbe.ScalarText(
                Database,
                "SELECT (SELECT count(*) FROM jsonb_array_elements(audit_report->'revision'->'review_after'->'issues') issue "
                + "WHERE issue->>'id' = 'i3')::text FROM story_versions "
                + $"WHERE episode_id = '{world.EpisodeId}' AND source = 'revised'")
            .ShouldBe("1", "the review #2 hint is kept in review_after.issues");
    }

    [Fact]
    public async Task GenerateStory_RevisionInvalidOutput_SavesOriginalWithAppliedFalse()
    {
        var world = await SeedAsync();
        var text = Script(
            WriterOk(),
            ReviewOk(BlockingHook()),
            RevisionInvalid());

        await using var worker = await StartWorkerAsync(text);
        var runId = await GenerateAsync(world);
        await DeliverAndRunAsync(worker);

        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("succeeded");
        VersionRows(world.EpisodeId).ShouldBe("generated|null", "the original draft becomes current");
        RevisionValue(world.EpisodeId, "->>'applied'").ShouldBe("false");
        RevisionValue(world.EpisodeId, "->>'reason'").ShouldBe("revision_invalid_output");
        RevisionCalls(text).ShouldBe(1, "Goal: exactly one revision call, never retried");
        ReviewCalls(text).ShouldBe(1, "review #2 never runs when the revision was not applied");
    }

    [Fact]
    public async Task GenerateStory_RevisionModerated_FailsRunAndNoVersionRow()
    {
        var world = await SeedAsync();
        var text = Script(
            WriterOk(),
            ReviewOk(BlockingHook()),
            RevisionModerated("violence"));

        await using var worker = await StartWorkerAsync(text);
        var runId = await GenerateAsync(world);
        await DeliverAndRunAsync(worker);

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || error_code FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("failed/story_moderated");
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM moderation_events WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBeGreaterThan(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
        RevisionCalls(text).ShouldBe(1);
    }

    [Fact]
    public async Task GenerateStory_DriftDetected_AuditAdvisoryOnly()
    {
        var world = await SeedAsync();
        var text = Script(
            WriterOk(),
            ReviewOk(BlockingHook()),
            RevisionOk(DriftedDraft()),
            ReviewOk());

        await using var worker = await StartWorkerAsync(text);
        var runId = await GenerateAsync(world);
        await DeliverAndRunAsync(worker);

        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("succeeded");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("story_review", "drift is advisory and never blocks the episode");

        using var audit = JsonDocument.Parse(
            SqlProbe.ScalarText(
                Database,
                "SELECT (audit_report->'revision'->'review_after'->'issues')::text FROM story_versions "
                + $"WHERE episode_id = '{world.EpisodeId}' AND source = 'revised'"));
        var drift = audit.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == AuditReportBuilder.RevisionDriftType);
        drift.GetProperty("severity").GetString().ShouldBe("advisory");
        drift.GetProperty("kind").GetString().ShouldBe("objective");
        SqlProbe.ScalarText(
                Database,
                $"SELECT (audit_report->'revision'->'drift'->'changed_segments' ? 'p:0')::text FROM story_versions WHERE episode_id = '{world.EpisodeId}' AND source = 'revised'")
            .ShouldBe("True", "the untouched paragraph the revision rewrote is reported");
        SqlProbe.ScalarText(
                Database,
                $"SELECT (audit_report->'revision'->'drift'->>'segment_count_changed') FROM story_versions WHERE episode_id = '{world.EpisodeId}' AND source = 'revised'")
            .ShouldBe("False");
    }

    [Fact]
    public async Task GenerateStory_RevisionAndReview2_MeteredAndNotCountedTowardLimit()
    {
        var world = await SeedAsync();
        var text = Script(
            WriterOk(),
            ReviewOk(BlockingHook()),
            RevisionOk(RevisedDraft()),
            ReviewOk());

        await using var worker = await StartWorkerAsync(text);
        var runId = await GenerateAsync(world);
        var reservation = Guid.Parse(
            SqlProbe.ScalarText(Database, $"SELECT reservation_id::text FROM generation_runs WHERE id = '{runId}'"));
        await DeliverAndRunAsync(worker);

        SqlProbe.ScalarText(
                Database,
                "SELECT string_agg(operation || '=' || calls, ',' ORDER BY operation) FROM "
                + $"(SELECT operation, count(*) AS calls FROM usage_records WHERE generation_run_id = '{runId}' GROUP BY operation) usage")
            .ShouldBe("story=1,story_review=2,story_revision=1");

        SqlProbe.ScalarText(
                Database,
                $"SELECT bool_and(reservation_id = '{reservation}' AND price_version = 1)::text FROM usage_records WHERE generation_run_id = '{runId}'")
            .ShouldBe("True", "both extra calls carry the run's reservation id and the run's price version");

        SqlProbe.ScalarText(
                Database,
                "SELECT (cost_usd > 0 AND cost_usd = (SELECT sum(cost_usd) FROM usage_records "
                + $"WHERE generation_run_id = '{runId}'))::text FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("True", "generation_runs.cost_usd includes the review #2 and the revision");

        SqlProbe.ScalarText(Database, $"SELECT counts_toward_limit FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("True", "D2: review and revision never change the counting flag");
    }

    [Fact]
    public async Task GenerateStory_RevisionPromptCarriesOnlyBlockingObjectiveIssues()
    {
        var world = await SeedAsync();
        var text = Script(
            WriterOk(),
            ReviewOk(BlockingHook(), TastePacing()),
            RevisionOk(RevisedDraft()),
            ReviewOk());

        await using var worker = await StartWorkerAsync(text);
        var runId = await GenerateAsync(world);
        await DeliverAndRunAsync(worker);

        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("succeeded");

        var request = text.Requests.Where(call => call.Step == TextStep.Revision).ShouldHaveSingleItem();
        using var data = JsonDocument.Parse(request.UserDataJson);
        data.RootElement.GetProperty("task").GetString().ShouldBe("revise_story");
        var issues = data.RootElement.GetProperty("issues").EnumerateArray().ToArray();
        issues.Select(issue => issue.GetProperty("id").GetString()).ShouldBe(["i1"], "only the blocking objective issue is sent");
        issues.ShouldAllBe(issue => issue.GetProperty("type").GetString() == "hook_missing");
        data.RootElement.GetProperty("issues").EnumerateArray().ShouldNotContain(issue => issue.GetProperty("id").GetString() == "i2");
    }

    // ---- seed / host / flow helpers ----------------------------------------------------------------

    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: Topic.Ghost, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, brief: "Một tập thử nghiệm.", ct: Ct);

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
                "revision-price-version",
                Start.AddDays(-1)));
            await db.SaveChangesAsync(Ct);
        }

        return new World(user.Id, series.Id, episode.Id);
    }

    private async Task<WorkerHost> StartWorkerAsync(FakeTextClient text, TestLogSink? sink = null)
    {
        var host = new WorkerHost(Database, new FakeClock(Start))
            .WithSetting("Story:Review:Enabled", "true")
            .WithSetting("Story:Revision:Enabled", "true")
            .ConfigureServices(services =>
            {
                services.RemoveAll<ITextClient>();
                services.AddSingleton<ITextClient>(text);
                services.RemoveAll<IContentModerator>();
                services.AddSingleton<IContentModerator>(new FakeContentModerator());
                services.RemoveAll<IRealtimeNotifier>();
                services.AddSingleton<IRealtimeNotifier, NullNotifier>();
                services.RemoveAll<IStoryCostEstimator>();
                services.AddSingleton<IStoryCostEstimator, FixedEstimator>();
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
        request.Headers.Add("Idempotency-Key", $"revision-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        return body.GetProperty("generationRunId").GetGuid();
    }

    private static async Task DeliverAndRunAsync(WorkerHost worker)
    {
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBeGreaterThan(0);
        worker.Jobs.Enqueued.ShouldContain(job => job.JobType == "GenerateStoryJob");
        await worker.Jobs.RunPendingAsync(Ct);
        worker.Jobs.Failures.ShouldBeEmpty();
        await worker.RunOutboxOnceAsync(Ct);
    }

    private async Task<JsonDocument> ReadEpisodeAsync(World world)
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync($"/api/v1/episodes/{world.EpisodeId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    // ---- scripting and SQL probes ------------------------------------------------------------------

    private static FakeTextClient Script(params Scripted[] outcomes)
    {
        var text = new FakeTextClient();
        foreach (var (step, outcome) in outcomes)
        {
            switch (outcome)
            {
                case TextCallResult<StoryContentV1> draft:
                    text.Enqueue(step, draft);
                    break;
                case TextCallResult<StoryReviewV1> review:
                    text.Enqueue(step, review);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported scripted outcome {outcome.GetType().Name}.");
            }
        }

        return text;
    }

    private static Scripted WriterOk() =>
        new(TextStep.Writer, TextCallResult<StoryContentV1>.Ok(Draft(), FakeTextClient.Model, null, "req-writer"));

    private static Scripted RevisionOk(StoryContentV1 revised) =>
        new(TextStep.Revision, TextCallResult<StoryContentV1>.Ok(revised, FakeTextClient.Model, null, "req-revision"));

    private static Scripted RevisionInvalid() =>
        new(TextStep.Revision, TextCallResult<StoryContentV1>.InvalidOutput("gpt-6-luna", null, null, ["schema_invalid"]));

    private static Scripted RevisionModerated(params string[] categories) =>
        new(TextStep.Revision, TextCallResult<StoryContentV1>.Moderated("gpt-6-luna", null, null, categories));

    private static Scripted ReviewOk(params ReviewIssueV1[] issues) =>
        new(TextStep.Review, TextCallResult<StoryReviewV1>.Ok(Review(issues), FakeTextClient.Model, null, "req-review"));

    /// <summary>One scripted provider outcome, tagged with the step it answers.</summary>
    private readonly record struct Scripted(TextStep Step, object Outcome);

    private static int RevisionCalls(FakeTextClient text) => text.Requests.Count(call => call.Step == TextStep.Revision);

    private static int ReviewCalls(FakeTextClient text) => text.Requests.Count(call => call.Step == TextStep.Review);

    private string VersionRows(Guid episodeId) => SqlProbe.ScalarText(
        Database,
        "SELECT string_agg(source || '|' || coalesce(parent_version_id::text, 'null'), ',' ORDER BY version) "
        + $"FROM story_versions WHERE episode_id = '{episodeId}'");

    private string IdOfVersion(Guid episodeId, int version) => SqlProbe.ScalarText(
        Database,
        $"SELECT id::text FROM story_versions WHERE episode_id = '{episodeId}' AND version = {version}");

    /// <summary>Reads a path out of the <c>revision</c> block: applied blocks live on the revised row, not-applied blocks on the generated one.</summary>
    private string RevisionValue(Guid episodeId, string path) => SqlProbe.ScalarText(
        Database,
        "SELECT coalesce((SELECT audit_report->'revision'" + path + " FROM story_versions "
        + $"WHERE episode_id = '{episodeId}' AND audit_report ? 'revision' ORDER BY version DESC LIMIT 1), '')");

    // ---- review and draft fixtures -----------------------------------------------------------------

    private static ReviewIssueV1 BlockingHook() =>
        new("i1", "hook_missing", "blocking", "hook", "Mở đầu.", "Thiếu móc.", "Thêm móc rõ ràng.");

    private static ReviewIssueV1 BlockingEnding() =>
        new("i3", "ending_missing", "blocking", "ending", "Kết.", "Thiếu kết.", "Viết lại phần kết.");

    private static ReviewIssueV1 TastePacing() =>
        new("i2", "pacing_issue", "advisory", "p:0", "Đoạn một.", "Nhịp chậm.", "Rút gọn đoạn.");

    private static StoryReviewV1 Review(IReadOnlyList<ReviewIssueV1> issues) => new(
        new ReviewScores(4, 4, 4, 4, 4, null, 4),
        [.. issues],
        [.. EarRuleQuestions.For(Language.Vi).Select(question => new EarRuleAnswer(question.QuestionId, true, "meta", string.Empty))],
        FactualCare: null,
        [],
        "Một bản tóm tắt bình tĩnh.");

    private static StoryContentV1 Draft() => new(
        $"Tiêu đề {StoryMarker}",
        new StorySegmentV1($"Mở đầu {StoryMarker}.", ["k1"], []),
        [new StorySegmentV1($"Đoạn một {StoryMarker}.", ["k2"], [])],
        new StorySegmentV1($"Kết {StoryMarker}.", ["k3"], []),
        "Mô tả.",
        ["tag"],
        [],
        new StoryStructureV1("a", "b", "c", "d", "e"));

    private static StoryContentV1 RevisedDraft() => new(
        $"Tiêu đề {StoryMarker}",
        new StorySegmentV1($"Mở đầu mới {StoryMarker} với lời dẫn rõ ràng hơn.", ["k1"], []),
        [new StorySegmentV1($"Đoạn một {StoryMarker} đã được viết lại cho gọn hơn.", ["k2"], [])],
        new StorySegmentV1($"Kết {StoryMarker}.", ["k3"], []),
        "Mô tả.",
        ["tag"],
        [],
        new StoryStructureV1("a", "b", "c", "d", "e"));

    /// <summary>The revision fixes the addressed hook and rewrites the untouched paragraph beyond <c>MinTextSimilarity</c>.</summary>
    private static StoryContentV1 DriftedDraft() => new(
        $"Tiêu đề {StoryMarker}",
        new StorySegmentV1($"Mở đầu {StoryMarker} nay đã có móc.", ["k1"], []),
        [new StorySegmentV1("Trời chuyển mưa trên con phố vắng và chiếc đồng hồ ga tàu điểm mười hai tiếng.", ["k2"], [])],
        new StorySegmentV1($"Kết {StoryMarker}.", ["k3"], []),
        "Mô tả.",
        ["tag"],
        [],
        new StoryStructureV1("a", "b", "c", "d", "e"));

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    private sealed class NullNotifier : IRealtimeNotifier
    {
        public Task NotifyAsync(Guid userId, RealtimeEvent realtimeEvent, CancellationToken cancellationToken) => Task.CompletedTask;
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
