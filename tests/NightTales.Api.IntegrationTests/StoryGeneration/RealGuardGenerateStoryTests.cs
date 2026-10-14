using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
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
/// The real <c>FactSheetGuard</c> on the generate path (02.5.5-T1, 02 README D8/D21): the host registers the real
/// guard, so <c>POST /episodes/{id}/story/generate</c> refuses a true-crime episode whose approved sheet is
/// missing, still a draft or already superseded — and accepts one whose approved row is real and resolvable.
/// </summary>
/// <remarks>
/// No guard is injected here, on purpose: the DI cutover of 02.5.5-T1 is what these cases exercise, so every
/// refusal comes from the registered <c>FactSheetGuard</c> reading real <c>fact_sheets</c> rows.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RealGuardGenerateStoryTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task GenerateStoryCommand_TrueCrime_NoApprovedSheet_Returns409FactSheetRequired()
    {
        var world = await SeedAsync(Topic.TrueCrime);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await GenerateAsync(client, world.EpisodeId);

        await AssertRefusedAsync(world, response);
    }

    [Fact]
    public async Task Generate_TrueCrimeWithADraftSheetOnly_Returns409FactSheetRequired()
    {
        var world = await SeedAsync(Topic.TrueCrime);
        var source = await RealGuardFixtures.SeedFetchedSourceAsync(Database, world.UserId, world.EpisodeId, new string('a', 64), Start, Ct);
        await RealGuardFixtures.SheetAsync(
            Database,
            world.UserId,
            world.EpisodeId,
            1,
            [RealGuardFixtures.CitedFact("f1", "One.", source.Id, "s1")],
            RealGuardFixtures.Aliases(("s1", source.Id)),
            FactSheetStatus.Draft,
            Start,
            Ct);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await GenerateAsync(client, world.EpisodeId);

        await AssertRefusedAsync(world, response);
    }

    [Fact]
    public async Task Generate_TrueCrimeWithASupersededSheetOnly_Returns409FactSheetRequired()
    {
        var world = await SeedAsync(Topic.TrueCrime);
        var first = await RealGuardFixtures.SeedFetchedSourceAsync(Database, world.UserId, world.EpisodeId, new string('a', 64), Start, Ct);
        var second = await RealGuardFixtures.SeedFetchedSourceAsync(Database, world.UserId, world.EpisodeId, new string('b', 64), Start, Ct);
        // A full, resolvable sheet: only its lifecycle state disqualifies it.
        await RealGuardFixtures.SheetAsync(
            Database,
            world.UserId,
            world.EpisodeId,
            1,
            [RealGuardFixtures.CitedFact("f1", "One.", first.Id, "s1"), RealGuardFixtures.CitedFact("f2", "Two.", second.Id, "s2")],
            RealGuardFixtures.Aliases(("s1", first.Id), ("s2", second.Id)),
            FactSheetStatus.Superseded,
            Start,
            Ct);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await GenerateAsync(client, world.EpisodeId);

        await AssertRefusedAsync(world, response);
    }

    [Fact]
    public async Task Generate_TrueCrimeWithAnEmptyApprovedSheet_Returns409FactSheetRequired()
    {
        var world = await SeedAsync(Topic.TrueCrime);
        // Approved but without a single fact: there is nothing a story could rest on, so it is unusable.
        await RealGuardFixtures.SheetAsync(
            Database,
            world.UserId,
            world.EpisodeId,
            1,
            [],
            RealGuardFixtures.Aliases(),
            FactSheetStatus.Approved,
            Start,
            Ct);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await GenerateAsync(client, world.EpisodeId);

        await AssertRefusedAsync(world, response);
    }

    [Fact]
    public async Task Generate_FictionWithoutAnySheet_IsAccepted()
    {
        var world = await SeedAsync(Topic.Ghost);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await GenerateAsync(client, world.EpisodeId);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_generating");
    }

    [Fact]
    public async Task Generate_TrueCrimeWithARealApprovedSheet_IsAcceptedAndPinsTheSheet()
    {
        var world = await SeedAsync(Topic.TrueCrime);
        var first = await RealGuardFixtures.SeedFetchedSourceAsync(Database, world.UserId, world.EpisodeId, new string('a', 64), Start, Ct);
        var second = await RealGuardFixtures.SeedFetchedSourceAsync(Database, world.UserId, world.EpisodeId, new string('b', 64), Start, Ct);
        var sheet = await RealGuardFixtures.SheetAsync(
            Database,
            world.UserId,
            world.EpisodeId,
            1,
            [RealGuardFixtures.CitedFact("f1", "One.", first.Id, "s1"), RealGuardFixtures.CitedFact("f2", "Two.", second.Id, "s2")],
            RealGuardFixtures.Aliases(("s1", first.Id), ("s2", second.Id)),
            FactSheetStatus.Approved,
            Start,
            Ct);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await GenerateAsync(client, world.EpisodeId);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var runId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("generationRunId").GetGuid();
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || coalesce(input_fact_sheet_id::text, 'null') FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe($"queued/{sheet.Id}", "the real guard resolved the approved row and the run pins its id");
    }

    /// <summary>The D8 refusal: 409 <c>fact_sheet_required</c>, no run, no key, the episode untouched.</summary>
    private async Task AssertRefusedAsync(World world, HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ProblemAsync(response)).Code.ShouldBe("fact_sheet_required");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("draft");
        SqlProbe.Count(Database, $"SELECT count(*) FROM idempotency_keys WHERE user_id = '{world.UserId}'")
            .ShouldBe(0, "a refused command rolls its idempotency claim back");
    }

    private ApiFactory Factory() =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                // The real estimator needs a price version this fixture does not seed, and the guard runs before it.
                services.RemoveAll<IStoryCostEstimator>();
                services.AddSingleton<IStoryCostEstimator, FixedStoryCostEstimator>();
            });

    private static async Task<HttpResponseMessage> GenerateAsync(HttpClient client, Guid episodeId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/story/generate");
        request.Headers.Add("Idempotency-Key", $"real-guard-{Guid.NewGuid():N}");

        return await client.SendAsync(request, Ct);
    }

    private static async Task<ProblemResponse> ProblemAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<ProblemResponse>(await response.Content.ReadAsStringAsync(Ct), ProblemResponse.Options)!;

    private async Task<World> SeedAsync(Topic topic)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(
            user.Id,
            series.Id,
            status: EpisodeStatus.Draft,
            ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
