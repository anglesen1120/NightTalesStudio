using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The <c>scheduled</c> step (02.8.3 plan §3.5 rows <c>scheduled → published</c>,
/// <c>scheduled → published_private_locked</c> and the failure rows, §7.3 and §7.4, S71-19): the publication row is
/// the signal, so the step ends the run with the outcome the row reports.
/// </summary>
/// <remarks>
/// The upload job that moves the row is 06.2.3-T2's and is covered there; these rows pin what the run does with each
/// state it can find. Graph: the worktree is not indexed.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AutopilotScheduledStepTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = AutopilotStepContexts.DefaultNow;

    /// <summary>A published row ends the run <c>published</c> and stamps <c>finished_at</c>.</summary>
    [Fact]
    public async Task Scheduled_Published_EndsPublished()
    {
        var (run, step) = await ArrangeAsync(PublicationStatus.Published);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.Published);
        run.Run.State.ShouldBe(AutopilotRunState.Published);
        run.Run.Outcome.ShouldBe(AutopilotRunOutcome.Published);
        run.Run.FinishedAt.ShouldBe(Now);
    }

    /// <summary>A row published while the project is pre-audit stays private: the run ends with the locked outcome (plan §7.4).</summary>
    [Fact]
    public async Task Scheduled_PublishedPreAudit_EndsPublishedPrivateLocked()
    {
        var (run, step) = await ArrangeAsync(PublicationStatus.Published, preAudit: true);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Code.ShouldBe(AutopilotStepCodes.PublishedPrivateLocked);
        run.Run.Outcome.ShouldBe(AutopilotRunOutcome.PublishedPrivateLocked);
    }

    /// <summary>The upload job has not finished: the tick leaves the run alone.</summary>
    [Fact]
    public async Task Scheduled_StillProcessing_IsIdle()
    {
        var (run, step) = await ArrangeAsync(PublicationStatus.Processing);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Idle);
        run.Run.State.ShouldBe(AutopilotRunState.Scheduled);
    }

    /// <summary>The day's quota is a catalogued failure: the run ends with the closed code (plan §3.6 row 8).</summary>
    [Fact]
    public async Task Scheduled_QuotaExhausted_EndsWithTheClosedCode()
    {
        var (run, step) = await ArrangeAsync(
            PublicationStatus.Failed,
            errorCode: YouTubeErrorCodes.QuotaExceeded);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.YoutubeQuotaExhausted);
        run.Run.State.ShouldBe(AutopilotRunState.Failed);
    }

    /// <summary>A cancelled row is a failure at this stage: the go-live never happened.</summary>
    [Fact]
    public async Task Scheduled_Cancelled_EndsFailedWithPublicationCancelled()
    {
        var (run, step) = await ArrangeAsync(PublicationStatus.Cancelled);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.PublicationCancelled);
        run.Run.State.ShouldBe(AutopilotRunState.Failed);
    }

    /// <summary>A run in <c>scheduled</c> without the row it tracks has nothing to follow, so the tick retries.</summary>
    [Fact]
    public async Task Scheduled_NoPublicationAttached_Retries()
    {
        var series = await SeriesAsync();
        var run = AutopilotRuns.WalkTo(series, AutopilotRunState.Scheduled, Now, withPublication: false);
        var step = new ScheduledStep(new FakeStepReader(), NullLogger<ScheduledStep>.Instance);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run, series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Retry);
        result.Code.ShouldBe(AutopilotStepCodes.PublicationMissing);
    }

    private async Task<SeriesAggregate> SeriesAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;

        return await new SeriesBuilder(Database, actor).CreateAsync(user.Id, autopilotEnabled: true, ct: Ct);
    }

    private async Task<(RunAndSeries Run, ScheduledStep Step)> ArrangeAsync(
        PublicationStatus status,
        string? errorCode = null,
        bool preAudit = false)
    {
        var series = await SeriesAsync();
        var publicationId = Guid.CreateVersion7();
        var run = AutopilotRuns.WalkTo(
            series,
            AutopilotRunState.Scheduled,
            Now,
            publicationId: publicationId);

        var reader = new FakeStepReader
        {
            Publication = new AutopilotPublicationSnapshot(
                publicationId,
                status,
                CreatedAt: Now.AddHours(-26),
                ConsentAt: Now.AddHours(-25),
                VetoNoticeSentAt: Now.AddHours(-26),
                VetoEndsAt: Now.AddHours(-25),
                ErrorCode: errorCode,
                PreAudit: preAudit),
        };

        return (new RunAndSeries(run, series), new ScheduledStep(reader, NullLogger<ScheduledStep>.Instance));
    }

    private sealed record RunAndSeries(AutopilotRun Run, SeriesAggregate Series);
}
