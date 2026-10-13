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
/// <c>PUT /api/v1/episodes/{id}/fact-sheet</c> against the real host and a real PostgreSQL 17 (02.5.4-T1):
/// the <c>If-Match</c> contract (428/412), the F26 episode-state gate, one new version per edit with the
/// previous row untouched (D9), fact-id stability (F12) and the T53 confidence rules. The body-validation
/// risk list lives in <see cref="FactSheetPutValidationTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FactSheetPutEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task PutFactSheet_MissingIfMatch_Returns428IfMatchRequired()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            ifMatch: null,
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "One fact.", [source])));

        await FactSheetHarness.AssertProblemAsync(response, 428, "if_match_required");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1, "a refused edit writes no version");
        FactSheetHarness.CurrentSheetId(Database, world).ShouldBe(sheet.Id);
    }

    [Fact]
    public async Task PutFactSheet_StaleEtag_Returns412FactSheetConflict()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(Guid.CreateVersion7()),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "One fact.", [source])));

        await FactSheetHarness.AssertProblemAsync(response, 412, "fact_sheet_conflict");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
        FactSheetHarness.CurrentSheetId(Database, world).ShouldBe(sheet.Id);
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.fact_sheet_saved' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task PutFactSheet_SecondEditWithTheFirstEtag_Returns412()
    {
        // The ETag is the row id (D9): the moment an edit lands, the tag the two tabs both read addresses a row
        // that is no longer the current version, so the second tab's save loses instead of overwriting.
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);
        using var first = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "One fact.", [source]), FactSheetHarness.Fact(null, "A second fact.", [source])));
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(FactSheetHarness.Ct));

        using var second = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "A different edit.", [source])));

        await FactSheetHarness.AssertProblemAsync(second, 412, "fact_sheet_conflict");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(2, "the refused save adds no third version");
    }

    [Fact]
    public async Task PutFactSheet_InStoryApproved_Returns409EpisodeInvalidState()
    {
        var world = await FactSheetHarness.SeedAsync(Database, EpisodeStatus.StoryApproved);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "One fact.", [source])));

        await FactSheetHarness.AssertProblemAsync(response, 409, "episode_invalid_state");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Fact]
    public async Task PutFactSheet_InRendering_Returns409()
    {
        var world = await FactSheetHarness.SeedAsync(Database, EpisodeStatus.Rendering);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "One fact.", [source])));

        await FactSheetHarness.AssertProblemAsync(response, 409, "episode_invalid_state");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Fact]
    public async Task PutFactSheet_InStoryReview_Saves()
    {
        // F26: draft and story_review are the two editable states; the reviewer's sheet is still editable.
        var world = await FactSheetHarness.SeedAsync(Database, EpisodeStatus.StoryReview);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "One fact.", [source])));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        (await FactSheetHarness.JsonAsync(response)).GetProperty("version").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task PutFactSheet_EpisodeWithoutSheet_Returns404FactSheetNotFound()
    {
        var world = await FactSheetHarness.SeedAsync(Database);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(Guid.CreateVersion7()),
            FactSheetHarness.EmptyFactsBody);

        await FactSheetHarness.AssertProblemAsync(response, 404, "fact_sheet_not_found");
        FactSheetHarness.Count(Database, $"SELECT count(*) FROM fact_sheets WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0, "an edit never creates the first version");
    }

    [Fact]
    public async Task PutFactSheet_Anonymous_Returns401()
    {
        var world = await FactSheetHarness.SeedAsync(Database);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.AnonymousClient();

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(Guid.CreateVersion7()),
            FactSheetHarness.EmptyFactsBody);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PutFactSheet_AnotherTenantsEpisode_Returns404()
    {
        var owner = await FactSheetHarness.SeedAsync(Database);
        var stranger = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, owner);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, owner, 1, [FactSheetHarness.Seed("f1", "Owned fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(stranger.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            owner.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "Hijacked fact.", [source])));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "another tenant's episode is a 404, never a 403");
        FactSheetHarness.SheetRows(Database, owner).Count.ShouldBe(1);
        FactSheetHarness.StoredFactIds(Database, owner, 1).ShouldBe(["f1"]);
    }

    [Fact]
    public async Task PutFactSheet_CreatesVersionPlusOne_AndLeavesPreviousRowUntouched()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [FactSheetHarness.Seed("f1", "First stored fact.", [source]), FactSheetHarness.Seed("f2", "Second stored fact.", [source])]);
        var before = RowFingerprint(world, 1);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(
                FactSheetHarness.Fact("f1", "First stored fact.", [source]),
                FactSheetHarness.Fact(null, "A fact the user added.", [source])));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        var body = await FactSheetHarness.JsonAsync(response);
        body.GetProperty("version").GetInt32().ShouldBe(2);
        body.GetProperty("status").GetString().ShouldBe("draft");
        body.GetProperty("factCount").GetInt32().ShouldBe(2);
        var newEtag = FactSheetHarness.ResponseEtag(response);
        newEtag.ShouldNotBe(FactSheetHarness.Etag(sheet.Id), "an edit is a new row, so the ETag moves");
        body.GetProperty("etag").GetString().ShouldBe(newEtag);
        newEtag.ShouldBe(FactSheetHarness.Etag(FactSheetHarness.CurrentSheetId(Database, world)));

        FactSheetHarness.SheetRows(Database, world).ShouldBe(
            [
                $"1|0|2|null|{FactSheetBuilder.DefaultPromptVersion}|{world.UserId}",
                $"2|0|3|null|user|{world.UserId}",
            ],
            "exactly one new row: version 2, draft, no run, prompt_version 'user', created_by the editor");
        RowFingerprint(world, 1).ShouldBe(before, "the previous version is never mutated (D9)");
        FactSheetHarness.StoredFactIds(Database, world, 2).ShouldBe(["f1", "f3"]);
        FactSheetHarness.StoredFactIds(Database, world, 1).ShouldBe(["f1", "f2"]);
    }

    [Fact]
    public async Task PutFactSheet_OnApprovedCurrentRow_CreatesANewDraft_AndKeepsOneApprovedRow()
    {
        // 02 §4: adding a fact to an approved sheet is not allowed directly — the first edit creates a new draft
        // version and the approved row stays approved until that draft is approved.
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

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(approved.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "Approved fact, corrected.", [source])));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        (await FactSheetHarness.JsonAsync(response)).GetProperty("version").GetInt32().ShouldBe(2);
        FactSheetHarness.SheetsInStatus(Database, world, FactSheetStatus.Approved).ShouldBe(1);
        FactSheetHarness.SheetsInStatus(Database, world, FactSheetStatus.Superseded).ShouldBe(0);
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' AND version = 1 AND status = 1")
            .ShouldBe(1, "the approved row is untouched until the new draft is approved");
    }

    [Fact]
    public async Task PutFactSheet_EmptyFactList_SavesAnEmptyDraft()
    {
        // Contract §13: an empty list is a legal body; approve is what refuses it (F13).
        var world = await FactSheetHarness.SeedAsync(Database);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(Guid.CreateVersion7()),
            FactSheetHarness.EmptyFactsBody);

        // The episode has no sheet yet, so the first row cannot be created by an edit: PUT needs the seed row.
        await FactSheetHarness.AssertProblemAsync(response, 404, "fact_sheet_not_found");

        var seeded = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [])]);
        using var second = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(seeded.Id),
            FactSheetHarness.EmptyFactsBody);

        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        (await FactSheetHarness.JsonAsync(second)).GetProperty("factCount").GetInt32().ShouldBe(0);
        SqlProbe.ScalarText(
                Database,
                $"SELECT facts::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' AND version = 2")
            .ShouldBe("[]");
    }

    [Fact]
    public async Task PutFactSheet_WritesOneOutboxRowAndOneAuditRow()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "One fact, corrected.", [source])));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'fact_sheet_saved.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.fact_sheet_saved' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(1);
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.fact_sheet_approved' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task UpdateFactSheet_NewFactGetsNextId_ExistingIdsPreserved()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [FactSheetHarness.Seed("f1", "First stored fact.", [source]), FactSheetHarness.Seed("f2", "Second stored fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(
                FactSheetHarness.Fact("f2", "Second stored fact.", [source]),
                FactSheetHarness.Fact(null, "A new fact.", [source]),
                FactSheetHarness.Fact("f1", "First stored fact.", [source])));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        (await FetchedFactIdsAsync(client, world)).ShouldBe(["f2", "f3", "f1"], "new facts continue from max_fact_id, in input order");
        FactSheetHarness.StoredFactIds(Database, world, 2).ShouldBe(["f2", "f3", "f1"]);
        SqlProbe.ScalarText(
                Database,
                $"SELECT max_fact_id::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' AND version = 2")
            .ShouldBe("3");
    }

    [Fact]
    public async Task UpdateFactSheet_DeletedFactId_IsNeverReusedInLaterVersion()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [FactSheetHarness.Seed("f1", "Kept fact.", [source]), FactSheetHarness.Seed("f2", "Doomed fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var first = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "Kept fact.", [source])));
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        var secondEtag = FactSheetHarness.ResponseEtag(first);

        using var second = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            secondEtag,
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "Kept fact.", [source]), FactSheetHarness.Fact(null, "Fresh fact.", [source])));

        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        (await FetchedFactIdsAsync(client, world)).ShouldBe(["f1", "f3"], "the retired f2 is never handed out again (F12)");
        SqlProbe.ScalarText(
                Database,
                $"SELECT max_fact_id::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' ORDER BY version DESC LIMIT 1")
            .ShouldBe("3");
        FactSheetHarness.StoredFactIds(Database, world, 2).ShouldBe(["f1"]);
    }

    [Fact]
    public async Task UpdateFactSheet_CitingARetiredIdInALaterVersion_Returns400()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [FactSheetHarness.Seed("f1", "Kept fact.", [source]), FactSheetHarness.Seed("f2", "Doomed fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);
        using var first = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "Kept fact.", [source])));
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(FactSheetHarness.Ct));

        using var second = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.ResponseEtag(first),
            FactSheetHarness.Body(FactSheetHarness.Fact("f2", "Doomed fact.", [source])));

        await FactSheetHarness.AssertProblemAsync(second, 400, "fact_sheet_invalid_content");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(2);
    }

    [Fact]
    public async Task UpdateFactSheet_FactIdsStayStable_AcrossThreeEditsWithDeletions()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [
                FactSheetHarness.Seed("f1", "First fact.", [source]),
                FactSheetHarness.Seed("f2", "Second fact.", [source]),
                FactSheetHarness.Seed("f3", "Third fact.", [source]),
            ]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        var etag = FactSheetHarness.Etag(sheet.Id);

        // Edit 1: keep f1 and f2, retire f3, add a new fact -> f4.
        etag = await EditAsync(client, world, etag, source, ["f1", "f2"], "Fourth fact.");
        (await FetchedFactIdsAsync(client, world)).ShouldBe(["f1", "f2", "f4"]);

        // Edit 2: keep f1 and f4, retire f2, add a new fact -> f5.
        etag = await EditAsync(client, world, etag, source, ["f1", "f4"], "Fifth fact.");
        (await FetchedFactIdsAsync(client, world)).ShouldBe(["f1", "f4", "f5"]);

        // Edit 3: keep f4 and f5, retire f1, add a new fact -> f6.
        await EditAsync(client, world, etag, source, ["f4", "f5"], "Sixth fact.");
        (await FetchedFactIdsAsync(client, world)).ShouldBe(["f4", "f5", "f6"]);

        FactSheetHarness.StoredFactIds(Database, world, 2).ShouldBe(["f1", "f2", "f4"]);
        FactSheetHarness.StoredFactIds(Database, world, 3).ShouldBe(["f1", "f4", "f5"]);
        FactSheetHarness.StoredFactIds(Database, world, 4).ShouldBe(["f4", "f5", "f6"]);
        SqlProbe.ScalarText(
                Database,
                $"SELECT max_fact_id::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' ORDER BY version DESC LIMIT 1")
            .ShouldBe("6", "the watermark only ever grows (F12)");
    }

    [Fact]
    public async Task UpdateFactSheet_UserNoteSourceAllowed_AndLabelledIsUserNote()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var note = await FactSheetHarness.AddNoteAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "A seeded fact.", [note])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "A note-backed fact.", [note])));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));

        using var read = await FactSheetHarness.GetAsync(client, world.EpisodeId);
        var fact = (await FactSheetHarness.JsonAsync(read)).GetProperty("facts").EnumerateArray().Single();
        var reference = fact.GetProperty("sources").EnumerateArray().Single();
        reference.GetProperty("sourceId").GetGuid().ShouldBe(note);
        reference.GetProperty("isUserNote").GetBoolean().ShouldBeTrue();
        reference.GetProperty("url").ValueKind.ShouldBe(JsonValueKind.Null);
        reference.GetProperty("title").GetString()
            .ShouldBe("note", "the DTO carries a neutral, key-free title; the SPA localises it (contract §Application)");
    }

    [Fact]
    public async Task UpdateFactSheet_PreservesStoredConfidence_WhenInputOmitsIt()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [FactSheetHarness.Seed("f1", "A shaky fact.", [source], FactConfidence.Low)]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // The member is absent, not null: the body carries no "confidence" key at all, and either spelling keeps
        // the stored value (T53).
        var body = $$"""{"facts":[{"id":"f1","statement":"A shaky fact.","sourceIds":["{{source}}"]}]}""";

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        (await ConfidenceAsync(client, world)).ShouldBe("low", "an edit must not drop the confidence a model assigned (T53)");
    }

    [Fact]
    public async Task UpdateFactSheet_OverridesConfidence_WhenInputSetsIt()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [FactSheetHarness.Seed("f1", "A strong fact.", [source], FactConfidence.High)]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "A strong fact.", [source], "medium")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        (await ConfidenceAsync(client, world)).ShouldBe("medium");
    }

    [Fact]
    public async Task UpdateFactSheet_NewFactDefaultsToHighConfidence()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "A seeded fact.", [source])]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(
                FactSheetHarness.Fact("f1", "A seeded fact.", [source]),
                FactSheetHarness.Fact(null, "A fact the user asserts.", [source])));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        using var read = await FactSheetHarness.GetAsync(client, world.EpisodeId);
        var facts = (await FactSheetHarness.JsonAsync(read)).GetProperty("facts").EnumerateArray().ToArray();
        facts.Single(fact => fact.GetProperty("id").GetString() == "f2").GetProperty("confidence").GetString()
            .ShouldBe("high", "the user asserts a fact they wrote");
        facts.Single(fact => fact.GetProperty("id").GetString() == "f1").GetProperty("confidence").GetString().ShouldBe("high");
    }

    [Fact]
    public async Task UpdateFactSheet_UnchangedFactKeepsOriginAndOverlap_EditedOrRecitedBecomesUser()
    {
        // Contract §11: a fact copied verbatim (id, statement and citations unchanged) keeps its provenance;
        // a changed statement or a changed citation list becomes a user fact with no overlap score.
        var world = await FactSheetHarness.SeedAsync(Database);
        var firstSource = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var secondSource = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            [
                FactSheetHarness.Seed("f1", "Untouched fact.", [firstSource], FactConfidence.High, FactOrigin.Model, 0.11m),
                FactSheetHarness.Seed("f2", "Rephrased fact.", [firstSource], FactConfidence.High, FactOrigin.Model, 0.22m),
                FactSheetHarness.Seed("f3", "Recited fact.", [firstSource], FactConfidence.High, FactOrigin.Model, 0.33m),
            ]);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(
                FactSheetHarness.Fact("f1", "Untouched fact.", [firstSource]),
                FactSheetHarness.Fact("f2", "Rephrased fact, now worded differently.", [firstSource]),
                FactSheetHarness.Fact("f3", "Recited fact.", [secondSource])));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        using var read = await FactSheetHarness.GetAsync(client, world.EpisodeId);
        var facts = (await FactSheetHarness.JsonAsync(read)).GetProperty("facts").EnumerateArray().ToArray();

        facts[0].GetProperty("origin").GetString().ShouldBe("model");
        facts[0].GetProperty("sourceOverlap").GetDecimal().ShouldBe(0.11m);
        facts[1].GetProperty("origin").GetString().ShouldBe("user");
        facts[1].GetProperty("sourceOverlap").ValueKind.ShouldBe(JsonValueKind.Null);
        facts[2].GetProperty("origin").GetString().ShouldBe("user");
        facts[2].GetProperty("sourceOverlap").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>Runs one edit that keeps <paramref name="keptIds"/> and appends one new fact; returns the new ETag.</summary>
    private static async Task<string> EditAsync(
        HttpClient client,
        World world,
        string etag,
        Guid sourceId,
        IReadOnlyList<string> keptIds,
        string newStatement)
    {
        using var read = await FactSheetHarness.GetAsync(client, world.EpisodeId);
        var statements = (await FactSheetHarness.JsonAsync(read)).GetProperty("facts").EnumerateArray()
            .ToDictionary(fact => fact.GetProperty("id").GetString()!, fact => fact.GetProperty("statement").GetString()!);

        var body = FactSheetHarness.Body(
            [
                .. keptIds.Select(id => FactSheetHarness.Fact(id, statements[id], [sourceId])),
                FactSheetHarness.Fact(null, newStatement, [sourceId]),
            ]);

        using var response = await FactSheetHarness.PutAsync(client, world.EpisodeId, etag, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));

        return FactSheetHarness.ResponseEtag(response);
    }

    /// <summary>The fact ids the current version reports, in wire order.</summary>
    private static async Task<IReadOnlyList<string>> FetchedFactIdsAsync(HttpClient client, World world)
    {
        using var read = await FactSheetHarness.GetAsync(client, world.EpisodeId);

        return [.. (await FactSheetHarness.JsonAsync(read)).GetProperty("facts").EnumerateArray()
            .Select(fact => fact.GetProperty("id").GetString()!)];
    }

    /// <summary>The confidence the current version reports for its single fact.</summary>
    private static async Task<string> ConfidenceAsync(HttpClient client, World world)
    {
        using var read = await FactSheetHarness.GetAsync(client, world.EpisodeId);

        return (await FactSheetHarness.JsonAsync(read)).GetProperty("facts").EnumerateArray().Single()
            .GetProperty("confidence").GetString()!;
    }

    /// <summary>Everything the row of one version holds, so "untouched" can be compared byte for byte.</summary>
    private string RowFingerprint(World world, int version) =>
        SqlProbe.ScalarText(
            Database,
            "SELECT version::text || '|' || status::text || '|' || max_fact_id::text || '|' || facts::text || '|' "
            + $"|| content_sha256 || '|' || updated_at::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}' AND version = {version}");
}
