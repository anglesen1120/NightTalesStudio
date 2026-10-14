using System.Net;
using System.Text.Json.Nodes;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// <c>PUT /api/v1/series/{id}/bible</c> against the real host and a real PostgreSQL 17 (02.6.3-T1): the
/// <c>If-Match</c> contract of D9 (428 without a tag, 412 <c>bible_conflict</c> for a stale, weak or starred
/// one), one new version per accepted write with the <c>"sb-&lt;version&gt;"</c> tag moving with it, the §3.2
/// limits answered as 400 <c>bible_invalid_content</c> with the offending field paths, the tenant rule, and the
/// lost-update race two tabs run.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SeriesBiblePutEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task PutBible_WithoutIfMatch_Returns428()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            ifMatch: null,
            SeriesBibleHarness.Content());

        // The RequireIfMatch filter answers before the handler runs, so nothing is written.
        await SeriesBibleHarness.AssertProblemAsync(response, 428, "if_match_required");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Fact]
    public async Task PutBible_WithStaleEtag_Returns412()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(9),
            SeriesBibleHarness.Content(narratorName: "Người kể khác"));

        await SeriesBibleHarness.AssertProblemAsync(response, 412, "bible_conflict");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1, "a refused write changes nothing");
    }

    [Fact]
    public async Task PutBible_RepeatSameIfMatch_Second412()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var first = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content());
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.ResponseEtag(first).ShouldBe(SeriesBibleHarness.Etag(2));

        // The second tab still holds "sb-1": the version it read no longer exists (lost update, D9).
        using var second = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(narratorName: "Người kể thứ hai"));

        await SeriesBibleHarness.AssertProblemAsync(second, 412, "bible_conflict");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(2);
    }

    [Fact]
    public async Task PutBible_IfMatchStar_Succeeds()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // RFC 9110: `*` matches any existing version, so this is an ordinary replace and the tag moves with it
        // (02.6.3-T1 failure handling). The bible route accepts it through its own BibleEtags.Matches, which layers
        // this rule over the shared ETags.Matches — that helper alone rejects `*` (Application/Common/ETags.cs).
        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            ifMatch: "*",
            SeriesBibleHarness.Content());

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.ResponseEtag(response).ShouldBe(SeriesBibleHarness.Etag(2));
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(2);
    }

    [Fact]
    public async Task PutBible_WeakEtag_Returns412()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            $"W/{SeriesBibleHarness.Etag(1)}",
            SeriesBibleHarness.Content());

        // A weak tag is not a strong match: weak validation would let a caller overwrite a row it never read.
        await SeriesBibleHarness.AssertProblemAsync(response, 412, "bible_conflict");
    }

    [Fact]
    public async Task PutBible_ValidContent_BumpsVersionAndReturnsNewEtag()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(
                characters: new JsonArray(SeriesBibleHarness.Character(1, "Bà Tư")),
                rules: new JsonArray(SeriesBibleHarness.Rule(1))));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.ResponseEtag(response).ShouldBe(SeriesBibleHarness.Etag(2));

        // The frozen success body is { version, etag }: the version moves and the body carries the same tag as the
        // header, quotes included, so the body value is itself a valid next If-Match (02.6.3-T1 acceptance, D9).
        var body = await SeriesBibleHarness.JsonAsync(response);
        body.EnumerateObject().Select(member => member.Name).ShouldBe(["version", "etag"]);
        body.GetProperty("version").GetInt32().ShouldBe(2);
        body.GetProperty("etag").GetString().ShouldBe(SeriesBibleHarness.Etag(2), "the body tag is quoted, byte-identical to the header");

        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(2);
        SeriesBibleHarness.RowCount(Database, world.SeriesId).ShouldBe(1, "a series owns exactly one bible row");

        // ...and the read answers the document that was written, at the version the tag names.
        using var read = await SeriesBibleHarness.GetAsync(client, world.SeriesId);
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.ResponseEtag(read).ShouldBe(SeriesBibleHarness.Etag(2));

        var content = (await SeriesBibleHarness.JsonAsync(read)).GetProperty("content");
        content.GetProperty("narrator").GetProperty("persona").GetString().ShouldBe("kể chuyện đêm");
        content.GetProperty("recurring_characters")[0].GetProperty("name").GetString().ShouldBe("Bà Tư");
        content.GetProperty("rules")[0].GetProperty("id").GetString().ShouldBe("rl1");
    }

    [Fact]
    public async Task PutBible_TooManyCharacters_Returns400WithFieldPath()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        var characters = new JsonArray();
        for (var index = 1; index <= 31; index++)
        {
            characters.Add(SeriesBibleHarness.Character(index));
        }

        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(characters: characters));

        // The code stays bible_invalid_content (D8); the per-field list is what the UI renders.
        await SeriesBibleHarness.AssertProblemAsync(response, 400, "bible_invalid_content");
        var problem = await SeriesBibleHarness.ProblemAsync(response);
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("recurring_characters");
        problem.Errors["recurring_characters"].ShouldBe(["too_many"]);
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1, "a rejected document writes nothing");
    }

    [Fact]
    public async Task PutBible_UnapprovedOrUnknownEpisodeInSummaries_Returns400()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var unapproved = await SeriesBibleHarness.SeedDraftEpisodeAsync(Database, world);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // An episode of this series that has never been approved...
        using var unapprovedResponse = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(
                summaries: new JsonArray(SeriesBibleHarness.Summary(unapproved, "Tập nháp", "Tóm tắt."))));

        await SeriesBibleHarness.AssertProblemAsync(unapprovedResponse, 400, "bible_invalid_content");
        var unapprovedProblem = await SeriesBibleHarness.ProblemAsync(unapprovedResponse);
        unapprovedProblem.Errors.ShouldNotBeNull();
        unapprovedProblem.Errors!.ShouldContainKey("episode_summaries[0].episode_id");
        unapprovedProblem.Errors["episode_summaries[0].episode_id"].ShouldBe(["not_approved_episode"]);

        // ...and one no episode of the series ever had: the validator sees the approved ids and cannot tell the
        // two apart, so a foreign id takes the same code (the plan's only summary rule is "approved, same series").
        using var foreignResponse = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(
                summaries: new JsonArray(SeriesBibleHarness.Summary(Guid.CreateVersion7(), "Tập lạ", "Tóm tắt."))));

        await SeriesBibleHarness.AssertProblemAsync(foreignResponse, 400, "bible_invalid_content");
        var foreignProblem = await SeriesBibleHarness.ProblemAsync(foreignResponse);
        foreignProblem.Errors.ShouldNotBeNull();
        foreignProblem.Errors!.ShouldContainKey("episode_summaries[0].episode_id");
        foreignProblem.Errors["episode_summaries[0].episode_id"].ShouldBe(["not_approved_episode"]);
    }

    [Fact]
    public async Task PutBible_EditableSummaries_KeepsServerDerivedStructureAndApprovedAt()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var approved = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var first = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(
                summaries: new JsonArray(SeriesBibleHarness.Summary(approved, "Tập một", "Tóm tắt ban đầu."))));
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));

        var derived = SeriesBibleHarness.Row(Database, world.SeriesId).Content["episode_summaries"]![0]!;
        derived["structure"].ShouldNotBeNull("the server derives structure from the approved story version");
        derived["approved_at"].ShouldNotBeNull("the server derives the approval time from the episode");
        var structure = derived["structure"]!.ToJsonString();
        var approvedAt = derived["approved_at"]!.ToJsonString();

        // The client edits the title and summary and sends its own structure/approved_at: both are ignored.
        using var second = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(2),
            SeriesBibleHarness.Content(
                summaries: new JsonArray(SeriesBibleHarness.Summary(
                    approved,
                    "Tập một (sửa)",
                    "Tóm tắt thứ hai.",
                    derived: true))));
        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));

        var stored = SeriesBibleHarness.Row(Database, world.SeriesId).Content["episode_summaries"]![0]!;
        stored["title"]!.GetValue<string>().ShouldBe("Tập một (sửa)");
        stored["summary"]!.GetValue<string>().ShouldBe("Tóm tắt thứ hai.");
        stored["structure"]!.ToJsonString().ShouldBe(structure, "the client's structure is ignored");
        stored["approved_at"]!.ToJsonString().ShouldBe(approvedAt, "the client's approved_at is ignored");
    }

    [Fact]
    public async Task PutBible_ControlCharactersInName_Returns400()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // A name is a value the writer receives, so every C0 control is refused (prompt-metadata rule of D3/F36).
        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(
                characters: new JsonArray(SeriesBibleHarness.Character(1, "Bà\u0001Tư"))));

        await SeriesBibleHarness.AssertProblemAsync(response, 400, "bible_invalid_content");
        var problem = await SeriesBibleHarness.ProblemAsync(response);
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("recurring_characters[0].name");
        problem.Errors["recurring_characters[0].name"].ShouldBe(["invalid_structure"]);
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Fact]
    public async Task PutBible_CrossTenant_Returns404()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var stranger = factory.ClientFor(Guid.CreateVersion7());

        using var response = await SeriesBibleHarness.PutAsync(
            stranger,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(narratorName: "Kẻ lạ"));

        // D10: another user's row is 404, never 403 — and the tag would have matched for its owner.
        await SeriesBibleHarness.AssertProblemAsync(response, 404, "not_found");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Fact]
    public async Task PutBible_ConcurrentSameIfMatch_KeepsExactlyOneWrite()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var firstTab = factory.ClientFor(world.UserId);
        using var secondTab = factory.ClientFor(world.UserId);

        // Both tabs read "sb-1" and save at the same time: exactly one write wins and one is answered 412.
        var responses = await Task.WhenAll(
            SeriesBibleHarness.PutAsync(firstTab, world.SeriesId, SeriesBibleHarness.Etag(1), SeriesBibleHarness.Content(narratorName: "Người kể A")),
            SeriesBibleHarness.PutAsync(secondTab, world.SeriesId, SeriesBibleHarness.Etag(1), SeriesBibleHarness.Content(narratorName: "Người kể B")));

        try
        {
            responses.Select(response => response.StatusCode).ShouldBe(
                [HttpStatusCode.OK, HttpStatusCode.PreconditionFailed],
                ignoreOrder: true);
            SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(2, "one accepted save, not two");
            SeriesBibleHarness.AuditCount(Database, world.UserId).ShouldBe(1, "the refused save rolls its audit row back");
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}
