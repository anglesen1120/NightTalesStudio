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
/// It parses synchronously and has no cancellation hook, so the caller bounds it: <c>SafeHttpSourceFetcher</c> runs
/// <see cref="HtmlBudgetGuard"/> first and runs this on a watchdogged worker (T105).
/// </remarks>
/// <param name="options">The fetcher options (<c>MinTextChars</c>).</param>
public sealed class SmartReaderArticleExtractor(IOptions<FetcherOptions> options) : IArticleExtractor
{
    /// <inheritdoc />
    public ArticleExtraction Extract(string html, Uri url, string? contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(url);

        ct.ThrowIfCancellationRequested();

        var article = new Reader(url.AbsoluteUri, html).GetArticle();
        ct.ThrowIfCancellationRequested();

        var text = TextNormalizer.Normalize(article.TextContent);
        var title = string.IsNullOrWhiteSpace(article.Title) ? null : TextNormalizer.Normalize(article.Title).Replace('\n', ' ');

        return new ArticleExtraction(title, text, text.Length, article.IsReadable && text.Length >= options.Value.MinTextChars);
    }
}
