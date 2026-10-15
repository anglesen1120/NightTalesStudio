using System.Globalization;
using NightTales.Domain.Series.Bible;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Bible;

/// <summary>
/// 02.6.3-T2 (plan §7.1 step 5, §9): the post-processing of a proposal's op list — the four kinds only, the
/// required field of each kind, the character rules the PUT validator applies, the dedupe against the content and
/// within the batch, the caps and the plan §3.2 limits — plus the merge of a second approval into a pending
/// proposal and the accept path's sanitising of an already stored proposal.
/// </summary>
public sealed class BibleProposalValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);

    /// <summary>The plan's numbers: 10 ops, 3 characters, 3 places, 2 rules.</summary>
    private static readonly BibleProposalCaps Caps = new();

    private static readonly BibleLimits Limits = new();

    private static SeriesBibleContent Content(
        IReadOnlyList<BibleCharacter>? characters = null,
        IReadOnlyList<BiblePlace>? places = null,
        IReadOnlyList<BibleRule>? rules = null,
        IReadOnlyList<BibleEpisodeSummary>? summaries = null) =>
        new(
            SeriesBibleContent.V1,
            new BibleNarrator("Người kể chuyện", "Kể chuyện đêm khuya cho người lớn.", string.Empty),
            characters ?? [],
            places ?? [],
            rules ?? [],
            summaries ?? []);

    private static BibleOpCandidate Candidate(
        string? kind,
        string? summary = null,
        string? name = null,
        string? description = null,
        string? text = null,
        string? reason = null) =>
        new(kind, summary, name, description, text, reason);

    private static BibleProposal Proposal(params BibleOp[] ops) => new(ops, 3, Now);

    [Fact]
    public void BibleProposalValidator_UnknownOpKind_Dropped()
    {
        var result = BibleProposalValidator.Validate(
            [
                Candidate("set_narrator", name: "Người kể chuyện"),
                Candidate(null, name: "Bà Tư"),
                Candidate("Add_Character", name: "Bà Tư"),
                Candidate("add_character", name: "Bà Tư"),
            ],
            Content(),
            Caps);

        result.Ops.Count.ShouldBe(1);
        result.Ops[0].Name.ShouldBe("Bà Tư", "only the four snake_case kinds are op kinds");
        result.Drops.Count.ShouldBe(3);
        result.Drops.ShouldAllBe(drop => drop.Reason == BibleProposalValidator.UnknownKind);
        result.Drops.ShouldAllBe(drop => drop.Kind == BibleProposalValidator.UnknownKindName, "the model's own spelling is never echoed into a metric label");
    }

    [Fact]
    public void BibleProposalValidator_BlankRequiredField_Dropped()
    {
        var result = BibleProposalValidator.Validate(
            [
                Candidate("add_summary", summary: "Một câu chuyện.", name: "   "),
                Candidate("add_summary", summary: null, name: "Tập một"),
                Candidate("add_character", name: " "),
                Candidate("add_place", name: null),
                Candidate("add_rule", text: "  "),
            ],
            Content(),
            Caps);

        result.Ops.ShouldBeEmpty();
        result.Drops.Count.ShouldBe(5);
        result.Drops.ShouldAllBe(drop => drop.Reason == BibleProposalValidator.EmptyField);
    }

    [Fact]
    public void BibleProposalValidator_ForbiddenCharacter_Dropped()
    {
        var result = BibleProposalValidator.Validate(
            [
                Candidate("add_character", name: "<b>Bà Tư", description: "Người giữ nhà."),
                Candidate("add_character", name: "Bà Tư", description: "Người giữ nhà\u200B."),
                Candidate("add_place", name: "Ngôi nhà", description: "Nhà gỗ\u202E hai tầng."),
                Candidate("add_rule", text: "Không nói tên thật.", reason: "vì\u00AD sao"),
                Candidate("add_rule", text: "Không nói tên thật."),
            ],
            Content(),
            Caps);

        result.Ops.Count.ShouldBe(1);
        result.Ops[0].Text.ShouldBe("Không nói tên thật.");
        result.Drops.Count.ShouldBe(4);
        result.Drops.ShouldAllBe(drop => drop.Reason == BibleProposalValidator.ForbiddenCharacter);
    }

    [Fact]
    public void BibleProposalValidator_DuplicateCharacterAgainstContent_Dropped()
    {
        var content = Content(characters: [new BibleCharacter("ch1", "Bà Tư", "Người giữ nhà.", null)]);

        var result = BibleProposalValidator.Validate(
            [
                Candidate("add_character", name: "bà  tư.", description: "Người giữ căn nhà cũ."),
                Candidate("add_character", name: "Ông Ba", description: "Người đưa đò."),
            ],
            content,
            Caps);

        result.Ops.Count.ShouldBe(1);
        result.Ops[0].Name.ShouldBe("Ông Ba");
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(BibleProposalValidator.Duplicate);
    }

    [Fact]
    public void BibleProposalValidator_NamesAreComparedPerArray_SoACharacterMayShareAPlacesName()
    {
        var content = Content(places: [new BiblePlace("pl1", "Bà Tư", "Nhà của bà Tư.")]);

        var result = BibleProposalValidator.Validate(
            [Candidate("add_character", name: "Bà Tư", description: "Người giữ nhà.")],
            content,
            Caps);

        result.Ops.ShouldHaveSingleItem();
        result.Drops.ShouldBeEmpty();
    }

    [Fact]
    public void BibleProposalValidator_WithinBatchDuplicate_Dropped()
    {
        var result = BibleProposalValidator.Validate(
            [
                Candidate("add_rule", text: "Không nói tên người chết."),
                Candidate("add_rule", text: "không  nói tên người chết"),
            ],
            Content(),
            Caps);

        result.Ops.ShouldHaveSingleItem().Text.ShouldBe("Không nói tên người chết.");
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(BibleProposalValidator.Duplicate);
    }

    [Fact]
    public void BibleProposalValidator_SecondSummaryOp_Dropped()
    {
        var result = BibleProposalValidator.Validate(
            [
                Candidate("add_summary", summary: "Tập một.", name: "Tập một"),
                Candidate("add_summary", summary: "Tập hai.", name: "Tập hai"),
            ],
            Content(),
            Caps);

        result.Ops.ShouldHaveSingleItem().Summary.ShouldBe("Tập một.", "one batch is one episode, so it has one summary");
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(BibleProposalValidator.Duplicate);
    }

    [Fact]
    public void BibleProposalValidator_OverOpCap_TrimsToCap()
    {
        var caps = new BibleProposalCaps(MaxOps: 2, MaxCharacterOps: 5, MaxPlaceOps: 5, MaxRuleOps: 5);

        var result = BibleProposalValidator.Validate(
            [
                Candidate("add_character", name: "Bà Tư"),
                Candidate("add_character", name: "Ông Ba"),
                Candidate("add_character", name: "Cô Sáu"),
            ],
            Content(),
            caps);

        result.Ops.Count.ShouldBe(2);
        result.Ops[0].Name.ShouldBe("Bà Tư");
        result.Ops[1].Name.ShouldBe("Ông Ba");
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(BibleProposalValidator.OverCap);
    }

    [Fact]
    public void BibleProposalValidator_OverPerKindCap_Dropped()
    {
        var caps = new BibleProposalCaps(MaxOps: 10, MaxCharacterOps: 1, MaxPlaceOps: 3, MaxRuleOps: 2);

        var result = BibleProposalValidator.Validate(
            [Candidate("add_character", name: "Bà Tư"), Candidate("add_character", name: "Ông Ba")],
            Content(),
            caps);

        result.Ops.ShouldHaveSingleItem().Name.ShouldBe("Bà Tư");
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(BibleProposalValidator.OverCap);
    }

    [Fact]
    public void BibleProposalValidator_SummaryOver200_TruncatedOnWordBoundary()
    {
        var words = Enumerable.Range(0, 60).Select(index => string.Create(CultureInfo.InvariantCulture, $"từ{index:00000000}")).ToList();
        var summary = string.Join(' ', words);
        summary.Length.ShouldBeGreaterThan(200);

        var result = BibleProposalValidator.Validate(
            [Candidate("add_summary", summary: summary, name: "Tập một")],
            Content(),
            Caps);

        var truncated = result.Ops.ShouldHaveSingleItem().Summary
            ?? throw new InvalidOperationException("A summary op keeps its summary.");
        truncated.Length.ShouldBeLessThanOrEqualTo(Limits.SummaryChars);
        summary.ShouldStartWith(truncated);
        truncated.Split(' ').ShouldAllBe(word => word.Length == 10, "a word boundary cut never leaves half a word");
        result.Drops.ShouldBeEmpty("a length is truncated, never a reason to drop an op");
    }

    [Fact]
    public void BibleProposalValidator_OverLongDescriptionAndName_TruncatedToTheFieldLimits()
    {
        var result = BibleProposalValidator.Validate(
            [
                Candidate(
                    "add_character",
                    name: new string('n', Limits.NameChars + 20),
                    description: new string('d', Limits.DescriptionChars + 20)),
            ],
            Content(),
            Caps);

        var op = result.Ops.ShouldHaveSingleItem();
        op.Name!.Length.ShouldBe(Limits.NameChars);
        op.Description!.Length.ShouldBe(Limits.DescriptionChars);
    }

    [Fact]
    public void BibleProposalValidator_ContentAtTheCharacterLimit_Dropped()
    {
        var content = Content(
            characters: [.. Enumerable.Range(1, Limits.MaxCharacters).Select(index => new BibleCharacter($"ch{index}", $"Nhân vật {index}", string.Empty, null))]);

        var result = BibleProposalValidator.Validate(
            [Candidate("add_character", name: "Bà Tư", description: "Người giữ nhà.")],
            content,
            Caps);

        result.Ops.ShouldBeEmpty();
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(BibleProposalValidator.OverLimit);
    }

    [Fact]
    public void BibleProposalValidator_OverTheContentLimit_IsCheckedAgainstTheConfiguredLimits()
    {
        var limits = new BibleLimits { MaxRules = 1 };
        var content = Content(rules: [new BibleRule("rl1", "Không nói tên thật.")]);

        var result = BibleProposalValidator.Validate(
            [Candidate("add_rule", text: "Người chết không quay lại.")],
            content,
            Caps,
            limits);

        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(BibleProposalValidator.OverLimit);
    }

    [Fact]
    public void BibleProposalValidator_Merge_AppendsNewOpsAndDedupesAgainstTheStoredOnes()
    {
        var existing = Proposal(
            new BibleOp(BibleOpKind.AddCharacter, null, "Bà Tư", "Người giữ nhà.", null, null),
            new BibleOp(BibleOpKind.AddRule, null, null, null, "Không nói tên thật.", null));

        var merged = BibleProposalValidator.Merge(
            existing,
            [
                new BibleOp(BibleOpKind.AddCharacter, null, "bà  tư.", "Người giữ căn nhà cũ.", null, null),
                new BibleOp(BibleOpKind.AddPlace, null, "Ngôi nhà cuối ngõ", "Nhà gỗ hai tầng.", null, null),
                new BibleOp(BibleOpKind.AddRule, null, null, null, "Không nói tên thật.", null),
            ],
            Caps.MaxOps);

        merged.Count.ShouldBe(3, "the order is the stored ops first, then the new ones");
        merged[0].Name.ShouldBe("Bà Tư");
        merged[1].Text.ShouldBe("Không nói tên thật.");
        merged[1].Kind.ShouldBe(BibleOpKind.AddRule);
        merged[2].Name.ShouldBe("Ngôi nhà cuối ngõ");
        merged[2].Kind.ShouldBe(BibleOpKind.AddPlace);
    }

    [Fact]
    public void BibleProposalValidator_Merge_KeepsBothSummaryOps()
    {
        var existing = Proposal(new BibleOp(BibleOpKind.AddSummary, "Tập một.", "Tập một", null, null, null));

        var merged = BibleProposalValidator.Merge(
            existing,
            [new BibleOp(BibleOpKind.AddSummary, "Tập hai.", "Tập hai", null, null, null)],
            Caps.MaxOps);

        merged.Count.ShouldBe(2, "an op carries no episode id, so a replace would silently lose an episode's summary");
        merged[0].Summary.ShouldBe("Tập một.");
        merged[1].Summary.ShouldBe("Tập hai.");
    }

    [Fact]
    public void BibleProposalValidator_Merge_StopsAtTheCapAndAcceptsNoProposal()
    {
        var existing = Proposal(new BibleOp(BibleOpKind.AddCharacter, null, "Bà Tư", null, null, null));
        var newOps = new List<BibleOp>
        {
            new(BibleOpKind.AddPlace, null, "Ngôi nhà cuối ngõ", null, null, null),
            new(BibleOpKind.AddRule, null, null, null, "Không nói tên thật.", null),
        };

        BibleProposalValidator.Merge(existing, newOps, maxOps: 2).Count.ShouldBe(2);
        BibleProposalValidator.Merge(existing, newOps, maxOps: 0).ShouldBeEmpty();
        BibleProposalValidator.Merge(null, newOps, maxOps: 1).Count.ShouldBe(1);
    }

    [Fact]
    public void BibleProposalValidator_SanitizeStored_DropsTheOpsThatMayNotBeApplied()
    {
        var proposal = Proposal(
            new BibleOp(BibleOpKind.AddCharacter, null, "Bà Tư", "Người giữ nhà.", null, null),
            new BibleOp(BibleOpKind.AddPlace, null, "Ngôi <nhà>", "Nhà gỗ hai tầng.", null, null),
            new BibleOp(BibleOpKind.AddRule, null, null, null, "  ", null),
            new BibleOp(BibleOpKind.AddRule, null, null, null, "Không nói tên thật.", "vì\u202E sao"));

        var sanitised = BibleProposalValidator.SanitizeStored(proposal);

        sanitised.Ops.ShouldHaveSingleItem().Name.ShouldBe("Bà Tư");
        sanitised.BasedOnVersion.ShouldBe(proposal.BasedOnVersion);
        sanitised.ProposedAt.ShouldBe(proposal.ProposedAt);
    }

    [Fact]
    public void BibleProposalValidator_SanitizeStored_CleanProposal_IsTheSameProposal()
    {
        var proposal = Proposal(new BibleOp(BibleOpKind.AddCharacter, null, "Bà Tư", "Người giữ nhà.", null, null));

        var sanitised = BibleProposalValidator.SanitizeStored(proposal);

        sanitised.ShouldBeSameAs(proposal, "a proposal whose ops are all storable is not copied");
    }

    [Fact]
    public void BibleProposalValidator_ValidBatch_KeepsTheOrderAndReportsNoDrop()
    {
        var result = BibleProposalValidator.Validate(
            [
                Candidate("add_character", name: "Bà Tư", description: "Người giữ nhà."),
                Candidate("add_place", name: "Ngôi nhà cuối ngõ", description: "Nhà gỗ hai tầng."),
                Candidate("add_rule", text: "Không nói tên người chết."),
                Candidate("add_summary", summary: "Một người trở về căn nhà cũ.", name: "Tập một"),
            ],
            Content(),
            Caps);

        result.Drops.ShouldBeEmpty();
        result.Ops.Select(op => op.Kind).ShouldBe(
            new[]
            {
                BibleOpKind.AddCharacter,
                BibleOpKind.AddPlace,
                BibleOpKind.AddRule,
                BibleOpKind.AddSummary,
            });
    }

    [Fact]
    public void BibleProposalValidator_Merge_KeepsEverySummaryEvenWhenTheCapIsFull()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var stored = new List<BibleOp>();
        for (var i = 1; i <= 9; i++)
        {
            stored.Add(new BibleOp(BibleOpKind.AddCharacter, null, $"Nhân vật {i}", "Mô tả.", null, null));
        }

        stored.Add(new BibleOp(BibleOpKind.AddSummary, "Tập một.", "Tập một", null, null, null, first));
        var existing = Proposal(stored.ToArray());

        var merged = BibleProposalValidator.Merge(
            existing,
            [
                new BibleOp(BibleOpKind.AddSummary, "Tập hai.", "Tập hai", null, null, null, second),
                new BibleOp(BibleOpKind.AddPlace, null, "Ngôi nhà cuối ngõ", "Nhà gỗ hai tầng.", null, null),
            ],
            Caps.MaxOps);

        merged.Count.ShouldBe(Caps.MaxOps, "the cap still holds");
        merged.Where(op => op.Kind == BibleOpKind.AddSummary).Select(op => op.EpisodeId)
            .ShouldBe([first, second], "an episode's summary is never cut by the cap, so each episode keeps its own");
    }

    [Fact]
    public void BibleProposalValidator_Merge_ReplacesTheSummaryOfTheSameEpisodeOnly()
    {
        var episode = Guid.NewGuid();
        var existing = Proposal(new BibleOp(BibleOpKind.AddSummary, "Bản cũ.", "Tập một", null, null, null, episode));

        var merged = BibleProposalValidator.Merge(
            existing,
            [new BibleOp(BibleOpKind.AddSummary, "Bản mới.", "Tập một", null, null, null, episode)],
            Caps.MaxOps);

        merged.ShouldHaveSingleItem().Summary.ShouldBe("Bản mới.", "one summary per episode, the newer text wins");
    }

    [Fact]
    public void BibleProposalValidator_SanitizeStored_NullsTheMembersThatTheKindDoesNotUse()
    {
        var proposal = Proposal(
            new BibleOp(BibleOpKind.AddSummary, "Một câu.", "Tập một", "thừa", "thừa", null),
            new BibleOp(BibleOpKind.AddCharacter, "thừa", "Bà Tư", "Người giữ nhà.", "thừa", null),
            new BibleOp(BibleOpKind.AddRule, "thừa", "thừa", "thừa", "Không nói tên thật.", "vì sao"));

        var sanitised = BibleProposalValidator.SanitizeStored(proposal);

        sanitised.Ops[0].ShouldBe(new BibleOp(BibleOpKind.AddSummary, "Một câu.", "Tập một", null, null, null));
        sanitised.Ops[1].ShouldBe(new BibleOp(BibleOpKind.AddCharacter, null, "Bà Tư", "Người giữ nhà.", null, null));
        sanitised.Ops[2].ShouldBe(new BibleOp(BibleOpKind.AddRule, null, null, null, "Không nói tên thật.", "vì sao"));
    }

    [Fact]
    public void BibleProposalValidator_SanitizeStored_CapsTheReasonWithoutSplittingASurrogatePair()
    {
        var proposal = Proposal(
            new BibleOp(BibleOpKind.AddRule, null, null, null, "Không nói tên thật.", new string('a', 400)),
            new BibleOp(BibleOpKind.AddRule, null, null, null, "Không nói tên thật.", new string('a', 299) + "😀"));

        var sanitised = BibleProposalValidator.SanitizeStored(proposal);

        sanitised.Ops[0].Reason.ShouldBe(new string('a', BibleProposalValidator.MaxReasonLength));
        sanitised.Ops[1].Reason.ShouldBe(new string('a', 299), "a cut at 300 would split the emoji, so the cap steps back one");
    }

    [Fact]
    public void BibleProposalValidator_IsStorable_RefusesAnOpTheStoreWouldRefuse()
    {
        BibleProposalValidator.IsStorable(new BibleOp(BibleOpKind.AddSummary, "Tóm tắt.", "Ngôi <nhà>", null, null, null)).ShouldBeFalse();
        BibleProposalValidator.IsStorable(new BibleOp(BibleOpKind.AddRule, null, null, null, "  ", null)).ShouldBeFalse();
        BibleProposalValidator.IsStorable(new BibleOp(BibleOpKind.AddSummary, "Tóm tắt.", "Tập một", null, null, null)).ShouldBeTrue();
    }
}
