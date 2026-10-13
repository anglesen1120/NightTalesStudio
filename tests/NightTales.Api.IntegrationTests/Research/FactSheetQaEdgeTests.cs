using System.Net;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Research;

/// <summary>
/// The independent-QA edges of the fact-sheet read/edit/approve routes (02.5.4-T1): the exact ceilings the task
/// file names (40 facts, 10 sources), the F12 watermark across a version that was emptied, the ETag contract
/// after a second approval (a superseded row, a sibling episode's row), the check order of a re-approval the
/// task file's Notes document, and the two disclosure claims of the read path (the owner's <c>user_note</c> text
/// is never returned; a deleted source renders the <c>unavailable</c> placeholder). Written against the same
/// real host and PostgreSQL 17 as the implementer's suites and without touching them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FactSheetQaEdgeTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task PutFactSheet_StaleEtagOfTheSupersededRowAfterAnApprove_Returns412()
    {
        // The tag a first tab still holds after the sheet was re-approved: the row it names was superseded by
        // the approval, so the save must lose (D9) instead of resurrecting the old version.
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var citations = (Func<int, IReadOnlyList<Guid>>)(number => number <= 3 ? [first] : [second]);
        var previous = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, citations),
            FactSheetStatus.Approved,
            approvedBy: world.UserId,
            approvedAt: FactSheetHarness.Start);
        var draft = await FactSheetHarness.SeedSheetAsync(Database, world, 2, Cites(5, citations, "Corrected fact"));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var approved = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(draft.Id));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{previous.Id}'")
            .ShouldBe("2", "the approval supersedes the row the stale tag names");

        using var stale = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(previous.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "Fact number 1.", [first])));

        await FactSheetHarness.AssertProblemAsync(stale, 412, "fact_sheet_conflict");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(2, "a stale tag writes no version");
        FactSheetHarness.SheetsInStatus(Database, world, FactSheetStatus.Approved).ShouldBe(1);
    }

    [Fact]
    public async Task PutFactSheet_EtagOfASiblingEpisodesSheet_Returns412()
    {
        // Two episodes of one tenant: only the addressed episode's own current tag may save, so a tag copied from
        // the sibling (or from any other row) is stale, and the refusal leaks neither the sibling's row nor text.
        var world = await FactSheetHarness.SeedAsync(Database);
        var actor = new TestCurrentUser { Id = world.UserId };
        var sibling = await new EpisodeBuilder(Database, actor).CreateAsync(world.UserId, world.SeriesId, ct: FactSheetHarness.Ct);
        var siblingWorld = new World(world.UserId, world.SeriesId, sibling.Id);

        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "Owned fact.", [source])]);
        var siblingSource = await FactSheetHarness.AddFetchedUrlAsync(Database, siblingWorld);
        var siblingSheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            siblingWorld,
            1,
            [FactSheetHarness.Seed("f1", "SIBLING-CANARY fact.", [siblingSource])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(siblingSheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "Owned fact.", [source])));

        await FactSheetHarness.AssertProblemAsync(response, 412, "fact_sheet_conflict");
        var text = await response.Content.ReadAsStringAsync(FactSheetHarness.Ct);
        text.ShouldNotContain(siblingSheet.Id.ToString(), Case.Insensitive, "the refusal never echoes another row's id");
        text.ShouldNotContain("SIBLING-CANARY", Case.Insensitive);
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1, "the addressed episode keeps its one version");
        FactSheetHarness.SheetRows(Database, siblingWorld).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ApproveFactSheet_SupersededRowIsTheCurrentVersion_Returns412()
    {
        // The target must be the current draft (task file, ApproveFactSheetCommand): a superseded row that is the
        // highest version (reachable when a later version was removed out of band) is not approvable.
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [FactSheetHarness.Seed("f1", "One fact.", [source])],
            FactSheetStatus.Superseded,
            approvedBy: world.UserId,
            approvedAt: FactSheetHarness.Start);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        await FactSheetHarness.AssertProblemAsync(response, 412, "fact_sheet_conflict");
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{sheet.Id}'").ShouldBe("2");
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'fact_sheet_approved.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(0, "a refused approval raises nothing");
    }

    [Fact]
    public async Task ApproveFactSheet_ApprovedCurrentRowWhenTheEpisodeIsStoryApproved_AnswersEpisodeInvalidState()
    {
        // DEFECT-PROVING (see the QA report): the task file's Notes (decision 2) and the session contract
        // (decision 8) fix the approve order as ETag → F26 episode-state gate (409) → target-is-draft (412) →
        // F13. ApproveFactSheetCommandHandler.cs:88-99 checks the row's status (412) *before* `CanEditStory`
        // (409), so a re-approval of an approved sheet in a `story_approved` episode answers the conflict code
        // instead of the state code the F26 decision names. This test pins the documented order.
        var world = await FactSheetHarness.SeedAsync(Database, EpisodeStatus.StoryApproved);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, number => number <= 3 ? [first] : [second]),
            FactSheetStatus.Approved,
            approvedBy: world.UserId,
            approvedAt: FactSheetHarness.Start);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        await FactSheetHarness.AssertProblemAsync(response, 409, "episode_invalid_state");
    }

    [Fact]
    public async Task PutFactSheet_ExactlyFortyFacts_SavesTheVersion()
    {
        // The ceiling itself (T51/contract §13): 40 is legal, 41 is not (the implementer's suite pins the 41).
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        var facts = Enumerable.Range(0, 40)
            .Select(number => FactSheetHarness.Fact(null, $"Statement number {number}.", [source]))
            .ToArray();

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(facts));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        (await FactSheetHarness.JsonAsync(response)).GetProperty("factCount").GetInt32().ShouldBe(40);

        var ids = FactSheetHarness.StoredFactIds(Database, world, 2);
        ids.Count.ShouldBe(40);
        ids[0].ShouldBe("f2", "new facts continue from the watermark, in input order");
        ids[39].ShouldBe("f41");
        SqlProbe.ScalarText(
                Database,
                $"SELECT max_fact_id::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' AND version = 2")
            .ShouldBe("41");
    }

    [Fact]
    public async Task PutFactSheet_ExactlyTenSourceIdsOnOneFact_Saves_AndAliasesThemS1ToS10()
    {
        // The per-fact source ceiling is exactly the width of the alias scheme (s1…s10); the implementer's suite
        // pins only the 11th id.
        var world = await FactSheetHarness.SeedAsync(Database);
        const string HexDigits = "0123456789";
        var sources = new List<Guid>();
        for (var index = 0; index < 10; index++)
        {
            sources.Add(await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha(HexDigits[index])));
        }

        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [sources[0]])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(
                FactSheetHarness.Fact("f1", "One fact.", [sources[0]]),
                FactSheetHarness.Fact(null, "A fact citing ten sources.", sources)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));

        using var read = await FactSheetHarness.GetAsync(client, world.EpisodeId);
        var facts = (await FactSheetHarness.JsonAsync(read)).GetProperty("facts").EnumerateArray().ToArray();
        var cited = facts.Single(fact => fact.GetProperty("id").GetString() == "f2").GetProperty("sources").EnumerateArray().ToArray();
        cited.Length.ShouldBe(10);
        cited.Select(reference => reference.GetProperty("alias").GetString()).ShouldBe(
            ["s1", "s2", "s3", "s4", "s5", "s6", "s7", "s8", "s9", "s10"],
            "the aliases are handed out in first-citation order and every one of them is reused for the kept fact");
        cited.Select(reference => reference.GetProperty("sourceId").GetGuid()).ShouldBe(sources);
    }

    [Fact]
    public async Task PutFactSheet_WatermarkSurvivesAnEmptiedVersion_NextNewFactContinuesAfterTheRetiredIds()
    {
        // F12 across the emptiest boundary: deleting every fact saves an empty version, and the watermark it
        // carries must still retire f1 and f2, so the next added fact is f3 — never a recycled id.
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [FactSheetHarness.Seed("f1", "Kept fact.", [source]), FactSheetHarness.Seed("f2", "Doomed fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var emptied = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.EmptyFactsBody);
        emptied.StatusCode.ShouldBe(HttpStatusCode.OK, await emptied.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        SqlProbe.ScalarText(
                Database,
                $"SELECT max_fact_id::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' AND version = 2")
            .ShouldBe("2", "the watermark is carried into the empty version");

        using var added = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.ResponseEtag(emptied),
            FactSheetHarness.Body(FactSheetHarness.Fact(null, "A fresh fact.", [source])));

        added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        FactSheetHarness.StoredFactIds(Database, world, 3).ShouldBe(["f3"], "the retired f1 and f2 are never handed out again (F12)");
        SqlProbe.ScalarText(
                Database,
                $"SELECT max_fact_id::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' ORDER BY version DESC LIMIT 1")
            .ShouldBe("3");
    }

    [Fact]
    public async Task PutFactSheet_OnASupersededCurrentRow_CreatesTheNextDraft()
    {
        // FactSheet.CreateUserDraft documents that the current row's status does not matter: editing is how the
        // next draft begins, so the highest version being superseded still yields version + 1 as a draft.
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [FactSheetHarness.Seed("f1", "One fact.", [source])],
            FactSheetStatus.Superseded,
            approvedBy: world.UserId,
            approvedAt: FactSheetHarness.Start);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "One fact, corrected.", [source])));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        var body = await FactSheetHarness.JsonAsync(response);
        body.GetProperty("version").GetInt32().ShouldBe(2);
        body.GetProperty("status").GetString().ShouldBe("draft");
        FactSheetHarness.SheetsInStatus(Database, world, FactSheetStatus.Superseded).ShouldBe(1, "the superseded row is history");
    }

    [Fact]
    public async Task PutFactSheet_OnAnApprovedSheet_ThenApproveTheNewDraft_SupersedesTheOldApprovedRow()
    {
        // The whole 02 §4 chain over HTTP: the first edit of an approved sheet is a new draft, and approving that
        // draft is what demotes the older approved row — exactly one approved row per episode at every step.
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var citations = (Func<int, IReadOnlyList<Guid>>)(number => number <= 3 ? [first] : [second]);
        var approved = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, citations),
            FactSheetStatus.Approved,
            approvedBy: world.UserId,
            approvedAt: FactSheetHarness.Start);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var edit = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(approved.Id),
            FactSheetHarness.Body(
                [
                    .. Enumerable.Range(1, 5).Select(number => FactSheetHarness.Fact(
                        $"f{number}",
                        number == 1 ? "Fact number 1, corrected." : $"Fact number {number}.",
                        citations(number))),
                ]));
        edit.StatusCode.ShouldBe(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{approved.Id}'")
            .ShouldBe("1", "the edit that follows an approval leaves it approved until the new draft is approved");
        FactSheetHarness.SheetsInStatus(Database, world, FactSheetStatus.Superseded).ShouldBe(0);

        using var approval = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.ResponseEtag(edit));

        approval.StatusCode.ShouldBe(HttpStatusCode.OK, await approval.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{approved.Id}'").ShouldBe("2");
        SqlProbe.ScalarText(
                Database,
                $"SELECT status::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' ORDER BY version DESC LIMIT 1")
            .ShouldBe("1");
        FactSheetHarness.SheetsInStatus(Database, world, FactSheetStatus.Approved).ShouldBe(1);
    }

    [Fact]
    public async Task GetFactSheet_NeverReturnsTheUserNotesText()
    {
        // 02 §8 / the DTO contract: a user_note contributes facts by construction, but its note_text is the
        // owner's own material and must never travel back through the fact-sheet read (only the neutral label).
        const string Canary = "CANARY-NOTE-TEXT-THE-OWNER-WROTE";
        var world = await FactSheetHarness.SeedAsync(Database);
        var article = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var note = await FactSheetHarness.AddNoteAsync(Database, world, Canary);
        await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [
                FactSheetHarness.Seed("f1", "A fact from the article.", [article]),
                FactSheetHarness.Seed("f2", "A fact from the owner's note.", [note]),
            ]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.GetAsync(client, world.EpisodeId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        var text = await response.Content.ReadAsStringAsync(FactSheetHarness.Ct);
        text.ShouldNotContain(Canary, Case.Insensitive, "the read returns the citation, never the note's text");

        var noteRef = (await FactSheetHarness.JsonAsync(response)).GetProperty("facts").EnumerateArray()
            .Single(fact => fact.GetProperty("id").GetString() == "f2")
            .GetProperty("sources").EnumerateArray().Single();
        noteRef.GetProperty("sourceId").GetGuid().ShouldBe(note);
        noteRef.GetProperty("isUserNote").GetBoolean().ShouldBeTrue();
        noteRef.GetProperty("title").GetString().ShouldBe("note");
        noteRef.GetProperty("url").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetFactSheet_CitedSourceDeleted_RendersTheUnavailablePlaceholder()
    {
        // Decision 3 of the task file's Notes: a citation whose source is gone renders the neutral `unavailable`
        // label with no URL, so the reader can tell the version still cites it (the implementer's suite pins only
        // the id).
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "Kept fact.", [source])]);
        SqlProbe.Execute(Database, $"DELETE FROM story_sources WHERE id = '{source}'");

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.GetAsync(client, world.EpisodeId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        var reference = (await FactSheetHarness.JsonAsync(response)).GetProperty("facts").EnumerateArray().Single()
            .GetProperty("sources").EnumerateArray().Single();
        reference.GetProperty("sourceId").GetGuid().ShouldBe(source);
        reference.GetProperty("title").GetString().ShouldBe("unavailable");
        reference.GetProperty("url").ValueKind.ShouldBe(JsonValueKind.Null);
        reference.GetProperty("isUserNote").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task PutFactSheet_CitingAnotherTenantsSource_Returns400WithoutEchoingTheForeignSource()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var stranger = await FactSheetHarness.SeedAsync(Database);
        var foreign = await FactSheetHarness.AddFetchedUrlAsync(Database, stranger);
        var own = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "Owned fact.", [own])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "CANARY-FOREIGN-SOURCE fact.", [foreign])));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        var text = await response.Content.ReadAsStringAsync(FactSheetHarness.Ct);
        text.ShouldNotContain(foreign.ToString(), Case.Insensitive, "the 400 names the field, never the foreign id");
        text.ShouldNotContain("CANARY-FOREIGN-SOURCE", Case.Insensitive);
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Fact]
    public async Task PutFactSheet_AnotherTenantsEpisode_Returns404WithoutEchoingItsSheet()
    {
        var owner = await FactSheetHarness.SeedAsync(Database);
        var stranger = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, owner);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, owner, 1, [FactSheetHarness.Seed("f1", "CANARY-OWNER fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(stranger.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            owner.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "Hijacked fact.", [source])));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "another tenant's episode is a 404, never a 403");
        var text = await response.Content.ReadAsStringAsync(FactSheetHarness.Ct);
        text.ShouldNotContain(sheet.Id.ToString(), Case.Insensitive, "the 404 never confirms that a sheet exists");
        text.ShouldNotContain("CANARY-OWNER", Case.Insensitive);
        FactSheetHarness.SheetRows(Database, owner).Count.ShouldBe(1);
        FactSheetHarness.StoredFactIds(Database, owner, 1).ShouldBe(["f1"]);
    }

    [Fact]
    public async Task ApproveFactSheet_AfterANewerDraftVersionExists_TheOlderTagIsStale_AndTheNewDraftApproves()
    {
        // Failure handling: a generate run that committed its own version while the owner was reading leaves the
        // owner's tag naming the previous draft, so that approval answers 412 and the owner re-approves the row
        // the read now returns.
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var citations = (Func<int, IReadOnlyList<Guid>>)(number => number <= 3 ? [first] : [second]);
        var edited = await FactSheetHarness.SeedSheetAsync(Database, world, 1, Cites(5, citations));
        var runDraft = await FactSheetHarness.SeedSheetAsync(Database, world, 2, Cites(5, citations, "Run-extracted fact"));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var stale = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(edited.Id));
        await FactSheetHarness.AssertProblemAsync(stale, 412, "fact_sheet_conflict");

        using var read = await FactSheetHarness.GetAsync(client, world.EpisodeId);
        var current = await FactSheetHarness.JsonAsync(read);
        current.GetProperty("id").GetGuid().ShouldBe(runDraft.Id, "the read returns the newer version");

        using var reapprove = await FactSheetHarness.ApproveAsync(
            client,
            world.EpisodeId,
            current.GetProperty("etag").GetString());

        reapprove.StatusCode.ShouldBe(HttpStatusCode.OK, await reapprove.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{runDraft.Id}'").ShouldBe("1");
        FactSheetHarness.SheetsInStatus(Database, world, FactSheetStatus.Approved).ShouldBe(1);
    }

    [Fact]
    public async Task PutFactSheet_UnknownConfidenceValue_IsRefusedWithoutWritingAVersion()
    {
        // Task-file Notes decision 10: an unknown `confidence` is refused by the enum converter before the handler
        // runs, so the answer is a 400 the handler never produced — never a 500 and never a saved version.
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        var body = $$"""
            {"facts":[{"id":"f1","statement":"One fact.","sourceIds":["{{source}}"],"confidence":"ultra"}]}
            """;

        using var response = await FactSheetHarness.PutAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id), body);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            $"an unusable confidence is a client error, got {response.StatusCode}: {await response.Content.ReadAsStringAsync(FactSheetHarness.Ct)}");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1, "a refused body writes no version");
    }

    [Fact]
    public async Task PutFactSheet_AndApprove_CarryNoStatementTextIntoTheOutboxOrTheAuditTrail()
    {
        // The outbox payload and the audit row are ids-and-metadata documents (contract §15, 09 README BD11): the
        // material the owner typed must not land in either, only in the fact-sheet row itself.
        const string StatementCanary = "CANARY-EDITED-STATEMENT";
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var citations = (Func<int, IReadOnlyList<Guid>>)(number => number <= 3 ? [first] : [second]);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, Cites(5, citations));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var edit = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(
                [
                    .. Enumerable.Range(1, 5).Select(number => FactSheetHarness.Fact(
                        $"f{number}",
                        number == 1 ? $"{StatementCanary} number 1." : $"Fact number {number}.",
                        citations(number))),
                ]));
        edit.StatusCode.ShouldBe(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync(FactSheetHarness.Ct));

        using var approval = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.ResponseEtag(edit));
        approval.StatusCode.ShouldBe(HttpStatusCode.OK, await approval.Content.ReadAsStringAsync(FactSheetHarness.Ct));

        var outbox = SqlProbe.ScalarText(
            Database,
            "SELECT coalesce(string_agg(payload::text, ' | '), '') FROM outbox_messages "
            + $"WHERE user_id = '{world.UserId}' AND type IN ('fact_sheet_saved.v1', 'fact_sheet_approved.v1')");
        outbox.ShouldNotContain(StatementCanary, Case.Insensitive, "the outbox carries ids only");
        outbox.ShouldContain(sheet.EpisodeId.ToString(), Case.Insensitive, "the event still names the episode");

        var audit = SqlProbe.ScalarText(
            Database,
            "SELECT coalesce(string_agg(data::text, ' | '), '') FROM audit_logs "
            + $"WHERE subject_user_id = '{world.UserId}' AND action IN ('episode.fact_sheet_saved', 'episode.fact_sheet_approved')");
        audit.ShouldNotContain(StatementCanary, Case.Insensitive, "the audit row carries ids and metadata only");
    }

    /// <summary>
    /// Builds <paramref name="count"/> facts numbered <c>f1…fN</c>, each citing what <paramref name="citations"/>
    /// picks for its number.
    /// </summary>
    private static FactSeed[] Cites(
        int count,
        Func<int, IReadOnlyList<Guid>> citations,
        string statementPrefix = "Fact number") =>
        [
            .. Enumerable.Range(1, count).Select(number => FactSheetHarness.Seed(
                $"f{number}",
                $"{statementPrefix} {number}.",
                citations(number))),
        ];
}
