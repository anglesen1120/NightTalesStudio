using NightTales.Domain.Compliance;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Compliance;

/// <summary>The excerpt cut of <see cref="ModerationEvent.Record"/> (02.4.1-T1 QA-1).</summary>
public sealed class ModerationEventTests
{
    [Fact]
    public void ModerationEvent_Record_ExcerptCutAcrossASurrogatePair_DropsTheHighSurrogate()
    {
        // The emoji's high surrogate sits at index 299, so a plain cut at 300 would keep half a pair.
        var text = new string('a', ModerationEvent.ExcerptMaxLength - 1) + "\U0001F600" + "tail";

        var row = ModerationEvent.Record(Guid.NewGuid(), ModerationSource.OpenaiText, "draft", ["violence"], DateTimeOffset.UnixEpoch, null, text);

        row.Excerpt.ShouldBe(new string('a', ModerationEvent.ExcerptMaxLength - 1));
    }

    // MemberData, not InlineData: attribute metadata is UTF-8, which cannot carry a lone surrogate (it arrives as U+FFFD bytes).
    public static TheoryData<string, string> LoneSurrogateCases => new()
    {
        { "ab\uD83Dcd", "ab\uFFFDcd" },
        { "ab\uDE00cd", "ab\uFFFDcd" },
        { "\uD83D\uD83Dx", "\uFFFD\uFFFDx" },
        { "\uDE00\uD83D", "\uFFFD\uFFFD" },
        { "a\uD83D", "a\uFFFD" },
    };

    [Theory]
    [MemberData(nameof(LoneSurrogateCases))]
    public void ModerationEvent_Record_LoneSurrogateAnywhereInTheExcerpt_IsReplacedWithTheReplacementCharacter(string text, string expected)
    {
        var row = ModerationEvent.Record(Guid.NewGuid(), ModerationSource.OpenaiText, "draft", ["violence"], DateTimeOffset.UnixEpoch, null, text);

        row.Excerpt.ShouldBe(expected);
    }

    [Fact]
    public void ModerationEvent_Record_ValidPairsInTheExcerpt_AreKeptIntact()
    {
        var text = "a\U0001F600b\U0001F680c";

        var row = ModerationEvent.Record(Guid.NewGuid(), ModerationSource.OpenaiText, "draft", ["violence"], DateTimeOffset.UnixEpoch, null, text);

        row.Excerpt.ShouldBe(text);
    }

    [Fact]
    public void ModerationEvent_Record_LoneSurrogateBeforeTheCut_IsReplacedAndTheCutStillKeepsPairsWhole()
    {
        // A lone low surrogate at the start, then filler so the emoji straddles the 300 cap.
        var text = "\uDE00" + new string('a', ModerationEvent.ExcerptMaxLength - 2) + "\U0001F600" + "tail";

        var row = ModerationEvent.Record(Guid.NewGuid(), ModerationSource.OpenaiText, "draft", ["violence"], DateTimeOffset.UnixEpoch, null, text);

        row.Excerpt.ShouldBe("\uFFFD" + new string('a', ModerationEvent.ExcerptMaxLength - 2));
    }

    [Fact]
    public void ModerationEvent_Record_PairEndingExactlyAtTheCap_IsKept()
    {
        var text = new string('a', ModerationEvent.ExcerptMaxLength - 2) + "\U0001F600" + "tail";

        var row = ModerationEvent.Record(Guid.NewGuid(), ModerationSource.OpenaiText, "draft", ["violence"], DateTimeOffset.UnixEpoch, null, text);

        row.Excerpt.ShouldBe(new string('a', ModerationEvent.ExcerptMaxLength - 2) + "\U0001F600");
    }
}
