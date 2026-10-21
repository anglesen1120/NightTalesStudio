using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Npgsql;
using OpenTelemetry;

namespace NightTales.Infrastructure.Telemetry.Redaction;

/// <summary>
/// Masks credential-bearing values on every span before an exporter or a listener reads it (09.9.3-T2, 09
/// README BD12/BD16): the query and URL attributes (<c>url.query</c>, <c>url.full</c>, <c>http.url</c>,
/// <c>http.target</c>, <c>query.string</c>, <c>request.query</c>) and the free-text attributes an exception
/// leaves (<c>exception.message</c>, <c>exception.stacktrace</c>, <c>error.message</c>,
/// <c>otel.status_description</c>) plus the activity's status description, and every string attribute of every
/// activity <em>event</em> (an <c>exception</c> event carries <c>exception.message</c> and
/// <c>exception.stacktrace</c>). For an exception event of a <see cref="PostgresException"/> the message is cut
/// to the shape <see cref="LogExceptionSanitizer"/> logs — SQLSTATE and the trimmed server message — and the
/// message and stack trace are cut at the first <c>DETAIL:</c> / <c>Exception data:</c> line (the offending
/// key/column values, present when the connection string sets <c>Include Error Detail</c>). Anything after the
/// marker is dropped, never resumed: a Detail value shaped like a stack frame cannot slip back into the text.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="ActivityEvent"/> is a struct without a public way to change its attributes, and
/// <see cref="Activity"/> has no way to remove or replace an event. The rewrite therefore calls the runtime's
/// own <c>Set</c> on the event's attribute list by reflection (resolved once per runtime type). A test pins that
/// the method still exists, so a runtime upgrade that drops it fails the build instead of leaking silently.
/// </para>
/// <para>
/// It runs for every recorded activity — ASP.NET Core, HttpClient, Npgsql, the platform's own sources — and is
/// registered whether or not the OTLP exporter is on. Redaction is idempotent, so the request-start hook
/// (<see cref="Redact"/> from the ASP.NET Core instrumentation) and this end-of-span pass never double a mask.
/// Attributes are looked up by a fixed name list: no tag enumeration, no allocation for a span without them.
/// Events are enumerated without allocation; a span without events costs nothing.
/// </para>
/// </remarks>
/// <param name="options">The shared redaction list.</param>
public sealed class RedactingActivityProcessor(RedactionOptions options) : BaseProcessor<Activity>
{
    private static readonly string[] QueryTags = ["url.query", "query.string", "request.query"];

    private static readonly string[] UrlTags = ["url.full", "http.url", "http.target"];

    private static readonly string[] TextTags =
        ["exception.message", "exception.stacktrace", "error.message", "otel.status_description"];

    private static readonly string PostgresExceptionType = typeof(PostgresException).FullName!;

    private static readonly ConcurrentDictionary<Type, MethodInfo?> SetMethods = new();

    private const int MaxMessageLength = 300;

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

        RedactEvents(activity, options);
    }

    private static void RedactEvents(Activity activity, RedactionOptions options)
    {
        foreach (var activityEvent in activity.EnumerateEvents())
        {
            var isPostgres = false;
            foreach (var tag in activityEvent.EnumerateTagObjects())
            {
                if (tag.Key == "exception.type" && tag.Value is string type && type == PostgresExceptionType)
                {
                    isPostgres = true;
                }
            }

            List<KeyValuePair<string, object?>>? changes = null;
            foreach (var tag in activityEvent.EnumerateTagObjects())
            {
                if (tag.Value is not string value || value.Length == 0)
                {
                    continue;
                }

                var scrubbed = ScrubEventText(tag.Key, value, isPostgres, options);
                if (!ReferenceEquals(scrubbed, value))
                {
                    (changes ??= []).Add(new KeyValuePair<string, object?>(tag.Key, scrubbed));
                }
            }

            if (changes is not null)
            {
                SetEventTags(activityEvent, changes);
            }
        }
    }

    private static string ScrubEventText(string key, string value, bool isPostgres, RedactionOptions options)
    {
        // A wrapped PostgresException records the wrapper's type but its ToString still names Npgsql's type.
        var postgres = isPostgres || value.Contains(PostgresExceptionType, StringComparison.Ordinal);
        var text = key switch
        {
            "exception.message" when postgres => FirstLine(value),
            "exception.stacktrace" when postgres => StripPostgresText(value),
            _ => value,
        };

        return QueryStringRedactor.RedactText(text, options.QueryKeys);
    }

    private static string FirstLine(string message)
    {
        var newline = message.AsSpan().IndexOfAny('\r', '\n');
        var line = newline >= 0 ? message.AsSpan(0, newline) : message.AsSpan();
        if (line.Length == message.Length && line.Length <= MaxMessageLength)
        {
            return message;
        }

        return line.Length <= MaxMessageLength ? line.ToString() : string.Concat(line[..MaxMessageLength], "…");
    }

    /// <summary>
    /// Cuts a PostgreSQL error text at the first <c>DETAIL:</c> or <c>Exception data:</c> line and keeps only
    /// what precedes it. The <c>DETAIL:</c> block holds the offending values and the <c>Exception data:</c>
    /// block of <see cref="Exception.ToString"/> the schema/table/constraint names; both run to the end of the
    /// text, so nothing after the marker is ever kept — a Detail value shaped like a stack frame cannot
    /// re-enable output. Stack frames of a <see cref="PostgresException"/> sit after its <c>DETAIL:</c> line,
    /// so they are cut too.
    /// </summary>
    private static string StripPostgresText(string text)
    {
        var position = 0;
        while (position < text.Length)
        {
            var lineEnd = text.IndexOf('\n', position);
            var next = lineEnd < 0 ? text.Length : lineEnd + 1;
            var trimmed = text.AsSpan(position, next - position).Trim();
            if (trimmed.StartsWith("DETAIL:", StringComparison.Ordinal)
                || trimmed.SequenceEqual("Exception data:"))
            {
                return text.AsSpan(0, position).TrimEnd("\r\n ").ToString();
            }

            position = next;
        }

        return text;
    }

    private static void SetEventTags(ActivityEvent activityEvent, List<KeyValuePair<string, object?>> changes)
    {
        var tags = activityEvent.Tags;
        if (tags is null)
        {
            return;
        }

        var set = SetMethods.GetOrAdd(tags.GetType(), ResolveSet);
        if (set is null)
        {
            return;
        }

        foreach (var change in changes)
        {
            set.Invoke(tags, [change]);
        }
    }

    /// <summary>The runtime's <c>Set(KeyValuePair&lt;string, object?&gt;)</c> on an event's attribute list, or <see langword="null"/>.</summary>
    /// <param name="tagsType">The runtime type behind <see cref="ActivityEvent.Tags"/>.</param>
    /// <returns>The method, or <see langword="null"/> when this runtime does not have it.</returns>
    private static MethodInfo? ResolveSet(Type tagsType) =>
        tagsType.GetMethod(
            "Set",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(KeyValuePair<string, object?>)]);

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
