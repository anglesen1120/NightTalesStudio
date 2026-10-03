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
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Telemetry;

/// <summary>
/// 09.9.3-T2 QA additions (written by the QA agent): paths the implementer's <see cref="RedactionTests"/> do not
/// pin — an outgoing HttpClient span, mixed-case keys, and the framework's own request lines at Information.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedactionQaTests(TestDatabase database) : IntegrationTest(database)
{
    // Built by concatenation: a literal this shape trips the secret scanner.
    private static readonly string RawA = "qa" + "-raw-" + "a1b2c3";
    private static readonly string RawB = "qa" + "-raw-" + "d4e5f6";

    private const string ProbePath = "/probe/qa-redaction";
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Outgoing_http_client_span_url_is_redacted()
    {
        var sink = new TestLogSink();
        using var factory = Factory(sink);
        _ = factory.Services;
        using var spans = new ActivityCapture();
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync(ProbePath + "/outgoing", Ct);

        response.IsSuccessStatusCode.ShouldBeTrue();
        var outgoing = await spans.WhenStopped(a => (a.GetTagItem("url.full") as string)?.Contains("127.0.0.1:1", StringComparison.Ordinal) == true, Ct);
        // The OTel HttpClient instrumentation masks the whole query itself (`?*`); either way no raw value may survive.
        var full = (string)outgoing.GetTagItem("url.full")!;
        full.ShouldStartWith("http://127.0.0.1:1/callback");
        full.ShouldNotContain(RawA, Case.Sensitive);
        full.ShouldNotContain(RawB, Case.Sensitive);
        spans.AllText().ShouldNotContain(RawA, Case.Sensitive);
        spans.AllText().ShouldNotContain(RawB, Case.Sensitive);
        sink.AllText().ShouldNotContain(RawA, Case.Sensitive);
        sink.AllText().ShouldNotContain(RawB, Case.Sensitive);
    }

    [Fact]
    public async Task Mixed_case_keys_are_redacted_in_logs_and_spans()
    {
        var sink = new TestLogSink();
        using var factory = Factory(sink);
        _ = factory.Services;
        using var spans = new ActivityCapture();
        using var client = factory.AnonymousClient();

        // A probe route, not the hub: the capture is process-wide, so a path other tests also request would be ambiguous.
        using var response = await client.GetAsync($"{ProbePath}/plain?TICKET={RawA}&Access_Token={RawB}", Ct);

        var request = await spans.WhenStopped(a => a.GetTagItem("url.path") as string == ProbePath + "/plain", Ct);
        var query = (request.GetTagItem("url.query") as string).ShouldNotBeNull();
        query.ShouldContain("TICKET=<redacted>", Case.Sensitive);
        query.ShouldContain("Access_Token=<redacted>", Case.Sensitive);
        spans.AllText().ShouldNotContain(RawA, Case.Sensitive);
        spans.AllText().ShouldNotContain(RawB, Case.Sensitive);
        sink.AllText().ShouldNotContain(RawA, Case.Sensitive);
        sink.AllText().ShouldNotContain(RawB, Case.Sensitive);
    }

    [Fact]
    public async Task Framework_request_lines_at_information_carry_no_raw_value()
    {
        var sink = new TestLogSink();
        using var factory = Factory(sink)
            .WithSetting("Serilog:MinimumLevel:Override:Microsoft.AspNetCore", "Information");
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync($"{ProbePath}/plain?ticket={RawA}&token={RawB}&keep=visible", Ct);

        // Positive control: the framework's own line must actually be present, else this test proves nothing.
        sink.Events.ShouldContain(e => e.MessageTemplate.Text.Contains("Request starting", StringComparison.Ordinal));
        sink.AllText().ShouldNotContain(RawA, Case.Sensitive);
        sink.AllText().ShouldNotContain(RawB, Case.Sensitive);
    }

    [Fact]
    public async Task Unlisted_key_and_listed_key_in_one_query_keep_order_in_the_probe_line()
    {
        var sink = new TestLogSink();
        using var factory = Factory(sink);
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync($"{ProbePath}/plain?a=1&token={RawA}&b=2&state={RawB}&c", Ct);

        var probe = sink.Events.Single(e => e.MessageTemplate.Text.StartsWith("qa-probe ", StringComparison.Ordinal));
        ((Serilog.Events.ScalarValue)probe.Properties["QueryString"]).Value
            .ShouldBe("?a=1&token=<redacted>&b=2&state=<redacted>&c");
    }

    private ApiFactory Factory(TestLogSink sink) =>
        new ApiFactory(Database, new FakeClock())
            .WithServices(services => services.AddSingleton<ILogEventSink>(sink))
            .MapEndpoints(MapProbes);

    private static void MapProbes(IEndpointRouteBuilder app)
    {
        app.MapGet(
            ProbePath + "/plain",
            (HttpContext http) =>
            {
                http.RequestServices.GetRequiredService<Serilog.ILogger>()
                    .Information("qa-probe {QueryString}", http.Request.QueryString.Value);

                return Results.Ok();
            }).AllowAnonymous();
        app.MapGet(
            ProbePath + "/outgoing",
            async (CancellationToken ct) =>
            {
                using var outgoing = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                try
                {
                    using var _ = await outgoing.GetAsync($"http://127.0.0.1:1/callback?token={RawA}&code={RawB}", ct);
                }
                catch (HttpRequestException)
                {
                    // Nothing listens on port 1: the client span is all this probe is for.
                }

                return Results.Ok();
            }).AllowAnonymous();
    }
}
