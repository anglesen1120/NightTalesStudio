using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Events;
using Shouldly;
using Xunit;
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Domain.UnitTests.Episodes;

/// <summary>Independent QA boundary probes for the 02.3.2-T1 approve/reopen aggregate methods.</summary>
public sealed class EpisodeApproveReopenQaTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 5, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Approve_EmptyVersionId_RejectedEvenWhenCurrentVersionIsEmpty()
    {
        var episode = ReviewEpisode();
        Set(episode, nameof(episode.CurrentStoryVersionId), Guid.Empty);

        var result = episode.ApproveStory(Guid.Empty, ApprovalSource.User, FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("story_version_conflict");
        episode.Status.ShouldBe(EpisodeStatus.StoryReview);
        episode.ApprovedAt.ShouldBeNull();
        episode.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Approve_EmptySeriesId_IsInvalidStateAndRaisesNothing()
    {
        var episode = ReviewEpisode();
        Set(episode, nameof(episode.SeriesId), Guid.Empty);

        var result = episode.ApproveStory(episode.CurrentStoryVersionId!.Value, ApprovalSource.User, FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(EpisodeAggregate.InvalidStateCode);
        episode.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Approve_StateErrorWinsOverVersionError()
    {
        var episode = ReviewEpisode();
        Set(episode, nameof(episode.Status), EpisodeStatus.Rendering);

        var result = episode.ApproveStory(Guid.NewGuid(), ApprovalSource.User, FixedNow);

        result.FirstError.Code.ShouldBe(EpisodeAggregate.InvalidStateCode);
    }

    [Fact]
    public void Approve_SecondCallOnApprovedAggregate_IsInvalidStateAndKeepsFirstTimestampAndSingleEvent()
    {
        var episode = ReviewEpisode();
        var versionId = episode.CurrentStoryVersionId!.Value;
        episode.ApproveStory(versionId, ApprovalSource.User, FixedNow).IsError.ShouldBeFalse();

        var second = episode.ApproveStory(versionId, ApprovalSource.Autopilot, FixedNow.AddHours(1));

        second.IsError.ShouldBeTrue();
        second.FirstError.Code.ShouldBe(EpisodeAggregate.InvalidStateCode);
        episode.ApprovedAt.ShouldBe(FixedNow);
        episode.DomainEvents.OfType<StoryApproved>().Count().ShouldBe(1);
        episode.DomainEvents.OfType<StoryApproved>().Single().ApprovalSource.ShouldBe(ApprovalSource.User);
    }

    [Fact]
    public void Reopen_ClearsPointerAndTimestamp_KeepsCurrentVersion_AndEventCarriesTheApprovedVersion()
    {
        var episode = ReviewEpisode();
        var versionId = episode.CurrentStoryVersionId!.Value;
        episode.ApproveStory(versionId, ApprovalSource.User, FixedNow).IsError.ShouldBeFalse();
        episode.ClearDomainEvents();

        var result = episode.ReopenStory(FixedNow.AddMinutes(5));

        result.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.StoryReview);
        episode.ApprovedStoryVersionId.ShouldBeNull();
        episode.ApprovedAt.ShouldBeNull();
        episode.CurrentStoryVersionId.ShouldBe(versionId);
        var reopened = episode.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StoryReopened>();
        reopened.StoryVersionId.ShouldBe(versionId);
        reopened.EpisodeId.ShouldBe(episode.Id);
        reopened.SeriesId.ShouldBe(episode.SeriesId);
        reopened.UserId.ShouldBe(episode.UserId);
    }

    [Fact]
    public void Reopen_SecondCall_IsInvalidStateWithoutAnotherEvent()
    {
        var episode = ReviewEpisode();
        episode.ApproveStory(episode.CurrentStoryVersionId!.Value, ApprovalSource.User, FixedNow).IsError.ShouldBeFalse();
        episode.ReopenStory(FixedNow).IsError.ShouldBeFalse();

        var second = episode.ReopenStory(FixedNow.AddMinutes(1));

        second.IsError.ShouldBeTrue();
        second.FirstError.Code.ShouldBe(EpisodeAggregate.InvalidStateCode);
        episode.DomainEvents.OfType<StoryReopened>().Count().ShouldBe(1);
    }

    [Fact]
    public void Reopen_ApprovedWithoutAnyVersionPointer_IsInvalidState()
    {
        var episode = ReviewEpisode();
        Set(episode, nameof(episode.Status), EpisodeStatus.StoryApproved);
        Set(episode, nameof(episode.CurrentStoryVersionId), null);
        Set(episode, nameof(episode.ApprovedStoryVersionId), null);

        var result = episode.ReopenStory(FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(EpisodeAggregate.InvalidStateCode);
        episode.Status.ShouldBe(EpisodeStatus.StoryApproved);
        episode.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Reopen_PrefersTheApprovedPointerOverTheCurrentVersionInTheEvent()
    {
        var episode = ReviewEpisode();
        var approved = Guid.NewGuid();
        Set(episode, nameof(episode.Status), EpisodeStatus.StoryApproved);
        Set(episode, nameof(episode.ApprovedStoryVersionId), approved);

        episode.ReopenStory(FixedNow).IsError.ShouldBeFalse();

        episode.DomainEvents.OfType<StoryReopened>().Single().StoryVersionId.ShouldBe(approved);
    }

    private static EpisodeAggregate ReviewEpisode()
    {
        var episode = EpisodeAggregate.Create(Guid.NewGuid(), Guid.NewGuid(), "Title", "Brief", LengthTier.Standard, FixedNow);
        Set(episode, nameof(episode.Status), EpisodeStatus.StoryReview);
        Set(episode, nameof(episode.CurrentStoryVersionId), Guid.NewGuid());
        episode.ClearDomainEvents();
        return episode;
    }

    private static void Set(EpisodeAggregate episode, string property, object? value) =>
        typeof(EpisodeAggregate).GetProperty(property)!.GetSetMethod(nonPublic: true)!.Invoke(episode, [value]);
}
