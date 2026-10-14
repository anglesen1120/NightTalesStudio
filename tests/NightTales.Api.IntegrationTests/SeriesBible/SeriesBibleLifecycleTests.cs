using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// The two lifecycle facts of 02.6.3-T1 that are not an HTTP shape: the create-series transaction writes the
/// series' one <c>series_bibles</c> row (<c>CreateDefault</c>, plan §3.3), and an accepted save leaves exactly
/// one <c>series.bible_updated</c> audit row whose data carries ids and counts only (D13, D19).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SeriesBibleLifecycleTests(TestDatabase database) : IntegrationTest(database)
{
    private const string SeriesRoute = "/api/v1/series";

    [Fact]
    public async Task CreateSeries_CreatesEmptyBible()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: SeriesBibleHarness.Ct);
        var builder = new SeriesBuilder(Database, actor, new FakeClock(SeriesBibleHarness.Start));
        var voice = await builder.EnsureVoiceAsync(SeriesBuilder.DefaultVoiceId, Language.Vi, ct: SeriesBibleHarness.Ct);
        var template = await builder.EnsureStyleTemplateAsync(ct: SeriesBibleHarness.Ct);

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(user.Id);

        using var created = await client.PostAsJsonAsync(
            SeriesRoute,
            new
            {
                title = "Chuỗi thử nghiệm",
                topic = "ghost",
                language = "vi",
                aspect = "landscape",
                voiceId = voice.Id,
                styleTemplateId = template.Id,
                storyPreset = (string?)null,
            },
            SeriesBibleHarness.Ct);

        created.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            await created.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        var seriesId = (await SeriesBibleHarness.JsonAsync(created)).GetProperty("id").GetGuid();

        // The hook writes the row inside the create transaction: exactly one, at version 1, empty.
        SeriesBibleHarness.RowCount(Database, seriesId).ShouldBe(1, "a series owns exactly one bible row");
        var row = SeriesBibleHarness.Row(Database, seriesId);
        row.Version.ShouldBe(1);
        row.Content["narrator"]!["persona"]!.GetValue<string>().ShouldBeEmpty();
        foreach (var array in new[] { "recurring_characters", "places", "rules", "episode_summaries" })
        {
            row.Content[array]!.AsArray().Count.ShouldBe(0, $"a new bible's {array} is empty");
        }

        // ...and the read route answers that very row.
        using var read = await SeriesBibleHarness.GetAsync(client, seriesId);
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.ResponseEtag(read).ShouldBe(SeriesBibleHarness.Etag(1), "a fresh bible is at version 1");
    }

    [Fact]
    public async Task PutBible_WritesAuditRowWithoutContent()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(
                characters: new JsonArray(SeriesBibleHarness.Character(1, "Bà Tư Bí Mật")),
                rules: new JsonArray(SeriesBibleHarness.Rule(1))));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));

        // One row from the attribute and the projection: the category and visibility of [Audited], the actor, and
        // a data document of ids and counts — no name, persona, rule text or summary (D19).
        SeriesBibleHarness.AuditCount(Database, world.UserId).ShouldBe(1, "an accepted save writes exactly one audit row");
        var row = SeriesBibleHarness.AuditRow(Database, world.UserId);
        row.ShouldStartWith("series.bible_updated|episodes|user|user|");
        row.ShouldContain(world.UserId.ToString("D"));
        row.ShouldContain(world.SeriesId.ToString("D"));
        SeriesBibleHarness.AuditDataField(Database, world.UserId, "characters").ShouldBe("1");
        SeriesBibleHarness.AuditDataField(Database, world.UserId, "version").ShouldBe("2");
        row.ShouldNotContain("Bà Tư Bí Mật");
        row.ShouldNotContain("kể chuyện đêm");
        row.ShouldNotContain("Luật 1");
    }

    [Fact]
    public async Task PutBible_RefusedWrite_WritesNoAuditRow()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(9),
            SeriesBibleHarness.Content());

        await SeriesBibleHarness.AssertProblemAsync(response, 412, "bible_conflict");
        SeriesBibleHarness.AuditCount(Database, world.UserId).ShouldBe(0, "the refused write rolls back, audit row included");
    }
}
