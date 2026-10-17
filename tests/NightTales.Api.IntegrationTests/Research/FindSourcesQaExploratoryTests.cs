using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Research.SourceFinder;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Research;

/// <summary>
/// QA exploratory cases for <c>POST /api/v1/episodes/{id}/sources/find</c> (02.7.2-T2) that the named acceptance
/// facts in <see cref="FindSourcesEndpointTests"/> do not reach: the archived-series refusal, the 422 replay of a
/// reused key with a different body, the per-language allow-list gate with a positive control, the
/// <c>story_review</c> state, the query length boundary, a whitespace-only query and an unknown episode.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FindSourcesQaExploratoryTests(TestDatabase database) : IntegrationTest(database)
{
    private const string OutboxType = "source_find_requested.v1";

    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    private static string FindRoute(Guid episodeId) => $"/api/v1/episodes/{episodeId}/sources/find";

    [Fact]
    public async Task FindSources_ArchivedSeries_Returns409SeriesArchived_TakesNoRunAndNoReservation()
    {
        var world = await SeedAsync();
        SqlProbe.Execute(Database, $"UPDATE series SET archived_at = now() WHERE id = '{world.SeriesId}'");
        var ledger = new CountingLedger();
        using var factory = NewFactory(Language.Vi, ledger);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "archived-k");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("series_archived");
        CountRuns(world).ShouldBe(0, "an archived series is refused before a run row is written");
        OutboxCount(world).ShouldBe(0);
        ledger.Reservations.ShouldBe(0, "an archived series is refused before any credit is reserved");
    }

    [Fact]
    public async Task FindSources_ReusedKeyWithDifferentQuery_Returns422_AndNoSecondRun()
    {
        var world = await SeedAsync();
        using var factory = NewFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var first = await FindAsync(client, world.EpisodeId, "reuse-k", query: "first query");
        using var reused = await FindAsync(client, world.EpisodeId, "reuse-k", query: "a different query");

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync(Ct));
        reused.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity,
            await reused.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(reused)).Code.ShouldBe("idempotency_key_reused");
        CountRuns(world).ShouldBe(1, "the reused key must not queue a second run");
        OutboxCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task FindSources_SeriesLanguageHasNoApprovedSite_Returns404FeatureDisabled_EvenWhenOtherLanguageIsApproved()
    {
        var world = await SeedAsync(language: Language.En);
        using var factory = NewFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "en-k");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("feature_disabled");
        CountRuns(world).ShouldBe(0);
        OutboxCount(world).ShouldBe(0);
    }

    [Fact]
    public async Task FindSources_SeriesLanguageHasApprovedSite_Returns202_PositiveControl()
    {
        var world = await SeedAsync(language: Language.Vi);
        using var factory = NewFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "vi-k");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        CountRuns(world).ShouldBe(1);
        OutboxCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task FindSources_StoryReviewEpisode_Returns202()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryReview);
        using var factory = NewFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "review-k");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        CountRuns(world).ShouldBe(1);
    }

    [Fact]
    public async Task FindSources_QueryExactly300Chars_Returns202()
    {
        var world = await SeedAsync();
        using var factory = NewFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "q300-k", query: new string('q', 300));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        CountRuns(world).ShouldBe(1);
    }

    [Fact]
    public async Task FindSources_WhitespaceOnlyQuery_Returns202_AsIfNoQuery()
    {
        var world = await SeedAsync();
        using var factory = NewFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "blank-k", query: "   ");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        CountRuns(world).ShouldBe(1);
    }

    [Fact]
    public async Task FindSources_UnknownEpisode_Returns404NotFound()
    {
        var world = await SeedAsync();
        using var factory = NewFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, Guid.CreateVersion7(), "missing-k");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");
        CountRuns(world).ShouldBe(0);
    }

    private ApiFactory NewFactory(Language approvedFor, CountingLedger? ledger = null) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<IFindSourcesCostEstimator>();
                services.AddSingleton<IFindSourcesCostEstimator, FixedEstimator>();

                services.RemoveAll<IFeatureFlags>();
                services.AddSingleton<IFeatureFlags, AlwaysOnFeatureFlags>();

                services.RemoveAll<IResearchAllowList>();
                services.AddSingleton<IResearchAllowList>(new ApprovedOnlyFor(approvedFor));

                if (ledger is not null)
                {
                    services.RemoveAll<ICreditLedger>();
                    services.AddSingleton<ICreditLedger>(ledger);
                }
            });

    private static Task<HttpResponseMessage> FindAsync(
        HttpClient client,
        Guid episodeId,
        string key,
        string? query = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, FindRoute(episodeId))
        {
            Content = JsonContent.Create(new { query }),
        };
        request.Headers.Add("Idempotency-Key", key);

        return client.SendAsync(request, Ct);
    }

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem.ShouldNotBeNull();

        return problem;
    }

    private long CountRuns(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}' AND kind = 'find_sources'");

    private long OutboxCount(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM outbox_messages WHERE type = '{OutboxType}' AND user_id = '{world.UserId}'");

    private async Task<World> SeedAsync(
        EpisodeStatus status = EpisodeStatus.Draft,
        Language language = Language.Vi)
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, new TestCurrentUser(), new FakeClock(Start))
            .CreateAsync(user.Id, topic: Topic.TrueCrime, language: language, ct: Ct);
        var episode = await new EpisodeBuilder(Database, new TestCurrentUser(), new FakeClock(Start))
            .CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    /// <summary>The worst case is fixed: the route test never depends on a price version row.</summary>
    private sealed class FixedEstimator : IFindSourcesCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.25m, 1_000, 2_000));
    }

    /// <summary>Every flag on, so the allow-list gate is the one under test.</summary>
    private sealed class AlwaysOnFeatureFlags : IFeatureFlags
    {
        public Task<bool> IsEnabledAsync(string key, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>Approves one site for exactly one language and nothing for the other, so the gate is per language.</summary>
    private sealed class ApprovedOnlyFor(Language language) : IResearchAllowList
    {
        public IReadOnlyList<AllowedSite> Approved(Language requested) =>
            requested == language
                ? [new AllowedSite("www.example.org", "Example", requested.ToString().ToLowerInvariant(), "news", "approved")]
                : [];

        public bool IsAllowed(string registrableOrHost) => false;

        public string Hash => string.Empty;
    }

    /// <summary>Counts reservations, so a refused command is proven to have taken none.</summary>
    private sealed class CountingLedger : ICreditLedger
    {
        public int Reservations { get; private set; }

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            Reservations++;

            return ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(Guid.CreateVersion7()));
        }

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }
}
