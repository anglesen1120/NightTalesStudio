using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace NightTales.Fetcher;

/// <summary>
/// The fetcher's metrics pipeline (02.5.2-T3 §Observability). Metrics only, exported over OTLP behind the switch the Api and
/// the Worker use (<c>Otlp:Enabled</c> + <c>Otlp:Endpoint</c>, or <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>); no Prometheus listener.
/// There is deliberately no tracing and no <c>HttpClient</c> instrumentation: a source URL would otherwise reach a span as
/// <c>url.full</c>/<c>url.query</c> (02 D19), and the host has nothing else worth tracing.
/// </summary>
public static class FetcherTelemetry
{
    /// <summary>The OpenTelemetry <c>service.name</c> of the host.</summary>
    public const string ServiceName = "nts-fetcher";

    /// <summary>Registers the meter and, when enabled, the OTLP metrics exporter.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddFetcherTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithMetrics(metrics => metrics.AddMeter(FetcherMetrics.MeterName));

        if (ResolveEndpoint(configuration) is { } endpoint)
        {
            telemetry.UseOtlpExporter(OtlpExportProtocol.Grpc, endpoint);
        }

        return services;
    }

    private static Uri? ResolveEndpoint(IConfiguration configuration)
    {
        if (!configuration.GetValue("Otlp:Enabled", false))
        {
            return null;
        }

        var raw = configuration["Otlp:Endpoint"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : null;
    }
}
