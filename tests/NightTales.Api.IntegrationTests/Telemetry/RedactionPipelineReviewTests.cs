using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Infrastructure.Telemetry;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Telemetry;

/// <summary>
/// 09.9.3-T2 review fixes: span-event redaction (SEC-SPAN-EXC-DETAIL, QA-3), the sink bypass through
/// <c>Serilog:WriteTo</c> (SEC-SINK-BYPASS-CONFIG) and the processor order (SEC-TEST-ORDER). An in-memory processor
/// added after <see cref="TelemetryRegistration.AddNtsTelemetry"/> stands in for the exporter.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedactionPipelineReviewTests(TestDatabase database) : IntegrationTest(database)
{
    // Built by concatenation: a literal this shape trips the secret scanner.
    private static readonly string Raw = "review" + "-raw-" + "a1b2c3";
    private const string SourceName = "nts-review-test";
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Postgres_exception_event_carries_no_detail_and_only_the_sanitized_shape()
    {
        using var pipeline = Pipeline();
        var sentinel = "pg" + "-sentinel-" + "x9y8";

        // `Include Error Detail=true` is the setting under which Npgsql puts the offending values into the message.
        await using (var source = NpgsqlDataSource.Create(Database.ConnectionString + ";Include Error Detail=true"))
        {
            await using var command = source.CreateCommand(
                $"CREATE TEMP TABLE review_t (email text UNIQUE); INSERT INTO review_t VALUES ('{sentinel}'); INSERT INTO review_t VALUES ('{sentinel}')");
            await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
        }

        pipeline.Flush();
        var activityEvent = pipeline.Exporter.Spans.SelectMany(span => span.Events).Single(e => e.Name == "exception");
        var tags = activityEvent.Tags!.ToDictionary(t => t.Key, t => (t.Value as string)!);

        tags["exception.message"].ShouldStartWith("23505: duplicate key value violates unique constraint");
        tags["exception.message"].ShouldNotContain("DETAIL");
        tags["exception.message"].ShouldNotContain("\n");
        tags["exception.stacktrace"].ShouldContain("Npgsql.PostgresException");
        tags["exception.stacktrace"].ShouldContain("   at ", customMessage: "the frames survive");
        tags["exception.stacktrace"].ShouldNotContain("Exception data:");
        tags["exception.stacktrace"].ShouldNotContain("TableName:");
        foreach (var value in tags.Values)
        {
            value.ShouldNotBeNull().ShouldNotContain(sentinel);
        }
    }

    [Fact]
    public void String_attributes_of_every_span_event_are_query_redacted()
    {
        using var pipeline = Pipeline();
        using var source = new ActivitySource(SourceName);

        using (var activity = source.StartActivity("event-carrier")!)
        {
            activity.AddEvent(new ActivityEvent("custom", tags: new ActivityTagsCollection
            {
                ["http.url"] = $"https://example.test/hub?ticket={Raw}",
                ["note"] = $"retry GET /x?ticket={Raw}&page=2",
                ["count"] = 3,
            }));
            activity.AddException(new InvalidOperationException($"failed https://example.test/a?ticket={Raw}"));
        }

        pipeline.Flush();
        var span = pipeline.Exporter.Spans.Single(s => s.Source.Name == SourceName);
        var text = string.Join('\n', span.Events.SelectMany(e => e.Tags).Select(t => t.Key + "=" + t.Value));

        text.ShouldNotContain(Raw);
        text.ShouldContain("page=2", customMessage: "unlisted text survives");
        span.Events.First().Tags!.Single(t => t.Key == "count").Value.ShouldBe(3, "non-string values are untouched");
    }

    [Fact]
    public void An_exporter_position_processor_added_after_AddNtsTelemetry_sees_masked_tags()
    {
        using var pipeline = Pipeline();
        using var source = new ActivitySource(SourceName);

        using (var activity = source.StartActivity("order-pin")!)
        {
            activity.SetTag("url.query", $"ticket={Raw}&page=2");
            activity.SetTag("url.full", $"https://example.test/hub?ticket={Raw}");
        }

        pipeline.Flush();
        var span = pipeline.Exporter.Spans.Single(s => s.Source.Name == SourceName);

        span.GetTagItem("url.query").ShouldBeOfType<string>().ShouldNotContain(Raw);
        span.GetTagItem("url.full").ShouldBeOfType<string>().ShouldNotContain(Raw);
        span.GetTagItem("url.query").ShouldBeOfType<string>().ShouldContain("page=2");
    }

    [Theory]
    [InlineData("Serilog:WriteTo:0:Name")]
    [InlineData("Serilog:AuditTo:0:Name")]
    public void A_sink_declared_in_Serilog_configuration_fails_start_up(string key)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            Configuration = new ConfigurationManager(),
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = "Console" });

        var failure = Should.Throw<InvalidOperationException>(() => builder.AddNtsTelemetry("nts-test"));

        failure.Message.ShouldContain(key[..key.LastIndexOf(":0", StringComparison.Ordinal)]);
    }

    [Fact]
    public void Serilog_minimum_level_configuration_still_starts()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            Configuration = new ConfigurationManager(),
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Warning",
        });

        Should.NotThrow(() => builder.AddNtsTelemetry("nts-test"));
    }

    private static TestPipeline Pipeline() => new();

    private sealed class TestPipeline : IDisposable
    {
        private readonly IHost _host;
        private readonly TracerProvider _tracer;

        public TestPipeline()
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                EnvironmentName = Environments.Development,
                Configuration = new ConfigurationManager(),
            });
            builder.AddNtsTelemetry("nts-test");
            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing
                .AddSource(SourceName)
                .AddProcessor(new SimpleActivityExportProcessor(Exporter)));
            _host = builder.Build();
            _tracer = _host.Services.GetRequiredService<TracerProvider>();
        }

        public Collector Exporter { get; } = new();

        public void Flush() => _tracer.ForceFlush(10_000).ShouldBeTrue();

        public void Dispose() => _host.Dispose();
    }

    private sealed class Collector : BaseExporter<Activity>
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
