using System.Globalization;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;
using StoryText = NightTales.Domain.Stories.Similarity.StoryText;

namespace NightTales.Domain.UnitTests.Stories.Similarity;

public sealed class SimilarityScorerTests
{
    private static readonly string[] TopAddresses = ["p:0", "p:1", "p:10", "p:11", "p:2", "p:3", "p:4", "p:5", "p:6", "p:7"];
    private static readonly string[] StoryAddresses = ["title", "hook", "p:0", "p:1", "ending"];
    private static readonly string[] StoryProse = ["Title", "Hook", "First paragraph", "Second paragraph", "Ending"];

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Vi)]
    public void Score_IdenticalText_IsOne(Language language)
    {
        var text = language == Language.Vi ? SimilarityFixtures.VietnameseSource : SimilarityFixtures.EnglishSource;
        SimilarityScorer.Score(SimilarityFixtures.Story(text), SimilarityFixtures.Corpus(text), SimilarityFixtures.Options(language)).Score.ShouldBe(1);
    }

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Vi)]
    public void Score_UnrelatedText_IsZero(Language language)
    {
        var source = language == Language.Vi ? SimilarityFixtures.VietnameseSource : SimilarityFixtures.EnglishSource;
        var fresh = language == Language.Vi ? SimilarityFixtures.VietnameseNew : SimilarityFixtures.EnglishNew;
        SimilarityScorer.Score(SimilarityFixtures.Story(fresh), SimilarityFixtures.Corpus(source), SimilarityFixtures.Options(language)).Score.ShouldBe(0);
    }

    [Fact]
    public void Score_LightParaphrase_IsBetween_0_02_And_0_10()
    {
        // Eleven copied tokens provide four n=8 windows. The fresh 73-token paragraph
        // provides 66 disjoint windows: 4 / (4 + 66), not a tuned threshold or tiny fixture.
        var story = SimilarityFixtures.Story(SimilarityFixtures.CopiedSentence, SimilarityFixtures.EnglishNew);
        var result = SimilarityScorer.Score(story, SimilarityFixtures.Corpus(SimilarityFixtures.EnglishSource), SimilarityFixtures.Options(Language.En));
        result.Score.ShouldBe(4d / 70);
        result.Score.ShouldBeGreaterThan(0.02);
        result.Score.ShouldBeLessThan(0.10);
        SimilarityScorer.IsScorable(story, SimilarityFixtures.Corpus(SimilarityFixtures.EnglishSource), SimilarityFixtures.Options(Language.En)).ShouldBeTrue();
    }

    [Fact]
    public void Score_MostlyCopiedStory_IsAbove_0_20()
    {
        var story = SimilarityFixtures.Story(SimilarityFixtures.EnglishSource,
            "At dusk his niece arrived with fresh tea and listened quietly as he described the sailor who had survived.");
        var result = SimilarityScorer.Score(story, SimilarityFixtures.Corpus(SimilarityFixtures.EnglishSource), SimilarityFixtures.Options(Language.En));
        // 64 source tokens give 57 matched windows; the new 19-token ending gives 12 unmatched.
        result.Score.ShouldBe(57d / 69);
        result.Score.ShouldBeGreaterThan(0.20);
    }

    [Fact]
    public void Score_VietnameseUsesN10_EnglishUsesN8()
    {
        SimilarityOptions.NFor(Language.Vi).ShouldBe(10);
        SimilarityOptions.NFor(Language.En).ShouldBe(8);
        SimilarityFixtures.Options(Language.Vi).N.ShouldBe(10);
        SimilarityFixtures.Options(Language.En).N.ShouldBe(8);
        var eightTokens = SimilarityFixtures.Story("one two three four five six seven eight");
        var corpus = SimilarityFixtures.Corpus(eightTokens.Segments[0].Text);
        SimilarityScorer.Score(eightTokens, corpus, SimilarityFixtures.Options(Language.En)).Score.ShouldBe(1);
        SimilarityScorer.Score(eightTokens, corpus, SimilarityFixtures.Options(Language.Vi)).Score.ShouldBe(0);
    }

    [Fact]
    public void IsScorable_StoryShorterThanMinTokens_IsFalse() =>
        SimilarityScorer.IsScorable(SimilarityFixtures.Story(string.Join(' ', Enumerable.Repeat("quiet", 49))),
            SimilarityFixtures.Corpus(SimilarityFixtures.EnglishSource), SimilarityFixtures.Options(Language.En)).ShouldBeFalse();

    [Fact]
    public void IsScorable_EmptyCorpus_IsFalse()
    {
        var story = SimilarityFixtures.Story(SimilarityFixtures.EnglishSource);
        SimilarityScorer.IsScorable(story, [], SimilarityFixtures.Options(Language.En)).ShouldBeFalse();
        SimilarityScorer.IsScorable(story, SimilarityFixtures.Corpus(" \0“”—\ud800"), SimilarityFixtures.Options(Language.En)).ShouldBeFalse();
    }

    [Fact]
    public void IsScorable_ExactlyMinTokensAcrossSegments_IsTrue()
    {
        var story = SimilarityFixtures.Story(string.Join(' ', Enumerable.Repeat("quiet", 25)), string.Join(' ', Enumerable.Repeat("room", 25)));
        SimilarityScorer.IsScorable(story, SimilarityFixtures.Corpus("one"), SimilarityFixtures.Options(Language.En)).ShouldBeTrue();
    }

    [Fact]
    public void Score_ShareIsOverStoryShingles_NotCorpus()
    {
        var options = SimilarityFixtures.Options(Language.En) with { N = 2 };
        var result = SimilarityScorer.Score(SimilarityFixtures.Story("alpha beta gamma delta"),
            SimilarityFixtures.Corpus("alpha beta epsilon zeta eta theta iota kappa"), options);
        result.Score.ShouldBe(1d / 3);
    }

    [Fact]
    public void Score_PerSegmentShare_IsExactForCopiedParagraph()
    {
        var result = SimilarityScorer.Score(SimilarityFixtures.Story(SimilarityFixtures.EnglishSource, SimilarityFixtures.EnglishNew, "short hook"),
            SimilarityFixtures.Corpus(SimilarityFixtures.EnglishSource), SimilarityFixtures.Options(Language.En));
        result.Segments.ShouldHaveSingleItem().ShouldBe(new SegmentOverlap("p:0", 1));
    }

    [Fact]
    public void Score_SegmentsSortedDescAndTop10()
    {
        var paragraphs = Enumerable.Range(0, 12).Select(i => new StorySegment("p:" + i.ToString(CultureInfo.InvariantCulture),
            "quiet room number " + i.ToString(CultureInfo.InvariantCulture) + " holds a hidden letter beneath the floor")).ToArray();
        var corpus = SimilarityFixtures.Corpus(string.Join(' ', paragraphs.Select(p => p.Text)));
        var result = SimilarityScorer.Score(new StoryText(paragraphs), corpus, SimilarityFixtures.Options(Language.En));
        result.Segments.Select(s => s.Segment).ShouldBe(TopAddresses);
        result.Segments.All(s => s.Share == 1).ShouldBeTrue();

        var mixed = SimilarityScorer.Score(SimilarityFixtures.Story(SimilarityFixtures.EnglishSource + " " + SimilarityFixtures.EnglishNew, SimilarityFixtures.EnglishSource),
            SimilarityFixtures.Corpus(SimilarityFixtures.EnglishSource), SimilarityFixtures.Options(Language.En));
        mixed.Segments[0].Segment.ShouldBe("p:1");
        mixed.Segments[0].Share.ShouldBeGreaterThan(mixed.Segments[1].Share);
    }

    [Theory]
    [InlineData(Language.En, 8)]
    [InlineData(Language.Vi, 10)]
    public void Score_ResultN_IsTheLanguageN(Language language, int n) =>
        SimilarityScorer.Score(SimilarityFixtures.Story(SimilarityFixtures.EnglishSource), [], SimilarityFixtures.Options(language)).N.ShouldBe(n);

    [Fact]
    public void Score_WindowsNeverCrossSegmentBoundaries()
    {
        var result = SimilarityScorer.Score(SimilarityFixtures.Story("one", "two"), SimilarityFixtures.Corpus("one two"),
            SimilarityFixtures.Options(Language.En) with { N = 2 });
        result.Score.ShouldBe(0);
        result.Segments.ShouldBeEmpty();
    }

    [Fact]
    public void Score_StoryAndCorpusDuplicateWindows_AreDeduplicated()
    {
        var result = SimilarityScorer.Score(SimilarityFixtures.Story("alpha beta gamma", "alpha beta gamma"),
            SimilarityFixtures.Corpus("alpha beta", "alpha beta"), SimilarityFixtures.Options(Language.En) with { N = 2 });
        result.Score.ShouldBe(0.5);
        result.Segments.Count.ShouldBe(2);
        result.Segments.All(segment => segment.Share == 0.5).ShouldBeTrue();
    }

    [Fact]
    public void Score_CorpusMatchesAreUnionedAcrossSources()
    {
        SimilarityScorer.Score(SimilarityFixtures.Story("alpha beta gamma"), SimilarityFixtures.Corpus("alpha beta", "beta gamma"),
            SimilarityFixtures.Options(Language.En) with { N = 2 }).Score.ShouldBe(1);
    }

    [Fact]
    public void StoryText_From_UsesFrozenAddressesAndExcludesMetadata()
    {
        var content = new StoryContentV1("Title", new StorySegmentV1("Hook", [], []),
            [new StorySegmentV1("First paragraph", [], []), new StorySegmentV1("Second paragraph", [], [])],
            new StorySegmentV1("Ending", [], []), "Metadata description", ["tag"], ["warning"],
            new StoryStructureV1("subgenre", "setting", "opening", "twist", "narrator"));
        var story = StoryText.From(content);
        story.Segments.Select(s => s.Address).ShouldBe(StoryAddresses);
        story.Segments.Select(s => s.Text).ShouldBe(StoryProse);
    }

    [Fact]
    public void SimilarityOptions_For_MissingLanguageFallsBackToEnN()
    {
        var settings = new SimilaritySettings();
        settings.N.Remove("vi");
        settings.N["en"] = 12;
        SimilarityOptions.For(Language.Vi, settings).N.ShouldBe(12);
        settings.N.Clear();
        SimilarityOptions.For(Language.Vi, settings).N.ShouldBe(8);
    }

    [Fact]
    public void SimilarityOptions_For_CopiesThresholdsAndMinTokens()
    {
        var settings = new SimilaritySettings { WarnAbove = 0.1, BlockAbove = 0.2, MinTokens = 70 };
        SimilarityOptions.For(Language.En, settings).ShouldBe(new SimilarityOptions(Language.En, 8, 0.1, 0.2, 70));
    }
}

