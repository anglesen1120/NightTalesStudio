using ErrorOr;
using NightTales.Domain.Assets;
using NightTales.Domain.Episodes;
using NightTales.Domain.Episodes.Events;
using Shouldly;
using Xunit;
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Domain.UnitTests.Episodes;

/// <summary>
/// The video-review transitions of 09.7.1-T1 (<c>video_review → ready</c> and
/// <c>video_review → story_review | story_approved</c>, WT3) plus <see cref="Asset.ScheduleRetention"/>
/// (WT56). The episode is driven from every state through the reflection setter the neighbouring
/// <c>EpisodeApproveReopenTests</c> uses, because the write path that would reach those states is a later
/// story's. The SEC-1/SEC-2 pins also cover the refusal of a NUL in the reason and the clearing of the
/// approval pointer on the <c>story</c> target (both mirroring <c>ReopenStory</c>).
/// </summary>
public sealed class EpisodeVideoReviewTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 18, 9, 30, 0, TimeSpan.Zero);

    /// <summary>The lowercase SHA-256 hex of an arbitrary 64-character payload (<c>char(64)</c>).</summary>
    private static readonly string Sha256Hex = new('a', 64);

    [Fact]
    public void ApproveVideo_FromVideoReview_SetsReadyAndReadyAt()
    {
        var assetId = Guid.NewGuid();
        var episode = EpisodeIn(EpisodeStatus.VideoReview, assetId);
        var approvedAt = FixedNow.AddMinutes(1);
        var eventCount = episode.DomainEvents.Count;

        episode.ApproveVideo(approvedAt).IsError.ShouldBeFalse();

        episode.Status.ShouldBe(EpisodeStatus.Ready);
        episode.ReadyAt.ShouldBe(approvedAt);
        episode.VideoRejectReason.ShouldBeNull();
        episode.FinalVideoAssetId.ShouldBe(assetId);
        episode.UpdatedAt.ShouldBe(approvedAt);
        episode.DomainEvents.Count.ShouldBe(eventCount + 1);
        var approved = episode.DomainEvents.OfType<VideoApproved>().ShouldHaveSingleItem();
        approved.EpisodeId.ShouldBe(episode.Id);
        approved.SeriesId.ShouldBe(episode.SeriesId);
        approved.UserId.ShouldBe(episode.UserId);
        approved.AssetId.ShouldBe(assetId);
        approved.OccurredAt.ShouldBe(approvedAt);
    }

    [Fact]
    public void ApproveVideo_WithoutFinalVideoAsset_RaisesEventWithNullAssetId()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview);

        episode.ApproveVideo(FixedNow.AddMinutes(1)).IsError.ShouldBeFalse();

        episode.Status.ShouldBe(EpisodeStatus.Ready);
        episode.DomainEvents.OfType<VideoApproved>().ShouldHaveSingleItem().AssetId.ShouldBeNull();
    }

    [Theory]
    [InlineData(EpisodeStatus.Draft)]
    [InlineData(EpisodeStatus.StoryGenerating)]
    [InlineData(EpisodeStatus.StoryReview)]
    [InlineData(EpisodeStatus.StoryApproved)]
    [InlineData(EpisodeStatus.Rendering)]
    [InlineData(EpisodeStatus.Ready)]
    [InlineData(EpisodeStatus.Scheduled)]
    [InlineData(EpisodeStatus.Published)]
    [InlineData(EpisodeStatus.Failed)]
    [InlineData(EpisodeStatus.Cancelled)]
    public void ApproveVideo_FromDraftOrReady_ReturnsEpisodeInvalidState(EpisodeStatus status)
    {
        var episode = EpisodeIn(status, Guid.NewGuid());

        AssertRejected(episode, EpisodeAggregate.InvalidStateCode,
            () => episode.ApproveVideo(FixedNow.AddMinutes(1)));
    }

    [Fact]
    public void ApproveVideo_AfterApproval_ReturnsEpisodeInvalidState()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());
        episode.ApproveVideo(FixedNow.AddMinutes(1)).IsError.ShouldBeFalse();

        AssertRejected(episode, EpisodeAggregate.InvalidStateCode,
            () => episode.ApproveVideo(FixedNow.AddMinutes(2)));
    }

    [Fact]
    public void RejectVideo_ToStoryTarget_MovesToStoryReview()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());
        var rejectedAt = FixedNow.AddMinutes(1);
        var eventCount = episode.DomainEvents.Count;

        episode.RejectVideo("Speech is buried under the music", RejectTarget.Story, rejectedAt)
            .IsError.ShouldBeFalse();

        episode.Status.ShouldBe(EpisodeStatus.StoryReview);
        episode.VideoRejectReason.ShouldBe("Speech is buried under the music");
        episode.ReadyAt.ShouldBeNull();
        episode.UpdatedAt.ShouldBe(rejectedAt);
        episode.DomainEvents.Count.ShouldBe(eventCount + 1);
        var rejected = episode.DomainEvents.OfType<VideoRejected>().ShouldHaveSingleItem();
        rejected.EpisodeId.ShouldBe(episode.Id);
        rejected.SeriesId.ShouldBe(episode.SeriesId);
        rejected.UserId.ShouldBe(episode.UserId);
        rejected.Target.ShouldBe(RejectTarget.Story);
        rejected.OccurredAt.ShouldBe(rejectedAt);
    }

    [Fact]
    public void RejectVideo_ToReRender_MovesToStoryApproved()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());
        var rejectedAt = FixedNow.AddMinutes(1);
        var eventCount = episode.DomainEvents.Count;

        episode.RejectVideo("The cut is wrong", RejectTarget.ReRender, rejectedAt).IsError.ShouldBeFalse();

        episode.Status.ShouldBe(EpisodeStatus.StoryApproved);
        episode.VideoRejectReason.ShouldBe("The cut is wrong");
        episode.ReadyAt.ShouldBeNull();
        episode.UpdatedAt.ShouldBe(rejectedAt);
        episode.DomainEvents.Count.ShouldBe(eventCount + 1);
        episode.DomainEvents.OfType<VideoRejected>().ShouldHaveSingleItem()
            .Target.ShouldBe(RejectTarget.ReRender);
    }

    [Fact]
    public void RejectVideo_StoresReasonAndTrimsIt()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());

        episode.RejectVideo("  pacing drags in act two \t", RejectTarget.Story, FixedNow.AddMinutes(1))
            .IsError.ShouldBeFalse();

        episode.VideoRejectReason.ShouldBe("pacing drags in act two");
        episode.DomainEvents.OfType<VideoRejected>().ShouldHaveSingleItem()
            .Target.ShouldBe(RejectTarget.Story);
        // The reason text never leaves the aggregate: the outbox payload carries ids and the target only.
        typeof(VideoRejected).GetProperties().Select(property => property.Name)
            .ShouldNotContain("Reason");
        typeof(VideoRejected).GetProperties().Select(property => property.Name)
            .ShouldNotContain("VideoRejectReason");
    }

    [Fact]
    public void RejectVideo_ToStoryTarget_ClearsTheApprovalPointerAndStamp()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());
        Set(episode, nameof(EpisodeAggregate.ApprovedStoryVersionId), Guid.NewGuid());
        Set(episode, nameof(EpisodeAggregate.ApprovedAt), FixedNow.AddMinutes(-30));

        episode.RejectVideo("Send it back for another edit", RejectTarget.Story, FixedNow.AddMinutes(1))
            .IsError.ShouldBeFalse();

        episode.Status.ShouldBe(EpisodeStatus.StoryReview);
        // SEC-2: entering `story_review` clears the approval pointer exactly as `ReopenStory` does, so a
        // consumer that keys on the pointer alone cannot read a version that is under review again.
        episode.ApprovedStoryVersionId.ShouldBeNull();
        episode.ApprovedAt.ShouldBeNull();
    }

    [Fact]
    public void RejectVideo_ToReRender_KeepsTheApprovalPointerAndStamp()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());
        var approvedVersionId = Guid.NewGuid();
        var approvedAt = FixedNow.AddMinutes(-30);
        Set(episode, nameof(EpisodeAggregate.ApprovedStoryVersionId), approvedVersionId);
        Set(episode, nameof(EpisodeAggregate.ApprovedAt), approvedAt);

        episode.RejectVideo("Render it again from the same story", RejectTarget.ReRender, FixedNow.AddMinutes(1))
            .IsError.ShouldBeFalse();

        episode.Status.ShouldBe(EpisodeStatus.StoryApproved);
        // SEC-2: a re-render renders the approved story again, so its pointer and stamp are untouched.
        episode.ApprovedStoryVersionId.ShouldBe(approvedVersionId);
        episode.ApprovedAt.ShouldBe(approvedAt);
    }

    [Fact]
    public void RejectVideo_ReasonContainingNul_ReturnsValidationWithoutMutation()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());
        Set(episode, nameof(EpisodeAggregate.ApprovedStoryVersionId), Guid.NewGuid());
        Set(episode, nameof(EpisodeAggregate.ApprovedAt), FixedNow.AddMinutes(-30));

        // SEC-1: PostgreSQL refuses a NUL in the varchar column (22021), so the aggregate refuses it here
        // rather than letting the write surface as an unhandled 500.
        var result = AssertRejected(episode, "video_reject_reason_invalid",
            () => episode.RejectVideo("dark\u0000cut", RejectTarget.Story, FixedNow.AddMinutes(1)));

        result.FirstError.Type.ShouldBe(ErrorType.Validation);
        episode.VideoRejectReason.ShouldBeNull();
    }

    [Fact]
    public void RejectVideo_ReasonOf500Characters_IsAccepted()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());
        var reason = new string('x', 500);

        episode.RejectVideo(reason, RejectTarget.Story, FixedNow.AddMinutes(1)).IsError.ShouldBeFalse();

        episode.VideoRejectReason.ShouldBe(reason);
        episode.VideoRejectReason!.Length.ShouldBe(500);
    }

    [Fact]
    public void ApproveVideo_KeepsTheLastRejectionReason()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());
        episode.RejectVideo("First attempt was out of focus", RejectTarget.ReRender, FixedNow.AddMinutes(1))
            .IsError.ShouldBeFalse();
        Set(episode, nameof(EpisodeAggregate.Status), EpisodeStatus.VideoReview);

        episode.ApproveVideo(FixedNow.AddMinutes(2)).IsError.ShouldBeFalse();

        episode.Status.ShouldBe(EpisodeStatus.Ready);
        episode.VideoRejectReason.ShouldBe("First attempt was out of focus");
    }

    [Theory]
    [InlineData(EpisodeStatus.Draft)]
    [InlineData(EpisodeStatus.StoryGenerating)]
    [InlineData(EpisodeStatus.StoryReview)]
    [InlineData(EpisodeStatus.StoryApproved)]
    [InlineData(EpisodeStatus.Rendering)]
    [InlineData(EpisodeStatus.Ready)]
    [InlineData(EpisodeStatus.Scheduled)]
    [InlineData(EpisodeStatus.Published)]
    [InlineData(EpisodeStatus.Failed)]
    [InlineData(EpisodeStatus.Cancelled)]
    public void RejectVideo_WrongState_ReturnsEpisodeInvalidStateWithoutMutation(EpisodeStatus status)
    {
        var episode = EpisodeIn(status, Guid.NewGuid());

        AssertRejected(episode, EpisodeAggregate.InvalidStateCode,
            () => episode.RejectVideo("Anything", RejectTarget.Story, FixedNow.AddMinutes(1)));
    }

    [Fact]
    public void RejectVideo_AfterRejection_ReturnsEpisodeInvalidStateWithoutSecondEvent()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());
        episode.RejectVideo("First", RejectTarget.Story, FixedNow.AddMinutes(1)).IsError.ShouldBeFalse();

        AssertRejected(episode, EpisodeAggregate.InvalidStateCode,
            () => episode.RejectVideo("Second", RejectTarget.ReRender, FixedNow.AddMinutes(2)));

        episode.DomainEvents.OfType<VideoRejected>().ShouldHaveSingleItem().Target.ShouldBe(RejectTarget.Story);
        episode.VideoRejectReason.ShouldBe("First");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t\r\n")]
    public void RejectVideo_BlankReason_ReturnsValidationWithoutMutation(string reason)
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());

        var result = AssertRejected(episode, "video_reject_reason_invalid",
            () => episode.RejectVideo(reason, RejectTarget.Story, FixedNow.AddMinutes(1)));

        result.FirstError.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void RejectVideo_TooLongReason_ReturnsValidationWithoutMutation()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());
        var reason = new string('x', 501);

        var result = AssertRejected(episode, "video_reject_reason_invalid",
            () => episode.RejectVideo(reason, RejectTarget.Story, FixedNow.AddMinutes(1)));

        result.FirstError.Type.ShouldBe(ErrorType.Validation);
        episode.VideoRejectReason.ShouldBeNull();
    }

    [Fact]
    public void RejectVideo_UndefinedTarget_ReturnsValidationWithoutMutation()
    {
        var episode = EpisodeIn(EpisodeStatus.VideoReview, Guid.NewGuid());

        var result = AssertRejected(episode, "video_reject_target_invalid",
            () => episode.RejectVideo("A reason", (RejectTarget)99, FixedNow.AddMinutes(1)));

        result.FirstError.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void ScheduleRetention_ReStampsOverAnExistingValue()
    {
        var created = Asset.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AssetKind.Video,
            "media/episode/cut.mp4", "video/mp4", 1_024, 12.5m, Sha256Hex,
            FixedNow.AddDays(1), FixedNow);
        created.IsError.ShouldBeFalse();
        var asset = created.Value;
        asset.RetentionUntil.ShouldBe(FixedNow.AddDays(1));

        var later = FixedNow.AddDays(30);
        asset.ScheduleRetention(later);
        asset.RetentionUntil.ShouldBe(later);

        // The overwrite is unconditional: an earlier instant replaces a later one too (WT56).
        var earlier = FixedNow.AddHours(2);
        asset.ScheduleRetention(earlier);
        asset.RetentionUntil.ShouldBe(earlier);
    }

    private static EpisodeAggregate EpisodeIn(EpisodeStatus status, Guid? finalVideoAssetId = null)
    {
        var episode = EpisodeAggregate.Create(Guid.NewGuid(), Guid.NewGuid(), "Title", "Brief",
            LengthTier.Standard, FixedNow);
        Set(episode, nameof(EpisodeAggregate.Status), status);
        Set(episode, nameof(EpisodeAggregate.CurrentStoryVersionId), Guid.NewGuid());
        if (finalVideoAssetId is not null)
        {
            Set(episode, nameof(EpisodeAggregate.FinalVideoAssetId), finalVideoAssetId);
        }

        return episode;
    }

    private static ErrorOr<Success> AssertRejected(
        EpisodeAggregate episode,
        string code,
        Func<ErrorOr<Success>> action)
    {
        var before = Snapshot(episode);

        var result = action();

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(code);
        Snapshot(episode).ShouldBe(before);

        return result;
    }

    private static (EpisodeStatus Status, DateTimeOffset? ReadyAt, string? VideoRejectReason,
        Guid? FinalVideoAssetId, Guid? ApprovedStoryVersionId, DateTimeOffset? ApprovedAt,
        DateTimeOffset UpdatedAt, int EventCount) Snapshot(EpisodeAggregate episode) =>
        (episode.Status, episode.ReadyAt, episode.VideoRejectReason, episode.FinalVideoAssetId,
            episode.ApprovedStoryVersionId, episode.ApprovedAt, episode.UpdatedAt, episode.DomainEvents.Count);

    private static void Set(EpisodeAggregate episode, string property, object? value) =>
        typeof(EpisodeAggregate).GetProperty(property)!.GetSetMethod(nonPublic: true)!
            .Invoke(episode, [value]);
}
