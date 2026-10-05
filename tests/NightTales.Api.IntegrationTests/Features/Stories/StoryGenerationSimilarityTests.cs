using System.Globalization;
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
using NightTales.Application.Abstractions.Similarity;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Review;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.Infrastructure.Storage;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Research;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Stories;

/// <summary>Generation step 6 through the real Api, Worker, corpus provider and PostgreSQL; only provider edges are scripted.</summary>
[Trait("Category", "Integration")]
public sealed class StoryGenerationSimilarityTests(TestDatabase database) : IntegrationTest(database)
{
    private const string TextKey = "research/SOURCEKEYMARKEROTTER.txt";
    private const string StoryMarker = "STORYMARKERSIMILARITYIBIS";
    private const string SourceUrlText = "https://news.com/similarity-source";
    private const string Paragraph = "Tôi trở về ngôi làng nhỏ bên dòng sông khi bóng chiều phủ kín con đường cũ. "
        + "Người gác cổng đứng lặng dưới mái hiên và nhìn qua khu vườn nơi những chiếc lá đang rơi. "
        + "Từ căn nhà cuối phố một tiếng chuông vang lên khiến mọi người dừng lại trước cánh cửa gỗ. "
        + "Tôi bước qua sân tìm chiếc đèn mà bà đã để trên bàn từ đêm trước.";
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly string[] SimilarityKeys = ["corpus", "n", "score", "segments"];
    private static readonly string[] SegmentKeys = ["segment", "share"];
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task WorkerHost_ResolvesSimilarityCorpus_WithProductionExtractedTextStore()
    {
        await using var worker = new WorkerHost(Database, new FakeClock(Start));
        await worker.StartAsync(Ct);

        using var scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISimilarityCorpusProvider>().ShouldBeOfType<SimilarityCorpusProvider>();
        scope.ServiceProvider.GetServices<IExtractedTextStore>().ShouldHaveSingleItem().ShouldBeOfType<ExtractedTextStore>();
        scope.ServiceProvider.GetRequiredService<IBlobStore>().ShouldBeOfType<S3BlobStore>();
    }

    [Fact]
    public async Task GenerateStoryJob_WithFetchedSources_WritesSimilarityScoreAndAuditSection()
    {
        var store = new FakeExtractedTextStore();
        store.Add(TextKey, SourceText());
        var world = await SeedAsync(SourceKind.Url);
        var sink = new TestLogSink();
        await using var worker = await StartWorkerAsync(store, sink);

        await GenerateAndRunAsync(world, worker);

        using var audit = ReadAudit(world);
        var similarity = audit.RootElement.GetProperty("similarity");
        similarity.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(SimilarityKeys);
        similarity.GetProperty("score").GetDecimal().ShouldBe(1m);
        similarity.GetProperty("n").GetInt32().ShouldBe(10);
        similarity.GetProperty("corpus").GetString().ShouldBe("sources");
        var segments = similarity.GetProperty("segments").EnumerateArray().ToArray();
        segments.ShouldNotBeEmpty();
        segments.ShouldContain(segment => segment.GetProperty("segment").GetString() == "p:0"
            && segment.GetProperty("share").GetDecimal() == 1m);
        foreach (var segment in segments)
        {
            segment.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(SegmentKeys);
        }

        var score = decimal.Parse(SqlProbe.ScalarText(Database,
            $"SELECT similarity_score::text FROM story_versions WHERE episode_id = '{world.EpisodeId}'"), CultureInfo.InvariantCulture);
        score.ShouldBe(Math.Round(similarity.GetProperty("score").GetDecimal(), 4, MidpointRounding.AwayFromZero));
        Issues(audit).ShouldContain(issue => issue.GetProperty("rule").GetString() == "Q-SIM-02");
        Issues(audit).ShouldNotContain(issue => issue.GetProperty("rule").GetString() == "similarity_unavailable");
        store.ReadCalls.ShouldBe(1);
        AssertPrivateLogs(sink);
        sink.AllText().ShouldContain("similarity_scored");
    }

    [Fact]
    public async Task GenerateStoryJob_WithoutSources_WritesNullScoreAndNoAuditSection()
    {
        var store = new FakeExtractedTextStore();
        var world = await SeedAsync(SourceKind.None);
        await using var worker = await StartWorkerAsync(store, new TestLogSink());

        await GenerateAndRunAsync(world, worker);

        AssertNotScored(world);
        store.ReadCalls.ShouldBe(0);
    }

