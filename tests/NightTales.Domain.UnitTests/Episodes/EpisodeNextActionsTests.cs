using NightTales.Domain.Episodes;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Episodes;

/// <summary>
/// The next-actions table of 00 §7 (09 README D16, BD1): what the episode page may offer for each of the
/// eleven <see cref="EpisodeStatus"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Pure unit tests: no clock, no database, no context. Each named test pins one row of the table — the
/// members the task file names, plus the edge of 00 §7 the row also carries (the story editor of
/// <c>story_review</c>, the reopen of <c>story_approved</c>, the download of <c>video_review</c> and
/// <c>ready</c>) — while <see cref="Expected"/> is the whole table and
/// <see cref="Table_Matches_00_S7"/> walks it as a theory, so a member that changes for one status
/// cannot go unnoticed.
/// </para>
/// <para>
/// The order of every expectation is <see cref="EpisodeNextAction"/>'s declaration order, which is what
/// <see cref="EpisodeNextActions.For"/> returns, so the wire array is deterministic.
/// </para>
/// </remarks>
public sealed class EpisodeNextActionsTests
{
    /// <summary>
    /// The table of 00 §7, one row per status, in <see cref="EpisodeNextAction"/> declaration order.
    /// The theory below is built from it, so this dictionary is the single source of truth.
    /// </summary>
    private static readonly Dictionary<EpisodeStatus, EpisodeNextAction[]> Expected = new()
    {
        [EpisodeStatus.Draft] = [EpisodeNextAction.GenerateStory, EpisodeNextAction.Cancel],
        [EpisodeStatus.StoryGenerating] = [EpisodeNextAction.Cancel],
        [EpisodeStatus.StoryReview] =
        [
            EpisodeNextAction.EditStory,
            EpisodeNextAction.ApproveStory,
            EpisodeNextAction.ReopenStory,
            EpisodeNextAction.Cancel,
        ],
        [EpisodeStatus.StoryApproved] =
            [EpisodeNextAction.Render, EpisodeNextAction.ReopenStory, EpisodeNextAction.Cancel],
        [EpisodeStatus.Rendering] = [EpisodeNextAction.Cancel],
        [EpisodeStatus.VideoReview] =
        [
            EpisodeNextAction.ApproveVideo,
            EpisodeNextAction.RejectVideo,
            EpisodeNextAction.Download,
            EpisodeNextAction.Cancel,
        ],
        [EpisodeStatus.Ready] =
            [EpisodeNextAction.Publish, EpisodeNextAction.Download, EpisodeNextAction.Cancel],
        [EpisodeStatus.Scheduled] = [EpisodeNextAction.Publish, EpisodeNextAction.Cancel],
        [EpisodeStatus.Published] = [],
        [EpisodeStatus.Failed] = [EpisodeNextAction.RetryRender, EpisodeNextAction.Cancel],
        [EpisodeStatus.Cancelled] = [],
    };

    /// <summary>The same table as the theory's rows; xUnit.v3 reads the property through <c>MemberData</c>.</summary>
    public static TheoryData<EpisodeStatus, EpisodeNextAction[]> Table
    {
        get
        {
            var table = new TheoryData<EpisodeStatus, EpisodeNextAction[]>();

            foreach (var (status, actions) in Expected)
            {
                table.Add(status, actions);
            }

            return table;
        }
    }

    [Fact]
    public void For_Draft_Allows_GenerateStory_And_Cancel()
    {
        EpisodeNextActions.For(EpisodeStatus.Draft).ShouldBe(
            new[] { EpisodeNextAction.GenerateStory, EpisodeNextAction.Cancel });
    }

    [Fact]
    public void For_StoryGenerating_Allows_CancelOnly()
    {
        EpisodeNextActions.For(EpisodeStatus.StoryGenerating).ShouldBe(
            new[] { EpisodeNextAction.Cancel },
            "00 §7 leaves the run itself to the generator; the user's only intent is the cancel edge "
            + "(the command refuses an immediate DELETE while the generator owns the row, D16)");
    }

