using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Stories;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The gate engine's composition (02.8.2-T1 plan §4, task step 9): everything 02.8.3-T1's orchestrator needs to
/// evaluate a run's story and video gates, in one call.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is not here.</b> <see cref="AutopilotGateOptions"/> is bound by the host
/// (<c>AddAutopilotGatesOptions</c>) because Application may not reference the options binder; the Worker and the
/// Api both call it. Nothing here calls a command: <see cref="AutopilotApprovalService"/> sends the same commands
/// a user's request sends, through the pipeline the host already composed.
/// </para>
/// <para>
/// <b>Lifetimes.</b> Every service is scoped like the read model and the transaction it works in, except
/// <see cref="AutopilotGateMetrics"/> and <see cref="ContentGateMetrics"/>: one instrument per process, so a scrape
/// never sees two series for one counter.
/// </para>
/// </remarks>
public static class AutopilotGatesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the evaluators, the writer, the recorder, the approval service, the content gate of 02.8.2-T2 and
    /// the instruments.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddAutopilotGates(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<StoryGateInputFactory>();
        services.TryAddScoped<StoryGateEvaluator>();
        services.TryAddScoped<VideoGateInputFactory>();
        services.TryAddScoped<VideoGateEvaluator>();

        // The one entry point 02.8.3-T1 depends on: it delegates each half to its evaluator, so the orchestrator
        // takes one interface instead of reaching for two classes.
        services.TryAddScoped<IAutoApprovalGate, AutoApprovalGate>();

        services.TryAddScoped<GateResultWriter>();
        services.TryAddScoped<GateOutcomeRecorder>();
        services.TryAddScoped<AutopilotApprovalService>();
        services.TryAddSingleton<AutopilotGateMetrics>();

        // 02.8.2-T2: the content gate (check 7), the scene-prompt expansion it moderates and the real-world
        // detector's Application half (check 8). Scoped like the evaluators, except the instrument, which is one per
        // process beside the gate engine's own counters.
        services.TryAddScoped<IAutopilotContentGate, AutopilotModerationGate>();
        services.TryAddScoped<ScenePromptExpander>();
        services.TryAddScoped<IRealWorldSignalService, RealWorldSignalService>();
        services.TryAddSingleton<ContentGateMetrics>();

        return services;
    }
}
