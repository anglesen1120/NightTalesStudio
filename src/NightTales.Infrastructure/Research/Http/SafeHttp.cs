using System.IO.Compression;
using System.Net;
using System.Reflection;
using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Research.Http;

/// <summary>
/// The request and response rules the page fetch and the robots fetch share (02 D11, 02.5.2-T1): one request shape, one
/// redirect-target rule, one decompress-and-count body reader — so robots.txt goes through the same safety pipeline as a
/// page and the two cannot drift apart.
/// </summary>
public static class SafeHttp
{
    /// <summary>The <c>Accept</c> header of a page request.</summary>
    public const string PageAccept = "text/html,application/xhtml+xml,text/plain;q=0.9";

    /// <summary>The <c>Accept</c> header of a robots.txt request.</summary>
    public const string RobotsAccept = "text/plain,text/html;q=0.8";

    /// <summary>
    /// The D11 user agent, <c>NightTalesStudioBot/&lt;version&gt; (+https://&lt;public-host&gt;/bot)</c>.
    /// </summary>
    /// <param name="options">The fetcher options (public host).</param>
    public static string UserAgent(FetcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var version = typeof(SafeHttp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        version = string.IsNullOrWhiteSpace(version) ? "0.1.0" : version.Split('+')[0];

        return $"{FetcherOptions.UserAgentToken}/{version} (+https://{options.PublicHost}/bot)";
    }

    /// <summary>
    /// A GET with exactly the allowed headers: user agent, accept, accept-encoding <c>gzip, br</c> and an optional
    /// <c>Accept-Language</c>. No <c>Authorization</c>, <c>Cookie</c> or <c>Referer</c> is ever added.
    /// </summary>
    /// <param name="uri">The absolute URL.</param>
    /// <param name="userAgent">The user agent.</param>
    /// <param name="accept">The <c>Accept</c> value.</param>
    /// <param name="acceptLanguage">The <c>Accept-Language</c> value, or <see langword="null"/> for none.</param>
    public static HttpRequestMessage CreateGet(Uri uri, string userAgent, string accept, string? acceptLanguage)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");
        if (!string.IsNullOrWhiteSpace(acceptLanguage))
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        return request;
    }

    /// <summary>Whether <paramref name="status"/> is one of the redirects the fetcher follows (301, 302, 303, 307, 308).</summary>
    public static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>The absolute redirect target of <paramref name="response"/> resolved against <paramref name="current"/>, or <see langword="null"/>.</summary>
    /// <param name="current">The URL that was requested.</param>
    /// <param name="response">The redirect response.</param>
    public static Uri? RedirectTarget(Uri current, HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(response);

        var location = response.Headers.Location;
        if (location is null)
        {
            return null;
        }

        var target = location.IsAbsoluteUri ? location : new Uri(current, location);

        return target.IsAbsoluteUri ? target : null;
    }

    /// <summary>
    /// Opens the response body, decompressing <c>gzip</c> and <c>br</c> ourselves (the handler does not), behind a
    /// <see cref="CountingStream"/> with <paramref name="limit"/> on the <em>decompressed</em> bytes. Any other
    /// <c>Content-Encoding</c> (or a chain) is read as-is and fails the content sniff.
    /// </summary>
    /// <param name="response">The response, read with <c>ResponseHeadersRead</c>.</param>
    /// <param name="limit">The decompressed byte cap.</param>
    /// <param name="ct">Cancels the open.</param>
    public static async Task<Stream> OpenBodyAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);

        var raw = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var encodings = response.Content.Headers.ContentEncoding;
        Stream body = encodings.Count == 1
            ? encodings.First().ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => new GZipStream(raw, CompressionMode.Decompress),
                "br" => new BrotliStream(raw, CompressionMode.Decompress),
                _ => raw,
            }
            : raw;

        return new CountingStream(body, limit);
    }

    /// <summary>The <see cref="SourceFetchException"/> inside <paramref name="exception"/>'s chain, or <see langword="null"/>.</summary>
    public static SourceFetchException? FindFetchException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SourceFetchException fetch)
            {
                return fetch;
            }
        }

        return null;
    }
}
