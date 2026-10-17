using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Domain.UnitTests.Rendering;

/// <summary>
/// The BD5 state machine of <see cref="RenderJob"/> (09.6.1-T1): creation with the reserved bound, the adapter's
/// submit/progress/fail calls, the poll re-schedule, the two cancellation steps, and the two outbox events the
/// aggregate raises (<c>render_requested.v1</c> from the command, <c>render_failed.v1</c> from the Worker).
/// </summary>
/// <remarks>
/// Every method takes the clock reading as an argument, so the whole file is deterministic. The two episode tests drive
/// the episode half of the same story (BD5's <c>rendering</c> ↔ <c>story_approved</c>/<c>video_review</c> edges) through
/// <see cref="EpisodeAggregate.RenderFailed"/>/<see cref="EpisodeAggregate.RenderSucceeded"/>; the episode is placed in
/// <c>rendering</c> through the property setter, as the neighbouring episode tests do.
/// </remarks>
public sealed class RenderJobTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 16, 9, 30, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid EpisodeId = Guid.CreateVersion7();
    private static readonly Guid StoryVersionId = Guid.CreateVersion7();

    [Fact]
    public void Create_StartsQueued_WithZeroPercent()
    {
        var jobId = Guid.CreateVersion7();
        var reservationId = Guid.CreateVersion7();

        var job = Create(jobId, reservationId);

        job.Id.ShouldBe(jobId);
        job.UserId.ShouldBe(UserId);
        job.EpisodeId.ShouldBe(EpisodeId);
        job.StoryVersionId.ShouldBe(StoryVersionId);
        job.ReservationId.ShouldBe(reservationId);
        job.WorstCaseUsd.ShouldBe(1.25m, "the bound the reservation was taken for is the row's own value");
        job.Status.ShouldBe(RenderJobStatus.Queued);
        job.Stage.ShouldBe(RenderJob.QueuedStage);
        job.Percent.ShouldBe((short)0);
        job.Attempt.ShouldBe(1);
        job.ImagesNeeded.ShouldBe(10);
        job.AdapterTaskId.ShouldBeNull();
        job.WorkerNodeId.ShouldBeNull();
        job.SubmittedAt.ShouldBeNull();
        job.StartedAt.ShouldBeNull();
        job.FinishedAt.ShouldBeNull();
        job.NextPollAt.ShouldBeNull();
        job.CancelRequestedAt.ShouldBeNull();
        job.ErrorCode.ShouldBeNull();
        job.ErrorMessage.ShouldBeNull();
        job.QualityReport.ShouldBeNull();
    }

    [Fact]
    public void MarkSubmitted_FromQueued_Only_ElseInvalidState()
    {
        var job = Create();
        var nodeId = Guid.CreateVersion7();

        var submitted = job.MarkSubmitted("task-1", nodeId, FixedNow);

        submitted.IsError.ShouldBeFalse();
        job.Status.ShouldBe(RenderJobStatus.Submitted);
        job.AdapterTaskId.ShouldBe("task-1");
        job.WorkerNodeId.ShouldBe(nodeId);
        job.SubmittedAt.ShouldBe(FixedNow);
        job.StartedAt.ShouldBe(FixedNow);
        job.NextPollAt.ShouldBe(FixedNow + RenderJob.PollInterval);

        var again = job.MarkSubmitted("task-2", nodeId, FixedNow);

        again.IsError.ShouldBeTrue();
        again.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);
        job.AdapterTaskId.ShouldBe("task-1");
        job.Status.ShouldBe(RenderJobStatus.Submitted);

        var namelessNode = Create().MarkSubmitted("task-3", Guid.Empty, FixedNow);

        namelessNode.IsError.ShouldBeTrue();
        namelessNode.FirstError.Code.ShouldBe(RenderJob.ValidationFailedCode);
    }

    [Fact]
    public void UpdateProgress_NeverDecreases()
    {
        var job = Submitted();

        job.UpdateProgress("voice", 40, FixedNow).IsError.ShouldBeFalse();

        job.Status.ShouldBe(RenderJobStatus.Running);
        job.Stage.ShouldBe("voice");
        job.Percent.ShouldBe((short)40);
        var stamped = job.NextPollAt;
        stamped.ShouldBe(FixedNow + RenderJob.PollInterval);

        var decreasing = job.UpdateProgress("voice", 39, FixedNow + TimeSpan.FromSeconds(5));

        decreasing.IsError.ShouldBeTrue();
        decreasing.FirstError.Code.ShouldBe(RenderJob.ValidationFailedCode);
        decreasing.FirstError.Metadata!["field"].ShouldBe("percent");
        job.Percent.ShouldBe((short)40);
        job.Stage.ShouldBe("voice");
        job.NextPollAt.ShouldBe(stamped);

        var outOfRange = job.UpdateProgress("voice", 101, FixedNow);

        outOfRange.IsError.ShouldBeTrue();
        outOfRange.FirstError.Code.ShouldBe(RenderJob.ValidationFailedCode);
        job.Percent.ShouldBe((short)40);

        var refused = Create().UpdateProgress("voice", 1, FixedNow);

        refused.IsError.ShouldBeTrue();
        refused.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);
    }

    [Fact]
    public void UpdateProgress_Unchanged_IsNoOp()
    {
        var job = Submitted();
        job.UpdateProgress("images", 10, FixedNow).IsError.ShouldBeFalse();

        var stage = job.Stage;
        var percent = job.Percent;
        var stamped = job.NextPollAt;

        var repeat = job.UpdateProgress("images", 10, FixedNow + TimeSpan.FromSeconds(30));

        repeat.IsError.ShouldBeFalse();
        job.Stage.ShouldBe(stage);
        job.Percent.ShouldBe(percent);
        job.NextPollAt.ShouldBe(stamped);

        // The first report of a submitted row is the state change itself (submitted -> running), even when it repeats
        // the queued stage and percent: only a report on an already-running row is the no-op.
        var fresh = Submitted();
        var first = fresh.UpdateProgress(RenderJob.QueuedStage, 0, FixedNow);

        first.IsError.ShouldBeFalse();
        fresh.Status.ShouldBe(RenderJobStatus.Running);
    }

    [Fact]
    public void RequestCancel_ThenCancel()
    {
        var job = Submitted();

        job.RequestCancel(FixedNow).IsError.ShouldBeFalse();
        job.CancelRequestedAt.ShouldBe(FixedNow);
        job.Status.ShouldBe(RenderJobStatus.Submitted);

        job.RequestCancel(FixedNow + TimeSpan.FromMinutes(5)).IsError.ShouldBeFalse();
        job.CancelRequestedAt.ShouldBe(FixedNow);

        var finished = FixedNow + TimeSpan.FromSeconds(20);
        job.Cancel(finished).IsError.ShouldBeFalse();
        job.Status.ShouldBe(RenderJobStatus.Cancelled);
        job.FinishedAt.ShouldBe(finished);
        job.NextPollAt.ShouldBeNull();

        var twice = job.Cancel(FixedNow);
        twice.IsError.ShouldBeTrue();
        twice.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);

        var queued = Create();
        var early = queued.RequestCancel(FixedNow);
        early.IsError.ShouldBeTrue();
        early.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);
        queued.CancelRequestedAt.ShouldBeNull();
        queued.Cancel(FixedNow).IsError.ShouldBeFalse();
        queued.Status.ShouldBe(RenderJobStatus.Cancelled);

        var ingesting = Submitted();
        ingesting.MarkIngesting(FixedNow).IsError.ShouldBeFalse();
        ingesting.NextPollAt.ShouldBeNull();
        var tooLate = ingesting.Cancel(FixedNow);
        tooLate.IsError.ShouldBeTrue();
        tooLate.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);
        ingesting.RequestCancel(FixedNow).FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);

        var notWatched = Create().MarkIngesting(FixedNow);
        notWatched.IsError.ShouldBeTrue();
        notWatched.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);
    }

    [Fact]
    public void Fail_UnknownCode_Rejected()
    {
        var job = Submitted();

        var unknown = job.Fail("render_exploded", FixedNow);

        unknown.IsError.ShouldBeTrue();
        unknown.FirstError.Code.ShouldBe(RenderJob.ValidationFailedCode);
        unknown.FirstError.Metadata!["field"].ShouldBe("code");
        job.Status.ShouldBe(RenderJobStatus.Submitted);
        job.ErrorCode.ShouldBeNull();
        job.FinishedAt.ShouldBeNull();

        job.Fail("render_timeout", FixedNow).IsError.ShouldBeFalse();

        job.Status.ShouldBe(RenderJobStatus.Failed);
        job.ErrorCode.ShouldBe("render_timeout");
        job.ErrorMessage.ShouldBeNull();
        job.FinishedAt.ShouldBe(FixedNow);
        job.NextPollAt.ShouldBeNull();

        var twice = job.Fail("render_node_error", FixedNow);
        twice.IsError.ShouldBeTrue();
        twice.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);
    }

    [Fact]
    public void RenderFailed_ReturnsEpisodeToStoryApproved()
    {
        var episode = RenderingEpisode();

        var failed = episode.RenderFailed(FixedNow);

        failed.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.StoryApproved);
        episode.UpdatedAt.ShouldBe(FixedNow);

        var resumed = episode.RenderFailed(FixedNow + TimeSpan.FromMinutes(1));

        resumed.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.StoryApproved);

        var draft = DraftEpisode();
        var refused = draft.RenderFailed(FixedNow);

        refused.IsError.ShouldBeTrue();
        refused.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);
        draft.Status.ShouldBe(EpisodeStatus.Draft);
    }

    [Fact]
    public void RenderSucceeded_MovesEpisodeToVideoReview()
    {
        var episode = RenderingEpisode();

        var succeeded = episode.RenderSucceeded(FixedNow);

        succeeded.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.VideoReview);
        episode.UpdatedAt.ShouldBe(FixedNow);

        var resumed = episode.RenderSucceeded(FixedNow + TimeSpan.FromSeconds(30));

        resumed.IsError.ShouldBeFalse();
        episode.Status.ShouldBe(EpisodeStatus.VideoReview);

        var approved = DraftEpisode();
        var refused = approved.RenderFailed(FixedNow);
        refused.IsError.ShouldBeTrue();
        refused.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);
    }

    [Fact]
    public void Create_RaisesRenderRequested_WithIdsOnly()
    {
        var jobId = Guid.CreateVersion7();

        var job = Create(jobId);

        var raised = job.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<RenderRequested>();
        raised.RenderJobId.ShouldBe(jobId);
        raised.EpisodeId.ShouldBe(EpisodeId);
        raised.UserId.ShouldBe(UserId);
        raised.OccurredAt.ShouldBe(FixedNow);

        // The payload is ids and one instant (D11/D19): nothing that could carry story content travels with it.
        typeof(RenderRequested).GetProperties().Select(property => property.PropertyType)
            .ShouldAllBe(type => type == typeof(Guid) || type == typeof(DateTimeOffset));
    }

    [Fact]
    public void InvalidStateCode_MatchesTheErrorCatalogue()
    {
        // The Domain may not reference the catalogue (D1), so the strings are spelled out in RenderJob and kept equal
        // to the 09 README D16/D17 codes here.
        RenderJob.InvalidStateCode.ShouldBe(ErrorCodes.EpisodeInvalidState);
        RenderJob.ValidationFailedCode.ShouldBe(ErrorCodes.ValidationFailed);
        RenderJob.FieldMetadataKey.ShouldBe("field");
        RenderJob.QueuedStage.ShouldBe("queued");
    }

    [Fact]
    public void OutcomeCodes_MatchTheFrozenContract()
    {
        // The 05 D5 table (05.4.1-T2 render-outcome.v1) is the single source: the aggregate's set is the frozen list.
        RenderJob.OutcomeCodes.ShouldBe(RenderOutcomeCodes.All);
    }

    [Fact]
    public void ReschedulePoll_MovesTheClaimInstant_OnlyWhileThePollerWatches()
    {
        var job = Submitted();
        job.UpdateProgress("voice", 40, FixedNow).IsError.ShouldBeFalse();
        var later = FixedNow + TimeSpan.FromSeconds(30);

        var again = job.ReschedulePoll(later);

        again.IsError.ShouldBeFalse();
        job.Stage.ShouldBe("voice", "the poll recorded no progress, so nothing but the claim instant moves");
        job.Percent.ShouldBe((short)40);
        job.NextPollAt.ShouldBe(later + RenderJob.PollInterval);

        // The adapter's `deferred` answer on a job nobody reported progress for: the stage stays `queued` and only
        // the next poll moves, which is what keeps the claim query from re-reading the same oldest rows every tick.
        var fresh = Submitted();
        fresh.ReschedulePoll(FixedNow).IsError.ShouldBeFalse();
        fresh.Stage.ShouldBe(RenderJob.QueuedStage);
        fresh.NextPollAt.ShouldBe(FixedNow + RenderJob.PollInterval);

        var queued = Create();
        queued.ReschedulePoll(FixedNow).FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);

        var ingesting = Submitted();
        ingesting.MarkIngesting(FixedNow).IsError.ShouldBeFalse();
        var tooLate = ingesting.ReschedulePoll(FixedNow);
        tooLate.IsError.ShouldBeTrue();
        tooLate.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);
        ingesting.NextPollAt.ShouldBeNull();
    }

    [Fact]
    public void RaiseFailed_IsValidOnlyOnAFailedJob_AndWritesNoState()
    {
        var queued = Create();
        var early = queued.RaiseFailed(final: true);

        early.IsError.ShouldBeTrue();
        early.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);

        // The only event on the job is the one Create raised (BD5 step 10): a queued job has no terminal failure
        // to report, so the refused call must not have added a second event of its own.
        queued.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<RenderRequested>();
        queued.DomainEvents.OfType<RenderFailed>().ShouldBeEmpty();

        var job = Submitted();
        job.Fail("render_node_error", FixedNow).IsError.ShouldBeFalse();
        job.ClearDomainEvents();

        var raised = job.RaiseFailed(final: false);

        raised.IsError.ShouldBeFalse();
        job.Status.ShouldBe(RenderJobStatus.Failed, "the method reports the state Fail decided; it never writes one");
        var failed = job.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<RenderFailed>();
        failed.EpisodeId.ShouldBe(EpisodeId);
        failed.RenderJobId.ShouldBe(job.Id);
        failed.ErrorCode.ShouldBe("render_node_error");
        failed.Attempt.ShouldBe(1);
        failed.Final.ShouldBeFalse();
        failed.OccurredAt.ShouldBe(FixedNow, "the instant Fail stored is the instant the event carries");

        // The payload is ids, the catalogued code, the attempt and the retry verdict (D11/D19): nothing that could
        // carry content, so the outbox row of a failure stays as content-free as the request that started it.
        typeof(RenderFailed).GetProperties().Select(property => property.PropertyType)
            .ShouldAllBe(type => type == typeof(Guid) || type == typeof(string) || type == typeof(int)
                || type == typeof(bool) || type == typeof(DateTimeOffset));
        typeof(RenderFailed).GetCustomAttributes(typeof(OutboxEventAttribute), inherit: false)
            .Cast<OutboxEventAttribute>().Single().Name.ShouldBe("render_failed.v1");
    }

    private static RenderJob Create(Guid? id = null, Guid? reservationId = null, decimal worstCaseUsd = 1.25m) =>
        RenderJob.Create(
            id ?? Guid.CreateVersion7(),
            UserId,
            EpisodeId,
            StoryVersionId,
            reservationId ?? Guid.CreateVersion7(),
            worstCaseUsd,
            Params(),
            1,
            FixedNow).Value;

    private static RenderJob Submitted()
    {
        var job = Create();
        job.MarkSubmitted("task-1", Guid.CreateVersion7(), FixedNow).IsError.ShouldBeFalse();
        return job;
    }

    private static RenderParamsV1 Params() => new(
        RenderParamsV1.CurrentMapperVersion,
        StoryVersionId,
        "landscape",
        "vi",
        "nts-test-voice-vi",
        Guid.CreateVersion7(),
        "style=v1;neg=v1",
        new ImagePlanSummary(6, 10, 12, 10, 0, [], [], "style=v1"),
        new SubtitlesParams(true, "landscape@1"),
        null,
        6);

    private static EpisodeAggregate RenderingEpisode()
    {
        var episode = DraftEpisode();
        SetStatus(episode, EpisodeStatus.Rendering);
        return episode;
    }

    private static EpisodeAggregate DraftEpisode() =>
        EpisodeAggregate.Create(UserId, Guid.CreateVersion7(), null, "Mot tap thu nghiem cho kiem thu.", LengthTier.Standard, FixedNow);

    private static void SetStatus(EpisodeAggregate episode, EpisodeStatus status) =>
        typeof(EpisodeAggregate)
            .GetProperty(nameof(EpisodeAggregate.Status))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(episode, [status]);
}
