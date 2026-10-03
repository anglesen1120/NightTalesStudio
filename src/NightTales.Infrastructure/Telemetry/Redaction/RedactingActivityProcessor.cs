using System.Diagnostics;
using OpenTelemetry;

namespace NightTales.Infrastructure.Telemetry.Redaction;

/// <summary>
/// Masks credential-bearing values on every span before an exporter or a listener reads it (09.9.3-T2, 09
/// README BD12/BD16): the query and URL attributes (<c>url.query</c>, <c>url.full</c>, <c>http.url</c>,
/// <c>http.target</c>, <c>query.string</c>, <c>request.query</c>) and the free-text attributes an exception
/// leaves (<c>exception.message</c>, <c>exception.stacktrace</c>, <c>error.message</c>,
/// <c>otel.status_description</c>) plus the activity's status description.
/// </summary>
/// <remarks>
/// It runs for every recorded activity — ASP.NET Core, HttpClient, Npgsql, the platform's own sources — and is
/// registered whether or not the OTLP exporter is on. Redaction is idempotent, so the request-start hook
/// (<see cref="Redact"/> from the ASP.NET Core instrumentation) and this end-of-span pass never double a mask.
/// Attributes are looked up by a fixed name list: no tag enumeration, no allocation for a span without them.
/// </remarks>
/// <param name="options">The shared redaction list.</param>
public sealed class RedactingActivityProcessor(RedactionOptions options) : BaseProcessor<Activity>
{
    private static readonly string[] QueryTags = ["url.query", "query.string", "request.query"];

    private static readonly string[] UrlTags = ["url.full", "http.url", "http.target"];

    private static readonly string[] TextTags =
        ["exception.message", "exception.stacktrace", "error.message", "otel.status_description"];

    /// <inheritdoc />
    public override void OnEnd(Activity data) => Redact(data, options);

    /// <summary>Masks the credential-bearing attributes of one activity in place.</summary>
    /// <param name="activity">The activity.</param>
    /// <param name="options">The redaction list.</param>
    public static void Redact(Activity activity, RedactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(options);

        foreach (var tag in QueryTags)
        {
            Rewrite(activity, tag, options, Mode.Query);
        }

        foreach (var tag in UrlTags)
        {
            Rewrite(activity, tag, options, Mode.Url);
        }

        foreach (var tag in TextTags)
        {
            Rewrite(activity, tag, options, Mode.Text);
        }

        var description = activity.StatusDescription;
        if (!string.IsNullOrEmpty(description))
        {
            var redacted = QueryStringRedactor.RedactText(description, options.QueryKeys);
            if (!ReferenceEquals(redacted, description))
            {
                activity.SetStatus(activity.Status, redacted);
            }
        }
    }

    private enum Mode
    {
        Query,
        Url,
        Text,
    }

    private static void Rewrite(Activity activity, string tag, RedactionOptions options, Mode mode)
    {
        if (activity.GetTagItem(tag) is not string value || value.Length == 0)
        {
            return;
        }

        var redacted = mode switch
        {
            Mode.Query => QueryStringRedactor.Redact(value, options.QueryKeys),
            Mode.Url => QueryStringRedactor.RedactUrl(value, options.QueryKeys),
            _ => QueryStringRedactor.RedactText(value, options.QueryKeys),
        };
        if (!ReferenceEquals(redacted, value))
        {
            activity.SetTag(tag, redacted);
        }
    }
}
