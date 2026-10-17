using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Research.GenerateFactSheet;
using NightTales.Application.Features.Research.SourceFinder;
using NightTales.Infrastructure.Money;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.Infrastructure.Pricing;
using NightTales.Infrastructure.Rendering;

namespace NightTales.Infrastructure.Persistence.Usage;

/// <summary>Composition of the price versions, the cost calculator inputs and the usage meter (02.2.4-T1).</summary>
public static class UsageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PricingOptions"/> (<c>OpenAi:Pricing</c>), the <see cref="PriceVersionSeeder"/>
    /// (an <c>ISeeder</c>, run by the 09.2.5-T1 <c>SeedRunner</c>: Development auto-seed and <c>nts-seed</c>),
    /// <see cref="IPriceCatalog"/>, <see cref="IUsageMeter"/>,
    /// <see cref="IStoryCostEstimator"/> and the fact-sheet run's <see cref="IFactSheetCostEstimator"/> over the
    /// <c>Facts</c> section (02.5.3-T2). Needs <c>AddPersistence</c> and <c>AddPromptCatalog</c> in the same host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddUsageMetering(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<PricingOptions>()
            .BindConfiguration(PricingOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PricingOptions>, PricingOptionsValidator>());

        services.AddScoped<ISeeder, PriceVersionSeeder>();
        services.TryAddSingleton<IPriceCatalog, PriceCatalog>();
        services.TryAddSingleton<IUsageMeter, UsageMeter>();
        services.TryAddSingleton<IStoryCostEstimator, StoryCostEstimator>();

        // 02.5.3-T2 (02 README D14/T56): the worst-case bound of one fact-sheet run, read by the command that
        // reserves it and by the job that settles it. Both hosts compose this method, exactly as they do for
        // IStoryCostEstimator, so the Api needs no fact-sheet-specific registration. The input ceiling comes from the
        // `Facts` section (the same key FactsOptions binds) and the model follows OpenAi:Models:Facts, as the story
        // steps follow theirs; the section's own Model is only the absent-key default.
        services.AddOptions<FactSheetCostOptions>()
            .BindConfiguration(FactSheetCostOptions.SectionName)
            .PostConfigure<IConfiguration>((options, configuration) =>
            {
                if (configuration["OpenAi:Models:Facts"] is { Length: > 0 } model)
                {
                    options.Model = model;
                }
            })
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FactSheetCostOptions>, FactSheetCostOptionsValidator>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<FactSheetCostOptions>>().Value);
        services.TryAddSingleton<IFactSheetCostEstimator, FactSheetCostEstimator>();

        // 02.6.3-T2 (02 README D1/D14/T56): the worst-case bound of one bible-proposal run. Both hosts compose
        // this method, exactly as they do for the story and fact-sheet bounds, so the Api needs no bible-specific
        // registration. The step's output ceiling is not a key here: it is
        // price_versions.limits.bible_proposal.max_output_tokens, which the estimator reads from the active price
        // version (T56 forbids a configuration fallback for a step's cap).
        services.TryAddSingleton<IBibleProposalCostEstimator, BibleProposalCostEstimator>();

        // 02.7.2-T2 (02 README D1/D14/T56, 02.7 plan §7): the worst-case bound of one source-search run — the finder
        // call at the `SourceFinder` ceilings plus MaxSearchCalls times the version's flat web_search_call fee. Both
        // hosts compose this method, so the Api needs no source-finder-specific registration. The model follows
        // OpenAi:Models:Finder, as the other steps follow theirs; the section's own Model is the absent-key default.
        // The call's max_output_tokens is the ruled 2,000 (T12), read from the estimator until
        // price_versions.limits.finder ships with 02.7.2-T3.
        services.AddOptions<FindSourcesCostOptions>()
            .BindConfiguration(FindSourcesCostOptions.SectionName)
            .PostConfigure<IConfiguration>((options, configuration) =>
            {
                if (configuration["OpenAi:Models:Finder"] is { Length: > 0 } model)
                {
                    options.Model = model;
                }
            })
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FindSourcesCostOptions>, FindSourcesCostOptionsValidation>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<FindSourcesCostOptions>>().Value);
        services.TryAddSingleton<IFindSourcesCostEstimator, FindSourcesCostEstimator>();

        // 03.2.1-T1: the Images section (scene budget, D3) and the worst-case image bound (D8).
        services.AddOptions<ImagesOptions>()
            .BindConfiguration(ImagesOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ImagesOptions>, ImagesOptionsValidation>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<ImagesOptions>>().Value);
        services.TryAddSingleton<IImageCostEstimator, ImageCostEstimator>();

        // 03.2.2-T1: the name corpus of one episode (D11), read through the scope's tenant-filtered read
        // model, so it is scoped exactly as the other read services over IReadDb are.
        services.TryAddScoped<INameCorpusProvider, NameCorpusProvider>();

        // 02.2.2-T2: the platform spend cap (Budget section, all keys default) and the phase-P metering-only ledger
        // that 12.1.2 replaces at M2. The guard is scoped: it takes the advisory lock on the command's own AppDbContext.
        services.AddOptions<BudgetOptions>()
            .BindConfiguration(BudgetOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BudgetOptions>, BudgetOptionsValidation>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<BudgetOptions>>().Value);
        services.TryAddScoped<IOpenBoundsReader, GenerationRunOpenBoundsReader>();
        services.TryAddScoped<IBudgetGuard, UsageBudgetGuard>();
        services.TryAddSingleton<ICreditLedger, MeteringOnlyCreditLedger>();

        return services;
    }
}

