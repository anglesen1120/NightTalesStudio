using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NightTales.Infrastructure.Telemetry;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Trace;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// 09.9.3-T1 review fixes: the edge sampler and baggage drop (SEC-3), the OTLP endpoint guard (SEC-5), the
/// PostgreSQL exception rendering (SEC-6) and the start-up failure event (CR-7).
/// </summary>
public sealed class TelemetryHardeningTests
{
    private const string Detail = "Key (email)=(someone@example.com) already exists.";

    [Fact]
    public void Edge_sampler_ignores_a_remote_sampled_flag_when_the_ratio_is_zero()
    {
        var sampler = TelemetryRegistration.CreateEdgeSampler(0d);

        sampler.ShouldSample(Parameters(remoteSampled: true)).Decision.ShouldBe(SamplingDecision.Drop);
        sampler.ShouldSample(Parameters(remoteSampled: false)).Decision.ShouldBe(SamplingDecision.Drop);
    }

    [Fact]
    public void Edge_sampler_samples_a_remote_not_sampled_parent_when_the_ratio_is_one()
    {
        var sampler = TelemetryRegistration.CreateEdgeSampler(1d);

        sampler.ShouldSample(Parameters(remoteSampled: false)).Decision.ShouldBe(SamplingDecision.RecordAndSample);
        sampler.ShouldSample(Parameters(remoteSampled: true)).Decision.ShouldBe(SamplingDecision.RecordAndSample);
    }

    [Fact]
    public void Inbound_baggage_is_dropped_but_traceparent_is_read()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            Configuration = new ConfigurationManager(),
        });
        builder.AddNtsTelemetry("nts-test");

        var carrier = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["baggage"] = "tenant=attacker,role=owner",
        };

        var context = Propagators.DefaultTextMapPropagator.Extract(
            default,
            carrier,
            (source, key) => source.TryGetValue(key, out var value) ? [value] : []);

        context.Baggage.Count.ShouldBe(0, "an inbound baggage header must not cross the edge");
        context.ActivityContext.TraceId.ToString().ShouldBe("0af7651916cd43dd8448eb211c80319c");
    }

    [Theory]
    [InlineData("http://collector.internal:4317")]
    [InlineData("http://10.0.0.5:4317")]
    public void Otlp_endpoint_outside_development_must_be_https_or_loopback(string endpoint)
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => TelemetryRegistration.ResolveExporterEndpoint(Settings(endpoint), Environment(Environments.Production)));

        exception.Message.ShouldNotContain(endpoint, Case.Sensitive);
    }

    [Theory]
    [InlineData("https://collector.internal:4317", "Production")]
    [InlineData("http://127.0.0.1:4317", "Production")]
    [InlineData("http://localhost:4317", "Staging")]
    [InlineData("http://[::1]:4317", "Production")]
    [InlineData("http://collector.internal:4317", "Development")]
    public void Otlp_endpoint_https_loopback_or_development_is_accepted(string endpoint, string environment)
    {
        TelemetryRegistration.ResolveExporterEndpoint(Settings(endpoint), Environment(environment))
            .ShouldBe(new Uri(endpoint));
    }

    [Theory]
    [InlineData("https://user:secret@collector.internal:4317", "Production")]
    [InlineData("http://user@127.0.0.1:4317", "Development")]
    public void Otlp_endpoint_with_userinfo_is_rejected_in_every_environment(string endpoint, string environment)
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => TelemetryRegistration.ResolveExporterEndpoint(Settings(endpoint), Environment(environment)));

        exception.Message.ShouldNotContain("secret", Case.Sensitive);
    }

    [Fact]
    public void Sanitizer_keeps_type_and_trimmed_message_of_a_postgres_error_but_never_its_detail()
    {
        var error = Postgres(Detail);
        var wrapped = new InvalidOperationException("save failed", error);

        var sanitized = LogExceptionSanitizer.Sanitize(wrapped).ShouldNotBeNull();

        var text = sanitized.ToString();
        text.ShouldContain(typeof(InvalidOperationException).FullName!);
        text.ShouldContain(typeof(PostgresException).FullName!);
        text.ShouldContain("23505");
        text.ShouldContain("duplicate key value violates unique constraint");
        text.ShouldNotContain("someone@example.com");
        text.ShouldNotContain(Detail);
    }

    [Fact]
    public void Sanitizer_returns_an_exception_without_postgres_error_unchanged()
    {
        var plain = new InvalidOperationException("boom", new TimeoutException("inner"));

        LogExceptionSanitizer.Sanitize(plain).ShouldBeSameAs(plain);
        LogExceptionSanitizer.Sanitize(null).ShouldBeNull();
    }

    [Fact]
    public void Sanitizing_sink_forwards_a_rewritten_event_with_properties_intact()
    {
        var inner = new CapturingSink();
        var sink = new ExceptionSanitizingSink(inner);
        var template = new MessageTemplateParser().Parse("failed {Name}");

        sink.Emit(new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            Postgres(Detail),
            template,
            [new LogEventProperty("Name", new ScalarValue("x"))]));

        var forwarded = inner.Events.ShouldHaveSingleItem();
        forwarded.Level.ShouldBe(LogEventLevel.Error);
        forwarded.Properties["Name"].ToString().ShouldBe("\"x\"");
        forwarded.Exception.ShouldNotBeNull().ToString().ShouldNotContain("someone@example.com");
    }

    [Fact]
    public void Missing_salt_in_production_is_reported_as_one_json_failure_event()
    {
        using var output = new StringWriter();
        var logger = BootstrapLogging.Create(output);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
            Configuration = new ConfigurationManager(),
        });

        var failure = Should.Throw<InvalidOperationException>(() => builder.AddNtsTelemetry("nts-test"));
        BootstrapLogging.ReportFailure(failure, logger);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = lines.ShouldHaveSingleItem();
        using var json = JsonDocument.Parse(line);
        json.RootElement.GetProperty("@l").GetString().ShouldBe("Fatal");
        json.RootElement.GetProperty("@mt").GetString().ShouldBe(BootstrapLogging.FailureMessage);
        json.RootElement.GetProperty("@x").GetString().ShouldNotBeNull().ShouldContain("Logging:UserHashSalt");
    }

    [Fact]
    public void Failure_event_of_a_postgres_error_carries_no_detail()
    {
        using var output = new StringWriter();

        BootstrapLogging.ReportFailure(Postgres(Detail), BootstrapLogging.Create(output));

        output.ToString().ShouldNotContain("someone@example.com");
        output.ToString().ShouldContain("23505");
    }

    private static PostgresException Postgres(string detail) =>
        new(
            "duplicate key value violates unique constraint \"ux_users_email\"",
            "ERROR",
            "ERROR",
            "23505",
            detail: detail);

    private static SamplingParameters Parameters(bool remoteSampled) =>
        new(
            new ActivityContext(
                ActivityTraceId.CreateRandom(),
                ActivitySpanId.CreateRandom(),
                remoteSampled ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None,
                traceState: null,
                isRemote: true),
            ActivityTraceId.CreateRandom(),
            "GET /x",
            ActivityKind.Server);

    private static IHostEnvironment Environment(string name) =>
        Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = name,
            Configuration = new ConfigurationManager(),
        }).Environment;

    private static IConfigurationRoot Settings(string endpoint) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Otlp:Endpoint"] = endpoint })
            .Build();

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }
}
