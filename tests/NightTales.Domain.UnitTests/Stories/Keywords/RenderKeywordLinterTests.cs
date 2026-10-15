using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Keywords;

/// <summary>
/// <c>RenderKeywordLinter</c> (03 README D10, D11, F10, G4; 03.2.2-T1 Tests list): the 02 <c>KeywordRules</c> issues pass
/// through untouched, a name from the corpus adds the blocking objective <c>keyword_name_detected</c>, the D22
/// address travels with the issue and no keyword text does, and the issue order follows segments then keyword index.
/// </summary>
public sealed class RenderKeywordLinterTests
{
    /// <summary>The corpus of the acceptance fixture: "Vụ án Nguyễn Văn An" holds the pair (van, an).</summary>
    private static NameCorpus CaseCorpus => NameCorpus.From(["Vụ án Nguyễn Văn An"]);

    [Fact]
    public void Lint_KeywordRulesIssuesPassThrough()
    {
        // 02 CR-5: the 02 rules stay the same rule ids in the same one-error shape; the Evidence is the failing
        // rule id instead of the quoted keyword, because the render path carries no keyword text (03 §8).
        var issues = RenderKeywordLinter.Lint(Segments(("p:0", ["old house"])), NameCorpus.Empty);

        var issue = issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe(StoryDraftValidator.KeywordInvalid);
        issue.Severity.ShouldBe(IssueSeverity.Blocking);
        issue.Kind.ShouldBe(IssueKind.Objective);
        issue.Evidence.ShouldBe($"{KeywordRules.TooFewWords};index:0");
        issue.Segment.ShouldBe("p:0");
    }

    [Fact]
    public void Lint_NameDetected_BlockingObjective()
    {
        var issues = RenderKeywordLinter.Lint(Segments(("p:0", ["portrait of van an at dawn"])), CaseCorpus);

        var issue = issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe(RenderKeywordLinter.NameDetected);
        issue.Severity.ShouldBe(IssueSeverity.Blocking);
        issue.Kind.ShouldBe(IssueKind.Objective);
        issue.Evidence.ShouldBe("run:3;index:0");
    }

    [Fact]
    public void Lint_IssueCarriesSegmentAddressAndNoKeywordText()
    {
        var issues = RenderKeywordLinter.Lint(Segments(("p:2", ["portrait of nguyen van at dawn"])), CaseCorpus);

        var issue = issues.ShouldHaveSingleItem();
        issue.Segment.ShouldBe("p:2");
        issue.Evidence.ShouldBe("run:3;index:0");

        // No field of the issue may carry the keyword, a token of it, or the matched name (03 §8, 02 D19).
        foreach (var field in new[] { issue.RuleId, issue.Segment, issue.Evidence, issue.RepairDirection })
        {
            field.ShouldNotContain("nguyen", Case.Insensitive);
            field.ShouldNotContain("van", Case.Insensitive);
            field.ShouldNotContain("portrait", Case.Insensitive);
        }
    }

    [Fact]
    public void Lint_OrderFollowsSegments()
    {
        var segments = Segments(
            ("hook", ["old house"]),
            ("p:0", ["portrait of van an at dawn"]),
            ("p:1", ["pool of blood", "12 baker street"]),
            ("ending", ["empty road at night"]));

        var issues = RenderKeywordLinter.Lint(segments, CaseCorpus);

        issues.Select(issue => issue.Segment).ShouldBe(["hook", "p:0", "p:1", "p:1"]);
        issues.Select(issue => issue.RuleId).ShouldBe([
            StoryDraftValidator.KeywordInvalid,
            RenderKeywordLinter.NameDetected,
            StoryDraftValidator.KeywordInvalid,
            StoryDraftValidator.KeywordInvalid,
        ]);
        issues[2].Evidence.ShouldBe($"{KeywordRules.Gore};index:0", "the first keyword of p:1 is gore");
        issues[3].Evidence.ShouldBe($"{KeywordRules.RealPlace};index:1", "the second keyword of p:1 is an address");
    }

    [Fact]
    public void Lint_BothRulesFire_InvalidBeforeName()
    {
        // "Van An" opens with a capital after the first word: R5 fires, and the name rule fires on the pair.
        var issues = RenderKeywordLinter.Lint(Segments(("p:0", ["portrait of Van An at dawn"])), CaseCorpus);

        issues.Select(issue => issue.RuleId).ShouldBe([
            StoryDraftValidator.KeywordInvalid,
            RenderKeywordLinter.NameDetected,
        ]);
        issues[1].Evidence.ShouldBe("run:3;index:0");
    }

    [Fact]
    public void Lint_EmptyCorpus_OnlyKeywordRules()
    {
        // Fiction episodes and the flag-off rollback have an empty corpus (D11): only the 02 rules report.
        var issues = RenderKeywordLinter.Lint(Segments(("p:0", ["portrait of van an at dawn"])), NameCorpus.Empty);

        issues.ShouldBeEmpty();
    }

