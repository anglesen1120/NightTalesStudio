using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.MptAdapter.Health;
using NightTales.MptAdapter.ImageRelay;
using NightTales.MptAdapter.Tls;
using NightTales.RenderNode.Server;

namespace NightTales.MptAdapter;

/// <summary>
/// The adapter as a node service (11.3.5-T4): D6 configuration binding with <c>ValidateOnStart</c>, the fatal
/// startup checks (exit 2), Kestrel HTTPS on the node's private address, the health sources and the relay sink.
/// </summary>
internal static class AdapterHost
{
    /// <summary><c>ImageRelay:Enabled</c> (D6, default true).</summary>
    internal const string RelayEnabledKey = "ImageRelay:Enabled";

    /// <summary>
    /// Registers everything the adapter adds to the render-node server core. The MPT <see cref="HttpClient"/>
    /// is only ever built by <see cref="MptHttpClient.Create"/> (redirects off, D6): no other construction path
    /// exists in this assembly, and the proxy probe uses the same factory.
    /// </summary>
    internal static IServiceCollection AddMptAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AdapterOptions>()
            .Configure(options => configuration.GetSection(AdapterOptions.SectionName).Bind(options))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AdapterOptions>, AdapterOptionsValidator>());
        services.AddRenderNodeServer(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IMptProcessStartReader, MptProcessStartReader>();
        services.AddSingleton<TaskDirCleaner>();
        services.AddSingleton(provider => new SqliteTaskStore(
            new SqliteStoreOptions(provider.GetRequiredService<IOptions<AdapterOptions>>().Value.TasksDbPath),
            provider.GetRequiredService<ILogger<SqliteTaskStore>>()));
        services.AddSingleton<ITaskStore>(provider => provider.GetRequiredService<SqliteTaskStore>());

        services.AddSingleton(provider => new MptClient(
            MptHttpClient.Create(provider.GetRequiredService<IOptions<AdapterOptions>>().Value),
            provider.GetRequiredService<IOptions<AdapterOptions>>(),
            provider.GetRequiredService<IOptions<RenderNodeServerOptions>>(),
            provider.GetRequiredService<IMptProcessStartReader>(),
            provider.GetRequiredService<TaskDirCleaner>(),
            provider.GetRequiredService<ILogger<MptClient>>()));
        services.AddSingleton<IRenderBackend>(provider => provider.GetRequiredService<MptClient>());

        // The post-processor (11.3.5-T3) owns the consumer; TryAdd so whichever registers first wins.
        services.TryAddSingleton<ICompletedTaskFeed, ChannelCompletedTaskFeed>();
        services.AddHostedService<MptTaskPoller>();

        services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        services.AddSingleton<IDiskStats, SystemDiskStats>();
        services.AddSingleton(provider => BundleInfo.Load(provider.GetRequiredService<IOptions<AdapterOptions>>().Value));
        services.AddSingleton<MptSampler>();
        services.AddSingleton(provider => new ProxySampler(
            MptHttpClient.Create(provider.GetRequiredService<IOptions<AdapterOptions>>().Value),
            provider.GetRequiredService<IOptions<AdapterOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ProxySampler>>()));
        services.AddSingleton<DiskSampler>();
        services.AddSingleton<GpuSampler>();
        services.AddSingleton<AssetsSampler>();
        services.AddSingleton<CodecProbe>();
        services.AddSingleton<ConfigProbe>();
        services.AddSingleton<IHealthSampler>(provider => provider.GetRequiredService<MptSampler>());
        services.AddSingleton<IHealthSampler>(provider => provider.GetRequiredService<ProxySampler>());
        services.AddSingleton<IHealthSampler>(provider => provider.GetRequiredService<DiskSampler>());
        services.AddSingleton<IHealthSampler>(provider => provider.GetRequiredService<GpuSampler>());
        services.AddSingleton<IHealthSampler>(provider => provider.GetRequiredService<AssetsSampler>());
        services.AddSingleton<IHealthSampler>(provider => provider.GetRequiredService<CodecProbe>());
        services.AddSingleton<IHealthSampler>(provider => provider.GetRequiredService<ConfigProbe>());
        services.AddHostedService<HealthSamplerService>();
        services.AddSingleton<INodeHealthSource, NodeHealthSource>();

        // Relay wiring (03.4.3-T1): the relay host itself is not part of this build, so an enabled relay is
        // reported not ok (fail closed, no dispatch) instead of pretending to listen on 127.0.0.1:8091.
        var relayEnabled = !bool.TryParse(configuration[RelayEnabledKey], out var enabled) || enabled;
        services.TryAddSingleton<IImagePlanSink, NoOpImagePlanSink>();
        if (relayEnabled)
        {
            services.TryAddSingleton<IImageRelayHealthProvider, MissingImageRelayHealth>();
        }
        else
        {
            services.TryAddSingleton<IImageRelayHealthProvider, DisabledImageRelayHealth>();
        }

        return services;
    }

    /// <summary>The process entry: exit 2 on a fatal startup check, 0 after a clean stop.</summary>
    internal static async Task<int> RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddMptAdapter(builder.Configuration);

        var fatal = StartupKeyChecks.Run(
            builder.Configuration,
            Environment.GetEnvironmentVariable,
            builder.Environment.IsDevelopment(),
            TimeProvider.System,
            out var certificate);
        if (fatal is not null || certificate is null)
        {
            await Console.Error.WriteLineAsync(fatal ?? TlsValidator.MissingMessage).ConfigureAwait(false);
            return StartupKeyChecks.ExitCode;
        }

        var adapter = new AdapterOptions();
        builder.Configuration.GetSection(AdapterOptions.SectionName).Bind(adapter);
        var address = TlsPrivateAddressGuard.Parse(adapter.ListenAddress);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(
            address,
            adapter.Port,
            listen => listen.UseHttps(certificate.Certificate)));

        await using var app = builder.Build();
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NightTales.MptAdapter.Host");
        TlsValidator.LogFingerprint(log, certificate);
        log.Listening(adapter.ListenAddress, adapter.Port);
        if (!bool.TryParse(app.Configuration[RelayEnabledKey], out var relayEnabled) || relayEnabled)
        {
            log.RelayHostMissing();
        }
        else
        {
            log.RelayDisabled();
        }

        await app.Services.GetRequiredService<SqliteTaskStore>().MigrateAsync(CancellationToken.None).ConfigureAwait(false);

        app.UseRenderNodeServer();
        app.MapRenderNodeV1();
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
