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
}
