using ErrorOr;
using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Rendering;

/// <summary>QA negative and boundary checks for <see cref="RenderJob"/> (09.6.1-T1). Written by the QA agent.</summary>
public sealed class RenderJobQaTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid EpisodeId = Guid.CreateVersion7();
    private static readonly Guid StoryVersionId = Guid.CreateVersion7();

    [Theory]
    [InlineData("render_job_id")]
    [InlineData("user_id")]
    [InlineData("episode_id")]
    [InlineData("story_version_id")]
    [InlineData("reservation_id")]
    public void Create_RejectsAnEmptyId_NamingTheField(string field)
    {
        var some = Guid.CreateVersion7();
        var empty = Guid.Empty;

        var result = RenderJob.Create(
            field == "render_job_id" ? empty : some,
            field == "user_id" ? empty : some,
            field == "episode_id" ? empty : some,
            field == "story_version_id" ? empty : some,
            field == "reservation_id" ? empty : some,
            Params(),
            1,
            FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(RenderJob.ValidationFailedCode);
        result.FirstError.Metadata!.ShouldContainKeyAndValue("field", field);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_RejectsAnAttemptBelowOne(int attempt)
    {
        var result = Create(attempt);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(RenderJob.ValidationFailedCode);
    }

    [Fact]
    public void Create_AcceptsARetryAttempt_Two()
    {
        var result = Create(2);

        result.IsError.ShouldBeFalse();
        result.Value.Attempt.ShouldBe(2);
        result.Value.Status.ShouldBe(RenderJobStatus.Queued);
    }

    [Fact]
    public void UpdateProgress_AcceptsTheBoundaries_ZeroAndOneHundred()
    {
        var job = Submitted();

        job.UpdateProgress("voice", 0, FixedNow).IsError.ShouldBeFalse();
        job.UpdateProgress("voice", 100, FixedNow).IsError.ShouldBeFalse();
        job.Percent.ShouldBe((short)100);
    }

    [Theory]
    [InlineData((short)-1)]
    [InlineData((short)101)]
    public void UpdateProgress_RejectsPercentOutsideZeroToHundred(short percent)
    {
        var job = Submitted();

        var result = job.UpdateProgress("voice", percent, FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(RenderJob.ValidationFailedCode);
        job.Percent.ShouldBe((short)0);
    }

    [Fact]
    public void MarkSubmitted_RejectsAnEmptyWorkerNode_AndStaysQueued()
    {
        var job = Create(1).Value;

        var result = job.MarkSubmitted("task-1", Guid.Empty, FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(RenderJob.ValidationFailedCode);
        job.Status.ShouldBe(RenderJobStatus.Queued);
    }

    [Fact]
    public void Fail_OnAQueuedJob_IsInvalidState_AndLeavesNoErrorCode()
    {
        var job = Create(1).Value;

        var result = job.Fail("render_timeout", FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);
        job.Status.ShouldBe(RenderJobStatus.Queued);
        job.ErrorCode.ShouldBeNull();
        job.FinishedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("TTS_FAILED")]
    [InlineData("Render_Timeout")]
    [InlineData("render_timeout ")]
    [InlineData("render-timeout")]
    public void Fail_RejectsCodesOutsideTheFrozenList_AndStaysSubmitted(string code)
    {
        var job = Submitted();

        var result = job.Fail(code, FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(RenderJob.ValidationFailedCode);
        result.FirstError.Metadata!.ShouldContainKeyAndValue("field", "code");
        job.Status.ShouldBe(RenderJobStatus.Submitted);
        job.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public void Fail_NullCode_ThrowsArgumentNullException()
    {
        var job = Submitted();

        Should.Throw<ArgumentNullException>(() => job.Fail(null!, FixedNow));
        job.Status.ShouldBe(RenderJobStatus.Submitted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Fail_BlankCode_ThrowsArgumentException(string code)
    {
        var job = Submitted();

        Should.Throw<ArgumentException>(() => job.Fail(code, FixedNow));
        job.Status.ShouldBe(RenderJobStatus.Submitted);
    }

    private static RenderJob Submitted()
    {
        var job = Create(1).Value;
        job.MarkSubmitted("task-1", Guid.CreateVersion7(), FixedNow).IsError.ShouldBeFalse();
        return job;
    }

    private static ErrorOr<RenderJob> Create(int attempt) =>
        RenderJob.Create(Guid.CreateVersion7(), UserId, EpisodeId, StoryVersionId, Guid.CreateVersion7(), Params(), attempt, FixedNow);

    private static RenderParamsV1 Params() =>
        new(
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
}