    [Fact]
    public async Task CorpusReadFailure_DegradesToNullAndAdvisoryIssue()
    {
        var store = new FakeExtractedTextStore { FailReads = true };
        var world = await SeedAsync(SourceKind.Url);
        var sink = new TestLogSink();
        await using var worker = await StartWorkerAsync(store, sink);

        await GenerateAndRunAsync(world, worker);

        using var audit = ReadAudit(world);
        audit.RootElement.TryGetProperty("similarity", out _).ShouldBeFalse();
        AssertNullScore(world);
        var issue = Issues(audit).Single(item => item.GetProperty("rule").GetString() == "similarity_unavailable");
        issue.GetProperty("severity").GetString().ShouldBe("advisory");
        issue.GetProperty("kind").GetString().ShouldBe("objective");
        store.ReadCalls.ShouldBe(1);
        sink.AllText().ShouldContain("storage_error");
        AssertPrivateLogs(sink);
    }

    [Fact]
    public async Task GenerateStoryJob_OnlyUserNote_WritesNullScoreAndNoAuditSection()
    {
        var store = new FakeExtractedTextStore { FailReads = true };
        var world = await SeedAsync(SourceKind.UserNote);
        await using var worker = await StartWorkerAsync(store, new TestLogSink());

        await GenerateAndRunAsync(world, worker);

        AssertNotScored(world);
        store.ReadCalls.ShouldBe(0, "even a fetched user note is never loaded into the corpus");
    }

