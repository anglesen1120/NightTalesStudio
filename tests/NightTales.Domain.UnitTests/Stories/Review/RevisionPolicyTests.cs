using NightTales.Domain.Stories.Review;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Review;

/// <summary>The D21 step-5 decision (02.6.2-T1): revise once, and only for a blocking objective issue.</summary>
public sealed class RevisionPolicyTests
{
    [Fact]
    public void RevisionPolicy_BlockingObjectiveIssue_Revises()
    {
        var review = Review(new ReviewIssueV1("r1", "missing_setup_before_twist", "blocking", "p:2", "e", "i", "r"));

        RevisionPolicy.ShouldRevise(review, maxRevisions: 1, revisionAlreadyApplied: false).ShouldBeTrue();
        RevisionPolicy.BlockingObjectiveIssues(review).ShouldHaveSingleItem().Id.ShouldBe("r1");
    }

    [Fact]
    public void RevisionPolicy_TasteOnly_DoesNotRevise()
    {
        // A taste issue is never revised, even when a malformed review marked it blocking (D21).
        var advisoryTaste = Review(new ReviewIssueV1("r1", "weak_hook", "advisory", "hook", "e", "i", "r"));
        var blockingTaste = Review(new ReviewIssueV1("r1", "weak_hook", "blocking", "hook", "e", "i", "r"));

        RevisionPolicy.ShouldRevise(advisoryTaste, 1, revisionAlreadyApplied: false).ShouldBeFalse();
        RevisionPolicy.ShouldRevise(blockingTaste, 1, revisionAlreadyApplied: false).ShouldBeFalse();
        RevisionPolicy.BlockingObjectiveIssues(blockingTaste).ShouldBeEmpty();
    }

    [Fact]
    public void RevisionPolicy_RevisionAlreadyApplied_DoesNotReviseAgain()
    {
        var review = Review(new ReviewIssueV1("r1", "fact_ref_invalid", "blocking", "p:1", "e", "i", "r"));

        RevisionPolicy.ShouldRevise(review, 1, revisionAlreadyApplied: true).ShouldBeFalse();
        RevisionPolicy.BlockingObjectiveIssues(review).ShouldHaveSingleItem().Id.ShouldBe("r1");
    }

    [Fact]
    public void RevisionPolicy_MaxRevisionsZero_NeverRevises()
    {
        var review = Review(new ReviewIssueV1("r1", "fact_ref_invalid", "blocking", "p:1", "e", "i", "r"));

        RevisionPolicy.ShouldRevise(review, maxRevisions: 0, revisionAlreadyApplied: false).ShouldBeFalse();
        RevisionPolicy.ShouldRevise(review, maxRevisions: -1, revisionAlreadyApplied: false).ShouldBeFalse();
    }

    [Fact]
    public void RevisionPolicy_NullReviewOrNullIssues_MeansNoIssuesNeverACrash()
    {
        var nullIssues = new StoryReviewV1(new ReviewScores(4, 3, 4, 4, 3, null, 4), null!, [], null, [], "summary");

        RevisionPolicy.ShouldRevise(null, 1, revisionAlreadyApplied: false).ShouldBeFalse();
        RevisionPolicy.BlockingObjectiveIssues(null).ShouldBeEmpty();
        RevisionPolicy.ShouldRevise(nullIssues, 1, revisionAlreadyApplied: false).ShouldBeFalse();
        RevisionPolicy.BlockingObjectiveIssues(nullIssues).ShouldBeEmpty();
        RevisionPolicy.ShouldRevise(Review(), 1, revisionAlreadyApplied: false).ShouldBeFalse();
    }

    [Fact]
    public void RevisionPolicy_BlockingObjectiveIssues_KeepsOnlyBlockingObjectiveInReviewOrder()
    {
        var review = Review(
            new ReviewIssueV1("r1", "missing_setup_before_twist", "blocking", "p:2", "e", "i", "r"),
            new ReviewIssueV1("r2", "weak_hook", "advisory", "hook", "e", "i", "r"),
            new ReviewIssueV1("r3", "ear_rule_violation", "blocking", "p:1", "e", "i", "r"),
            new ReviewIssueV1("r4", "length_out_of_range", "advisory", "meta", "e", "i", "r"));

        RevisionPolicy.ShouldRevise(review, 1, revisionAlreadyApplied: false).ShouldBeTrue();
        RevisionPolicy.BlockingObjectiveIssues(review).Select(issue => issue.Id).ShouldBe(["r1", "r3"]);
    }

    [Fact]
    public void RevisionPolicy_IssueTypeOutsideTheClosedList_IsNotObjectiveAndNeverRevises()
    {
        var review = Review(new ReviewIssueV1("r1", "plot_hole", "blocking", "p:1", "e", "i", "r"));

        RevisionPolicy.BlockingObjectiveIssues(review).ShouldBeEmpty();
        RevisionPolicy.ShouldRevise(review, 1, revisionAlreadyApplied: false).ShouldBeFalse();
    }

    private static StoryReviewV1 Review(params ReviewIssueV1[] issues) =>
        new(new ReviewScores(4, 3, 4, 4, 3, null, 4), issues, [], null, [], "summary");
}
