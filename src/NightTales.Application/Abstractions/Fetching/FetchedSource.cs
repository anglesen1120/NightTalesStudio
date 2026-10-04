namespace NightTales.Application.Abstractions.Fetching;

/// <summary>
/// The result of a successful fetch (02.5.2-T1 step 12). The raw HTML never leaves the fetcher (02 D11).
/// </summary>
/// <param name="FinalUrl">The URL after redirects.</param>
/// <param name="Title">The page title, when found.</param>
/// <param name="ContentType">The media type of the response, lowercase.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the UTF-8 text (<c>content_sha256</c>, F13).</param>
/// <param name="Text">The extracted article text, at most 300,000 characters.</param>
/// <param name="TextChars">The length of <paramref name="Text"/>.</param>
/// <param name="HttpStatus">The HTTP status of the final response.</param>
public sealed record FetchedSource(
    string FinalUrl,
    string? Title,
    string ContentType,
    string Sha256,
    string Text,
    int TextChars,
    int HttpStatus);
