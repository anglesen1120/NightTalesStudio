using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.Application.Telemetry;
using NightTales.Infrastructure.Jobs;
using NightTales.Infrastructure.Persistence.Outbox;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Extensions.Logging;
using Serilog.Sinks.OpenTelemetry;

namespace NightTales.Infrastructure.Telemetry;

/// <summary>
/// The shared observability wiring of the Api and the Worker (09 README BD12, 09.9.3-T1): Serilog writes
/// compact JSON to stdout and, when the exporter is on, to the OTLP collector; OpenTelemetry traces and
/// metrics cover ASP.NET Core, <c>HttpClient</c> and PostgreSQL (<c>Npgsql.OpenTelemetry</c>) — never EF Core
/// (WF09) — plus the platform's own activity sources and meters.
/// </summary>
public static class TelemetryRegistration
{
    /// <summary>The master switch of the OTLP exporter and the Serilog OpenTelemetry sink.</summary>
    public const string OtlpEnabledKey = "Otlp:Enabled";

    /// <summary>The collector's gRPC endpoint; falls back to <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>.</summary>
    public const string OtlpEndpointKey = "Otlp:Endpoint";

    /// <summary>The trace sampling ratio in <c>[0, 1]</c>; default <c>1.0</c>.</summary>
    public const string SampleRatioKey = "Otel:SampleRatio";

    /// <summary>The configuration key of the log user-hash salt (a secret outside Development).</summary>
    public const string UserHashSaltKey = "Logging:UserHashSalt";

    private const string EndpointEnvironmentVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The console queue: a full queue drops events instead of blocking a request thread (PERF-1).</summary>
    private const int ConsoleQueueSize = 10_000;

    private const string UrlQueryTag = "url.query";

    /// <summary>Registers Serilog, OpenTelemetry and the custom metrics for one host.</summary>
    /// <param name="builder">The host builder (Api or Worker).</param>
    /// <param name="serviceName">The OpenTelemetry <c>service.name</c>: <c>nts-api</c> or <c>nts-worker</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IHostApplicationBuilder AddNtsTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var configuration = builder.Configuration;
        var environmentName = builder.Environment.EnvironmentName;
        var exporterEndpoint = ResolveExporterEndpoint(configuration, builder.Environment);
        var sampleRatio = ResolveSampleRatio(configuration);
        var userHashSalt = ResolveUserHashSalt(configuration, builder.Environment);
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";
        var instance = $"{Environment.MachineName}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}";

        // The Api is the trust edge: an inbound `baggage` header is dropped (only `traceparent`/`tracestate`
        // are read), so a caller cannot inject key/value pairs that downstream spans or logs would carry.
        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<NightTalesMetrics>();
        builder.Services.AddSingleton(new UserHashEnricherOptions(userHashSalt));
        builder.Services.AddSingleton<UserHashEnricher>();

