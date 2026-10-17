using System.Globalization;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
using NightTales.Domain.Stories.Quality;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Quality;

/// <summary>
/// The 03.2.2-T2 wiring of <c>RenderKeywordLinter</c> into <see cref="StoryQualityChecker"/> (03 README D10, D11,
/// CR-14; plan.md §4): a keyword that repeats a corpus run raises the blocking objective Q-KW-05 with the linter's
/// D22 address and <c>;index:&lt;i&gt;</c> evidence, an empty corpus raises nothing, the plan hints stay advisory with
/// segment <c>meta</c>, and no issue ever carries the keyword or the matched name.
/// </summary>
public sealed class Checker_RenderLinterIssuesTests
{
    private static readonly StoryQualityChecker Checker = new();

    /// <summary>Ten distinct, rule-valid keywords, one per scene slot of the fixtures below.</summary>
    private static readonly string[] Keywords =
    [
        "old lantern in fog",
        "empty road at night",
        "locked wooden door ajar",
        "dusty attic with trunk",
        "moonlit lake and boat",
        "quiet chapel at dawn",
        "broken bridge in rain",
        "frozen garden path",
        "silent staircase landing",
        "ancient orchard gate",
    ];

    private static readonly string[] Beats = ["setup", "twist", "reveal"];

    /// <summary>D11 runs over both spellings of the name in "Vụ án Nguyễn Văn An": the pair (van, an) is stored.</summary>
    private static NameCorpus CaseCorpus => NameCorpus.From(["Vụ án Nguyễn Văn An"]);

    [Fact]
    public void Checker_NameDetected_IsBlockingObjective()
    {
        // "portrait of van an at dawn" folds to portrait/of/van/an/at/dawn and repeats the corpus pair (van, an),
        // whose run — nguyen van an — is three tokens long.
        var draft = Draft(
            ["old lantern in fog"],
            ["dusty attic with trunk"],
            ["portrait of van an at dawn"],
            ["quiet chapel at dawn"],
            ["broken bridge in rain"]);

        var report = Checker.Check(draft, Context(CaseCorpus));

        var issue = report.Issues.Single(candidate => candidate.RuleId == StoryQualityChecker.KeywordNameDetected);
        issue.Severity.ShouldBe(QualitySeverity.Blocking);
        issue.Kind.ShouldBe(QualityKind.Objective);
        issue.Segment.ShouldBe("p:1");
        issue.MessageKey.ShouldBe("quality.keyword_name_detected");
        issue.MessageKey.ShouldBe(StoryQualityChecker.MessageKeys[StoryQualityChecker.KeywordNameDetected]);
        issue.Evidence.ShouldBe("run:3;index:0");
        issue.RepairDirection.ShouldNotBeNullOrWhiteSpace();
        report.HasBlocking.ShouldBeTrue();

        // The evidence stays the linter carrier, so the 03.2.2-T2 approve/render wiring can rebuild D10's
        // errors[].index from the very same issue the UI reads.
        RenderKeywordLinter.TryGetKeywordIndex(
            ValidationIssue.Blocking(StoryQualityChecker.KeywordNameDetected, issue.Segment, issue.Evidence, issue.RepairDirection ?? string.Empty),
            out var index).ShouldBeTrue();
        index.ShouldBe(0);
    }

    [Fact]
    public void Checker_EmptyCorpus_AddsNoNameIssue()
    {
        var draft = Draft(
            ["old lantern in fog"],
            ["dusty attic with trunk"],
            ["portrait of van an at dawn"],
            ["quiet chapel at dawn"],
            ["broken bridge in rain"]);

        var withoutCorpus = Checker.Check(draft, Context(NameCorpus.Empty));
        var withCorpus = Checker.Check(draft, Context(CaseCorpus));

        withoutCorpus.Issues.ShouldNotContain(issue => issue.RuleId == StoryQualityChecker.KeywordNameDetected);
        withoutCorpus.HasBlocking.ShouldBeFalse();

        // Control: the draft, not the corpus, is the variable — the same keywords do flag once a corpus is present.
        withCorpus.Issues.ShouldContain(issue => issue.RuleId == StoryQualityChecker.KeywordNameDetected);
    }

