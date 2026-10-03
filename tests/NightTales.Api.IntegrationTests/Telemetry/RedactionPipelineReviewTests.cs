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
/// added after <see cref="TelemetryRegistration.AddNtsTelemetry"/> stands in for the exporter; it copies what it
/// sees inside <c>Export</c>, because an <see cref="Activity"/> keeps changing after the exporter has it.
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
        using var pipeline = new TestPipeline();
        var sentinel = "pg" + "-sentinel-" + "x9y8";
        // Process-wide capture: select the span by a table name no other test uses.
        var table = "rev_t_" + Guid.NewGuid().ToString("N")[..12];

        // `Include Error Detail=true` is the setting under which Npgsql puts the offending values into the message.
        await using (var source = NpgsqlDataSource.Create(Database.ConnectionString + ";Include Error Detail=true"))
        {
            await using var command = source.CreateCommand(
                $"CREATE TEMP TABLE {table} (email text UNIQUE); INSERT INTO {table} VALUES ('{sentinel}'); INSERT INTO {table} VALUES ('{sentinel}')");
            await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
        }

        pipeline.Flush();
        var span = pipeline.Exporter.Spans.Single(s => s.Tags.GetValueOrDefault("db.query.text") is string text && text.Contains(table));
        var tags = span.Events.Single(e => e.Name == "exception").Tags.ToDictionary(t => t.Key, t => (t.Value as string)!);

        tags["exception.message"].ShouldStartWith("23505: duplicate key value violates unique constraint");
        tags["exception.message"].ShouldNotContain("DETAIL");
        tags["exception.message"].ShouldNotContain("\n");
        tags["exception.stacktrace"].ShouldContain("Npgsql.PostgresException");
        tags["exception.stacktrace"].ShouldNotContain("Exception data:");
        tags["exception.stacktrace"].ShouldNotContain("TableName:");
        foreach (var value in tags.Values)
        {
            value.ShouldNotContain(sentinel);
        }
    }

    [Fact]
    public void Postgres_detail_shaped_like_a_stack_frame_does_not_resume_the_output()
    {
        using var pipeline = new TestPipeline();
        using var source = new ActivitySource(SourceName);
        var marker = "op-" + Guid.NewGuid().ToString("N")[..8];
        var secret = "raw" + "-secret-" + "q1w2";

        using (var activity = source.StartActivity(marker)!)
        {
            activity.AddException(new PostgresException(
                "duplicate key", "ERROR", "ERROR", "23505", detail: $"private\n   at {secret}\n--- End of stack trace ---\nprivate-two"));
        }

        pipeline.Flush();
        var span = pipeline.Exporter.Spans.Single(s => s.OperationName == marker);
        var text = string.Join('\n', span.Events.SelectMany(e => e.Tags).Select(t => t.Key + "=" + t.Value));

        text.ShouldNotContain(secret);
        text.ShouldNotContain("private");
        text.ShouldContain("23505: duplicate key");
    }

    [Fact]
    public void Detail_like_text_of_a_non_postgres_exception_is_kept()
    {
        using var pipeline = new TestPipeline();
        using var source = new ActivitySource(SourceName);
        var marker = "op-" + Guid.NewGuid().ToString("N")[..8];

        using (var activity = source.StartActivity(marker)!)
        {
            activity.AddException(new InvalidOperationException("failed\nDETAIL: episodeId=7"));
        }

        pipeline.Flush();
        var span = pipeline.Exporter.Spans.Single(s => s.OperationName == marker);
        var tags = span.Events.Single().Tags.ToDictionary(t => t.Key, t => (t.Value as string)!);

        tags["exception.message"].ShouldContain("episodeId=7");
        tags["exception.stacktrace"].ShouldContain("episodeId=7");
    }

    [Fact]
    public void String_attributes_of_every_span_event_are_query_redacted()
    {
        using var pipeline = new TestPipeline();
        using var source = new ActivitySource(SourceName);
        var marker = "op-" + Guid.NewGuid().ToString("N")[..8];

        using (var activity = source.StartActivity(marker)!)
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
        var span = pipeline.Exporter.Spans.Single(s => s.OperationName == marker);
        var text = string.Join('\n', span.Events.SelectMany(e => e.Tags).Select(t => t.Key + "=" + t.Value));

        text.ShouldNotContain(Raw);
        text.ShouldContain("page=2", customMessage: "unlisted text survives");
        span.Events.First().Tags.Single(t => t.Key == "count").Value.ShouldBe(3, "non-string values are untouched");
    }

    [Fact]
    public void An_exporter_position_processor_added_after_AddNtsTelemetry_sees_masked_tags()
    {
        using var pipeline = new TestPipeline();
        using var source = new ActivitySource(SourceName);
        var marker = "op-" + Guid.NewGuid().ToString("N")[..8];

        using (var activity = source.StartActivity(marker)!)
        {
            activity.SetTag("url.query", $"ticket={Raw}&page=2");
            activity.SetTag("url.full", $"https://example.test/hub?ticket={Raw}");
        }

        pipeline.Flush();
        var span = pipeline.Exporter.Spans.Single(s => s.OperationName == marker);

        span.Tags["url.query"].ShouldBeOfType<string>().ShouldNotContain(Raw);
        span.Tags["url.full"].ShouldBeOfType<string>().ShouldNotContain(Raw);
        span.Tags["url.query"].ShouldBeOfType<string>().ShouldContain("page=2");
    }

    [Theory]
    [InlineData("Serilog:WriteTo:0:Name", "Serilog:WriteTo")]
    [InlineData("Serilog:AuditTo:0:Name", "Serilog:AuditTo")]
    public void A_sink_declared_in_Serilog_configuration_fails_start_up(string key, string section)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            Configuration = new ConfigurationManager(),
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = "Console" });

        var failure = Should.Throw<InvalidOperationException>(() => builder.AddNtsTelemetry("nts-test"));

        failure.Message.ShouldContain(section);
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

    /// <summary>What the exporter saw, copied inside <c>Export</c>.</summary>
    private sealed record SpanSnapshot(
        string OperationName,
        Dictionary<string, object?> Tags,
        List<(string Name, Dictionary<string, object?> Tags)> Events);

    private sealed class Collector : BaseExporter<Activity>
    {
        public ConcurrentQueue<SpanSnapshot> Spans { get; } = new();

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var span in batch)
            {
                Spans.Enqueue(new SpanSnapshot(
                    span.OperationName,
                    span.TagObjects.ToDictionary(t => t.Key, t => t.Value),
                    [.. span.Events.Select(e => (e.Name, e.Tags.ToDictionary(t => t.Key, t => t.Value)))]));
            }

            return ExportResult.Success;
        }
    }
}
