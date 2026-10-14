using System.Net;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Research;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>
/// Independent QA boundary set for 02.3.3-T1 against the real host, real gate and PostgreSQL: the wire-level
/// attestation-version edges, the configurable <c>MinCitedSources</c> floor, a source row deleted after the
/// story was written (D19: the message names a count, never a URL), a cited user note, and the strict
/// similarity threshold. It is another part of <see cref="ApproveStoryEndpointTests"/>, so it reuses that
/// suite's seeding, real-gate factory and audit probes.
/// </summary>
public sealed partial class ApproveStoryEndpointTests
{
    [Theory]
    [InlineData(" ta.v1")]
    [InlineData("ta.v1 ")]
    [InlineData("TA.V1")]
    public async Task ApproveStory_TrueCrimeQaTests_AttestationVersionNotByteIdentical_Returns400(string textVersion)
    {
        var world = await SeedAsync(topic: Topic.TrueCrime);
        var sheet = await SeedTrueCrimeApprovalAsync(world);
        using var factory = RealGateFactory(sheet);
        using var client = factory.ClientFor(world.UserId);

        using var response = await ApproveAsync(
            client, world, $"{{\"attestation\":{{\"accepted\":true,\"textVersion\":\"{textVersion}\"}}}}");

        await AssertProblemAsync(response, 400, "story_attestation_required");
        AssertNothingApproved(world);
        AttestationCount(world).ShouldBe(0);
    }

    /// <summary>
    /// <c>MinCitedSources</c> is config, not a literal: with a floor of 3, two cited distinct fetched sources
    /// are refused and the message names the count it found.
    /// </summary>
    [Fact]
    public async Task ApproveStory_TrueCrimeQaTests_MinCitedSourcesThree_TwoQualifying_Returns409()
    {
        var world = await SeedAsync(topic: Topic.TrueCrime);
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
        using var factory = RealGateFactory(sheet).WithSetting("Story:TrueCrime:MinCitedSources", "3");
        using var client = factory.ClientFor(world.UserId);

        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");

        var body = await response.Content.ReadAsStringAsync(Ct);
        await AssertProblemAsync(response, 409, "story_sources_required");
        body.ShouldContain("cites 2", Case.Sensitive, "the message names the count it found");
        body.ShouldNotContain("news.example.com", Case.Insensitive, "D19: the message never carries a source URL");
        AssertNothingApproved(world);
    }

    /// <summary>A source row deleted after the story was written drops out of the citation join, so the floor fails.</summary>
    [Fact]
    public async Task ApproveStory_TrueCrimeQaTests_SourceDeletedAfterTheStoryWasWritten_Returns409WithoutAUrl()
    {
        var world = await SeedAsync(topic: Topic.TrueCrime);
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
        SqlProbe.Execute(Database, $"DELETE FROM story_sources WHERE id = '{second}'");

        using var factory = RealGateFactory(sheet);
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");

        var body = await response.Content.ReadAsStringAsync(Ct);
        await AssertProblemAsync(response, 409, "story_sources_required");
        body.ShouldContain("cites 1", Case.Sensitive, "the message names the count it found");
        body.ShouldNotContain("news.example.com", Case.Insensitive, "D19: a refusal never echoes a URL");
        body.ShouldNotContain("https://news", Case.Insensitive);
        AssertNothingApproved(world);
    }

    /// <summary>
    /// A cited user note is fetched and carries a real content hash, so only the gate's url-only read keeps it
    /// out of the F13 floor (02.5.1 T1: a note is a source, never an article).
    /// </summary>
    [Fact]
    public async Task ApproveStory_TrueCrimeQaTests_CitedUserNoteSource_NeverCounts()
    {
        var world = await SeedAsync(topic: Topic.TrueCrime);
        var sheetRow = await SheetRowAsync(world);
        var article = Guid.CreateVersion7();
        var note = Guid.CreateVersion7();
        await AddFetchedSourceAsync(world, article, new string('a', 64));
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }))
        {
            db.StorySources.Add(StorySource
                .CreateNote(note, world.UserId, world.EpisodeId, "My own recollection of that night.", Start).Value);
            await db.SaveChangesAsync(Ct);
        }

        PinSheet(world, sheetRow.Id);
        var sheet = new ApprovedFactSheet(
            sheetRow.Id,
            sheetRow.Version,
            [new FactForWriter("f1", "Fact one", ["s1"]), new FactForWriter("f2", "Fact two", ["s2"])],
            new Dictionary<string, Guid>(StringComparer.Ordinal) { ["s1"] = article, ["s2"] = note });
        using var factory = RealGateFactory(sheet);
        using var client = factory.ClientFor(world.UserId);

        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");

        await AssertProblemAsync(response, 409, "story_sources_required");
        AssertNothingApproved(world);
    }

    /// <summary>
    /// The classifier uses strict <c>&gt;</c> (02.5.6): a stored score exactly equal to the configured
    /// <c>Similarity:BlockAbove</c> only warns, and one above it refuses (02 §5.9: not topic-scoped).
    /// </summary>
    /// <param name="score">The stored <c>similarity_score</c>.</param>
    /// <param name="expected">The HTTP status the classifier decides.</param>
    [Theory]
    [InlineData(0.5, 200)]
    [InlineData(0.5001, 409)]
    public async Task ApproveStory_QaTests_StoredScoreAtTheBlockThreshold_IsClassifiedStrictly(double score, int expected)
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
        }
        else
        {
            AssertApproved(world);
            AttestationCount(world).ShouldBe(0, "a fiction approval writes no attested row");
        }
    }
}
