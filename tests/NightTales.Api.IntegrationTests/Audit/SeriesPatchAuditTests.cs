using System.Net;
using System.Net.Http.Json;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Audit;

/// <summary>
/// The acceptance of 09.3.6-T1 over a real request: 09.4.1-T1's <c>PATCH /series/{id}</c> is an
/// <c>[Audited]</c> command, so exactly one <c>audit_logs</c> row carries the attribute's action,
/// category and visibility, the projection's <c>data</c> and the caller's own ids — and a refused patch
/// (409) leaves nothing behind.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SeriesPatchAuditTests(TestDatabase database) : IntegrationTest(database)
{
    private const string RoutePrefix = "/api/v1/series";

    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SeriesPatch_WritesOneRowFromTheAttributeAndTheProjection()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(
            client,
            seriesId,
            new { title = "Tiêu đề mới" },
            await ReadETagAsync(client, seriesId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        SqlProbe.QueryStrings(Database, RowSql(seriesId))
            .ShouldHaveSingleItem()
            .ShouldBe($"series.updated|episodes|user|user|{userId}|{userId}|series|{seriesId}|-|-");

        SqlProbe.ScalarText(Database, $"SELECT data->>'fields' FROM audit_logs WHERE entity_id = '{seriesId}'")
            .ShouldBe("title");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs entry, jsonb_object_keys(entry.data) WHERE entry.entity_id = '{seriesId}'")
            .ShouldBe(1, "data holds the projection's keys — the changed field names — and no others");
    }

    [Fact]
    public async Task SeriesPatch_ArchivedConflict_WritesNoRow()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var archive = await client.DeleteAsync($"{RoutePrefix}/{seriesId}", Ct);
        archive.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var response = await PatchAsync(
            client,
            seriesId,
            new { title = "x" },
            await ReadETagAsync(client, seriesId));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE entity_id = '{seriesId}' AND action = 'series.updated'")
            .ShouldBe(0, "the 409 rolls the transaction — and its audit row — back");
    }

    /// <summary>The row's key fields, with a dash for every column an ordinary command leaves null.</summary>
    private static string RowSql(Guid seriesId) => $"""
        SELECT action || '|' || category || '|' || visibility || '|' || actor_type || '|' || actor_user_id
            || '|' || subject_user_id || '|' || coalesce(entity_type, '-') || '|' || coalesce(entity_id::text, '-')
            || '|' || coalesce(ip::text, '-') || '|' || coalesce(user_agent, '-')
        FROM audit_logs WHERE entity_id = '{seriesId}'
        """;

    private async Task<Guid> CreateUserAsync()
        => (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct)).Id;

    private async Task<Guid> SeedSeriesAsync(Guid userId)
        => (await new SeriesBuilder(Database, new TestCurrentUser(), new FakeClock(Start)).CreateAsync(userId, ct: Ct)).Id;

    private static async Task<string> ReadETagAsync(HttpClient client, Guid seriesId)
    {
        using var response = await client.GetAsync($"{RoutePrefix}/{seriesId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();

        return response.Headers.ETag!.ToString();
    }

    private static async Task<HttpResponseMessage> PatchAsync<TBody>(
        HttpClient client,
        Guid seriesId,
        TBody body,
        string ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{RoutePrefix}/{seriesId}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        return await client.SendAsync(request, Ct);
    }
}
