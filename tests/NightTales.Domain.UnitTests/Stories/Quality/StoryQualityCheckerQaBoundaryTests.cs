using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
using NightTales.Domain.Stories.Quality;
using NightTales.Domain.Usage;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Quality;

/// <summary>Independent QA of 02.3.4-T1: exact thresholds, vi/en text, Unicode, size, ordering, purity of output.</summary>
public sealed class StoryQualityCheckerQaBoundaryTests
{
    private static readonly StoryQualityChecker Checker = new();

    private static readonly string[] Adjectives = ["quiet", "broken", "rusty", "frozen", "hidden", "silent", "crooked", "ancient"];
    private static readonly string[] Nouns = ["bridge", "lantern", "staircase", "window", "garden", "cellar", "chapel", "orchard"];

    // ---- Q-LEN-01 / Q-LEN-02: exact token thresholds (short vi band 195-260, tolerance 0.15 => 165.75 .. 299) ----

    [Theory]
    [InlineData(165, "Q-LEN-01")]
    [InlineData(166, "Q-LEN-02")]
    [InlineData(194, "Q-LEN-02")]
    [InlineData(195, null)]
    [InlineData(260, null)]
    [InlineData(261, "Q-LEN-02")]
    [InlineData(299, "Q-LEN-02")]
    [InlineData(300, "Q-LEN-01")]
    public void Length_ExactTokenBoundaries(int tokens, string? expected)
    {
        var report = Checker.Check(Draft(tokens), Ctx());

        report.Counters.Tokens.ShouldBe(tokens);
        report.Issues.Select(i => i.RuleId).ShouldBe(expected is null ? [] : [expected]);
    }

    [Fact]
    public void Length_FarIssue_CarriesMeasuredAndBound()
    {
        var issue = Checker.Check(Draft(300), Ctx()).Issues.ShouldHaveSingleItem();

        issue.Measured.ShouldBe(300);
        issue.Threshold.ShouldBe(299m);
        issue.Segment.ShouldBe("meta");
    }

    [Theory]
    [InlineData(3, "Q-SEG-01")]
    [InlineData(4, null)]
    [InlineData(5, null)]
    [InlineData(6, "Q-SEG-01")]
    public void SegmentCount_ExactBoundaries(int segments, string? expected)
    {
        var report = Checker.Check(Draft(230, paragraphs: segments - 2), Ctx());

        report.Counters.Segments.ShouldBe(segments);
        report.Issues.Select(i => i.RuleId).ShouldBe(expected is null ? [] : [expected]);
    }

    [Fact]
    public void CustomTolerance_MovesTheFarBoundary()
    {
        var strict = new StoryQualityChecker(StoryQualityThresholds.Defaults with { LengthTolerance = 0 });

        strict.Check(Draft(261), Ctx()).Issues.ShouldHaveSingleItem().RuleId.ShouldBe("Q-LEN-01");
        strict.Check(Draft(260), Ctx()).Issues.ShouldBeEmpty();
    }

    // ---- Q-HOOK-01 / Q-HOOK-02 ----

    [Theory]
    [InlineData("", true)]
    [InlineData("Wait.", true)]
    [InlineData("Wait now.", true)]
    [InlineData("Night fell fast.", false)]
    public void Hook_UnderThreeTokens_IsTooShort(string hook, bool flagged)
    {
        var report = Checker.Check(Draft(230, hook: hook), Ctx());

        report.Issues.Select(i => i.RuleId).Contains("Q-HOOK-01").ShouldBe(flagged);
    }

    [Fact]
    public void Hook_Empty_IsReportedOnHookSegment()
    {
        var issue = Checker.Check(Draft(230, hook: string.Empty), Ctx()).Issues.Single(i => i.RuleId == "Q-HOOK-01");

        issue.Segment.ShouldBe("hook");
        issue.Severity.ShouldBe(QualitySeverity.Blocking);
    }

