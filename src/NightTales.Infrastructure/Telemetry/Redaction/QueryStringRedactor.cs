using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace NightTales.Infrastructure.Telemetry.Redaction;

/// <summary>
/// Masks the values of credential-bearing query keys (09.9.3-T2, 09 README BD12): <c>access_token</c>,
/// <c>ticket</c>, <c>token</c>, <c>code</c> and <c>state</c> plus whatever configuration adds. One pass, no
/// regex and no LINQ; a string is built only when a value is actually masked, and an input without a listed
/// key is returned as the same instance. Every method is idempotent — an already masked value is left alone —
/// so a second writer (the span start hook and the end processor) never doubles the mask.
/// </summary>
public static class QueryStringRedactor
{
    private const string FailClosed = "?" + RedactionOptions.Mask;

    /// <summary>Masks the listed keys of a query string, with or without its leading <c>?</c>.</summary>
    /// <param name="queryString">The raw query string.</param>
    /// <param name="keys">The keys to mask, ordinal-ignore-case.</param>
    /// <returns>
    /// <paramref name="queryString"/> itself when nothing is listed (and for null or empty); the query with the
    /// values masked and the order and every other parameter kept; or <c>?&lt;redacted&gt;</c> for the whole
    /// query when it cannot be parsed (an undecodable <c>%</c> sequence) — fail closed, never the raw text.
    /// </returns>
    [return: NotNullIfNotNull(nameof(queryString))]
    public static string? Redact(string? queryString, IReadOnlySet<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (string.IsNullOrEmpty(queryString))
        {
            return queryString;
        }

        return RedactSpan(queryString, keys) ?? queryString;
    }

    /// <summary>Masks the query and the fragment of an absolute or relative URL.</summary>
    /// <param name="url">The URL, for example a span's <c>url.full</c>.</param>
    /// <param name="keys">The keys to mask.</param>
    /// <returns>
    /// <paramref name="url"/> itself when it carries nothing to mask; otherwise the URL with the listed query
    /// values masked (the whole query when it cannot be parsed) and a non-empty fragment — which a reset link
    /// uses for its token (BD3) — replaced by the mask.
    /// </returns>
    [return: NotNullIfNotNull(nameof(url))]
    public static string? RedactUrl(string? url, IReadOnlySet<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        var text = url.AsSpan();
        var fragmentStart = text.IndexOf('#');
        var queryStart = text.IndexOf('?');
        if (fragmentStart >= 0 && queryStart > fragmentStart)
        {
            queryStart = -1;
        }

        var queryEnd = fragmentStart >= 0 ? fragmentStart : text.Length;
        var query = queryStart >= 0 ? RedactSpan(text[queryStart..queryEnd], keys) : null;
        var maskFragment = fragmentStart >= 0
            && fragmentStart + 1 < text.Length
            && !text[(fragmentStart + 1)..].SequenceEqual(RedactionOptions.Mask);
        if (query is null && !maskFragment)
        {
            return url;
        }

        var head = queryStart >= 0 ? text[..queryStart] : text[..queryEnd];
        var middle = query is not null ? query.AsSpan() : queryStart >= 0 ? text[queryStart..queryEnd] : default;
        var tail = fragmentStart < 0
            ? default
            : maskFragment ? "#" + RedactionOptions.Mask : text[fragmentStart..].ToString();

        return string.Concat(head, middle, tail);
    }

    /// <summary>Masks <c>key=value</c> pairs for a listed key anywhere in free text, such as an exception message.</summary>
    /// <param name="text">The text.</param>
    /// <param name="keys">The keys to mask.</param>
    /// <returns><paramref name="text"/> itself when no listed pair occurs; otherwise the text with the values masked.</returns>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? RedactText(string? text, IReadOnlySet<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var span = text.AsSpan();
        StringBuilder? result = null;
        var copied = 0;
        var from = 0;
        while (from < span.Length)
        {
            var found = span[from..].IndexOf('=');
            if (found < 0)
            {
                break;
            }

            var equals = from + found;
            var keyStart = equals;
            while (keyStart > 0 && IsKeyCharacter(span[keyStart - 1]))
            {
                keyStart--;
            }

            var valueStart = equals + 1;
            var valueEnd = valueStart;
            while (valueEnd < span.Length && !IsValueTerminator(span[valueEnd]))
            {
                valueEnd++;
            }

            if (keyStart < equals
                && valueEnd > valueStart
                && !span[valueStart..valueEnd].SequenceEqual(RedactionOptions.Mask)
                && IsListed(span[keyStart..equals], keys))
            {
                result ??= new StringBuilder(text.Length);
                result.Append(span[copied..valueStart]).Append(RedactionOptions.Mask);
                copied = valueEnd;
            }

            from = Math.Max(valueEnd, equals + 1);
        }

        if (result is null)
        {
            return text;
        }

        return result.Append(span[copied..]).ToString();
    }

    /// <summary>The masked query, or null when nothing changed.</summary>
    private static string? RedactSpan(ReadOnlySpan<char> query, IReadOnlySet<string> keys)
    {
        if (!HasValidEscapes(query))
        {
            return query.SequenceEqual(FailClosed) ? null : FailClosed;
        }

        StringBuilder? result = null;
        var copied = 0;
        var position = query.Length > 0 && query[0] == '?' ? 1 : 0;
        while (position <= query.Length)
        {
            var next = query[position..].IndexOf('&');
            var end = next < 0 ? query.Length : position + next;
            var segment = query[position..end];
            var equals = segment.IndexOf('=');
            if (equals > 0 && equals + 1 < segment.Length)
            {
                var valueStart = position + equals + 1;
                if (!query[valueStart..end].SequenceEqual(RedactionOptions.Mask) && IsListed(segment[..equals], keys))
                {
                    result ??= new StringBuilder(query.Length);
                    result.Append(query[copied..valueStart]).Append(RedactionOptions.Mask);
                    copied = end;
                }
            }

            if (end == query.Length)
            {
                break;
            }

            position = end + 1;
        }

        return result?.Append(query[copied..]).ToString();
    }

    private static bool HasValidEscapes(ReadOnlySpan<char> text)
    {
        for (var index = text.IndexOf('%'); index >= 0;)
        {
            if (index + 2 >= text.Length || !char.IsAsciiHexDigit(text[index + 1]) || !char.IsAsciiHexDigit(text[index + 2]))
            {
                return false;
            }

            var rest = text[(index + 3)..].IndexOf('%');
            index = rest < 0 ? -1 : index + 3 + rest;
        }

        return true;
    }

    private static bool IsListed(ReadOnlySpan<char> key, IReadOnlySet<string> keys)
    {
        if (key.IndexOfAny('%', '+') >= 0)
        {
            return keys.Contains(Uri.UnescapeDataString(key.ToString().Replace('+', ' ')));
        }

        if (keys is HashSet<string> set && set.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup))
        {
            return lookup.Contains(key);
        }

        return keys.Contains(key.ToString());
    }

    private static bool IsKeyCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '%' or '+';

    private static bool IsValueTerminator(char c) =>
        char.IsWhiteSpace(c) || c is '&' or '"' or '\'' or '#' or ')' or ']' or ',' or ';';
}
