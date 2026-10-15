using NightTales.Domain.Series.Bible;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Bible;

/// <summary>
/// QA boundary tests for <see cref="AvoidPlotsBuilder"/> (02.6.3-T3). Each guards a plausible off-by-one or
/// word-split bug in the 120-character cut that the existing <c>AvoidPlotsBuilderTests</c> only bounds loosely.
/// </summary>
public sealed class AvoidPlotsBoundaryQaTests
{
    [Fact]
    public void AvoidPlotsBuilder_SentenceOfExactly120Chars_IsKeptWhole()
    {
        var sentence = new string('x', 119) + ".";
        sentence.Length.ShouldBe(120);

        IReadOnlyList<BibleEpisodeSummarySnapshot> summaries =
        [
            new BibleEpisodeSummarySnapshot("Tập 1", sentence),
        ];

        var entries = AvoidPlotsBuilder.Build(summaries);

        entries.Count.ShouldBe(1);
        entries[0].ShouldBe(sentence);
    }

    [Fact]
    public void AvoidPlotsBuilder_SpaceFreeSentenceOver120Chars_IsCutToExactly120()
    {
        var sentence = new string('x', 121) + ".";

        IReadOnlyList<BibleEpisodeSummarySnapshot> summaries =
        [
            new BibleEpisodeSummarySnapshot("Tập 1", sentence),
        ];

        var entries = AvoidPlotsBuilder.Build(summaries);

        entries.Count.ShouldBe(1);
        entries[0].Length.ShouldBe(120);
        sentence.StartsWith(entries[0], StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void AvoidPlotsBuilder_LongSpacedSentence_CutsOnWordBoundary()
    {
        // 30 five-letter words, no terminator: 179 chars, so the cut must land on a space.
        var sentence = string.Join(' ', Enumerable.Repeat("abcde", 30));

        IReadOnlyList<BibleEpisodeSummarySnapshot> summaries =
        [
            new BibleEpisodeSummarySnapshot("Tập 1", sentence),
        ];

        var entries = AvoidPlotsBuilder.Build(summaries);

        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Length.ShouldBeLessThanOrEqualTo(120);
        entry.ShouldNotEndWith(" ");
        sentence.StartsWith(entry + " ", StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void AvoidPlotsBuilder_BlankSummaries_ContributeNothing_AndEmptyInputOrZeroMax_ReturnEmpty()
    {
        IReadOnlyList<BibleEpisodeSummarySnapshot> blanks =
        [
            new BibleEpisodeSummarySnapshot("Tập 1", string.Empty),
            new BibleEpisodeSummarySnapshot("Tập 2", "   \n\t "),
            new BibleEpisodeSummarySnapshot("Tập 3", "Một người trở về."),
        ];

        var entries = AvoidPlotsBuilder.Build(blanks);

        entries.Count.ShouldBe(1);
        entries[0].ShouldBe("Một người trở về.");

        IReadOnlyList<BibleEpisodeSummarySnapshot> none = [];
        AvoidPlotsBuilder.Build(none).Count.ShouldBe(0);
        AvoidPlotsBuilder.Build(blanks, max: 0).Count.ShouldBe(0);
    }
}
