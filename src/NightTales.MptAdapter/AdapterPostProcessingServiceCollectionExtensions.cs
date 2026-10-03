using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Server;

namespace NightTales.MptAdapter;

/// <summary>
/// Registers the adapter's post-processing (11.3.5-T3): the check runner and report reader, the output
/// publisher and file source, the disk rules and the <see cref="PostProcessor"/> hosted service, all over ONE
/// <see cref="ICompletedTaskFeed"/> that the poller (T2) publishes to and the post-processor consumes.
/// </summary>
/// <remarks>
/// <see cref="PostProcessor"/> has an internal constructor (its collaborators are internal), so a host must call
/// this method instead of <c>AddHostedService&lt;PostProcessor&gt;()</c>. The T2 pieces it needs
/// (<see cref="MptClient"/>, the process-start reader, the feed, the poller) are added with <c>TryAdd</c>, so a
/// host that registered its own keeps it.
/// </remarks>
public static class AdapterPostProcessingServiceCollectionExtensions
{
    /// <summary>Binds the <c>Adapter</c> section (<c>Check*</c>, <c>Max*Bytes</c>, ...) and registers the services above.</summary>
    public static IServiceCollection AddAdapterPostProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AdapterOptions>(configuration.GetSection(AdapterOptions.SectionName));

        // One feed instance behind both the concrete type and the interface: the poller publishes into the same
        // channel the post-processor reads.
        services.TryAddSingleton<ChannelCompletedTaskFeed>();
        services.TryAddSingleton<ICompletedTaskFeed>(static provider => provider.GetRequiredService<ChannelCompletedTaskFeed>());

        services.TryAddSingleton<IMptProcessStartReader, MptProcessStartReader>();
        services.TryAddSingleton<TaskDirCleaner>();
        services.TryAddSingleton<ICheckRunner, CheckRunner>();
        services.TryAddSingleton<CheckReportReader>();
        services.TryAddSingleton<OutputPublisher>();
        services.TryAddSingleton<FileOutputSource>();
        services.TryAddSingleton(static provider => new DiskRuleEnforcer(
            provider.GetRequiredService<TaskDirCleaner>(),
            provider.GetRequiredService<ILogger<DiskRuleEnforcer>>()));

        services.TryAddSingleton(static provider => new MptClient(
            MptHttpClient.Create(provider.GetRequiredService<IOptions<AdapterOptions>>().Value),
            provider.GetRequiredService<IOptions<AdapterOptions>>(),
            provider.GetRequiredService<IOptions<RenderNodeServerOptions>>(),
            provider.GetRequiredService<IMptProcessStartReader>(),
            provider.GetRequiredService<DiskRuleEnforcer>(),
            provider.GetRequiredService<FileOutputSource>(),
            provider.GetRequiredService<ILogger<MptClient>>()));
        services.TryAddSingleton<IRenderBackend>(static provider => provider.GetRequiredService<MptClient>());
        services.TryAddSingleton<MptTaskPoller>();

        services.TryAddSingleton(static provider => new PostProcessor(
            provider.GetRequiredService<ICompletedTaskFeed>(),
            provider.GetRequiredService<ITaskStore>(),
            provider.GetRequiredService<ICheckRunner>(),
            provider.GetRequiredService<CheckReportReader>(),
            provider.GetRequiredService<OutputPublisher>(),
            provider.GetRequiredService<DiskRuleEnforcer>(),
            provider.GetRequiredService<IOptions<AdapterOptions>>(),
            provider.GetRequiredService<ILogger<PostProcessor>>()));
        services.AddHostedService(static provider => provider.GetRequiredService<PostProcessor>());

        return services;
    }
}