        // A provider rather than AddSerilog's factory replacement: the host's other ILoggerProviders
        // (the test harness's capture sinks, Hangfire's logger bridge) keep receiving every event.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<Serilog.ILogger>(services =>
        {
            var loggerConfiguration = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.With(services.GetRequiredService<UserHashEnricher>());

            // Every sink sits behind the exception sanitizer; the console behind a bounded queue that drops
            // when full, so a stalled stdout pipe can never block a request thread. The sinks live in one
            // inner logger (events are enriched and level-filtered before they reach it).
            var sinks = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Async(
                    console => console.Console(new CompactJsonFormatter()),
                    bufferSize: ConsoleQueueSize,
                    blockWhenFull: false);

            if (exporterEndpoint is not null)
            {
                sinks.WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = exporterEndpoint.AbsoluteUri;
                    options.Protocol = OtlpProtocol.Grpc;
                    options.OnBeginSuppressInstrumentation = SuppressInstrumentationScope.Begin;
                    options.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = serviceName,
                        ["service.version"] = version,
                        ["deployment.environment"] = environmentName,
                        ["instance"] = instance,
                    };
                });
            }

            loggerConfiguration.WriteTo.Sink(new ExceptionSanitizingSink(sinks.CreateLogger()));

            return loggerConfiguration.CreateLogger();
        });
        builder.Services.AddSingleton<ILoggerProvider>(
            services => new SerilogLoggerProvider(services.GetRequiredService<Serilog.ILogger>(), dispose: true));
        builder.Services.AddSingleton(
            services => new Serilog.Extensions.Hosting.DiagnosticContext(services.GetRequiredService<Serilog.ILogger>()));
        builder.Services.AddSingleton<Serilog.IDiagnosticContext>(
            services => services.GetRequiredService<Serilog.Extensions.Hosting.DiagnosticContext>());

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: version)
                .AddAttributes(
                [
                    new KeyValuePair<string, object>("deployment.environment", environmentName),
                    new KeyValuePair<string, object>("instance", instance),
                ]))
            .WithTracing(tracing => tracing
                .SetSampler(CreateEdgeSampler(sampleRatio))
                .AddAspNetCoreInstrumentation(options =>
                    // The query of a request URL can carry a single-use credential (`?ticket=` on the hub):
                    // no span ever exports it (09.8.1-T2, 09.9.3-T1 SEC-2).
                    options.EnrichWithHttpRequest = static (activity, _) => activity.SetTag(UrlQueryTag, null))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddSource(JobTelemetryFilter.ActivitySourceName)
                .AddSource(OutboxDispatcher.ActivitySourceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("NightTales*"));

        if (exporterEndpoint is not null)
        {
            telemetry.UseOtlpExporter(OtlpExportProtocol.Grpc, exporterEndpoint);
        }

        return builder;
    }

    /// <summary>
    /// The trace sampler of both hosts: head sampling at <paramref name="ratio"/> for a local root <em>and</em>
    /// for a remote parent, whatever its sampled flag says. A plain <c>ParentBasedSampler</c> would honour an
    /// inbound <c>traceparent</c> with the sampled bit set and let any caller force 100 % sampling.
    /// </summary>
    /// <param name="ratio">The configured <c>Otel:SampleRatio</c>.</param>
    /// <returns>The sampler.</returns>
    public static Sampler CreateEdgeSampler(double ratio)
    {
        var ratioSampler = new TraceIdRatioBasedSampler(ratio);

        return new ParentBasedSampler(
            ratioSampler,
            remoteParentSampled: ratioSampler,
            remoteParentNotSampled: ratioSampler);
    }

    /// <summary>
    /// The exporter endpoint, or <see langword="null"/> when the exporter is off: <c>Otlp:Enabled</c> is
    /// false, or no endpoint is configured (an exporter without a target would only retry against localhost).
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The validated absolute <c>http</c>/<c>https</c> URI, or <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">The configured endpoint is not an absolute http(s) URI.</exception>
    public static Uri? ResolveExporterEndpoint(IConfiguration configuration, IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.GetValue(OtlpEnabledKey, true))
        {
            return null;
        }

        var raw = configuration[OtlpEndpointKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"'{OtlpEndpointKey}' must be an absolute http or https URI.");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException($"'{OtlpEndpointKey}' must not carry credentials in the URI.");
        }

        // Telemetry carries user hashes and request metadata: outside Development it leaves the host only
        // over TLS, or to a collector on the same machine.
        if (environment is not null
            && !environment.IsDevelopment()
            && endpoint.Scheme != Uri.UriSchemeHttps
            && !endpoint.IsLoopback)
        {
            throw new InvalidOperationException(
                $"'{OtlpEndpointKey}' must be https or a loopback address outside Development.");
        }

        return endpoint;
    }

    private static double ResolveSampleRatio(IConfiguration configuration)
    {
        var ratio = configuration.GetValue(SampleRatioKey, 1d);
        if (ratio is < 0d or > 1d || double.IsNaN(ratio))
        {
            throw new InvalidOperationException($"'{SampleRatioKey}' must be between 0 and 1.");
        }

        return ratio;
    }

    private static string ResolveUserHashSalt(IConfiguration configuration, IHostEnvironment environment)
    {
        var salt = configuration[UserHashSaltKey];
        if (!string.IsNullOrWhiteSpace(salt))
        {
            return salt;
        }

        // The build-time OpenAPI generator (09.1.5-T1) boots the Api in-process to read its document; it
        // writes no log line a user could be correlated through, so it needs no real salt.
        if (environment.IsDevelopment()
            || string.Equals(
                Assembly.GetEntryAssembly()?.GetName().Name, "GetDocument.Insider", StringComparison.Ordinal))
        {
            return UserHashEnricher.DevelopmentSalt;
        }

        throw new InvalidOperationException(
            $"Configuration value '{UserHashSaltKey}' is required outside Development.");
    }
}
