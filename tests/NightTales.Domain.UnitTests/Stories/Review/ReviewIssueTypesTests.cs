using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Review;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Review;

/// <summary>The closed review issue list (02.6.1-T1): objective types may block, taste types never do.</summary>
public sealed class ReviewIssueTypesTests
{
    private static readonly ReviewIssueType[] TasteTypes =
    [
        ReviewIssueType.WeakHook,
        ReviewIssueType.FlatEnding,
        ReviewIssueType.PacingIssue,
        ReviewIssueType.RepetitivePhrasing,
    ];

    [Fact]
    public void ReviewIssueTypes_BlockingAllowed_OnlyForObjectiveTypes()
    {
        var all = Enum.GetValues<ReviewIssueType>();

        all.Length.ShouldBe(17);
        foreach (var type in all)
        {
            var taste = TasteTypes.Contains(type);

            ReviewIssueTypes.IsObjective(type).ShouldBe(!taste, type.ToString());
            ReviewIssueTypes.IsBlockingAllowed(type).ShouldBe(!taste, type.ToString());
            ReviewIssueTypes.KindOf(ReviewIssueTypes.Wire(type)).ShouldBe(taste ? IssueKind.Taste : IssueKind.Objective, type.ToString());
        }
    }

    [Fact]
    public void ReviewIssueTypes_Wire_RoundTripsEveryTypeAndRejectsUnknownNames()
    {
        foreach (var type in Enum.GetValues<ReviewIssueType>())
        {
            ReviewIssueTypes.TryParse(ReviewIssueTypes.Wire(type), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(type);
        }

        ReviewIssueTypes.TryParse("plot_hole", out _).ShouldBeFalse();
        ReviewIssueTypes.TryParse(null, out _).ShouldBeFalse();
        ReviewIssueTypes.TryParse("Weak_Hook", out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => ReviewIssueTypes.KindOf("plot_hole"));
    }
}