    [Fact]
    public void Checker_AddsTrimmedHint_AsAdvisory()
    {
        // Ten keywords, two per scene: the Short cap is six terms at the documented defaults, so the planner trims.
        var draft = Draft(
            [Keywords[0], Keywords[1]],
            [Keywords[2], Keywords[3]],
            [Keywords[4], Keywords[5]],
            [Keywords[6], Keywords[7]],
            [Keywords[8], Keywords[9]]);
        var plan = Plan(draft, scriptTokens: 200);

        plan.HintCodes.ShouldContain(RenderImagePlanner.ScenesTrimmed);
        plan.TrimmedCount.ShouldBeGreaterThan(0);

        var report = Checker.Check(draft, Context(NameCorpus.Empty, plan));

        var issue = report.Issues.Single(candidate => candidate.RuleId == StoryQualityChecker.ScenesTrimmed);
        issue.Severity.ShouldBe(QualitySeverity.Advisory);
        issue.Kind.ShouldBe(QualityKind.Objective);
        issue.Segment.ShouldBe("meta");
        issue.MessageKey.ShouldBe("quality.scenes_trimmed");
        issue.Measured.ShouldBe(plan.TrimmedCount);
        issue.Threshold.ShouldBeNull();
    }

    [Fact]
    public void Checker_AddsSparseHint_AsAdvisory()
    {
        // One keyword per scene and a narration long enough to need ten images: nothing is trimmed, but the five
        // planned terms fall under the 70 % sparse line (ceil(10 × 0.7) = 7).
        var draft = Draft(
            [Keywords[0]],
            [Keywords[1]],
            [Keywords[2]],
            [Keywords[3]],
            [Keywords[4]]);
        var plan = Plan(draft, scriptTokens: 400);

        plan.HintCodes.ShouldContain(RenderImagePlanner.ScenesSparse);
        plan.HintCodes.ShouldNotContain(RenderImagePlanner.ScenesTrimmed);

        var report = Checker.Check(draft, Context(NameCorpus.Empty, plan));

        var issue = report.Issues.Single(candidate => candidate.RuleId == StoryQualityChecker.ScenesSparse);
        issue.Severity.ShouldBe(QualitySeverity.Advisory);
        issue.Kind.ShouldBe(QualityKind.Objective);
        issue.Segment.ShouldBe("meta");
        issue.MessageKey.ShouldBe("quality.scenes_sparse");
        issue.Measured.ShouldBe(plan.Terms.Count);
        issue.Threshold.ShouldBe(plan.NeededImages);
    }

    [Fact]
    public void Checker_NameIssue_CarriesNoKeywordOrNameText()
    {
        var draft = Draft(
            ["old lantern in fog"],
            ["dusty attic with trunk"],
            ["portrait of van an at dawn"],
            ["quiet chapel at dawn"],
            ["broken bridge in rain"]);

        var report = Checker.Check(draft, Context(CaseCorpus));
        var issue = report.Issues.Single(candidate => candidate.RuleId == StoryQualityChecker.KeywordNameDetected);

        // 03 §8 and 02 D19: neither the keyword nor a token of the matched name may reach an issue field.
        foreach (var field in new[] { issue.RuleId, issue.Segment, issue.MessageKey, issue.Evidence, issue.RepairDirection ?? string.Empty })
        {
            field.ShouldNotContain("portrait", Case.Insensitive);
            field.ShouldNotContain("van", Case.Insensitive);
            field.ShouldNotContain("nguyen", Case.Insensitive);
            field.ShouldNotContain("nguyễn", Case.Insensitive);
        }

        foreach (var candidate in report.Issues)
        {
            candidate.Evidence.ShouldNotContain("portrait", Case.Insensitive);
            candidate.Evidence.ShouldNotContain("van", Case.Insensitive);
        }
    }

    [Fact]
    public void Checker_PartialDraftWithNullSegmentsAndNullKeywordLists_NeverThrows()
    {
        // An edited draft can arrive half-built (02.3.4-T1: the check never throws for a malformed draft): a null
        // paragraph element, a null hook/ending segment and keyword lists that are null rather than empty. The name
        // rule hands the linter an empty list for each of those and keeps linting the segment that does have
        // keywords, so a null list can neither fault the check nor turn the rule off for the rest of the draft.
        var draft = new StoryContentV1(
            "A quiet night",
            new StorySegmentV1(Prose(20, 1), null!, []),
            [
                null!,
                new StorySegmentV1(Prose(60, 1000), ["portrait of van an at dawn"], []),
                new StorySegmentV1(Prose(60, 1100), null!, []),
            ],
            new StorySegmentV1(Prose(20, 9000), null!, []),
            "A short story.",
            ["ghost"],
            [],
            new StoryStructureV1("sub", "setting", "opening", "twist", "first_person"));

        var report = Should.NotThrow(() => Checker.Check(draft, Context(CaseCorpus)));

        // The one segment whose keyword list is a list is still linted; the null ones contribute no issue and no
        // exception, and the address the surviving issue carries is the segment's own (p:1).
        var issue = report.Issues.Single(candidate => candidate.RuleId == StoryQualityChecker.KeywordNameDetected);
        issue.Segment.ShouldBe("p:1");
        issue.Severity.ShouldBe(QualitySeverity.Blocking);
    }

