using System.Net;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.TestKit;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Research;

/// <summary>
/// <c>GET /api/v1/episodes/{id}/fact-sheet</c> against the real host and a real PostgreSQL 17 (02.5.4-T1):
/// the current version with its <c>fs-</c> ETag (D9), the resolved source references with the
/// <c>isUserNote</c> flag, the tenant 404 and the no-sheet 404. The read route has no episode-state gate, so
/// it answers for every state of the episode.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FactSheetGetEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task GetFactSheet_ReturnsCurrentVersionWithFsEtag_AndResolvedSources()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var article = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'), "The river house");
        var note = await FactSheetHarness.AddNoteAsync(Database, world, "The owner's own note.");
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [
                FactSheetHarness.Seed("f1", "The house stood empty for a year.", [article], FactConfidence.Medium, FactOrigin.Model, 0.12m),
                FactSheetHarness.Seed("f2", "The owner kept a note about the knock.", [note]),
            ]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.GetAsync(client, world.EpisodeId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        FactSheetHarness.ResponseEtag(response).ShouldBe(FactSheetHarness.Etag(sheet.Id));
        response.Headers.CacheControl!.NoCache.ShouldBeTrue("the read is never cached");
        response.Headers.Vary.ShouldContain("Authorization", "the answer is per-tenant, so a shared cache must key on the token");

        var body = await FactSheetHarness.JsonAsync(response);
        body.GetProperty("id").GetGuid().ShouldBe(sheet.Id);
        body.GetProperty("version").GetInt32().ShouldBe(1);
        body.GetProperty("status").GetString().ShouldBe("draft");
        body.GetProperty("etag").GetString().ShouldBe(FactSheetHarness.Etag(sheet.Id));
        body.GetProperty("promptVersion").GetString().ShouldBe(FactSheetBuilder.DefaultPromptVersion);
        body.GetProperty("approvedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("maxFactId").GetInt32().ShouldBe(2, "the sheet reports the episode's id watermark (F12)");

        var facts = body.GetProperty("facts").EnumerateArray().ToArray();
        facts.Length.ShouldBe(2);
        facts[0].GetProperty("id").GetString().ShouldBe("f1");
        facts[0].GetProperty("statement").GetString().ShouldBe("The house stood empty for a year.");
        facts[0].GetProperty("origin").GetString().ShouldBe("model");
        facts[0].GetProperty("confidence").GetString().ShouldBe("medium");
        facts[0].GetProperty("sourceOverlap").GetDecimal().ShouldBe(0.12m);

        var cited = facts[0].GetProperty("sources").EnumerateArray().ToArray();
        cited.Length.ShouldBe(1);
        cited[0].GetProperty("sourceId").GetGuid().ShouldBe(article);
        cited[0].GetProperty("alias").GetString().ShouldBe("s1");
        cited[0].GetProperty("title").GetString().ShouldBe("The river house");
        cited[0].GetProperty("url").GetString().ShouldNotBeNullOrWhiteSpace();
        cited[0].GetProperty("isUserNote").GetBoolean().ShouldBeFalse();

        var noteRef = facts[1].GetProperty("sources").EnumerateArray().Single();
        noteRef.GetProperty("sourceId").GetGuid().ShouldBe(note);
        noteRef.GetProperty("isUserNote").GetBoolean().ShouldBeTrue();
        noteRef.GetProperty("url").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetFactSheet_ReturnsHighestVersion_NotTheApprovedRow()
    {
        // D9/decision 3: "current" is the highest version whatever its status, so an edit that is still a draft
        // hides the older approved row from the read route.
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var approved = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [FactSheetHarness.Seed("f1", "Approved fact.", [source])],
            FactSheetStatus.Approved,
            approvedBy: world.UserId,
            approvedAt: FactSheetHarness.Start);
        var draft = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            2,
            [FactSheetHarness.Seed("f1", "Approved fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.GetAsync(client, world.EpisodeId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        var body = await FactSheetHarness.JsonAsync(response);
        body.GetProperty("id").GetGuid().ShouldBe(draft.Id);
        body.GetProperty("version").GetInt32().ShouldBe(2);
        body.GetProperty("status").GetString().ShouldBe("draft");
        body.GetProperty("id").GetGuid().ShouldNotBe(approved.Id);
    }

    [Fact]
    public async Task GetFactSheet_ApprovedRow_ReportsApprovedStatusAndStamp()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var approved = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [FactSheetHarness.Seed("f1", "Approved fact.", [source])],
            FactSheetStatus.Approved,
            approvedBy: world.UserId,
            approvedAt: FactSheetHarness.Start);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.GetAsync(client, world.EpisodeId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        var body = await FactSheetHarness.JsonAsync(response);
        body.GetProperty("id").GetGuid().ShouldBe(approved.Id);
        body.GetProperty("status").GetString().ShouldBe("approved");
        body.GetProperty("approvedAt").GetDateTimeOffset().ShouldBe(FactSheetHarness.Start);
        body.GetProperty("etag").GetString().ShouldBe(FactSheetHarness.Etag(approved.Id));
    }

    [Fact]
    public async Task GetFactSheet_ReadsTheHighestVersion_EvenWhenNoDraftExists()
    {
        var world = await FactSheetHarness.SeedAsync(Database, EpisodeStatus.StoryApproved);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var superseded = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [FactSheetHarness.Seed("f1", "Old approved fact.", [source])],
            FactSheetStatus.Superseded,
            approvedBy: world.UserId,
            approvedAt: FactSheetHarness.Start);
        var approved = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            2,
            [FactSheetHarness.Seed("f2", "New approved fact.", [source])],
            FactSheetStatus.Approved,
            approvedBy: world.UserId,
            approvedAt: FactSheetHarness.Start);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.GetAsync(client, world.EpisodeId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        var body = await FactSheetHarness.JsonAsync(response);
        body.GetProperty("id").GetGuid().ShouldBe(approved.Id);
        body.GetProperty("version").GetInt32().ShouldBe(2);
        body.GetProperty("status").GetString().ShouldBe("approved");
        body.GetProperty("id").GetGuid().ShouldNotBe(superseded.Id, "the superseded row is history, not the current version");
    }

    [Fact]
    public async Task GetFactSheet_Anonymous_Returns401()
    {
        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.AnonymousClient();

        using var response = await FactSheetHarness.GetAsync(client, Guid.CreateVersion7());

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetFactSheet_AnotherTenantsEpisode_Returns404()
    {
        var owner = await FactSheetHarness.SeedAsync(Database);
        var stranger = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, owner);
        await FactSheetHarness.SeedSheetAsync(Database, owner, 1, [FactSheetHarness.Seed("f1", "Owned fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(stranger.UserId);

        using var response = await FactSheetHarness.GetAsync(client, owner.EpisodeId);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "another tenant's episode is a 404, never a 403");
    }

    [Fact]
    public async Task GetFactSheet_UnknownEpisode_Returns404()
    {
        var world = await FactSheetHarness.SeedAsync(Database);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.GetAsync(client, Guid.CreateVersion7());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetFactSheet_EpisodeWithoutSheet_Returns404FactSheetNotFound()
    {
        var world = await FactSheetHarness.SeedAsync(Database);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.GetAsync(client, world.EpisodeId);

        await FactSheetHarness.AssertProblemAsync(response, 404, "fact_sheet_not_found");
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM fact_sheets WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0, "the read never creates a row");
    }

    [Fact]
    public async Task GetFactSheet_CitedSourceRemovedAfterTheSave_StillResolvesTheStoredCitation()
    {
        // The citation a version carries is (alias, id) in its own document (CR-4/D21), so removing the source
        // from the episode afterwards does not break the read: the fact keeps citing the id it was saved with.
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "Kept fact.", [source])]);
        SqlProbe.Execute(Database, $"DELETE FROM story_sources WHERE id = '{source}'");

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.GetAsync(client, world.EpisodeId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        var facts = (await FactSheetHarness.JsonAsync(response)).GetProperty("facts").EnumerateArray().ToArray();
        facts.Length.ShouldBe(1);
        facts[0].GetProperty("sources").EnumerateArray().Single().GetProperty("sourceId").GetGuid().ShouldBe(source);
    }

    [Fact]
    public async Task GetFactSheet_RedirectedSource_ReportsTheCanonicalUrl_NotTheFetchedFinalUrl()
    {
        // The fetcher follows redirects, so a source's final_url can differ from the canonical url it was saved
        // with. The rendered reference is story_sources.url (a client must never link to the fetch target), while
        // the title falls back through final_url when the source carries none — both directions pinned here.
        var world = await FactSheetHarness.SeedAsync(Database);
        var redirect = $"https://example.com/redirected-{Guid.CreateVersion7():N}";
        var source = await FactSheetHarness.AddFetchedUrlAsync(
            Database,
            world,
            FactSheetHarness.Sha('a'),
            title: null,
            finalUrl: redirect);
        var canonical = SqlProbe.ScalarText(Database, $"SELECT url FROM story_sources WHERE id = '{source}'");
        var final = SqlProbe.ScalarText(Database, $"SELECT final_url FROM story_sources WHERE id = '{source}'");
        await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "A redirected fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.GetAsync(client, world.EpisodeId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        var cited = (await FactSheetHarness.JsonAsync(response)).GetProperty("facts").EnumerateArray().Single()
            .GetProperty("sources").EnumerateArray().Single();
        final.ShouldNotBe(canonical, "the fixture must give the fetcher a different target than the saved url");
        cited.GetProperty("sourceId").GetGuid().ShouldBe(source);
        cited.GetProperty("url").GetString().ShouldBe(canonical, "the reference resolves the canonical url, never the fetch target");
        cited.GetProperty("title").GetString().ShouldBe(final, "a source without a title falls back through final_url");
        cited.GetProperty("isUserNote").GetBoolean().ShouldBeFalse();
    }
}
