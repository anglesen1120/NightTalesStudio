using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.Realtime;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Time;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Telemetry;

/// <summary>
/// 09.9.3-T2 (09 README BD12/BD16, 09:233/249): a raw <c>ticket</c>, <c>token</c>, <c>access_token</c>,
/// <c>code</c> or <c>state</c> value reaches no captured log event (properties, rendered message, exception)
/// and no span attribute; the probe route logs the query and the headers the way a framework line would.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedactionTests(TestDatabase database) : IntegrationTest(database)
{
    // Built by concatenation: a literal this shape trips the secret scanner.
    private static readonly string RawTicket = "hub" + "-ticket-" + "7f3a9c";
    private static readonly string RawToken = "reset" + "-token-" + "51d2e8";
    private static readonly string RawAccess = "access" + "-value-" + "c04b17";
    private static readonly string RawCode = "oauth" + "-code-" + "88e1aa";
    private static readonly string RawState = "oauth" + "-state-" + "29fd03";
    private static readonly string[] AllRaw = [RawTicket, RawToken, RawAccess, RawCode, RawState];

    private const string ProbePath = "/probe/redaction";
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Hub_connect_ticket_absent_from_logs_and_spans()
    {
        var sink = new TestLogSink();
        using var factory = Factory(sink);
        _ = factory.Services;
        using var spans = new ActivityCapture();
        using var client = factory.AnonymousClient();
        // Unique key name: query keys survive masking (`<redacted>`/`Redacted`), so it picks this request's span.
        var marker = "m" + Guid.NewGuid().ToString("N");

        using var response = await client.GetAsync($"{EpisodeHub.Path}?ticket={RawTicket}&{marker}=1", Ct);

        var request = await spans.WhenStopped(
            a => a.GetTagItem("url.path") as string == EpisodeHub.Path
                && (a.GetTagItem("url.query") as string ?? string.Empty).Contains(marker, StringComparison.Ordinal), Ct);
        (request.GetTagItem("url.query") as string).ShouldNotBeNull().ShouldStartWith("?ticket=<redacted>");
        spans.AllText().ShouldNotContain(RawTicket, Case.Sensitive);
        sink.AllText().ShouldNotContain(RawTicket, Case.Sensitive);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("code")]
    [InlineData("state")]
    public async Task Reset_link_and_oauth_values_absent_from_logs_and_spans(string key)
    {
        var raw = key switch { "token" => RawToken, "access_token" => RawAccess, "code" => RawCode, _ => RawState };
        var sink = new TestLogSink();
        using var factory = Factory(sink);
        _ = factory.Services;
        using var spans = new ActivityCapture();
        using var client = factory.AnonymousClient();
        var marker = "m" + Guid.NewGuid().ToString("N");

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/auth/reset-password?{key}={raw}&episodeId=1&{marker}=1", new { }, Ct);

        var request = await spans.WhenStopped(
            a => a.GetTagItem("url.path") as string == "/api/v1/auth/reset-password"
                && (a.GetTagItem("url.query") as string ?? string.Empty).Contains(marker, StringComparison.Ordinal), Ct);
        // The OTel SDK itself masks the unlisted values (`episodeId=Redacted`); the listed key is ours.
        (request.GetTagItem("url.query") as string).ShouldNotBeNull().ShouldStartWith($"?{key}=<redacted>");
        spans.AllText().ShouldNotContain(raw, Case.Sensitive);
        sink.AllText().ShouldNotContain(raw, Case.Sensitive);
    }

    [Fact]
    public async Task Authorization_cookie_headers_are_masked()
    {
        var sink = new TestLogSink();
        using var factory = Factory(sink);
        using var client = factory.AnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ProbePath}/{Guid.NewGuid()}");
        request.Headers.Add("Authorization", "Bearer " + RawAccess);
        request.Headers.Add("Cookie", "sid=" + RawToken);
        request.Headers.Add("Accept-Language", "vi");

        using var response = await client.SendAsync(request, Ct);

        response.IsSuccessStatusCode.ShouldBeTrue();
        var text = sink.AllText();
        text.ShouldNotContain(RawAccess, Case.Sensitive);
        text.ShouldNotContain(RawToken, Case.Sensitive);
        text.ShouldContain("<redacted>");
        text.ShouldContain("vi", Case.Sensitive);
    }

    [Fact]
    public async Task Unlisted_query_key_is_preserved()
    {
        var sink = new TestLogSink();
        using var factory = Factory(sink);
        using var client = factory.AnonymousClient();
        var episodeId = Guid.CreateVersion7();

        using var response = await client.GetAsync(
            $"{ProbePath}/{Guid.NewGuid()}?episodeId={episodeId}&ticket={RawTicket}", Ct);

        var probe = Probe(sink);
        Text(probe, "QueryString").ShouldBe($"?episodeId={episodeId}&ticket=<redacted>");
    }

    [Fact]
    public async Task Route_template_is_preserved()
    {
        var sink = new TestLogSink();
        using var factory = Factory(sink);
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync($"{ProbePath}/{Guid.NewGuid()}?token={RawToken}", Ct);

        var completion = sink.Events.Single(e => e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal));
        Text(completion, "route").ShouldBe(ProbePath + "/{id:guid}");
    }

    [Fact]
    public async Task Malformed_query_string_is_fully_masked()
    {
        var sink = new TestLogSink();
        using var factory = Factory(sink);
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync($"{ProbePath}/{Guid.NewGuid()}?ticket=abc%zz&token=x", Ct);

        // The client may re-encode the stray '%'; either way no part of the raw value survives (the
        // undecodable case itself is the fail-closed unit test).
        Text(Probe(sink), "QueryString").ShouldNotBeNull().ShouldNotContain("abc", Case.Sensitive);
        sink.AllText().ShouldNotContain("abc", Case.Sensitive);
    }

    [Fact]
    public async Task Exception_message_url_is_redacted()
    {
        var sink = new TestLogSink();
        using var factory = Factory(sink);
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync($"{ProbePath}/throw?token={RawToken}", Ct);

        // Background probes (e.g. storage health) log their own exceptions into the same sink, so select
        // this request's events by its own throw route, not by "has an exception".
        var failures = sink.Events.Where(
            e => e.Exception?.ToString().Contains(ProbePath + "/throw", StringComparison.Ordinal) == true).ToList();
        failures.ShouldNotBeEmpty();
        failures.ShouldAllBe(e => !e.Exception!.ToString().Contains(RawToken, StringComparison.Ordinal));
        failures.ShouldAllBe(e => e.Exception!.ToString().Contains("token=<redacted>", StringComparison.Ordinal));
        sink.AllText().ShouldNotContain(RawToken, Case.Sensitive);
    }

    [Fact]
    public async Task Processor_active_when_otlp_disabled()
    {
        var sink = new TestLogSink();
        using var factory = Factory(sink).WithSetting("Otlp:Enabled", "false");
        _ = factory.Services;
        using var spans = new ActivityCapture();
        using var client = factory.AnonymousClient();

        var path = $"{ProbePath}/{Guid.NewGuid()}";
        using var response = await client.GetAsync($"{path}?code={RawCode}", Ct);

        var request = await spans.WhenStopped(a => a.GetTagItem("url.path") as string == path, Ct);
        request.GetTagItem("url.query").ShouldBe("?code=<redacted>");
    }

    [Fact]
    public async Task No_raw_secret_in_any_log_line_of_integration_run()
    {
        var sink = new TestLogSink();
        using var factory = Factory(sink);
        _ = factory.Services;
        using var spans = new ActivityCapture();
        using var client = factory.AnonymousClient();

        foreach (var raw in AllRaw)
        {
            using var probe = await client.GetAsync($"{ProbePath}/{Guid.NewGuid()}?ticket={raw}&state={raw}", Ct);
            using var hub = await client.GetAsync($"{EpisodeHub.Path}?access_token={raw}", Ct);
            using var failing = await client.GetAsync($"{ProbePath}/throw?code={raw}", Ct);
        }

        sink.Events.ShouldNotBeEmpty();
        foreach (var raw in AllRaw)
        {
            sink.AllText().ShouldNotContain(raw, Case.Sensitive);
            spans.AllText().ShouldNotContain(raw, Case.Sensitive);
        }
    }

    private ApiFactory Factory(TestLogSink sink) =>
        new ApiFactory(Database, new FakeClock())
            .WithServices(services => services.AddSingleton<ILogEventSink>(sink))
            .MapEndpoints(MapProbes);

    private static void MapProbes(IEndpointRouteBuilder app)
    {
        // Logs the query and the headers the way a framework or third-party line would.
        app.MapGet(
            ProbePath + "/{id:guid}",
            (Guid id, HttpContext http) =>
            {
                http.RequestServices.GetRequiredService<Serilog.ILogger>().Information(
                    "probe {QueryString} {@Headers}",
                    http.Request.QueryString.Value,
                    http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()));

                return Results.Ok();
            }).AllowAnonymous();
        app.MapGet(
            ProbePath + "/throw",
            (HttpContext http) => throw new InvalidOperationException($"handler failed for {http.Request.Path}{http.Request.QueryString}"))
            .AllowAnonymous();
    }

    private static LogEvent Probe(TestLogSink sink) =>
        sink.Events.Where(e => e.MessageTemplate.Text.StartsWith("probe ", StringComparison.Ordinal)).ToList().ShouldHaveSingleItem();

    private static string? Text(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out var value) ? ((ScalarValue)value).Value?.ToString() : null;
}
