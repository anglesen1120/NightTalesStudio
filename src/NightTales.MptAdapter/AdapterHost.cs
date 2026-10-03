using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
    internal static IServiceCollection AddMptAdapter(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AdapterOptions>()
            .Configure(options => configuration.GetSection(AdapterOptions.SectionName).Bind(options))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AdapterOptions>, AdapterOptionsValidator>());
        services.AddRenderNodeServer(configuration);
        services.AddServerKeyEnvDefaults(environment ?? Environment.GetEnvironmentVariable);

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

    /// <summary>
    /// The D6 env names the server core's keys fall back to when the <c>RenderNodeServer</c> section has no
    /// value: the same fallbacks <see cref="StartupKeyChecks"/> accepts, so the middleware authenticates with
    /// exactly the key the startup checks validated (CR-1). An explicit section value wins.
    /// </summary>
    internal static IServiceCollection AddServerKeyEnvDefaults(
        this IServiceCollection services,
        Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        services.PostConfigure<RenderNodeServerOptions>(options =>
        {
            if (string.IsNullOrEmpty(options.ApiKey) && environment("ADAPTER_API_KEY") is { Length: > 0 } key)
            {
                options.ApiKey = key;
            }

            if (string.IsNullOrEmpty(options.ApiKeyNext) && environment("ADAPTER_API_KEY_NEXT") is { Length: > 0 } next)
            {
                options.ApiKeyNext = next;
            }
        });
        return services;
    }

    /// <summary>The fixed prefix of the exit-2 line for an invalid configuration (QA-2); keys only, never values.</summary>
    internal const string InvalidConfigurationMessage = "Invalid configuration: ";

    /// <summary>Fixed text when Kestrel would bind anything beyond the one guarded address (SEC-4).</summary>
    internal const string ExtraEndpointMessage =
        "The adapter listens only on Adapter:ListenAddress:Port; ASPNETCORE_URLS and Kestrel endpoint settings are not supported.";

    /// <summary>The process entry: exit 2 on a fatal startup check, 0 after a clean stop.</summary>
    internal static async Task<int> RunAsync(string[] args, CancellationToken stopping = default)
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

        // SEC-4: the guarded endpoint is the only one. Config-declared Kestrel endpoints are refused up front,
        // hosting URLs (ASPNETCORE_URLS, --urls) are cleared, and the bound addresses are asserted after start.
        if (builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
        {
            await Console.Error.WriteLineAsync(ExtraEndpointMessage).ConfigureAwait(false);
            return StartupKeyChecks.ExitCode;
        }

        builder.WebHost.UseUrls();
        builder.WebHost.PreferHostingUrls(false);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(
            address,
            adapter.Port,
            listen => listen.UseHttps(certificate.Certificate)));

        try
        {
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
            await app.StartAsync(stopping).ConfigureAwait(false);

            if (!OnlyGuardedAddress(app.Services.GetRequiredService<IServer>(), address, adapter.Port))
            {
                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await Console.Error.WriteLineAsync(ExtraEndpointMessage).ConfigureAwait(false);
                return StartupKeyChecks.ExitCode;
            }

            await app.WaitForShutdownAsync(stopping).ConfigureAwait(false);
            return 0;
        }
        catch (OptionsValidationException exception)
        {
            // The failures name the setting and never its value (the validators' contract), so they are safe to print.
            await Console.Error.WriteLineAsync(
                InvalidConfigurationMessage + string.Join(" ", exception.Failures)).ConfigureAwait(false);
            return StartupKeyChecks.ExitCode;
        }
    }

    /// <summary>True when the server's bound addresses are exactly the one https listener.</summary>
    internal static bool OnlyGuardedAddress(IServer server, IPAddress address, int port)
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count != 1)
        {
            return false;
        }

        return Uri.TryCreate(addresses.First(), UriKind.Absolute, out var bound)
            && bound.Scheme == Uri.UriSchemeHttps
            && bound.Port == port
            && IPAddress.TryParse(bound.Host.Trim('[', ']'), out var host)
            && host.Equals(address);
    }
}
