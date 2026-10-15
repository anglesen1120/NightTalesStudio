using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Keywords;

/// <summary>
/// QA of <c>RenderKeywordLinter</c> (03.2.2-T1): the D22 address of every segment shape, the recoverable
/// <c>;index:&lt;i&gt;</c> Evidence of every rule, the guarantee that no issue field ever carries the keyword or
/// the matched name — even for a keyword that contains the name — and the malformed list (a null element, a null
/// keyword, a blank keyword) that must never throw.
/// </summary>
public sealed class RenderKeywordLinterQaTests
{
    /// <summary>The acceptance fixture's corpus: "Vụ án Nguyễn Văn An" holds the pair (van, an) as a 3-token run.</summary>
    private static NameCorpus CaseCorpus => NameCorpus.From(["Vụ án Nguyễn Văn An"]);

    [Fact]
    public void Lint_HookParagraphAndEndingAddresses_ArePreserved()
    {
        var segments = new List<SegmentKeywords>
        {
            new("hook", ["portrait of van an at dawn"]),
            new("p:2", ["portrait of nguyen van at dawn"]),
            new("ending", ["portrait of van an at night"]),
        };

        var issues = RenderKeywordLinter.Lint(segments, CaseCorpus);

        issues.Select(issue => issue.Segment).ShouldBe(["hook", "p:2", "ending"]);
        issues.ShouldAllBe(issue => issue.RuleId == RenderKeywordLinter.NameDetected);
    }

    [Fact]
    public void Lint_EvidenceIndex_IsRecoverableForEveryRuleAndSegment()
    {
        var segments = new List<SegmentKeywords>
        {
            new("hook", ["old house"]),
            new("p:0", ["empty road at night", "portrait of van an at dawn"]),
            new("p:1", ["quiet street at dawn", "empty road at night", "old house"]),
        };

        var issues = RenderKeywordLinter.Lint(segments, CaseCorpus);

        issues.Count.ShouldBe(3);
        issues.Select(issue => issue.Segment).ShouldBe(["hook", "p:0", "p:1"]);
        issues.Select(issue => issue.Evidence).ShouldBe([
            $"{KeywordRules.TooFewWords};index:0",
            "run:3;index:1",
            $"{KeywordRules.TooFewWords};index:2",
        ]);

        foreach (var issue in issues)
        {
            RenderKeywordLinter.TryGetKeywordIndex(issue, out _).ShouldBeTrue();
        }
    }

    [Fact]
    public void Lint_HostileKeywordText_NeverReachesAnIssueField()
    {
        var segments = new List<SegmentKeywords>
        {
            new("p:9", ["portrait of nguyen van an at dawn", "Van An at the old mill"]),
        };

        var issues = RenderKeywordLinter.Lint(segments, CaseCorpus);

        issues.Count.ShouldBe(3, "one name issue for the first keyword, an R5 and a name issue for the second");

        foreach (var issue in issues)
        {
            foreach (var field in new[] { issue.RuleId, issue.Segment, issue.Evidence, issue.RepairDirection })
            {
                field.ShouldNotContain("nguyen", Case.Insensitive);
                field.ShouldNotContain("van", Case.Insensitive);
                field.ShouldNotContain("portrait", Case.Insensitive);
                field.ShouldNotContain("dawn", Case.Insensitive);
                field.ShouldNotContain("mill", Case.Insensitive);
            }
        }
    }

    [Fact]
    public void Lint_OneKeywordTwoRules_CarryTheSameIndex()
    {
        var segments = new List<SegmentKeywords> { new("p:2", ["Van An at the old mill"]) };

        var issues = RenderKeywordLinter.Lint(segments, CaseCorpus);

        issues.Select(issue => issue.RuleId).ShouldBe([
            StoryDraftValidator.KeywordInvalid,
            RenderKeywordLinter.NameDetected,
        ]);

        foreach (var issue in issues)
        {
            RenderKeywordLinter.TryGetKeywordIndex(issue, out var index).ShouldBeTrue();
            index.ShouldBe(0);
        }
    }

    [Fact]
    public void Lint_R2Failure_SkipsTheCorpusCheck()
    {
        // S4: a keyword whose only violation is the non-ASCII alphabet rule (R2) must be rewritten anyway, so
        // the name check is deferred to the re-lint of the rewritten keyword — it is not a DoS guard. No name
        // issue is added here, while the ASCII rewrite of the same keyword (same folded name) is caught.
        // "Vụ án Nguyễn Văn An" holds the run (van, an).
        var nonAscii = RenderKeywordLinter.Lint(
            new List<SegmentKeywords> { new("p:0", ["portrait of vãn an at dawn"]) },
            CaseCorpus);

        nonAscii.ShouldHaveSingleItem().RuleId.ShouldBe(StoryDraftValidator.KeywordInvalid);
        nonAscii.ShouldAllBe(issue => issue.RuleId != RenderKeywordLinter.NameDetected);

        var ascii = RenderKeywordLinter.Lint(
            new List<SegmentKeywords> { new("p:0", ["portrait of van an at dawn"]) },
            CaseCorpus);

        ascii.ShouldHaveSingleItem().RuleId.ShouldBe(RenderKeywordLinter.NameDetected);
    }

    [Fact]
    public void Lint_CombiningMarkFlood_FailsR2WithoutTheCorpusCheck()
    {
        // S4: 10,000 combining marks make R2 fail, so the name check is deferred to the re-lint of the
        // rewritten keyword and the lint yields only keyword_invalid. No timing assertion — the point is the
        // R2 deferral, not a DoS guard.
        var flooded = "portrait of van an at dawn" + new string('\u0301', 10_000);

        var issues = RenderKeywordLinter.Lint(
            new List<SegmentKeywords> { new("p:0", [flooded]) },
            CaseCorpus);

        issues.ShouldHaveSingleItem().RuleId.ShouldBe(StoryDraftValidator.KeywordInvalid);
        issues.ShouldAllBe(issue => issue.RuleId != RenderKeywordLinter.NameDetected);
    }

    [Fact]
    public void Lint_NullElementsNullKeywordsAndBlankKeywords_NeverThrow()
    {
        var segments = new List<SegmentKeywords>
        {
            null!,
            new(null!, ["portrait of van an at dawn"]),
            new("p:2", null!),
            new("p:3", [null!, string.Empty, "   ", "portrait of van an at dawn"]),
        };

        var issues = RenderKeywordLinter.Lint(segments, CaseCorpus);

        issues.Count.ShouldBe(5);
        issues.Select(issue => issue.Segment).ShouldBe(["", "p:3", "p:3", "p:3", "p:3"]);
        issues.Select(issue => issue.RuleId).ShouldBe([
            RenderKeywordLinter.NameDetected,
            StoryDraftValidator.KeywordInvalid,
            StoryDraftValidator.KeywordInvalid,
            StoryDraftValidator.KeywordInvalid,
            RenderKeywordLinter.NameDetected,
        ]);
        issues[1].Evidence.ShouldBe($"{KeywordRules.TooFewWords};index:0", "a null keyword fails R1");
        issues[3].Evidence.ShouldBe($"{KeywordRules.TooFewWords};index:2", "a blank keyword fails R1");
    }
}
