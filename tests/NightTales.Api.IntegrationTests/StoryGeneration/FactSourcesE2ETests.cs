using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Review;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Pricing;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>
/// 02.5.5-T1 (02 §7, 02 README D3/D21): sources → sheet → approve → story, end to end on the real Worker graph.
/// The host registers the real <c>FactSheetGuard</c> (no guard is injected here), a true-crime episode is generated
/// by a scripted writer whose draft cites the approved fact, and the stored <c>story_versions.content</c> carries the
/// backend-built <c>fact_sources</c> — the cited <c>story_sources</c> row's title and url, never anything the model
/// wrote.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FactSourcesE2ETests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task GenerateJob_TrueCrime_StoresTheCitedSourcesInFactSources()
    {
        var world = await SeedAsync();
        var source = await RealGuardFixtures.SeedFetchedSourceAsync(
            Database,
            world.UserId,
            world.EpisodeId,
            new string('a', 64),
            Start,
            Ct);
        var sheet = await RealGuardFixtures.SheetAsync(
            Database,
            world.UserId,
            world.EpisodeId,
            1,
            [RealGuardFixtures.CitedFact("f1", "Nguon da duoc kiem chung.", source.Id, "s1")],
            RealGuardFixtures.Aliases(("s1", source.Id)),
            FactSheetStatus.Approved,
            Start,
            Ct);

        var runId = await GenerateAsync(world);
        SqlProbe.ScalarText(Database, $"SELECT input_fact_sheet_id::text FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe(sheet.Id.ToString(), "the generate path pinned the approved sheet");

        var writer = new CitedFactSourcesWriter();
        await using var worker = await StartWorkerAsync(writer);

        (await worker.RunOutboxOnceAsync(Ct)).ShouldBeGreaterThan(0);
        worker.Jobs.Enqueued.ShouldContain(job => job.JobType == "GenerateStoryJob");
        await worker.Jobs.RunPendingAsync(Ct);
        worker.Jobs.Failures.ShouldBeEmpty();
        await worker.RunOutboxOnceAsync(Ct);

        writer.Calls.ShouldBe(1, "the writer is reached once, with the facts the guard approved");
        SqlProbe.ScalarText(Database, $"SELECT status FROM generation_runs WHERE id = '{runId}'").ShouldBe("succeeded");

        var content = SqlProbe.ScalarText(
            Database,
            $"SELECT content FROM story_versions WHERE episode_id = '{world.EpisodeId}' ORDER BY version LIMIT 1");
        var sources = StoryContentJson.ReadFactSources(content);
        var entry = sources.ShouldHaveSingleItem()!.AsObject();
        entry["source_id"]!.GetValue<string>().ShouldBe(source.Id.ToString());
        entry["title"]!.GetValue<string>().ShouldBe("Article");
        entry["url"]!.GetValue<string>().ShouldBe(source.Url);
    }

    /// <summary>Seeds a user, a true-crime series with a draft episode and the active price version at the run's queued_at.</summary>
    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: Topic.TrueCrime, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(
            user.Id, series.Id, brief: "Mot tap thu nghiem.", ct: Ct);

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
                "fact-sources-e2e-price-version",
                Start.AddDays(-1)));
            await db.SaveChangesAsync(Ct);
        }

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary><c>POST /story/generate</c> on the real Api host; the registered real guard accepts the approved sheet.</summary>
    private async Task<Guid> GenerateAsync(World world)
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<IStoryCostEstimator>();
                services.AddSingleton<IStoryCostEstimator, FixedStoryCostEstimator>();
            });
        using var client = factory.ClientFor(world.UserId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{world.EpisodeId}/story/generate");
        request.Headers.Add("Idempotency-Key", $"fact-sources-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("generationRunId").GetGuid();
    }

    /// <summary>The real Worker graph with the scripted provider edge; the real <c>FactSheetGuard</c> is left in place.</summary>
    private async Task<WorkerHost> StartWorkerAsync(CitedFactSourcesWriter writer)
    {
        var host = new WorkerHost(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithSetting("Story:Review:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStoryWriter>();
                services.AddSingleton<IStoryWriter>(writer);
                services.RemoveAll<IStoryCostEstimator>();
                services.AddSingleton<IStoryCostEstimator, FixedStoryCostEstimator>();
                services.RemoveAll<IContentModerator>();
                services.AddSingleton<IContentModerator>(new FakeContentModerator());
                services.RemoveAll<IPromptAssembler>();
                services.AddSingleton<IPromptAssembler, StubPromptAssembler>();
                services.RemoveAll<IRealtimeNotifier>();
                services.AddSingleton<IRealtimeNotifier, NullStoryNotifier>();
                services.RemoveAll<IExtractedTextStore>();
                services.AddSingleton<IExtractedTextStore>(new FakeExtractedTextStore());
            });

        await host.StartAsync(Ct);

        return host;
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    /// <summary>A writer whose one valid draft cites the approved fact <c>f1</c>; no provider contact.</summary>
    private sealed class CitedFactSourcesWriter : IStoryWriter
    {
        /// <summary>How many paid writer calls were made.</summary>
        public int Calls { get; private set; }

        public ValueTask<TextCallResult<StoryContentV1>> WriteAsync(StoryWriteRequest req, CancellationToken ct)
        {
            Calls++;

            return ValueTask.FromResult(TextCallResult<StoryContentV1>.Ok(
                new StoryContentV1(
                    "Tieu de",
                    new StorySegmentV1("Mo dau f1.", ["k1"], ["f1"]),
                    [new StorySegmentV1("Doan mot.", ["k2"], [])],
                    new StorySegmentV1("Ket.", ["k3"], []),
                    "Mo ta.",
                    ["tag"],
                    [],
                    new StoryStructureV1("a", "b", "c", "d", "e")),
                "gpt-test",
                null,
                "req-test"));
        }

        public ValueTask<TextCallResult<StoryReviewV1>> ReviewAsync(StoryReviewRequest req, CancellationToken ct) =>
            throw new InvalidOperationException("The review stage is disabled in this test.");

        public ValueTask<TextCallResult<StoryContentV1>> ReviseAsync(StoryRevisionRequest req, CancellationToken ct) =>
            throw new InvalidOperationException("The revision stage is disabled in this test.");
    }
}
