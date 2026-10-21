using System.Globalization;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using NightTales.Application.Features.Stories.Jobs;
using NightTales.Domain.Stories.Similarity;
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

[Trait("Category", "Integration")]
public sealed class SimilarityQaTests(TestDatabase database) : IntegrationTest(database)
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
    public async Task Qa_CorpusCap_TieOrderingAndSurrogateBoundary_AreEnforced()
    {
        var world = await SeedAsync(SourceKind.None);
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToArray();
        foreach (var id in ids.Reverse())
        {
            InsertSource(world.UserId, world.EpisodeId, id, Start);
        }

        var settings = new SimilaritySettings();
        settings.Corpus.MaxSourceTexts = 2;
        settings.Corpus.MaxSourceTextChars = 3;
        var store = new FakeExtractedTextStore();
        foreach (var id in ids)
        {
            store.Add(id.ToString(), "ab\U0001F600c");
        }

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var corpus = await new SimilarityCorpusProvider(db, store, Options.Create(settings)).LoadAsync(world.EpisodeId, Language.Vi, Ct);
        corpus.Select(item => item.SourceId).ShouldBe(ids.Order().Take(2));
        corpus.All(item => item.Text == "ab").ShouldBeTrue();
        store.ReadCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Qa_CorpusIsolation_BothDirectionsAndOtherSeries_AreEnforced()
    {
        var owner = await SeedAsync(SourceKind.Url);
        var other = await SeedAsync(SourceKind.Url);
        var extraSeries = await new SeriesBuilder(Database, new TestCurrentUser()).CreateAsync(owner.UserId, ct: Ct);
        var extraEpisode = await new EpisodeBuilder(Database, new TestCurrentUser()).CreateAsync(owner.UserId, extraSeries.Id, ct: Ct);
        InsertStory(owner.UserId, owner.EpisodeId, "owner body");
        InsertStory(other.UserId, other.EpisodeId, "other body");
        InsertStory(owner.UserId, extraEpisode.Id, "other series body");
        var store = new FakeExtractedTextStore();
        store.Add(TextKey, SourceText());
        foreach (var world in new[] { owner, other })
        {
            var foreign = world.UserId == owner.UserId ? other : owner;
            await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
            var provider = new SimilarityCorpusProvider(db, store, Options.Create(new SimilaritySettings()));
            (await provider.LoadAsync(foreign.EpisodeId, Language.Vi, Ct)).ShouldBeEmpty();
            (await provider.LoadAsync(world.EpisodeId, Language.Vi, Ct)).Count.ShouldBe(1);
            (await provider.GetPastEpisodesAsync(foreign.SeriesId, Guid.Empty, 50, Ct)).ShouldBeEmpty();
            (await provider.GetPastEpisodesAsync(world.SeriesId, Guid.Empty, 50, Ct)).ShouldHaveSingleItem().SourceId.ShouldBe(world.EpisodeId);
        }
    }

    [Fact]
    public async Task Qa_NotFound_IsStorageError_NeverSilentSkip()
    {
        var world = await SeedAsync(SourceKind.Url);
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var store = new FakeExtractedTextStore();
        var provider = new SimilarityCorpusProvider(db, store, Options.Create(new SimilaritySettings()));
        var exception = await Should.ThrowAsync<SimilarityCorpusUnavailableException>(async () => await provider.LoadAsync(world.EpisodeId, Language.Vi, Ct));
        exception.Reason.ShouldBe("storage_error");
        exception.Message.ShouldBe("The similarity corpus is unavailable.");
        store.ReadCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData("timeout", "timeout")]
    [InlineData("db", "db_error")]
    [InlineData("wrapped_db", "db_error")]
    [InlineData("storage", "storage_error")]
    public async Task Qa_PipelineCorpusFailure_DegradesWithoutLeaking(string mode, string expectedReason)
    {
        var world = await SeedAsync(SourceKind.Url);
        var provider = new ProbeCorpus(mode);
        var sink = new TestLogSink();
        await using var worker = await StartWorkerAsync(new FakeExtractedTextStore(), sink, provider: provider);
        var run = await QueueAsync(world);
        var watch = Stopwatch.StartNew();
        using var scope = worker.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GenerateStoryJob>().RunAsync(run, Ct);
        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id='{run}'").ShouldBe("succeeded");
        SqlProbe.ScalarText(Database, $"SELECT similarity_score IS NULL FROM story_versions WHERE episode_id='{world.EpisodeId}'").ShouldBe("True");
        using var audit = JsonDocument.Parse(SqlProbe.ScalarText(Database, $"SELECT audit_report::text FROM story_versions WHERE episode_id='{world.EpisodeId}'"));
        audit.RootElement.TryGetProperty("similarity", out _).ShouldBeFalse();
        audit.RootElement.GetProperty("local").GetProperty("issues").EnumerateArray().ShouldContain(i =>
            i.GetProperty("rule").GetString() == "similarity_unavailable" && i.GetProperty("severity").GetString() == "advisory");
        sink.AllText().ShouldContain(expectedReason);
        AssertPrivateLogs(sink);
        if (mode == "timeout")
        {
            provider.CancellationObserved.ShouldBeTrue();
            watch.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(9));
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task Qa_JobCancellationDuringCorpusRead_PropagatesWithoutDegradation()
    {
        var world = await SeedAsync(SourceKind.Url);
        var provider = new ProbeCorpus("timeout");
        var sink = new TestLogSink();
        await using var worker = await StartWorkerAsync(new FakeExtractedTextStore(), sink, provider: provider);
        var run = await QueueAsync(world);
        using var jobToken = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var scope = worker.Services.CreateScope();
        var pending = scope.ServiceProvider.GetRequiredService<GenerateStoryJob>().RunAsync(run, jobToken.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await jobToken.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        provider.CancellationObserved.ShouldBeTrue();
        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id='{run}'").ShouldBe("running");
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id='{world.EpisodeId}'").ShouldBe(0);
        sink.AllText().ShouldNotContain("similarity_unavailable");
        sink.AllText().ShouldNotContain("reason=timeout");
        AssertPrivateLogs(sink);
    }

    [Fact]
    public async Task Qa_Success_AuditRoundingAndEveryLogLine_AreContentFree()
    {
        var world = await SeedAsync(SourceKind.Url);
        var store = new FakeExtractedTextStore();
        store.Add(TextKey, SourceText());
        var sink = new TestLogSink();
        await using var worker = await StartWorkerAsync(store, sink);
        var run = await QueueAsync(world);
        using var scope = worker.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GenerateStoryJob>().RunAsync(run, Ct);
        using var audit = JsonDocument.Parse(SqlProbe.ScalarText(Database, $"SELECT audit_report::text FROM story_versions WHERE episode_id='{world.EpisodeId}'"));
        var similarity = audit.RootElement.GetProperty("similarity");
        similarity.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ShouldBe(SimilarityKeys);
        similarity.GetRawText().ShouldNotContain(StoryMarker);
        similarity.GetRawText().ShouldNotContain(Paragraph);
        var score = similarity.GetProperty("score").GetDecimal();
        score.ShouldBe(Math.Round(score, 4, MidpointRounding.AwayFromZero));
        SqlProbe.ScalarText(Database, $"SELECT similarity_score::text FROM story_versions WHERE episode_id='{world.EpisodeId}'")
            .ShouldBe(score.ToString("F4", CultureInfo.InvariantCulture));
        foreach (var segment in similarity.GetProperty("segments").EnumerateArray())
        {
            segment.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ShouldBe(SegmentKeys);
            var share = segment.GetProperty("share").GetDecimal();
            share.ShouldBe(Math.Round(share, 4, MidpointRounding.AwayFromZero));
        }

        AssertPrivateLogs(sink);
        sink.AllText().ShouldContain("similarity_scored");
    }

    private static void AssertPrivateLogs(TestLogSink sink)
    {
        // AllText includes every property and exception, not only rendered messages.
        var text = sink.AllText();
        text.ShouldNotContain(StoryMarker);
        text.ShouldNotContain(Paragraph);
        text.ShouldNotContain(TextKey);
        text.ShouldNotContain(SourceUrlText);
    }

    private void InsertSource(Guid userId, Guid episodeId, Guid id, DateTimeOffset createdAt) =>
        SqlProbe.Execute(Database, "INSERT INTO story_sources (id,user_id,episode_id,kind,url,normalized_url,registrable_domain,status,extracted_text_key,created_at) "
            + $"VALUES ('{id}','{userId}','{episodeId}','url','https://example.com/{id}','https://example.com/{id}','example.com','fetched','{id}','{createdAt:O}')");

    private void InsertStory(Guid userId, Guid episodeId, string body)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(Database, "INSERT INTO story_versions (id,user_id,episode_id,version,source,title,body,keywords,word_count,content,created_at) "
            + $"VALUES ('{id}','{userId}','{episodeId}',1,'generated','title','{body}','{{}}',1,'{{}}'::jsonb,now())");
        SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id='{id}' WHERE id='{episodeId}'");
    }

    private sealed class ProbeCorpus(string mode) : ISimilarityCorpusProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }
        public async ValueTask<IReadOnlyList<CorpusText>> LoadAsync(Guid episodeId, Language lang, CancellationToken ct)
        {
            Entered.TrySetResult();
            if (mode == "db")
            {
                throw new ProbeDbException();
            }

            if (mode == "wrapped_db")
            {
                throw new InvalidOperationException(TextKey + SourceUrlText + Paragraph, new ProbeDbException());
            }

            if (mode == "storage")
            {
                throw new InvalidOperationException(TextKey + SourceUrlText + Paragraph);
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("The blocking corpus must be cancelled.");
        }

        public ValueTask<IReadOnlyList<CorpusText>> GetPastEpisodesAsync(Guid seriesId, Guid excludeEpisodeId, int max, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ProbeDbException() : DbException(TextKey + SourceUrlText + Paragraph);

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

    private async Task<Guid> QueueAsync(World world)
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

        return runId;
    }

    private async Task<WorkerHost> StartWorkerAsync(FakeExtractedTextStore store, TestLogSink sink, bool shortDraft = false, ISimilarityCorpusProvider? provider = null)
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
                if (provider is not null)
                {
                    services.RemoveAll<ISimilarityCorpusProvider>();
                    services.AddSingleton(provider);
                }
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

        public ValueTask<TextCallResult<StoryContentV1>> ReviseAsync(StoryRevisionRequest req, CancellationToken ct) =>
            throw new InvalidOperationException("Revision is disabled for these generation tests.");
    }

    private sealed class NullNotifier : IRealtimeNotifier
    {
        public Task NotifyAsync(Guid userId, RealtimeEvent realtimeEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubAssembler : IPromptAssembler
    {
        public AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext ctx) => new("system", "{}", "pv-similarity", "cache");
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
