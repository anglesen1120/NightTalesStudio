using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// <c>POST /api/v1/series/{id}/bible/proposal/accept</c> against the real host and a real PostgreSQL 17
/// (02.6.3-T2, plan §4 U4): the pending ops are applied onto the current content, the version moves once with its
/// <c>"sb-&lt;version&gt;"</c> tag, the proposal is cleared (so the second call is 409
/// <c>bible_proposal_not_pending</c>), the accept announces itself as <c>series_bible_updated.v1</c> with the
/// accepting user as <c>changed_by</c>, and the route is the caller's own series or 404.
/// </summary>
/// <remarks>
/// The proposal is written straight into the row through the Domain's <c>SetProposal</c>
/// (<see cref="BibleProposalCommandHarness.SeedProposalAsync"/>), so these tests never depend on the job that
/// normally stages one.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostAcceptProposalTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Action = "series.bible_proposal_accepted";

    [Fact]
    public async Task PostAccept_AppliesOpsAndBumpsVersion_ThenSecondCallReturns409()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var approved = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        await BibleProposalCommandHarness.SeedProposalAsync(
            Database,
            world,
            approved,
            [
                BibleProposalCommandHarness.CharacterOp("Bà Tư"),
                BibleProposalCommandHarness.RuleOp("Không nhắc tên người thật."),
                BibleProposalCommandHarness.SummaryOp("Căn nhà cuối hành lang", "Một chuyện ma nhẹ nhàng về căn nhà bên sông."),
            ]);

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));

        // The frozen body of a write that produced a content version: { version, etag }, the tag byte-identical to
        // the header, so it is itself a valid next If-Match (plan §4 U4, README D9).
        SeriesBibleHarness.ResponseEtag(response).ShouldBe(SeriesBibleHarness.Etag(2));
        var body = await SeriesBibleHarness.JsonAsync(response);
        body.EnumerateObject().Select(member => member.Name).ShouldBe(["version", "etag"]);
        body.GetProperty("version").GetInt32().ShouldBe(2);
        body.GetProperty("etag").GetString().ShouldBe(SeriesBibleHarness.Etag(2));

        // The ops are content now: append-only, in model order, with the summary bound to the approved episode and
        // its server-derived members filled in (the op itself carries no episode).
        var row = SeriesBibleHarness.Row(Database, world.SeriesId);
        row.Version.ShouldBe(2, "an accept bumps the version exactly once");
        row.Content["recurring_characters"]!.AsArray().Count.ShouldBe(1);
        row.Content["recurring_characters"]![0]!["name"]!.GetValue<string>().ShouldBe("Bà Tư");
        row.Content["rules"]![0]!["text"]!.GetValue<string>().ShouldBe("Không nhắc tên người thật.");
        var summary = row.Content["episode_summaries"]![0]!;
        summary["episode_id"]!.GetValue<string>().ShouldBe(approved.ToString("D"));
        summary["title"]!.GetValue<string>().ShouldBe("Căn nhà cuối hành lang");
        summary["structure"]!.AsObject().Count.ShouldBeGreaterThan(0, "the structure is derived from the approved version");
        summary["approved_at"].ShouldNotBeNull("the approval instant is derived from the episode");

        // The proposal is gone from the row, and the read route agrees.
        BibleProposalCommandHarness.ProposalJson(Database, world.SeriesId).ShouldBeEmpty();
        BibleProposalCommandHarness.ProposalEpisodeId(Database, world.SeriesId).ShouldBeEmpty();

        using var read = await SeriesBibleHarness.GetAsync(client, world.SeriesId);
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.ResponseEtag(read).ShouldBe(SeriesBibleHarness.Etag(2));
        (await SeriesBibleHarness.JsonAsync(read)).GetProperty("proposal").ValueKind.ShouldBe(JsonValueKind.Null);

        // ...so the second accept has nothing to resolve: 409, and the version stays where the first call left it.
        using var second = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);
        await SeriesBibleHarness.AssertProblemAsync(second, 409, "bible_proposal_not_pending");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(2, "a refused accept writes nothing");
    }

    [Fact]
    public async Task PostAccept_WithoutProposal_Returns409()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);

        await SeriesBibleHarness.AssertProblemAsync(response, 409, "bible_proposal_not_pending");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1, "nothing was pending, so nothing moved");
    }

    [Fact]
    public async Task PostAccept_AnnouncesTheVersionAndWritesTheAuditRow()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var approved = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        await BibleProposalCommandHarness.SeedProposalAsync(
            Database,
            world,
            approved,
            [
                BibleProposalCommandHarness.CharacterOp("Bà Tư Bí Mật"),
                BibleProposalCommandHarness.RuleOp("Không nhắc tên người thật."),
            ]);

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));

        // The announcement is a content version like a save's: one series_bible_updated.v1 row in the accept's own
        // transaction, with the accepting user as changed_by (plan §5, STATUS BIBLE-4(f)).
        SeriesBibleHarness.OutboxCount(Database, world.UserId, SeriesBibleHarness.BibleUpdatedEvent)
            .ShouldBe(1, "an accepted proposal announces itself exactly once");
        var payload = JsonNode.Parse(
            SeriesBibleHarness.OutboxPayload(Database, world.UserId, SeriesBibleHarness.BibleUpdatedEvent))!;
        payload["seriesId"]!.GetValue<string>().ShouldBe(world.SeriesId.ToString("D"));
        payload["userId"]!.GetValue<string>().ShouldBe(world.UserId.ToString("D"));
        payload["version"]!.GetValue<int>().ShouldBe(2);
        payload["changedBy"]!.GetValue<string>().ShouldBe(
            world.UserId.ToString("D"),
            "the actor of an accept is the accepting user");
        SeriesBibleHarness.OutboxPayload(Database, world.UserId, SeriesBibleHarness.BibleUpdatedEvent)
            .ShouldNotContain(
                "Bà Tư Bí Mật",
                customMessage: "the payload carries ids and the version only (D19)");

        // One audit row from the attribute and the projection: the action's own keys, ids, versions and counts.
        BibleProposalCommandHarness.AuditCount(Database, world.UserId, Action).ShouldBe(1);
        var audit = BibleProposalCommandHarness.AuditRow(Database, world.UserId, Action);
        audit.ShouldStartWith("series.bible_proposal_accepted|episodes|user|user|");
        audit.ShouldContain(world.UserId.ToString("D"));
        audit.ShouldContain(world.SeriesId.ToString("D"));
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "seriesId")
            .ShouldBe(world.SeriesId.ToString("D"));
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "fromVersion").ShouldBe("1");
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "toVersion").ShouldBe("2");
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "applied").ShouldBe("2");
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "skipped").ShouldBe("0");
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "proposedFromEpisodeId")
            .ShouldBe(approved.ToString("D"));

        // Never an op's text, a name or a summary (D13/D19).
        audit.ShouldNotContain("Bà Tư Bí Mật");
        audit.ShouldNotContain("Không nhắc tên người thật.");
        audit.ShouldNotContain("kể chuyện đêm");
    }

    [Fact]
    public async Task PostAccept_OpWithForbiddenCharacter_IsSkippedAndNeverStored()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var approved = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // The document the accept must leave savable is the client's own save, not the create-path row: a fresh
        // row's persona is empty by design (02.6.3-T1 open item), so a GET-then-PUT of it is refused with
        // `narrator.persona: empty` before any proposal exists. Saving a valid document first is what makes the
        // last assertion of this test about the accept's own output.
        using var initial = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(1),
            SeriesBibleHarness.Content());
        initial.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await initial.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));

        await BibleProposalCommandHarness.SeedProposalAsync(
            Database,
            world,
            approved,
            [
                BibleProposalCommandHarness.CharacterOp("Bà <script> Tư"),
                BibleProposalCommandHarness.RuleOp("Không nhắc tên người thật."),
            ]);

        using var response = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"an accept never fails for its content: the op is skipped, not stored — {await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct)}");

        // The refused op is gone (a stored document a later PUT would refuse is never written) and the valid op
        // still applied; the two counts reconcile with what the proposal carried.
        var row = SeriesBibleHarness.Row(Database, world.SeriesId);
        row.Version.ShouldBe(3, "the first save is version 2 and the accepted proposal is one more");
        row.Content.ToJsonString().ShouldNotContain("<");
        row.Content["recurring_characters"]!.AsArray().ShouldBeEmpty();
        row.Content["rules"]![0]!["text"]!.GetValue<string>().ShouldBe("Không nhắc tên người thật.");
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "applied").ShouldBe("1");
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "skipped").ShouldBe("1");

        // The very next save of the document the accept stored is accepted, which is what the skip is for: nothing
        // the accept writes may make the client's own copy unsavable.
        using var read = await SeriesBibleHarness.GetAsync(client, world.SeriesId);
        var content = (await SeriesBibleHarness.JsonAsync(read)).GetProperty("content").Deserialize<JsonObject>()!;
        using var save = await SeriesBibleHarness.PutAsync(
            client,
            world.SeriesId,
            SeriesBibleHarness.Etag(3),
            content);
        save.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await save.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
    }

    [Fact]
    public async Task PostAccept_ReopenedEpisode_SkipsTheSummaryOpAndAppliesTheRest()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var approved = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        await BibleProposalCommandHarness.SeedProposalAsync(
            Database,
            world,
            approved,
            [
                BibleProposalCommandHarness.CharacterOp("Bà Tư"),
                BibleProposalCommandHarness.SummaryOp("Căn nhà cuối hành lang", "Một chuyện ma nhẹ nhàng."),
            ]);

        // The episode is reopened between the proposal and the accept: there is no approval left to derive the
        // summary's structure and approved_at from, and a summary stored for an unapproved episode would make the
        // next PUT refuse the client's whole document (not_approved_episode).
        SqlProbe.Execute(
            Database,
            $"UPDATE episodes SET approved_story_version_id = NULL, approved_at = NULL WHERE id = '{approved:D}'");

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));

        var row = SeriesBibleHarness.Row(Database, world.SeriesId);
        row.Version.ShouldBe(2);
        row.Content["recurring_characters"]![0]!["name"]!.GetValue<string>().ShouldBe("Bà Tư");
        row.Content["episode_summaries"]!.AsArray().ShouldBeEmpty("a summary cannot be stored for an unapproved episode");
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "applied").ShouldBe("1");
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "skipped").ShouldBe("1");
    }

    [Fact]
    public async Task AcceptBibleProposal_OtherUsersSeries_Returns404()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var approved = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        await BibleProposalCommandHarness.SeedProposalAsync(
            Database,
            world,
            approved,
            [BibleProposalCommandHarness.CharacterOp("Bà Tư")]);

        using var factory = SeriesBibleHarness.Factory(Database);
        using var stranger = factory.ClientFor(Guid.CreateVersion7());

        using var response = await BibleProposalCommandHarness.AcceptAsync(stranger, world.SeriesId);

        // D10: another user's series is 404, never 403 — and the proposal was there for its owner.
        await SeriesBibleHarness.AssertProblemAsync(response, 404, "not_found");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1, "a foreign accept changes nothing");
        BibleProposalCommandHarness.ProposalJson(Database, world.SeriesId).ShouldNotBeEmpty();
        BibleProposalCommandHarness.AuditCount(Database, world.UserId, Action).ShouldBe(0);
    }
}
