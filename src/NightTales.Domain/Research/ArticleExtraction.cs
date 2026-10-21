namespace NightTales.Domain.Research;

/// <summary>The result of <see cref="IArticleExtractor.Extract"/> (02.5.2-T0 spike §2).</summary>
/// <param name="Title">The page title, or <see langword="null"/> when none was found.</param>
/// <param name="Text">The main text: whitespace-normalised NFC, no HTML, no markdown.</param>
/// <param name="TextChars">The length of <paramref name="Text"/> in UTF-16 code units.</param>
/// <param name="IsMainTextFound">
/// Whether the extractor found an article. <see langword="false"/> maps to <c>source_no_text</c> (02.5.2-T1 T104).
/// </param>
public sealed record ArticleExtraction(string? Title, string Text, int TextChars, bool IsMainTextFound);
