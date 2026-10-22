using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
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
/// The <c>veto_window</c> step (02.8.3 plan §3.5 rows <c>publication_created → veto_window</c>,
/// <c>veto_window → scheduled</c> and <c>veto_window → cancelled_by_owner</c>, S71-19): it never consents on the
/// owner's behalf, it follows the publication row, and a failure ends the run with the row's own code.
/// </summary>
/// <remarks>
/// The consent write belongs to 06.2.2-T3, so the seam is a fake: the step's own decision — wait, follow, or end —
/// is what these rows pin. Graph: the worktree is not indexed.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AutopilotVetoWindowStepTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = AutopilotStepContexts.DefaultNow;

    /// <summary>While the row is <c>awaiting_consent</c> and the window has not been consented, the tick leaves it alone.</summary>
    [Fact]
    public async Task Veto_AwaitingConsent_IsIdle()
    {
        var (run, step, consent) = await ArrangeAsync(PublicationStatus.AwaitingConsent, consented: false);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Idle);
        run.Run.State.ShouldBe(AutopilotRunState.VetoWindow);
        consent.Calls.ShouldBe(1);
    }

    /// <summary>The written consent is the go-live signal: the run records the consent step and moves to <c>scheduled</c>.</summary>
    [Fact]
    public async Task Veto_ConsentWritten_MovesToScheduled()
    {
        var (run, step, consent) = await ArrangeAsync(PublicationStatus.AwaitingConsent, consented: true);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Advanced);
        result.Code.ShouldBe(AutopilotRunStep.Consent);
        run.Run.State.ShouldBe(AutopilotRunState.Scheduled);
        consent.Calls.ShouldBe(1);
    }

    /// <summary>
    /// A row already past the window was consented by whoever moved it, so the run follows it without asking the
    /// consent seam again (recovery from a lost tick).
    /// </summary>
    [Fact]
    public async Task Veto_AlreadyScheduled_FollowsTheRowWithoutAskingConsent()
    {
        var (run, step, consent) = await ArrangeAsync(PublicationStatus.Scheduled, consented: false);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Advanced);
        run.Run.State.ShouldBe(AutopilotRunState.Scheduled);
        consent.Calls.ShouldBe(0, "a scheduled row cannot precede its consent");
    }

    /// <summary>The owner's veto inside the window ends the run <c>cancelled_by_owner</c>: a decision, not a failure.</summary>
    [Fact]
    public async Task Veto_CancelledInsideTheWindow_EndsCancelledByOwner()
    {
        var (run, step, _) = await ArrangeAsync(PublicationStatus.Cancelled, consented: false);

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.CancelledByOwner);
        run.Run.State.ShouldBe(AutopilotRunState.CancelledByOwner);
        run.Run.Outcome.ShouldBe(AutopilotRunOutcome.CancelledByOwner);
        run.Run.FinishedAt.ShouldBe(Now);
    }

    /// <summary>A publication that failed before the window ends the run with the row's own closed code.</summary>
    [Fact]
    public async Task Veto_FailedPublication_EndsWithItsCode()
    {
        var (run, step, _) = await ArrangeAsync(PublicationStatus.Failed, consented: false, errorCode: "publication_failed");

        var result = await step.HandleAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe("publication_failed");
        run.Run.State.ShouldBe(AutopilotRunState.Failed);
    }

    /// <summary>A run in the window without the row it tracks has nothing to follow, so the tick retries.</summary>
    [Fact]
    public async Task Veto_NoPublicationAttached_Retries()
    {
        var series = await SeriesAsync();
        var run = AutopilotRuns.WalkTo(series, AutopilotRunState.VetoWindow, Now, withPublication: false);
        var reader = new FakeStepReader();
        var consent = new FakeConsentGate(consented: true);

        var step = new VetoWindowStep(reader, consent, NullLogger<VetoWindowStep>.Instance);
        var result = await step.HandleAsync(AutopilotStepContexts.For(run, series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Retry);
        result.Code.ShouldBe(AutopilotStepCodes.PublicationMissing);
        consent.Calls.ShouldBe(0);
    }

    private async Task<SeriesAggregate> SeriesAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;

        return await new SeriesBuilder(Database, actor).CreateAsync(user.Id, autopilotEnabled: true, ct: Ct);
    }

    private async Task<(RunAndSeries Run, VetoWindowStep Step, FakeConsentGate Consent)> ArrangeAsync(
        PublicationStatus status,
        bool consented,
        string? errorCode = null)
    {
        var series = await SeriesAsync();
        var publicationId = Guid.CreateVersion7();
        var run = AutopilotRuns.WalkTo(
            series,
            AutopilotRunState.VetoWindow,
            Now,
            publicationId: publicationId);

        var reader = new FakeStepReader
        {
            Publication = new AutopilotPublicationSnapshot(
                publicationId,
                status,
                CreatedAt: Now.AddMinutes(-5),
                ConsentAt: consented ? Now : null,
                VetoNoticeSentAt: Now.AddMinutes(-5),
                VetoEndsAt: Now.AddHours(24),
                ErrorCode: errorCode,
                PreAudit: false),
        };
        var consent = new FakeConsentGate(consented);

        return (
            new RunAndSeries(run, series),
            new VetoWindowStep(reader, consent, NullLogger<VetoWindowStep>.Instance),
            consent);
    }

    private sealed record RunAndSeries(AutopilotRun Run, SeriesAggregate Series);
}
