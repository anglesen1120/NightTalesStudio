using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace NightTales.TestKit.Telemetry;

/// <summary>
/// A hand-written <see cref="ILogEventSink"/> that keeps the events exactly as the sink received them —
/// structured properties and exception intact, before any rendering (09.9.3-T2). Register it with
/// <c>ApiFactory.WithServices(s =&gt; s.AddSingleton&lt;ILogEventSink&gt;(sink))</c>: the host's sinks all sit
/// behind its exception sanitizer, so what this sink holds is what stdout and the OTLP exporter would get.
/// </summary>
public sealed class TestLogSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];

    /// <summary>A snapshot of every event received so far.</summary>
    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_events)
        {
            _events.Add(logEvent);
        }
    }

    /// <summary>
    /// Every captured event as one text: rendered message, every property (recursively), and the exception
    /// with its message and stack trace — the surface a raw secret could leak through.
    /// </summary>
    /// <returns>One line per event.</returns>
    public string AllText() =>
        string.Join('\n', Events.Select(e =>
            e.RenderMessage(CultureInfo.InvariantCulture)
            + " | " + string.Join(' ', e.Properties.Select(p => p.Key + "=" + p.Value))
            + " | " + e.Exception));
}
