using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
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
/// The candidate routes of an episode against the real host and PostgreSQL 17 (02.7.2-T2, plan §4/§5):
/// <c>GET …/sources/candidates</c>, <c>POST …/candidates/{id}/add</c> and <c>POST …/candidates/{id}/dismiss</c>.
/// The add route runs the normal 02.5.1 source path (episode and 24 h caps, the blocklist, duplicates) and only
/// then flags the candidate, so every failure leaves it <c>proposed</c>; the dismiss route is idempotent and an
/// <c>added</c> candidate refuses it. Another user's — or another episode's — candidate is always a 404.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SourceCandidateEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 11, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    private static string CandidatesRoute(Guid episodeId) => $"/api/v1/episodes/{episodeId}/sources/candidates";

    private static string AddRoute(Guid episodeId, Guid candidateId) =>
        $"{CandidatesRoute(episodeId)}/{candidateId}/add";

    private static string DismissRoute(Guid episodeId, Guid candidateId) =>
        $"{CandidatesRoute(episodeId)}/{candidateId}/dismiss";

    [Fact]
    public async Task AddCandidate_AppliesSourceCaps()
    {
        var world = await SeedAsync();
        SeedSources(world, count: 10);
        var candidate = await CreateCandidateAsync(world, url: "https://example.com/the-eleventh");
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.PostAsync(AddRoute(world.EpisodeId, candidate.Id), content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("source_limit_reached");
        CandidateStatus(candidate.Id).ShouldBe("proposed", "a refused add leaves the candidate proposed");
        SourceCount(world).ShouldBe(10);
    }

    [Fact]
    public async Task AddCandidate_AppliesDailyFetchCap()
    {
        var world = await SeedAsync();
        SeedSourceCreations(world.UserId, count: 30, createdAt: Start.AddHours(-3));
        var candidate = await CreateCandidateAsync(world, url: "https://example.com/the-31st");
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.PostAsync(AddRoute(world.EpisodeId, candidate.Id), content: null, Ct);

        response.StatusCode.ShouldBe((HttpStatusCode)429, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("source_fetch_limit_reached");
        response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBe(21 * 3600);
        CandidateStatus(candidate.Id).ShouldBe("proposed");
    }

    [Fact]
    public async Task AddCandidate_Success_Returns201MarksAdded()
    {
        var world = await SeedAsync();
        var candidate = await CreateCandidateAsync(
            world,
            url: "https://example.com/research-a",
            title: "A harbour at night",
            siteName: "Example",
            rank: 2);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.PostAsync(AddRoute(world.EpisodeId, candidate.Id), content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var body = await ReadJsonAsync(response);
        body.GetProperty("kind").GetString().ShouldBe("url");
        body.GetProperty("status").GetString().ShouldBe("pending");
        body.GetProperty("registrableDomain").GetString().ShouldBe("example.com");
        var sourceId = body.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString()
            .ShouldBe($"/api/v1/episodes/{world.EpisodeId}/sources/{sourceId}");

        CandidateStatus(candidate.Id).ShouldBe("added");
        SqlProbe.ScalarText(Database, $"SELECT source_id::text FROM source_candidates WHERE id = '{candidate.Id}'")
            .ShouldBe(sourceId.ToString("D"));
        SqlProbe.Count(Database, $"SELECT count(*) FROM fetch_tasks WHERE source_id = '{sourceId}' AND status = 'queued'")
            .ShouldBe(1);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.source_candidate_added' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(1);

        using var list = await client.GetAsync(CandidatesRoute(world.EpisodeId), Ct);
        var item = (await ReadJsonAsync(list)).GetProperty("items")[0];
        item.GetProperty("status").GetString().ShouldBe("added");
        item.GetProperty("sourceId").GetGuid().ShouldBe(sourceId);
        item.GetProperty("title").GetString().ShouldBe("A harbour at night");
        item.GetProperty("rank").GetInt16().ShouldBe((short)2);
    }

    [Fact]
    public async Task AddCandidate_Twice_Returns409SourceCandidateNotProposed()
    {
        var world = await SeedAsync();
        var candidate = await CreateCandidateAsync(world, url: "https://example.com/twice");
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var first = await client.PostAsync(AddRoute(world.EpisodeId, candidate.Id), content: null, Ct);
        using var second = await client.PostAsync(AddRoute(world.EpisodeId, candidate.Id), content: null, Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.Content.ReadAsStringAsync(Ct));
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict, await second.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(second)).Code.ShouldBe("source_candidate_not_proposed");
        SourceCount(world).ShouldBe(1, "the second call adds no second source");
    }

    [Fact]
    public async Task AddCandidate_DuplicateManualSource_Returns409SourceDuplicate_StaysProposed()
    {
        var world = await SeedAsync();
        var candidate = await CreateCandidateAsync(world, url: "https://example.com/already-added");
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);
        using var manual = await client.PostAsJsonAsync(
            $"/api/v1/episodes/{world.EpisodeId}/sources",
            new { url = "https://example.com/already-added" },
            Ct);
        manual.StatusCode.ShouldBe(HttpStatusCode.Created, await manual.Content.ReadAsStringAsync(Ct));

        using var response = await client.PostAsync(AddRoute(world.EpisodeId, candidate.Id), content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("source_duplicate");
        CandidateStatus(candidate.Id).ShouldBe("proposed");
        SourceCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task AddCandidate_ErrorStatusCodes()
    {
        var world = await SeedAsync();
        var candidate = await CreateCandidateAsync(world, url: "https://youtube.com/watch?v=blocked");
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.PostAsync(AddRoute(world.EpisodeId, candidate.Id), content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("source_domain_not_allowed");
        CandidateStatus(candidate.Id).ShouldBe("proposed");
        SourceCount(world).ShouldBe(0);
    }

    [Fact]
    public async Task Candidates_OtherUser_Returns404()
    {
        var owner = await SeedAsync();
        var candidate = await CreateCandidateAsync(owner);
        var stranger = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(stranger.UserId);

        (await client.GetAsync(CandidatesRoute(owner.EpisodeId), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync(AddRoute(owner.EpisodeId, candidate.Id), content: null, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync(DismissRoute(owner.EpisodeId, candidate.Id), content: null, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        CandidateStatus(candidate.Id).ShouldBe("proposed", "a stranger's request changed nothing");
    }

    [Fact]
    public async Task Candidates_WrongEpisodeId_Returns404()
    {
        var world = await SeedAsync();
        var candidate = await CreateCandidateAsync(world);
        var otherEpisode = await new EpisodeBuilder(Database, new TestCurrentUser(), new FakeClock(Start))
            .CreateAsync(world.UserId, world.SeriesId, ct: Ct);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        // The candidate belongs to the first episode, so addressing it under the second one is a 404 on both
        // mutation routes; the second episode's own candidate list is simply empty.
        (await client.PostAsync(AddRoute(otherEpisode.Id, candidate.Id), content: null, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync(DismissRoute(otherEpisode.Id, candidate.Id), content: null, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);

        using var list = await client.GetAsync(CandidatesRoute(otherEpisode.Id), Ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(list)).GetProperty("items").GetArrayLength().ShouldBe(0);
        CandidateStatus(candidate.Id).ShouldBe("proposed");
    }

    [Fact]
    public async Task Dismiss_Proposed_Returns204()
    {
        var world = await SeedAsync();
        var candidate = await CreateCandidateAsync(world);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.PostAsync(DismissRoute(world.EpisodeId, candidate.Id), content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        CandidateStatus(candidate.Id).ShouldBe("dismissed");
        SourceCount(world).ShouldBe(0, "a dismissal adds nothing to the episode");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.source_candidate_dismissed' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Dismiss_Twice_Returns204NoOp()
    {
        var world = await SeedAsync();
        var candidate = await CreateCandidateAsync(world);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var first = await client.PostAsync(DismissRoute(world.EpisodeId, candidate.Id), content: null, Ct);
        using var second = await client.PostAsync(DismissRoute(world.EpisodeId, candidate.Id), content: null, Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent, await second.Content.ReadAsStringAsync(Ct));
        CandidateStatus(candidate.Id).ShouldBe("dismissed");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.source_candidate_dismissed' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(1, "the idempotent repeat changes nothing, so it records nothing");
    }

    [Fact]
    public async Task Dismiss_Added_Returns409()
    {
        var world = await SeedAsync();
        var candidate = await CreateCandidateAsync(world);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);
        using var added = await client.PostAsync(AddRoute(world.EpisodeId, candidate.Id), content: null, Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync(Ct));

        using var response = await client.PostAsync(DismissRoute(world.EpisodeId, candidate.Id), content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("source_candidate_not_proposed");
        CandidateStatus(candidate.Id).ShouldBe("added");
        SourceCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task List_OrdersNewestFirstThenRank()
    {
        var world = await SeedAsync();
        var olderRankTwo = await CreateCandidateAsync(world, url: "https://example.com/older-2", rank: 2);
        var olderRankOne = await CreateCandidateAsync(world, url: "https://example.com/older-1", rank: 1);
        var newerRankOne = await CreateCandidateAsync(world, url: "https://example.com/newer-1", rank: 1);

        // The three rows share the builder's clock, so the run order is pinned explicitly: the two "older" rows
        // belong to the earlier run, and rank orders them inside it.
        SqlProbe.Execute(
            Database,
            $"UPDATE source_candidates SET created_at = '{Start.AddMinutes(-30):O}' "
            + $"WHERE id IN ('{olderRankTwo.Id}', '{olderRankOne.Id}')");
        SqlProbe.Execute(
            Database,
            $"UPDATE source_candidates SET created_at = '{Start:O}' WHERE id = '{newerRankOne.Id}'");

        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.GetAsync(CandidatesRoute(world.EpisodeId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var items = (await ReadJsonAsync(response)).GetProperty("items");
        items.GetArrayLength().ShouldBe(3);
        items[0].GetProperty("id").GetGuid().ShouldBe(newerRankOne.Id);
        items[0].GetProperty("status").GetString().ShouldBe("proposed");
        items[1].GetProperty("id").GetGuid().ShouldBe(olderRankOne.Id);
        items[2].GetProperty("id").GetGuid().ShouldBe(olderRankTwo.Id);
        items[0].GetProperty("createdAt").GetDateTimeOffset().ShouldBeGreaterThan(
            items[1].GetProperty("createdAt").GetDateTimeOffset());
    }

    private ApiFactory NewFactory() => new(Database, new FakeClock(Start));

    private async Task<SourceCandidate> CreateCandidateAsync(
        World world,
        string url = SourceCandidateBuilder.DefaultUrl,
        string? title = null,
        string siteName = SourceCandidateBuilder.DefaultSiteName,
        short rank = 1) =>
        await new SourceCandidateBuilder(Database, new TestCurrentUser { Id = world.UserId }, new FakeClock(Start))
            .CreateAsync(world.UserId, world.SeriesId, world.EpisodeId, url, title: title, siteName: siteName, rank: rank, ct: Ct);

    private async Task<World> SeedAsync(EpisodeStatus status = EpisodeStatus.Draft)
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, new TestCurrentUser(), new FakeClock(Start))
            .CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, new TestCurrentUser(), new FakeClock(Start))
            .CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary>Inserts <paramref name="count"/> fetched url sources of the episode, the state the 10 cap counts.</summary>
    private void SeedSources(World world, int count)
    {
        for (var index = 0; index < count; index++)
        {
            SqlProbe.Execute(
                Database,
                "INSERT INTO story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, created_at) "
                + $"VALUES (gen_random_uuid(), '{world.UserId}', '{world.EpisodeId}', 'url', "
                + $"'https://s.example/{index}', 'https://s.example/{index}', 's.example', 'fetched', '{Start.AddHours(-2):O}')");
        }
    }

    /// <summary>Writes the rolling-window ledger rows the 24 h cap counts (<c>source_creations</c>).</summary>
    private void SeedSourceCreations(Guid userId, int count, DateTimeOffset createdAt)
    {
        for (var index = 0; index < count; index++)
        {
            SqlProbe.Execute(
                Database,
                "INSERT INTO source_creations (id, user_id, created_at) "
                + $"VALUES (gen_random_uuid(), '{userId}', '{createdAt:O}')");
        }
    }

    private long SourceCount(World world) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_sources WHERE episode_id = '{world.EpisodeId}'");

    private string CandidateStatus(Guid candidateId) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM source_candidates WHERE id = '{candidateId}'");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem.ShouldNotBeNull();

        return problem;
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
