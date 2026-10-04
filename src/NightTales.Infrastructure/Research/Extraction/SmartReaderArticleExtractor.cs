using Microsoft.Extensions.Options;
using NightTales.Domain.Research;
using SmartReader;

namespace NightTales.Infrastructure.Research.Extraction;

/// <summary>
/// <see cref="IArticleExtractor"/> over SmartReader 0.11.1, the extractor the 02.5.2-T0 spike chose. The adapter maps
/// SmartReader's own verdict: <c>IsMainTextFound = IsReadable &amp;&amp; TextChars ≥ MinTextChars</c> (02 T104) — never on
/// non-empty text alone, because a nav-dominated page can carry more than the threshold in link text.
/// </summary>
/// <remarks>
/// It parses synchronously and has no cancellation hook, so the caller bounds it: <c>SafeHttpSourceFetcher</c> runs this
/// on a watchdogged worker that holds one in-flight slot until the thread actually ends (T105). The HTML is parsed once
/// by <see cref="HtmlBudgetGuard.Parse"/> (element and depth caps measured on the real DOM), content a browser never
/// renders as text is removed (F5), and the checked document is handed to SmartReader with
/// <see cref="Reader.MaxElemsToParse"/> set to the same element cap.
/// </remarks>
/// <param name="options">The fetcher options (<c>MinTextChars</c>).</param>
public sealed class SmartReaderArticleExtractor(IOptions<FetcherOptions> options) : IArticleExtractor
{
    // Elements whose content is a fallback, a template or a nested browsing context: never visible article text.
    private const string NonRenderedSelector = "iframe, object, embed, video, audio, template, noscript";

    /// <inheritdoc />
    public ArticleExtraction Extract(string html, Uri url, string? contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(url);

        ct.ThrowIfCancellationRequested();

        var limits = options.Value;
        using var document = HtmlBudgetGuard.Parse(html, limits.ExtractMaxElements, limits.ExtractMaxDepth);
        foreach (var element in document.QuerySelectorAll(NonRenderedSelector).ToArray())
        {
            element.Remove();
        }

        ct.ThrowIfCancellationRequested();

        var article = new Reader(url.AbsoluteUri, document) { MaxElemsToParse = limits.ExtractMaxElements }.GetArticle();
        ct.ThrowIfCancellationRequested();

        var text = TextNormalizer.Normalize(article.TextContent);
        var title = string.IsNullOrWhiteSpace(article.Title) ? null : TextNormalizer.Normalize(article.Title).Replace('\n', ' ');

        return new ArticleExtraction(title, text, text.Length, article.IsReadable && text.Length >= options.Value.MinTextChars);
    }
}
