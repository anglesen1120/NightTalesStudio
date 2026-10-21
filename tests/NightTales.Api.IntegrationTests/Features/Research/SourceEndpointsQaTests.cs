using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
/// QA probes for 02.5.1-T1, written by an agent that did not write the code: exact cap boundaries, notes, tenant
/// isolation of source ids, the state gate over every episode state, retry from every status, transaction atomicity,
/// audit/log privacy (02 D19, D13) and malformed bodies. Expectations come from the task file and 02 README D8/D13/D19.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SourceEndpointsQaTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    private static string Route(Guid episodeId) => $"/api/v1/episodes/{episodeId}/sources";

    private async Task<(Guid UserId, Guid SeriesId, Guid EpisodeId)> SeedAsync(EpisodeStatus status = EpisodeStatus.Draft)
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, new TestCurrentUser(), new FakeClock(Start)).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, new TestCurrentUser(), new FakeClock(Start))
            .CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        return (user.Id, series.Id, episode.Id);
    }

    private async Task<Guid> NewEpisodeAsync(Guid userId, Guid seriesId) =>
        (await new EpisodeBuilder(Database, new TestCurrentUser(), new FakeClock(Start)).CreateAsync(userId, seriesId, ct: Ct)).Id;

    private ApiFactory NewFactory() => new(Database, new FakeClock(Start));

    private static async Task<HttpResponseMessage> PostUrlAsync(HttpClient client, Guid episodeId, string url) =>
        await client.PostAsJsonAsync(Route(episodeId), new { url }, Ct);

    private static async Task<HttpResponseMessage> PostNoteAsync(HttpClient client, Guid episodeId, string noteText) =>
        await client.PostAsJsonAsync(Route(episodeId), new { noteText }, Ct);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<string> CodeOfAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("code").GetString()!;

    private string Scalar(string sql) => SqlProbe.QueryStrings(Database, $"select ({sql})::text").ShouldHaveSingleItem();

    private void SeedUrlRows(Guid userId, Guid episodeId, int count, DateTimeOffset createdAt, string status = "fetched", string tag = "s")
    {
        for (var index = 0; index < count; index++)
        {
            var url = $"https://{tag}.example/{Guid.NewGuid():N}";
            SqlProbe.Execute(
                Database,
                "insert into story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, created_at) "
                + $"values (gen_random_uuid(), '{userId}', '{episodeId}', 'url', '{url}', '{url}', '{tag}.example', '{status}', '{createdAt:O}')");

            // The cap counts creations (CR-31), so a seeded url source is also one ledger row.
            SqlProbe.Execute(
                Database,
                $"insert into source_creations (id, user_id, created_at) values (gen_random_uuid(), '{userId}', '{createdAt:O}')");
        }
    }

    private async Task SeedDailyAsync(Guid userId, Guid seriesId, int count, DateTimeOffset createdAt)
    {
        var remaining = count;
        while (remaining > 0)
        {
            var episode = await NewEpisodeAsync(userId, seriesId);
            var batch = Math.Min(10, remaining);
            SeedUrlRows(userId, episode, batch, createdAt);
            remaining -= batch;
        }
    }

    // ------------------------------------------------------------------ episode cap (10th / 11th, notes count)

    [Fact]
    public async Task EpisodeCap_TenthUrlSucceeds_EleventhIs409_AndNothingIsPersistedForIt()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        for (var index = 1; index <= 10; index++)
        {
            using var ok = await PostUrlAsync(client, episodeId, $"https://example.com/{index}");
            ok.StatusCode.ShouldBe(HttpStatusCode.Created, $"source {index}");
        }

        using var eleventh = await PostUrlAsync(client, episodeId, "https://example.com/11");
        eleventh.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(eleventh)).ShouldBe("source_limit_reached");
        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("10");
        Scalar($"select count(*) from fetch_tasks where episode_id='{episodeId}'").ShouldBe("10");
    }

    [Fact]
    public async Task EpisodeCap_NotesConsumeSlots_AndAnEleventhNoteIs409()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        for (var index = 1; index <= 5; index++)
        {
            (await PostUrlAsync(client, episodeId, $"https://example.com/{index}")).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        for (var index = 1; index <= 5; index++)
        {
            (await PostNoteAsync(client, episodeId, "same note text")).StatusCode.ShouldBe(HttpStatusCode.Created, $"note {index}: two notes may be identical");
        }

        using var note = await PostNoteAsync(client, episodeId, "one too many");
        note.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(note)).ShouldBe("source_limit_reached");

        using var url = await PostUrlAsync(client, episodeId, "https://example.com/extra");
        url.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task EpisodeCap_IsPerEpisode_AndTheSameUrlMayLiveOnTwoEpisodes()
    {
        var (userId, seriesId, first) = await SeedAsync();
        var second = await NewEpisodeAsync(userId, seriesId);
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        (await PostUrlAsync(client, first, "https://example.com/shared")).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PostUrlAsync(client, second, "https://example.com/shared")).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task EpisodeCap_BlockedDomainAndInvalidUrlWinOverTheCaps()
    {
        var (userId, _, episodeId) = await SeedAsync();
        SeedUrlRows(userId, episodeId, 10, Start.AddHours(-1));
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var blocked = await PostUrlAsync(client, episodeId, "https://m.youtube.com/watch?v=x");
        blocked.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOfAsync(blocked)).ShouldBe("source_domain_not_allowed");

        using var invalid = await PostUrlAsync(client, episodeId, "http://127.0.0.1/");
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOfAsync(invalid)).ShouldBe("source_url_invalid");
    }

    // ------------------------------------------------------------------ rolling 24 h cap boundaries

    [Fact]
    public async Task DailyCap_ThirtiethUrlSucceeds_ThirtyFirstIs429_WithPositiveRetryAfter()
    {
        var (userId, seriesId, episodeId) = await SeedAsync();
        await SeedDailyAsync(userId, seriesId, 29, Start.AddHours(-2));
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var thirtieth = await PostUrlAsync(client, episodeId, "https://example.com/the-30th");
        thirtieth.StatusCode.ShouldBe(HttpStatusCode.Created, await thirtieth.Content.ReadAsStringAsync(Ct));

        using var thirtyFirst = await PostUrlAsync(client, episodeId, "https://example.com/the-31st");
        thirtyFirst.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CodeOfAsync(thirtyFirst)).ShouldBe("source_fetch_limit_reached");
        thirtyFirst.Headers.RetryAfter.ShouldNotBeNull();
        thirtyFirst.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeGreaterThan(0);
        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("1");
    }

    [Fact]
    public async Task DailyCap_RowsExactlyTwentyFourHoursOld_AreOutsideTheWindow()
    {
        var (userId, seriesId, episodeId) = await SeedAsync();
        await SeedDailyAsync(userId, seriesId, 30, Start.AddHours(-24));
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PostUrlAsync(client, episodeId, "https://example.com/fresh");

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task DailyCap_RowsJustInsideTheWindow_GiveRetryAfterOfAboutTheRemainder()
    {
        var (userId, seriesId, episodeId) = await SeedAsync();
        await SeedDailyAsync(userId, seriesId, 30, Start.AddHours(-24).AddSeconds(2));
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PostUrlAsync(client, episodeId, "https://example.com/blocked");

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBe(2);
    }

    [Fact]
    public async Task DailyCap_NotesAreExcluded_AndOtherUsersRowsAreNotCounted()
    {
        var (userId, seriesId, episodeId) = await SeedAsync();
        for (var episodeIndex = 0; episodeIndex < 3; episodeIndex++)
        {
            var episode = await NewEpisodeAsync(userId, seriesId);
            for (var index = 0; index < 10; index++)
            {
                SqlProbe.Execute(
                    Database,
                    "insert into story_sources (id, user_id, episode_id, kind, note_text, status, content_sha256, text_chars, created_at, fetched_at) "
                    + $"values (gen_random_uuid(), '{userId}', '{episode}', 'user_note', 'n', 'fetched', repeat('a', 64), 1, '{Start.AddHours(-1):O}', '{Start.AddHours(-1):O}')");
            }
        }

        var (strangerId, strangerSeries, _) = await SeedAsync();
        await SeedDailyAsync(strangerId, strangerSeries, 30, Start.AddHours(-1));

        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        (await PostUrlAsync(client, episodeId, "https://example.com/a")).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task DailyCap_RetryOfAFailedSource_ReappliesTheCap_With429()
    {
        var (userId, seriesId, episodeId) = await SeedAsync();
        await SeedDailyAsync(userId, seriesId, 29, Start.AddHours(-1));
        var failed = Guid.NewGuid();
        SqlProbe.Execute(
            Database,
            "insert into story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, error_code, created_at) "
            + $"values ('{failed}', '{userId}', '{episodeId}', 'url', 'https://f.example/a', 'https://f.example/a', 'f.example', 'failed', 'source_unreachable', '{Start.AddHours(-1):O}')");
        SqlProbe.Execute(
            Database,
            $"insert into source_creations (id, user_id, created_at) values (gen_random_uuid(), '{userId}', '{Start.AddHours(-1):O}')");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await client.PostAsync($"{Route(episodeId)}/{failed}/retry", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CodeOfAsync(response)).ShouldBe("source_fetch_limit_reached");
        response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeGreaterThan(0);
        Scalar($"select status from story_sources where id='{failed}'").ShouldBe("failed");
        Scalar($"select count(*) from fetch_tasks where source_id='{failed}'").ShouldBe("0");
    }

    /// <summary>
    /// QA-1 (F15 "cap counted on creations in rolling 24 h", CR-31): deleting sources must not free quota, otherwise a
    /// user could create unbounded url sources (each one a fetch) per day by add-delete cycles.
    /// </summary>
    [Fact]
    public async Task DailyCap_DeletingSourcesDoesNotFreeQuota()
    {
        var (userId, seriesId, episodeId) = await SeedAsync();
        await SeedDailyAsync(userId, seriesId, 29, Start.AddHours(-1));
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        var created = await ReadJsonAsync(await PostUrlAsync(client, episodeId, "https://example.com/30th"));
        (await client.DeleteAsync($"{Route(episodeId)}/{created.GetProperty("id").GetGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var again = await PostUrlAsync(client, episodeId, "https://example.com/30th-again");

        again.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "30 url sources were created in the window");
    }

    // ------------------------------------------------------------------ notes

    [Fact]
    public async Task Note_Exactly3000Characters_Succeeds_3001Is400()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var ok = await PostNoteAsync(client, episodeId, new string('n', 3000));
        ok.StatusCode.ShouldBe(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync(Ct));
        (await ReadJsonAsync(ok)).GetProperty("textChars").GetInt32().ShouldBe(3000);

        using var over = await PostNoteAsync(client, episodeId, new string('n', 3001));
        over.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("1");
    }

    [Fact]
    public async Task Note_IsTrimmed_ThenCountedAndHashed()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PostNoteAsync(client, episodeId, "  padded note \n");

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var body = await ReadJsonAsync(response);
        body.GetProperty("noteText").GetString().ShouldBe("padded note");
        body.GetProperty("textChars").GetInt32().ShouldBe("padded note".Length);
        body.GetProperty("contentSha256").GetString().ShouldBe(Scalar("encode(sha256(convert_to('padded note', 'UTF8')), 'hex')"));
        Scalar($"select count(*) from fetch_tasks where episode_id='{episodeId}'").ShouldBe("0");
        body.GetProperty("fetchedAt").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Note_NfcNormalisedBeforeHashing()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        var decomposed = "Cafe\u0301";
        var composed = "Caf\u00E9";

        var first = await ReadJsonAsync(await PostNoteAsync(client, episodeId, decomposed));
        var second = await ReadJsonAsync(await PostNoteAsync(client, episodeId, composed));

        first.GetProperty("contentSha256").GetString().ShouldBe(second.GetProperty("contentSha256").GetString(), "sha256(UTF8(NFC(note)))");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"url\":null,\"noteText\":null}")]
    [InlineData("{\"url\":\"\",\"noteText\":\"\"}")]
    [InlineData("{\"noteText\":\"   \"}")]
    [InlineData("{\"url\":\"   \"}")]
    [InlineData("{\"url\":\"https://example.com/a\",\"noteText\":\"both\"}")]
    [InlineData("{\"url\":\"https://example.com/a\",\"noteText\":\"  \"}")]
    public async Task Add_WrongShape_Is400_NeverAPersistedRow(string json)
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(Route(episodeId), content, Ct);

        if (json.Contains("\"  \"", StringComparison.Ordinal) && json.Contains("https://example.com/a", StringComparison.Ordinal))
        {
            // A whitespace-only note next to a URL is "exactly one of the two": the URL wins.
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            return;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("0");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"url\":123}")]
    [InlineData("{\"url\":[\"https://example.com\"]}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    public async Task Add_MalformedBody_Is4xx_Not500(string json)
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(Route(episodeId), content, Ct);

        ((int)response.StatusCode).ShouldBeInRange(400, 499, await response.Content.ReadAsStringAsync(Ct));
        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("0");
    }

    [Fact]
    public async Task Add_UrlOf2048Accepted_2049Rejected400_HugeUrlRejectedWithoutA500()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        const string prefix = "https://example.com/";

        using var exact = await PostUrlAsync(client, episodeId, prefix + new string('a', 2048 - prefix.Length));
        exact.StatusCode.ShouldBe(HttpStatusCode.Created, await exact.Content.ReadAsStringAsync(Ct));

        using var over = await PostUrlAsync(client, episodeId, prefix + new string('a', 2049 - prefix.Length));
        over.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var huge = await PostUrlAsync(client, episodeId, prefix + new string('b', 1_000_000));
        ((int)huge.StatusCode).ShouldBeInRange(400, 499);
    }

    // ------------------------------------------------------------------ duplicates and normalisation

    [Theory]
    [InlineData("https://example.com/a#frag")]
    [InlineData("HTTPS://EXAMPLE.COM/a")]
    [InlineData("https://example.com:443/a")]
    [InlineData("https://example.com./a")]
    [InlineData("  https://example.com/a  ")]
    public async Task Duplicate_NormalisedVariants_Are409(string variant)
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        (await PostUrlAsync(client, episodeId, "https://example.com/a")).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var response = await PostUrlAsync(client, episodeId, variant);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe("source_duplicate");
        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("1");
        Scalar($"select count(*) from fetch_tasks where episode_id='{episodeId}'").ShouldBe("1");
    }

    [Theory]
    [InlineData("https://example.com/a?x=1")]
    [InlineData("https://example.com/A")]
    [InlineData("http://example.com/a")]
    [InlineData("https://www.example.com/a")]
    public async Task NotDuplicate_QueryPathCaseSchemeAndSubdomainDiffer_Are201(string other)
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        (await PostUrlAsync(client, episodeId, "https://example.com/a")).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var response = await PostUrlAsync(client, episodeId, other);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Duplicate_ParallelSameUrl_OneCreatedOneConflict_OneTask()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        var results = await Task.WhenAll(
            PostUrlAsync(client, episodeId, "https://example.com/race"),
            PostUrlAsync(client, episodeId, "https://example.com/race#x"));

        results.Select(result => result.StatusCode).Order().ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        Scalar($"select count(*) from fetch_tasks where episode_id='{episodeId}'").ShouldBe("1");
        foreach (var result in results)
        {
            result.Dispose();
        }
    }

    [Fact]
    public async Task Add_Url_ResponseShape_Has201Location_NormalisedUrl_AndNoLeakOfOtherFields()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PostUrlAsync(client, episodeId, "HTTP://WWW.VnExpress.NET:80/Path?Q=1#frag");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await ReadJsonAsync(response);
        var id = body.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().ShouldEndWith($"/sources/{id}");
        body.GetProperty("kind").GetString().ShouldBe("url");
        body.GetProperty("status").GetString().ShouldBe("pending");
        body.GetProperty("normalizedUrl").GetString().ShouldBe("http://www.vnexpress.net/Path?Q=1");
        body.GetProperty("registrableDomain").GetString().ShouldBe("vnexpress.net");
        Scalar($"select url from story_sources where id='{id}'").ShouldNotContain("#frag");
        var task = SqlProbe.QueryStrings(
            Database,
            $"select concat_ws('|', kind, status, attempts::text, url, registrable_domain, (not_before = '{Start:O}'::timestamptz)::text) from fetch_tasks where source_id='{id}'")
            .ShouldHaveSingleItem();
        task.ShouldBe("fetch_source|queued|0|http://www.vnexpress.net/Path?Q=1|vnexpress.net|true");
    }

    // ------------------------------------------------------------------ tenant isolation of source ids

    [Fact]
    public async Task Tenant_StrangerWithOwnEpisodeAndOwnersSourceId_Gets404_AndSourceSurvives()
    {
        var (ownerId, _, ownerEpisode) = await SeedAsync();
        var (strangerId, _, strangerEpisode) = await SeedAsync();
        using var factory = NewFactory();
        using var owner = factory.ClientFor(ownerId);
        var sourceId = (await ReadJsonAsync(await PostUrlAsync(owner, ownerEpisode, "https://example.com/secret"))).GetProperty("id").GetGuid();
        SqlProbe.Execute(Database, $"update story_sources set status='failed', error_code='source_unreachable' where id='{sourceId}'");
        using var stranger = factory.ClientFor(strangerId);

        using var crossDelete = await stranger.DeleteAsync($"{Route(strangerEpisode)}/{sourceId}", Ct);
        using var crossRetry = await stranger.PostAsync($"{Route(strangerEpisode)}/{sourceId}/retry", null, Ct);
        using var wrongEpisodeDelete = await stranger.DeleteAsync($"{Route(ownerEpisode)}/{sourceId}", Ct);
        using var list = await stranger.GetAsync(Route(strangerEpisode), Ct);

        crossDelete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        crossRetry.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        wrongEpisodeDelete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(list)).GetProperty("items").GetArrayLength().ShouldBe(0);
        Scalar($"select count(*) from story_sources where id='{sourceId}'").ShouldBe("1");
        Scalar($"select count(*) from fetch_tasks where source_id='{sourceId}' and status='queued'").ShouldBe("1");
    }

    [Fact]
    public async Task Tenant_OwnersOwnSourceOnAnotherOfTheirEpisodes_Is404()
    {
        var (userId, seriesId, first) = await SeedAsync();
        var second = await NewEpisodeAsync(userId, seriesId);
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        var sourceId = (await ReadJsonAsync(await PostUrlAsync(client, first, "https://example.com/a"))).GetProperty("id").GetGuid();

        using var response = await client.DeleteAsync($"{Route(second)}/{sourceId}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Routes_NonGuidIds_AreNotFound_NotServerErrors()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        (await client.GetAsync("/api/v1/episodes/not-a-guid/sources", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"{Route(episodeId)}/not-a-guid", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync($"{Route(episodeId)}/not-a-guid/retry", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ state gate over every state

    [Theory]
    [InlineData(EpisodeStatus.Draft, true)]
    [InlineData(EpisodeStatus.StoryReview, true)]
    [InlineData(EpisodeStatus.StoryGenerating, false)]
    [InlineData(EpisodeStatus.StoryApproved, false)]
    [InlineData(EpisodeStatus.Rendering, false)]
    [InlineData(EpisodeStatus.VideoReview, false)]
    [InlineData(EpisodeStatus.Ready, false)]
    [InlineData(EpisodeStatus.Scheduled, false)]
    [InlineData(EpisodeStatus.Published, false)]
    [InlineData(EpisodeStatus.Failed, false)]
    public async Task StateGate_OnlyDraftAndStoryReviewAcceptSources(EpisodeStatus status, bool allowed)
    {
        var (userId, _, episodeId) = await SeedAsync(status);
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var url = await PostUrlAsync(client, episodeId, "https://example.com/a");
        using var note = await PostNoteAsync(client, episodeId, "a note");
        using var list = await client.GetAsync(Route(episodeId), Ct);

        list.StatusCode.ShouldBe(HttpStatusCode.OK, "GET stays available in every state");
        if (allowed)
        {
            url.StatusCode.ShouldBe(HttpStatusCode.Created);
            note.StatusCode.ShouldBe(HttpStatusCode.Created);
        }
        else
        {
            url.StatusCode.ShouldBe(HttpStatusCode.Conflict, status.ToString());
            (await CodeOfAsync(url)).ShouldBe("episode_invalid_state");
            note.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("0");
        }
    }

    [Fact]
    public async Task StateGate_BlockedDomainOnWrongState_IsTheStateError_NotTheDomainError()
    {
        var (userId, _, episodeId) = await SeedAsync(EpisodeStatus.Published);
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PostUrlAsync(client, episodeId, "https://youtube.com/watch?v=x");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe("episode_invalid_state");
    }

    // ------------------------------------------------------------------ DELETE and retry semantics

    [Fact]
    public async Task Delete_LeasedTask_IsCancelledByCascade_AndNoteDeleteWorks()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        var sourceId = (await ReadJsonAsync(await PostUrlAsync(client, episodeId, "https://example.com/a"))).GetProperty("id").GetGuid();
        SqlProbe.Execute(
            Database,
            $"update fetch_tasks set status='leased', leased_by='qa', lease_until=now() + interval '1 minute', leased_at=now(), attempts=1 where source_id='{sourceId}'");
        var noteId = (await ReadJsonAsync(await PostNoteAsync(client, episodeId, "note"))).GetProperty("id").GetGuid();

        using var deleted = await client.DeleteAsync($"{Route(episodeId)}/{sourceId}", Ct);
        using var noteDeleted = await client.DeleteAsync($"{Route(episodeId)}/{noteId}", Ct);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        noteDeleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Scalar($"select count(*) from fetch_tasks where episode_id='{episodeId}'").ShouldBe("0");
        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("0");
    }

    [Fact]
    public async Task Delete_FreesAnEpisodeSlot_AndTheSameUrlCanBeAddedAgain()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        var id = (await ReadJsonAsync(await PostUrlAsync(client, episodeId, "https://example.com/a"))).GetProperty("id").GetGuid();
        (await client.DeleteAsync($"{Route(episodeId)}/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var again = await PostUrlAsync(client, episodeId, "https://example.com/a");

        again.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("pending", 409)]
    [InlineData("fetched", 409)]
    [InlineData("rejected", 409)]
    [InlineData("failed", 202)]
    public async Task Retry_FromEachStatus(string status, int expected)
    {
        var (userId, _, episodeId) = await SeedAsync();
        var sourceId = Guid.NewGuid();
        SqlProbe.Execute(
            Database,
            "insert into story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, error_code, created_at) "
            + $"values ('{sourceId}', '{userId}', '{episodeId}', 'url', 'https://r.example/a', 'https://r.example/a', 'r.example', '{status}', "
            + $"{(status is "failed" or "rejected" ? "'source_unreachable'" : "null")}, '{Start.AddMinutes(-5):O}')");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await client.PostAsync($"{Route(episodeId)}/{sourceId}/retry", null, Ct);

        ((int)response.StatusCode).ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        if (expected == 409)
        {
            (await CodeOfAsync(response)).ShouldBe("episode_invalid_state");
            Scalar($"select status from story_sources where id='{sourceId}'").ShouldBe(status);
            Scalar($"select count(*) from fetch_tasks where source_id='{sourceId}'").ShouldBe("0");
        }
        else
        {
            var body = await ReadJsonAsync(response);
            body.GetProperty("status").GetString().ShouldBe("pending");
            body.GetProperty("errorCode").ValueKind.ShouldBe(JsonValueKind.Null, "a retry clears the previous error");
            Scalar($"select count(*) from fetch_tasks where source_id='{sourceId}' and status='queued' and attempts=0").ShouldBe("1");
        }
    }

    [Fact]
    public async Task Retry_Twice_SecondIs409_AndOnlyOneLiveTaskExists()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        var sourceId = (await ReadJsonAsync(await PostUrlAsync(client, episodeId, "https://example.com/a"))).GetProperty("id").GetGuid();
        SqlProbe.Execute(Database, $"update story_sources set status='failed', error_code='source_unreachable' where id='{sourceId}'");
        SqlProbe.Execute(Database, $"update fetch_tasks set status='failed', error_code='source_unreachable', attempts=2 where source_id='{sourceId}'");

        using var first = await client.PostAsync($"{Route(episodeId)}/{sourceId}/retry", null, Ct);
        using var second = await client.PostAsync($"{Route(episodeId)}/{sourceId}/retry", null, Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        Scalar($"select count(*) from fetch_tasks where source_id='{sourceId}' and status in ('queued','leased')").ShouldBe("1");
        Scalar($"select count(*) from fetch_tasks where source_id='{sourceId}'").ShouldBe("2");
    }

    [Fact]
    public async Task Retry_NotFoundSource_Is404()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        (await client.PostAsync($"{Route(episodeId)}/{Guid.NewGuid()}/retry", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ list shape

    [Fact]
    public async Task List_FailedSource_CarriesErrorMessageKey_AndNeverTheExtractedTextOrHash()
    {
        var (userId, _, episodeId) = await SeedAsync();
        var sourceId = Guid.NewGuid();
        SqlProbe.Execute(
            Database,
            "insert into story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, error_code, http_status, extracted_text_key, content_sha256, created_at) "
            + $"values ('{sourceId}', '{userId}', '{episodeId}', 'url', 'https://l.example/a', 'https://l.example/a', 'l.example', 'failed', 'source_unreachable', 503, 'research/key.txt', repeat('b', 64), '{Start.AddMinutes(-5):O}')");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var list = await client.GetAsync(Route(episodeId), Ct);

        var raw = await list.Content.ReadAsStringAsync(Ct);
        var item = JsonDocument.Parse(raw).RootElement.GetProperty("items")[0];
        item.GetProperty("errorCode").GetString().ShouldBe("source_unreachable");
        item.GetProperty("errorMessageKey").GetString().ShouldNotBeNullOrWhiteSpace();
        item.GetProperty("httpStatus").GetInt32().ShouldBe(503);
        raw.ShouldNotContain("research/key.txt");
        raw.ShouldNotContain(new string('b', 64));
        raw.ShouldNotContain("extractedText", Case.Insensitive);
    }

    [Fact]
    public async Task List_IsOrderedByCreationTime_AndLimitsCountOnlyTheCallersUrlRowsInTheWindow()
    {
        var (userId, seriesId, episodeId) = await SeedAsync();
        SeedUrlRows(userId, episodeId, 2, Start.AddHours(-30), tag: "old");
        var otherEpisode = await NewEpisodeAsync(userId, seriesId);
        SeedUrlRows(userId, otherEpisode, 3, Start.AddHours(-1), tag: "recent");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        (await PostUrlAsync(client, episodeId, "https://example.com/new")).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var list = await client.GetAsync(Route(episodeId), Ct);

        var body = await ReadJsonAsync(list);
        body.GetProperty("items").GetArrayLength().ShouldBe(3);
        body.GetProperty("limits").GetProperty("urlSourcesLast24h").GetInt32().ShouldBe(4, "3 recent on the other episode + the new one; the 2 old ones are outside the window");
        var created = body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("createdAt").GetDateTimeOffset()).ToList();
        created.ShouldBe(created.Order().ToList());
    }

    // ------------------------------------------------------------------ atomicity: source + fetch_task in one transaction

    [Fact]
    public async Task Add_WhenTheFetchTaskInsertFails_TheSourceRowIsRolledBack()
    {
        var (userId, _, episodeId) = await SeedAsync();
        var suffix = episodeId.ToString("N");
        var function = $"qa_fail_fetch_task_{suffix}";
        SqlProbe.Execute(
            Database,
            $"create function {function}() returns trigger language plpgsql as $$ begin raise exception 'qa_forced_failure'; end $$");
        SqlProbe.Execute(
            Database,
            $"create trigger {function}_trg before insert on fetch_tasks for each row when (new.episode_id = '{episodeId}') execute function {function}()");
        try
        {
            using var factory = NewFactory();
            using var client = factory.ClientFor(userId);

            HttpStatusCode? status = null;
            try
            {
                using var response = await PostUrlAsync(client, episodeId, "https://example.com/atomic");
                status = response.StatusCode;
            }
            catch (Exception)
            {
                // The test host may surface the unhandled exception instead of a 500; the database state is what counts.
            }

            if (status is { } code)
            {
                ((int)code).ShouldBeGreaterThanOrEqualTo(500);
            }

            Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("0", "source + task are one transaction");
            Scalar($"select count(*) from fetch_tasks where episode_id='{episodeId}'").ShouldBe("0");
        }
        finally
        {
            SqlProbe.Execute(Database, $"drop trigger if exists {function}_trg on fetch_tasks");
            SqlProbe.Execute(Database, $"drop function if exists {function}()");
        }
    }

    // ------------------------------------------------------------------ privacy: logs (D19) and audit data (D13)

    [Fact]
    public async Task Privacy_NoUrlNoteOrTitleInLogsOrAuditData_AcrossEveryRouteAndFailurePath()
    {
        const string SecretPath = "qa-secret-path-9f3a1c";
        const string SecretNote = "qa-secret-note-8c1d77";
        const string SecretTitle = "qa-secret-title-77aa02";
        var (userId, _, episodeId) = await SeedAsync();
        var sink = new CapturingLoggerProvider();
        using var factory = new ApiFactory(Database, new FakeClock(Start)).WithLoggerProvider(sink);
        using var client = factory.ClientFor(userId);

        var created = await ReadJsonAsync(await PostUrlAsync(client, episodeId, $"https://leak-check.example.com/{SecretPath}?token={SecretPath}"));
        var urlId = created.GetProperty("id").GetGuid();
        (await PostUrlAsync(client, episodeId, $"https://leak-check.example.com/{SecretPath}?token={SecretPath}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PostUrlAsync(client, episodeId, $"https://youtube.com/{SecretPath}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostUrlAsync(client, episodeId, $"http://127.0.0.1/{SecretPath}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostUrlAsync(client, episodeId, $"https://user:{SecretPath}@leak-check.example.com/")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostNoteAsync(client, episodeId, SecretNote)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PostNoteAsync(client, episodeId, new string('x', 3001) + SecretNote)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        SqlProbe.Execute(
            Database,
            $"update story_sources set status='failed', error_code='source_unreachable', title='{SecretTitle}', final_url='https://leak-check.example.com/{SecretPath}' where id='{urlId}'");
        SqlProbe.Execute(Database, $"update fetch_tasks set status='failed', error_code='source_unreachable', attempts=2 where source_id='{urlId}'");
        (await client.GetAsync(Route(episodeId), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync($"{Route(episodeId)}/{urlId}/retry", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await client.DeleteAsync($"{Route(episodeId)}/{urlId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var lines = sink.Lines;
        lines.ShouldNotBeEmpty("the host logs requests, so the capture must have seen something");
        foreach (var secret in new[] { SecretPath, SecretNote, SecretTitle })
        {
            lines.Where(line => line.Contains(secret, StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty($"{secret} leaked into a log line");
        }

        var audit = SqlProbe.QueryStrings(
            Database,
            $"select concat_ws('|', action, entity_type, entity_id::text, data::text) from audit_logs where subject_user_id='{userId}' and action like 'episode.source_%' order by created_at");
        audit.ShouldNotBeEmpty("add/remove/retry are audited");
        foreach (var row in audit)
        {
            foreach (var secret in new[] { SecretPath, SecretNote, SecretTitle, "leak-check.example.com/" })
            {
                row.ShouldNotContain(secret, Case.Insensitive);
            }
        }

        audit.Select(row => row.Split('|')[0]).ShouldContain("episode.source_added");
        audit.Select(row => row.Split('|')[0]).ShouldContain("episode.source_removed");
        audit.Select(row => row.Split('|')[0]).ShouldContain("episode.source_retried");
    }

    /// <summary>
    /// QA-2 (task Design step 8): the audit row of an add carries <c>{ sourceId, kind, registrableDomain, status }</c> and
    /// no URL.
    /// </summary>
    [Fact]
    public async Task Audit_Add_CarriesSourceIdKindRegistrableDomainAndStatus()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);
        var sourceId = (await ReadJsonAsync(await PostUrlAsync(client, episodeId, "https://example.com/a"))).GetProperty("id").GetGuid();

        var data = SqlProbe.QueryStrings(
            Database,
            $"select data::text from audit_logs where subject_user_id='{userId}' and action='episode.source_added'").ShouldHaveSingleItem();

        data.ShouldContain(sourceId.ToString());
        data.ShouldContain("example.com");
        data.ShouldContain("pending");
    }

    [Fact]
    public async Task Audit_FailedAdds_WriteNoSourceAddedRow_OrAtLeastNoneThatClaimSuccess()
    {
        var (userId, _, episodeId) = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        (await PostUrlAsync(client, episodeId, "https://youtube.com/watch?v=x")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostUrlAsync(client, episodeId, "http://127.0.0.1/")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("0");
        var rows = SqlProbe.QueryStrings(
            Database,
            $"select data::text from audit_logs where subject_user_id='{userId}' and action='episode.source_added'");
        rows.Count.ShouldBe(0, "a rejected add changed nothing, so it must not leave a success audit row");
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var text = formatter(state, exception) + (exception is null ? string.Empty : " " + exception);
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    text += " " + string.Join(' ', pairs.Select(pair => $"{pair.Key}={pair.Value}"));
                }

                lock (owner._gate)
                {
                    owner._lines.Add(text);
                }
            }
        }
    }
}
