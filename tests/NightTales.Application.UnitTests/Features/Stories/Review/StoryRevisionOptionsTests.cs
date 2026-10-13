using NightTales.Application.Features.Stories.Review;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Review;

/// <summary>
/// The <c>Story:Revision</c> section (02.6.2-T1, 02 README D21): a run allows 0 or 1 revisions and a drifted-segment
/// similarity inside (0, 1]; a value outside either bound must fail the Worker's boot, never reach the pipeline.
/// </summary>
public sealed class StoryRevisionOptionsTests
{
    [Fact]
    public void RevisionOptions_Defaults_PassValidation()
    {
        var options = new StoryRevisionOptions();

        options.Enabled.ShouldBeTrue();
        options.MaxRevisions.ShouldBe(1);
        options.MinTextSimilarity.ShouldBe(0.6);
        StoryRevisionOptions.Validate(options).ShouldBeEmpty();
    }

    [Fact]
    public void RevisionOptions_MaxRevisionsZero_PassesValidation()
    {
        // D21 allows a permanent off-switch without disabling the stage: zero revisions is valid.
        var options = new StoryRevisionOptions { MaxRevisions = 0 };

        StoryRevisionOptions.Validate(options).ShouldBeEmpty();
    }

    [Fact]
    public void RevisionOptions_MaxRevisionsTwo_FailsValidation()
    {
        var options = new StoryRevisionOptions { MaxRevisions = 2 };

        var problems = StoryRevisionOptions.Validate(options);

        problems.ShouldHaveSingleItem().ShouldContain(nameof(StoryRevisionOptions.MaxRevisions));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void RevisionOptions_MaxRevisionsOutOfRange_FailsValidation(int maxRevisions)
    {
        var options = new StoryRevisionOptions { MaxRevisions = maxRevisions };

        StoryRevisionOptions.Validate(options).ShouldHaveSingleItem()
            .ShouldContain(nameof(StoryRevisionOptions.MaxRevisions));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.0001)]
    [InlineData(double.NaN)]
    public void RevisionOptions_MinTextSimilarityOutOfRange_FailsValidation(double minTextSimilarity)
    {
        var options = new StoryRevisionOptions { MinTextSimilarity = minTextSimilarity };

        StoryRevisionOptions.Validate(options).ShouldHaveSingleItem()
            .ShouldContain(nameof(StoryRevisionOptions.MinTextSimilarity));
    }

    [Fact]
    public void RevisionOptions_MinTextSimilarityOne_PassesValidation()
    {
        var options = new StoryRevisionOptions { MinTextSimilarity = 1 };

        StoryRevisionOptions.Validate(options).ShouldBeEmpty();
    }
}
