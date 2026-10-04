using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Quality;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Quality;

public sealed class StoryQualityCheckerTests
{
    private static readonly string[] Keywords =
    [
        "old lantern in fog",
        "empty road at night",
        "locked wooden door ajar",
        "dusty attic with trunk",
        "moonlit lake and boat",
    ];

    private static readonly StoryQualityChecker Checker = new();

    [Fact]
    public void Check_CleanDraft_EmitsNothing()
    {
        var report = Checker.Check(Draft(), Context());

        report.Issues.ShouldBeEmpty();
        report.HasBlocking.ShouldBeFalse();
    }

    [Fact]
    public void Check_LengthFarOutOfRange_EmitsBlockingQLen01()
    {
        // 20 + 20 + 3 x 90 = 310 tokens; band 195-260, far bound 299.
        var report = Checker.Check(Draft(paragraphTokens: 90), Context());

        Only(report, StoryQualityChecker.LengthFarOutOfRange, QualitySeverity.Blocking, QualityKind.Objective, "meta");
    }

    [Fact]
    public void Check_LengthSlightlyOutOfRange_EmitsAdvisoryQLen02()
    {
        // 20 + 20 + 3 x 72 = 256 is inside; 3 x 77 = 271 is 4 % over.
        var report = Checker.Check(Draft(paragraphTokens: 77), Context());

        Only(report, StoryQualityChecker.LengthOutOfRange, QualitySeverity.Advisory, QualityKind.Objective, "meta");
    }

