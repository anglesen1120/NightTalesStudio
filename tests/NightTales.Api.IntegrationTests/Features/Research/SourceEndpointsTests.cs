using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Episodes;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;

using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// The four source routes of an episode against the real host and PostgreSQL 17 (02.5.1-T1 acceptance checks): add a URL or
/// a note, the D8 codes and caps, list with limits, remove with the task cascade, retry, the state gate, idempotent replay
/// and the start-up validation. Each test seeds its own user, so the shared class database cannot leak rows between tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SourceEndpointsTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    private static string Route(Guid episodeId) => $"/api/v1/episodes/{episodeId}/sources";

    private async Task<(Guid UserId, Guid EpisodeId)> SeedAsync(EpisodeStatus status = EpisodeStatus.Draft)
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, new TestCurrentUser(), new FakeClock(Start)).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, new TestCurrentUser(), new FakeClock(Start))
            .CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        return (user.Id, episode.Id);
    }

    private ApiFactory NewFactory() => new(Database, new FakeClock(Start));

    private static async Task<HttpResponseMessage> PostUrlAsync(HttpClient client, Guid episodeId, string url, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route(episodeId)) { Content = JsonContent.Create(new { url }) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> CodeOfAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        return (await ReadJsonAsync(response)).GetProperty("code").GetString()!;
    }

    private string Scalar(string sql) => SqlProbe.QueryStrings(Database, $"select ({sql})::text").ShouldHaveSingleItem();

    [Fact]
    public async Task SourceEndpoints_AddUrl_CreatesSourceAndFetchTask()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var created = await PostUrlAsync(client, episodeId, "https://vnexpress.net/a");

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var body = await ReadJsonAsync(created);
        body.GetProperty("kind").GetString().ShouldBe("url");
        body.GetProperty("status").GetString().ShouldBe("pending");
        body.GetProperty("registrableDomain").GetString().ShouldBe("vnexpress.net");
        var sourceId = body.GetProperty("id").GetGuid();
        created.Headers.Location!.ToString().ShouldBe($"{Route(episodeId)}/{sourceId}");

        using var list = await client.GetAsync(Route(episodeId), Ct);
        var items = (await ReadJsonAsync(list)).GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        items[0].GetProperty("status").GetString().ShouldBe("pending");

        Scalar($"select count(*) from fetch_tasks where source_id='{sourceId}' and status='queued'").ShouldBe("1");
    }

    [Fact]
    public async Task SourceEndpoints_AddUrl_BlockedDomain_Returns400WithMessage()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PostUrlAsync(client, episodeId, "https://youtube.com/watch?v=x");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadJsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("source_domain_not_allowed");
        problem.ToString().ShouldContain("video and social platforms");
        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("0");
    }

    [Theory]
    [InlineData("http://2130706433/")]
    [InlineData("https://user:pw@example.com/")]
    public async Task SourceEndpoints_AddUrl_InvalidUrl_Returns400(string url)
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PostUrlAsync(client, episodeId, url);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOfAsync(response)).ShouldBe("source_url_invalid");
    }

    [Fact]
    public async Task SourceEndpoints_Add_BodyShapeErrors_Return400ValidationFailed()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        foreach (var body in new object[]
        {
            new { },
            new { url = "https://example.com/a", noteText = "both" },
            new { noteText = "   " },
            new { url = "https://example.com/" + new string('a', 2_100) },
        })
        {
            using var response = await client.PostAsJsonAsync(Route(episodeId), body, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, JsonSerializer.Serialize(body));
            (await CodeOfAsync(response)).ShouldBe("validation_failed");
        }

        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("0");
    }

    [Fact]
    public async Task SourceEndpoints_Add_DuplicateUrl_Returns409_ForNormalisedVariants()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        (await PostUrlAsync(client, episodeId, "https://example.com/a")).StatusCode.ShouldBe(HttpStatusCode.Created);

        foreach (var variant in new[] { "https://example.com/a", "https://example.com/a#frag", "https://EXAMPLE.com:443/a" })
        {
            using var response = await PostUrlAsync(client, episodeId, variant);

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, variant);
            (await CodeOfAsync(response)).ShouldBe("source_duplicate");
        }
    }

    [Fact]
    public async Task SourceEndpoints_Add_ExceedsEpisodeCap_Returns409()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        for (var index = 0; index < 10; index++)
        {
            (await PostUrlAsync(client, episodeId, $"https://example.com/{index}")).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var eleventh = await PostUrlAsync(client, episodeId, "https://example.com/11");

        eleventh.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(eleventh)).ShouldBe("source_limit_reached");
    }

    [Fact]
    public async Task SourceEndpoints_Add_ExceedsDailyCap_Returns429WithRetryAfter()
    {
        var (userId, episodeId) = await SeedAsync();
        var series = await new SeriesBuilder(Database, new TestCurrentUser(), new FakeClock(Start)).CreateAsync(userId, ct: Ct);
        for (var batch = 0; batch < 3; batch++)
        {
            var other = (await new EpisodeBuilder(Database, new TestCurrentUser()).CreateAsync(userId, series.Id, ct: Ct)).Id;
            for (var index = 0; index < 10; index++)
            {
                SqlProbe.Execute(
                    Database,
                    "insert into story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, created_at) "
                    + $"values (gen_random_uuid(), '{userId}', '{other}', 'url', 'https://s.example/{batch}-{index}', "
                    + $"'https://s.example/{batch}-{index}', 's.example', 'fetched', '{Start.AddHours(-3):O}')");
            }
        }

        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PostUrlAsync(client, episodeId, "https://example.com/the-31st");

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CodeOfAsync(response)).ShouldBe("source_fetch_limit_reached");
        response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBe(21 * 3600, "the oldest counted row leaves the window in 21 h");
    }

    [Fact]
    public async Task SourceEndpoints_AddNote_CreatesFetchedNoteWithoutTask()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await client.PostAsJsonAsync(Route(episodeId), new { noteText = "I watched the documentary" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var body = await ReadJsonAsync(response);
        body.GetProperty("kind").GetString().ShouldBe("user_note");
        body.GetProperty("status").GetString().ShouldBe("fetched");
        body.GetProperty("contentSha256").GetString()!.Length.ShouldBe(64);
        body.GetProperty("textChars").GetInt32().ShouldBe("I watched the documentary".Length);
        Scalar($"select count(*) from fetch_tasks where episode_id='{episodeId}'").ShouldBe("0");
    }

    [Fact]
    public async Task SourceEndpoints_Delete_RemovesFetchTaskByCascade()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        var sourceId = (await ReadJsonAsync(await PostUrlAsync(client, episodeId, "https://example.com/a"))).GetProperty("id").GetGuid();

        using var deleted = await client.DeleteAsync($"{Route(episodeId)}/{sourceId}", Ct);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Scalar($"select count(*) from fetch_tasks where source_id='{sourceId}'").ShouldBe("0");
        using var second = await client.DeleteAsync($"{Route(episodeId)}/{sourceId}", Ct);
        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SourceEndpoints_Retry_FailedSourceIs202WithNewTask_FetchedIs409()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        var sourceId = (await ReadJsonAsync(await PostUrlAsync(client, episodeId, "https://example.com/a"))).GetProperty("id").GetGuid();
        var noteId = (await ReadJsonAsync(await client.PostAsJsonAsync(Route(episodeId), new { noteText = "a note" }, Ct))).GetProperty("id").GetGuid();
        SqlProbe.Execute(Database, $"update story_sources set status='failed', error_code='source_unreachable' where id='{sourceId}'");
        SqlProbe.Execute(Database, $"update fetch_tasks set status='failed', error_code='source_unreachable' where source_id='{sourceId}'");

        using var retried = await client.PostAsync($"{Route(episodeId)}/{sourceId}/retry", null, Ct);

        retried.StatusCode.ShouldBe(HttpStatusCode.Accepted, await retried.Content.ReadAsStringAsync(Ct));
        (await ReadJsonAsync(retried)).GetProperty("status").GetString().ShouldBe("pending");
        Scalar($"select count(*) from fetch_tasks where source_id='{sourceId}' and status='queued'").ShouldBe("1");

        using var notFailed = await client.PostAsync($"{Route(episodeId)}/{noteId}/retry", null, Ct);
        notFailed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(notFailed)).ShouldBe("episode_invalid_state");
    }

    [Fact]
    public async Task SourceEndpoints_OnApprovedEpisode_MutationsAre409_ListStillWorks()
    {
        var (userId, episodeId) = await SeedAsync(EpisodeStatus.StoryApproved);
        var source = Guid.NewGuid();
        SqlProbe.Execute(
            Database,
            "insert into story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, created_at) "
            + $"values ('{source}', '{userId}', '{episodeId}', 'url', 'https://e.example/a', 'https://e.example/a', 'e.example', 'failed', now())");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        foreach (var response in new[]
        {
            await PostUrlAsync(client, episodeId, "https://example.com/a"),
            await client.DeleteAsync($"{Route(episodeId)}/{source}", Ct),
            await client.PostAsync($"{Route(episodeId)}/{source}/retry", null, Ct),
        })
        {
            using (response)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                (await CodeOfAsync(response)).ShouldBe("episode_invalid_state");
            }
        }

        using var list = await client.GetAsync(Route(episodeId), Ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(list)).GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task SourceEndpoints_Add_IsIdempotent()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var first = await PostUrlAsync(client, episodeId, "https://example.com/a", key: "key-1");
        using var replay = await PostUrlAsync(client, episodeId, "https://example.com/a", key: "key-1");

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.StatusCode.ShouldBe(HttpStatusCode.Created, await replay.Content.ReadAsStringAsync(Ct));
        (await ReadJsonAsync(replay)).GetProperty("id").GetGuid().ShouldBe((await ReadJsonAsync(first)).GetProperty("id").GetGuid());
        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("1");

        using var reused = await PostUrlAsync(client, episodeId, "https://example.com/other", key: "key-1");
        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOfAsync(reused)).ShouldBe("idempotency_key_reused");

        using var badKey = await PostUrlAsync(client, episodeId, "https://example.com/b", key: new string('k', 129));
        badKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOfAsync(badKey)).ShouldBe("idempotency_key_required");
    }

    [Fact]
    public async Task SourceEndpoints_List_ReturnsLimitsBlock()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        (await PostUrlAsync(client, episodeId, "https://example.com/a")).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.PostAsJsonAsync(Route(episodeId), new { noteText = "n" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var list = await client.GetAsync(Route(episodeId), Ct);

        var body = await ReadJsonAsync(list);
        var limits = body.GetProperty("limits");
        limits.GetProperty("maxPerEpisode").GetInt32().ShouldBe(10);
        limits.GetProperty("urlSourcesLast24h").GetInt32().ShouldBe(1, "notes do not count");
        limits.GetProperty("maxUrlSourcesPerDay").GetInt32().ShouldBe(30);
        body.GetProperty("items").GetArrayLength().ShouldBe(2);
        var raw = body.GetProperty("items").ToString();
        raw.ShouldNotContain("normalizedUrl");
        raw.ShouldNotContain("contentSha256");
        body.GetProperty("items")[0].GetProperty("isCited").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task SourceEndpoints_OtherUsersEpisode_Returns404OnEveryRoute()
    {
        var (_, episodeId) = await SeedAsync();
        var (strangerId, _) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(strangerId);

        (await client.GetAsync(Route(episodeId), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostUrlAsync(client, episodeId, "https://example.com/a")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"{Route(episodeId)}/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync($"{Route(episodeId)}/{Guid.NewGuid()}/retry", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SourceEndpoints_Anonymous_Returns401()
    {
        var (_, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync(Route(episodeId), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SourceEndpoints_ConcurrentTenthAndEleventh_OnlyOneSucceeds()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        for (var index = 0; index < 9; index++)
        {
            (await PostUrlAsync(client, episodeId, $"https://example.com/{index}")).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var results = await Task.WhenAll(
            PostUrlAsync(client, episodeId, "https://example.com/ten"),
            PostUrlAsync(client, episodeId, "https://example.com/eleven"));

        results.Select(response => response.StatusCode).Order().ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("10");
        foreach (var response in results)
        {
            response.Dispose();
        }
    }

    [Theory]
    [InlineData("Fetcher:BlockedDomains:0", "https://youtube.com", "Fetcher:BlockedDomains")]
    [InlineData("Fetcher:BlockedDomains:0", "localhost", "Fetcher:BlockedDomains")]
    [InlineData("Sources:MaxPerEpisode", "0", "Sources:MaxPerEpisode")]
    public void Startup_BadSourcesOrBlocklistConfiguration_FailsWithANamedError(string key, string value, string named)
    {
        using var factory = NewFactory().WithSetting(key, value);

        var failure = Should.Throw<Exception>(() => factory.CreateClient());

        Flatten(failure).ShouldContain(named);
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            messages.Add(current.Message);
            if (current.InnerException is null)
            {
                break;
            }
        }

        return string.Join(" | ", messages);
    }
}
