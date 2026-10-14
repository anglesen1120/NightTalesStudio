using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Pricing;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>
/// 02.3.3-T1 (02 README D21): the job re-checks the approved fact sheet at start. When a re-approval lands between
/// <c>POST /episodes/{id}/story/generate</c> and the job (<c>GenerateStoryJob</c> ->
/// <c>StoryGenerationPipeline.PrepareAsync</c>, step 0), the run pins the sheet the command saw and the guard now
/// serves a newer one: the run fails with <c>story_generation_failed</c>/<c>fact_sheet_outdated</c>, is not counted
/// toward the daily limit, and no paid call is made. The real Worker graph and PostgreSQL, the provider edge scripted.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JobRecheckFactSheetChangeTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task JobRecheck_FactSheetChangedBetweenCommandAndStart_FailsRunNotCounted()
    {
        var world = await SeedAsync();
        var sheet = await new FactSheetBuilder(Database, new TestCurrentUser { Id = world.UserId })
            .CreateAsync(world.UserId, world.EpisodeId, Ct);
        var guard = new FakeFactSheetGuard { ApprovedSheet = Approved(sheet) };

        var runId = await GenerateAsync(world, guard);
        SqlProbe.ScalarText(Database, $"SELECT input_fact_sheet_id::text FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe(sheet.Id.ToString(), "the command pinned the sheet it approved");

        var writer = new ScriptedStoryWriter();
        await using var worker = await StartWorkerAsync(writer, guard);

        // The re-approval lands before the job starts: the guard serves a newer sheet while the run still pins the
        // sheet the command saw (the newer sheet needs no real row: only the pinned id is a foreign key, and the
        // re-check compares the ids the guard returns).
        guard.ApprovedSheet = new ApprovedFactSheet(
            Guid.CreateVersion7(), sheet.Version + 1, [], new Dictionary<string, Guid>(StringComparer.Ordinal));

        (await worker.RunOutboxOnceAsync(Ct)).ShouldBeGreaterThan(0);
        worker.Jobs.Enqueued.ShouldContain(job => job.JobType == "GenerateStoryJob");
        await worker.Jobs.RunPendingAsync(Ct);
        worker.Jobs.Failures.ShouldBeEmpty();
        await worker.RunOutboxOnceAsync(Ct);

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || error_code || '/' || error_detail || '/' || counts_toward_limit::text "
                + $"FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("failed/story_generation_failed/fact_sheet_outdated/false");

        // The refusal is step 0: nothing is built, no model call is paid, no version is written and the episode is
        // restored to the status the command found.
        writer.Calls.ShouldBe(0, "the guard refuses before the request is built and before any paid call");
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
    }

    /// <summary>Seeds a user, a true-crime series, a <c>draft</c> episode and the active price version at queued_at.</summary>
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
                "recheck-price-version",
                Start.AddDays(-1)));
            await db.SaveChangesAsync(Ct);
        }

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary><c>POST /story/generate</c> on the real Api host with the scripted guard; returns the queued run's id.</summary>
    private async Task<Guid> GenerateAsync(World world, IFactSheetGuard guard)
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<IStoryCostEstimator>();
                services.AddSingleton<IStoryCostEstimator, FixedStoryCostEstimator>();
                services.RemoveAll<IFactSheetGuard>();
                services.AddSingleton<IFactSheetGuard>(guard);
            });
        using var client = factory.ClientFor(world.UserId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{world.EpisodeId}/story/generate");
        request.Headers.Add("Idempotency-Key", $"recheck-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        return body.GetProperty("generationRunId").GetGuid();
    }

    private async Task<WorkerHost> StartWorkerAsync(ScriptedStoryWriter writer, IFactSheetGuard guard)
    {
        var host = new WorkerHost(Database, new FakeClock(Start))
            .WithSetting("Story:Review:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStoryWriter>();
                services.AddSingleton<IStoryWriter>(writer);
                services.RemoveAll<IContentModerator>();
                services.AddSingleton<IContentModerator>(new FakeContentModerator());
                services.RemoveAll<IPromptAssembler>();
                services.AddSingleton<IPromptAssembler, StubPromptAssembler>();
                services.RemoveAll<IRealtimeNotifier>();
                services.AddSingleton<IRealtimeNotifier, NullStoryNotifier>();
                services.RemoveAll<IFactSheetGuard>();
                services.AddSingleton<IFactSheetGuard>(guard);
            });

        await host.StartAsync(Ct);

        return host;
    }

    /// <summary>The guard's view of a real draft row: the id and version the command pins, no facts.</summary>
    private static ApprovedFactSheet Approved(FactSheet sheet) => new(
        sheet.Id,
        sheet.Version,
        [],
        new Dictionary<string, Guid>(StringComparer.Ordinal));

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
