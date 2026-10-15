using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Publishing;
using NightTales.Infrastructure.Publishing;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>
/// The YouTube publishing quota (06.3.1-T1): binds and validates <see cref="YouTubeOptions"/> from <c>YouTube</c> and
/// registers the scoped <see cref="IQuotaLedger"/>. Called from <c>AddPersistence</c>, so the Api, the Worker and the
/// test harness all compose it.
/// </summary>
public static class YouTubeServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="YouTubeOptions"/> (the <c>YouTube:Quota:UnitsPerDay</c> key defaults to 100, so no host needs a
    /// placeholder), validates it on start, and registers the scoped ledger. The ledger uses the caller's
    /// <c>IAppDbContext</c>, so <c>AddPersistence</c> must run in the same host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddYouTubePublishing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<YouTubeOptions>()
            .BindConfiguration(YouTubeOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<YouTubeOptions>, YouTubeOptionsValidator>());

        services.TryAddScoped<IQuotaLedger, YouTubeQuotaLedger>();
        // Application cannot reference Microsoft.Extensions.Options, so the ETA handler takes the plain value; the scoped
        // lifetime re-reads the monitor per request, so a configuration reload is seen on the next call.
        services.TryAddScoped(provider => provider.GetRequiredService<IOptionsMonitor<YouTubeOptions>>().CurrentValue);

        return services;
    }
}

/// <summary>Fails the start-up when <c>YouTube:Quota:UnitsPerDay</c> is below 1: a zero limit would refuse every publish.</summary>
internal sealed class YouTubeOptionsValidator : IValidateOptions<YouTubeOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, YouTubeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Quota.UnitsPerDay >= 1
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                [$"'{YouTubeOptions.SectionName}:Quota:{nameof(YouTubeQuotaOptions.UnitsPerDay)}' must be at least 1."]);
    }
}
