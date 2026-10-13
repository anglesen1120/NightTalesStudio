using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Domain.Common;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>
/// 02.3.3-T1 (02 README D8/D13/F12/F13 and the SEC-1 pinned-data rule) with the real
/// <c>StoryApprovalGate</c> in the host: the true-crime chain end to end, the attestation-version rule
/// including the <c>Story:TrueCrime:AttestationVersion</c> override, the two F13 source refusals, a story
/// pinned to a superseded sheet, the non-topic-scoped similarity block, and the fact-sheet-pinned fiction
/// topic that must still demand the sheet and the tick. The 02.5.5-T1 <c>IFactSheetGuard</c> is not merged,
/// so the approved sheet is scripted through the hand-written <c>FakeFactSheetGuard</c>.
/// </summary>
public sealed partial class ApproveStoryEndpointTests
{
    /// <summary>
    /// The whole true-crime chain: two fetched url sources with distinct hashes that kept facts cite, a
    /// story version pinned to the approved sheet, the tick, and an approval that writes exactly the two
    /// audit rows of the episode with ids and versions only, then reads back a non-null
    /// <c>currentStory.attestation.acceptedAt</c>.
    /// </summary>
    [Fact]
    public async Task TrueCrimeFlow_SourcesToSheetToStoryToAttestationToApprove_EndToEnd()
    {
        var world = await SeedAsync(topic: Topic.TrueCrime);
        var sheet = await SeedTrueCrimeApprovalAsync(world);
        using var factory = RealGateFactory(sheet);
        using var client = factory.ClientFor(world.UserId);
        var clause = factory.Services.GetRequiredService<IPromptCatalog>().GetAttestation(Language.Vi);

        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        AssertApproved(world);
        AttestationCount(world).ShouldBe(1);
        AuditRows(world).ShouldBe(
            "episode.story_approved|episode,episode.true_crime_attested|episode",
            "the episode carries exactly the approval row and the attestation row");
        AuditDataKeys(world).ShouldBe("story_version_id,text_version", "D13: ids and versions only");
        AuditDataValues(world).ShouldBe($"{world.VersionId}|ta.v1");
        SqlProbe.ScalarText(Database,
                $"SELECT data::text FROM audit_logs WHERE action='episode.true_crime_attested' AND subject_user_id='{world.UserId}'")
            .Contains(clause.Text, StringComparison.Ordinal)
            .ShouldBeFalse("the clause text is never stored in the audit row");
        clause.Text.ShouldNotBeNullOrWhiteSpace("the negative control needs the real clause text");

        using var detail = await client.GetAsync($"/api/v1/episodes/{world.EpisodeId}", Ct);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK, await detail.Content.ReadAsStringAsync(Ct));
        var current = await BodyAsync(detail);
        var attestation = current.GetProperty("currentStory").GetProperty("attestation");
        attestation.GetProperty("required").GetBoolean().ShouldBeTrue();
        attestation.GetProperty("textVersion").GetString().ShouldBe("ta.v1");
        attestation.GetProperty("acceptedAt").ValueKind.ShouldBe(JsonValueKind.String);
        attestation.GetProperty("acceptedAt").GetDateTimeOffset().ShouldBe(Start);
    }

    /// <summary>Acceptance: a tick that names another text version is refused with the D8 code and writes nothing.</summary>
    [Fact]
    public async Task ApproveStory_AttestationTextVersionTaV0_Returns400AndWritesNothing()
    {
        var world = await SeedAsync(topic: Topic.TrueCrime);
        var sheet = await SeedTrueCrimeApprovalAsync(world);
        using var factory = RealGateFactory(sheet);
        using var client = factory.ClientFor(world.UserId);

        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v0"}}""");

        await AssertProblemAsync(response, 400, "story_attestation_required");
        AssertNothingApproved(world);
        AttestationCount(world).ShouldBe(0);
        AuditRows(world).ShouldBe(string.Empty);
    }

    /// <summary>
    /// Acceptance: <c>Story:TrueCrime:AttestationVersion</c> is the single accepted value. With
    /// <c>ta.v2</c> configured, an approval carrying <c>ta.v1</c> is refused and one carrying
    /// <c>ta.v2</c> succeeds and is audited with the version the gate accepted.
    /// </summary>
    [Fact]
    public async Task ApproveStory_AttestationVersionConfigOverride_AcceptsOnlyTheConfiguredVersion()
    {
        var world = await SeedAsync(topic: Topic.TrueCrime);
        var sheet = await SeedTrueCrimeApprovalAsync(world);
        using var factory = RealGateFactory(sheet).WithSetting("Story:TrueCrime:AttestationVersion", "ta.v2");
        using var client = factory.ClientFor(world.UserId);

        using var stale = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");

        await AssertProblemAsync(stale, 400, "story_attestation_required");
        AssertNothingApproved(world);
        AttestationCount(world).ShouldBe(0);

        using var accepted = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v2"}}""");

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(Ct));
        AssertApproved(world);
        AuditDataKeys(world).ShouldBe("story_version_id,text_version");
        AuditDataValues(world).ShouldBe($"{world.VersionId}|ta.v2", "the row records the version the gate accepted");
    }

    /// <summary>
    /// Acceptance (F13): a true-crime story needs two distinct cited fetched sources. One cited plus one
    /// fetched-but-uncited source, and two cited sources that share one content hash, both refuse with the
    /// D8 code and write nothing.
    /// </summary>
    /// <param name="kind"><c>uncited</c> leaves the second source uncited; <c>same-hash</c> makes it a mirror.</param>
    [Theory]
    [InlineData("uncited")]
    [InlineData("same-hash")]
    public async Task ApproveStory_TrueCrimeSourcesNotTwoDistinctCited_ReturnsSourcesRequired(string kind)
    {
        var world = await SeedAsync(topic: Topic.TrueCrime);
        var sheetRow = await SheetRowAsync(world);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await AddFetchedSourceAsync(world, first, new string('a', 64));
        await AddFetchedSourceAsync(world, second, kind == "same-hash" ? new string('a', 64) : new string('b', 64));
        PinSheet(world, sheetRow.Id);
        var aliases = new Dictionary<string, Guid>(StringComparer.Ordinal) { ["s1"] = first, ["s2"] = second };
        var sheet = kind == "uncited"
            ? new ApprovedFactSheet(sheetRow.Id, sheetRow.Version, [new FactForWriter("f1", "Fact one", ["s1"])], aliases)
            : new ApprovedFactSheet(
                sheetRow.Id,
                sheetRow.Version,
                [new FactForWriter("f1", "Fact one", ["s1"]), new FactForWriter("f2", "Fact two", ["s2"])],
                aliases);
        using var factory = RealGateFactory(sheet);
        using var client = factory.ClientFor(world.UserId);

        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");

        await AssertProblemAsync(response, 409, "story_sources_required");
        AssertNothingApproved(world);
        AttestationCount(world).ShouldBe(0);
        AuditRows(world).ShouldBe(string.Empty, "the refused approval rolls every audit row back");
    }

    /// <summary>
    /// Acceptance (F12): a story whose <c>fact_sheet_id</c> names a superseded sheet is refused, because
    /// the guard now returns a different approved sheet for the episode.
    /// </summary>
    [Fact]
    public async Task ApproveStory_TrueCrimeStoryPinnedToSupersededSheet_ReturnsFactSheetOutdated()
    {
        var world = await SeedAsync(topic: Topic.TrueCrime);
        var superseded = await SheetRowAsync(world);
        PinSheet(world, superseded.Id);
        // The guard now returns the sheet a re-approval created. Only the pinned id needs a real row (the
        // story_versions FK); the fake returns the guard's sheet as a record, and ux_fact_sheets_episode_version
        // forbids a second version-1 row for this episode.
        var current = Guid.CreateVersion7();
        var sheet = new ApprovedFactSheet(
            current, superseded.Version + 1, [], new Dictionary<string, Guid>(StringComparer.Ordinal));
        using var factory = RealGateFactory(sheet);
        using var client = factory.ClientFor(world.UserId);

        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");

        await AssertProblemAsync(response, 409, "story_fact_sheet_outdated");
        AssertNothingApproved(world);
        AttestationCount(world).ShouldBe(0);
        AuditRows(world).ShouldBe(string.Empty);
    }

    /// <summary>
    /// 02 §5.9: the similarity block is not topic-scoped. A sourced fiction episode whose stored score sits
    /// above the configured <c>Similarity:BlockAbove</c> is refused; below it the same episode approves and
    /// ignores the tick, writing no attested row.
    /// </summary>
    /// <param name="score">The stored <c>similarity_score</c>.</param>
    /// <param name="expected">The HTTP status the classifier decides.</param>
    [Theory]
    [InlineData(0.6, 409)]
    [InlineData(0.4, 200)]
    public async Task ApproveStory_SourcedFictionStoredScore_IsClassifiedAgainstTheConfiguredBlockThreshold(double score, int expected)
    {
        var world = await SeedAsync(topic: Topic.Ghost);
        await AddFetchedSourceAsync(world, Guid.CreateVersion7(), new string('c', 64));
        SetSimilarity(world, (decimal)score);
        using var factory = RealGateFactory().WithSetting("Similarity:BlockAbove", "0.5");
        using var client = factory.ClientFor(world.UserId);

        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");

        response.StatusCode.ShouldBe((HttpStatusCode)expected, await response.Content.ReadAsStringAsync(Ct));
        if (expected == 409)
        {
            await AssertProblemAsync(response, 409, "story_too_similar_to_source");
            AssertNothingApproved(world);
            AttestationCount(world).ShouldBe(0, "a refused approval writes no attested row");
        }
        else
        {
            AssertApproved(world);
            AttestationCount(world).ShouldBe(0, "fiction approves without a tick and writes no attested row");
        }
    }

    /// <summary>
    /// SEC-1 pinned data: a version that carries a fact sheet stays true-crime however the series topic reads
    /// now. A fiction topic must not let the pinned version pass without the approved sheet, the tick, or the
    /// audited attestation.
    /// </summary>
    [Fact]
    public async Task ApproveStory_PinnedFactSheetWithFictionTopic_StillDemandsSheetAndAttestation()
    {
        var world = await SeedAsync(topic: Topic.Ghost);
        var sheetRow = await SheetRowAsync(world);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await AddFetchedSourceAsync(world, first, new string('a', 64));
        await AddFetchedSourceAsync(world, second, new string('b', 64));
        PinSheet(world, sheetRow.Id);
        var sheet = new ApprovedFactSheet(
            sheetRow.Id,
            sheetRow.Version,
            [new FactForWriter("f1", "Fact one", ["s1"]), new FactForWriter("f2", "Fact two", ["s2"])],
            new Dictionary<string, Guid>(StringComparer.Ordinal) { ["s1"] = first, ["s2"] = second });

        // The default host guard refuses a non-fiction topic, so the pinned version cannot skip the sheet.
        using (var refusing = RealGateFactory())
        using (var client = refusing.ClientFor(world.UserId))
        using (var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}"""))
        {
            await AssertProblemAsync(response, 409, "story_fact_sheet_outdated");
        }

        // With the sheet in place the tick is still demanded although the series says fiction.
        using (var gate = RealGateFactory(sheet))
        using (var client = gate.ClientFor(world.UserId))
        using (var response = await ApproveAsync(client, world))
        {
            await AssertProblemAsync(response, 400, "story_attestation_required");
        }

        AssertNothingApproved(world);
        AttestationCount(world).ShouldBe(0);

        // The tick passes and the handler audits it under the same pinned predicate.
        using (var gate = RealGateFactory(sheet))
        using (var client = gate.ClientFor(world.UserId))
        using (var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}"""))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }

        AssertApproved(world);
        AuditRows(world).ShouldBe("episode.story_approved|episode,episode.true_crime_attested|episode");
        AuditDataValues(world).ShouldBe($"{world.VersionId}|ta.v1");
    }

    /// <summary>
    /// Every audit row that names the episode, as <c>action|entity_type</c>: the exact set (not just a
    /// count) plus proof that each row carries the episode as its entity.
    /// </summary>
    private string AuditRows(World world) => SqlProbe.ScalarText(Database,
        "SELECT coalesce(string_agg(action || '|' || coalesce(entity_type, ''), ',' ORDER BY action), '') "
        + $"FROM audit_logs WHERE entity_id='{world.EpisodeId}'");

    /// <summary>Inserts one real <c>fact_sheets</c> row, so a pinned or approved id names one.</summary>
    private async Task<FactSheet> SheetRowAsync(World world) =>
        await new FactSheetBuilder(Database, new TestCurrentUser { Id = world.UserId }).CreateAsync(world.UserId, world.EpisodeId, Ct);

    /// <summary>Adds one fetched url source of the episode with the given content hash.</summary>
    private async Task AddFetchedSourceAsync(World world, Guid id, string sha256)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        db.StorySources.Add(Fetched(world, id, sha256));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Pins the current version to a sheet, with a passing stored score so only the sheet rules decide.</summary>
    private void PinSheet(World world, Guid sheetId) => SqlProbe.Execute(Database,
        $"UPDATE story_versions SET fact_sheet_id = '{sheetId}', similarity_score = 0 WHERE id = '{world.VersionId}'");

    /// <summary>Sets the stored score of the current version.</summary>
    private void SetSimilarity(World world, decimal score) => SqlProbe.Execute(Database,
        $"UPDATE story_versions SET similarity_score = {score.ToString(CultureInfo.InvariantCulture)} WHERE id = '{world.VersionId}'");
}
