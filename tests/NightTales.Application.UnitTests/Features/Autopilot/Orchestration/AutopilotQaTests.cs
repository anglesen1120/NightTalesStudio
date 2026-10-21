using System.Globalization;
using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Events;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Generation.Events;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration;

/// <summary>
/// The independent QA pass over 02.8.3-T1's Application surface that needs no database: the idempotency-key
/// contract of plan §3.5 (UTC slot instant, one key per step), the tick's due predicate, and the outbox handler's
/// "no run, no work" and "surface the error" rules.
/// </summary>
/// <remarks>
/// Written by a reviewer who did not write the product code. No clock, no container, no provider: the runs are
/// arranged through the aggregate and the handlers are driven with hand-written fakes.
/// </remarks>
public sealed class AutopilotQaTests
{
    private static readonly Guid SeriesId = Guid.Parse("018f0000-0000-7000-8000-0000000002a1");

    private static readonly Guid UserId = Guid.Parse("018f0000-0000-7000-8000-0000000002a2");

    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SlotAt = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Qa_IdempotencyKey_IsTheUtcSlotInstantExactly()
    {
        // Plan §3.5: `autopilot:{seriesId}:{slotAt:O}:{step}`. A slot handed over with the cadence's own +07:00
        // offset must produce the same key as its UTC instant, or a redelivery would pay twice.
        var slotWithOffset = new DateTimeOffset(2026, 10, 22, 19, 0, 0, TimeSpan.FromHours(7));
        var utcSlot = slotWithOffset.ToUniversalTime();
        var expected = string.Create(CultureInfo.InvariantCulture, $"autopilot:{SeriesId}:{utcSlot:O}:story");

        AutopilotIdempotencyKeys.For(SeriesId, slotWithOffset, AutopilotRunStep.Story).ShouldBe(expected);
        AutopilotIdempotencyKeys.For(SeriesId, utcSlot, AutopilotRunStep.Story).ShouldBe(expected);
        expected.ShouldEndWith($"2026-10-22T12:00:00.0000000+00:00:story");
        expected.ShouldStartWith($"autopilot:{SeriesId}:");
    }

    [Fact]
    public void Qa_IdempotencyKey_IsPerStepAndPerSlot()
    {
        string[] steps =
        [
            AutopilotRunStep.Authorization,
            AutopilotRunStep.Idea,
            AutopilotRunStep.FindSources,
            AutopilotRunStep.FactSheet,
            AutopilotRunStep.SourcesRequired,
            AutopilotRunStep.Story,
            AutopilotRunStep.StoryGate,
            AutopilotRunStep.Story2,
            AutopilotRunStep.Render,
            AutopilotRunStep.VideoGate,
            AutopilotRunStep.Publication,
            AutopilotRunStep.Consent,
            AutopilotRunStep.GoLive,
        ];

        var keys = steps
            .Select(step => AutopilotIdempotencyKeys.For(SeriesId, SlotAt, step))
            .ToArray();

        keys.Distinct(StringComparer.Ordinal).Count().ShouldBe(steps.Length, "one key per step of the ladder");

        // The same step of the next slot is a different key, which is what lets two slots of one series coexist.
        AutopilotIdempotencyKeys.For(SeriesId, SlotAt.AddDays(3), AutopilotRunStep.Story).ShouldNotBe(
            AutopilotIdempotencyKeys.For(SeriesId, SlotAt, AutopilotRunStep.Story));

        // The run overload derives nothing from the row id: it is the same key the paid dispatch uses.
        AutopilotIdempotencyKeys.For(Planned(), AutopilotRunStep.Story)
            .ShouldBe(AutopilotIdempotencyKeys.For(SeriesId, SlotAt, AutopilotRunStep.Story));
    }

    [Fact]
    public void Qa_DuePredicate_FollowsTheTerminalAndPausedSets()
    {
        var due = AutopilotRunOrchestrator.Due(Now).Compile();

        foreach (var (state, run) in ArrangedStates())
        {
            var expected = !AutopilotRunStateMachine.IsTerminal(state) && state != AutopilotRunState.Paused;

            due(run).ShouldBe(expected, $"the tick must {(expected ? "" : "not ")}advance a run in {state}");
        }
    }

    [Fact]
    public void Qa_DuePredicate_IgnoresARunWhoseStartIsStillAhead()
    {
        var due = AutopilotRunOrchestrator.Due(Now).Compile();
        var early = AutopilotRun.Plan(UserId, SeriesId, SlotAt, Now.AddMinutes(1), authorizationId: null, Now);

        due(early).ShouldBeFalse("plan §7.1/§3.4: a run starts when start_at arrives, not when it is planned");

        var exactlyDue = AutopilotRun.Plan(UserId, SeriesId, SlotAt, Now, authorizationId: null, Now);

        due(exactlyDue).ShouldBeTrue("start_at <= now is the inclusive boundary");
    }

