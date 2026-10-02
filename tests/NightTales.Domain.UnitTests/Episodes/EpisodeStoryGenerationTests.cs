using NightTales.Application.Common.Errors;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Generation.Events;
using Shouldly;
using Xunit;

// The test namespace's last segment is `Episodes`, the aggregate's namespace segment, so the entity is
// aliased the way EpisodeTests aliases it.
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Domain.UnitTests.Episodes;

/// <summary>
/// The story-generation state machine of the episode (02.2.2 plan §3.3): one named test per transition row
/// T1–T9, plus the idempotency rule (a repeat for the same run is <c>Success</c> with no second event).
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no clock. The counting rule of plan §3.6 lives on the
/// <see cref="GenerationRun"/> the caller writes next to the episode, so the two
/// <c>FailStoryGeneration_*Limit*</c> tests drive both aggregates exactly as <c>StoryGenerationProgress</c> will.
/// </remarks>
public sealed class EpisodeStoryGenerationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 3, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void RequestStoryGeneration_FromDraft_SetsGeneratingAndRaisesRequested()
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);
        var runId = Guid.CreateVersion7();
        episode.ClearDomainEvents();

        var result = episode.RequestStoryGeneration(runId, GenerationTrigger.User, FixedNow);

        result.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.StoryGenerating);
        episode.UpdatedAt.ShouldBe(FixedNow);

        var raised = episode.DomainEvents.OfType<StoryGenerationRequested>().ShouldHaveSingleItem();
        raised.EpisodeId.ShouldBe(episode.Id);
        raised.SeriesId.ShouldBe(episode.SeriesId);
        raised.UserId.ShouldBe(episode.UserId);
        raised.GenerationRunId.ShouldBe(runId);
        raised.Trigger.ShouldBe(GenerationTrigger.User);
        raised.OccurredAt.ShouldBe(FixedNow);
    }

    [Fact]
    public void RequestStoryGeneration_FromStoryReview_KeepsTheOldVersionCurrent()
    {
        var episode = EpisodeIn(EpisodeStatus.StoryReview);
        var oldVersion = Guid.CreateVersion7();
        Set(episode, nameof(EpisodeAggregate.CurrentStoryVersionId), oldVersion);
        episode.ClearDomainEvents();

        var result = episode.RequestStoryGeneration(Guid.CreateVersion7(), GenerationTrigger.Autopilot, FixedNow);

        result.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.StoryGenerating);
        episode.CurrentStoryVersionId.ShouldBe(oldVersion, "the old version stays current until the new one is saved");
        episode.DomainEvents.OfType<StoryGenerationRequested>().ShouldHaveSingleItem()
            .Trigger.ShouldBe(GenerationTrigger.Autopilot);
    }

    [Fact]
    public void RequestStoryGeneration_FromStoryGenerating_ReturnsEpisodeInvalidState()
    {
        var episode = EpisodeIn(EpisodeStatus.StoryGenerating);
        episode.ClearDomainEvents();

        var result = episode.RequestStoryGeneration(Guid.CreateVersion7(), GenerationTrigger.User, FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(EpisodeAggregate.InvalidStateCode);
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
        episode.Status.ShouldBe(EpisodeStatus.StoryGenerating);
        episode.DomainEvents.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(EpisodeStatus.StoryApproved)]
    [InlineData(EpisodeStatus.Rendering)]
    [InlineData(EpisodeStatus.VideoReview)]
    [InlineData(EpisodeStatus.Ready)]
    [InlineData(EpisodeStatus.Scheduled)]
    [InlineData(EpisodeStatus.Published)]
    [InlineData(EpisodeStatus.Failed)]
    [InlineData(EpisodeStatus.Cancelled)]
    public void RequestStoryGeneration_FromStoryApproved_ReturnsEpisodeInvalidState(EpisodeStatus status)
    {
        var episode = EpisodeIn(status);
        episode.ClearDomainEvents();

        var result = episode.RequestStoryGeneration(Guid.CreateVersion7(), GenerationTrigger.User, FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(EpisodeAggregate.InvalidStateCode);
        episode.Status.ShouldBe(status, "a refused transition leaves the status alone");
        episode.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void RequestStoryGeneration_RepeatedForSameRun_IsIdempotent()
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);
        var runId = Guid.CreateVersion7();
        episode.ClearDomainEvents();

        episode.RequestStoryGeneration(runId, GenerationTrigger.User, FixedNow).IsError.ShouldBeFalse();
        var repeat = episode.RequestStoryGeneration(runId, GenerationTrigger.User, FixedNow.AddSeconds(1));

        repeat.IsError.ShouldBeFalse("the at-least-once outbox can deliver the step twice");
        episode.DomainEvents.OfType<StoryGenerationRequested>().ShouldHaveSingleItem();
        episode.UpdatedAt.ShouldBe(FixedNow, "the repeat changed nothing");
    }

    [Fact]
    public void MarkGenerationStage_RaisesStageChanged_AndRepeatedStageRaisesNothing()
    {
        var episode = GeneratingEpisode(out var runId);

        episode.MarkGenerationStage(runId, GenerationStage.Writing, GenerationTrigger.User, FixedNow)
            .IsError.ShouldBeFalse();
        episode.MarkGenerationStage(runId, GenerationStage.Writing, GenerationTrigger.User, FixedNow)
            .IsError.ShouldBeFalse();
        episode.MarkGenerationStage(runId, GenerationStage.Checking, GenerationTrigger.User, FixedNow)
            .IsError.ShouldBeFalse();

        episode.Status.ShouldBe(EpisodeStatus.StoryGenerating, "T5 does not change the status");
        var stages = episode.DomainEvents.OfType<StoryGenerationStageChanged>().ToList();
        stages.Select(stage => stage.Stage).ShouldBe([GenerationStage.Writing, GenerationStage.Checking]);
        stages.ShouldAllBe(stage => stage.GenerationRunId == runId && stage.Trigger == GenerationTrigger.User);
    }

    [Theory]
    [InlineData(EpisodeStatus.Draft)]
    [InlineData(EpisodeStatus.StoryReview)]
    [InlineData(EpisodeStatus.Cancelled)]
    public void MarkGenerationStage_WhenNotGenerating_ReturnsNotGenerating(EpisodeStatus status)
    {
        var episode = EpisodeIn(status);
        episode.ClearDomainEvents();

        var result = episode.MarkGenerationStage(
            Guid.CreateVersion7(),
            GenerationStage.Writing,
            GenerationTrigger.User,
            FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(EpisodeAggregate.NotGeneratingCode);
        episode.DomainEvents.ShouldBeEmpty("T9 raises no event");
        episode.Status.ShouldBe(status);
    }

    [Fact]
    public void CompleteStoryGeneration_SetsStoryReviewAndRaisesGenerated()
    {
        var episode = GeneratingEpisode(out var runId);
        var draft = Guid.CreateVersion7();
        var revised = Guid.CreateVersion7();

        var result = episode.CompleteStoryGeneration(
            runId,
            GenerationTrigger.Autopilot,
            [draft, revised],
            revised,
            FixedNow);

        result.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.StoryReview);
        episode.CurrentStoryVersionId.ShouldBe(revised);

        var generated = episode.DomainEvents.OfType<StoryGenerated>().ShouldHaveSingleItem();
        generated.GenerationRunId.ShouldBe(runId);
        generated.StoryVersionId.ShouldBe(revised);
        generated.Revised.ShouldBeTrue("more than one version was saved");
        generated.Trigger.ShouldBe(GenerationTrigger.Autopilot);
        generated.SeriesId.ShouldBe(episode.SeriesId);
        generated.UserId.ShouldBe(episode.UserId);
    }

    [Fact]
    public void CompleteStoryGeneration_SingleVersion_IsNotRevised_AndARepeatRaisesNothing()
    {
        var episode = GeneratingEpisode(out var runId);
        var only = Guid.CreateVersion7();

        episode.CompleteStoryGeneration(runId, GenerationTrigger.User, [only], only, FixedNow).IsError.ShouldBeFalse();
        var repeat = episode.CompleteStoryGeneration(runId, GenerationTrigger.User, [only], only, FixedNow);

        repeat.IsError.ShouldBeFalse();
        episode.DomainEvents.OfType<StoryGenerated>().ShouldHaveSingleItem().Revised.ShouldBeFalse();
    }

    [Fact]
    public void CompleteStoryGeneration_EmptyVersionList_ReturnsValidationError()
    {
        var episode = GeneratingEpisode(out var runId);

        var result = episode.CompleteStoryGeneration(runId, GenerationTrigger.User, [], Guid.CreateVersion7(), FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        episode.Status.ShouldBe(EpisodeStatus.StoryGenerating);
        episode.DomainEvents.OfType<StoryGenerated>().ShouldBeEmpty();
    }

    [Fact]
    public void CompleteStoryGeneration_CurrentVersionNotInList_ReturnsValidationError()
    {
        var episode = GeneratingEpisode(out var runId);

        var result = episode.CompleteStoryGeneration(
            runId,
            GenerationTrigger.User,
            [Guid.CreateVersion7()],
            Guid.CreateVersion7(),
            FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        episode.Status.ShouldBe(EpisodeStatus.StoryGenerating);
        episode.CurrentStoryVersionId.ShouldBeNull();
    }

    [Fact]
    public void CompleteStoryGeneration_WhenNotGenerating_ReturnsNotGenerating()
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);
        var version = Guid.CreateVersion7();

        var result = episode.CompleteStoryGeneration(
            Guid.CreateVersion7(),
            GenerationTrigger.User,
            [version],
            version,
            FixedNow);

        result.FirstError.Code.ShouldBe(EpisodeAggregate.NotGeneratingCode);
    }

    [Theory]
    [InlineData(EpisodeStatus.Draft)]
    [InlineData(EpisodeStatus.StoryReview)]
    public void FailStoryGeneration_ReturnsToPreviousStatus_NotStoryReview(EpisodeStatus previous)
    {
        // F04 regression: the high-level plan returned every failure to story_review; from draft the
        // episode must be draft again.
        var episode = EpisodeIn(previous);
        var runId = Guid.CreateVersion7();
        episode.RequestStoryGeneration(runId, GenerationTrigger.User, FixedNow);
        episode.ClearDomainEvents();

        var result = episode.FailStoryGeneration(
            runId,
            GenerationTrigger.User,
            previous,
            GenerationErrorCode.StoryGenerationFailed,
            FixedNow);

        result.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(previous);

        var failed = episode.DomainEvents.OfType<StoryGenerationFailed>().ShouldHaveSingleItem();
        failed.ErrorCode.ShouldBe(GenerationErrorCode.StoryGenerationFailed);
        failed.GenerationRunId.ShouldBe(runId);
        failed.UserId.ShouldBe(episode.UserId);
    }

    [Fact]
    public void FailStoryGeneration_StoryModerated_KeepsCountsTowardLimitTrue()
    {
        var (episode, run) = QueuedPair(EpisodeStatus.Draft);
        run.Start(FixedNow);

        // StoryGenerationProgress.FailAsync: the run takes the code and the counting flag, the episode the code.
        run.Fail(GenerationErrorCode.StoryModerated.Value, null, countsTowardLimit: true, FixedNow);
        var result = episode.FailStoryGeneration(
            run.Id,
            run.Trigger,
            run.PreviousEpisodeStatus!.Value,
            GenerationErrorCode.StoryModerated,
            FixedNow);

        result.IsError.ShouldBeFalse();
        run.CountsTowardLimit.ShouldBeTrue("the user's content caused the refusal (plan §3.6)");
        episode.DomainEvents.OfType<StoryGenerationFailed>().ShouldHaveSingleItem()
            .ErrorCode.Value.ShouldBe("story_moderated");
    }

    [Fact]
    public void FailStoryGeneration_StoryInvalidOutput_SetsCountsTowardLimitFalse()
    {
        var (episode, run) = QueuedPair(EpisodeStatus.StoryReview);
        run.Start(FixedNow);

        run.Fail(GenerationErrorCode.StoryInvalidOutput.Value, null, countsTowardLimit: false, FixedNow);
        var result = episode.FailStoryGeneration(
            run.Id,
            run.Trigger,
            run.PreviousEpisodeStatus!.Value,
            GenerationErrorCode.StoryInvalidOutput,
            FixedNow);

        result.IsError.ShouldBeFalse();
        run.CountsTowardLimit.ShouldBeFalse("a platform failure does not cost the user a generation (plan §3.6)");
        episode.Status.ShouldBe(EpisodeStatus.StoryReview);
    }

    [Fact]
    public void FailStoryGeneration_RepeatedForSameRun_IsIdempotent()
    {
        var episode = GeneratingEpisode(out var runId);
        episode.ClearDomainEvents();

        episode.FailStoryGeneration(runId, GenerationTrigger.User, EpisodeStatus.Draft, GenerationErrorCode.StoryInvalidOutput, FixedNow)
            .IsError.ShouldBeFalse();
        var repeat = episode.FailStoryGeneration(
            runId,
            GenerationTrigger.User,
            EpisodeStatus.Draft,
            GenerationErrorCode.StoryInvalidOutput,
            FixedNow);

        repeat.IsError.ShouldBeFalse();
        episode.DomainEvents.OfType<StoryGenerationFailed>().ShouldHaveSingleItem();
    }

    [Fact]
    public void FailStoryGeneration_PreviousStatusOutsideDraftOrReview_ReturnsValidationError()
    {
        var episode = GeneratingEpisode(out var runId);

        var result = episode.FailStoryGeneration(
            runId,
            GenerationTrigger.User,
            EpisodeStatus.Ready,
            GenerationErrorCode.StoryGenerationFailed,
            FixedNow);

        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        episode.Status.ShouldBe(EpisodeStatus.StoryGenerating);
    }

    [Fact]
    public void FailStoryGeneration_WhenNotGenerating_ReturnsNotGenerating()
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);
        episode.ClearDomainEvents();

        var result = episode.FailStoryGeneration(
            Guid.CreateVersion7(),
            GenerationTrigger.User,
            EpisodeStatus.Draft,
            GenerationErrorCode.StoryGenerationFailed,
            FixedNow);

        result.FirstError.Code.ShouldBe(EpisodeAggregate.NotGeneratingCode);
        episode.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void CancelStoryGeneration_ReturnsToPreviousStatus()
    {
        var episode = EpisodeIn(EpisodeStatus.StoryReview);
        var runId = Guid.CreateVersion7();
        episode.RequestStoryGeneration(runId, GenerationTrigger.System, FixedNow);
        episode.ClearDomainEvents();

        var result = episode.CancelStoryGeneration(runId, GenerationTrigger.System, EpisodeStatus.StoryReview, FixedNow);

        result.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.StoryReview);

        var failed = episode.DomainEvents.OfType<StoryGenerationFailed>().ShouldHaveSingleItem();
        failed.ErrorCode.ShouldBe(GenerationErrorCode.StoryGenerationCancelled, "T8 raises Failed with the D8 cancelled code");
        failed.ErrorCode.Value.ShouldBe(ErrorCodes.StoryGenerationCancelled);
        failed.Trigger.ShouldBe(GenerationTrigger.System);
    }

    [Fact]
    public void CancelStoryGeneration_WhenNotGenerating_ReturnsNotGenerating()
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);

        episode.CancelStoryGeneration(Guid.CreateVersion7(), GenerationTrigger.System, EpisodeStatus.Draft, FixedNow)
            .FirstError.Code.ShouldBe(EpisodeAggregate.NotGeneratingCode);
    }

    [Fact]
    public void RunIdRequired_EveryMethodRefusesAnEmptyRunId()
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);

        episode.RequestStoryGeneration(Guid.Empty, GenerationTrigger.User, FixedNow).FirstError.Type
            .ShouldBe(ErrorOr.ErrorType.Validation);
        episode.Status.ShouldBe(EpisodeStatus.Draft);
    }

    [Fact]
    public void NotGeneratingCode_IsNotAnHttpCatalogueCode()
    {
        // T9 is a domain signal the job logs; it must not collide with a catalogued HTTP code.
        EpisodeAggregate.NotGeneratingCode.ShouldNotBe(ErrorCodes.EpisodeInvalidState);
    }

    private static EpisodeAggregate GeneratingEpisode(out Guid runId)
    {
        var episode = EpisodeIn(EpisodeStatus.Draft);
        runId = Guid.CreateVersion7();
        episode.RequestStoryGeneration(runId, GenerationTrigger.User, FixedNow).IsError.ShouldBeFalse();
        episode.ClearDomainEvents();
        return episode;
    }

    /// <summary>An episode in <paramref name="previous"/> that went generating through the run that remembers it.</summary>
    private static (EpisodeAggregate Episode, GenerationRun Run) QueuedPair(EpisodeStatus previous)
    {
        var episode = EpisodeIn(previous);
        var run = GenerationRun.Queue(
                episode.UserId,
                episode.SeriesId,
                episode.Id,
                GenerationKind.Story,
                GenerationTrigger.User,
                "key-1",
                previous,
                null,
                null,
                0.25m,
                FixedNow)
            .Value;

        episode.RequestStoryGeneration(run.Id, run.Trigger, FixedNow).IsError.ShouldBeFalse();
        episode.ClearDomainEvents();

        return (episode, run);
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

        Set(episode, nameof(EpisodeAggregate.Status), status);
        episode.ClearDomainEvents();

        return episode;
    }

    /// <summary>Writes a private-set column, the way the persistence layer materializes a stored row.</summary>
    private static void Set(EpisodeAggregate episode, string property, object value) =>
        typeof(EpisodeAggregate)
            .GetProperty(property)!
            .GetSetMethod(nonPublic: true)!
            .Invoke(episode, [value]);
}
