using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Research.Extraction;

/// <summary>
/// The pre-parse guard of the extract phase (02.5.2-T1 T105, SEC-0928-1): a streaming tokenizer that counts elements and
/// nesting depth over the decoded HTML <em>without building a DOM</em>, so a pathological document is rejected before
/// AngleSharp sees it — a <see cref="StackOverflowException"/> in the parser cannot be caught, so the depth check must run
/// first. Memory is bounded: the open-element stack never holds more than <c>maxDepth + 1</c> names.
/// </summary>
public static class HtmlBudgetGuard
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title",
    };

    /// <summary>Throws <c>source_too_large</c> when <paramref name="html"/> exceeds either bound.</summary>
    /// <param name="html">The decoded HTML.</param>
    /// <param name="maxElements">The most elements allowed (<c>Fetcher:ExtractMaxElements</c>).</param>
    /// <param name="maxDepth">The deepest nesting allowed (<c>Fetcher:ExtractMaxDepth</c>).</param>
    /// <exception cref="SourceFetchException"><c>source_too_large</c>: a bound is exceeded.</exception>
    public static void Check(string html, int maxElements, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(html);

        var elements = 0;
        var open = new List<string>();
        var index = 0;

        while (index < html.Length)
        {
            var lt = html.IndexOf('<', index);
            if (lt < 0 || lt + 1 >= html.Length)
            {
                return;
            }

            var next = html[lt + 1];
            if (next == '!')
            {
                index = SkipComment(html, lt);
                continue;
            }

            if (next == '?')
            {
                index = IndexAfter(html, '>', lt + 2);
                continue;
            }

            var closing = next == '/';
            var nameStart = lt + (closing ? 2 : 1);
            if (nameStart >= html.Length || !char.IsAsciiLetter(html[nameStart]))
            {
                index = lt + 1;
                continue;
            }

            var nameEnd = nameStart;
            while (nameEnd < html.Length && IsNameChar(html[nameEnd]))
            {
                nameEnd++;
            }

            var name = html[nameStart..nameEnd].ToLowerInvariant();
            var tagEnd = SkipTag(html, nameEnd, out var selfClosing);

            if (closing)
            {
                var at = open.LastIndexOf(name);
                if (at >= 0)
                {
                    open.RemoveRange(at, open.Count - at);
                }

                index = tagEnd;
                continue;
            }

            elements++;
            if (elements > maxElements)
            {
                throw new SourceFetchException(SourceErrors.SourceTooLarge);
            }

            if (VoidElements.Contains(name) || selfClosing)
            {
                index = tagEnd;
                continue;
            }

            open.Add(name);
            if (open.Count > maxDepth)
            {
                throw new SourceFetchException(SourceErrors.SourceTooLarge);
            }

            index = RawTextElements.Contains(name) ? SkipRawText(html, tagEnd, name) : tagEnd;
        }
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or ':' or '_';

    private static int IndexAfter(string html, char c, int from)
    {
        var at = html.IndexOf(c, from);

        return at < 0 ? html.Length : at + 1;
    }

    private static int SkipComment(string html, int lt)
    {
        if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
        {
            var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);

            return end < 0 ? html.Length : end + 3;
        }

        return IndexAfter(html, '>', lt + 2);
    }

    // Walks the attributes honouring quotes, so a '>' inside a value does not end the tag.
    private static int SkipTag(string html, int from, out bool selfClosing)
    {
        selfClosing = false;
        var quote = '\0';
        for (var i = from; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                selfClosing = i > from && html[i - 1] == '/';

                return i + 1;
            }
        }

        return html.Length;
    }

    private static int SkipRawText(string html, int from, string name)
    {
        var end = html.IndexOf("</" + name, from, StringComparison.OrdinalIgnoreCase);

        return end < 0 ? html.Length : end;
    }
}
