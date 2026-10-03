using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NightTales.Infrastructure.Telemetry;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// QA gap tests for 09.9.3-T1: the user hash against an independent HMAC computation, non-GUID subjects,
/// the enricher's request/no-user paths, and OTLP endpoint grammar edges.
/// </summary>
public sealed class TelemetryEdgeTests
{
    private sealed class Factory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }

    private static IConfigurationRoot Settings(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static string ExpectedHash(string salt, string subject)
    {
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes(subject));
        return Convert.ToBase64String(digest).Replace('+', '-').Replace('/', '_').TrimEnd('=')[..16];
    }

    private static LogEvent NewEvent() =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Information, null, new MessageTemplateParser().Parse("x"), []);

    private static (UserHashEnricher Enricher, HttpContextAccessor Accessor) Create(string salt = "qa-edge-salt")
    {
        var accessor = new HttpContextAccessor();

        return (new UserHashEnricher(new UserHashEnricherOptions(salt), accessor), accessor);
    }

    private static string? UserIdOf(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(UserHashEnricher.PropertyName, out var value)
            ? ((ScalarValue)value).Value as string
            : null;

    [Theory]
    [InlineData("0197a3f2-1111-7000-8000-000000000001")]
    [InlineData("qa.user@example.com")]
    public void UserHash_matches_an_independent_hmac_computation(string subject)
    {
        var (enricher, _) = Create();

        var hash = enricher.Hash(subject);

        hash.ShouldBe(ExpectedHash("qa-edge-salt", subject));
        hash.ShouldMatch("^[A-Za-z0-9_-]{16}$");
    }

    [Fact]
    public void UserHash_depends_on_the_salt()
    {
        Create("a").Enricher.Hash("same").ShouldNotBe(Create("b").Enricher.Hash("same"));
    }

    [Fact]
    public void Enrich_without_a_request_adds_nothing()
    {
        var (enricher, _) = Create();
        var logEvent = NewEvent();

        enricher.Enrich(logEvent, new Factory());

        UserIdOf(logEvent).ShouldBeNull();
    }

    [Fact]
    public void Enrich_with_an_anonymous_request_adds_nothing()
    {
        var (enricher, accessor) = Create();
        accessor.HttpContext = new DefaultHttpContext();
        var logEvent = NewEvent();

        enricher.Enrich(logEvent, new Factory());

        UserIdOf(logEvent).ShouldBeNull();
    }

    [Theory]
    [InlineData("sub")]
    [InlineData(ClaimTypes.NameIdentifier)]
    public void Enrich_hashes_the_subject_and_never_stores_it_raw(string claimType)
    {
        const string raw = "0197a3f2-2222-7000-8000-00000000abcd";
        var (enricher, accessor) = Create();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claimType, raw)], "test")),
        };
        var logEvent = NewEvent();

        enricher.Enrich(logEvent, new Factory());

        UserIdOf(logEvent).ShouldBe(ExpectedHash("qa-edge-salt", raw));
        logEvent.Properties.Values.Select(v => v.ToString()).ShouldNotContain(v => v.Contains(raw));
    }

    [Fact]
    public void Enrich_keeps_an_existing_userId_property()
    {
        var (enricher, accessor) = Create();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "x")], "test")),
        };
        var logEvent = NewEvent();
        logEvent.AddOrUpdateProperty(new LogEventProperty(UserHashEnricher.PropertyName, new ScalarValue("preset")));

        enricher.Enrich(logEvent, new Factory());

        UserIdOf(logEvent).ShouldBe("preset");
    }

    [Theory]
    [InlineData("ftp://collector:21")]
    [InlineData("file:///tmp/otlp")]
    [InlineData("/relative/path")]
    [InlineData("grpc://collector:4317")]
    public void Endpoint_rejects_non_http_and_relative_values(string endpoint)
    {
        Should.Throw<InvalidOperationException>(
            () => TelemetryRegistration.ResolveExporterEndpoint(
                Settings(("Otlp:Enabled", "true"), ("Otlp:Endpoint", endpoint))));
    }

    [Fact]
    public void Endpoint_is_not_validated_while_disabled()
    {
        TelemetryRegistration.ResolveExporterEndpoint(
            Settings(("Otlp:Enabled", "false"), ("Otlp:Endpoint", "not a uri"))).ShouldBeNull();
    }

    [Fact]
    public void Endpoint_accepts_https_with_port()
    {
        TelemetryRegistration.ResolveExporterEndpoint(
                Settings(("Otlp:Enabled", "true"), ("Otlp:Endpoint", "https://collector.example:4317")))
            .ShouldNotBeNull().Scheme.ShouldBe("https");
    }
}
