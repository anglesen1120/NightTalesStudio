using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.DependencyInjection;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Application.Features.Autopilot.Planning;
using NightTales.Domain.Series.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration;

/// <summary>
/// The handler map and its composition (02.8.3 plan §3.5 "one handler per source state"): a duplicate is a code
/// error that must fail — the Worker fails its boot on it — and the assembly scan must register exactly the
/// handlers it finds, so a new step never needs a DI edit.
/// </summary>
public sealed class AutopilotStepHandlerRegistrationTests
{
    /// <summary>Two handlers for one state is refused; neither silently wins.</summary>
    [Fact]
    public void Index_TwoHandlersForOneState_Throws()
    {
        var first = new FakeStep(AutopilotRunState.Planned);
        var second = new FakeStep(AutopilotRunState.Planned);

        var exception = Should.Throw<InvalidOperationException>(
            () => AutopilotStepHandlers.Index([first, second]));

        exception.Message.ShouldContain(AutopilotRunState.Planned.ToString());
    }

    /// <summary>One handler per state indexes by that state; the empty set is an empty map, not an error.</summary>
    [Fact]
    public void Index_OnePerState_KeysByState_AndEmptyIsEmpty()
    {
        var planned = new FakeStep(AutopilotRunState.Planned);
        var paused = new FakeStep(AutopilotRunState.Paused);

        var index = AutopilotStepHandlers.Index([planned, paused]);

        index.Count.ShouldBe(2);
        index[AutopilotRunState.Planned].ShouldBeSameAs(planned);
        index[AutopilotRunState.Paused].ShouldBeSameAs(paused);
        AutopilotStepHandlers.Index([]).ShouldBeEmpty();
    }

    /// <summary>
    /// The composition registers one <see cref="IAutopilotStepHandler"/> descriptor per scanned type, plus the
    /// planner, the orchestrator, the metrics and the render/publish step's shared creator.
    /// </summary>
    /// <remarks>
    /// The shared Application ports the handlers resolve — <see cref="ISlotCalculator"/> and
    /// <see cref="IChannelAuditPolicy"/> — are registered by <c>AddNightTalesApplication</c>, not here: the Api's
    /// autopilot routes resolve them too and the Api never calls this Worker-only extension, so a registration here
    /// would resolve nowhere in the Api. Both are composed in this test for the same reason the Worker composes both
    /// (02.8.3-T2); <see cref="AddNightTalesApplication_RegistersThePortsTheApiRoutesResolve"/> pins that half.
    /// </remarks>
    [Fact]
    public void AddAutopilotOrchestration_RegistersEveryScannedHandler()
    {
        var services = new ServiceCollection();

        services.AddNightTalesApplication();
        services.AddAutopilotOrchestration();

        var scanned = AutopilotOrchestrationServiceCollectionExtensions.StepHandlerTypes(
            typeof(IAutopilotStepHandler).Assembly);

        scanned.ShouldNotBeEmpty("the Application assembly holds this feature's step handlers");
        services.Where(descriptor => descriptor.ServiceType == typeof(IAutopilotStepHandler))
            .Count()
            .ShouldBe(scanned.Count);
        services.Any(descriptor => descriptor.ServiceType == typeof(ISlotCalculator)).ShouldBeTrue();
        services.Any(descriptor => descriptor.ServiceType == typeof(AutopilotSlotPlanner)).ShouldBeTrue();
        services.Any(descriptor => descriptor.ServiceType == typeof(IAutopilotRunOrchestrator)).ShouldBeTrue();
        services.Any(descriptor => descriptor.ServiceType == typeof(AutopilotRunMetrics)).ShouldBeTrue();
    }

    /// <summary>
    /// The two ports the Api's autopilot routes resolve beside the Worker's orchestration belong to the shared
    /// Application registration, because the Api never calls <c>AddAutopilotOrchestration</c> (02.8.3-T2).
    /// </summary>
    [Fact]
    public void AddNightTalesApplication_RegistersThePortsTheApiRoutesResolve()
    {
        var services = new ServiceCollection();

        services.AddNightTalesApplication();

        services.Any(descriptor => descriptor.ServiceType == typeof(ISlotCalculator)).ShouldBeTrue();
        services.Any(descriptor => descriptor.ServiceType == typeof(IChannelAuditPolicy)).ShouldBeTrue();
    }

    /// <summary>The scan never picks up a nested or abstract type: neither can be a handler.</summary>
    [Fact]
    public void StepHandlerTypes_SkipsNestedAndAbstractTypes()
    {
        AutopilotOrchestrationServiceCollectionExtensions.StepHandlerTypes(typeof(FakeStep).Assembly)
            .ShouldNotContain(typeof(NestedStep));
        AutopilotOrchestrationServiceCollectionExtensions.StepHandlerTypes(typeof(FakeStep).Assembly)
            .ShouldNotContain(typeof(AbstractStep));
    }

    private sealed class FakeStep(AutopilotRunState state) : IAutopilotStepHandler
    {
        public AutopilotRunState State { get; } = state;

        public ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct) =>
            ValueTask.FromResult(StepResult.Idle);
    }

    private sealed class NestedStep : IAutopilotStepHandler
    {
        public AutopilotRunState State => AutopilotRunState.Planned;

        public ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct) =>
            ValueTask.FromResult(StepResult.Idle);
    }

    private abstract class AbstractStep : IAutopilotStepHandler
    {
        public abstract AutopilotRunState State { get; }

        public ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct) =>
            ValueTask.FromResult(StepResult.Idle);
    }
}