    [Fact]
    public async Task GenerateStoryJob_EmptyExtractedText_WritesNullScoreWithoutAdvisory()
    {
        var store = new FakeExtractedTextStore();
        store.Add(TextKey, " \t\r\n ");
        var world = await SeedAsync(SourceKind.Url);
        await using var worker = await StartWorkerAsync(store, new TestLogSink());

        await GenerateAndRunAsync(world, worker);

        AssertNotScored(world);
        store.ReadCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GenerateStoryJob_ShortDraft_WritesNullScoreWithoutAdvisory()
    {
        var store = new FakeExtractedTextStore();
        store.Add(TextKey, SourceText());
        var world = await SeedAsync(SourceKind.Url);
        await using var worker = await StartWorkerAsync(store, new TestLogSink(), shortDraft: true);

        await GenerateAndRunAsync(world, worker);

        AssertNotScored(world);
        store.ReadCalls.ShouldBe(1);
    }

    private void AssertNotScored(World world)
    {
        AssertNullScore(world);
        using var audit = ReadAudit(world);
        audit.RootElement.TryGetProperty("similarity", out _).ShouldBeFalse();
        Issues(audit).ShouldNotContain(issue => issue.GetProperty("rule").GetString() == "similarity_unavailable");
    }

    private void AssertNullScore(World world) => SqlProbe.ScalarText(Database,
        $"SELECT similarity_score IS NULL FROM story_versions WHERE episode_id = '{world.EpisodeId}'").ShouldBe("True");

    private JsonDocument ReadAudit(World world) => JsonDocument.Parse(SqlProbe.ScalarText(Database,
        $"SELECT audit_report::text FROM story_versions WHERE episode_id = '{world.EpisodeId}'"));

    private static JsonElement.ArrayEnumerator Issues(JsonDocument audit) =>
        audit.RootElement.GetProperty("local").GetProperty("issues").EnumerateArray();

    private static void AssertPrivateLogs(TestLogSink sink)
    {
        sink.AllText().ShouldNotContain(StoryMarker);
        sink.AllText().ShouldNotContain(Paragraph);
        sink.AllText().ShouldNotContain(TextKey);
        sink.AllText().ShouldNotContain(SourceUrlText);
    }

    private async Task<World> SeedAsync(SourceKind sourceKind)
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
                new Dictionary<string, ImagePriceRate>(), null, TextCostCalculator.DefaultSurchargeThresholdTokens).ToJson();
            db.PriceVersions.Add(PriceVersion.Create(1, Start.AddDays(-1), rates,
                PriceVersionJson.WriteLimits(PriceLimits.D4Defaults), "similarity-price-version", Start.AddDays(-1)));
        }

        if (sourceKind == SourceKind.Url)
        {
            var url = SourceUrl.Parse(SourceUrlText, new FakePublicSuffixList());
            url.IsError.ShouldBeFalse();
            var source = StorySource.CreateUrl(Guid.CreateVersion7(), user.Id, episode.Id, url.Value, Start);
            source.IsError.ShouldBeFalse();
            source.Value.MarkFetched(SourceUrlText, null, "text/plain", new string('a', 64), TextKey, SourceText().Length, Start);
            db.StorySources.Add(source.Value);
        }
        else if (sourceKind == SourceKind.UserNote)
        {
            var source = StorySource.CreateNote(Guid.CreateVersion7(), user.Id, episode.Id, SourceText(), Start);
            source.IsError.ShouldBeFalse();
            db.StorySources.Add(source.Value);
        }

        await db.SaveChangesAsync(Ct);
        return new World(user.Id, series.Id, episode.Id);
    }

    private async Task GenerateAndRunAsync(World world, WorkerHost worker)
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
        request.Headers.Add("Idempotency-Key", $"similarity-{Guid.NewGuid():N}");
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var runId = body.GetProperty("generationRunId").GetGuid();
        var reservation = Guid.Parse(SqlProbe.ScalarText(Database, $"SELECT reservation_id::text FROM generation_runs WHERE id = '{runId}'"));
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(Start)))
        {
            db.UsageRecords.Add(UsageRecord.Record(world.UserId, UsageOperation.Story, "openai", 0.25m, Start,
                episodeId: world.EpisodeId, seriesId: world.SeriesId, generationRunId: runId, reservationId: reservation));
            await db.SaveChangesAsync(Ct);
        }

        (await worker.RunOutboxOnceAsync(Ct)).ShouldBeGreaterThan(0);
        worker.Jobs.Enqueued.ShouldContain(job => job.JobType == "GenerateStoryJob");
        await worker.Jobs.RunPendingAsync(Ct);
        worker.Jobs.Failures.ShouldBeEmpty();
        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("succeeded");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_review");
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
    }

    private async Task<WorkerHost> StartWorkerAsync(FakeExtractedTextStore store, TestLogSink sink, bool shortDraft = false)
    {
        var host = new WorkerHost(Database, new FakeClock(Start))
            .WithSetting("Story:Review:Enabled", "false")
            .WithTelemetry()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExtractedTextStore>();
                services.AddSingleton<IExtractedTextStore>(store);
                services.RemoveAll<IStoryWriter>();
                services.AddSingleton<IStoryWriter>(new Writer(shortDraft));
                services.RemoveAll<IContentModerator>();
                services.AddSingleton<IContentModerator>(new FakeContentModerator());
                services.RemoveAll<IPromptAssembler>();
                services.AddSingleton<IPromptAssembler, StubAssembler>();
                services.RemoveAll<IRealtimeNotifier>();
                services.AddSingleton<IRealtimeNotifier, NullNotifier>();
                services.AddSingleton<ILogEventSink>(sink);
            });
        await host.StartAsync(Ct);
        return host;
    }

    private static StoryContentV1 Draft(bool shortDraft = false) => new(
        $"Tiêu đề {StoryMarker}",
        new StorySegmentV1($"Mở đầu {StoryMarker}.", ["k1"], []),
        [new StorySegmentV1(shortDraft ? $"Đoạn ngắn {StoryMarker}." : $"{Paragraph} {StoryMarker}.", ["k2"], [])],
        new StorySegmentV1($"Kết thúc {StoryMarker}.", ["k3"], []),
        "Mô tả.", ["tag"], [], new StoryStructureV1("a", "b", "c", "d", "e"));

    private static string SourceText() => $"Tiêu đề {StoryMarker} Mở đầu {StoryMarker}. {Paragraph} {StoryMarker}. Kết thúc {StoryMarker}.";

    private enum SourceKind { None, Url, UserNote }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    private sealed class Writer(bool shortDraft) : IStoryWriter
    {
        public ValueTask<TextCallResult<StoryContentV1>> WriteAsync(StoryWriteRequest req, CancellationToken ct) =>
            ValueTask.FromResult(TextCallResult<StoryContentV1>.Ok(Draft(shortDraft), "gpt-test", null, "req-similarity"));

        public ValueTask<TextCallResult<StoryReviewV1>> ReviewAsync(StoryReviewRequest req, CancellationToken ct) =>
            throw new InvalidOperationException("Review is disabled for these generation tests.");
    }

    private sealed class NullNotifier : IRealtimeNotifier
    {
        public Task NotifyAsync(Guid userId, RealtimeEvent realtimeEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubAssembler : IPromptAssembler
    {
        public AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext ctx) => new("system", "{}", "pv-similarity", "cache");
        public ErrorOr<AssembledPrompt> Review(StoryReviewRequest req) => new AssembledPrompt("system", "{}", "pv-review", "cache");
    }

    private sealed class FixedEstimator : IStoryCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));
        public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WorstCaseAsync(tier, language, ct);
        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WorstCaseAsync(tier, language, ct);
    }
}
