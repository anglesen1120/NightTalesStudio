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

        return MaskText(text, keys) ?? text;
    }

    /// <summary>
    /// The text with every listed pair masked, or null when nothing changed. Scanning resumes right after each
    /// <c>=</c>, so a listed pair nested in an unlisted value (<c>next=/cb?code=x</c>) is found; an unlisted
    /// value that is percent-encoded is decoded and rescanned, and masked whole when it hides a listed pair.
    /// A pair that follows <c>?</c> or <c>&amp;</c> is URL text, whose value may hold <c>; , ( )</c>, so it runs
    /// to the next <c>&amp; # quote whitespace</c>; a quoted value runs to its closing quote.
    /// </summary>
    private static string? MaskText(ReadOnlySpan<char> span, IReadOnlySet<string> keys)
    {
        StringBuilder? result = null;
        var copied = 0;
        var from = 0;
        // One value run per terminator mode is kept: valueStart only moves forward, and a run holds no
        // terminator, so every later '=' inside it ends at the same runEnd — each char is scanned once.
        var urlEnd = -1;
        var urlPct = -1;
        var proseEnd = -1;
        var prosePct = -1;
        // A value already decoded and rescanned cannot hide a listed pair inside itself, so decoded values
        // bounded by checkedTo are not checked again — the total decoded text stays O(n).
        var checkedTo = -1;
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

            var urlMode = keyStart > 0 && span[keyStart - 1] is '?' or '&';
            var valueStart = equals + 1;
            var quote = valueStart < span.Length && span[valueStart] is '"' or '\'' ? span[valueStart] : '\0';
            if (quote != '\0')
            {
                valueStart++;
            }

            int valueEnd;
            bool hasPct;
            if (quote != '\0')
            {
                // A quoted value ends at its own closing quote; quoted pairs never overlap, so no cache.
                valueEnd = valueStart;
                while (valueEnd < span.Length && span[valueEnd] != quote)
                {
                    valueEnd++;
                }

                hasPct = span[valueStart..valueEnd].IndexOf('%') >= 0;
            }
            else if (urlMode)
            {
                if (valueStart <= urlEnd)
                {
                    valueEnd = urlEnd;
                    hasPct = urlPct >= valueStart;
                }
                else
                {
                    valueEnd = valueStart;
                    urlPct = -1;
                    while (valueEnd < span.Length && !IsValueTerminator(span[valueEnd], urlMode: true))
                    {
                        if (span[valueEnd] == '%')
                        {
                            urlPct = valueEnd;
                        }

                        valueEnd++;
                    }

                    urlEnd = valueEnd;
                    hasPct = urlPct >= valueStart;
                }
            }
            else
            {
                if (valueStart <= proseEnd)
                {
                    valueEnd = proseEnd;
                    hasPct = prosePct >= valueStart;
                }
                else
                {
                    valueEnd = valueStart;
                    prosePct = -1;
                    while (valueEnd < span.Length && !IsValueTerminator(span[valueEnd], urlMode: false))
                    {
                        if (span[valueEnd] == '%')
                        {
                            prosePct = valueEnd;
                        }

                        valueEnd++;
                    }

                    proseEnd = valueEnd;
                    hasPct = prosePct >= valueStart;
                }
            }

            var value = span[valueStart..valueEnd];
            var listed = keyStart < equals && IsListed(span[keyStart..equals], keys);
            var hides = !listed && hasPct && valueEnd > checkedTo && HidesListedPair(value, keys);
            if (!listed && hasPct && valueEnd > checkedTo)
            {
                checkedTo = valueEnd;
            }

            if (value.Length > 0
                && !value.SequenceEqual(RedactionOptions.Mask)
                && (listed || hides))
            {
                result ??= new StringBuilder(span.Length);
                result.Append(span[copied..valueStart]).Append(RedactionOptions.Mask);
                copied = valueEnd;
                from = valueEnd;
                continue;
            }

            from = equals + 1;
        }

        return result?.Append(span[copied..]).ToString();
    }

    /// <summary>True when a percent-encoded value (decoded up to three times) carries a listed pair.</summary>
    private static bool HidesListedPair(ReadOnlySpan<char> value, IReadOnlySet<string> keys)
    {
        if (value.IndexOf('%') < 0)
        {
            return false;
        }

        var decoded = value.ToString();
        for (var round = 0; round < 3; round++)
        {
            var next = Uri.UnescapeDataString(decoded);
            if (next == decoded)
            {
                break;
            }

            decoded = next;
            if (MaskText(decoded, keys) is not null)
            {
                return true;
            }
        }

        return false;
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
                else if (segment[(equals + 1)..].IndexOfAny('?', '=', '%') >= 0
                    && MaskText(segment, keys) is { } nested)
                {
                    result ??= new StringBuilder(query.Length);
                    result.Append(query[copied..position]).Append(nested);
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

    private static bool IsValueTerminator(char c, bool urlMode) =>
        char.IsWhiteSpace(c) || c is '&' or '"' or '\'' or '#'
        || (!urlMode && c is ')' or ']' or ',' or ';');
}
