using NightTales.Application.Common.Errors;
using NightTales.Domain.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>
/// The invariants of the <see cref="FactSheet"/> aggregate (02.5.3 plan §3): <c>CreateDraft</c> rejects a
/// programmer error and computes the content hash, the canonical JSON is stable, and <c>Supersede</c> is the
/// same-transaction demotion of T54. The 02.5.4-T1 part adds the hand-edit factory (<c>CreateUserDraft</c>) and
/// the approve transition, whose refusals are the 412 <c>fact_sheet_conflict</c>.
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
    public void CreateUserDraft_StartsTheNextDraftVersion()
    {
        var previous = Create(Fact("f1"));
        var editor = Guid.Parse("0197a000-0000-7000-8000-0000000000a9");
        var at = Now.AddMinutes(5);

        var draft = FactSheet.CreateUserDraft(previous, [Fact("f2")], Aliases(), 41, editor, at);

        draft.Id.ShouldNotBe(previous.Id, "an edit is a new row: that is what makes the fs- ETag work (D9)");
        draft.Version.ShouldBe(2);
        draft.Status.ShouldBe(FactSheetStatus.Draft);
        draft.EpisodeId.ShouldBe(EpisodeId);
        draft.UserId.ShouldBe(UserId, "the version belongs to the episode's owner");
        draft.CreatedBy.ShouldBe(editor);
        draft.GenerationRunId.ShouldBeNull("a hand-edited version carries no generation run");
        draft.PromptVersion.ShouldBe(FactSheet.UserPromptVersion);
        draft.PromptVersion.ShouldBe("user");
        draft.InputTruncated.ShouldBe(previous.InputTruncated);
        draft.MaxFactId.ShouldBe(41);
        draft.CreatedAt.ShouldBe(at);
        draft.UpdatedAt.ShouldBe(at);
        draft.ApprovedBy.ShouldBeNull();
        draft.ApprovedAt.ShouldBeNull();
        draft.Facts.Count.ShouldBe(1);
        draft.Facts[0].Id.ShouldBe("f2");
    }

    [Fact]
    public void CreateUserDraft_RecomputesTheContentHash()
    {
        var previous = Create(Fact("f1"));

        var draft = FactSheet.CreateUserDraft(previous, [Fact("f2", "A new fact.")], Aliases(), 2, UserId, Now);

        draft.ContentSha256.ShouldBe(FactSheet.ComputeContentSha256(draft.Facts));
        draft.ContentSha256.ShouldNotBe(previous.ContentSha256);
    }

    [Fact]
    public void CreateUserDraft_InlineEditsVersionNumber()
    {
        var previous = Create(Fact("f1"));
        var second = FactSheet.CreateUserDraft(previous, [Fact("f2")], Aliases(), 2, UserId, Now);
        var third = FactSheet.CreateUserDraft(second, [], Aliases(), 2, UserId, Now);

        third.Version.ShouldBe(3);
    }

    [Fact]
    public void CreateUserDraft_EmptyFactList_IsAValidEmptyDraft()
    {
        // The body may save an empty draft; approve refuses it later with fact_sheet_sources_insufficient (F13).
        var previous = Create(Fact("f1"));

        var draft = FactSheet.CreateUserDraft(previous, [], Aliases(), 40, UserId, Now);

        draft.Facts.ShouldBeEmpty();
        draft.ContentSha256.ShouldBe(FactSheet.ComputeContentSha256([]));
    }

    [Fact]
    public void CreateUserDraft_KeepsEveryCreateDraftInvariant()
    {
        var previous = Create(Fact("f1"));
        var uncited = new FactItem("f2", "A fact.", [], ["s1"], null, FactConfidence.High, FactOrigin.User);
        var strangerSource = new FactItem("f2", "A fact.", [Guid.CreateVersion7()], ["s1"], null, FactConfidence.High, FactOrigin.User);
        var malformedId = new FactItem("g2", "A fact.", [SourceId], ["s1"], null, FactConfidence.High, FactOrigin.User);

        Should.Throw<ArgumentException>(
            () => FactSheet.CreateUserDraft(previous, [uncited], Aliases(), 2, UserId, Now));
        Should.Throw<ArgumentException>(
            () => FactSheet.CreateUserDraft(previous, [strangerSource], Aliases(), 2, UserId, Now));
        Should.Throw<ArgumentException>(
            () => FactSheet.CreateUserDraft(previous, [malformedId], Aliases(), 2, UserId, Now));
        Should.Throw<ArgumentOutOfRangeException>(
            () => FactSheet.CreateUserDraft(previous, [Fact("f2")], Aliases(), 1, UserId, Now));
        Should.Throw<ArgumentNullException>(
            () => FactSheet.CreateUserDraft(null!, [], Aliases(), 1, UserId, Now));
    }

    [Fact]
    public void CreateUserDraft_DoesNotTouchThePreviousVersion()
    {
        // Editing an approved sheet opens the next draft and leaves the approved row exactly as it was: it is
        // superseded only when the new draft is approved (D9/T54).
        var previous = Approved();
        var before = (previous.Status, previous.Version, previous.Facts.Count, previous.ContentSha256);

        var draft = FactSheet.CreateUserDraft(previous, [Fact("f2")], Aliases(), 2, UserId, Now.AddMinutes(1));

        (previous.Status, previous.Version, previous.Facts.Count, previous.ContentSha256).ShouldBe(before);
        draft.Version.ShouldBe(2);
        draft.Status.ShouldBe(FactSheetStatus.Draft);
    }

    [Fact]
    public void Approve_FromDraft_SetsApprovedByAndAt()
    {
        var sheet = Create(Fact("f1"));
        var approver = Guid.Parse("0197a000-0000-7000-8000-0000000000a8");
        var at = Now.AddHours(1);

        var result = sheet.Approve(approver, at);

        result.IsError.ShouldBeFalse();
        sheet.Status.ShouldBe(FactSheetStatus.Approved);
        sheet.ApprovedBy.ShouldBe(approver);
        sheet.ApprovedAt.ShouldBe(at);
        sheet.UpdatedAt.ShouldBe(at);
    }

    [Fact]
    public void Approve_FromApproved_Returns412AndChangesNothing()
    {
        var sheet = Approved();
        var before = (sheet.Status, sheet.ApprovedBy, sheet.ApprovedAt, sheet.UpdatedAt);

        var result = sheet.Approve(Guid.CreateVersion7(), Now.AddHours(2));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(FactSheet.ConflictCode);
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
        (sheet.Status, sheet.ApprovedBy, sheet.ApprovedAt, sheet.UpdatedAt).ShouldBe(before);
    }

    [Fact]
    public void Approve_FromSuperseded_Returns412()
    {
        var sheet = Approved();
        sheet.Supersede();

        var result = sheet.Approve(Guid.CreateVersion7(), Now.AddHours(2));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(FactSheet.ConflictCode);
        sheet.Status.ShouldBe(FactSheetStatus.Superseded);
    }

    [Fact]
    public void Supersede_ApprovedBecomesSuperseded()
    {
        var sheet = Approved();

        sheet.Supersede();

        sheet.Status.ShouldBe(FactSheetStatus.Superseded);
    }

    [Fact]
    public void Supersede_DraftIsUntouched()
    {
        // The draft an edit just created is the row the user must approve: it is never demoted (T54).
        var sheet = Create(Fact("f1"));

        sheet.Supersede();

        sheet.Status.ShouldBe(FactSheetStatus.Draft);
    }

    [Fact]
    public void Supersede_IsIdempotent()
    {
        var sheet = Approved();
        sheet.Supersede();

        sheet.Supersede();

        sheet.Status.ShouldBe(FactSheetStatus.Superseded);
    }

    [Fact]
    public void Supersede_StampsNothingElse()
    {
        var sheet = Approved();
        var approvedBy = sheet.ApprovedBy;
        var approvedAt = sheet.ApprovedAt;
        var updatedAt = sheet.UpdatedAt;

        sheet.Supersede();

        sheet.ApprovedBy.ShouldBe(approvedBy);
        sheet.ApprovedAt.ShouldBe(approvedAt);
        sheet.UpdatedAt.ShouldBe(updatedAt, "only the status moves");
    }

    [Fact]
    public void FactSheet_Codes_MatchTheErrorCatalogue()
    {
        // The Domain may not reference Application/Common/Errors/ErrorCodes.cs; this test is the join.
        FactSheet.ConflictCode.ShouldBe(ErrorCodes.FactSheetConflict);
        FactSheet.UserPromptVersion.ShouldBe("user");
    }

    private static FactSheet Approved()
    {
        var sheet = Create(Fact("f1"));
        sheet.Approve(UserId, Now.AddMinutes(1)).IsError.ShouldBeFalse();

        return sheet;
    }
}
