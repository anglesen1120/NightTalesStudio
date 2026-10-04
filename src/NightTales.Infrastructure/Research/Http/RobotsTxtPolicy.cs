using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Research.Http;

/// <summary>
/// The robots.txt policy of the fetcher (02 D11, 02.5.2-T0 spec): owns the robots fetch — through the same safe
/// <see cref="HttpClient"/> as a page, so the same address rules, no proxy, no cookies and a decompressed byte cap
/// apply — and the 24 h per-origin cache.
/// </summary>
/// <remarks>
/// Failure semantics (D11): 2xx parse; 3xx follow at most three hops (each re-checked); 4xx allow all; 5xx, timeout,
/// connection failure, an over-cap file, an unsupported type or too many redirects disallow all. A verdict, including a
/// disallow, is cached for the whole TTL, so a failing host is not retried on every page. The crawl delay becomes the
/// decision's <see cref="RobotsDecision.MinInterval"/> (the 1 s floor or the larger delay); a delay above the cap denies.
/// </remarks>
/// <param name="http">The safe client from <see cref="SafeHttpClientFactory"/>.</param>
/// <param name="psl">The public-suffix list that checks every redirect target.</param>
/// <param name="blocklist">The blocked domains, checked at every robots redirect hop.</param>
/// <param name="options">The fetcher options.</param>
/// <param name="time">The time source of the cache.</param>
/// <param name="log">The log; never given a URL path.</param>
public sealed partial class RobotsTxtPolicy(
    HttpClient http,
    IPublicSuffixList psl,
    DomainBlocklist blocklist,
    IOptions<FetcherOptions> options,
    TimeProvider time,
    ILogger<RobotsTxtPolicy> log) : IRobotsPolicy
{
    /// <summary>The most origins kept in the cache before the least recently used are dropped.</summary>
    public const int MaxCachedOrigins = 1_000;

    /// <summary>The floor of the per-host interval: one request per second.</summary>
    public static readonly TimeSpan MinIntervalFloor = TimeSpan.FromSeconds(1);

    private readonly FetcherOptions _options = options.Value;
    private readonly string _userAgent = SafeHttp.UserAgent(options.Value);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <summary>The number of cached origins (for tests).</summary>
    public int CachedOrigins => _cache.Count;

    /// <inheritdoc />
    public async ValueTask<RobotsDecision> IsAllowedAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);

        var origin = url.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        var now = time.GetUtcNow();

        if (!_cache.TryGetValue(origin, out var entry) || entry.ExpiresAt <= now)
        {
            entry = await LoadAsync(url, now, ct).ConfigureAwait(false);
            _cache[origin] = entry;
            Trim();
        }
        else
        {
            entry.LastUsed = now;
        }

        return Decide(entry, url);
    }

    private RobotsDecision Decide(CacheEntry entry, Uri url)
    {
        if (entry.DenyAll)
        {
            return new RobotsDecision(false, entry.Reason ?? "robots_disallow_all", MinIntervalFloor);
        }

        var rules = entry.Rules!;
        var interval = MinIntervalFloor;
        if (rules.CrawlDelay is { } delay)
        {
            if (delay > TimeSpan.FromSeconds(_options.CrawlDelayMaxSeconds))
            {
                return new RobotsDecision(false, "robots_crawl_delay_too_long", MinIntervalFloor);
            }

            interval = delay > interval ? delay : interval;
        }

        return rules.IsAllowed(url.PathAndQuery)
            ? new RobotsDecision(true, null, interval)
            : new RobotsDecision(false, "robots_disallow", interval);
    }

    private async Task<CacheEntry> LoadAsync(Uri url, DateTimeOffset now, CancellationToken ct)
    {
        var expires = now + TimeSpan.FromHours(_options.RobotsCacheHours);

        using var robotsBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        robotsBudget.CancelAfter(TimeSpan.FromSeconds(_options.RobotsTimeoutSeconds));

        try
        {
            var current = new Uri(url.GetLeftPart(UriPartial.Authority) + "/robots.txt");
            for (var hop = 0; ; hop++)
            {
                using var request = SafeHttp.CreateGet(current, _userAgent, SafeHttp.RobotsAccept, acceptLanguage: null);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, robotsBudget.Token)
                    .ConfigureAwait(false);

                if (SafeHttp.IsRedirect(response.StatusCode))
                {
                    if (hop >= _options.MaxRedirects)
                    {
                        return Deny(expires, "robots_too_many_redirects");
                    }

                    var next = SafeHttp.RedirectTarget(current, response);
                    var target = next is null ? default : SourceUrl.Parse(next.AbsoluteUri, psl);
                    if (next is null || target.IsError || blocklist.IsBlocked(target.Value.RegistrableDomain))
                    {
                        return Deny(expires, "robots_redirect_rejected");
                    }

                    current = new Uri(target.Value.Value);
                    continue;
                }

                var status = (int)response.StatusCode;
                if (status is >= 400 and < 500)
                {
                    return new CacheEntry(RobotsRules.AllowAll, false, null, expires, now);
                }

                if (status is < 200 or >= 300)
                {
                    return Deny(expires, "robots_server_error");
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (mediaType is not ("text/plain" or "text/html"))
                {
                    return Deny(expires, "robots_unsupported_type");
                }

                var body = await ReadBodyAsync(response, robotsBudget.Token).ConfigureAwait(false);
                if (body is null)
                {
                    LogRuleLimit();
                    return Deny(expires, "robots_too_large");
                }

                var rules = RobotsRules.Parse(body, FetcherOptions.UserAgentToken);
                if (rules.RuleLimitExceeded)
                {
                    LogRuleLimit();
                }

                return new CacheEntry(rules, false, null, expires, now);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Deny(expires, "robots_timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or SourceFetchException or IOException)
        {
            var failure = SafeHttp.FindFetchException(ex)?.Error.Code ?? ex.GetType().Name;
            LogRobotsFailure(failure);
            return Deny(expires, "robots_unreachable");
        }
    }

    private async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var body = await SafeHttp.OpenBodyAsync(response, _options.MaxRobotsBytes, ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, ct).ConfigureAwait(false);

            // Invalid UTF-8 decodes with replacement characters and a BOM is skipped: never throws.
            var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
            if (bytes.StartsWith(Encoding.UTF8.Preamble))
            {
                bytes = bytes[Encoding.UTF8.Preamble.Length..];
            }

            return Encoding.UTF8.GetString(bytes);
        }
        catch (SourceFetchException ex) when (ex.Error.Code == SourceErrors.SourceTooLargeCode)
        {
            return null;
        }
    }

    private static CacheEntry Deny(DateTimeOffset expires, string reason) =>
        new(null, true, reason, expires, expires);

    private void Trim()
    {
        if (_cache.Count <= MaxCachedOrigins)
        {
            return;
        }

        foreach (var stale in _cache.OrderBy(pair => pair.Value.LastUsed).Take(_cache.Count - MaxCachedOrigins))
        {
            _cache.TryRemove(stale.Key, out _);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "robots_rule_limit_exceeded")]
    private partial void LogRuleLimit();

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "robots fetch failed: {Reason}")]
    private partial void LogRobotsFailure(string reason);

    private sealed class CacheEntry(RobotsRules? rules, bool denyAll, string? reason, DateTimeOffset expiresAt, DateTimeOffset lastUsed)
    {
        public RobotsRules? Rules { get; } = rules;

        public bool DenyAll { get; } = denyAll;

        public string? Reason { get; } = reason;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }
}
