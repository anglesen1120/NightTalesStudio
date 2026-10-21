using Serilog.Core;
using Serilog.Events;

namespace NightTales.Infrastructure.Telemetry.Redaction;

/// <summary>
/// Rewrites, on every log event, the properties that can carry a credential before any sink — stdout JSON or
/// the OTLP exporter — sees the event (09.9.3-T2, 09 README BD12/BD16): the query string and URL properties
/// (<c>QueryString</c>, <c>RequestPath</c>, <c>url.query</c>, <c>url.full</c>, <c>http.url</c>, …) get their
/// listed keys masked — destructured structures, sequences and dictionaries under those names are walked and
/// every string leaf is redacted while the shape is kept — and the values of the listed headers
/// (<c>authorization</c>, <c>cookie</c>, <c>set-cookie</c>) inside a <c>Headers</c> property — or as a
/// property of their own — become the mask.
/// The platform's own request line logs only the route template; this enricher is the guarantee for any other
/// line (a framework or third-party log, a future call site) that does carry them.
/// </summary>
/// <remarks>
/// Serilog applies every enricher before the first sink, so the events a sink receives are always the
/// rewritten ones; the lookups are by a fixed name list, so an event without these properties costs a handful
/// of dictionary reads and one scan of the property names, and no allocation. Every container rewrite is
/// lazy: an event whose listed properties hold nothing to mask keeps the original value instances.
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

        // A listed header logged as a property of its own (`{Authorization}`, `{Cookie}`) is masked whole —
        // its value exists only to carry the credential.
        List<string>? headerNames = null;
        if (logEvent.Properties is Dictionary<string, LogEventPropertyValue> properties)
        {
            // Enumerating the concrete dictionary costs a struct enumerator, not a boxed one.
            foreach (var (name, _) in properties)
            {
                if (IsHeader(name))
                {
                    (headerNames ??= []).Add(name);
                }
            }
        }
        else
        {
            foreach (var name in logEvent.Properties.Keys)
            {
                if (IsHeader(name))
                {
                    (headerNames ??= []).Add(name);
                }
            }
        }

        if (headerNames is not null)
        {
            foreach (var name in headerNames)
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, new ScalarValue(RedactionOptions.Mask)));
            }
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

        var rewritten = kind == Kind.Headers ? MaskHeaders(value) : RewriteValue(value, kind == Kind.Url);
        if (!ReferenceEquals(rewritten, value))
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(name, rewritten));
        }
    }

    /// <summary>
    /// Redacts a scalar, or every string leaf of a structure, sequence or dictionary, returning the original
    /// instance untouched when nothing had to change so the shape — and the allocation — is preserved.
    /// </summary>
    private LogEventPropertyValue RewriteValue(LogEventPropertyValue value, bool isUrl)
    {
        switch (value)
        {
            case ScalarValue { Value: null }:
                return value;
            case ScalarValue scalar:
                // A non-string scalar (a `QueryString` or `Uri` bound through `{QueryString}`) is rewritten
                // through its rendered text, so no rendered form can smuggle the raw value past the mask.
                return RewriteText(value, scalar.Value is string raw ? raw : scalar.ToString(), isUrl);
            case StructureValue structure:
                {
                    LogEventProperty[]? rewritten = null;
                    for (var i = 0; i < structure.Properties.Count; i++)
                    {
                        var property = structure.Properties[i];
                        var leaf = RewriteValue(property.Value, isUrl);
                        if (ReferenceEquals(leaf, property.Value))
                        {
                            if (rewritten is not null)
                            {
                                rewritten[i] = property;
                            }

                            continue;
                        }

                        rewritten ??= CopyHead(structure.Properties, i);
                        rewritten[i] = new LogEventProperty(property.Name, leaf);
                    }

                    return rewritten is null ? structure : new StructureValue(rewritten, structure.TypeTag);
                }
            case SequenceValue sequence:
                {
                    LogEventPropertyValue[]? rewritten = null;
                    for (var i = 0; i < sequence.Elements.Count; i++)
                    {
                        var leaf = RewriteValue(sequence.Elements[i], isUrl);
                        if (ReferenceEquals(leaf, sequence.Elements[i]))
                        {
                            if (rewritten is not null)
                            {
                                rewritten[i] = leaf;
                            }

                            continue;
                        }

                        rewritten ??= CopyHead(sequence.Elements, i);
                        rewritten[i] = leaf;
                    }

                    return rewritten is null ? sequence : new SequenceValue(rewritten);
                }
            case DictionaryValue dictionary:
                {
                    List<KeyValuePair<ScalarValue, LogEventPropertyValue>>? rewritten = null;
                    var index = 0;
                    foreach (var (key, element) in dictionary.Elements)
                    {
                        var leaf = RewriteValue(element, isUrl);
                        if (rewritten is null && ReferenceEquals(leaf, element))
                        {
                            index++;
                            continue;
                        }

                        if (rewritten is null)
                        {
                            rewritten = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
                            rewritten.AddRange(dictionary.Elements.Take(index));
                        }

                        rewritten.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(key, leaf));
                        index++;
                    }

                    return rewritten is null ? dictionary : new DictionaryValue(rewritten);
                }
            default:
                return value;
        }
    }

    private LogEventPropertyValue RewriteText(LogEventPropertyValue value, string text, bool isUrl)
    {
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
                {
                    LogEventPropertyValue[]? elements = null;
                    for (var i = 0; i < sequence.Elements.Count; i++)
                    {
                        var element = MaskHeaders(sequence.Elements[i]);
                        if (ReferenceEquals(element, sequence.Elements[i]))
                        {
                            if (elements is not null)
                            {
                                elements[i] = element;
                            }

                            continue;
                        }

                        elements ??= CopyHead(sequence.Elements, i);
                        elements[i] = element;
                    }

                    return elements is null ? value : new SequenceValue(elements);
                }
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
        LogEventProperty[]? properties = null;
        for (var i = 0; i < structure.Properties.Count; i++)
        {
            var property = structure.Properties[i];
            var masked = (isHeaderPair && property.Name == "Value") || IsHeader(property.Name)
                ? new ScalarValue(RedactionOptions.Mask)
                : MaskHeaders(property.Value);
            if (ReferenceEquals(masked, property.Value))
            {
                if (properties is not null)
                {
                    properties[i] = property;
                }

                continue;
            }

            properties ??= CopyHead(structure.Properties, i);
            properties[i] = new LogEventProperty(property.Name, masked);
        }

        return properties is null ? structure : new StructureValue(properties, structure.TypeTag);
    }

    private DictionaryValue MaskDictionary(DictionaryValue dictionary)
    {
        List<KeyValuePair<ScalarValue, LogEventPropertyValue>>? elements = null;
        var index = 0;
        foreach (var (key, element) in dictionary.Elements)
        {
            var masked = key.Value is string name && IsHeader(name) ? new ScalarValue(RedactionOptions.Mask) : MaskHeaders(element);
            if (elements is null && ReferenceEquals(masked, element))
            {
                index++;
                continue;
            }

            if (elements is null)
            {
                elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
                elements.AddRange(dictionary.Elements.Take(index));
            }

            elements.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(key, masked));
            index++;
        }

        return elements is null ? dictionary : new DictionaryValue(elements);
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

    /// <summary>Copies <paramref name="head"/> elements <c>[0, head)</c> of <paramref name="source"/> into a new array.</summary>
    private static T[] CopyHead<T>(IReadOnlyList<T> source, int head)
    {
        var copy = new T[source.Count];
        for (var i = 0; i < head; i++)
        {
            copy[i] = source[i];
        }

        return copy;
    }

    private bool IsHeader(string name) => options.Headers.Contains(name);
}