    [Fact]
    public void For_StoryReview_Allows_ApproveStory_ReopenStory_Cancel()
    {
        EpisodeNextActions.For(EpisodeStatus.StoryReview).ShouldBe(
            new[]
            {
                EpisodeNextAction.EditStory,
                EpisodeNextAction.ApproveStory,
                EpisodeNextAction.ReopenStory,
                EpisodeNextAction.Cancel,
            },
            "story_review is the only state 02.3.1-T1's editor accepts (edit_story), and 00 §7 allows both "
            + "story_review → story_approved (approve) and story_review → story_generating (regenerate)");
    }

    [Fact]
    public void For_StoryApproved_Allows_Render_And_Cancel()
    {
        EpisodeNextActions.For(EpisodeStatus.StoryApproved).ShouldBe(
            new[]
            {
                EpisodeNextAction.Render,
                EpisodeNextAction.ReopenStory,
                EpisodeNextAction.Cancel,
            },
            "00 §7 L341 keeps story_approved → story_review (reopen for edits) beside the render edge");
    }

    [Fact]
    public void For_Rendering_Allows_Cancel()
    {
        EpisodeNextActions.For(EpisodeStatus.Rendering).ShouldBe(
            new[] { EpisodeNextAction.Cancel },
            "00 §7: rendering → story_approved on a failure or a cancel; the renderer owns everything else");
    }

    [Fact]
    public void For_VideoReview_Allows_ApproveVideo_RejectVideo_Cancel()
    {
        EpisodeNextActions.For(EpisodeStatus.VideoReview).ShouldBe(
            new[]
            {
                EpisodeNextAction.ApproveVideo,
                EpisodeNextAction.RejectVideo,
                EpisodeNextAction.Download,
                EpisodeNextAction.Cancel,
            },
            "00 §7 allows video_review → ready (approve) and → story_review | rendering (reject); 10.6.2-T1 "
            + "offers the download of the cut under review, gated on this member");
    }

    [Fact]
    public void For_Ready_Allows_Publish_Cancel()
    {
        EpisodeNextActions.For(EpisodeStatus.Ready).ShouldBe(
            new[]
            {
                EpisodeNextAction.Publish,
                EpisodeNextAction.Download,
                EpisodeNextAction.Cancel,
            },
            "00 §7: ready → scheduled is the publication flow (06.2.1-T1), and the approved video stays "
            + "downloadable (09.7.2-T1's signed URL)");
    }

    [Fact]
    public void For_Scheduled_Allows_Publish_Cancel()
    {
        EpisodeNextActions.For(EpisodeStatus.Scheduled).ShouldBe(
            new[] { EpisodeNextAction.Publish, EpisodeNextAction.Cancel },
            "the publication is in flight (00 §7: ready → scheduled → published); the user can still follow "
            + "it and can cancel the episode");
    }

    [Fact]
    public void For_Published_Allows_None()
    {
        EpisodeNextActions.For(EpisodeStatus.Published).ShouldBeEmpty("published is terminal for the user");
    }

    [Fact]
    public void For_Failed_Allows_RetryRender_And_Cancel()
    {
        EpisodeNextActions.For(EpisodeStatus.Failed).ShouldBe(
            new[] { EpisodeNextAction.RetryRender, EpisodeNextAction.Cancel },
            "00 §7: failed is not terminal; Episode.Cancel accepts it, so the page must offer delete");
    }

    [Fact]
    public void For_Cancelled_Allows_None()
    {
        EpisodeNextActions.For(EpisodeStatus.Cancelled).ShouldBeEmpty("a cancelled row is not readable at all");
    }

    [Fact]
    public void For_UnknownStatus_Empty()
    {
        // Guards the table against a new EpisodeStatus member: an undefined value hides the actions
        // instead of throwing, and this test is the reminder to add its row above (plan §9).
        EpisodeNextActions.For((EpisodeStatus)1_000).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Table_Matches_00_S7(EpisodeStatus status, EpisodeNextAction[] expected)
    {
        EpisodeNextActions.For(status).ShouldBe(expected);
    }

    [Fact]
    public void Table_CoversEveryStatus()
    {
        // The theory cannot notice a status that was never added to the table, so the two sets are
        // compared instead: every declared EpisodeStatus has exactly one row and no row invents one.
        Expected.Keys.OrderBy(status => status).ShouldBe(Enum.GetValues<EpisodeStatus>().OrderBy(status => status));
    }
}
