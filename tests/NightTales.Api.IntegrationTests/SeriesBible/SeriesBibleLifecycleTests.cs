using System.Globalization;
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
/// The lifecycle facts of 02.6.3-T1 that are not an HTTP shape: the create-series transaction writes the
/// series' one <c>series_bibles</c> row (<c>CreateDefault</c>, plan §3.3); an accepted save leaves exactly
/// one <c>series.bible_updated</c> audit row whose data carries ids and counts only (D13, D19); and the same
/// save announces itself once as <c>series_bible_updated.v1</c> in the outbox of its own transaction
/// (plan §5).
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

    [Fact]
    public async Task PutBible_WritesOneOutboxRowOfTheAnnouncement()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(characters: new JsonArray(SeriesBibleHarness.Character(1, "Bà Tư"))));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));

        // The series aggregate raises the announcement after the content is replaced, so the interceptor writes
        // the row with the new version in the save's own SaveChanges — one row, ids and the version only (plan §5).
        SeriesBibleHarness.OutboxCount(Database, world.UserId, SeriesBibleHarness.BibleUpdatedEvent)
            .ShouldBe(1, "an accepted save announces itself exactly once");

        var payload = JsonNode.Parse(
            SeriesBibleHarness.OutboxPayload(Database, world.UserId, SeriesBibleHarness.BibleUpdatedEvent))!;
        payload["seriesId"]!.GetValue<string>().ShouldBe(world.SeriesId.ToString("D", CultureInfo.InvariantCulture));
        payload["userId"]!.GetValue<string>().ShouldBe(world.UserId.ToString("D", CultureInfo.InvariantCulture));
        payload["version"]!.GetValue<int>().ShouldBe(2, "the version the save produced");
        payload["changedBy"]!.GetValue<string>().ShouldBe(
            world.UserId.ToString("D", CultureInfo.InvariantCulture),
            "the actor of the save is the owner in phase P");
        DateTimeOffset.Parse(payload["occurredAt"]!.GetValue<string>(), CultureInfo.InvariantCulture)
            .ShouldBe(SeriesBibleHarness.Start);

        // The payload is an ids-and-metadata document (02 D19): no title, name or rule text travels with it.
        SeriesBibleHarness.OutboxPayload(Database, world.UserId, SeriesBibleHarness.BibleUpdatedEvent)
            .ShouldNotContain("Bà Tư");
    }

    [Fact]
    public async Task PutBible_RefusedWrite_WritesNoOutboxRow()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var stale = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(9),
            SeriesBibleHarness.Content());
        await SeriesBibleHarness.AssertProblemAsync(stale, 412, "bible_conflict");

        var characters = new JsonArray();
        for (var index = 1; index <= 31; index++)
        {
            characters.Add(SeriesBibleHarness.Character(index));
        }

        using var invalid = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(characters: characters));
        await SeriesBibleHarness.AssertProblemAsync(invalid, 400, "bible_invalid_content");

        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1, "a refused write changes nothing");
        SeriesBibleHarness.OutboxCount(Database, world.UserId, SeriesBibleHarness.BibleUpdatedEvent)
            .ShouldBe(0, "the announcement is written by the save's own transaction, so a refusal leaves none");
    }
}
