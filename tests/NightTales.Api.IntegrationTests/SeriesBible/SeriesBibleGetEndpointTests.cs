using System.Net;
using System.Text.Json;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// <c>GET /api/v1/series/{id}/bible</c> against the real host and a real PostgreSQL 17 (02.6.3-T1): the
/// <c>"sb-&lt;version&gt;"</c> ETag (D9), the empty <c>bible.v1</c> document of a fresh series, the pending
/// proposal slot T2 fills, and the tenant rule — an unknown id, another user's series and a drifted series
/// without its row (500 <c>series_bible_missing</c>, plan §4 U1).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SeriesBibleGetEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task GetBible_ReturnsProposalNullAndEtag()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SeriesBibleHarness.GetAsync(client, world.SeriesId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.ResponseEtag(response).ShouldBe(SeriesBibleHarness.Etag(1), "the tag names the row's own version");

        var body = await SeriesBibleHarness.JsonAsync(response);
        body.GetProperty("seriesId").GetGuid().ShouldBe(world.SeriesId);
        body.GetProperty("version").GetInt32().ShouldBe(1);
        body.GetProperty("etag").GetString().ShouldBe(SeriesBibleHarness.Etag(1));
        body.GetProperty("proposal").ValueKind.ShouldBe(
            JsonValueKind.Null,
            "no proposal exists before 02.6.3-T2 writes one; the member stays in the contract");

        var content = body.GetProperty("content");
        content.GetProperty("narrator").GetProperty("name").ValueKind.ShouldBe(JsonValueKind.Null);
        content.GetProperty("narrator").GetProperty("persona").GetString().ShouldBeEmpty();
        content.GetProperty("narrator").GetProperty("voice_notes").GetString().ShouldBeEmpty();
        foreach (var array in new[] { "recurring_characters", "places", "rules", "episode_summaries" })
        {
            content.GetProperty(array).GetArrayLength().ShouldBe(0, $"a fresh bible's {array} is empty");
        }

        // The read never writes: the series still owns exactly the one row at version 1.
        SeriesBibleHarness.RowCount(Database, world.SeriesId).ShouldBe(1);
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Fact]
    public async Task GetBible_UnknownSeries_Returns404()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SeriesBibleHarness.GetAsync(client, Guid.CreateVersion7());

        await SeriesBibleHarness.AssertProblemAsync(response, 404, "not_found");
    }

    [Fact]
    public async Task GetBible_OtherUsersSeries_Returns404()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var stranger = factory.ClientFor(Guid.CreateVersion7());

        using var response = await SeriesBibleHarness.GetAsync(stranger, world.SeriesId);

        await SeriesBibleHarness.AssertProblemAsync(response, 404, "not_found");
    }

    [Fact]
    public async Task GetBible_MissingRow_Returns500SeriesBibleMissing()
    {
        var world = await SeriesBibleHarness.SeedSeriesWithoutBibleAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SeriesBibleHarness.GetAsync(client, world.SeriesId);

        // Data drift (the hook did not land, or a row was removed): the query never auto-creates it (plan §4 U1).
        await SeriesBibleHarness.AssertProblemAsync(response, 500, "series_bible_missing");
        SeriesBibleHarness.RowCount(Database, world.SeriesId).ShouldBe(0, "a read must not write the missing row");
    }
}
