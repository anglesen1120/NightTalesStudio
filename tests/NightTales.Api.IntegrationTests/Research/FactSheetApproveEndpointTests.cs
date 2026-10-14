using System.Net;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Research;

/// <summary>
/// <c>POST /api/v1/episodes/{id}/fact-sheet/approve</c> against the real host and a real PostgreSQL 17
/// (02.5.4-T1): the <c>If-Match</c> contract, the F26 state gate, the F13 source-diversity rule (at least five
/// facts and two distinct cited fetched url sources with distinct content hashes), the approve-and-supersede
/// transaction that keeps exactly one approved row per episode, and the row a successful approval writes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FactSheetApproveEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task ApproveFactSheet_OneCitedSource_Returns409StorySourcesRequired()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, Cites(5, _ => [source]));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        await FactSheetHarness.AssertProblemAsync(response, 409, "story_sources_required");
        AssertStillDraft(world, sheet.Id);
    }

    [Fact]
    public async Task ApproveFactSheet_TwoSourcesSameContentSha_Returns409()
    {
        // F13's anti-duplicate rule: two sources that hold the same text are one source, not two.
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, number => number <= 3 ? [first] : [second]));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        await FactSheetHarness.AssertProblemAsync(response, 409, "story_sources_required");
        AssertStillDraft(world, sheet.Id);
    }

    [Fact]
    public async Task ApproveFactSheet_UnfetchedUrlSource_DoesNotCountForDiversity_Returns409()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var fetched = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var pending = await FactSheetHarness.AddPendingUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, number => number <= 4 ? [fetched] : [pending]));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        await FactSheetHarness.AssertProblemAsync(response, 409, "story_sources_required");
        AssertStillDraft(world, sheet.Id);
    }

    [Fact]
    public async Task ApproveFactSheet_UserNoteNeverCountsForDiversity_Returns409()
    {
        // Failure handling: a user note is Fetched by construction but it is not a url source, so it never
        // supplies the second half of the diversity rule.
        var world = await FactSheetHarness.SeedAsync(Database);
        var fetched = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var note = await FactSheetHarness.AddNoteAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, number => number <= 4 ? [fetched] : [note]));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        await FactSheetHarness.AssertProblemAsync(response, 409, "story_sources_required");
        AssertStillDraft(world, sheet.Id);
    }

    [Fact]
    public async Task ApproveFactSheet_SourceRemovedAfterTheDraft_DoesNotCount_Returns409()
    {
        // A fact whose only source was deleted from the episode keeps the citation in its document (CR-4), but the
        // F13 check resolves the source through story_sources, where it no longer exists.
        var world = await FactSheetHarness.SeedAsync(Database);
        var fetched = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var doomed = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, number => number <= 4 ? [fetched] : [doomed]));
        SqlProbe.Execute(Database, $"DELETE FROM story_sources WHERE id = '{doomed}'");

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        await FactSheetHarness.AssertProblemAsync(response, 409, "story_sources_required");
        AssertStillDraft(world, sheet.Id);
    }

    [Fact]
    public async Task ApproveFactSheet_ZeroFacts_Returns409FactSheetSourcesInsufficient()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, []);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        await FactSheetHarness.AssertProblemAsync(response, 409, "fact_sheet_sources_insufficient");
        AssertStillDraft(world, sheet.Id);
    }

    [Fact]
    public async Task ApproveFactSheet_FewerThanFiveFacts_Returns409FactSheetSourcesInsufficient()
    {
        // The minimum is checked before the diversity rule, so four well-sourced facts still answer the
        // "insufficient" conflict (S31 hand-over).
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(4, number => number <= 2 ? [first] : [second]));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        await FactSheetHarness.AssertProblemAsync(response, 409, "fact_sheet_sources_insufficient");
        AssertStillDraft(world, sheet.Id);
    }

    [Fact]
    public async Task ApproveFactSheet_TwoDistinctCitedFetchedSources_SetsApprovedAndReturnsNewEtag()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, number => number <= 3 ? [first] : [second]));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        var body = await FactSheetHarness.JsonAsync(response);
        body.GetProperty("id").GetGuid().ShouldBe(sheet.Id, "the approved version is the draft row itself");
        body.GetProperty("version").GetInt32().ShouldBe(1);
        body.GetProperty("status").GetString().ShouldBe("approved");
        body.GetProperty("factCount").GetInt32().ShouldBe(5);
        body.GetProperty("etag").GetString().ShouldBe(FactSheetHarness.Etag(sheet.Id));
        FactSheetHarness.ResponseEtag(response).ShouldBe(FactSheetHarness.Etag(sheet.Id));

        SqlProbe.ScalarText(
                Database,
                "SELECT status::text || '|' || (approved_by IS NOT NULL)::text || '|' || (approved_at IS NOT NULL)::text "
                + $"FROM fact_sheets WHERE id = '{sheet.Id}'")
            .ShouldBe("1|true|true");
        AssertOneApproved(world);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("draft", "approving a sheet does not touch the story or the episode state");

        using var read = await FactSheetHarness.GetAsync(client, world.EpisodeId);
        (await FactSheetHarness.JsonAsync(read)).GetProperty("status").GetString().ShouldBe("approved");
    }

    [Fact]
    public async Task ApproveFactSheet_SupersedesPreviousApprovedRow_OnlyOneApprovedPerEpisode()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var previous = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, number => number <= 3 ? [first] : [second]),
            FactSheetStatus.Approved,
            approvedBy: world.UserId,
            approvedAt: FactSheetHarness.Start);
        var draft = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            2,
            Cites(5, number => number <= 3 ? [first] : [second], "Corrected"));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(draft.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{previous.Id}'")
            .ShouldBe("2", "the older approved row is superseded in the same transaction");
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{draft.Id}'").ShouldBe("1");
        AssertOneApproved(world);
        (await FactSheetHarness.JsonAsync(response)).GetProperty("version").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task ApproveFactSheet_AlreadyApprovedRow_Returns412()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, number => number <= 3 ? [first] : [second]));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var approved = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(FactSheetHarness.Ct));

        using var again = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        await FactSheetHarness.AssertProblemAsync(again, 412, "fact_sheet_conflict");
        AssertOneApproved(world);
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'fact_sheet_approved.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1, "the refused second approval raises nothing");
    }

    [Fact]
    public async Task ApproveFactSheet_MissingIfMatch_Returns428()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, []);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, ifMatch: null);

        await FactSheetHarness.AssertProblemAsync(response, 428, "if_match_required");
        AssertStillDraft(world, sheet.Id);
    }

    [Fact]
    public async Task ApproveFactSheet_StaleEtag_Returns412()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, []);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(Guid.CreateVersion7()));

        await FactSheetHarness.AssertProblemAsync(response, 412, "fact_sheet_conflict");
        AssertStillDraft(world, sheet.Id);
    }

    [Theory]
    [InlineData(EpisodeStatus.StoryApproved)]
    [InlineData(EpisodeStatus.Rendering)]
    public async Task ApproveFactSheet_InStoryApprovedOrRendering_Returns409(EpisodeStatus status)
    {
        var world = await FactSheetHarness.SeedAsync(Database, status);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, number => number <= 3 ? [first] : [second]));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        await FactSheetHarness.AssertProblemAsync(response, 409, "episode_invalid_state");
        AssertStillDraft(world, sheet.Id);
    }

    [Fact]
    public async Task ApproveFactSheet_Anonymous_Returns401()
    {
        var world = await FactSheetHarness.SeedAsync(Database);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.AnonymousClient();

        using var response = await FactSheetHarness.ApproveAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(Guid.CreateVersion7()));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ApproveFactSheet_AnotherTenantsEpisode_Returns404()
    {
        var owner = await FactSheetHarness.SeedAsync(Database);
        var stranger = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, owner);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, owner, 1, Cites(5, _ => [source]));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(stranger.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, owner.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "another tenant's episode is a 404, never a 403");
        AssertStillDraft(owner, sheet.Id);
    }

    [Fact]
    public async Task ApproveFactSheet_EpisodeWithoutSheet_Returns404FactSheetNotFound()
    {
        var world = await FactSheetHarness.SeedAsync(Database);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(Guid.CreateVersion7()));

        await FactSheetHarness.AssertProblemAsync(response, 404, "fact_sheet_not_found");
    }

    [Fact]
    public async Task ApproveFactSheet_WritesOneOutboxRowAndOneAuditRow()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var sheet = await FactSheetHarness.SeedSheetAsync(
            Database,
            world,
            1,
            Cites(5, number => number <= 3 ? [first] : [second]));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'fact_sheet_approved.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.fact_sheet_approved' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(1);
        FactSheetHarness.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = 'episode.fact_sheet_saved' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task PostFactSheetApprove_ThenPutFactSheet_CreatesNewDraftVersion()
    {
        // 02 §4 + decision 3: an edit after an approval is a brand-new draft version; the approved row stays
        // approved (still the only approved row) until that new draft is approved itself.
        var world = await FactSheetHarness.SeedAsync(Database);
        var first = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('a'));
        var second = await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha('b'));
        var citations = (Func<int, IReadOnlyList<Guid>>)(number => number <= 3 ? [first] : [second]);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, Cites(5, citations));

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var approved = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(sheet.Id));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(FactSheetHarness.Ct));

        using var edit = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(
                [
                    .. Enumerable.Range(1, 5).Select(number => FactSheetHarness.Fact(
                        $"f{number}",
                        number == 1 ? "Fact number 1, corrected." : $"Fact number {number}.",
                        citations(number))),
                ]));

        edit.StatusCode.ShouldBe(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        var body = await FactSheetHarness.JsonAsync(edit);
        body.GetProperty("version").GetInt32().ShouldBe(2);
        body.GetProperty("status").GetString().ShouldBe("draft");
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{sheet.Id}'")
            .ShouldBe("1", "the approved row is untouched by the edit that follows it");
        AssertOneApproved(world);
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(2);

        using var read = await FactSheetHarness.GetAsync(client, world.EpisodeId);
        var current = await FactSheetHarness.JsonAsync(read);
        current.GetProperty("version").GetInt32().ShouldBe(2);
        current.GetProperty("status").GetString().ShouldBe("draft");
    }

    /// <summary>
    /// Builds <paramref name="count"/> facts numbered <c>f1…fN</c>, each citing the sources
    /// <paramref name="citations"/> picks for its number.
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

    private void AssertStillDraft(World world, Guid sheetId)
    {
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{sheetId}'").ShouldBe("0");
        AssertOneApproved(world, expected: 0);
    }

    private void AssertOneApproved(World world, long expected = 1) =>
        FactSheetHarness.SheetsInStatus(Database, world, FactSheetStatus.Approved).ShouldBe(expected);
}
