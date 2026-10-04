using NightTales.Application.Abstractions.Moderation;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Moderation;

/// <summary>
/// 02.4.1-T1 (02 README D12): the blocking / advisory classification that feeds <c>audit.v1.moderation</c>. A
/// blocking category blocks approval, never the run; every other category is a hint.
/// </summary>
public sealed class ModerationOptionsTests
{
    [Theory]
    [InlineData("sexual/minors")]
    [InlineData("self-harm/instructions")]
    [InlineData("illicit/violent")]
    [InlineData("hate/threatening")]
    public void Moderator_FlaggedDraft_BlockingCategory_BlocksApprovalNotRun(string category)
    {
        var classification = new ModerationOptions().Classify([category]);

        classification.BlocksApproval.ShouldBeTrue();
        classification.Blocking.ShouldBe([category]);
        classification.Advisory.ShouldBeEmpty();
    }

    [Fact]
    public void Moderator_FlaggedDraft_AdvisoryCategory_IsHintOnly()
    {
        var classification = new ModerationOptions().Classify(["violence", "harassment", "hate"]);

        classification.BlocksApproval.ShouldBeFalse();
        classification.Blocking.ShouldBeEmpty();
        classification.Advisory.ShouldBe(["violence", "harassment", "hate"]);
    }

    [Fact]
    public void Classify_MixedAndDuplicateCategories_SplitsAndDeduplicates()
    {
        var classification = new ModerationOptions().Classify(["violence", "hate/threatening", "violence", "hate/threatening"]);

        classification.Blocking.ShouldBe(["hate/threatening"]);
        classification.Advisory.ShouldBe(["violence"]);
        classification.BlocksApproval.ShouldBeTrue();
    }

    [Fact]
    public void Classify_ConfiguredBlockingSet_ReplacesTheDefault()
    {
        var options = new ModerationOptions { BlockingCategories = new HashSet<string>(StringComparer.Ordinal) { "violence" } };

        var classification = options.Classify(["violence", "hate/threatening"]);

        classification.Blocking.ShouldBe(["violence"]);
        classification.Advisory.ShouldBe(["hate/threatening"]);
    }

    [Fact]
    public void ModerationOptions_Defaults_AreD12AndTL21()
    {
        var options = new ModerationOptions();

        options.Model.ShouldBe("omni-moderation-latest");
        options.MaxCharsPerChunk.ShouldBe(8_000);
        options.TimeoutSeconds.ShouldBe(15);
    }
}
