using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Infrastructure.Persistence.Queries;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>
/// The FR-02-7 rolling-window limit (02.4.2-T1). Both hosts compose it: the Api serves the owner's own generate
/// call, and the Worker resolves the same command's handler because 02.8.3-T1's autopilot step dispatches a slot's
/// first story through it. Step 5 of the handler counts user-triggered runs only, so the autopilot never spends
/// the owner's limit; the registration is what lets Mediator activate the handler in either host.
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

/// <summary>Fails the start-up on an out-of-range <c>Story:DailyGenerationLimit</c> (1-1000) or <c>Story:LimitWindowHours</c> (1-8784).</summary>
internal sealed class GenerationLimitOptionsValidator : IValidateOptions<GenerationLimitOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, GenerationLimitOptions options)
    {
        var problems = GenerationLimitOptions.Validate(options);

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
