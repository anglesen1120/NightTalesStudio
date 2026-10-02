using System.Diagnostics.Metrics;

namespace NightTales.Application.Features.Email;

/// <summary>
/// The e-mail instruments of 09.2.6-T3 (Design "Metrics"; exported by 09.9.3-T1):
/// <c>email_queued_total{template}</c>, <c>email_sent_total{template}</c>,
/// <c>email_failed_total{template,code}</c> and <c>email_send_seconds</c>.
/// </summary>
/// <remarks>
/// <para>
/// One instance per process (registered as a singleton by <c>AddEmail</c>), so a scrape never sees two
/// series for one counter. The tags carry the template name and a wire code from the closed
/// <c>ErrorCodes</c> vocabulary only — never an address, a subject or a token (09 README BD16).
/// </para>
/// <para>
/// It lives in the Application layer because the two producers do: <c>EmailSender</c> counts what it
/// queues and <c>SendEmailJob</c> counts what it sends, and neither would be served by a port whose only
/// purpose is to move four instrument calls to the other side of the dependency rule. The meter name
/// follows the platform's <c>NightTales.*</c> convention (<c>NightTales.Email</c>).
/// </para>
/// </remarks>
public sealed class EmailMetrics : IDisposable
{
    /// <summary>The meter every instrument of this feature publishes on.</summary>
    public const string MeterName = "NightTales.Email";

    /// <summary>Messages queued by a feature, by template.</summary>
    public const string QueuedCounterName = "email_queued_total";

    /// <summary>Messages the SMTP server accepted, by template.</summary>
    public const string SentCounterName = "email_sent_total";

    /// <summary>Failed attempts, by template and failure code.</summary>
    public const string FailedCounterName = "email_failed_total";

    /// <summary>The SMTP call's duration in seconds, by template.</summary>
    public const string SendSecondsName = "email_send_seconds";

    /// <summary>The tag carrying the template name.</summary>
    public const string TemplateTagName = "template";

    /// <summary>The tag carrying the failure code.</summary>
    public const string CodeTagName = "code";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _queued;
    private readonly Counter<long> _sent;
    private readonly Counter<long> _failed;
    private readonly Histogram<double> _sendSeconds;

    /// <summary>Creates the meter and its four instruments.</summary>
    public EmailMetrics()
    {
        _queued = _meter.CreateCounter<long>(QueuedCounterName, "1", "E-mails queued for delivery, by template.");
        _sent = _meter.CreateCounter<long>(SentCounterName, "1", "E-mails the SMTP server accepted, by template.");
        _failed = _meter.CreateCounter<long>(FailedCounterName, "1", "Failed e-mail attempts, by template and code.");
        _sendSeconds = _meter.CreateHistogram<double>(SendSecondsName, "s", "Duration of one SMTP send, by template.");
    }

    /// <summary>Counts one queued message.</summary>
    /// <param name="template">The catalog template name.</param>
    public void Queued(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        _queued.Add(1, Tag(template));
    }

    /// <summary>Counts one accepted message and records how long the SMTP call took.</summary>
    /// <param name="template">The catalog template name.</param>
    /// <param name="duration">The send call's duration.</param>
    public void Sent(string template, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var tag = Tag(template);
        _sent.Add(1, tag);
        _sendSeconds.Record(duration.TotalSeconds, tag);
    }

    /// <summary>Counts one failed attempt.</summary>
    /// <param name="template">The catalog template name.</param>
    /// <param name="code">The failure code written to the row's <c>last_error_code</c>.</param>
    public void Failed(string template, string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        _failed.Add(
            1,
            new KeyValuePair<string, object?>(TemplateTagName, template),
            new KeyValuePair<string, object?>(CodeTagName, code));
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static KeyValuePair<string, object?> Tag(string template) =>
        new(TemplateTagName, template);
}
