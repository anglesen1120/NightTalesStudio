using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Quality;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;
using StoryText = NightTales.Domain.Stories.Similarity.StoryText;

namespace NightTales.Domain.UnitTests.Stories.Similarity;

public sealed class SimilarityQaTests
{
    private static readonly SimilarityOptions Options = new(Language.Vi, 10, 0.05, 0.15, 50);
    private static readonly string[] AuditKeys = ["score", "n", "corpus", "segments"];
    private static readonly string[] ShareKeys = ["segment", "share"];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(159)]
    [InlineData(0xD800)]
    [InlineData(0xDBFF)]
    [InlineData(0xDC00)]
    [InlineData(0xDFFF)]
    public void Normalize_ControlsAndUnpairedSurrogates_SeparateTokens(int character)
    {
        TextNormalizer.Normalize("Tôi" + (char)character + "đã", Language.Vi).ShouldBe("tôi đã");
    }

    [Theory]
    [InlineData("\uFEFF")]
    [InlineData("\u200B")]
    [InlineData("\u200C")]
    [InlineData("\u200D")]
    [InlineData("\U0001F600")]
    [InlineData("\U00010400")]
    public void Normalize_FormatAndSupplementaryCharacters_AreValidAndStable(string character)
    {
        var normalized = TextNormalizer.Normalize("Tôi " + character + " đã đi", Language.Vi);
        normalized.IsNormalized(NormalizationForm.FormC).ShouldBeTrue();
        TextNormalizer.Normalize(normalized, Language.Vi).ShouldBe(normalized);
        var story = Story(string.Join(' ', Enumerable.Repeat(normalized, 12)));
        var sources = Corpus(story.Segments[0].Text);
        var first = SimilarityScorer.Score(story, sources, Options);
        first.Score.ShouldBe(1);
        for (var i = 0; i < 3; i++)
        {
            SimilarityScorer.Score(story, sources, Options).ToAuditSection().ToString().ShouldBe(first.ToAuditSection().ToString());
        }
    }

    [Fact]
    public void Normalize_MixedVietnameseNfdNfc_MatchesComposedCorpus()
    {
        const string composed = "Tôi đã đi về nhà bên dòng sông khi màn đêm buông xuống";
        var mixed = composed.Normalize(NormalizationForm.FormD) + " " + composed;
        TextNormalizer.Normalize(mixed, Language.Vi).ShouldBe(TextNormalizer.Normalize(composed + " " + composed, Language.Vi));
        SimilarityScorer.Score(Story(mixed), Corpus(composed), Options).Score.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(" \t\r\n \u00A0 ")]
    [InlineData("“”——.,;:!?()[]_ / ")]
    public void IsScorable_WhitespaceAndPunctuationOnlyCorpus_IsFalse(string empty)
    {
        SimilarityScorer.IsScorable(Story(Tokens(50)), Corpus(empty), Options).ShouldBeFalse();
        SimilarityScorer.IsScorable(Story(empty), Corpus(Tokens(50)), Options).ShouldBeFalse();
        SimilarityScorer.Score(Story(empty), Corpus(Tokens(50)), Options).Score.ShouldBe(0);
    }

    [Fact]
    public void HugeSingleToken_NoWindows_NoException()
    {
        var token = new string('a', 400_000);
        Shingler.Hash(TextNormalizer.Normalize(token, Language.En), 8).ShouldBeEmpty();
        SimilarityScorer.IsScorable(Story(token), Corpus(token), Options).ShouldBeFalse();
        SimilarityScorer.ScoreFact(token, Corpus(token), Options).ShouldBe(0);
    }

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    public void IsScorable_MinTokensBoundary_IsExact(int count, bool expected)
    {
        var segments = new StoryText([new("hook", Tokens(25)), new("p:0", Tokens(count - 25))]);
        SimilarityScorer.IsScorable(segments, Corpus("source"), Options).ShouldBe(expected);
    }

    [Theory]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    public void Score_SegmentNBoundary_IsExact(int count, double expected)
    {
        var text = Tokens(count);
        SimilarityScorer.Score(Story(text), Corpus(text), Options).Score.ShouldBe(expected);
        Shingler.Hash(text, 10).Length.ShouldBe((int)expected);
    }

    [Fact]
    public void Score_WindowsCannotCrossSegmentsOrSourceTexts()
    {
        var first = Tokens(5);
        var second = "six seven eight nine ten";
        SimilarityScorer.Score(new StoryText([new("title", first), new("hook", second)]), Corpus(first + " " + second), Options).Score.ShouldBe(0);
        SimilarityScorer.Score(Story(first + " " + second), [new(Guid.Empty, first), new(Guid.Empty, second)], Options).Score.ShouldBe(0);
    }

    [Fact]
    public void Score_UnionBoundsAndDeterminism_AcrossDuplicateCorpora()
    {
        var story = Story("alpha beta gamma delta alpha beta gamma delta epsilon");
        var options = Options with { N = 2 };
        var sources = Corpus("alpha beta gamma", "gamma delta", "alpha beta gamma");
        var result = SimilarityScorer.Score(story, sources, options);
        result.Score.ShouldBe(3d / 5);
        for (var i = 0; i < 5; i++)
        {
            var again = SimilarityScorer.Score(story, sources.Reverse().ToArray(), options);
            again.Score.ShouldBeInRange(0, 1);
            again.ToAuditSection().ToString().ShouldBe(result.ToAuditSection().ToString());
        }
    }

    [Fact]
    public void Score_TopTenTies_UseOrdinalAddressesRegardlessOfInputOrder()
    {
        var segments = Enumerable.Range(0, 14).Select(i => new StorySegment("p:" + i.ToString(CultureInfo.InvariantCulture), Tokens(10))).Reverse().ToArray();
        var result = SimilarityScorer.Score(new StoryText(segments), Corpus(Tokens(10)), Options);
        result.Segments.Count.ShouldBe(10);
        result.Segments.Select(s => s.Segment).ShouldBe(segments.Select(s => s.Address).Order(StringComparer.Ordinal).Take(10));
        result.Segments.All(s => s.Share == 1).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0.05, SimilarityVerdict.Pass)]
    [InlineData(0.05000001, SimilarityVerdict.Warn)]
    [InlineData(0.15, SimilarityVerdict.Warn)]
    [InlineData(0.15000001, SimilarityVerdict.Block)]
    public void Guard_ExactThresholds_UseStrictGreaterThan(double score, SimilarityVerdict verdict) =>
        SimilarityApprovalGuard.Classify(score, Options).ShouldBe(verdict);

    [Fact]
    public void Guard_BlockAboveOneRollback_NeverBlocksValidScore()
    {
        SimilarityApprovalGuard.Classify(1, Options with { BlockAbove = 1 }).ShouldBe(SimilarityVerdict.Warn);
        SimilarityApprovalGuard.Classify(null, Options with { BlockAbove = 1 }).ShouldBe(SimilarityVerdict.Pass);
    }

    [Fact]
    public void ScoreFact_EmptySources_ReturnsZero() => SimilarityScorer.ScoreFact(Tokens(20), [], Options).ShouldBe(0);

    [Fact]
    public void Audit_RoundsHalfUpToFourDecimals_AndContainsNoText()
    {
        var audit = (JsonObject)new SimilarityResult(0.12345, 10, "sources", [new("p:3", 0.33335)]).ToAuditSection();
        audit.Select(p => p.Key).ShouldBe(AuditKeys);
        audit["score"]!.GetValue<double>().ShouldBe(0.1235);
        var share = (JsonObject)((JsonArray)audit["segments"]!)[0]!;
        share.Select(p => p.Key).ShouldBe(ShareKeys);
        share["share"]!.GetValue<double>().ShouldBe(0.3334);
        audit.ToJsonString().ShouldNotContain("text");
        new CorpusText(Guid.Empty, "PRIVATE_SOURCE_MARKER").ToString().ShouldNotContain("PRIVATE_SOURCE_MARKER");
    }

    private static StoryText Story(string text) => new([new("p:0", text)]);
    private static CorpusText[] Corpus(params string[] texts) => texts.Select(t => new CorpusText(Guid.Empty, t)).ToArray();
    private static string Tokens(int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => "word" + i.ToString(CultureInfo.InvariantCulture)));
}
