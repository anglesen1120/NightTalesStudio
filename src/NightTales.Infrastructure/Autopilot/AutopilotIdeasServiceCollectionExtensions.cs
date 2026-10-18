using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Autopilot.Ideas;

namespace NightTales.Infrastructure.Autopilot;

/// <summary>
/// Registers what the idea-generation step needs from Infrastructure (02.8.1-T1 plan §7.1): the
/// <c>Autopilot:Ideas</c> section bound and validated on start, the Domain limits record and the Application's
/// planner options as plain singletons, and the scoped context/history readers. 02.8.1-T2 adds the same for
/// <c>Autopilot:Ideas:{Novelty,Weights,Policy}</c> and the corpus reader; the two Application checkers stay with
/// the planner in <c>AddIdeaGeneration</c>, because only the Worker's host has the judge and the instrument they
/// take.
/// </summary>
/// <remarks>
/// <para>
/// Composed by <c>AddPersistence</c>, like the bible's section and snapshot provider (02.6.3), because both hosts
/// and the test harness compose persistence and the Api's planner path must be able to resolve the Application
/// graph even though only the Worker ever makes the paid call. The generator itself is registered with the text
/// adapter (<c>AddOpenAiTextClient</c>, Worker only), exactly as <c>OpenAiBibleProposalGenerator</c> is.
/// </para>
/// <para>
/// <see cref="IdeaLimits"/> and <see cref="IdeaPlannerOptions"/> are registered as their plain values because
/// Application may not reference Infrastructure's options type; both are bound from the one
/// <c>Autopilot:Ideas</c> section, so a host configures the count and the limits once.
/// </para>
/// </remarks>
public static class AutopilotIdeasServiceCollectionExtensions
{
    /// <summary>Registers the <c>Autopilot:Ideas</c> section and the Application's scoped idea readers.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <returns>The same collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAutopilotIdeas(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 02.8.1-T1: the candidate count and the per-field limits IdeaValidator enforces. Fails on start, like the
        // bible and facts sections: an impossible count or limit must never reach a paid call.
        services.AddOptions<IdeaOptions>()
            .BindConfiguration(IdeaOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<IdeaOptions>, IdeaOptionsValidator>());

        // The Application planner binds the same section through its own type; both hosts register the values the
        // handlers take, because Application cannot reference Infrastructure's options class.
        services.AddOptions<IdeaPlannerOptions>()
            .BindConfiguration(IdeaOptions.SectionName)
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<IdeaOptions>>().Value.Limits);
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<IdeaPlannerOptions>>().Value);

        services.TryAddScoped<IIdeaContextProvider, IdeaContextProvider>();
        services.TryAddScoped<IEpisodeStructureHistory, EpisodeStructureHistory>();

        // 02.8.1-T2: the novelty thresholds with the corpus cap, the pick weights and the content-policy
        // pre-check. Three more sections of the same feature, each validated on start for the same reason as the
        // count above: an impossible threshold or weight would silently decide every pick of a paid run.
        services.AddOptions<IdeaNoveltyOptions>()
            .BindConfiguration(IdeaNoveltyOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<IdeaNoveltyOptions>, IdeaNoveltyOptionsValidator>());

        services.AddOptions<IdeaPolicyOptions>()
            .BindConfiguration(IdeaPolicyOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<IdeaPolicyOptions>, IdeaPolicyOptionsValidator>());

        services.AddOptions<IdeaWeightsOptions>()
            .BindConfiguration(IdeaWeightsValidator.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<IdeaWeightsOptions>, IdeaWeightsValidator>());

        // The Application readers take the plain values, exactly like the planner options above, because
        // Application cannot reference Infrastructure's options types, and the corpus reader is scoped with the
        // read model it queries. The two 02.8.1-T2 checkers are NOT registered here: they are the paid idea step's
        // own half (they take the Worker's `IIdeaNoveltyJudge` and `IdeaMetrics`), so they belong next to the
        // planner in `AddIdeaGeneration` — the Api host, which this method also serves, has neither.
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<IdeaNoveltyOptions>>().Value);
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<IdeaPolicyOptions>>().Value);
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<IdeaWeightsOptions>>().Value.ToWeights());

        services.TryAddScoped<IIdeaCorpusProvider, IdeaCorpusProvider>();

        return services;
    }
}
