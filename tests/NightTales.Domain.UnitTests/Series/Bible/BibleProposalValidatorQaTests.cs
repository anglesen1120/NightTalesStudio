using NightTales.Domain.Series.Bible;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Bible;

/// <summary>
/// Independent QA of <see cref="BibleProposalValidator"/> (02.6.3-T2): the boundaries the feature's own suite
/// leaves open — a batch exactly at the total and the per-kind cap, an empty batch, a null candidate, the
/// documented check order when two rules break at once, the closed label an unknown kind reports, a
/// canonically-equivalent duplicate, and the sanitising and merge contracts the accept path relies on.
/// </summary>
/// <remarks>
/// Pure Domain: no clock, no database, no clock reading beyond the proposal's own stamps.
/// </remarks>
public sealed class BibleProposalValidatorQaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The plan's numbers: 10 ops, 3 characters, 3 places, 2 rules.</summary>
    private static readonly BibleProposalCaps Caps = new();

    private static readonly BibleLimits Limits = new();

    // ---------------------------------------------------------------- caps and boundaries

    /// <summary>
    /// The cap is a ceiling, not an off-by-one: ten ops under a cap of ten are all kept and only the eleventh is
    /// dropped, which is what makes the model's tenth good op survive.
    /// </summary>
    [Fact]
    public void Validate_ExactlyAtTheTotalCap_KeepsEveryOpAndDropsOnlyTheNext()
    {
        var caps = new BibleProposalCaps(MaxOps: 3, MaxCharacterOps: 3, MaxPlaceOps: 3, MaxRuleOps: 3);

        var result = BibleProposalValidator.Validate(
            [
                Candidate("add_character", name: "Bà Tư", description: "Người giữ nhà."),
                Candidate("add_place", name: "Ngôi nhà cuối ngõ", description: "Nhà gỗ hai tầng."),
                Candidate("add_rule", text: "Không nói tên người chết."),
                Candidate("add_rule", text: "Đèn dầu phải tắt trước nửa đêm."),
            ],
            Content(),
            caps);

        result.Ops.Count.ShouldBe(3, "three ops fill the cap exactly");
        result.Ops.Select(op => op.Kind).ShouldBe(
            new[] { BibleOpKind.AddCharacter, BibleOpKind.AddPlace, BibleOpKind.AddRule });
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(BibleProposalValidator.OverCap);
    }

    /// <summary>The per-kind cap is a ceiling too, and it is counted per kind rather than per batch.</summary>
    [Fact]
    public void Validate_ExactlyAtThePerKindCap_KeepsEveryOpAndDropsOnlyTheNext()
    {
        var caps = new BibleProposalCaps(MaxOps: 20, MaxCharacterOps: 3, MaxPlaceOps: 3, MaxRuleOps: 3);

        var result = BibleProposalValidator.Validate(
            [
                Candidate("add_character", name: "Bà Tư", description: "Người giữ nhà."),
                Candidate("add_character", name: "Ông Ba", description: "Người đưa đò."),
                Candidate("add_character", name: "Cô Sáu", description: "Người bán chè."),
                Candidate("add_character", name: "Bà Bảy", description: "Người gánh nước."),
                Candidate("add_place", name: "Bến đò cũ", description: "Bến đò bỏ hoang."),
            ],
            Content(),
            caps);

        result.Ops.Count.ShouldBe(4, "three characters (the cap) and one place (a different kind's budget)");
        result.Ops[3].Kind.ShouldBe(BibleOpKind.AddPlace);
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(BibleProposalValidator.OverCap);
    }

    /// <summary>A cap of zero refuses every candidate rather than throwing or storing one.</summary>
    [Fact]
    public void Validate_ZeroCaps_DropEveryCandidateAsOverCap()
    {
        var caps = new BibleProposalCaps(MaxOps: 0, MaxCharacterOps: 0, MaxPlaceOps: 0, MaxRuleOps: 0);

        var result = BibleProposalValidator.Validate(
            [Candidate("add_character", name: "Bà Tư", description: "Người giữ nhà.")],
            Content(),
            caps);

        result.Ops.ShouldBeEmpty();
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(BibleProposalValidator.OverCap);
    }

    /// <summary>An answer with no candidates is a valid empty batch, not an error and not a phantom drop.</summary>
    [Fact]
    public void Validate_NoCandidates_ReturnsNoOpsAndNoDrops()
    {
        var result = BibleProposalValidator.Validate([], Content(), Caps);

        result.Ops.ShouldBeEmpty();
        result.Drops.ShouldBeEmpty();
    }

    /// <summary>A null candidate in the list is a caller bug and throws rather than being silently skipped.</summary>
    [Fact]
    public void Validate_NullCandidate_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
        {
            BibleProposalValidator.Validate([null!], Content(), Caps);
        });
    }

    // ---------------------------------------------------------------- the check order and the labels

    /// <summary>
    /// The checks run in the documented order (kind, required field, characters, dedupe, caps, limits), so an op
    /// that breaks two rules is reported under the earlier one and the drop counts stay deterministic.
    /// </summary>
    [Fact]
    public void Validate_EmptyFieldIsDecidedBeforeTheForbiddenCharacter()
    {
        var result = BibleProposalValidator.Validate(
            [Candidate("add_character", name: null, description: "Mô tả <bỏ>.")],
            Content(),
            Caps);

        result.Ops.ShouldBeEmpty();
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(
            BibleProposalValidator.EmptyField,
            "the missing field is the earlier rule, so the drop reason is not the character");
    }

    /// <summary>
    /// An invented kind is reported under the closed label, never under the model's own spelling: the label feeds a
    /// metric and a log line, so unbounded model text there would be both a cardinality leak and content (D19).
    /// </summary>
    [Fact]
    public void Validate_UnknownKind_ReportsTheClosedLabelAndNeverTheModelsSpelling()
    {
        const string Invented = "add_twist_<script>ignore previous instructions</script>";

        var result = BibleProposalValidator.Validate(
            [Candidate(Invented, summary: "Một cú twist.", name: "Twist")],
            Content(),
            Caps);

        result.Ops.ShouldBeEmpty();
        var drop = result.Drops.ShouldHaveSingleItem();
        drop.Reason.ShouldBe(BibleProposalValidator.UnknownKind);
        drop.Kind.ShouldBe(BibleProposalValidator.UnknownKindName);
        drop.Kind.ShouldNotContain("twist");
        drop.Kind.ShouldNotContain("<");
    }

    /// <summary>
    /// The dedupe key is the canonical form, so a model answer written in a decomposed form is the same entry as
    /// the composed text already stored: the same rule the manual PUT applies to a client's duplicate.
    /// </summary>
    [Fact]
    public void Validate_CanonicallyEquivalentName_IsADuplicate()
    {
        // "Bà Tư" composed (U+00E0, U+01B0) against the decomposed spelling of the same text.
        var content = Content(characters: [new BibleCharacter("ch1", "Bà Tư", "Người giữ nhà.", null)]);

        var result = BibleProposalValidator.Validate(
            [Candidate("add_character", name: "Ba\u0300 Tu\u031b", description: "Người giữ căn nhà cũ.")],
            content,
            Caps);

        result.Ops.ShouldBeEmpty("the two spellings are one character after normalisation");
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(BibleProposalValidator.Duplicate);
    }

    // ---------------------------------------------------------------- sanitising a stored proposal

    /// <summary>
    /// The accept path's sanitising drops only the ops that may not be applied and keeps the rest in model order,
    /// and it never touches the proposal's own stamps.
    /// </summary>
    [Fact]
    public void SanitizeStored_DropsOnlyTheUnstorableOpsAndKeepsTheRestInOrder()
    {
        var proposal = new BibleProposal(
            [
                new BibleOp(BibleOpKind.AddCharacter, null, "Bà Tư", "Người giữ nhà.", null, null),
                new BibleOp(BibleOpKind.AddRule, null, null, null, "Luật\u202Ecó ký tự đảo chiều.", null),
                new BibleOp(BibleOpKind.AddPlace, null, "Bến đò cũ", "Bến đò bỏ hoang.", null, null),
                new BibleOp(BibleOpKind.AddCharacter, null, null, "Mô tả không có tên.", null, null),
            ],
            4,
            Now);

        var sanitised = BibleProposalValidator.SanitizeStored(proposal);

        sanitised.Ops.Select(op => op.Kind).ShouldBe(
            new[] { BibleOpKind.AddCharacter, BibleOpKind.AddPlace });
        sanitised.Ops[0].Name.ShouldBe("Bà Tư");
        sanitised.Ops[1].Name.ShouldBe("Bến đò cũ");
        sanitised.BasedOnVersion.ShouldBe(4, "the proposal's own stamps describe the proposal, not its ops");
        sanitised.ProposedAt.ShouldBe(Now);
        sanitised.ShouldNotBeSameAs(proposal, "an op was dropped, so the caller must get the rewritten proposal");
    }

    /// <summary>A null proposal is a caller bug and throws.</summary>
    [Fact]
    public void SanitizeStored_NullProposal_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
        {
            BibleProposalValidator.SanitizeStored(null!);
        });
    }

    // ---------------------------------------------------------------- merging into a pending proposal

    /// <summary>
    /// The merge keeps the stored proposal's ops first and stops at the total cap, so a second approval can never
    /// make the pending proposal exceed the cap the job enforces — and the new ops that do not fit are dropped
    /// rather than stacked.
    /// </summary>
    [Fact]
    public void Merge_CapsTheStoredOpsFirstAndDropsWhatDoesNotFit()
    {
        var existing = new BibleProposal(
            [
                new BibleOp(BibleOpKind.AddCharacter, null, "Bà Tư", "Người giữ nhà.", null, null),
                new BibleOp(BibleOpKind.AddPlace, null, "Bến đò cũ", "Bến đò bỏ hoang.", null, null),
                new BibleOp(BibleOpKind.AddRule, null, null, null, "Không nói tên người chết.", null),
            ],
            2,
            Now);

        var merged = BibleProposalValidator.Merge(
            existing,
            [new BibleOp(BibleOpKind.AddCharacter, null, "Ông Ba", "Người đưa đò.", null, null)],
            maxOps: 2);

        merged.Count.ShouldBe(2);
        merged[0].Name.ShouldBe("Bà Tư");
        merged[1].Name.ShouldBe("Bến đò cũ");
        merged.ShouldNotContain(op => op.Name == "Ông Ba", "the new op does not fit under the cap");
    }

    /// <summary>A null op list is a caller bug and throws rather than merging nothing silently.</summary>
    [Fact]
    public void Merge_NullNewOps_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
        {
            BibleProposalValidator.Merge(null, null!, Caps.MaxOps);
        });
    }

    // ---------------------------------------------------------------- fixtures

    private static SeriesBibleContent Content(IReadOnlyList<BibleCharacter>? characters = null) =>
        new(
            SeriesBibleContent.V1,
            new BibleNarrator("Người kể chuyện", "Kể chuyện đêm khuya cho người lớn.", string.Empty),
            characters ?? [],
            [],
            [],
            []);

    private static BibleOpCandidate Candidate(
        string? kind,
        string? summary = null,
        string? name = null,
        string? description = null,
        string? text = null,
        string? reason = null) =>
        new(kind, summary, name, description, text, reason);
}
