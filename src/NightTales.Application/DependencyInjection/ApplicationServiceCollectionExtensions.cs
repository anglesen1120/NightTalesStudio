using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Catalog;
using NightTales.Application.Abstractions.Compliance;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Stories;
using NightTales.Application.Features.Account.Legal;
using NightTales.Application.Features.Research.Jobs;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Application.Features.Stories.Moderation;
using NightTales.Application.Features.Stories.Render;
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

        // 02.2.2-T2 (02 CR-24): the fail-closed default until 02.5.5-T1 registers the real guard.
        services.TryAddSingleton<IFactSheetGuard, NullFactSheetGuard>();

        services.TryAddScoped<LegalAcceptanceReader>();
        services.TryAddScoped<ILegalAcceptance, LegalAcceptanceService>();

        // 02.3.2-T1: shared edit/approve moderation and approved-version rendering inputs.
        // The null gate is explicitly permitted until 02.3.3-T1 supplies the real approval checks.
        services.TryAddScoped<IStoryApprovalGate, NullStoryApprovalGate>();
        services.TryAddScoped<IStoryRenderInputProvider, StoryRenderInputProvider>();
        services.TryAddScoped<StoryDraftModeration>();

        return services;
    }

    /// <summary>
    /// Registers the story generation job's services (02.2.2-T3): the pipeline, the progress writer, the request factory and
    /// the metrics. The Worker calls it; the options (<c>Story:Job</c>, <c>Story:Sweeper</c>, <c>Story:Review</c>) are bound
    /// in the Worker's composition root.
    /// </summary>
    /// <param name="services">The composition root's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddStoryGeneration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<StoryGenerationMetrics>();
        services.TryAddScoped<StoryWriteRequestFactory>();
        services.TryAddScoped<StoryGenerationProgress>();
        services.TryAddScoped<StoryGenerationPipeline>();

        return services;
    }

    /// <summary>
    /// Registers the fact-sheet generation job's services (02.5.3-T2, plan §7): the pipeline that claims a queued
    /// <c>fact_sheet</c> run, extracts and validates its facts and saves the draft version. The Worker calls it; its
    /// options (<c>FactSheet:Job</c>, and the job's own copy of the <c>Facts</c> budget) are bound in the Worker's
    /// composition root, and the run's cost estimator (<c>IFactSheetCostEstimator</c>, the command's too) lives with
    /// the usage metering both hosts compose, next to <c>IStoryCostEstimator</c>.
    /// </summary>
    /// <param name="services">The composition root's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddFactSheetGeneration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<GenerateFactSheetPipeline>();

        return services;
    }
}