    [Fact]
    public void Hook_ThreeSentences_Ok_FourSentences_Hint()
    {
        Checker.Check(Draft(230, hook: "Night fell. Dogs howled. Nobody slept."), Ctx())
            .Issues.Select(i => i.RuleId).ShouldNotContain("Q-HOOK-02");
        Checker.Check(Draft(230, hook: "Night fell. Dogs howled. Nobody slept. Then it came."), Ctx())
            .Issues.Select(i => i.RuleId).ShouldContain("Q-HOOK-02");
    }

    [Fact]
    public void Hook_VietnameseTerminators_SplitSentences()
    {
        // ! ? … each end a sentence: four sentences; a run of dots is one terminator.
        var viewed = Checker.Check(Draft(230, hook: "Đêm xuống rồi! Ai đang gõ cửa? Không ai trả lời… Chỉ có gió"), Ctx());
        viewed.Issues.Select(i => i.RuleId).ShouldContain("Q-HOOK-02");

        var ellipsisRun = Checker.Check(Draft(230, hook: "Đêm xuống rồi... Ai đang gõ cửa... Không ai trả lời"), Ctx());
        ellipsisRun.Issues.Select(i => i.RuleId).ShouldNotContain("Q-HOOK-02");
    }

    [Fact]
    public void Hook_NewlinesSeparateSentences()
    {
        var lines = Checker.Check(Draft(230, hook: "Đêm xuống rồi\nAi đang gõ cửa\nKhông ai trả lời\nChỉ có gió"), Ctx());
        lines.Issues.Select(i => i.RuleId).ShouldContain("Q-HOOK-02");
    }

    // ---- Q-KW-01 .. Q-KW-04 ----

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void KeywordsPerSegment_ExactBoundaries(int count, bool flagged)
    {
        var report = Checker.Check(Draft(230, paragraphKeywordCounts: [1, count, 1]), Ctx(tier: LengthTier.Standard, length: true));

        var kw01 = report.Issues.Where(i => i.RuleId == "Q-KW-01").ToList();
        kw01.Select(i => i.Segment).ShouldBe(flagged ? ["p:1"] : []);
    }

    [Theory]
    [InlineData(LengthTier.Short, 6, false)]
    [InlineData(LengthTier.Short, 7, true)]
    [InlineData(LengthTier.Standard, 48, false)]
    [InlineData(LengthTier.Standard, 49, true)]
    [InlineData(LengthTier.Long, 90, false)]
    [InlineData(LengthTier.Long, 91, true)]
    public void KeywordTotal_ExactCap_CeilDTimesK(LengthTier tier, int total, bool flagged)
    {
        // hook 1 + ending 1 keyword; paragraphs share the rest, 2 each, the last one takes the remainder.
        var rest = total - 2;
        var counts = Enumerable.Range(0, (rest + 1) / 2).Select(i => Math.Min(2, rest - (i * 2))).ToArray();
        var draft = RawDraft(counts, 1, 1);
        var actual = draft.Hook.Keywords.Count + draft.Ending.Keywords.Count + draft.Paragraphs.Sum(p => p.Keywords.Count);
        actual.ShouldBe(total);

        var report = Checker.Check(draft, Ctx(tier: tier, length: false));

        report.Issues.Select(i => i.RuleId).Contains("Q-KW-02").ShouldBe(flagged);
        report.Counters.Keywords.ShouldBe(total);
    }

    [Fact]
    public void KeywordDuplicate_IsCaseAndNormalisationInsensitive_AndReportedOncePerKeyword()
    {
        var nfc = "cái đèn lồng cũ";
        var nfd = nfc.Normalize(NormalizationForm.FormD);
        var draft = RawDraft([1, 1, 1], 1, 1, hookKeywords: ["Old Lantern In Fog"], extra: (0, ["old lantern in fog"]), extra2: (1, ["OLD LANTERN IN FOG"]));

        var ids = Checker.Check(draft, Ctx(length: false)).Issues.Where(i => i.RuleId == "Q-KW-04").ToList();

        ids.ShouldHaveSingleItem().Segment.ShouldBe("meta");

        var viDraft = RawDraft([1, 1, 1], 1, 1, hookKeywords: [nfc], extra: (0, [nfd]));
        Checker.Check(viDraft, Ctx(length: false)).Issues.Count(i => i.RuleId == "Q-KW-04").ShouldBe(1);
    }

