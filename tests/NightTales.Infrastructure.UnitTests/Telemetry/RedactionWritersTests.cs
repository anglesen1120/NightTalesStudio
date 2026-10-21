using System.Diagnostics;
using System.Globalization;
using NightTales.Infrastructure.Telemetry;
using NightTales.Infrastructure.Telemetry.Redaction;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Telemetry;

/// <summary>09.9.3-T2: the Serilog enricher, the span processor and the exception sanitizer, each on its own.</summary>
public sealed class RedactionWritersTests
{
    private const string Raw = "raw-credential-value";

    private static readonly RedactionOptions Options = RedactionOptions.Default;

    [Fact]
    public void Enricher_masks_query_url_and_header_properties_and_keeps_the_rest()
    {
        var sink = new Capture();
        using var logger = new LoggerConfiguration()
            .Enrich.With(new QueryStringRedactionEnricher(Options))
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information(
            "req {QueryString} {RequestPath} {@Headers} {Keep}",
            $"?ticket={Raw}&episodeId=7",
            $"/hubs/episodes?access_token={Raw}",
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + Raw,
                ["cookie"] = "sid=" + Raw,
                ["Set-Cookie"] = "sid=" + Raw,
                ["Accept"] = "application/json",
            },
            "kept?token=visible-because-not-a-listed-property");

