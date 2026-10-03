using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Infrastructure.Persistence.Queries;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>
/// The FR-02-7 rolling-window limit (02.4.2-T1). Only the Api composes it: the Worker's autopilot runs are capped
/// by D17's per-run limits, not by this guard, and a user-triggered generate call is an Api request.
/// </summary>
public static class GenerationLimitServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates <see cref="GenerationLimitOptions"/> from <c>Story</c> (both keys default, so no host
    /// needs a placeholder) and registers the scoped <see cref="IGenerationLimitGuard"/>. Needs
    /// <c>AddPersistence</c> and an <c>IClock</c> in the same host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddGenerationLimit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<GenerationLimitOptions>()
            .BindConfiguration(GenerationLimitOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<GenerationLimitOptions>, GenerationLimitOptionsValidator>());

        // Scoped: the guard shares the command's AppDbContext, hence its transaction and the episode row lock.
        services.TryAddScoped<IGenerationLimitGuard, GenerationLimitGuard>();

        return services;
    }
}

/// <summary>Fails the start-up on a non-positive <c>Story:DailyGenerationLimit</c> or <c>Story:LimitWindowHours</c>.</summary>
internal sealed class GenerationLimitOptionsValidator : IValidateOptions<GenerationLimitOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, GenerationLimitOptions options)
    {
        var problems = GenerationLimitOptions.Validate(options);

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