    [Fact]
    public void Lint_RunRulesForwarded_KnownPlaceFails()
    {
        // The run's own lists reach R5/R12 through the rules (02 D3), exactly as the validator passes them.
        var segments = Segments(("p:0", ["mountain town near da lat"]));
        var rules = new StoryRules { KnownPlaceNames = new HashSet<string> { "da lat" } };

        RenderKeywordLinter.Lint(segments, NameCorpus.Empty).ShouldBeEmpty();
        RenderKeywordLinter.Lint(segments, NameCorpus.Empty, rules)
            .ShouldHaveSingleItem()
            .RuleId.ShouldBe(StoryDraftValidator.KeywordInvalid);
    }

    [Fact]
    public void Lint_NullSegmentAndNullKeywords_Safe()
    {
        // A malformed list never throws and never invents an issue: a null keyword is the one case the 02 rules
        // report themselves (R1), and a null segment or keyword list is skipped.
        var segments = new List<SegmentKeywords>
        {
            null!,
            new("p:0", null!),
            new("p:1", [null!, "portrait of van an at dawn"]),
        };

        var issues = RenderKeywordLinter.Lint(segments, CaseCorpus);

        issues.Select(issue => issue.Segment).ShouldBe(["p:1", "p:1"]);
        issues.Select(issue => issue.RuleId).ShouldBe([
            StoryDraftValidator.KeywordInvalid,
            RenderKeywordLinter.NameDetected,
        ]);
        issues[0].Evidence.ShouldBe($"{KeywordRules.TooFewWords};index:0", "a null keyword fails R1");
    }

    [Fact]
    public void Lint_IssueEvidenceCarriesKeywordIndex()
    {
        // D10's errors[].index is the keyword index inside the segment; it rides as the final ;index:<i> token
        // of the Evidence for both issue shapes, so a segment with [ok, flagged] is recoverable.
        var nameIssues = RenderKeywordLinter.Lint(
            Segments(("p:0", ["empty road at night", "portrait of van an at dawn"])),
            CaseCorpus);
        var nameIssue = nameIssues.ShouldHaveSingleItem();
        nameIssue.RuleId.ShouldBe(RenderKeywordLinter.NameDetected);
        nameIssue.Evidence.ShouldBe("run:3;index:1");
        RenderKeywordLinter.TryGetKeywordIndex(nameIssue, out var nameIndex).ShouldBeTrue();
        nameIndex.ShouldBe(1);

        var invalidIssues = RenderKeywordLinter.Lint(
            Segments(("p:0", ["empty road at night", "old house"])),
            NameCorpus.Empty);
        var invalidIssue = invalidIssues.ShouldHaveSingleItem();
        invalidIssue.RuleId.ShouldBe(StoryDraftValidator.KeywordInvalid);
        invalidIssue.Evidence.ShouldBe($"{KeywordRules.TooFewWords};index:1");
        RenderKeywordLinter.TryGetKeywordIndex(invalidIssue, out var invalidIndex).ShouldBeTrue();
        invalidIndex.ShouldBe(1);

        // Both shapes stay within the D22 cap and keep their recoverable index token. (The evidence argument is
        // only ever a rule id or run:<n>, so the cap is never actually approached; this pins it regardless.)
        foreach (var issue in new[] { nameIssue, invalidIssue })
        {
            issue.Evidence.Length.ShouldBeLessThanOrEqualTo(ValidationIssue.MaxEvidenceChars);
            RenderKeywordLinter.TryGetKeywordIndex(issue, out _).ShouldBeTrue();
        }
    }

    [Fact]
    public void TryGetKeywordIndex_RoundTrips_AndRejectsMalformed()
    {
        // Round-trip through a real Lint issue.
        var issue = RenderKeywordLinter.Lint(Segments(("p:0", ["old house"])), NameCorpus.Empty)
            .ShouldHaveSingleItem();
        RenderKeywordLinter.TryGetKeywordIndex(issue, out var index).ShouldBeTrue();
        index.ShouldBe(0);

        // Anything that is not a trailing, invariant, non-negative int token is rejected and never throws.
        static ValidationIssue Issue(string evidence) =>
            ValidationIssue.Blocking("r", "p:0", evidence, "fix");

        RenderKeywordLinter.TryGetKeywordIndex(null!, out _).ShouldBeFalse();
        RenderKeywordLinter.TryGetKeywordIndex(Issue("run:3"), out _).ShouldBeFalse();
        RenderKeywordLinter.TryGetKeywordIndex(Issue("run:3;index:-1"), out _).ShouldBeFalse();
        RenderKeywordLinter.TryGetKeywordIndex(Issue("run:3;index:"), out _).ShouldBeFalse();
        RenderKeywordLinter.TryGetKeywordIndex(Issue("run:3;index:abc"), out _).ShouldBeFalse();
        RenderKeywordLinter.TryGetKeywordIndex(Issue("run:3;index:99999999999999999999"), out _).ShouldBeFalse();
        RenderKeywordLinter.TryGetKeywordIndex(Issue("run:3;index:1x"), out _).ShouldBeFalse();
        RenderKeywordLinter.TryGetKeywordIndex(Issue("run:3;index:1 "), out _).ShouldBeFalse();
        RenderKeywordLinter.TryGetKeywordIndex(Issue("run:3;index:1;index:2"), out var last).ShouldBeTrue();
        last.ShouldBe(2);
    }

    private static List<SegmentKeywords> Segments(params (string Segment, string[] Keywords)[] rows) =>
        [.. rows.Select(row => new SegmentKeywords(row.Segment, row.Keywords))];
}
