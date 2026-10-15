using NightTales.Domain.Series.Bible;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Bible;

/// <summary>
/// The avoid-plots list (02.6.3 plan §4 U4): first sentence per summary, title-only summaries skipped, deduplicated,
/// capped at <c>Max</c> and cut to <c>MaxCharsPerEntry</c>.
/// </summary>
public sealed class AvoidPlotsBuilderTests
{
    [Fact]
    public void AvoidPlotsBuilder_ReturnsFirstSentenceTrimmed120_Deduped()
    {
        var longSentence = string.Join(' ', Enumerable.Repeat("bí", 150));
        IReadOnlyList<BibleEpisodeSummarySnapshot> summaries =
        [
            new BibleEpisodeSummarySnapshot("Tập 1", "Một người trở về. Chi tiết khác không cần nhắc."),
            new BibleEpisodeSummarySnapshot("Tập 2", "Một người trở về!"),
            new BibleEpisodeSummarySnapshot("Tập 3", longSentence),
        ];

        var entries = AvoidPlotsBuilder.Build(summaries);

        entries.Count.ShouldBe(2);
        entries[0].ShouldBe("Một người trở về.");
        entries[1].Length.ShouldBeLessThanOrEqualTo(120);
        entries[1].ShouldStartWith("bí bí");
        entries[1].ShouldEndWith("bí");
    }

    [Fact]
    public void AvoidPlotsBuilder_EndsFirstSentenceAtEllipsisOrExclamation()
    {
        IReadOnlyList<BibleEpisodeSummarySnapshot> summaries =
        [
            new BibleEpisodeSummarySnapshot("Tập 1", "Anh ấy đi… rồi không về."),
            new BibleEpisodeSummarySnapshot("Tập 2", "Cô ấy hỏi! Không ai đáp."),
            new BibleEpisodeSummarySnapshot("Tập 3", "Ông ta hỏi? Rồi im."),
        ];

        var entries = AvoidPlotsBuilder.Build(summaries);

        entries.Count.ShouldBe(3);
        entries[0].ShouldBe("Anh ấy đi…");
        entries[1].ShouldBe("Cô ấy hỏi!");
        entries[2].ShouldBe("Ông ta hỏi?");
    }

    [Fact]
    public void AvoidPlotsBuilder_CaseAndPunctuationVariants_CollapseToOneEntry()
    {
        IReadOnlyList<BibleEpisodeSummarySnapshot> summaries =
        [
            new BibleEpisodeSummarySnapshot("Tập 1", "Một người trở về."),
            new BibleEpisodeSummarySnapshot("Tập 2", "Một người trở về!"),
            new BibleEpisodeSummarySnapshot("Tập 3", "MỘT NGƯỜI TRỞ VỀ?"),
        ];

        var entries = AvoidPlotsBuilder.Build(summaries);

        entries.Count.ShouldBe(1);
        entries[0].ShouldBe("Một người trở về.");
    }

    [Fact]
    public void AvoidPlotsBuilder_SkipsTitleOnlyEntries()
    {
        IReadOnlyList<BibleEpisodeSummarySnapshot> summaries =
        [
            new BibleEpisodeSummarySnapshot("Tập 1", null),
            new BibleEpisodeSummarySnapshot("Tập 2", ""),
            new BibleEpisodeSummarySnapshot("Tập 3", "Một."),
            new BibleEpisodeSummarySnapshot("Tập 4", "   "),
            new BibleEpisodeSummarySnapshot("Tập 5", null),
            new BibleEpisodeSummarySnapshot("Tập 6", "Hai."),
            new BibleEpisodeSummarySnapshot("Tập 7", "Ba."),
        ];

        // The skipped entries do not count toward the cap: max 2 keeps the first two real sentences.
        var entries = AvoidPlotsBuilder.Build(summaries, max: 2);

        entries.Count.ShouldBe(2);
        entries[0].ShouldBe("Một.");
        entries[1].ShouldBe("Hai.");
    }

    [Fact]
    public void AvoidPlotsBuilder_CapsTheListAtMax_InInputOrder()
    {
        var summaries = Enumerable.Range(1, 50)
            .Select(index => new BibleEpisodeSummarySnapshot($"Tập {index}", $"Tập {index} có một bí mật."))
            .ToList();

        var entries = AvoidPlotsBuilder.Build(summaries, max: 25);

        entries.Count.ShouldBe(25);
        entries[0].ShouldBe("Tập 1 có một bí mật.");
        entries[^1].ShouldBe("Tập 25 có một bí mật.");
    }
}