internal static class SimilarityFixtures
{
    internal const string CopiedSentence = "The old keeper left a lantern beside the narrow wooden gate.";
    internal const string EnglishSource = "The old keeper left a lantern beside the narrow wooden gate. Before sunrise he inspected the harbour wall and counted the fishing boats returning through heavy rain. A broken radio lay under his desk while a kettle cooled near the window. When the youngest sailor finally reached shore, the keeper wrapped him in a dry blanket and sent a message to his worried family.";
    internal const string EnglishNew = "Across town a young baker prepared breakfast for the families waiting outside her shop. She warmed fresh bread in the oven and placed bowls of soup along the counter. Her daughter collected spare coats from neighbours who wanted to help. Nobody asked for payment that morning because several houses had flooded overnight. By noon the children were drawing cheerful pictures on scraps of paper while their parents arranged a temporary place to sleep.";
    internal const string VietnameseSource = "Đêm ấy người giữ bến mang chiếc đèn dầu ra trước cổng gỗ rồi đứng chờ những con thuyền trở về. Mưa phủ kín mặt sông và tiếng gió khiến ông không nghe rõ lời gọi từ xa. Khi một cậu bé xuất hiện dưới mái hiên ông đưa cho cậu chiếc áo khô rồi hỏi tên gia đình. Cậu chỉ về phía ngôi nhà nhỏ cuối con đường nơi mẹ vẫn đang thức.";
    internal const string VietnameseNew = "Sáng hôm sau cô thợ làm bánh mở cửa tiệm sớm hơn mọi ngày để chuẩn bị bữa ăn cho các gia đình mất nhà. Những người hàng xóm đem chăn sạch và áo ấm đến đặt lên bàn. Trẻ nhỏ ngồi vẽ tranh trong góc phòng còn cha mẹ bàn chuyện sửa lại mái ngói. Không ai nhắc đến tiền vì điều cần nhất lúc này là một chỗ nghỉ an toàn.";

    internal static SimilarityOptions Options(Language language) => SimilarityOptions.For(language, new SimilaritySettings());
    internal static StoryText Story(params string[] paragraphs) => new(paragraphs.Select((text, i) =>
        new StorySegment("p:" + i.ToString(CultureInfo.InvariantCulture), text)).ToArray());
    internal static IReadOnlyList<CorpusText> Corpus(params string[] texts) => texts.Select(text => new CorpusText(Guid.Empty, text)).ToArray();
}
