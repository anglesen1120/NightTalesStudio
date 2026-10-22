using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Features.Autopilot.Planning;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration;

/// <summary>
/// The run planner's and orchestrator's composition (02.8.3 plan §3 Steps 4–9): the slot calculator, the
/// planner, the orchestrator, the instruments, the plan job's step handlers and the metrics, in one call.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is not here.</b> <see cref="AutopilotRunOptions"/> is bound and validated by the host (the Worker
/// root), because <c>NightTales.Application</c> may not reference <c>Microsoft.Extensions.Options</c> — the
/// same split as <c>AddAutopilotGates</c>/<c>AddAutopilotGatesOptions</c> and <c>AddStoryGeneration</c>. The
/// bound value is registered as a singleton and injected as itself.
/// </para>
/// <para>
/// <b>Step handlers are found, not listed.</b> Every concrete <see cref="IAutopilotStepHandler"/> in this
/// assembly is registered scoped, one descriptor per type, so a new step needs no DI edit. Two handlers for one
/// state is a code error: it fails at start-up, where the Worker resolves the whole set once through
/// <see cref="AutopilotStepHandlers.Index"/>.
/// </para>
/// <para>
/// <b>Lifetimes.</b> The orchestrator and the planner are scoped (they read through scoped ports);
/// <see cref="AutopilotRunMetrics"/> is a singleton, one instrument per process; <see cref="ISlotCalculator"/> is
/// stateless and a singleton.
/// </para>
/// </remarks>
public static class AutopilotOrchestrationServiceCollectionExtensions
{
    /// <summary>Registers the calculator, the planner, the orchestrator, the step handlers and the instruments.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddAutopilotOrchestration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // `ISlotCalculator` and `IChannelAuditPolicy` are registered by AddNightTalesApplication
        // (ApplicationServiceCollectionExtensions): the Api's autopilot routes resolve them too, and this extension is
        // called from the Worker alone.
        services.TryAddSingleton<AutopilotRunMetrics>();

        services.TryAddScoped<IAutopilotRunOrchestrator, AutopilotRunOrchestrator>();
        services.TryAddScoped<AutopilotSlotPlanner>();

        // The render/publish steps' shared collaborators. None is an `IAutopilotStepHandler`, so the scan below
        // cannot see them, and none has another registration in the tree: `PublicationCreatedStep`,
        // `ScheduledStep` and `VetoWindowStep` take `IAutopilotStepReader` (and the veto step the consent gate)
        // in their only constructor, so leaving these out fails the Worker boot in `AutopilotStartupCheck`.
        // Through `ActivatorUtilities`: the plain container ignores `[ActivatorUtilitiesConstructor]` and finds the
        // two public seam constructors ambiguous once `IChannelAuditPolicy` is registered.
        services.TryAddScoped(serviceProvider =>
            ActivatorUtilities.CreateInstance<Steps.AutopilotPublicationCreator>(serviceProvider));
        services.TryAddScoped<Steps.IAutopilotStepReader, Steps.EfAutopilotStepReader>();
        services.TryAddScoped<Steps.IAutopilotConsentGate, Steps.PublicationConsentGate>();

        // 02.8.3-T2: `IChannelAuditPolicy` is registered by AddNightTalesApplication, not here — see the note by the
        // slot calculator above.

        // The research step's go signal, owned here because this extension owns the handler scan that consumes it
        // (`AddAutopilotOutboxHandlers` registers locator and handlers only). `TryAdd` lets a host that wants a
        // different implementation (a test fake) register it first and keep it.
        services.TryAddScoped<Steps.ISourceFinderGo, Steps.SourceFinderGo>();

        foreach (var handler in StepHandlerTypes(typeof(AutopilotOrchestrationServiceCollectionExtensions).Assembly))
        {
            services.TryAddScoped(handler);
            services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IAutopilotStepHandler), handler));
        }

        return services;
    }

    /// <summary>
    /// The step-handler classes of <paramref name="assembly"/>, in a stable order (by full name) so a duplicate
    /// state is reported for the same pair on every host.
    /// </summary>
    /// <param name="assembly">The assembly to scan; the Application assembly in production.</param>
    /// <returns>The concrete, non-nested <see cref="IAutopilotStepHandler"/> types.</returns>
    public static IReadOnlyList<Type> StepHandlerTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsNested: false }
                && typeof(IAutopilotStepHandler).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
    }
}
