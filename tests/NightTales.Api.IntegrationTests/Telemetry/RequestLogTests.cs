using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.Realtime;
using NightTales.Infrastructure.Telemetry;
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
/// The request-completion line and the span of one Api request (09.9.3-T1): route template and duration
/// instead of the raw path, <c>episodeId</c> only on episode routes, the hashed caller, and no URL query —
/// which on the hub carries a single-use ticket — in any log event or span.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RequestLogTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Secret = "ticket-value-that-must-never-be-logged";
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Episode_routes_log_route_template_duration_episode_id_and_hashed_user()
    {
        var sink = new CapturingSink();
        using var factory = Factory(sink);
        var userId = Guid.CreateVersion7();
        var episodeId = Guid.CreateVersion7();
        using var client = factory.ClientFor(userId);

        using var get = await client.GetAsync($"/api/v1/episodes/{episodeId}", Ct);
        using var delete = await client.DeleteAsync($"/api/v1/episodes/{episodeId}", Ct);
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var completions = sink.Completions();
        var expectedUser = Hash(userId.ToString("D"));
        var read = completions.Where(e => Text(e, "requestMethod") == "GET").ToList().ShouldHaveSingleItem();
        var cancel = completions.Where(e => Text(e, "requestMethod") == "DELETE").ToList().ShouldHaveSingleItem();

        foreach (var line in new[] { read, cancel })
        {
            Text(line, "route").ShouldBe("/api/v1/episodes/{id:guid}", "the route template, never the raw path");
            Text(line, "episodeId").ShouldBe(episodeId.ToString());
            Text(line, UserHashEnricher.PropertyName).ShouldBe(expectedUser);
            line.Properties.ShouldContainKey("durationMs");
            line.Properties.ShouldContainKey("statusCode");
            var message = line.RenderMessage(CultureInfo.InvariantCulture);
            message.ShouldNotContain(episodeId.ToString(), Case.Sensitive, "the raw path stays out of the line");
        }
    }

    [Fact]
    public async Task Non_episode_routes_never_log_an_episode_id()
    {
        var sink = new CapturingSink();
        using var factory = Factory(sink);
        using var client = factory.ClientFor(Guid.CreateVersion7());
        var seriesId = Guid.CreateVersion7();

        using var response = await client.GetAsync($"/api/v1/series/{seriesId}", Ct);

        var line = sink.Completions().ShouldHaveSingleItem();
        Text(line, "route").ShouldBe("/api/v1/series/{id:guid}");
        line.Properties.ShouldNotContainKey("episodeId", "a series id is not an episode id");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unmatched_path_logs_a_constant_route_not_the_raw_path()
    {
        var sink = new CapturingSink();
        using var factory = Factory(sink);
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync("/no/such/path-with-" + Secret, Ct);

        var line = sink.Completions().ShouldHaveSingleItem();
        Text(line, "route").ShouldBe("unmatched");
        sink.AllText().ShouldNotContain(Secret, Case.Sensitive);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Hub_ticket_query_reaches_no_log_event_and_no_span()
    {
        var sink = new CapturingSink();
        using var factory = Factory(sink);
        _ = factory.Services;
        using var capture = new ActivityCapture();
        using var client = factory.AnonymousClient();
        // Unique key name (query keys survive masking) picks this request's span among process-wide ones.
        var marker = "m" + Guid.NewGuid().ToString("N");

        using var response = await client.GetAsync($"{EpisodeHub.Path}?ticket={Secret}&{marker}=1", Ct);

        var own = await capture.WhenStopped(
            span => span.GetTagItem("url.path") as string == EpisodeHub.Path
                && (span.GetTagItem("url.query") as string ?? string.Empty).Contains(marker, StringComparison.Ordinal),
            Ct);
        sink.AllText().ShouldNotContain(Secret, Case.Sensitive, "no log event carries the hub ticket");
        (own.GetTagItem("url.query") as string ?? string.Empty).ShouldNotContain(
            Secret, Case.Sensitive, "09.9.3-T2: the URL query, when kept, carries no raw value (exact shape: RedactionTests)");
        foreach (var span in capture.Activities)
        {
            span.TagObjects.ShouldAllBe(
                tag => !(tag.Value + string.Empty).Contains(Secret, StringComparison.Ordinal),
                $"span '{span.OperationName}' must not export the URL query");
        }

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    private ApiFactory Factory(CapturingSink sink) =>
        new ApiFactory(Database, new FakeClock())
            .WithServices(services => services.AddSingleton<ILogEventSink>(sink));

    private static string Hash(string subject) =>
        new UserHashEnricher(new UserHashEnricherOptions("harness-user-hash-salt"), new HttpContextAccessor()).Hash(subject);

    private static string? Text(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out var value) ? ((ScalarValue)value).Value?.ToString() : null;

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
            {
                _events.Add(logEvent);
            }
        }

        public List<LogEvent> Completions()
        {
            lock (_events)
            {
                return [.. _events.Where(e => e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal))];
            }
        }

        public string AllText()
        {
            lock (_events)
            {
                return string.Join(
                    '\n',
                    _events.Select(e => e.RenderMessage(CultureInfo.InvariantCulture) + " " + string.Join(
                        ' ', e.Properties.Select(p => p.Key + "=" + p.Value))));
            }
        }
    }
}
