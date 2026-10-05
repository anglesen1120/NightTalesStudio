using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using NightTales.Domain.Research;

namespace NightTales.Fetcher.Core.Research.Extraction;

/// <summary>
/// The pre-extract guard of the extract phase (02.5.2-T1 T105, SEC-0928-1). It measures the <em>real</em> DOM instead of
/// approximating the HTML tokenizer: the document is parsed by AngleSharp's <see cref="HtmlParser"/> (an explicit-stack
/// tree builder, so the parse itself does not recurse and its cost is bounded by the byte cap the caller already
/// enforced), the element cap aborts the parse as soon as it is crossed, and the depth cap is checked by an iterative
/// walk (explicit stack, no recursion). Anything the real tokenizer and tree builder do — self-closing flags on non-void
/// elements, foreign content, abruptly closed comments, mismatched end tags, odd quoting — is therefore counted exactly,
/// because the count comes from the tree they built. A <see cref="StackOverflowException"/> cannot be caught, so a
/// document over the depth cap must never reach a recursive consumer.
/// </summary>
public static class HtmlBudgetGuard
{
    /// <summary>Throws <c>source_too_large</c> when <paramref name="html"/> exceeds either bound.</summary>
    /// <param name="html">The decoded HTML.</param>
    /// <param name="maxElements">The most elements allowed (<c>Fetcher:ExtractMaxElements</c>).</param>
    /// <param name="maxDepth">The deepest nesting allowed (<c>Fetcher:ExtractMaxDepth</c>).</param>
    /// <exception cref="SourceFetchException"><c>source_too_large</c>: a bound is exceeded.</exception>
    public static void Check(string html, int maxElements, int maxDepth)
    {
        using var document = Parse(html, maxElements, maxDepth);
    }

    /// <summary>
    /// Parses <paramref name="html"/> and returns the document only when it is within both bounds, so the caller can
    /// hand the already-checked tree to its consumer instead of parsing a second time.
    /// </summary>
    /// <param name="html">The decoded HTML.</param>
    /// <param name="maxElements">The most elements allowed, counting the implied <c>html</c>, <c>head</c> and <c>body</c>.</param>
    /// <param name="maxDepth">The deepest element nesting allowed; <c>html</c> is depth 1.</param>
    /// <returns>The parsed document; the caller disposes it.</returns>
    /// <exception cref="SourceFetchException"><c>source_too_large</c>: a bound is exceeded.</exception>
    public static IHtmlDocument Parse(string html, int maxElements, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(html);

        var created = 0;
        IElement? previous = null;
        var parser = new HtmlParser(new HtmlParserOptions
        {
            // Aborts a hostile document while it is being built instead of after: the parse is superlinear in depth (50,000
            // nested elements cost seconds), so waiting for the finished tree would spend the watchdog on it. An element is
            // not attached yet when this fires, but the one created before it is, so its real ancestor count is read here.
            OnCreated = (element, _) =>
            {
                if (++created > maxElements || (previous is not null && DepthOf(previous, maxDepth) > maxDepth))
                {
                    throw new SourceFetchException(SourceErrors.SourceTooLarge);
                }

                previous = element;
            },
        });

        var document = parser.ParseDocument(html);

        // A rejected document is dropped, not disposed: it can be as deep as the element cap allows.
        EnsureWithinBounds(document, maxElements, maxDepth);

        return document;
    }

    // The element's real ancestor count (itself included), counting no further than the cap is exceeded.
    private static int DepthOf(IElement element, int maxDepth)
    {
        var depth = 1;
        for (var parent = element.ParentElement; parent is not null && depth <= maxDepth; parent = parent.ParentElement)
        {
            depth++;
        }

        return depth;
    }

    // Iterative on purpose: the tree may be as deep as the element cap before it is rejected. A <template>'s children
    // live in its content fragment, not in ChildNodes, so the walk enters that too.
    private static void EnsureWithinBounds(IDocument document, int maxElements, int maxDepth)
    {
        var elements = 0;
        var pending = new Stack<(INode Node, int Depth)>();
        pending.Push((document, 0));

        while (pending.TryPop(out var current))
        {
            Visit(current.Node.ChildNodes, current.Depth);

            if (current.Node is IHtmlTemplateElement template)
            {
                Visit(template.Content.ChildNodes, current.Depth);
            }
        }

        void Visit(INodeList children, int parentDepth)
        {
            foreach (var child in children)
            {
                if (child is not IElement)
                {
                    continue;
                }

                if (++elements > maxElements || parentDepth + 1 > maxDepth)
                {
                    throw new SourceFetchException(SourceErrors.SourceTooLarge);
                }

                pending.Push((child, parentDepth + 1));
            }
        }
    }
}