    [Fact]
    public void Checker_KeywordFailingTheAlphabetRule_IsNotNameFlagged()
    {
        // 03.2.2-T1 S4 and 03.2.2-T2's hand-over note: the corpus check is deferred for a keyword that already fails
        // R2, because such a keyword must be rewritten anyway (the validator refuses to save it). So a name run
        // spelled with Vietnamese diacritics raises no Q-KW-05 here — a reader must not see a name blocker for a
        // keyword the save path rejects with its own 02 issue.
        var draft = Draft(
            ["old lantern in fog"],
            ["dusty attic with trunk"],
            ["nguyễn văn an lúc bình minh"],
            ["quiet chapel at dawn"],
            ["broken bridge in rain"]);

        var report = Checker.Check(draft, Context(CaseCorpus));

        report.Issues.ShouldNotContain(issue => issue.RuleId == StoryQualityChecker.KeywordNameDetected);
    }

    // ---- fixtures ----

    /// <summary>
    /// A D3 draft with three body paragraphs, so five scenes in all; the keywords are given per scene in story order.
    /// Every text is generated prose of the Short band, so only the keyword rules can fire.
    /// </summary>
    /// <param name="hook">The hook's keywords.</param>
    /// <param name="first">The first paragraph's keywords (address <c>p:0</c>).</param>
    /// <param name="second">The second paragraph's keywords (address <c>p:1</c>).</param>
    /// <param name="third">The third paragraph's keywords (address <c>p:2</c>).</param>
    /// <param name="ending">The ending's keywords.</param>
    private static StoryContentV1 Draft(string[] hook, string[] first, string[] second, string[] third, string[] ending)
    {
        string[][] paragraphs = [first, second, third];
        var body = new List<StorySegmentV1>();
        for (var index = 0; index < paragraphs.Length; index++)
        {
            body.Add(new StorySegmentV1(
                Prose(60, 1000 + (index * 100)),
                paragraphs[index],
                [],
                Beats[index % Beats.Length]));
        }

        return new StoryContentV1(
            "A quiet night",
            new StorySegmentV1(Prose(20, 1), hook, []),
            body,
            new StorySegmentV1(Prose(20, 9000), ending, []),
            "A short story.",
            ["ghost"],
            [],
            new StoryStructureV1("sub", "setting", "opening", "twist", "first_person"));
    }

    /// <summary>The draft's keywords in story order, addressed the way the render path addresses them.</summary>
    private static List<SegmentKeywords> Segments(StoryContentV1 draft)
    {
        var segments = new List<SegmentKeywords> { new(SegmentAddress.Hook, draft.Hook.Keywords) };
        for (var index = 0; index < draft.Paragraphs.Count; index++)
        {
            segments.Add(new SegmentKeywords(SegmentAddress.Paragraph(index), draft.Paragraphs[index].Keywords));
        }

        segments.Add(new SegmentKeywords(SegmentAddress.Ending, draft.Ending.Keywords));
        return segments;
    }

    private static RenderImagePlan Plan(StoryContentV1 draft, int scriptTokens) =>
        RenderImagePlanner.Plan(new RenderImagePlanInput(Segments(draft), scriptTokens, Language.Vi, LengthTier.Short, ImagePlanOptions.Defaults));

    private static StoryCheckContext Context(NameCorpus names, RenderImagePlan? plan = null)
    {
        var rules = new StoryRules();
        return new StoryCheckContext(
            Topic.Ghost,
            LengthTier.Short,
            rules.Length,
            rules,
            0,
            [],
            false,
            null,
            SimilarityVerdict.Pass,
            null,
            false,
            plan,
            names);
    }

    private static string Prose(int tokens, int seed)
    {
        var sentences = new List<string>();
        for (var used = 0; used < tokens; used += 10)
        {
            sentences.Add(string.Join(' ', Enumerable.Range(seed + used, Math.Min(10, tokens - used)).Select(Token)) + ".");
        }

        return string.Join(' ', sentences);
    }

    private static string Token(int number) =>
        "w" + string.Concat(number.ToString(CultureInfo.InvariantCulture).Select(digit => (char)('a' + (digit - '0'))));
}
