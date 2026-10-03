using System.Net;
using System.Net.Http.Json;
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
/// The 02.5.1-T1 review fixes against the real host and PostgreSQL 17: a note holding U+0000 is a 400 and persists
/// nothing (CR-2), and removing a source commits one <c>source_removed.v1</c> outbox row with ids only (CR-3).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SourceIntegrityReviewTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    private static string Route(Guid episodeId) => $"/api/v1/episodes/{episodeId}/sources";

    private async Task<(Guid UserId, Guid EpisodeId)> SeedAsync()
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, new TestCurrentUser(), new FakeClock(Start)).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, new TestCurrentUser(), new FakeClock(Start))
            .CreateAsync(user.Id, series.Id, status: EpisodeStatus.Draft, ct: Ct);

        return (user.Id, episode.Id);
    }

    private string Scalar(string sql) => SqlProbe.QueryStrings(Database, $"select ({sql})::text").ShouldHaveSingleItem();

    [Theory]
    [InlineData("before\0after")]
    [InlineData("\0")]
    [InlineData("a note\nwith lines\0")]
    public async Task AddNote_WithNulCharacter_Returns400AndPersistsNothing(string note)
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.PostAsJsonAsync(Route(episodeId), new { noteText = note }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("0");
    }

    [Fact]
    public async Task AddNote_Multiline_StillAccepted()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.PostAsJsonAsync(Route(episodeId), new { noteText = "line one\r\nline two\n\tline three" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        Scalar($"select count(*) from story_sources where episode_id='{episodeId}'").ShouldBe("1");
    }

    [Fact]
    public async Task RemoveSource_CommitsOneSourceRemovedOutboxRowWithIdsOnly()
    {
        var (userId, episodeId) = await SeedAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);
        using var added = await client.PostAsJsonAsync(Route(episodeId), new { url = "https://example.com/secret-article?token=abc" }, Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync(Ct));
        var sourceId = (await added.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct)).GetProperty("id").GetGuid();
        Scalar($"select count(*) from outbox_messages where type = 'source_removed.v1' and user_id = '{userId}'").ShouldBe("0");

        using var deleted = await client.DeleteAsync($"{Route(episodeId)}/{sourceId}", Ct);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Scalar($"select count(*) from outbox_messages where type = 'source_removed.v1' and user_id = '{userId}'").ShouldBe("1");
        var keys = SqlProbe.QueryStrings(
            Database,
            "select lower(k)::text from outbox_messages m, jsonb_object_keys(m.payload) k "
            + $"where m.type = 'source_removed.v1' and m.user_id = '{userId}' order by 1");
        keys.ShouldBe(["episodeid", "occurredat", "sourceid", "userid"]);
        var payload = SqlProbe.ScalarText(Database, $"select payload::text from outbox_messages where type = 'source_removed.v1' and user_id = '{userId}'");
        payload.ShouldContain(sourceId.ToString());
        payload.ShouldContain(episodeId.ToString());
        payload.ShouldNotContain("secret-article");
        payload.ShouldNotContain("token=abc");

        using var again = await client.DeleteAsync($"{Route(episodeId)}/{sourceId}", Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Scalar($"select count(*) from outbox_messages where type = 'source_removed.v1' and user_id = '{userId}'").ShouldBe("1");
    }
}
