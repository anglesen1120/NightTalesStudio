using Serilog.Core;
using Serilog.Events;

namespace NightTales.Infrastructure.Telemetry.Redaction;

/// <summary>
/// Rewrites, on every log event, the properties that can carry a credential before any sink — stdout JSON or
/// the OTLP exporter — sees the event (09.9.3-T2, 09 README BD12/BD16): the query string and URL properties
/// (<c>QueryString</c>, <c>RequestPath</c>, <c>url.query</c>, <c>url.full</c>, <c>http.url</c>, …) get their
/// listed keys masked, and the values of the listed headers (<c>authorization</c>, <c>cookie</c>,
/// <c>set-cookie</c>) inside a <c>Headers</c> property — or as a property of their own — become the mask.
/// The platform's own request line logs only the route template; this enricher is the guarantee for any other
/// line (a framework or third-party log, a future call site) that does carry them.
/// </summary>
/// <remarks>
/// Serilog applies every enricher before the first sink, so the events a sink receives are always the
/// rewritten ones; the lookups are by a fixed name list, so an event without these properties costs a handful
/// of dictionary reads and no allocation.
/// </remarks>
/// <param name="options">The shared redaction list.</param>
public sealed class QueryStringRedactionEnricher(RedactionOptions options) : ILogEventEnricher
{
    private static readonly string[] QueryProperties =
        ["QueryString", "url.query", "query.string", "request.query", "http.query"];

    private static readonly string[] UrlProperties =
        ["RequestPath", "url.full", "http.url", "http.target", "RequestUrl", "Uri", "Url"];

    private static readonly string[] HeaderContainers = ["Headers", "RequestHeaders", "ResponseHeaders"];

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        foreach (var name in QueryProperties)
        {
            Rewrite(logEvent, name, Kind.Query);
        }

        foreach (var name in UrlProperties)
        {
            Rewrite(logEvent, name, Kind.Url);
        }

        foreach (var name in HeaderContainers)
        {
            Rewrite(logEvent, name, Kind.Headers);
        }
    }

    private enum Kind
    {
        Query,
        Url,
        Headers,
    }

    private void Rewrite(LogEvent logEvent, string name, Kind kind)
    {
        if (!logEvent.Properties.TryGetValue(name, out var value))
        {
            return;
        }

        var rewritten = kind == Kind.Headers ? MaskHeaders(value) : RewriteScalar(value, kind == Kind.Url);
        if (!ReferenceEquals(rewritten, value))
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(name, rewritten));
        }
    }

    private LogEventPropertyValue RewriteScalar(LogEventPropertyValue value, bool isUrl)
    {
        if (value is ScalarValue { Value: null })
        {
            return value;
        }

        // A destructured value (a `QueryString` struct rendered through ToString, a sequence of URLs) is
        // rewritten through its rendered text, so no shape can smuggle the raw value past the mask.
        var text = value is ScalarValue { Value: string raw } ? raw : value.ToString();
        var redacted = isUrl
            ? QueryStringRedactor.RedactUrl(text, options.QueryKeys)
            : QueryStringRedactor.Redact(text, options.QueryKeys);

        return ReferenceEquals(redacted, text) ? value : new ScalarValue(redacted);
    }

    /// <summary>Masks the value of every listed header in a structure, a dictionary or a sequence of either.</summary>
    private LogEventPropertyValue MaskHeaders(LogEventPropertyValue value)
    {
        switch (value)
        {
            case StructureValue structure:
                return MaskStructure(structure);
            case DictionaryValue dictionary:
                return MaskDictionary(dictionary);
            case SequenceValue sequence:
                var elements = new LogEventPropertyValue[sequence.Elements.Count];
                var changed = false;
                for (var i = 0; i < elements.Length; i++)
                {
                    elements[i] = MaskHeaders(sequence.Elements[i]);
                    changed |= !ReferenceEquals(elements[i], sequence.Elements[i]);
                }

                return changed ? new SequenceValue(elements) : value;
            case ScalarValue { Value: string line }:
                return MaskHeaderLine(line) is { } masked ? new ScalarValue(masked) : value;
            default:
                return value;
        }
    }

    private StructureValue MaskStructure(StructureValue structure)
    {
        // A key/value pair destructures to { Key, Value }: the pair is masked when its key names a header.
        var pairKey = structure.Properties.FirstOrDefault(p => p.Name == "Key")?.Value as ScalarValue;
        var isHeaderPair = pairKey?.Value is string headerName && IsHeader(headerName);
        var properties = new LogEventProperty[structure.Properties.Count];
        var changed = false;
        for (var i = 0; i < properties.Length; i++)
        {
            var property = structure.Properties[i];
            var masked = (isHeaderPair && property.Name == "Value") || IsHeader(property.Name)
                ? new ScalarValue(RedactionOptions.Mask)
                : MaskHeaders(property.Value);
            changed |= !ReferenceEquals(masked, property.Value);
            properties[i] = ReferenceEquals(masked, property.Value) ? property : new LogEventProperty(property.Name, masked);
        }

        return changed ? new StructureValue(properties, structure.TypeTag) : structure;
    }

    private DictionaryValue MaskDictionary(DictionaryValue dictionary)
    {
        var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
        var changed = false;
        foreach (var (key, element) in dictionary.Elements)
        {
            var masked = key.Value is string name && IsHeader(name) ? new ScalarValue(RedactionOptions.Mask) : MaskHeaders(element);
            changed |= !ReferenceEquals(masked, element);
            elements.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(key, masked));
        }

        return changed ? new DictionaryValue(elements) : dictionary;
    }

    /// <summary>Masks a <c>name: value</c> or <c>name=value</c> header line, or returns null when it is not a listed header.</summary>
    private string? MaskHeaderLine(string line)
    {
        var separator = line.AsSpan().IndexOfAny(':', '=');
        if (separator <= 0 || !IsHeader(line[..separator].Trim()))
        {
            return null;
        }

        var masked = string.Concat(line.AsSpan(0, separator + 1), " ", RedactionOptions.Mask);

        return masked == line ? null : masked;
    }

    private bool IsHeader(string name) => options.Headers.Contains(name);
}
