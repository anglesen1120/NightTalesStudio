using System.Net;
using System.Text.Json;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// <c>POST /api/v1/series/{id}/bible/proposal/dismiss</c> against the real host and a real PostgreSQL 17
/// (02.6.3-T2, plan §4 U3): the pending proposal is thrown away with 204, the content version never moves and no
/// announcement is raised (a dismissal is not a bible change), the audit row carries the count and the episode
/// only, and the route is the caller's own series or 404.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostDismissProposalTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Action = "series.bible_proposal_dismissed";

    [Fact]
    public async Task PostDismiss_ClearsProposalWithoutVersionBump()
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

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await BibleProposalCommandHarness.DismissAsync(client, world.SeriesId);

        response.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        (await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct)).ShouldBeEmpty("the route answers no body");

        // The proposal was never content, so the version does not move and nothing is announced in the outbox.
        var row = SeriesBibleHarness.Row(Database, world.SeriesId);
        row.Version.ShouldBe(1, "a dismissal writes no content version");
        BibleProposalCommandHarness.ProposalJson(Database, world.SeriesId).ShouldBeEmpty();
        BibleProposalCommandHarness.ProposalEpisodeId(Database, world.SeriesId).ShouldBeEmpty();
        SeriesBibleHarness.OutboxCount(Database, world.UserId, SeriesBibleHarness.BibleUpdatedEvent)
            .ShouldBe(0, "only a save and an accept announce a version (plan §5)");

        // The read route agrees: no proposal, and the tag the client must still use is the one it had.
        using var read = await SeriesBibleHarness.GetAsync(client, world.SeriesId);
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.ResponseEtag(read).ShouldBe(SeriesBibleHarness.Etag(1));
        (await SeriesBibleHarness.JsonAsync(read)).GetProperty("proposal").ValueKind.ShouldBe(JsonValueKind.Null);

        // One audit row from the attribute and the projection: the series, how many ops were dropped and the
        // episode they came from — never an op's text (plan §4 U3, D13/D19).
        BibleProposalCommandHarness.AuditCount(Database, world.UserId, Action).ShouldBe(1);
        var audit = BibleProposalCommandHarness.AuditRow(Database, world.UserId, Action);
        audit.ShouldStartWith("series.bible_proposal_dismissed|episodes|user|user|");
        audit.ShouldContain(world.UserId.ToString("D"));
        audit.ShouldContain(world.SeriesId.ToString("D"));
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "seriesId")
            .ShouldBe(world.SeriesId.ToString("D"));
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "ops").ShouldBe("2");
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, Action, "proposedFromEpisodeId")
            .ShouldBe(approved.ToString("D"));
        audit.ShouldNotContain("Bà Tư");
        audit.ShouldNotContain("Một chuyện ma nhẹ nhàng.");

        // Dismissing twice: the second call has nothing to clear and says so.
        using var second = await BibleProposalCommandHarness.DismissAsync(client, world.SeriesId);
        await SeriesBibleHarness.AssertProblemAsync(second, 409, "bible_proposal_not_pending");
        BibleProposalCommandHarness.AuditCount(Database, world.UserId, Action)
            .ShouldBe(1, "the refused second call writes no audit row");
    }

    [Fact]
    public async Task PostDismiss_WithoutProposal_Returns409()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await BibleProposalCommandHarness.DismissAsync(client, world.SeriesId);

        await SeriesBibleHarness.AssertProblemAsync(response, 409, "bible_proposal_not_pending");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1, "nothing was pending, so nothing moved");
    }

    [Fact]
    public async Task DismissBibleProposal_OtherUsersSeries_Returns404()
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

        using var response = await BibleProposalCommandHarness.DismissAsync(stranger, world.SeriesId);

        // D10: another user's series is 404, never 403 — and the proposal is still there for its owner.
        await SeriesBibleHarness.AssertProblemAsync(response, 404, "not_found");
        BibleProposalCommandHarness.ProposalJson(Database, world.SeriesId).ShouldNotBeEmpty();
        BibleProposalCommandHarness.AuditCount(Database, world.UserId, Action).ShouldBe(0);
    }
}
