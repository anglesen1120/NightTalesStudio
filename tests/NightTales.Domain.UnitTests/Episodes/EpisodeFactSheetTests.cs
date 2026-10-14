using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research.Events;
using NightTales.Domain.Stories.Generation;
using Shouldly;
using Xunit;

// The test namespace's last segment is `Episodes`, the aggregate's namespace segment, so the entity is
// aliased the way the other episode tests alias it.
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Domain.UnitTests.Episodes;

/// <summary>
/// The fact-sheet announcements of the episode (02.5.4-T1, plan §5): a saved draft and an approved version
/// each raise their own event for the UI's <c>EpisodeUpdated</c>, carry ids and the version number only, and
/// refuse a malformed announcement. The status never moves — the fact sheet is a separate aggregate.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no clock. The F26 state gate belongs to the commands, so these methods add no
/// precondition of their own, exactly as <c>RaiseFactSheetGenerated</c> does for the run path.
/// </remarks>
public sealed class EpisodeFactSheetTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void RaiseFactSheetSaved_FromDraft_RaisesFactSheetSaved()
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);
        var factSheetId = Guid.CreateVersion7();
        episode.ClearDomainEvents();

        var result = episode.RaiseFactSheetSaved(factSheetId, 2, FixedNow);

        result.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.Draft);

        var raised = episode.DomainEvents.OfType<FactSheetSaved>().ShouldHaveSingleItem();
        raised.FactSheetId.ShouldBe(factSheetId);
        raised.Version.ShouldBe(2);
        raised.EpisodeId.ShouldBe(episode.Id);
        raised.SeriesId.ShouldBe(episode.SeriesId);
        raised.UserId.ShouldBe(episode.UserId);
        raised.OccurredAt.ShouldBe(FixedNow);
    }

    [Fact]
    public void RaiseFactSheetApproved_FromDraft_RaisesFactSheetApproved()
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);
        var factSheetId = Guid.CreateVersion7();
        episode.ClearDomainEvents();

        var result = episode.RaiseFactSheetApproved(factSheetId, 2, FixedNow);

        result.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.Draft);

        var raised = episode.DomainEvents.OfType<FactSheetApproved>().ShouldHaveSingleItem();
        raised.FactSheetId.ShouldBe(factSheetId);
        raised.Version.ShouldBe(2);
        raised.EpisodeId.ShouldBe(episode.Id);
        raised.SeriesId.ShouldBe(episode.SeriesId);
        raised.UserId.ShouldBe(episode.UserId);
        raised.OccurredAt.ShouldBe(FixedNow);
    }

    [Theory]
    [InlineData(EpisodeStatus.StoryApproved)]
    [InlineData(EpisodeStatus.Rendering)]
    [InlineData(EpisodeStatus.StoryReview)]
    public void RaiseFactSheetSaved_NeedsNoState(EpisodeStatus status)
    {
        // The row is already stored when the command announces it: an episode that has moved on must not turn
        // a saved edit into a failure (the F26 gate ran before the write).
        var episode = EpisodeIn(status);

        episode.RaiseFactSheetSaved(Guid.CreateVersion7(), 1, FixedNow).IsError.ShouldBeFalse();
        episode.RaiseFactSheetApproved(Guid.CreateVersion7(), 1, FixedNow).IsError.ShouldBeFalse();

        episode.DomainEvents.OfType<FactSheetSaved>().Count().ShouldBe(1);
        episode.DomainEvents.OfType<FactSheetApproved>().Count().ShouldBe(1);
        episode.Status.ShouldBe(status);
    }

    [Fact]
    public void RaiseFactSheetSaved_EmptyFactSheetId_IsAValidationError()
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);
        episode.ClearDomainEvents();

        var result = episode.RaiseFactSheetSaved(Guid.Empty, 1, FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("fact_sheet_id_required");
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        episode.DomainEvents.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RaiseFactSheetSaved_VersionBelowOne_IsAValidationError(int version)
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);
        episode.ClearDomainEvents();

        var result = episode.RaiseFactSheetSaved(Guid.CreateVersion7(), version, FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("fact_sheet_version_invalid");
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        episode.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void RaiseFactSheetApproved_EmptyFactSheetId_IsAValidationError()
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);
        episode.ClearDomainEvents();

        var result = episode.RaiseFactSheetApproved(Guid.Empty, 1, FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("fact_sheet_id_required");
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        episode.DomainEvents.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RaiseFactSheetApproved_VersionBelowOne_IsAValidationError(int version)
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);
        episode.ClearDomainEvents();

        var result = episode.RaiseFactSheetApproved(Guid.CreateVersion7(), version, FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("fact_sheet_version_invalid");
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        episode.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void RaiseFactSheetGenerated_KeepsItsOwnValidation()
    {
        // 02.5.4-T1 shares the id/version validation with this method; the run id stays its own check.
        var episode = EpisodeIn(EpisodeStatus.Draft);
        episode.ClearDomainEvents();

        episode.RaiseFactSheetGenerated(Guid.Empty, Guid.CreateVersion7(), 1, GenerationTrigger.User, FixedNow)
            .FirstError.Code.ShouldBe("generation_run_id_required");
        episode.RaiseFactSheetGenerated(Guid.CreateVersion7(), Guid.Empty, 1, GenerationTrigger.User, FixedNow)
            .FirstError.Code.ShouldBe("fact_sheet_id_required");
        episode.RaiseFactSheetGenerated(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, GenerationTrigger.User, FixedNow)
            .FirstError.Code.ShouldBe("fact_sheet_version_invalid");
        episode.DomainEvents.ShouldBeEmpty();

        episode.RaiseFactSheetGenerated(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, GenerationTrigger.User, FixedNow)
            .IsError.ShouldBeFalse();
        episode.DomainEvents.OfType<FactSheetGenerated>().Count().ShouldBe(1);
    }

    [Fact]
    public void FactSheetSavedAndApproved_HaveTheirOutboxNames()
    {
        // The outbox name is the wire contract the Worker dispatcher resolves the CLR type by.
        typeof(FactSheetSaved).GetCustomAttributes(typeof(OutboxEventAttribute), inherit: false)
            .Cast<OutboxEventAttribute>().Single().Name.ShouldBe("fact_sheet_saved.v1");
        typeof(FactSheetApproved).GetCustomAttributes(typeof(OutboxEventAttribute), inherit: false)
            .Cast<OutboxEventAttribute>().Single().Name.ShouldBe("fact_sheet_approved.v1");
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
    /// Moves the aggregate to <paramref name="status"/> the way 02's transitions will; the factory only ever
    /// produces <see cref="EpisodeStatus.Draft"/>, and the announcement itself is state-free.
    /// </summary>
    private static void SetStatus(EpisodeAggregate episode, EpisodeStatus status) =>
        typeof(EpisodeAggregate)
            .GetProperty(nameof(EpisodeAggregate.Status))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(episode, [status]);
}
