using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Catalog;
using NightTales.Application.Features.Stories.Writing;

namespace NightTales.Application.DependencyInjection;

/// <summary>
/// The Application layer's own registrations (09 README D4/F03): the ports whose implementation is not
/// written yet, so each composition root has one call for them.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Application-layer defaults the Api and the Worker both need.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 09.4.1-T1: no prompt catalog is shipped yet (02.1.4-T1 loads the embedded <c>prompts/</c>
    /// manifest), so every preset key is unknown and a series may only carry a null <c>storyPreset</c>
    /// ([DECISION] 09.4.1 plan §4 U1). <c>TryAdd</c> keeps the replacement additive: 02.1.4-T1 registers
    /// its own <see cref="IStoryPresetCatalog"/> and wins.
    /// </para>
    /// <para>
    /// The handlers and validators themselves need no call here: the Mediator source generator and
    /// <c>AddValidatorsFromAssemblyContaining</c> register them from the Application assembly.
    /// </para>
    /// </remarks>
    /// <param name="services">The composition root's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightTalesApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IStoryPresetCatalog, NullStoryPresetCatalog>();

        // 02.2.3-T2: stateless and pure; needs the IPromptCatalog singleton that both hosts register through AddPromptCatalog.
        services.TryAddSingleton<IPromptAssembler, PromptAssembler>();

        return services;
    }
}
