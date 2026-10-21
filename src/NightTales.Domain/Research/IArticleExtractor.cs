namespace NightTales.Domain.Research;

/// <summary>
/// The article-extraction port (02.5.2-T0 spike §2, 02.5.2-T1): turns the decoded HTML of one page into its main
/// text. The Domain holds the contract only — no HTTP and no HTML library.
/// </summary>
public interface IArticleExtractor
{
    /// <summary>Extracts the main text of <paramref name="html"/>.</summary>
    /// <param name="html">The decoded page. The raw HTML is never stored by the caller.</param>
    /// <param name="url">The final URL of the page, for relative-link and title resolution.</param>
    /// <param name="contentType">The response media type, or <see langword="null"/> when none was sent.</param>
    /// <param name="ct">Cancels the extraction.</param>
    /// <returns>The extraction; never <see langword="null"/>.</returns>
    ArticleExtraction Extract(string html, Uri url, string? contentType, CancellationToken ct);
}
