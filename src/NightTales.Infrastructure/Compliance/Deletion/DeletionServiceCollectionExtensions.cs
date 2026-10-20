using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Domain.Compliance.Deletion;
using NightTales.Infrastructure.Compliance.Deletion.Handlers;

namespace NightTales.Infrastructure.Compliance.Deletion;

/// <summary>Composition of the deletion engine (13.3.1-T1): the request entry point in both hosts, the executing half in the Worker.</summary>
public static class DeletionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDeletionRequests"/> and its collaborators (Api and Worker): a feature that starts a
    /// deletion clock needs only this half. No start-up validation and no database access happens at boot.
    /// </summary>
    public static IServiceCollection AddDeletionRequests(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IDeletionRequestWriter, DataDeletionRequestRepository>();
        services.TryAddScoped<IDeletionRequests, DeletionRequests>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDeletionTargetOwnership, EpisodeDeletionTargetOwnership>());

        // 06.4.1-T1: the channel scope's own check, without which a disconnect cannot schedule its purge (the engine
        // refuses a scope that needs a target and has no registered ownership check).
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDeletionTargetOwnership, ChannelDeletionTargetOwnership>());

        return services;
    }

    /// <summary>
    /// Registers the executing half (Worker only): validated options, metrics, the row executor, the registry with
    /// the <c>episode</c> handler, the restore pair and the start-up validator (K21).
    /// </summary>
    public static IServiceCollection AddDeletionEngine(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDeletionRequests();

        services.AddOptions<DataDeletionOptions>()
            .BindConfiguration(DataDeletionOptions.SectionName)
            .Validate(options => options.Validate() is null, "The 'DataDeletion' section is invalid; see the startup log for the setting.")
            .ValidateOnStart();

        // The Application classes take the bound value, not IOptions (the layer allow-list excludes
        // Microsoft.Extensions.Options).
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<DataDeletionOptions>>().Value);
        services.AddSingleton<DataDeletionMetrics>();
        services.AddSingleton<DeletionRowExecutor>();
        services.AddScoped<IDeletionScopeHandlerRegistry, DeletionScopeHandlerRegistry>();
        services.AddScoped<IDeletionScopeHandler, EpisodeDeletionScopeHandler>();
        services.AddScoped<IDeletionRestore, DeletionRestore>();
        services.AddHostedService<DeletionScopeHandlerRegistryValidator>();

        return services;
    }
}
