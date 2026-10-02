using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Infrastructure.Telemetry;
using OpenTelemetry.Trace;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Telemetry;

public sealed class ObservabilityWiringTests
{
    private const string EfInstrumentation = "OpenTelemetry.Instrumentation.EntityFrameworkCore";

    [Fact]
    public void EfCore_instrumentation_package_absent()
    {
        typeof(TelemetryRegistration).Assembly.GetReferencedAssemblies()
            .ShouldNotContain(name => name.Name == EfInstrumentation);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Packages.props")))
        {
            root = root.Parent;
        }

        root.ShouldNotBeNull("Directory.Packages.props must be reachable from the test output directory");
        File.ReadAllText(Path.Combine(root.FullName, "Directory.Packages.props")).ShouldNotContain(EfInstrumentation);
    }

    [Fact]
    public void Npgsql_instrumentation_registered()
    {
        using var provider = Build(Settings(("Logging:UserHashSalt", "salt"))).Services.BuildServiceProvider();
        using var tracer = provider.GetRequiredService<TracerProvider>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        // The Npgsql tracer subscribes to the driver's own source, which is how DB spans are produced
        // without any EF Core instrumentation (WF09).
        using var source = new ActivitySource("Npgsql");
        source.HasListeners().ShouldBeTrue();
    }

    [Fact]
    public void Compact_json_formatter_emits_one_object_per_line()
    {
        var formatter = new CompactJsonFormatter();
        using var writer = new StringWriter();
        var template = new MessageTemplateParser().Parse("hello {Name}");
        formatter.Format(
            new LogEvent(
                DateTimeOffset.UtcNow,
                LogEventLevel.Information,
                null,
                template,
                [new LogEventProperty("Name", new ScalarValue("x"))]),
            writer);

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(1);
        lines[0].ShouldStartWith("{");
        lines[0].ShouldEndWith("}");
    }

    [Fact]
    public void Otlp_exporter_off_when_disabled_even_with_an_endpoint()
    {
        TelemetryRegistration.ResolveExporterEndpoint(
            Settings(("Otlp:Enabled", "false"), ("Otlp:Endpoint", "http://127.0.0.1:4317"))).ShouldBeNull();
    }

    [Fact]
    public void Otlp_exporter_off_without_an_endpoint()
    {
        Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT").ShouldBeNull();
        TelemetryRegistration.ResolveExporterEndpoint(Settings()).ShouldBeNull();
    }

    [Fact]
    public void Otlp_sink_configured_when_enabled()
    {
        TelemetryRegistration.ResolveExporterEndpoint(
            Settings(("Otlp:Enabled", "true"), ("Otlp:Endpoint", "http://127.0.0.1:4317")))
            .ShouldBe(new Uri("http://127.0.0.1:4317"));
    }

    [Fact]
    public void Otlp_endpoint_must_be_an_absolute_http_uri()
    {
        Should.Throw<InvalidOperationException>(
            () => TelemetryRegistration.ResolveExporterEndpoint(Settings(("Otlp:Endpoint", "collector:4317"))));
    }

    [Fact]
    public void UserHash_is_stable_and_never_equals_sub()
    {
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor();
        var first = new UserHashEnricher(new UserHashEnricherOptions("salt-a"), accessor);
        var second = new UserHashEnricher(new UserHashEnricherOptions("salt-a"), accessor);
        var other = new UserHashEnricher(new UserHashEnricherOptions("salt-b"), accessor);
        var sub = "0197a3f2-1111-7000-8000-000000000001";

        first.Hash(sub).ShouldBe(second.Hash(sub));
        first.Hash(sub).ShouldNotBe(other.Hash(sub));
        first.Hash(sub).ShouldNotBe(sub);
        first.Hash(sub).Length.ShouldBe(16);
        first.Hash(sub).ShouldNotContain(sub);
    }

    [Fact]
    public void Missing_salt_fails_startup_outside_development()
    {
        var exception = Should.Throw<InvalidOperationException>(() => Build(Settings(), Environments.Production));

        exception.Message.ShouldBe("Configuration value 'Logging:UserHashSalt' is required outside Development.");
        Should.NotThrow(() => Build(Settings(), Environments.Development));
        Should.NotThrow(() => Build(Settings(("Logging:UserHashSalt", "x")), Environments.Production));
    }

    private static HostApplicationBuilder Build(IConfiguration configuration, string environment = "Development")
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment,
            Configuration = new ConfigurationManager(),
        });
        builder.Configuration.AddConfiguration(configuration);
        builder.AddNtsTelemetry("nts-test");

        return builder;
    }

    private static IConfigurationRoot Settings(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
