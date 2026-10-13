using NightTales.Domain.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>
/// The invariants of the <see cref="FactSheet"/> aggregate (02.5.3 plan §3): <c>CreateDraft</c> rejects a
/// programmer error and computes the content hash, the canonical JSON is stable, and <c>Supersede</c> is the
/// same-transaction demotion of T54.
/// </summary>
public sealed class FactSheetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    private static readonly Guid EpisodeId = Guid.Parse("0197a000-0000-7000-8000-0000000000a2");
    private static readonly Guid RunId = Guid.Parse("0197a000-0000-7000-8000-0000000000a3");
    private static readonly Guid SourceId = Guid.Parse("0197a000-0000-7000-8000-0000000000b1");

    private static FactItem Fact(string id, string statement = "A fact.") =>
        new(id, statement, [SourceId], ["s1"], 0.1m, FactConfidence.Medium, FactOrigin.Model);

    private static Dictionary<string, Guid> Aliases() =>
        new Dictionary<string, Guid>(StringComparer.Ordinal) { ["s1"] = SourceId };

    private static FactSheet Create(params FactItem[] facts) =>
        FactSheet.CreateDraft(UserId, EpisodeId, 1, facts, Aliases(), false, 40, RunId, "facts.v1", Now);

    [Fact]
    public void CreateDraft_SetsDraftFieldsAndComputesHash()
    {
        var sheet = Create(Fact("f1"));

        sheet.Id.ShouldNotBe(Guid.Empty);
        sheet.UserId.ShouldBe(UserId);
        sheet.EpisodeId.ShouldBe(EpisodeId);
        sheet.Version.ShouldBe(1);
        sheet.Status.ShouldBe(FactSheetStatus.Draft);
        sheet.MaxFactId.ShouldBe(40);
        sheet.CreatedBy.ShouldBe(UserId);
        sheet.CreatedAt.ShouldBe(Now);
        sheet.UpdatedAt.ShouldBe(Now);
        sheet.GenerationRunId.ShouldBe(RunId);
        sheet.InputTruncated.ShouldBeFalse();
        sheet.SourceAliases["s1"].ShouldBe(SourceId);
        sheet.ContentSha256.Length.ShouldBe(64);
        sheet.ContentSha256.ShouldBe(FactSheet.ComputeContentSha256(sheet.Facts));
    }

    [Fact]
    public void CreateDraft_EmptyFacts_IsAValidEmptyDraft()
    {
        var sheet = FactSheet.CreateDraft(UserId, EpisodeId, 1, [], Aliases(), true, 0, RunId, "facts.v1", Now);

        sheet.Facts.ShouldBeEmpty();
        sheet.InputTruncated.ShouldBeTrue();
        sheet.ContentSha256.ShouldBe(FactSheet.ComputeContentSha256([]));
    }

    [Fact]
    public void CreateDraft_EmptyRunId_StoresNull()
    {
        var sheet = FactSheet.CreateDraft(UserId, EpisodeId, 1, [], Aliases(), false, 0, Guid.Empty, "user", Now);

        sheet.GenerationRunId.ShouldBeNull();
    }

    [Fact]
    public void CreateDraft_FactWithoutSourceIds_Throws()
    {
        var uncited = new FactItem("f1", "A fact.", [], ["s1"], null, FactConfidence.High, FactOrigin.Model);

        Should.Throw<ArgumentException>(() => Create(uncited));
    }

    [Fact]
    public void CreateDraft_SourceIdOutsideTheAliasMap_Throws()
    {
        var stranger = new FactItem("f1", "A fact.", [Guid.CreateVersion7()], ["s1"], null, FactConfidence.High, FactOrigin.Model);

        Should.Throw<ArgumentException>(() => Create(stranger));
    }

    [Fact]
    public void CreateDraft_SourceAliasOutsideTheAliasMap_Throws()
    {
        // The stored citation is the (alias, id) pair: an alias the map does not name is as unusable as an id
        // it does not carry, because a reader resolves the alias through this map (CODE-11b).
        var strangerAlias = new FactItem("f1", "A fact.", [SourceId], ["n1"], null, FactConfidence.High, FactOrigin.Model);

        Should.Throw<ArgumentException>(() => Create(strangerAlias));
    }

    [Theory]
    [InlineData("f0")]
    [InlineData("f")]
    [InlineData("f01")]
    [InlineData("1")]
    [InlineData("x1")]
    public void CreateDraft_MalformedFactId_Throws(string id)
    {
        var fact = new FactItem(id, "A fact.", [SourceId], ["s1"], null, FactConfidence.High, FactOrigin.Model);

        Should.Throw<ArgumentException>(() => Create(fact));
    }

    [Fact]
    public void CreateDraft_RepeatedFactId_Throws()
    {
        Should.Throw<ArgumentException>(() => Create(Fact("f1"), Fact("f1", "Another fact.")));
    }

    [Fact]
    public void CreateDraft_MaxFactIdBelowHighestId_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => FactSheet.CreateDraft(
            UserId, EpisodeId, 1, [Fact("f2")], Aliases(), false, 1, RunId, "facts.v1", Now));
    }

    [Fact]
    public void CreateDraft_VersionBelowOne_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => FactSheet.CreateDraft(
            UserId, EpisodeId, 0, [], Aliases(), false, 0, RunId, "facts.v1", Now));
    }

    [Fact]
    public void ComputeContentSha256_IsStableAndStatementSensitive()
    {
        var first = FactSheet.ComputeContentSha256([Fact("f1")]);
        var second = FactSheet.ComputeContentSha256([Fact("f1")]);
        var changed = FactSheet.ComputeContentSha256([Fact("f1", "A different fact.")]);

        first.ShouldBe(second);
        changed.ShouldNotBe(first);
    }

    [Fact]
    public void FactSheetJson_SerialisesCanonicalSnakeCase()
    {
        var json = FactSheetJson.SerializeFacts([Fact("f1")]);

        json.ShouldContain("\"id\":\"f1\"");
        json.ShouldContain("\"source_ids\":[\"0197a000-0000-7000-8000-0000000000b1\"]");
        json.ShouldContain("\"source_aliases\":[\"s1\"]");
        json.ShouldContain("\"source_overlap\":0.1");
        json.ShouldContain("\"confidence\":\"medium\"");
        json.ShouldContain("\"origin\":\"model\"");
    }

    [Fact]
    public void Supersede_SetsSuperseded()
    {
        var sheet = Create(Fact("f1"));

        sheet.Supersede();

        sheet.Status.ShouldBe(FactSheetStatus.Superseded);
    }
}
