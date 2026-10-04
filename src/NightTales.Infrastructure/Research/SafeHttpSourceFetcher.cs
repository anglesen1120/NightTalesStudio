using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Research.Extraction;
using NightTales.Infrastructure.Research.Http;

namespace NightTales.Infrastructure.Research;

/// <summary>
/// Fetches one user-supplied URL and returns either the extracted article text or a D8 error code, enforcing every D11
/// rule in the order the rules must run: syntax, blocklist, robots, rate limit, request, manual redirect (each hop
/// re-checked), status, content type and sniff, decompressed size, charset, extraction. There is no bypass path: the
/// <see cref="HttpClient"/> is built by <see cref="SafeHttpClientFactory"/> so it cannot follow a redirect itself, cannot
/// resolve a name itself (the connect callback resolves, validates and connects to the validated address) and cannot read
/// more than the byte cap.
/// </summary>
/// <remarks>
/// <para>
/// [DECISION] The 10 s budget is shared across robots, DNS and all redirect hops (D11 "10 s total per page"). The extract
/// phase runs outside it, on its own watchdog (T105).
/// </para>
/// <para>
/// The address and DNS rules live in the connect callback of the <paramref name="http"/> client
/// (<see cref="SafeHttpConnector"/>), not here, so a caller cannot construct a fetcher without them.
/// </para>
/// </remarks>
/// <param name="http">The client from <see cref="SafeHttpClientFactory"/>.</param>
/// <param name="extractor">Turns HTML into article text.</param>
/// <param name="robots">The robots.txt policy.</param>
/// <param name="limiter">The per-domain rate limiter.</param>
/// <param name="blocklist">The blocked domains.</param>
/// <param name="psl">The public-suffix list that re-parses every hop.</param>
/// <param name="opt">The fetcher options.</param>
/// <param name="log">The log: registrable domains and exception types only, never a URL path or text (D19).</param>
public sealed partial class SafeHttpSourceFetcher(
    HttpClient http,
    IArticleExtractor extractor,
    IRobotsPolicy robots,
    IHostRateLimiter limiter,
    DomainBlocklist blocklist,
    IPublicSuffixList psl,
    IOptions<FetcherOptions> opt,
    ILogger<SafeHttpSourceFetcher> log) : ISourceFetcher
{
    private const int SniffBytes = 512;
    private const int MetaScanBytes = 4096;

    private readonly FetcherOptions _options = opt.Value;
    private readonly string _userAgent = SafeHttp.UserAgent(opt.Value);

    static SafeHttpSourceFetcher() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <inheritdoc />
    public async ValueTask<ErrorOr<FetchedSource>> FetchAsync(SourceFetchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await FetchCoreAsync(request, ct).ConfigureAwait(false);
        }
        catch (SourceFetchException ex)
        {
            return ex.Error;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The shared 10 s budget ran out (D11).
            return SourceErrors.SourceUnreachable;
        }
        catch (Exception ex)
        {
            // The connect callback's SourceFetchException surfaces wrapped by HttpClient.
            if (SafeHttp.FindFetchException(ex) is { } inner)
            {
                return inner.Error;
            }

            LogUnexpected(ex.GetType().Name);

            return SourceErrors.SourceUnreachable;
        }
    }

    private async Task<FetchedSource> FetchCoreAsync(SourceFetchRequest request, CancellationToken ct)
    {
        // 1. Syntax + blocklist, again here so a hand-built request cannot skip them.
        var first = SourceUrl.Parse(request.Url.AbsoluteUri, psl);
        if (first.IsError)
        {
            throw new SourceFetchException(SourceErrors.SourceUrlInvalid);
        }

        var current = first.Value;
        if (blocklist.IsBlocked(request.RegistrableDomain) || blocklist.IsBlocked(current.RegistrableDomain))
        {
            throw new SourceFetchException(SourceErrors.SourceDomainNotAllowed);
        }

        // 2. One budget for the whole fetch: robots, DNS and every hop.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(_options.TotalTimeoutSeconds));
        var token = budget.Token;

        for (var hop = 0; ; hop++)
        {
            var uri = new Uri(current.Value, UriKind.Absolute);

            // 3. robots for this hop's origin.
            var decision = await robots.IsAllowedAsync(uri, token).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                LogRobotsDenied(current.RegistrableDomain, decision.Reason);
                throw new SourceFetchException(SourceErrors.SourceBlockedByRobots);
            }

            // 4. rate limit per registrable domain.
            await limiter.WaitAsync(current.RegistrableDomain, decision.MinInterval, token).ConfigureAwait(false);

            // 5. request.
            using var message = SafeHttp.CreateGet(uri, _userAgent, SafeHttp.PageAccept, request.AcceptLanguage ?? _options.AcceptLanguage);
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

            // 6. manual redirect, at most MaxRedirects.
            if (SafeHttp.IsRedirect(response.StatusCode))
            {
                if (hop >= _options.MaxRedirects)
                {
                    throw new SourceFetchException(SourceErrors.SourceUnreachable);
                }

                var location = SafeHttp.RedirectTarget(uri, response)
                    ?? throw new SourceFetchException(SourceErrors.SourceUnreachable);
                var next = SourceUrl.Parse(location.AbsoluteUri, psl);
                if (next.IsError)
                {
                    throw new SourceFetchException(SourceErrors.SourceUrlInvalid);
                }

                if (blocklist.IsBlocked(next.Value.RegistrableDomain))
                {
                    throw new SourceFetchException(SourceErrors.SourceDomainNotAllowed);
                }

                if (current.IsHttps && !next.Value.IsHttps)
                {
                    LogDowngrade(current.RegistrableDomain);
                }

                current = next.Value;
                continue;
            }

            // 7. status.
            var status = (int)response.StatusCode;
            if (status is < 200 or >= 300)
            {
                throw new SourceFetchException(SourceErrors.SourceUnreachableWithStatus(status), status);
            }

            return await ReadAndExtractAsync(response, uri, current, status, token, ct).ConfigureAwait(false);
        }
    }

    private async Task<FetchedSource> ReadAndExtractAsync(
        HttpResponseMessage response,
        Uri uri,
        SourceUrl source,
        int status,
        CancellationToken token,
        CancellationToken ct)
    {
        // 8. content type, then the byte sniff below.
        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        var isPlain = mediaType == "text/plain";
        if (!isPlain && mediaType is not ("text/html" or "application/xhtml+xml"))
        {
            throw new SourceFetchException(SourceErrors.SourceUnsupportedType);
        }

        // 9. decompressed size: the count is on bytes after decompression.
        byte[] bytes;
        await using (var body = await SafeHttp.OpenBodyAsync(response, _options.MaxResponseBytes, token).ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, token).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        if (bytes.Length == 0)
        {
            throw new SourceFetchException(SourceErrors.SourceNoText);
        }

        // 10. charset: header, then meta in the first 4 KiB, then UTF-8.
        var encoding = ChooseEncoding(response.Content.Headers.ContentType?.CharSet, bytes);
        EnsureTextual(bytes, encoding);
        var decoded = encoding.GetString(bytes).TrimStart('\uFEFF');

        // 11. extract.
        ArticleExtraction extraction;
        if (isPlain)
        {
            var plain = TextNormalizer.Normalize(decoded);
            extraction = new ArticleExtraction(null, plain, plain.Length, plain.Length > 0);
        }
        else
        {
            extraction = await ExtractAsync(decoded, uri, mediaType, source.RegistrableDomain, ct).ConfigureAwait(false);
        }

        if (extraction.TextChars > _options.MaxTextChars)
        {
            throw new SourceFetchException(SourceErrors.SourceTooLarge);
        }

        if (!extraction.IsMainTextFound || extraction.TextChars < _options.MinTextChars)
        {
            throw new SourceFetchException(SourceErrors.SourceNoText);
        }

        // 12. hash + result. The raw HTML is dropped here and never leaves the method.
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(extraction.Text)));

        return new FetchedSource(source.Value, extraction.Title, mediaType!, sha256, extraction.Text, extraction.TextChars, status);
    }

    private async Task<ArticleExtraction> ExtractAsync(string html, Uri uri, string? mediaType, string domain, CancellationToken ct)
    {
        // T105: the guard rejects a pathological document before any DOM is built.
        HtmlBudgetGuard.Check(html, _options.ExtractMaxElements, _options.ExtractMaxDepth);

        // The parse is synchronous and outside the request budget: run it on a dedicated worker with a watchdog.
        using var worker = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var work = Task.Factory.StartNew(
            () => extractor.Extract(html, uri, mediaType, worker.Token),
            worker.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            return await work.WaitAsync(TimeSpan.FromSeconds(_options.ExtractTimeoutSeconds), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await worker.CancelAsync().ConfigureAwait(false);

            // Observe a late fault so it does not surface as an unobserved task exception.
            _ = work.ContinueWith(
                static finished => _ = finished.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            LogExtractTimeout(domain);

            throw new SourceFetchException(SourceErrors.SourceNoText);
        }
    }

    // The charset: the Content-Type header, then <meta charset> / <meta http-equiv> in the first 4 KiB, then UTF-8. An
    // unknown name falls through to the next source so a mis-declared page degrades instead of throwing.
    private static Encoding ChooseEncoding(string? headerCharset, byte[] bytes)
    {
        var fromHeader = Find(headerCharset);
        if (fromHeader is not null)
        {
            return fromHeader;
        }

        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, MetaScanBytes));
        var match = MetaCharset().Match(head);
        if (match.Success && Find(match.Groups[1].Value) is { } fromMeta)
        {
            return fromMeta;
        }

        return Utf8Replacement;
    }

    private static Encoding Utf8Replacement { get; } =
        Encoding.GetEncoding("utf-8", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);

    private static Encoding? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(name.Trim().Trim('"', '\''), EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // "Declared html, actually a zip": binary control bytes, or bytes that are invalid under the declared charset, in the
    // first 512 bytes make the body not text.
    private static void EnsureTextual(byte[] bytes, Encoding encoding)
    {
        var length = Math.Min(bytes.Length, SniffBytes);
        for (var i = 0; i < length; i++)
        {
            var b = bytes[i];
            if (b <= 0x08 || b == 0x0B || (b >= 0x0E && b <= 0x1A) || (b >= 0x1C && b <= 0x1F))
            {
                throw new SourceFetchException(SourceErrors.SourceUnsupportedType);
            }
        }

        var strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try
        {
            // flush: false, so a multi-byte sequence cut at byte 512 is not an error.
            _ = strict.GetDecoder().GetCharCount(bytes, 0, length, flush: false);
        }
        catch (DecoderFallbackException)
        {
            throw new SourceFetchException(SourceErrors.SourceUnsupportedType);
        }
    }

    [GeneratedRegex("""<meta[^>]+?charset\s*=\s*["']?\s*([A-Za-z0-9_\-:.]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex MetaCharset();

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "fetch blocked by robots for {Domain}: {Reason}")]
    private partial void LogRobotsDenied(string domain, string? reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "fetch redirect from https to http on {Domain}")]
    private partial void LogDowngrade(string domain);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "fetch extract_timeout for {Domain}")]
    private partial void LogExtractTimeout(string domain);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "fetch failed unexpectedly: {ExceptionType}")]
    private partial void LogUnexpected(string exceptionType);
}