    [Fact]
    public void Check_LengthWithinTolerance_EmitsNothing()
    {
        Checker.Check(Draft(paragraphTokens: 70), Context()).Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Check_TwentyPercentOver_IsBlocking_AndEightPercentOver_IsAdvisory()
    {
        var max = Context().Length!.MaxTokens;

        // body tokens = 40 + 3 x p
        Checker.Check(Draft(paragraphTokens: ((int)(max * 1.20) - 40) / 3), Context()).Issues
            .Select(issue => issue.RuleId).ShouldBe([StoryQualityChecker.LengthFarOutOfRange]);
        Checker.Check(Draft(paragraphTokens: ((int)(max * 1.08) - 40) / 3), Context()).Issues
            .Select(issue => issue.RuleId).ShouldBe([StoryQualityChecker.LengthOutOfRange]);
    }

    [Fact]
    public void Check_SegmentCountOutOfRange_EmitsQSeg01()
    {
        // hook + 1 paragraph + ending = 3 segments (band 4-5); tokens kept inside the band.
        var report = Checker.Check(Draft(paragraphs: 1, paragraphTokens: 180), Context());

        Only(report, StoryQualityChecker.SegmentCountOutOfRange, QualitySeverity.Blocking, QualityKind.Objective, "meta");
    }

    [Fact]
    public void Check_EmptyHook_EmitsQHook01()
    {
        var draft = Draft(hookText: string.Empty, paragraphTokens: 73);

        var report = Checker.Check(draft, Context());

        Only(report, StoryQualityChecker.HookMissing, QualitySeverity.Blocking, QualityKind.Objective, "hook");
    }

    [Fact]
    public void Check_FourSentenceHook_EmitsQHook02()
    {
        var hook = string.Join(' ', Enumerable.Range(0, 4).Select(i => $"{Words(5, 900 + i)}."));
        var report = Checker.Check(Draft(hookText: hook, paragraphTokens: 73), Context());

        Only(report, StoryQualityChecker.HookTooLong, QualitySeverity.Advisory, QualityKind.Objective, "hook");
    }

    [Fact]
    public void Check_ZeroKeywordsOnParagraph_EmitsQKw01_ForThatSegment()
    {
        var report = Checker.Check(Draft(paragraphKeywords: (1, []), paragraphTokens: 70), Context());

        Only(report, StoryQualityChecker.KeywordCountPerSegment, QualitySeverity.Advisory, QualityKind.Objective, "p:1");
    }

    [Fact]
    public void Check_ThreeKeywordsOnParagraph_EmitsQKw01()
    {
        var three = new[] { "ancient stone bridge", "silent village square", "broken church bell" };
        var report = Checker.Check(Draft(paragraphKeywords: (2, three), paragraphTokens: 70), Context(tier: LengthTier.Standard));

        // The paragraph at index 2 is the third one: its address is p:2, and nothing else fires.
        Only(report, StoryQualityChecker.KeywordCountPerSegment, QualitySeverity.Advisory, QualityKind.Objective, "p:2");
    }

    [Fact]
    public void Check_KeywordTotalAboveCap_EmitsQKw02()
    {
        // 5 segments x 2 keywords = 10 > ceil(1 x 6) = 6; all distinct and valid.
        var report = Checker.Check(Draft(keywordsPerSegment: 2, paragraphTokens: 70), Context());

        Only(report, StoryQualityChecker.KeywordTotalAboveCap, QualitySeverity.Advisory, QualityKind.Objective, "meta");
        report.Issues[0].Measured.ShouldBe(10);
        report.Issues[0].Threshold.ShouldBe(6);
    }

    [Fact]
    public void Check_KeywordWithVietnameseText_EmitsQKw03()
    {
        var report = Checker.Check(Draft(paragraphKeywords: (0, ["cái đèn lồng cũ"]), paragraphTokens: 70), Context());

        Only(report, StoryQualityChecker.KeywordInvalid, QualitySeverity.Advisory, QualityKind.Objective, "p:0");
    }

    [Fact]
    public void Check_DuplicateKeywordAcrossSegments_EmitsQKw04()
    {
        var report = Checker.Check(Draft(paragraphKeywords: (0, [Keywords[0]]), paragraphTokens: 70), Context());

        Only(report, StoryQualityChecker.KeywordDuplicate, QualitySeverity.Advisory, QualityKind.Objective, "meta");
    }

    [Fact]
    public void Check_RepeatedSentence_EmitsQRep01()
    {
        var sentence = Words(13, 700) + ".";
        var report = Checker.Check(Draft(paragraphTexts: new[] { (0, sentence + " " + Words(40, 10)), (2, sentence + " " + Words(40, 200)) }, paragraphTokens: 70), Context());

        var issue = report.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe(StoryQualityChecker.RepeatedSentence);
        issue.Segment.ShouldBe("p:2");
        issue.Severity.ShouldBe(QualitySeverity.Advisory);
        issue.Kind.ShouldBe(QualityKind.Objective);
    }

    [Fact]
    public void Check_RepetitivePhrasing_EmitsQRep02()
    {
        // The same 20-token run in every paragraph, separated into short sentences: shingles repeat, no sentence is >= 12 tokens.
        var run = string.Join(' ', Enumerable.Range(0, 8).Select(i => Words(5, 40 + (i * 5)) + "."));
        var report = Checker.Check(Draft(paragraphTexts: new[] { (0, run + " " + Words(20, 2000)), (1, run + " " + Words(20, 3000)), (2, run + " " + Words(20, 4000)) }, paragraphTokens: 70), Context());

        var issue = report.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe(StoryQualityChecker.RepetitivePhrasing);
        issue.Severity.ShouldBe(QualitySeverity.Advisory);
        issue.Kind.ShouldBe(QualityKind.Objective);
        issue.Segment.ShouldStartWith("p:");
        report.Counters.InternalShingleShare.ShouldNotBeNull().ShouldBeGreaterThan(0.25);
    }

    [Fact]
    public void Check_TrueCrimeUnknownName_EmitsQName01_AdvisoryTaste()
    {
        var text = "Then Marcus Thornfield walked in. " + Words(40, 10);
        var report = Checker.Check(
            Draft(paragraphTexts: (0, text), paragraphTokens: 70),
            Context(topic: Topic.TrueCrime, factCount: 0, titles: ["Some Case File"]));

        var issue = report.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe(StoryQualityChecker.PossibleRealName);
        issue.Severity.ShouldBe(QualitySeverity.Advisory);
        issue.Kind.ShouldBe(QualityKind.Taste);
        issue.Segment.ShouldBe("p:0");
    }

    [Fact]
    public void Check_TrueCrimeNameInSourceTitle_EmitsNothing()
    {
        var text = "Then Marcus Thornfield walked in. " + Words(40, 10);
        var report = Checker.Check(
            Draft(paragraphTexts: (0, text), paragraphTokens: 70),
            Context(topic: Topic.TrueCrime, factCount: 0, titles: ["The Marcus Thornfield Case"]));

        report.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Check_Fiction_DoesNotEmitQName01()
    {
        var text = "Then Marcus Thornfield walked in. " + Words(40, 10);

        var report = Checker.Check(Draft(paragraphTexts: (0, text), paragraphTokens: 70), Context());

        report.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Check_FictionWithoutSources_EmitsNoSimilarityHints()
    {
        var report = Checker.Check(Draft(), Context(verdict: SimilarityVerdict.Block, score: 0.9));

        report.Issues.ShouldBeEmpty();
        report.Counters.SimilarityScore.ShouldBe(0.9);
    }

    [Fact]
    public void Check_SourcedFictionAboveBlock_EmitsQSim02()
    {
        var report = Checker.Check(Draft(), Context(hasUrlSources: true, verdict: SimilarityVerdict.Block, score: 0.2));

        Only(report, StoryQualityChecker.SimilarityBlocked, QualitySeverity.Blocking, QualityKind.Objective, "meta");
    }

    [Fact]
    public void Check_SimilarityAboveWarn_EmitsQSim01()
    {
        var report = Checker.Check(Draft(), Context(hasUrlSources: true, verdict: SimilarityVerdict.Warn, score: 0.05));

        Only(report, StoryQualityChecker.SimilarityWarn, QualitySeverity.Advisory, QualityKind.Objective, "meta");
    }

    [Fact]
    public void Check_NullScore_NeverWarns()
    {
        Checker.Check(Draft(), Context(hasUrlSources: true, verdict: SimilarityVerdict.Pass, score: null)).Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Check_TrueCrimeSimilarityAboveBlock_EmitsQSim02()
    {
        var report = Checker.Check(Draft(), Context(topic: Topic.TrueCrime, factCount: 0, verdict: SimilarityVerdict.Block, score: 0.3));

        Only(report, StoryQualityChecker.SimilarityBlocked, QualitySeverity.Blocking, QualityKind.Objective, "meta");
    }

    [Fact]
    public void Check_TrueCrimeNonSetupParagraphWithoutFactRef_EmitsQFact01()
    {
        // Paragraphs are setup, twist, reveal; with one fact in the story the last two cite nothing.
        var report = Checker.Check(Draft(), Context(topic: Topic.TrueCrime, factCount: 1));

        report.Issues.Select(issue => (issue.RuleId, issue.Segment))
            .ShouldBe([(StoryQualityChecker.FactRefsMissing, "p:1"), (StoryQualityChecker.FactRefsMissing, "p:2")]);
        report.Issues.ShouldAllBe(issue => issue.Severity == QualitySeverity.Advisory && issue.Kind == QualityKind.Objective);
    }

    [Fact]
    public void Check_TrueCrimeSetupParagraphWithoutFactRef_EmitsNothing()
    {
        var draft = Draft(factRefs: "f1");

        Checker.Check(draft, Context(topic: Topic.TrueCrime, factCount: 1)).Issues
            .ShouldNotContain(issue => issue.Segment == "p:0");
    }

    [Fact]
    public void Check_FictionNonSetupParagraphWithoutFactRef_EmitsNothing()
    {
        Checker.Check(Draft(), Context(factCount: 3)).Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Check_MetadataOverLimit_EmitsQMeta01()
    {
        var draft = Draft() with { Title = new string('t', 101) };

        var report = Checker.Check(draft, Context());

        Only(report, StoryQualityChecker.MetadataInvalid, QualitySeverity.Blocking, QualityKind.Objective, "meta");
    }

    [Fact]
    public void Check_MarkdownInParagraph_EmitsQMd01()
    {
        var report = Checker.Check(Draft(paragraphTexts: (1, "**bold** " + Words(40, 10)), paragraphTokens: 70), Context());

        Only(report, StoryQualityChecker.FormattingInvalid, QualitySeverity.Blocking, QualityKind.Objective, "p:1");
    }

    [Fact]
    public void Check_BlockingModerationCategory_EmitsQMod01()
    {
        var report = Checker.Check(Draft(), Context(moderation: ["hate/threatening", "harassment"]));

        Only(report, StoryQualityChecker.ModerationBlocked, QualitySeverity.Blocking, QualityKind.Objective, "meta");
    }

    [Fact]
    public void Check_AdvisoryModerationCategory_EmitsNothing()
    {
        Checker.Check(Draft(), Context(moderation: ["harassment"])).Issues.ShouldBeEmpty();
    }

    /// <summary>NTS-EDIT-L1: no verdict is not a clean verdict; the checker fails closed on the unavailable marker.</summary>
    [Fact]
    public void Check_ModerationUnavailable_EmitsBlockingQMod01()
    {
        var report = Checker.Check(Draft(), Context() with { ModerationUnavailable = true });

        Only(report, StoryQualityChecker.ModerationBlocked, QualitySeverity.Blocking, QualityKind.Objective, "meta");
        report.HasBlocking.ShouldBeTrue();
        report.Issues.Single().Evidence.ShouldBe("moderation unavailable");
    }

    [Fact]
    public void Check_ModerationUnavailableAndFlagged_EmitsOneQMod01NamingTheCategory()
    {
        var report = Checker.Check(Draft(), Context(moderation: ["hate/threatening"]) with { ModerationUnavailable = true });

        report.Issues.ShouldHaveSingleItem().Evidence.ShouldBe("hate/threatening");
    }

    [Fact]
    public void Check_ModerationAvailable_EmitsNoQMod01()
    {
        Checker.Check(Draft(), Context(moderation: [])).Issues.ShouldNotContain(issue => issue.RuleId == StoryQualityChecker.ModerationBlocked);
    }

    [Fact]
    public void Check_StaleModelReview_EmitsQReview01()
    {
        var report = Checker.Check(Draft(), Context(stale: true));

        Only(report, StoryQualityChecker.ModelReviewStale, QualitySeverity.Advisory, QualityKind.Taste, "meta");
    }

    [Fact]
    public void Check_MissingLengthTier_EmitsQTier01Only()
    {
        // A far-too-long draft would trip Q-LEN-01 and Q-KW-02, but without a tier neither is judged.
        var report = Checker.Check(Draft(paragraphTokens: 300, keywordsPerSegment: 2), Context(tier: null));

        Only(report, StoryQualityChecker.LengthTierMissing, QualitySeverity.Advisory, QualityKind.Objective, "meta");
    }

    [Fact]
    public void Check_UnknownLengthTarget_SkipsLengthAndSegmentRules()
    {
        var report = Checker.Check(Draft(paragraphTokens: 300), Context(length: false));

        report.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Check_EmptyDraft_EmitsHookSegmentAndLengthTogether_AndNeverThrows()
    {
        var empty = new StoryContentV1(
            string.Empty,
            new StorySegmentV1(string.Empty, [], []),
            [],
            new StorySegmentV1(string.Empty, [], []),
            string.Empty,
            [],
            [],
            new StoryStructureV1(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty));

        var ids = Checker.Check(empty, Context()).Issues.Select(issue => issue.RuleId).ToList();

        ids.ShouldContain(StoryQualityChecker.HookMissing);
        ids.ShouldContain(StoryQualityChecker.SegmentCountOutOfRange);
        ids.ShouldContain(StoryQualityChecker.LengthFarOutOfRange);
    }

    [Fact]
    public void Check_NullMembers_DoNotThrow()
    {
        var broken = Draft() with { Paragraphs = null!, Hook = null!, Ending = null! };

        Should.NotThrow(() => Checker.Check(broken, Context()));
    }

    [Fact]
    public void Check_PlanHints_EmitAdvisoryMetaHints()
    {
        var plan = new RenderImagePlan([], 10, 6, 6, 2, [RenderImagePlanner.ScenesTrimmed, RenderImagePlanner.ScenesSparse]);

        var report = Checker.Check(Draft(), Context(plan: plan));

        report.Issues.Select(issue => issue.RuleId).ShouldBe([StoryQualityChecker.ScenesTrimmed, StoryQualityChecker.ScenesSparse]);
        report.Issues.ShouldAllBe(issue => issue.Segment == "meta" && issue.Severity == QualitySeverity.Advisory);
    }

    [Fact]
    public void Check_IdenticalInput_ProducesIdenticalOrderedIssues()
    {
        var draft = Draft(paragraphTokens: 90, keywordsPerSegment: 2) with { Title = new string('t', 101) };
        var ctx = Context(stale: true, hasUrlSources: true, verdict: SimilarityVerdict.Warn, score: 0.05);

        var first = Checker.Check(draft, ctx).Issues;
        var second = new StoryQualityChecker().Check(draft, ctx).Issues;

        first.Count.ShouldBeGreaterThan(3);
        second.ShouldBe(first);
        first.Select(issue => issue.RuleId).ShouldBe(first.Select(issue => issue.RuleId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Check_ReportCarriesNoSourceTextOrUrls()
    {
        var statement = "https://example.com/secret-source " + new string('s', 400);
        var title = "Secret Source Title " + new string('x', 300);
        var text = "**" + new string('y', 500) + " http://example.org/a";
        var draft = Draft(paragraphTexts: (0, text), paragraphTokens: 70);
        var ctx = Context(topic: Topic.TrueCrime, factCount: 1, titles: [title], hasUrlSources: true, verdict: SimilarityVerdict.Block, score: 0.5);

        var report = Checker.Check(draft, ctx);

        report.Issues.ShouldNotBeEmpty();
        foreach (var issue in report.Issues)
        {
            issue.Evidence.Length.ShouldBeLessThanOrEqualTo(200);
            issue.Evidence.ShouldNotContain("Secret Source");
            issue.Evidence.ShouldNotContain("secret-source");
            (issue.RepairDirection ?? string.Empty).ShouldNotContain(statement[..20]);
        }
    }

    [Fact]
    public void Check_Counters_MatchDerivedValues()
    {
        var draft = Draft(paragraphTokens: 70);

        var report = Checker.Check(draft, Context());
        var derived = DerivedStoryFields.From(draft);

        report.Counters.Tokens.ShouldBe(derived.WordCount);
        report.Counters.Keywords.ShouldBe(derived.Keywords.Count);
        report.Counters.Paragraphs.ShouldBe(3);
        report.Counters.Segments.ShouldBe(5);
    }

    [Fact]
    public void MessageKeys_CoverEveryRuleId_WithUniqueQualityKeys()
    {
        StoryQualityChecker.MessageKeys.Values.ShouldAllBe(key => key.StartsWith("quality.", StringComparison.Ordinal));
        StoryQualityChecker.MessageKeys.Values.Distinct().Count().ShouldBe(StoryQualityChecker.MessageKeys.Count);
    }

    // ---- fixtures ----

    private static void Only(QualityReport report, string ruleId, QualitySeverity severity, QualityKind kind, string segment)
    {
        var issue = report.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe(ruleId);
        issue.Severity.ShouldBe(severity);
        issue.Kind.ShouldBe(kind);
        issue.Segment.ShouldBe(segment);
        issue.MessageKey.ShouldBe(StoryQualityChecker.MessageKeys[ruleId]);
    }

    private static string Words(int count, int seed) =>
        string.Join(' ', Enumerable.Range(seed, count).Select(n => "w" + string.Concat(n.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(d => (char)('a' + (d - '0'))))));

    private static string Prose(int tokens, int seed)
    {
        var sentences = new List<string>();
        for (var used = 0; used < tokens; used += 10)
        {
            sentences.Add(Words(Math.Min(10, tokens - used), seed + used) + ".");
        }

        return string.Join(' ', sentences);
    }

    private static StoryContentV1 Draft(
        int paragraphs = 3,
        int paragraphTokens = 60,
        int keywordsPerSegment = 1,
        string? hookText = null,
        (int Index, string[] Keywords)? paragraphKeywords = null,
        string? factRefs = null,
        params (int Index, string Text)[] paragraphTexts)
    {
        string[] beats = ["setup", "twist", "reveal"];
        var next = 0;
        string[] Pick(int count) => Enumerable.Range(0, count).Select(_ => Keywords[next++ % Keywords.Length] + (next > Keywords.Length ? " again" + new string('x', next) : string.Empty)).ToArray();

        var hook = new StorySegmentV1(hookText ?? Prose(20, 1), Pick(keywordsPerSegment), []);
        var body = new List<StorySegmentV1>();
        for (var index = 0; index < paragraphs; index++)
        {
            var text = paragraphTexts.Any(t => t.Index == index)
                ? paragraphTexts.First(t => t.Index == index).Text
                : Prose(paragraphTokens, 1000 + (index * 1000));
            var keywords = paragraphKeywords is { } pk && pk.Index == index ? pk.Keywords : Pick(keywordsPerSegment);
            body.Add(new StorySegmentV1(
                text,
                keywords,
                index == 0 && factRefs is not null ? [factRefs] : [],
                beats[index % beats.Length]));
        }

        var ending = new StorySegmentV1(Prose(20, 9000), Pick(keywordsPerSegment), []);
        return new StoryContentV1(
            "A quiet night",
            hook,
            body,
            ending,
            "A short story.",
            ["ghost"],
            [],
            new StoryStructureV1("sub", "setting", "opening", "twist", "first_person"));
    }

    private static StoryCheckContext Context(
        Topic topic = Topic.Ghost,
        LengthTier? tier = LengthTier.Short,
        bool length = true,
        int factCount = 0,
        IReadOnlyList<string>? titles = null,
        bool hasUrlSources = false,
        double? score = null,
        SimilarityVerdict verdict = SimilarityVerdict.Pass,
        IReadOnlyList<string>? moderation = null,
        bool stale = false,
        RenderImagePlan? plan = null)
    {
        var rules = new StoryRules();
        return new StoryCheckContext(
            topic,
            tier,
            length ? rules.Length : null,
            rules,
            factCount,
            titles ?? [],
            hasUrlSources,
            score,
            verdict,
            moderation,
            stale,
            plan);
    }
}