/// <summary>Fails the start-up on an invalid <c>OpenAi:Pricing</c> section (rates, units, threshold), with every problem listed.</summary>
internal sealed class PricingOptionsValidator : IValidateOptions<PricingOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PricingOptions options)
    {
        var problems = PricingOptions.Validate(options);

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(problems.Select(problem => $"{PricingOptions.SectionName}: {problem}"));
    }
}

/// <summary>Fails the start-up on an invalid <c>Images</c> section, with every problem listed.</summary>
internal sealed class ImagesOptionsValidation : IValidateOptions<ImagesOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ImagesOptions options)
    {
        var problems = ImagesOptionsValidator.Validate(options);

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(problems.Select(problem => $"{ImagesOptions.SectionName}: {problem}"));
    }
}

/// <summary>
/// Fails the start-up on an invalid <c>SourceFinder</c> section (02.7.2-T2), with every problem listed: the
/// estimator turns <c>MaxSearchCalls</c> and <c>MaxInputBytes</c> into the money the run reserves, so an
/// out-of-bound value must never reach a request. Application owns the checks; this is the
/// <see cref="IValidateOptions{T}"/> adapter, as for <c>ImagesOptionsValidator</c>.
/// </summary>
internal sealed class FindSourcesCostOptionsValidation : IValidateOptions<FindSourcesCostOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FindSourcesCostOptions options)
    {
        var problems = FindSourcesCostOptionsValidator.Validate(options);

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}

/// <summary>
/// Fails the start-up on an invalid <c>Budget</c> section, with every problem listed. <c>Budget:Enabled=false</c> is a
/// test kill-switch that makes the spend cap allow everything, so it is refused outside Development (02.2.2-T2 SEC-4).
/// </summary>
internal sealed class BudgetOptionsValidation(IHostEnvironment environment) : IValidateOptions<BudgetOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BudgetOptions options)
    {
        var problems = BudgetOptions.Validate(options).ToList();
        if (!options.Enabled && !environment.IsDevelopment())
        {
            problems.Add($"'{BudgetOptions.SectionName}:{nameof(BudgetOptions.Enabled)}' may be false only in the Development environment; the spend cap is the platform's money guard.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