    [Fact]
    public void KeywordDuplicate_WithinOneSegment_IsNotQKw04_ButAcrossSegmentsIs()
    {
        var same = RawDraft([1, 1, 1], 1, 1, hookKeywords: ["old lantern in fog", "old lantern in fog"]);
        Checker.Check(same, Ctx(length: false)).Issues.ShouldNotContain(i => i.RuleId == "Q-KW-04");

        var across = RawDraft([1, 1, 1], 1, 1, hookKeywords: ["old lantern in fog"], extra: (0, ["old lantern in fog"]));
        Checker.Check(across, Ctx(length: false)).Issues.Count(i => i.RuleId == "Q-KW-04").ShouldBe(1);
    }

    [Fact]
    public void KeywordInvalid_TooShortAndTooLong_AndNoPersonName()
    {
        var twoWords = RawDraft([1, 1, 1], 1, 1, hookKeywords: ["old lantern"]);
        var thirteen = RawDraft([1, 1, 1], 1, 1, hookKeywords: [string.Join(' ', Enumerable.Repeat("fog", 13))]);
        var twelve = RawDraft([1, 1, 1], 1, 1, hookKeywords: [string.Join(' ', Enumerable.Repeat("fog", 12))]);
        var three = RawDraft([1, 1, 1], 1, 1, hookKeywords: ["old lantern fog"]);

        Checker.Check(twoWords, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldContain("Q-KW-03");
        Checker.Check(thirteen, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldContain("Q-KW-03");
        Checker.Check(twelve, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldNotContain("Q-KW-03");
        Checker.Check(three, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldNotContain("Q-KW-03");
    }

    // ---- Q-REP-01 ----

    [Fact]
    public void RepeatedSentence_ThresholdIsTwelveTokens_AndIgnoresCaseAndPunctuation()
    {
        var twelve = "one two three four five six seven eight nine ten eleven twelve";
        var eleven = "one two three four five six seven eight nine ten eleven";

        RepeatDraft(twelve + ".", "ONE, two three four five six seven eight nine ten eleven TWELVE!")
            .Issues.Count(i => i.RuleId == "Q-REP-01").ShouldBe(1);
        RepeatDraft(eleven + ".", eleven + ".")
            .Issues.Count(i => i.RuleId == "Q-REP-01").ShouldBe(0);
    }

    [Fact]
    public void RepeatedSentence_VietnameseNfcVersusNfd_IsStillARepeat_AndIsReportedOnTheLaterSegment()
    {
        var sentence = "Đêm đó trời mưa rất to và gió thổi mạnh qua những cánh đồng lúa chín";
        var report = RepeatDraft(sentence + ".", sentence.Normalize(NormalizationForm.FormD) + ".");

        var issue = report.Issues.Single(i => i.RuleId == "Q-REP-01");
        issue.Segment.ShouldBe("p:1");
    }

    // ---- Q-REP-02 / counters ----

    [Fact]
    public void ShingleShare_IsNullForAStoryWithNoShingle_AndNeverThrows()
    {
        var tiny = RawDraft([1, 1, 1], 1, 1, text: "a b c") with
        {
            Hook = new StorySegmentV1("Night fell fast.", Kws(1), []),
            Ending = new StorySegmentV1("The end now.", Kws(1), []),
        };

        var report = Checker.Check(tiny, Ctx(length: false));

        report.Counters.InternalShingleShare.ShouldBeNull();
        report.Issues.Select(i => i.RuleId).ShouldNotContain("Q-REP-02");
    }

    [Fact]
    public void RepetitivePhrasing_FullyRepeatedBody_ExceedsDefaultShare()
    {
        var phrase = string.Join(' ', Enumerable.Range(0, 20).Select(i => "tok" + Letters(i)));
        var draft = RawDraft([1, 1, 1], 1, 1, text: phrase + " " + phrase + " " + phrase);

        var report = Checker.Check(draft, Ctx(length: false));

        report.Issues.Select(i => i.RuleId).ShouldContain("Q-REP-02");
        report.Counters.InternalShingleShare.ShouldNotBeNull().ShouldBeGreaterThan(0.25);
    }

    // ---- true crime / fiction ----

    [Fact]
    public void Fiction_NeverEmitsNameOrFact_EvenWithFactsAndCapitalisedPairs()
    {
        var draft = RawDraft([1, 1, 1], 1, 1, text: "John Smith met Mary Jones near Little Rock yesterday evening", beat: "twist");

        var report = Checker.Check(draft, Ctx(length: false, topic: Topic.Ghost, factCount: 5));

        report.Issues.Select(i => i.RuleId).ShouldNotContain("Q-NAME-01");
        report.Issues.Select(i => i.RuleId).ShouldNotContain("Q-FACT-01");
    }

    [Fact]
    public void TrueCrime_NameMatchingTitle_IsExplained_CaseAndNormalisationInsensitive()
    {
        var draft = RawDraft([1, 1, 1], 1, 1, text: "Then Nguyễn Văn walked home alone in the dark", beat: "setup");
        var nfdTitle = "VỤ ÁN NGUYỄN VĂN".Normalize(NormalizationForm.FormD);

        var withTitle = Checker.Check(draft, Ctx(length: false, topic: Topic.TrueCrime, titles: [nfdTitle]));
        var without = Checker.Check(draft, Ctx(length: false, topic: Topic.TrueCrime));

        withTitle.Issues.Select(i => i.RuleId).ShouldNotContain("Q-NAME-01");
        without.Issues.Select(i => i.RuleId).ShouldContain("Q-NAME-01");
    }

    [Fact]
    public void TrueCrime_NameHeuristicSwitchOff_SilencesQName01()
    {
        var off = new StoryQualityChecker(StoryQualityThresholds.Defaults with { NameHeuristic = false });
        var draft = RawDraft([1, 1, 1], 1, 1, text: "Then John Smith walked home alone in the dark", beat: "setup");

        off.Check(draft, Ctx(length: false, topic: Topic.TrueCrime)).Issues.Select(i => i.RuleId).ShouldNotContain("Q-NAME-01");
    }

    [Fact]
    public void TrueCrime_NameInNfdAndNfc_ProducesTheSameHints()
    {
        var nfc = "Then Nguyễn Văn walked home alone in the dark";
        var a = Checker.Check(RawDraft([1, 1, 1], 1, 1, text: nfc, beat: "setup"), Ctx(length: false, topic: Topic.TrueCrime));
        var b = Checker.Check(RawDraft([1, 1, 1], 1, 1, text: nfc.Normalize(NormalizationForm.FormD), beat: "setup"), Ctx(length: false, topic: Topic.TrueCrime));

        b.Issues.Select(i => (i.RuleId, i.Segment)).ShouldBe(a.Issues.Select(i => (i.RuleId, i.Segment)));
    }

    [Fact]
    public void FactRefs_OnlyParagraphs_OnlyWithFacts_NullBeatCounts_AsNotSetup()
    {
        var withNullBeat = RawDraft([1, 1, 1], 1, 1, text: null, beat: null);
        var withSetup = RawDraft([1, 1, 1], 1, 1, text: null, beat: "setup");

        Checker.Check(withNullBeat, Ctx(length: false, topic: Topic.TrueCrime, factCount: 1)).Issues
            .Where(i => i.RuleId == "Q-FACT-01").Select(i => i.Segment).ShouldBe(["p:0", "p:1", "p:2"]);
        Checker.Check(withNullBeat, Ctx(length: false, topic: Topic.TrueCrime, factCount: 0)).Issues
            .Select(i => i.RuleId).ShouldNotContain("Q-FACT-01");
        Checker.Check(withSetup, Ctx(length: false, topic: Topic.TrueCrime, factCount: 1)).Issues
            .Select(i => i.RuleId).ShouldNotContain("Q-FACT-01");
    }

    // ---- context-driven rules ----

    [Theory]
    [InlineData("sexual/minors")]
    [InlineData("SEXUAL/MINORS")]
    [InlineData("self-harm/instructions")]
    [InlineData("illicit/violent")]
    [InlineData("hate/threatening")]
    public void Moderation_EachD12BlockingCategory_Blocks(string category)
    {
        var issue = Checker.Check(Draft(230), Ctx(moderation: [category])).Issues.ShouldHaveSingleItem();

        issue.RuleId.ShouldBe("Q-MOD-01");
        issue.Severity.ShouldBe(QualitySeverity.Blocking);
        issue.Segment.ShouldBe("meta");
    }

    [Theory]
    [InlineData("harassment")]
    [InlineData("violence")]
    [InlineData("sexual")]
    [InlineData("hate")]
    [InlineData("self-harm")]
    public void Moderation_NonBlockingCategories_Advisory_EmitNothing(string category)
    {
        Checker.Check(Draft(230), Ctx(moderation: [category])).Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Moderation_MixedCategories_ListsOnlyTheBlockingOnes()
    {
        var issue = Checker.Check(Draft(230), Ctx(moderation: ["harassment", "hate/threatening"])).Issues.ShouldHaveSingleItem();

        issue.Evidence.ShouldBe("hate/threatening");
    }

    [Fact]
    public void Similarity_TrueCrimeWithoutUrlSources_FollowsTheVerdict_FictionNeverWithoutUrls()
    {
        Checker.Check(Draft(230), Ctx(topic: Topic.Ghost, verdict: SimilarityVerdict.Block, score: 0.9)).Issues.ShouldBeEmpty();
        Checker.Check(Draft(230), Ctx(topic: Topic.Ghost, verdict: SimilarityVerdict.Warn, score: 0.9)).Issues.ShouldBeEmpty();
        Checker.Check(Draft(230), Ctx(topic: Topic.Ghost, hasUrlSources: true, verdict: SimilarityVerdict.Warn, score: 0.1))
            .Issues.ShouldHaveSingleItem().RuleId.ShouldBe("Q-SIM-01");
        Checker.Check(Draft(230), Ctx(topic: Topic.Ghost, hasUrlSources: true, verdict: SimilarityVerdict.Block, score: 0.2))
            .Issues.ShouldHaveSingleItem().RuleId.ShouldBe("Q-SIM-02");
    }

    [Fact]
    public void Similarity_PassVerdictWithHighScore_NeverEmits_CheckerDoesNotClassify()
    {
        Checker.Check(Draft(230), Ctx(hasUrlSources: true, verdict: SimilarityVerdict.Pass, score: 0.99)).Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Similarity_BlockVerdictWithNullScore_DoesNotThrow()
    {
        var issue = Checker.Check(Draft(230), Ctx(hasUrlSources: true, verdict: SimilarityVerdict.Block, score: null)).Issues.ShouldHaveSingleItem();

        issue.Measured.ShouldBeNull();
    }

    [Fact]
    public void TierMissing_EvenWithATarget_EmitsOnlyQTier01_AndNoLengthOrKeywordCap()
    {
        var oversized = Draft(900, keywordsTotalOverCap: true);

        var report = Checker.Check(oversized, Ctx(tier: null, length: true));

        report.Issues.Select(i => i.RuleId).ShouldBe(["Q-TIER-01"]);
    }

    // ---- ordering, determinism, purity ----

    [Fact]
    public void Issues_AreOrderedByRuleIdOrdinalThenNumericSegmentPosition()
    {
        // 12 paragraphs without keywords: p:10 and p:11 must come after p:9 (numeric, not string order).
        var counts = Enumerable.Repeat(0, 12).ToArray();
        var report = Checker.Check(RawDraft(counts, 1, 1), Ctx(length: false));

        report.Issues.Where(i => i.RuleId == "Q-KW-01").Select(i => i.Segment)
            .ShouldBe(Enumerable.Range(0, 12).Select(i => $"p:{i}"));

        var all = report.Issues.Select(i => i.RuleId).ToList();
        all.ShouldBe(all.OrderBy(id => id, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Check_DoesNotMutateTheDraft_AndIsRepeatableOnOneInstance()
    {
        var draft = Draft(310, keywordsTotalOverCap: true);
        var before = JsonSerializer.Serialize(draft);

        var first = Checker.Check(draft, Ctx());
        var second = Checker.Check(draft, Ctx());

        JsonSerializer.Serialize(draft).ShouldBe(before);
        JsonSerializer.Serialize(second.Issues).ShouldBe(JsonSerializer.Serialize(first.Issues));
        JsonSerializer.Serialize(second.Counters).ShouldBe(JsonSerializer.Serialize(first.Counters));
    }

    [Fact]
    public void Evidence_NeverExceeds200_ForOversizedInputs()
    {
        var huge = new string('k', 5000);
        var draft = RawDraft([1, 1, 1], 1, 1, hookKeywords: [huge], text: huge) with { Title = huge, Description = huge };

        var report = Checker.Check(draft, Ctx(length: false));

        report.Issues.ShouldNotBeEmpty();
        report.Issues.ShouldAllBe(i => i.Evidence.Length <= 200);
    }

    [Fact]
    public void Evidence_RepeatedSentenceCutAtASurrogatePair_StaysWellFormed()
    {
        // 12 short tokens, then a token whose emoji straddles index 199/200 of the sentence.
        var head = string.Join(' ', Enumerable.Range(0, 12).Select(i => "w" + Letters(i)));
        var tail = new string('z', 199 - head.Length - 1) + "😀😀😀";
        var sentence = head + " " + tail + ".";

        var report = RepeatDraft(sentence, sentence);

        var issue = report.Issues.Single(i => i.RuleId == "Q-REP-01");
        IsWellFormed(issue.Evidence).ShouldBeTrue();
    }

    [Fact]
    public void Evidence_KeywordInvalidCutAtASurrogatePair_StaysWellFormed()
    {
        var keyword = new string('a', 198) + "😀" + " tail";
        var draft = RawDraft([1, 1, 1], 1, 1, hookKeywords: [keyword]);

        var report = Checker.Check(draft, Ctx(length: false));

        foreach (var issue in report.Issues)
        {
            IsWellFormed(issue.Evidence).ShouldBeTrue($"{issue.RuleId} evidence is not well-formed UTF-16");
        }
    }

    [Fact]
    public void Evidence_FormattingIssue_DoesNotEchoTheUrl()
    {
        var draft = RawDraft([1, 1, 1], 1, 1, text: "see https://example.com/secret-source for details and more words here");

        var issues = Checker.Check(draft, Ctx(length: false)).Issues.Where(i => i.RuleId == "Q-MD-01").ToList();

        issues.ShouldNotBeEmpty();
        issues.ShouldAllBe(i => !i.Evidence.Contains("http", StringComparison.Ordinal));
    }

    [Fact]
    public void Markdown_BracketsListMarkersAndUrls_AllTripQMd01_OnTheirSegment()
    {
        foreach (var text in new[] { "**bold** words here", "[link] words here", "- item one here", "plain https://x.example/a words" })
        {
            var draft = RawDraft([1, 1, 1], 1, 1, text: text);
            var md = Checker.Check(draft, Ctx(length: false)).Issues.Where(i => i.RuleId == "Q-MD-01").ToList();

            md.ShouldNotBeEmpty(text);
            md.ShouldAllBe(i => i.Severity == QualitySeverity.Blocking && i.MessageKey == "quality.formatting_invalid");
        }
    }

    [Fact]
    public void Metadata_ExactLimits_Title100Ok_101Blocks_AngleBracketBlocks()
    {
        var ok = RawDraft([1, 1, 1], 1, 1) with { Title = new string('t', 100) };
        var bad = RawDraft([1, 1, 1], 1, 1) with { Title = new string('t', 101) };
        var angle = RawDraft([1, 1, 1], 1, 1) with { Title = "a<b" };
        var desc = RawDraft([1, 1, 1], 1, 1) with { Description = new string('d', 1001) };
        var descOk = RawDraft([1, 1, 1], 1, 1) with { Description = new string('d', 1000) };

        Checker.Check(ok, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldNotContain("Q-META-01");
        Checker.Check(bad, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldContain("Q-META-01");
        Checker.Check(angle, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldContain("Q-META-01");
        Checker.Check(desc, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldContain("Q-META-01");
        Checker.Check(descOk, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldNotContain("Q-META-01");
    }

    [Fact]
    public void Tags_ExactLimits_FifteenTagsOk_SixteenBlocks_ThirtyCharOk_ThirtyOneBlocks()
    {
        var baseDraft = RawDraft([1, 1, 1], 1, 1);
        var fifteen = baseDraft with { Tags = Enumerable.Range(0, 15).Select(i => "tag" + Letters(i)).ToList() };
        var sixteen = baseDraft with { Tags = Enumerable.Range(0, 16).Select(i => "tag" + Letters(i)).ToList() };
        var t30 = baseDraft with { Tags = [new string('a', 30)] };
        var t31 = baseDraft with { Tags = [new string('a', 31)] };

        Checker.Check(fifteen, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldNotContain("Q-META-01");
        Checker.Check(sixteen, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldContain("Q-META-01");
        Checker.Check(t30, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldNotContain("Q-META-01");
        Checker.Check(t31, Ctx(length: false)).Issues.Select(i => i.RuleId).ShouldContain("Q-META-01");
    }

    [Fact]
    public void VeryLongStory_IsLinear_AndNeverThrows()
    {
        var counts = Enumerable.Repeat(2, 600).ToArray();
        var draft = RawDraft(counts, 2, 2, text: Prose(100, 1), uniqueProse: true);

        var watch = Stopwatch.StartNew();
        var report = Checker.Check(draft, Ctx(length: false));
        watch.Stop();

        report.Counters.Tokens.ShouldBeGreaterThan(60_000);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void SingleSegmentAndEmptyParagraphLists_NeverThrow()
    {
        var empty = RawDraft([], 0, 0);

        var report = Checker.Check(empty, Ctx());

        report.Counters.Paragraphs.ShouldBe(0);
        report.Counters.Segments.ShouldBe(2);
        report.Issues.Select(i => i.RuleId).ShouldContain("Q-SEG-01");
    }

    [Fact]
    public void EnglishLanguageRules_UseTheEnglishBand()
    {
        var rules = new StoryRules { Language = NightTales.Domain.Common.Language.En };
        var length = NightTales.Domain.Stories.Length.LengthTargetResolver.Resolve(
            LengthTier.Short, NightTales.Domain.Common.Language.En, PriceLimits.D4Defaults);
        var ctx = Ctx() with { Rules = rules, Length = length };

        // en short band 150-200 tokens: 200 is in, 201 advisory, 230 advisory (<= 230), 231 blocking.
        Checker.Check(Draft(200), ctx).Issues.ShouldBeEmpty();
        Checker.Check(Draft(201), ctx).Issues.ShouldHaveSingleItem().RuleId.ShouldBe("Q-LEN-02");
        Checker.Check(Draft(230), ctx).Issues.ShouldHaveSingleItem().RuleId.ShouldBe("Q-LEN-02");
        Checker.Check(Draft(231), ctx).Issues.ShouldHaveSingleItem().RuleId.ShouldBe("Q-LEN-01");
    }

    [Fact]
    public void QualityReport_HasBlocking_TracksSeverity()
    {
        Checker.Check(Draft(230), Ctx(stale: true)).HasBlocking.ShouldBeFalse();
        Checker.Check(Draft(230), Ctx(moderation: ["illicit/violent"])).HasBlocking.ShouldBeTrue();
    }

    // ---- fixtures ----

    private static bool IsWellFormed(string value)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetBytes(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static string Letters(int n) =>
        string.Concat(n.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(d => (char)('a' + (d - '0'))));

    private static string Words(int count, int seed) => string.Join(' ', Enumerable.Range(seed, count).Select(n => "w" + Letters(n)));

    private static string Prose(int tokens, int seed)
    {
        var sentences = new List<string>();
        for (var used = 0; used < tokens; used += 10)
        {
            sentences.Add(Words(Math.Min(10, tokens - used), seed + used) + ".");
        }

        return string.Join(' ', sentences);
    }

    private static string Keyword(int i) => $"{Adjectives[i % 8]} {Nouns[(i / 8) % 8]} at dusk {Letters(i / 64)}";

    private static int _kw;

    private static string[] Kws(int count) => Enumerable.Range(0, count).Select(_ => Keyword(Interlocked.Increment(ref _kw))).ToArray();

    /// <summary>Hook 20 + ending 20 tokens + 3 paragraphs summing to <paramref name="total"/>; one distinct keyword per segment.</summary>
    private static StoryContentV1 Draft(
        int total,
        int paragraphs = 3,
        string? hook = null,
        int[]? paragraphKeywordCounts = null,
        bool keywordsTotalOverCap = false)
    {
        var hookText = hook ?? Prose(20, 1);
        var hookTokens = StoryText.CountTokens(hookText);
        var body = total - hookTokens - 20;
        var per = body / Math.Max(paragraphs, 1);
        var extra = body - (per * Math.Max(paragraphs, 1));
        var segments = new List<StorySegmentV1>();
        for (var i = 0; i < paragraphs; i++)
        {
            var n = per + (i == 0 ? extra : 0);
            var count = paragraphKeywordCounts is { } c ? c[i] : (keywordsTotalOverCap ? 2 : 1);
            segments.Add(new StorySegmentV1(Prose(n, 1000 + (i * 1000)), Kws(count), [], "setup"));
        }

        return new StoryContentV1(
            "A quiet night",
            new StorySegmentV1(hookText, Kws(keywordsTotalOverCap ? 2 : 1), []),
            segments,
            new StorySegmentV1(Prose(20, 9000), Kws(keywordsTotalOverCap ? 2 : 1), []),
            "A short story.",
            ["ghost"],
            [],
            new StoryStructureV1("sub", "setting", "opening", "twist", "first_person"));
    }

    /// <summary>A raw draft whose paragraph keyword counts are given; texts default to unique prose.</summary>
    private static StoryContentV1 RawDraft(
        int[] paragraphKeywordCounts,
        int hookKeywordCount,
        int endingKeywordCount,
        string[]? hookKeywords = null,
        (int Index, string[] Keywords)? extra = null,
        (int Index, string[] Keywords)? extra2 = null,
        string? text = null,
        string? beat = "setup",
        bool uniqueProse = false)
    {
        var paragraphs = new List<StorySegmentV1>();
        for (var i = 0; i < paragraphKeywordCounts.Length; i++)
        {
            var kws = extra is { } e && e.Index == i ? e.Keywords
                : extra2 is { } e2 && e2.Index == i ? e2.Keywords
                : Kws(paragraphKeywordCounts[i]);
            var body = uniqueProse ? Prose(100, 10_000 + (i * 100)) : text ?? Prose(30, 1000 + (i * 1000));
            paragraphs.Add(new StorySegmentV1(body, kws, [], beat));
        }

        return new StoryContentV1(
            "A quiet night",
            new StorySegmentV1(Prose(20, 1), hookKeywords ?? Kws(hookKeywordCount), []),
            paragraphs,
            new StorySegmentV1(Prose(20, 9000), Kws(endingKeywordCount), []),
            "A short story.",
            ["ghost"],
            [],
            new StoryStructureV1("sub", "setting", "opening", "twist", "first_person"));
    }

    private static QualityReport RepeatDraft(string first, string second)
    {
        var draft = RawDraft([1, 1], 1, 1) with { };
        var paragraphs = draft.Paragraphs.ToList();
        paragraphs[0] = paragraphs[0] with { Text = first };
        paragraphs[1] = paragraphs[1] with { Text = second };
        return Checker.Check(draft with { Paragraphs = paragraphs }, Ctx(length: false));
    }

    private static StoryCheckContext Ctx(
        Topic topic = Topic.Ghost,
        LengthTier? tier = LengthTier.Short,
        bool length = true,
        int factCount = 0,
        IReadOnlyList<string>? titles = null,
        bool hasUrlSources = false,
        double? score = null,
        SimilarityVerdict verdict = SimilarityVerdict.Pass,
        IReadOnlyList<string>? moderation = null,
        bool stale = false)
    {
        var rules = new StoryRules();
        return new StoryCheckContext(topic, tier, length ? rules.Length : null, rules, factCount, titles ?? [], hasUrlSources, score, verdict, moderation, stale, null, NameCorpus.Empty);
    }
}
