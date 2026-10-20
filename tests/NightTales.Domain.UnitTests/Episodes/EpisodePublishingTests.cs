using ErrorOr;
using NightTales.Domain.Episodes;
using Shouldly;
using Xunit;
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Domain.UnitTests.Episodes;

/// <summary>
/// The publishing part of the episode state machine (00 §7, 06 README D12, F15):
/// <see cref="EpisodeAggregate.MarkScheduled"/> moves a <c>ready</c> episode to <c>scheduled</c> in the
/// transaction that consents its publication, is a no-op for an episode already there, and refuses every other
/// state with 409 <c>episode_invalid_state</c>.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no clock, no context. The episode is driven from each state through the
/// reflection setter the neighbouring <c>EpisodeVideoReviewTests</c> uses, because the write path that would reach
/// most of those states is a later story's. 06.2.5-T1 adds <c>MarkPublished</c> beside this method and its own
/// tests in this class.
/// </remarks>
public sealed class EpisodePublishingTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 19, 9, 30, 0, TimeSpan.Zero);

    /// <summary>The publication every call names; the episode deliberately stores no reference to it (D12/F15).</summary>
    private static readonly Guid PublicationId = Guid.Parse("0199a2b3-0000-7000-8000-0000000000a1");

    [Fact]
    public void MarkScheduled_FromReady()
    {
        var episode = EpisodeIn(EpisodeStatus.Ready);
        var scheduledAt = FixedNow.AddMinutes(5);
        var eventCount = episode.DomainEvents.Count;

        var result = episode.MarkScheduled(PublicationId, scheduledAt);

        result.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.Scheduled);
        episode.UpdatedAt.ShouldBe(scheduledAt);

        // 06 README D13 + 06.2.1 `plan.md:228`: the consent's own `publication_updated.v1` already carries both
        // ids, so the episode status move adds no second event of its own.
        episode.DomainEvents.Count.ShouldBe(eventCount);
    }

    [Fact]
    public void MarkScheduled_FromScheduled_IsIdempotent()
    {
        // D12: a second publication of the same episode is a legitimate command — the first may have been
        // cancelled and re-created — and a replayed outbox message must not fail the caller either.
        var episode = EpisodeIn(EpisodeStatus.Scheduled);
        var before = Snapshot(episode);

        var result = episode.MarkScheduled(PublicationId, FixedNow.AddHours(1));

        result.IsError.ShouldBeFalse();
        Snapshot(episode).ShouldBe(before, "a no-op writes no status, no updated_at and no event");
    }

    [Theory]
    [InlineData(EpisodeStatus.Draft)]
    [InlineData(EpisodeStatus.StoryGenerating)]
    [InlineData(EpisodeStatus.StoryReview)]
    [InlineData(EpisodeStatus.StoryApproved)]
    [InlineData(EpisodeStatus.Rendering)]
    [InlineData(EpisodeStatus.VideoReview)]
    [InlineData(EpisodeStatus.Published)]
    [InlineData(EpisodeStatus.Failed)]
    [InlineData(EpisodeStatus.Cancelled)]
    public void MarkScheduled_OtherStates_ReturnEpisodeInvalidStateWithoutMutation(EpisodeStatus status)
    {
        var episode = EpisodeIn(status);

        AssertRejected(episode, EpisodeAggregate.InvalidStateCode,
            () => episode.MarkScheduled(PublicationId, FixedNow.AddMinutes(5)));
    }

    private static EpisodeAggregate EpisodeIn(EpisodeStatus status)
    {
        var episode = EpisodeAggregate.Create(Guid.NewGuid(), Guid.NewGuid(), "Title", "Brief",
            LengthTier.Standard, FixedNow);
        Set(episode, nameof(EpisodeAggregate.Status), status);

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

    private static (EpisodeStatus Status, DateTimeOffset UpdatedAt, int EventCount) Snapshot(EpisodeAggregate episode) =>
        (episode.Status, episode.UpdatedAt, episode.DomainEvents.Count);

    private static void Set(EpisodeAggregate episode, string property, object? value) =>
        typeof(EpisodeAggregate).GetProperty(property)!.GetSetMethod(nonPublic: true)!
            .Invoke(episode, [value]);
}
