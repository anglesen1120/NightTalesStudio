using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using NightTales.Api.IntegrationTests.Persistence;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// Independent QA of <c>GET/PUT /api/v1/series/{id}/bible</c> (02.6.3-T1): the negative, boundary and exploratory
/// cases the feature's own tests leave open — a body that is not a document at all, a body over the content budget
/// and a body over the host's request cap, the <c>If-Match</c> variants, the client-sent ids, the summaries of an
/// episode of another series or of one that was removed, a pending proposal, an archived series, and the cascade a
/// series delete leaves behind. Where a case only records shipped behaviour it says so.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SeriesBibleQaEdgeTests(TestDatabase database) : IntegrationTest(database)
{
    private const int ContentBudget = 65536;
    private const int HostBodyCap = 1024 * 1024;
    private const string Sentinel = "QA-SENTINEL-BIBLE-BODY";

    [Fact]
    public async Task PutBible_BodyWithoutContent_Returns400WithTheBodyField()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutRawAsync(client, world.SeriesId, SeriesBibleHarness.Etag(1), "{\"content\":null}");

        await SeriesBibleHarness.AssertProblemAsync(response, 400, "bible_invalid_content");
        var problem = await SeriesBibleHarness.ProblemAsync(response);
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("body", "a body that carries no document is the root 'body' field");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Fact]
    public async Task PutBible_ContentWithoutNarrator_Returns400OnThePersonaPath()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // No narrator at all, and a binder rather than a validator must not be what refuses it.
        using var absent = await PutRawAsync(client, world.SeriesId, SeriesBibleHarness.Etag(1), "{\"content\":{}}");
        await SeriesBibleHarness.AssertProblemAsync(absent, 400, "bible_invalid_content");
        (await SeriesBibleHarness.ProblemAsync(absent)).Errors!["narrator.persona"].ShouldBe(["empty"]);

        // The narrator as an explicit JSON null takes the same path.
        using var explicitNull = await PutRawAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            "{\"content\":{\"narrator\":null,\"recurring_characters\":[],\"places\":[],\"rules\":[],\"episode_summaries\":[]}}");
        await SeriesBibleHarness.AssertProblemAsync(explicitNull, 400, "bible_invalid_content");
        (await SeriesBibleHarness.ProblemAsync(explicitNull)).Errors!["narrator.persona"].ShouldBe(["empty"]);

        // An untouched GET-then-PUT is exactly this body: the fresh row's persona is empty (the open item of the
        // task file), so the first save must supply one and nothing is written by either attempt.
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Fact]
    public async Task PutBible_NullArrays_AreStoredEmpty()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutRawAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            "{\"content\":{\"narrator\":{\"name\":null,\"persona\":\"kể chuyện đêm\",\"voice_notes\":null},"
            + "\"recurring_characters\":null,\"places\":null,\"rules\":null,\"episode_summaries\":null}}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));

        var content = SeriesBibleHarness.Row(Database, world.SeriesId).Content!;
        foreach (var array in new[] { "recurring_characters", "places", "rules", "episode_summaries" })
        {
            content[array]!.AsArray().Count.ShouldBe(0, $"a null {array} is stored as an empty array");
        }

        content["narrator"]!["name"]!.ShouldBeNull();
        content["narrator"]!["voice_notes"]!.GetValue<string>().ShouldBeEmpty();
    }

    [Fact]
    public async Task PutBible_MalformedJson_Returns400ValidationFailed()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutRawAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            "{\"content\":{\"narrator\":{\"persona\":\"kể chuyện đêm\"}");

        // A body the binder cannot read is a malformed request (400 validation_failed), never a 500 and never a
        // silent write: the endpoint's own bible_invalid_content answer is for a body that parsed.
        await SeriesBibleHarness.AssertProblemAsync(response, 400, "validation_failed");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PutBible_EmptyOrBlankIfMatch_Returns428(string ifMatch)
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // A header that is present but names nothing is a missing precondition (428), not a stale one (412).
        using var response = await PutRawAsync(
            client,
            world.SeriesId,
            ifMatch,
            Envelope(SeriesBibleHarness.Content()));

        await SeriesBibleHarness.AssertProblemAsync(response, 428, "if_match_required");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Theory]
    [InlineData("sb-1")]
    [InlineData("\"SB-1\"")]
    [InlineData("\"sb-1\", \"sb-2\"")]
    [InlineData("\"sb-1\" \"sb-2\"")]
    public async Task PutBible_UnquotedOrListedIfMatch_Returns412(string ifMatch)
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // Only the exact quoted tag matches (the shared ETags.Matches): an unquoted value, a different case and a
        // list are all refused as stale copies, exactly like a tag of another version.
        using var response = await PutRawAsync(
            client,
            world.SeriesId,
            ifMatch,
            Envelope(SeriesBibleHarness.Content()));

        await SeriesBibleHarness.AssertProblemAsync(response, 412, "bible_conflict");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Fact]
    public async Task PutBible_WithTwoIfMatchHeaders_Returns412()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutRawAsync(
            client,
            world.SeriesId,
            [SeriesBibleHarness.Etag(1), SeriesBibleHarness.Etag(2)],
            Envelope(SeriesBibleHarness.Content()));

        await SeriesBibleHarness.AssertProblemAsync(response, 412, "bible_conflict");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Fact]
    public async Task PutBible_DocumentOverTheContentBudget_Returns400TooLongOnTheBodyPath()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // Every field is over its own limit too, which is the point: the budget error is added alongside them, on
        // the root 'body' path, and the refusal is a 400 problem (not the host's 413: this body is under the cap).
        var characters = new JsonArray();
        for (var index = 1; index <= 30; index++)
        {
            characters.Add(SeriesBibleHarness.Character(
                index,
                new string('a', 2000),
                new string('b', 3000)));
        }

        var body = Envelope(SeriesBibleHarness.Content(characters: characters));
        body.Length.ShouldBeGreaterThan(ContentBudget, "the document must exceed the content budget");
        body.Length.ShouldBeLessThan(HostBodyCap, "and stay under the host's request cap");

        using var response = await PutRawAsync(client, world.SeriesId, SeriesBibleHarness.Etag(1), body);

        await SeriesBibleHarness.AssertProblemAsync(response, 400, "bible_invalid_content");
        var problem = await SeriesBibleHarness.ProblemAsync(response);
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("body");
        problem.Errors["body"].ShouldBe(["too_long"]);
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Fact]
    public async Task PutBible_DeclaredBodyOverTheHostCap_Returns413PayloadTooLarge_WithoutEchoingIt()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // The host's cap (RequestLimits:MaxRequestBodySize, 1 MiB) is checked on the declared length, so this body
        // is refused with the catalogued problem before a handler sees it — the clean error the endpoint's
        // documented 413 promises, and it never echoes the payload.
        var padded = Sentence(Sentinel, HostBodyCap + 1);
        using var response = await PutRawAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(characters: new JsonArray(SeriesBibleHarness.Character(1, padded))).ToJsonString());

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        var problem = await SeriesBibleHarness.ProblemAsync(response);
        problem.Code.ShouldBe("payload_too_large");
        (await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct)).ShouldNotContain(Sentinel);
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Fact]
    public async Task PutBible_UnicodeComposedDuplicateNames_Returns400Duplicate()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // Composed ("Bà Tư") and decomposed ("Ba" + U+0300, "Tu" + U+031B) spellings of one name: the normaliser
        // composes before it compares, so the second entry is the duplicate the UI must not be able to save.
        var decomposed = "Ba\u0300 Tu\u031b";
        decomposed.ShouldNotBe("Bà Tư", "the two spellings are different strings on the wire");

        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(
                characters: new JsonArray(
                    SeriesBibleHarness.Character(1, "Bà Tư"),
                    SeriesBibleHarness.Character(2, decomposed))));

        await SeriesBibleHarness.AssertProblemAsync(response, 400, "bible_invalid_content");
        (await SeriesBibleHarness.ProblemAsync(response)).Errors!["recurring_characters[1].name"]
            .ShouldBe(["duplicate"]);
    }

    [Fact]
    public async Task PutBible_LoneSurrogateInAName_IsRefusedAsAMalformedBody()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // The escape for an unpaired surrogate travels, but System.Text.Json refuses to read it as UTF-16 ("Cannot
        // read incomplete UTF-16 JSON text as string with missing low surrogate"), so the malformed-body 400 is the
        // boundary this wire case meets. The Domain no longer depends on that boundary: the validator reports the
        // field as `invalid_structure` and the normaliser drops the unpaired unit instead of letting ICU throw
        // (02.6.3-T1 QA defect D2, pinned by BibleContentValidatorTests and BibleOpNormalizerTests), so nothing is
        // written either way.
        var body = "{\"content\":{\"narrator\":{\"persona\":\"kể chuyện đêm\"},\"recurring_characters\":"
            + "[{\"id\":\"ch1\",\"name\":\"Bà Tư\\ud800\",\"description\":\"Mô tả.\",\"first_episode_id\":null}],"
            + "\"places\":[],\"rules\":[],\"episode_summaries\":[]}}";

        using var response = await PutRawAsync(client, world.SeriesId, SeriesBibleHarness.Etag(1), body);

        await SeriesBibleHarness.AssertProblemAsync(response, 400, "validation_failed");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Fact]
    public async Task PutBible_ClientSentCharacterIds_AreCheckedForShapeAndUniqueness()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // CONTRACT (02.6.3-T1, QA fix): a bible id travels into the writer's data object, so a client-sent id is
        // no longer stored verbatim. It must match its array's prefix (`ch<n>`, `pl<n>`, `rl<n>`) and be unique
        // within the array; only a blank one is filled by the handler. Here the first id has no prefix and the
        // third repeats the second.
        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(
                characters: new JsonArray(
                    Character("zzz", "Nhân vật A"),
                    Character("ch1", "Nhân vật B"),
                    Character("ch1", "Nhân vật C"))));

        await SeriesBibleHarness.AssertProblemAsync(response, 400, "bible_invalid_content");
        var problem = await SeriesBibleHarness.ProblemAsync(response);
        problem.Errors!["recurring_characters[0].id"].ShouldBe(["invalid_structure"]);
        problem.Errors!["recurring_characters[2].id"].ShouldBe(["duplicate"]);
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1, "a refused document writes nothing");
    }

    [Fact]
    public async Task PutBible_RepeatedClientSentCharacterId_Returns400DuplicateOnTheSecondEntry()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // The shape of the duplicate the id rule (2) refuses: the same conforming id twice. The field path names
        // the second entry, and the first one keeps the id the client sent.
        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(
                characters: new JsonArray(
                    Character("ch1", "Nhân vật A"),
                    Character("ch1", "Nhân vật B"))));

        await SeriesBibleHarness.AssertProblemAsync(response, 400, "bible_invalid_content");
        (await SeriesBibleHarness.ProblemAsync(response)).Errors!["recurring_characters[1].id"].ShouldBe(["duplicate"]);
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1, "a refused document writes nothing");
    }

    [Fact]
    public async Task PutBible_BlankOrAbsentCharacterId_IsAssignedTheNextFreeOne()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using (var first = await SeriesBibleHarness.PutAsync(
                   client,
                   world.SeriesId,
                   SeriesBibleHarness.Etag(1),
                   SeriesBibleHarness.Content(characters: new JsonArray(Character("ch1", "Nhân vật A")))))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        }

        // The UI adds two entries without knowing their ids: one sends a blank `id`, the other omits the member
        // entirely. The handler assigns max(request ids, stored ids) + 1 per prefix before validation, so the
        // client never has to allocate an id itself.
        var withoutId = new JsonObject
        {
            ["name"] = "Nhân vật C",
            ["description"] = "Mô tả.",
            ["first_episode_id"] = null,
        };

        using var second = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(2),
            SeriesBibleHarness.Content(
                characters: new JsonArray(Character("ch1", "Nhân vật A"), Character(string.Empty, "Nhân vật B"), withoutId)));

        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.Row(Database, world.SeriesId).Content!["recurring_characters"]!
            .AsArray().Select(item => item!["id"]!.GetValue<string>()).ShouldBe(["ch1", "ch2", "ch3"]);
    }

    [Fact]
    public async Task PutBible_SummaryOfAnotherSeriesApprovedEpisode_Returns400NotApprovedEpisode()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // An episode that IS approved, of the same user, but of another series: the acceptance row's "foreign
        // episode"; the validator only ever sees this series' approved ids, so it cannot be told from an unknown one.
        var actor = new TestCurrentUser { Id = world.UserId };
        var other = await new SeriesBuilder(Database, actor, new FakeClock(SeriesBibleHarness.Start))
            .CreateAsync(world.UserId, title: "Chuỗi khác", ct: SeriesBibleHarness.Ct);
        var foreign = await SeriesBibleHarness.SeedApprovedEpisodeAsync(
            Database,
            new World(world.UserId, other.Id));

        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(
                summaries: new JsonArray(SeriesBibleHarness.Summary(foreign, "Tập của chuỗi khác", "Tóm tắt."))));

        await SeriesBibleHarness.AssertProblemAsync(response, 400, "bible_invalid_content");
        (await SeriesBibleHarness.ProblemAsync(response)).Errors!["episode_summaries[0].episode_id"]
            .ShouldBe(["not_approved_episode"]);
    }

    [Fact]
    public async Task PutBible_DeletedApprovedEpisode_RefusesItsSummaryAndAllowsItsRemoval()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episode = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using (var first = await SeriesBibleHarness.PutAsync(
                   client,
                   world.SeriesId,
                   SeriesBibleHarness.Etag(1),
                   SeriesBibleHarness.Content(
                       summaries: new JsonArray(SeriesBibleHarness.Summary(episode, "Tập một", "Tóm tắt ban đầu.")))))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        }

        // The episode goes (drift: nothing deletes an episode through the API in P). The stored summary survives in
        // the document, but its episode is no longer an approved one of this series.
        var key = episode.ToString("D", CultureInfo.InvariantCulture);
        SqlProbe.Execute(Database, $"DELETE FROM episodes WHERE id = '{key}'");

        // Editing it is refused, even though the server values are kept from the stored entry...
        using var kept = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(2),
            SeriesBibleHarness.Content(
                summaries: new JsonArray(SeriesBibleHarness.Summary(episode, "Tập một (sửa)", "Tóm tắt thứ hai."))));

        await SeriesBibleHarness.AssertProblemAsync(kept, 400, "bible_invalid_content");
        (await SeriesBibleHarness.ProblemAsync(kept)).Errors!["episode_summaries[0].episode_id"]
            .ShouldBe(["not_approved_episode"]);

        // ...while dropping the orphan summary is allowed (a wrong summary must be fixable).
        using var removed = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(2),
            SeriesBibleHarness.Content());

        removed.StatusCode.ShouldBe(HttpStatusCode.OK, await removed.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.Row(Database, world.SeriesId).Content!["episode_summaries"]!
            .AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task PutBible_WithPendingProposal_KeepsItAndBumpsOnlyTheVersion()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episode = await SeriesBibleHarness.SeedDraftEpisodeAsync(Database, world);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // A pending proposal, written the way the T2 job writes one (there is no route for it yet).
        const string proposalJson =
            """
            {"ops":[{"kind":"add_rule","summary":null,"name":null,"description":null,
                     "text":"Không nói tên thật.","reason":"từ tập một"}],
             "based_on_version":1,"proposed_at":"2026-10-14T09:00:00+00:00"}
            """;
        var episodeKey = episode.ToString("D", CultureInfo.InvariantCulture);
        var seriesKey = world.SeriesId.ToString("D", CultureInfo.InvariantCulture);
        SqlProbe.Execute(
            Database,
            "UPDATE series_bibles SET proposed_content = '" + proposalJson + "'::jsonb, "
            + "proposed_from_episode_id = '" + episodeKey + "' WHERE series_id = '" + seriesKey + "'");

        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(narratorName: "Người kể sau đề xuất"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(2);

        // The manual save keeps the proposal and does not move its base version: the ops stay append-only (plan §4 U2).
        using var read = await SeriesBibleHarness.GetAsync(client, world.SeriesId);
        var proposal = (await SeriesBibleHarness.JsonAsync(read)).GetProperty("proposal");
        proposal.ValueKind.ShouldNotBe(System.Text.Json.JsonValueKind.Null, "a pending proposal survives a manual save");
        proposal.GetProperty("basedOnVersion").GetInt32().ShouldBe(1);
        proposal.GetProperty("ops")[0].GetProperty("kind").GetString().ShouldBe("add_rule");
        proposal.GetProperty("preview").GetProperty("rules").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task PutBible_ArchivedSeries_IsAllowed()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        SqlProbe.Execute(
            Database,
            $"UPDATE series SET archived_at = now() WHERE id = '{world.SeriesId.ToString("D", CultureInfo.InvariantCulture)}'");

        // The bible route has no archived rule (plan §3.3 failure table: the owner may tidy an archived series).
        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content(narratorName: "Người kể của chuỗi đã lưu trữ"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(2);
    }

    [Fact]
    public async Task DeleteOfTheSeries_CascadesTheBibleRow()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);

        SqlProbe.Execute(Database, $"DELETE FROM series WHERE id = '{world.SeriesId.ToString("D", CultureInfo.InvariantCulture)}'");

        // Retention (plan §8): the bible is deleted with its series, through the same-owner composite FK.
        SeriesBibleHarness.RowCount(Database, world.SeriesId).ShouldBe(0, "the bible row goes with its series");
    }

    [Fact]
    public async Task PutBible_MissingRow_Returns500SeriesBibleMissing_AndCreatesNothing()
    {
        var world = await SeriesBibleHarness.SeedSeriesWithoutBibleAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content());

        await SeriesBibleHarness.AssertProblemAsync(response, 500, "series_bible_missing");
        SeriesBibleHarness.RowCount(Database, world.SeriesId).ShouldBe(0, "a write never creates the missing row");
    }

    [Fact]
    public async Task BibleRoutes_Anonymous_Return401()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.AnonymousClient();

        using var read = await SeriesBibleHarness.GetAsync(client, world.SeriesId);
        read.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var write = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content());

        write.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
    }

    [Fact]
    public async Task PutBible_UnchangedBodyWithTheNewTag_StillSavesAVersion()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);
        var body = SeriesBibleHarness.Content(narratorName: "Người kể không đổi");

        using (var first = await SeriesBibleHarness.PutAsync(client, world.SeriesId, SeriesBibleHarness.Etag(1), body))
        {
            SeriesBibleHarness.ResponseEtag(first).ShouldBe(SeriesBibleHarness.Etag(2));
        }

        // The route takes no Idempotency-Key: the ETag is the token, so an identical document under the tag the
        // first save returned is another version, and the same document under the OLD tag is the 412 (asserted by
        // the feature's own PutBible_RepeatSameIfMatch_Second412).
        using var second = await SeriesBibleHarness.PutAsync(client, world.SeriesId, SeriesBibleHarness.Etag(2), body);

        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.ResponseEtag(second).ShouldBe(SeriesBibleHarness.Etag(3));
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(3);
    }

    [Fact]
    public async Task PutBible_ClientSentSchemaVersion_IsIgnored()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        var content = SeriesBibleHarness.Content();
        content["schema_version"] = "bible.v9";

        using var response = await SeriesBibleHarness.PutAsync(client, world.SeriesId, SeriesBibleHarness.Etag(1), content);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.Row(Database, world.SeriesId).Content!["schema_version"]!
            .GetValue<string>().ShouldBe("bible.v1", "the backend writes its own schema version");

        using var read = await SeriesBibleHarness.GetAsync(client, world.SeriesId);
        (await SeriesBibleHarness.JsonAsync(read)).GetProperty("content").GetProperty("schema_version")
            .GetString().ShouldBe("bible.v1");
    }

    /// <summary>
    /// 02.6.3-T1 (D9, QA): the tag is the quoted <c>"sb-n"</c> in every direction — the <c>ETag</c> header, the write
    /// body, the read DTO and <c>If-Match</c> — so the body value echoed back as the next <c>If-Match</c> succeeds,
    /// while the bare <c>sb-n</c> never matches (the comparison is exact and quotes are part of the tag).
    /// </summary>
    [Fact]
    public async Task PutBible_EtagForms_AreQuotedEverywhere_AndTheBareFormIsRefused()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var read = await SeriesBibleHarness.GetAsync(client, world.SeriesId);
        (await SeriesBibleHarness.JsonAsync(read)).GetProperty("etag").GetString().ShouldBe("\"sb-1\"");
        SeriesBibleHarness.ResponseEtag(read).ShouldBe("\"sb-1\"");

        using var put = await SeriesBibleHarness.PutAsync(client, world.SeriesId, SeriesBibleHarness.Etag(1), SeriesBibleHarness.Content());
        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        var headerTag = SeriesBibleHarness.ResponseEtag(put);
        headerTag.ShouldBe("\"sb-2\"");
        var bodyTag = (await SeriesBibleHarness.JsonAsync(put)).GetProperty("etag").GetString();
        bodyTag.ShouldBe(headerTag, "the write body carries the same quoted tag the header does");

        using var readBack = await SeriesBibleHarness.GetAsync(client, world.SeriesId);
        (await SeriesBibleHarness.JsonAsync(readBack)).GetProperty("etag").GetString()
            .ShouldBe(headerTag, "the read body agrees with the write body and the header");

        // The body value is itself the exact If-Match the next save needs.
        using var echoed = await SeriesBibleHarness.PutAsync(client, world.SeriesId, bodyTag!, SeriesBibleHarness.Content(narratorName: "Người kể thứ ba"));
        echoed.StatusCode.ShouldBe(HttpStatusCode.OK, "the write body's own tag is accepted as the next If-Match");
        SeriesBibleHarness.ResponseEtag(echoed).ShouldBe(SeriesBibleHarness.Etag(3));
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(3);

        // The bare form names the same version but is still refused: the tag is quoted, and the match is exact.
        using var bare = await SeriesBibleHarness.PutAsync(client, world.SeriesId, "sb-3", SeriesBibleHarness.Content(narratorName: "Người kể thứ tư"));
        await SeriesBibleHarness.AssertProblemAsync(bare, 412, "bible_conflict");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(3, "the refused write changed nothing");
    }

    /// <summary>One recurring character with an explicit id, for the id cases the harness's numbered helper cannot express.</summary>
    private static JsonObject Character(string id, string name) =>
        new()
        {
            ["id"] = id,
            ["name"] = name,
            ["description"] = "Mô tả.",
            ["first_episode_id"] = null,
        };

    /// <summary>The request envelope of a raw <c>bible.v1</c> body: the content under its <c>content</c> member.</summary>
    private static string Envelope(JsonObject content) =>
        new JsonObject { ["content"] = content.DeepClone() }.ToJsonString();

    /// <summary>
    /// <c>PUT</c> with a raw JSON body and one <c>If-Match</c> header per value, so the cases the harness's
    /// <see cref="JsonObject"/> helper cannot express — a malformed body, a body over a cap, a header that is
    /// present but empty, two header lines — reach the route as sent.
    /// </summary>
    private static async Task<HttpResponseMessage> PutRawAsync(
        HttpClient client,
        Guid seriesId,
        string ifMatch,
        string rawBody) =>
        await PutRawAsync(client, seriesId, [ifMatch], rawBody);

    /// <summary><c>PUT</c> with a raw body and each of <paramref name="ifMatchValues"/> as its own header line.</summary>
    private static async Task<HttpResponseMessage> PutRawAsync(
        HttpClient client,
        Guid seriesId,
        IReadOnlyList<string> ifMatchValues,
        string rawBody)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, SeriesBibleHarness.Url(seriesId))
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
        };

        foreach (var value in ifMatchValues)
        {
            request.Headers.TryAddWithoutValidation("If-Match", value);
        }

        return await client.SendAsync(request, SeriesBibleHarness.Ct);
    }

    /// <summary>A body of <paramref name="bytes"/> characters that starts with <paramref name="sentinel"/>.</summary>
    private static string Sentence(string sentinel, int bytes)
    {
        var buffer = new StringBuilder(bytes);
        buffer.Append(sentinel);

        while (buffer.Length < bytes)
        {
            buffer.Append('a');
        }

        return buffer.ToString();
    }
}
