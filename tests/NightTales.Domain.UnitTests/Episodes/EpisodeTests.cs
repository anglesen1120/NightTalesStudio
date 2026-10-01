using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Episodes.Events;
using Shouldly;
using Xunit;

// The test namespace's last segment is `Episodes`, the aggregate's namespace segment, so the entity is
// aliased the way the series tests alias theirs (09.4.1-T1).
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Domain.UnitTests.Episodes;

/// <summary>
/// The behaviour of the <c>episode</c> aggregate (09.5.1 plan §3, 09 README D16): the factory and its
/// event, and the four states <c>Cancel</c> refuses against the seven it accepts.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no clock, no context. The wire checks (is the brief 1–1,000 characters,
/// is the title ≤ 120, is the length tier defined, does the series exist and is it archived) belong to the
/// Application validator and handler, so they are asserted there.
/// </remarks>
public sealed class EpisodeTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Episode_Create_StartsInDraft_RaisesEpisodeCreated()
    {
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var episode = EpisodeAggregate.Create(
            userId,
            seriesId,
            "  Tiêu đề  ",
            "Nội dung tập thử nghiệm.",
            LengthTier.Long,
            FixedNow);

        episode.Id.ShouldNotBe(Guid.Empty);
        episode.UserId.ShouldBe(userId);
        episode.SeriesId.ShouldBe(seriesId);
        episode.Title.ShouldBe("  Tiêu đề  ", "the caller trims; the factory stores what it was given");
        episode.Brief.ShouldBe("Nội dung tập thử nghiệm.");
        episode.LengthTier.ShouldBe(LengthTier.Long);
        episode.Status.ShouldBe(EpisodeStatus.Draft);
        episode.CurrentStoryVersionId.ShouldBeNull();
        episode.ApprovedStoryVersionId.ShouldBeNull();
        episode.FinalVideoAssetId.ShouldBeNull();
        episode.CreatedAt.ShouldBe(FixedNow);
        episode.UpdatedAt.ShouldBe(FixedNow);

        var created = episode.DomainEvents.OfType<EpisodeCreated>().ShouldHaveSingleItem();
        created.EpisodeId.ShouldBe(episode.Id);
        created.SeriesId.ShouldBe(seriesId);
        created.UserId.ShouldBe(userId);
        created.OccurredAt.ShouldBe(FixedNow);
    }

    [Fact]
    public void Episode_Create_WithoutTitle_StoresNull()
    {
        var episode = EpisodeAggregate.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            "Nội dung",
            LengthTier.Short,
            FixedNow);

        episode.Title.ShouldBeNull();
        episode.LengthTier.ShouldBe(LengthTier.Short);
    }

    [Theory]
    [InlineData(EpisodeStatus.StoryGenerating)]
    [InlineData(EpisodeStatus.Rendering)]
    [InlineData(EpisodeStatus.Published)]
    [InlineData(EpisodeStatus.Cancelled)]
    public void Episode_Cancel_FromBlockedStates_ReturnsInvalidState(EpisodeStatus status)
    {
        var episode = EpisodeIn(status);

        var result = episode.Cancel(FixedNow.AddMinutes(1));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("episode_invalid_state");
        result.FirstError.Code.ShouldBe(EpisodeAggregate.InvalidStateCode);
        episode.Status.ShouldBe(status, "a refused cancel must leave the row as it was");
        episode.UpdatedAt.ShouldBe(FixedNow, "a refused cancel must not move the stamp");
        episode.DomainEvents.OfType<EpisodeCancelled>().ShouldBeEmpty("a refused cancel raises no event");
    }

    [Theory]
    [InlineData(EpisodeStatus.Draft)]
    [InlineData(EpisodeStatus.StoryReview)]
    [InlineData(EpisodeStatus.StoryApproved)]
    [InlineData(EpisodeStatus.VideoReview)]
    [InlineData(EpisodeStatus.Ready)]
    [InlineData(EpisodeStatus.Scheduled)]
    [InlineData(EpisodeStatus.Failed)]
    public void Episode_Cancel_FromAllowedStates_RaisesEventWithPreviousStatus(EpisodeStatus status)
    {
        var episode = EpisodeIn(status);
        var cancelledAt = FixedNow.AddMinutes(1);

        var result = episode.Cancel(cancelledAt);

        result.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.Cancelled);
        episode.UpdatedAt.ShouldBe(cancelledAt);

        var cancelled = episode.DomainEvents.OfType<EpisodeCancelled>().ShouldHaveSingleItem();
        cancelled.EpisodeId.ShouldBe(episode.Id);
        cancelled.SeriesId.ShouldBe(episode.SeriesId);
        cancelled.UserId.ShouldBe(episode.UserId);
        cancelled.PreviousStatus.ShouldBe(status);
        cancelled.OccurredAt.ShouldBe(cancelledAt);
    }

    [Fact]
    public void Episode_Cancel_Twice_SecondIsRefusedAsInvalidState()
    {
        // Cancel is deliberately not idempotent (D16 lists `Cancelled` among the refused states): the
        // route answers 404 for an already cancelled episode, and the aggregate is where that starts.
        var episode = EpisodeIn(EpisodeStatus.Draft);
        episode.Cancel(FixedNow.AddMinutes(1)).IsError.ShouldBeFalse();

        var second = episode.Cancel(FixedNow.AddMinutes(2));

        second.IsError.ShouldBeTrue();
        second.FirstError.Code.ShouldBe(EpisodeAggregate.InvalidStateCode);
        episode.DomainEvents.OfType<EpisodeCancelled>().Count().ShouldBe(1);
    }

    [Fact]
    public void Episode_InvalidStateCode_MatchesTheErrorCatalogue()
    {
        // The Domain cannot reference Application/Common/Errors/ErrorCodes.cs (D1: ErrorOr is its only
        // package), so it spells the code itself; the HTTP status that code maps to is the catalogue's.
        // This test is the join between the two, and it is why a rename cannot slip through unnoticed.
        EpisodeAggregate.InvalidStateCode.ShouldBe(ErrorCodes.EpisodeInvalidState);
    }

    private static EpisodeAggregate EpisodeIn(EpisodeStatus status)
    {
        var episode = EpisodeAggregate.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Tiêu đề",
            "Nội dung",
            LengthTier.Standard,
            FixedNow);

        SetStatus(episode, status);

        return episode;
    }

    /// <summary>
    /// Moves the aggregate to <paramref name="status"/> the way 02's transitions will; the factory only
    /// ever produces <see cref="EpisodeStatus.Draft"/>, so the state matrix of <c>Cancel</c> cannot be
    /// exercised without writing the column.
    /// </summary>
    private static void SetStatus(EpisodeAggregate episode, EpisodeStatus status) =>
        typeof(EpisodeAggregate)
            .GetProperty(nameof(EpisodeAggregate.Status))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(episode, [status]);
}
