using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Infrastructure.Telemetry;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// 09.9.3-T1 acceptance: <c>AddNpgsql()</c> produces a PostgreSQL span for a real query, observed on the very
/// tracer provider <see cref="TelemetryRegistration.AddNtsTelemetry"/> configures (an in-memory processor
/// stands in for the OTLP exporter) — without any EF Core instrumentation.
/// </summary>
/// <param name="database">This class's isolated database.</param>
public sealed class NpgsqlSpanTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task A_real_query_produces_an_Npgsql_span_on_the_configured_tracer_provider()
    {
        var exporter = new CollectingExporter();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            Configuration = new ConfigurationManager(),
        });
        builder.AddNtsTelemetry("nts-test");
        builder.Services.ConfigureOpenTelemetryTracerProvider(
            tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(exporter)));

        using var host = builder.Build();
        var tracer = host.Services.GetRequiredService<TracerProvider>();

        await using (var source = NpgsqlDataSource.Create(Database.ConnectionString))
        {
            await using var command = source.CreateCommand("SELECT 1");
            (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        }

        tracer.ForceFlush(10_000).ShouldBeTrue();

        exporter.Spans.ShouldContain(
            span => span.Source.Name == "Npgsql" && span.Kind == ActivityKind.Client,
            "the configured provider must export the driver's own span for the query");
    }

    private sealed class CollectingExporter : BaseExporter<Activity>
    {
        public ConcurrentQueue<Activity> Spans { get; } = new();

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var span in batch)
            {
                Spans.Enqueue(span);
            }

            return ExportResult.Success;
        }
    }
}
