using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Review fixes of 02.5.1-T1 (group A) against the real host and PostgreSQL 17: the 24 h url cap holds across
/// <em>different</em> episodes of one user under parallel adds and retries (CR-1/SEC-1, the per-user advisory lock after
/// the episode lock), and a retry re-checks the domain blocklist (SEC-2). Each test seeds its own user.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SourceCapsReviewTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    private static string Route(Guid episodeId) => $"/api/v1/episodes/{episodeId}/sources";

    private ApiFactory NewFactory() => new(Database, new FakeClock(Start));

    private async Task<(Guid UserId, Guid SeriesId)> SeedUserAsync()
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, new TestCurrentUser(), new FakeClock(Start)).CreateAsync(user.Id, ct: Ct);

        return (user.Id, series.Id);
    }

    private async Task<Guid> NewEpisodeAsync(Guid userId, Guid seriesId) =>
        (await new EpisodeBuilder(Database, new TestCurrentUser(), new FakeClock(Start))
            .CreateAsync(userId, seriesId, status: EpisodeStatus.Draft, ct: Ct)).Id;

    private string Scalar(string sql) => SqlProbe.QueryStrings(Database, $"select ({sql})::text").ShouldHaveSingleItem();

    /// <summary>Appends <paramref name="count"/> ledger rows inside the window, as <paramref name="count"/> earlier url-source creations.</summary>
    private void SeedCreations(Guid userId, int count) =>
        SqlProbe.Execute(
            Database,
            "insert into source_creations (id, user_id, created_at) "
            + $"select gen_random_uuid(), '{userId}', '{Start:O}' from generate_series(1, {count})");

    /// <summary>A <c>failed</c> url source of <paramref name="episodeId"/> with no live task, ready to be retried.</summary>
    private Guid SeedFailedSource(Guid userId, Guid episodeId, string domain)
    {
        var id = Guid.NewGuid();
        var url = $"https://{domain}/{id:N}";
        SqlProbe.Execute(
            Database,
            "insert into story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, error_code, created_at) "
            + $"values ('{id}', '{userId}', '{episodeId}', 'url', '{url}', '{url}', '{domain}', 'failed', 'source_unreachable', '{Start:O}')");

        return id;
    }

    private static async Task<string> CodeOfAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("code").GetString()!;

    [Fact]
    public async Task UrlCap_ParallelAddsOnDifferentEpisodes_AdmitExactlyTheLastSlot()
    {
        const int Parallel = 8;
        var (userId, seriesId) = await SeedUserAsync();
        SeedCreations(userId, 29);
        var episodes = new List<Guid>();
        for (var index = 0; index < Parallel; index++)
        {
            episodes.Add(await NewEpisodeAsync(userId, seriesId));
        }

        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        var responses = await Task.WhenAll(episodes.Select((episodeId, index) =>
            client.PostAsJsonAsync(Route(episodeId), new { url = $"https://example.com/{index}" }, Ct)));

        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
            var rejected = responses.Where(response => response.StatusCode != HttpStatusCode.Created).ToList();
            rejected.Count.ShouldBe(Parallel - 1);
            foreach (var response in rejected)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
                (await CodeOfAsync(response)).ShouldBe("source_fetch_limit_reached");
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Scalar($"select count(*) from source_creations where user_id='{userId}'").ShouldBe("30");
        Scalar($"select count(*) from story_sources where user_id='{userId}'").ShouldBe("1");
        Scalar($"select count(*) from fetch_tasks where user_id='{userId}'").ShouldBe("1");
    }

    [Fact]
    public async Task UrlCap_ParallelAddAndRetryOnDifferentEpisodes_AdmitExactlyOne()
    {
        var (userId, seriesId) = await SeedUserAsync();
        SeedCreations(userId, 29);
        var addEpisode = await NewEpisodeAsync(userId, seriesId);
        var retryEpisode = await NewEpisodeAsync(userId, seriesId);
        var failedSource = SeedFailedSource(userId, retryEpisode, "retry-cap.example");

        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        var add = client.PostAsJsonAsync(Route(addEpisode), new { url = "https://example.com/added" }, Ct);
        var retry = client.PostAsync($"{Route(retryEpisode)}/{failedSource}/retry", null, Ct);
        using var addResponse = await add;
        using var retryResponse = await retry;

        var statuses = new[] { addResponse.StatusCode, retryResponse.StatusCode };
        statuses.Count(status => status is HttpStatusCode.Created or HttpStatusCode.Accepted).ShouldBe(1, string.Join(", ", statuses));
        statuses.Count(status => status == HttpStatusCode.TooManyRequests).ShouldBe(1, string.Join(", ", statuses));
        Scalar($"select count(*) from source_creations where user_id='{userId}'").ShouldBe("30");
    }

    [Fact]
    public async Task Retry_DomainBlockedSinceTheSourceWasAdded_Returns400AndCreatesNoTask()
    {
        const string Domain = "later-blocked.example";
        var (userId, seriesId) = await SeedUserAsync();
        var blockedEpisode = await NewEpisodeAsync(userId, seriesId);
        var blockedSource = SeedFailedSource(userId, blockedEpisode, Domain);
        var allowedEpisode = await NewEpisodeAsync(userId, seriesId);
        var allowedSource = SeedFailedSource(userId, allowedEpisode, "still-allowed.example");

        // The shipped list has 19 entries (appsettings.json), so index 19 appends one: the domain is blocked now, after the add.
        using var factory = NewFactory().WithSetting("Fetcher:BlockedDomains:19", Domain);
        using var client = factory.ClientFor(userId);

        using var blocked = await client.PostAsync($"{Route(blockedEpisode)}/{blockedSource}/retry", null, Ct);

        blocked.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await blocked.Content.ReadAsStringAsync(Ct));
        (await CodeOfAsync(blocked)).ShouldBe("source_domain_not_allowed");
        Scalar($"select status from story_sources where id='{blockedSource}'").ShouldBe("failed");
        Scalar($"select count(*) from fetch_tasks where source_id='{blockedSource}'").ShouldBe("0");
        Scalar($"select count(*) from source_creations where user_id='{userId}'").ShouldBe("0");

        // The control: an allowed domain on the same host retries normally, so the 400 above is the blocklist and nothing else.
        using var allowed = await client.PostAsync($"{Route(allowedEpisode)}/{allowedSource}/retry", null, Ct);

        allowed.StatusCode.ShouldBe(HttpStatusCode.Accepted, await allowed.Content.ReadAsStringAsync(Ct));
        Scalar($"select count(*) from fetch_tasks where source_id='{allowedSource}' and status='queued'").ShouldBe("1");
        Scalar($"select count(*) from source_creations where user_id='{userId}'").ShouldBe("1");
    }
}