    [Fact]
    public async Task Qa_OutboxHandler_UnknownEpisode_IsASilentNoOp()
    {
        // Every owner episode raises these events too; a delivery for an episode with no autopilot run must not
        // throw (that would dead-letter a message not meant for this feature) and must advance nothing.
        var orchestrator = new RecordingOrchestrator();
        var handler = new StoryGeneratedHandler(
            new RunLocator(result: null),
            orchestrator,
            NullLogger<StoryGeneratedHandler>.Instance);

        await handler.HandleAsync(StoryGeneratedEvent(), Outbox(), CancellationToken.None);

        orchestrator.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Qa_OutboxHandler_AdvanceError_IsSurfacedSoTheDispatcherRetries()
    {
        // A handler that swallowed the error would mark the event processed and stall the run (09 README D11), so
        // a real run whose advance failed must reach the caller as a throw.
        var orchestrator = new RecordingOrchestrator { FailWith = "autopilot_step_failed" };
        var handler = new StoryGeneratedHandler(
            new RunLocator((Guid.CreateVersion7(), UserId)),
            orchestrator,
            NullLogger<StoryGeneratedHandler>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await handler.HandleAsync(StoryGeneratedEvent(), Outbox(), CancellationToken.None));

        orchestrator.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Qa_OutboxHandler_ClosedRun_IsHandledWithoutThrowing()
    {
        // The run finished between the lookup and the advance: that delivery counts as handled (no retry storm).
        var orchestrator = new RecordingOrchestrator { FailWith = ErrorCodes.AutopilotRunNotFound };
        var handler = new StoryGeneratedHandler(
            new RunLocator((Guid.CreateVersion7(), UserId)),
            orchestrator,
            NullLogger<StoryGeneratedHandler>.Instance);

        await handler.HandleAsync(StoryGeneratedEvent(), Outbox(), CancellationToken.None);

        orchestrator.Calls.ShouldBe(1);
    }

    private static AutopilotRun Planned() =>
        AutopilotRun.Plan(UserId, SeriesId, SlotAt, Now, authorizationId: null, Now);

    private static OutboxContext Outbox() => new(Guid.CreateVersion7(), UserId, 1, Now);

    private static StoryGenerated StoryGeneratedEvent() => new(
        EpisodeId: Guid.CreateVersion7(),
        SeriesId: SeriesId,
        UserId: UserId,
        GenerationRunId: Guid.CreateVersion7(),
        StoryVersionId: Guid.CreateVersion7(),
        Revised: false,
        Trigger: GenerationTrigger.Autopilot,
        OccurredAt: Now);

    /// <summary>
    /// One run per state the aggregate can reach without a database: every state up to the story gate, plus the
    /// three blocked and two terminal ones. The publishing half needs a seeded episode and publication row, so it is
    /// covered by the integration QA case instead; the terminal and paused sets it belongs to are asserted by
    /// <see cref="ArrangedStates"/>'s completeness check.
    /// </summary>
    /// <returns>The arranged states.</returns>
    private static IEnumerable<(AutopilotRunState State, AutopilotRun Run)> ArrangedStates()
    {
        yield return (AutopilotRunState.Planned, Planned());

        var awaiting = Planned();
        awaiting.AdvanceTo(AutopilotRunState.AwaitingAuthorization, AutopilotRunStep.Authorization, Now);
        yield return (AutopilotRunState.AwaitingAuthorization, awaiting);

        var picked = Planned();
        picked.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
        yield return (AutopilotRunState.IdeaPicked, picked);

        var researching = Planned();
        researching.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
        researching.AdvanceTo(AutopilotRunState.Researching, AutopilotRunStep.FindSources, Now);
        yield return (AutopilotRunState.Researching, researching);

        var generating = Planned();
        generating.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
        generating.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.Story, Now);
        yield return (AutopilotRunState.StoryGenerating, generating);

        var gate = Planned();
        gate.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
        gate.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.Story, Now);
        gate.AdvanceTo(AutopilotRunState.StoryGate, AutopilotRunStep.StoryGate, Now);
        yield return (AutopilotRunState.StoryGate, gate);

        var waiting = Planned();
        waiting.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
        waiting.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.Story, Now);
        waiting.AdvanceTo(AutopilotRunState.StoryGate, AutopilotRunStep.StoryGate, Now);
        waiting.SetWaitingFor(WaitingFor.Story);
        waiting.AdvanceTo(AutopilotRunState.WaitingOwner, AutopilotRunStep.StoryGate, Now);
        yield return (AutopilotRunState.WaitingOwner, waiting);

        var paused = Planned();
        paused.Pause(AutopilotPauseReason.OwnerPaused, Now);
        yield return (AutopilotRunState.Paused, paused);

        var failed = Planned();
        failed.Fail("qa", Now);
        yield return (AutopilotRunState.Failed, failed);
    }

    private sealed class RunLocator((Guid RunId, Guid UserId)? result) : IAutopilotRunLocator
    {
        public Task<(Guid RunId, Guid UserId)?> FindActiveByEpisodeAsync(Guid episodeId, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class RecordingOrchestrator : IAutopilotRunOrchestrator
    {
        private int _calls;

        public int Calls => _calls;

        public string? FailWith { get; init; }

        public Task<ErrorOr<AdvanceResult>> AdvanceAsync(Guid runId, Guid userId, CancellationToken ct) =>
            AdvanceWhileProgressingAsync(runId, userId, maxSteps: 1, ct);

        public Task<ErrorOr<AdvanceResult>> AdvanceWhileProgressingAsync(
            Guid runId,
            Guid userId,
            int maxSteps,
            CancellationToken ct)
        {
            _calls++;

            return Task.FromResult<ErrorOr<AdvanceResult>>(FailWith is { } code
                ? Error.Failure(code, "qa")
                : new AdvanceResult(AutopilotRunState.IdeaPicked, StepResultKind.Advanced, null));
        }

        public Task<int> AdvanceDueAsync(DateTimeOffset now, int max, CancellationToken ct) =>
            throw new NotSupportedException("the outbox handlers never run the tick.");
    }
}