        var line = sink.Events.ShouldHaveSingleItem();
        var text = line.RenderMessage(CultureInfo.InvariantCulture) + string.Join(' ', line.Properties.Select(p => p.Value));
        text.ShouldNotContain(Raw);
        line.Properties["QueryString"].ToString().ShouldBe("\"?ticket=<redacted>&episodeId=7\"");
        line.Properties["RequestPath"].ToString().ShouldBe("\"/hubs/episodes?access_token=<redacted>\"");
        text.ShouldContain("application/json");
        text.ShouldContain("episodeId=7");
        text.ShouldContain("visible-because-not-a-listed-property");
    }

    [Fact]
    public void Enricher_masks_header_pairs_given_as_a_list_of_lines_or_key_value_structures()
    {
        var enricher = new QueryStringRedactionEnricher(Options);
        var template = new MessageTemplateParser().Parse("x");
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            template,
            [
                new LogEventProperty("Headers", new SequenceValue(
                [
                    new ScalarValue("Authorization: Bearer " + Raw),
                    new ScalarValue("Accept: text/plain"),
                    new StructureValue(
                    [
                        new LogEventProperty("Key", new ScalarValue("Cookie")),
                        new LogEventProperty("Value", new ScalarValue("sid=" + Raw)),
                    ]),
                ])),
            ]);

        enricher.Enrich(logEvent, null!);

        var text = logEvent.Properties["Headers"].ToString();
        text.ShouldNotContain(Raw);
        text.ShouldContain("Accept: text/plain");
    }

    [Fact]
    public void Enricher_is_idempotent_so_a_double_registration_masks_exactly_once()
    {
        var sink = new Capture();
        using var logger = new LoggerConfiguration()
            .Enrich.With(new QueryStringRedactionEnricher(Options))
            .Enrich.With(new QueryStringRedactionEnricher(Options))
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("{QueryString}", $"?token={Raw}");

        sink.Events.ShouldHaveSingleItem().Properties["QueryString"].ToString().ShouldBe("\"?token=<redacted>\"");
    }

    [Fact]
    public void Enricher_masks_a_destructured_query_string_and_a_sequence_of_urls_keeping_their_shape()
    {
        var sink = new Capture();
        using var logger = new LoggerConfiguration()
            .Enrich.With(new QueryStringRedactionEnricher(Options))
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information(
            "req {@QueryString} {@Url}",
            new { Value = $"?token={Raw}", Count = 1 },
            new[] { "/plain?page=2", $"/a?ticket={Raw}", $"/b?access_token={Raw}" });

        var line = sink.Events.ShouldHaveSingleItem();
        var text = string.Join(' ', line.Properties.Select(p => p.Value));
        text.ShouldNotContain(Raw);
        var structure = line.Properties["QueryString"].ShouldBeOfType<StructureValue>();
        structure.Properties.Select(p => p.Name).ShouldBe(["Value", "Count"]);
        structure.Properties[0].Value.ToString().ShouldBe("\"?token=<redacted>\"");
        var urls = line.Properties["Url"].ShouldBeOfType<SequenceValue>();
        urls.Elements.Select(e => e.ToString()).ShouldBe(
            ["\"/plain?page=2\"", "\"/a?ticket=<redacted>\"", "\"/b?access_token=<redacted>\""]);
    }

    [Fact]
    public void Enricher_keeps_the_original_instances_when_nothing_is_to_be_masked()
    {
        var enricher = new QueryStringRedactionEnricher(Options);
        var headers = new SequenceValue(
        [
            new ScalarValue("Accept: text/plain"),
            new StructureValue([new LogEventProperty("Key", new ScalarValue("Accept")), new LogEventProperty("Value", new ScalarValue("x"))]),
        ]);
        var dictionary = new DictionaryValue(
        [
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue("Accept"), new ScalarValue("x")),
        ]);
        var query = new StructureValue([new LogEventProperty("Value", new ScalarValue("?page=2"))]);
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("x"),
            [
                new LogEventProperty("Headers", headers),
                new LogEventProperty("RequestHeaders", dictionary),
                new LogEventProperty("QueryString", query),
            ]);

        enricher.Enrich(logEvent, null!);

        logEvent.Properties["Headers"].ShouldBeSameAs(headers);
        logEvent.Properties["RequestHeaders"].ShouldBeSameAs(dictionary);
        logEvent.Properties["QueryString"].ShouldBeSameAs(query);
    }

    [Fact]
    public void Enricher_masks_a_listed_header_logged_as_a_property_of_its_own()
    {
        var sink = new Capture();
        using var logger = new LoggerConfiguration()
            .Enrich.With(new QueryStringRedactionEnricher(Options))
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("{Authorization} {cookie} {Accept}", "Bearer " + Raw, "sid=" + Raw, "text/plain");

        var line = sink.Events.ShouldHaveSingleItem();
        var text = line.RenderMessage(CultureInfo.InvariantCulture) + string.Join(' ', line.Properties.Select(p => p.Value));
        text.ShouldNotContain(Raw);
        line.Properties["Authorization"].ToString().ShouldBe("\"<redacted>\"");
        line.Properties["cookie"].ToString().ShouldBe("\"<redacted>\"");
        line.Properties["Accept"].ToString().ShouldBe("\"text/plain\"");
    }

    [Fact]
    public void Processor_masks_url_and_exception_attributes_and_is_idempotent()
    {
        using var source = new ActivitySource("nts.test.redaction");
        using var listener = new ActivityListener
        {
            ShouldListenTo = static s => s.Name == "nts.test.redaction",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("request").ShouldNotBeNull();
        activity.SetTag("url.query", $"ticket={Raw}&episodeId=7");
        activity.SetTag("url.full", $"https://x.test/hubs/episodes?access_token={Raw}#{Raw}");
        activity.SetTag("exception.message", $"GET /reset?token={Raw} failed");
        activity.SetTag("url.path", "/hubs/episodes");
        activity.SetStatus(ActivityStatusCode.Error, $"bad ?code={Raw}");
        var processor = new RedactingActivityProcessor(Options);

        processor.OnEnd(activity);
        processor.OnEnd(activity);

        activity.GetTagItem("url.query").ShouldBe("ticket=<redacted>&episodeId=7");
        activity.GetTagItem("url.full").ShouldBe("https://x.test/hubs/episodes?access_token=<redacted>#<redacted>");
        activity.GetTagItem("exception.message").ShouldBe("GET /reset?token=<redacted> failed");
        activity.GetTagItem("url.path").ShouldBe("/hubs/episodes");
        activity.StatusDescription.ShouldBe("bad ?code=<redacted>");
        string.Join(' ', activity.TagObjects.Select(t => t.Value)).ShouldNotContain(Raw);
    }

    [Fact]
    public void Processor_masks_the_resumable_session_handle_and_keeps_the_harmless_query()
    {
        using var source = new ActivitySource("nts.test.upload.redaction");
        using var listener = new ActivityListener
        {
            ShouldListenTo = static s => s.Name == "nts.test.upload.redaction",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("upload").ShouldNotBeNull();

        // An HttpClient span's URL is the one place a resumable session URI reaches: `upload_id` is a bearer
        // capability for that upload (06.2.3-T1), so the frozen list carries it — while `uploadType`, which is the
        // same value on every call, stays readable.
        activity.SetTag(
            "url.full",
            $"https://www.googleapis.com/upload/youtube/v3/videos?upload_id={Raw}&uploadType=resumable");

        new RedactingActivityProcessor(Options).OnEnd(activity);

        var full = activity.GetTagItem("url.full").ShouldBeOfType<string>();
        full.ShouldNotContain(Raw);
        full.ShouldStartWith("https://www.googleapis.com/upload/youtube/v3/videos?upload_id=<redacted>");
        full.ShouldEndWith("&uploadType=resumable");
    }

    [Fact]
    public void Sanitizer_masks_listed_values_in_the_message_inner_message_and_stack_trace()
    {
        var thrown = Throw(new InvalidOperationException(
            $"handler failed for /reset?token={Raw}", new TimeoutException($"inner ?state={Raw}")));

        var sanitized = LogExceptionSanitizer.Sanitize(thrown, Options.QueryKeys).ShouldNotBeNull();

        var text = sanitized.ToString();
        text.ShouldNotContain(Raw);
        text.ShouldContain("token=<redacted>");
        text.ShouldContain("state=<redacted>");
        text.ShouldContain(nameof(InvalidOperationException));
    }

    [Fact]
    public void Sanitizer_keeps_an_exception_without_a_listed_key_and_without_keys_masks_nothing()
    {
        var plain = new InvalidOperationException("boom");
        var withUrl = new InvalidOperationException($"?token={Raw}");

        LogExceptionSanitizer.Sanitize(plain, Options.QueryKeys).ShouldBeSameAs(plain);
        LogExceptionSanitizer.Sanitize(withUrl).ShouldBeSameAs(withUrl);
    }

    private static Exception Throw(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    private sealed class Capture : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
