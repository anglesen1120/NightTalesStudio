using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Series;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>
/// The generate-side true-crime branch of 02.3.3-T1 (02 README D21) through the real
/// <c>GenerateStoryCommandHandler</c>: a true-crime episode whose guard returns an approved sheet is
/// accepted and the run pins that sheet's id, which the job re-check later compares. The refusal without a
/// sheet (409 <c>fact_sheet_required</c>, no run) and the fiction path are covered by
/// <c>GenerateStoryQaTests</c> and the other generate tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TrueCrimeGenerationFlowTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task GenerateStory_TrueCrimeWithApprovedSheet_AcceptsAndPinsTheSheetOnTheRun()
    {
        var world = await SeedAsync(Topic.TrueCrime);
        var sheet = await new FactSheetBuilder(Database, new TestCurrentUser { Id = world.UserId })
            .CreateAsync(world.UserId, world.EpisodeId, Ct);
        var guard = new FakeFactSheetGuard
        {
            ApprovedSheet = new ApprovedFactSheet(sheet.Id, sheet.Version, [], new Dictionary<string, Guid>(StringComparer.Ordinal)),
        };
        using var factory = Factory(guard);
        using var client = factory.ClientFor(world.UserId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{world.EpisodeId}/story/generate");
        request.Headers.Add("Idempotency-Key", $"tc-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var runId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("generationRunId").GetGuid();
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || coalesce(input_fact_sheet_id::text, 'null') FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe($"queued/{sheet.Id}", "the run pins the approved sheet the job re-checks at start");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_generating");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_generation_requested.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
    }

    private ApiFactory Factory(IFactSheetGuard guard) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<IStoryCostEstimator>();
                services.AddSingleton<IStoryCostEstimator, FixedStoryCostEstimator>();
                services.RemoveAll<IFactSheetGuard>();
                services.AddSingleton<IFactSheetGuard>(guard);
            });

    private async Task<World> SeedAsync(Topic topic)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
