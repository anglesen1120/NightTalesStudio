using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Rendering;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>
/// Registers the render-node registry, health cache and pinned-TLS transport of 11 README D13 (11.3.3-T1):
/// the pieces of the story that live in Infrastructure and are resolved by the Worker's composition root.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NodeHealthPoller"/> itself is a Worker type and is registered there (the Worker assembly owns
/// it); this extension is the Infrastructure half, so the Api can compose the same cache when 11.3.3-T2's
/// selector needs it.
/// </para>
/// <para>
/// The per-pull timeout and the poll cadence come from <see cref="RenderNodesOptions"/> and are validated on
/// start, so an impossible configuration fails the boot instead of the first tick.
/// </para>
/// </remarks>
public static class RenderNodesServiceCollectionExtensions
{
    /// <summary>Registers the node registry, health cache, connection factory and seeder.</summary>
    /// <param name="services">The composition root's service collection.</param>
    /// <param name="configuration">The host configuration: the <c>RenderNodes</c> section and the node keys.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddRenderNodes(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RenderNodesOptions>()
            .BindConfiguration(RenderNodesOptions.SectionName)
            .Validate(
                options => options.HealthPollSeconds > 0,
                $"'{RenderNodesOptions.SectionName}:{nameof(RenderNodesOptions.HealthPollSeconds)}' must be greater than zero.")
            .Validate(
                options => options.DownAfterSeconds > 0,
                $"'{RenderNodesOptions.SectionName}:{nameof(RenderNodesOptions.DownAfterSeconds)}' must be greater than zero.")
            .Validate(
                options => options.HealthStaleSeconds > 0,
                $"'{RenderNodesOptions.SectionName}:{nameof(RenderNodesOptions.HealthStaleSeconds)}' must be greater than zero.")
            .Validate(
                options => options.DriftAlertPulls > 0,
                $"'{RenderNodesOptions.SectionName}:{nameof(RenderNodesOptions.DriftAlertPulls)}' must be greater than zero.")
            .ValidateOnStart();

        // One meter instance per host, exactly like AuthMetrics/OutboxMetrics: a test attaches a listener to
        // this instance and never to a same-named meter of another process.
        services.AddSingleton<NodeMetrics>();

        // The cache is process-local state the poller writes and the gauges read, so it is one instance for
        // the host's lifetime.
        services.AddSingleton<INodeHealthCache, NodeHealthCache>();

        services.AddSingleton<IRenderNodeConnectionFactory, RenderNodeConnectionFactory>();

        // Both reads are scoped with the context they query; the seeder writes through the same scope.
        services.AddScoped<INodeRegistry, NodeRegistry>();
        services.AddScoped<INodeLoadReader, RenderJobNodeLoadReader>();
        services.AddScoped<WorkerNodeSeeder>();

        // The dispatch decision (11.3.3-T2): scoped with the IReadDb and INodeLoadReader it queries; the
        // log gate is a singleton so "nothing dispatchable" warns once per interval, not once per pick.
        services.AddSingleton<NodeDispatchLogGate>();
        services.AddScoped<INodeSelector, NodeSelector>();

        // The advisory-lock guard keeps one dedicated connection for the poller's lifetime, so it is a
        // singleton that owns that connection.
        var connectionString = configuration.GetConnectionString(PersistenceOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new OptionsValidationException(
                nameof(PersistenceOptions),
                typeof(PersistenceOptions),
                [$"Configuration value 'ConnectionStrings:{PersistenceOptions.ConnectionStringName}' is required for the render-node health poller."]);
        }

        services.AddSingleton(new NodeHealthPollerLock(connectionString));

        return services;
    }
}
