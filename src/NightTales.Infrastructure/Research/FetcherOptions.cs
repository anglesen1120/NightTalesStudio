namespace NightTales.Infrastructure.Research;

/// <summary>
/// The <c>Fetcher</c> section (02.5.1 plan §3.3). 02.5.1-T1 owns <see cref="BlockedDomains"/>; 02.5.2-T1 adds the
/// fetcher's own keys to this class and to <see cref="FetcherOptionsValidator"/>.
/// </summary>
public sealed class FetcherOptions
{
    /// <summary>The configuration section, <c>Fetcher</c>.</summary>
    public const string SectionName = "Fetcher";

    /// <summary>The blocked registrable domains (02 D11): data, changed only through a reviewed PR.</summary>
    public IList<string> BlockedDomains { get; set; } = [];

    /// <summary>The crawler token of the user agent and of robots.txt matching (02 D11).</summary>
    public const string UserAgentToken = "NightTalesStudioBot";

    /// <summary>The public host named in the user-agent contact URL, <c>https://{PublicHost}/bot</c> (02 D11).</summary>
    public string PublicHost { get; set; } = "nighttales.example";

    /// <summary>The one budget for robots, DNS and every redirect hop of a page, in seconds (D11: 10 s).</summary>
    public int TotalTimeoutSeconds { get; set; } = 10;

    /// <summary>The budget of one robots.txt fetch, in seconds (D11: 5 s).</summary>
    public int RobotsTimeoutSeconds { get; set; } = 5;

    /// <summary>How long a robots.txt verdict is cached per origin, in hours (D11: 24 h).</summary>
    public int RobotsCacheHours { get; set; } = 24;

    /// <summary>The longest robots.txt crawl-delay honoured, in seconds; above it the fetch is refused (T43).</summary>
    public int CrawlDelayMaxSeconds { get; set; } = 10;

    /// <summary>The most redirect hops followed for a page or a robots.txt (D11: 3).</summary>
    public int MaxRedirects { get; set; } = 3;

    /// <summary>The cap on decompressed page bytes (D11: 2 MiB).</summary>
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>The cap on decompressed robots.txt bytes (512 KiB).</summary>
    public int MaxRobotsBytes { get; set; } = 512 * 1024;

    /// <summary>The fewest extracted characters that count as an article; below it the page is <c>source_no_text</c> (T0: 1,275, T104).</summary>
    public int MinTextChars { get; set; } = 1_275;

    /// <summary>The most extracted characters stored; a longer article is <c>source_too_large</c> (D11: 300,000).</summary>
    public int MaxTextChars { get; set; } = 300_000;

    /// <summary>The watchdog of the synchronous extraction, in seconds (T105).</summary>
    public int ExtractTimeoutSeconds { get; set; } = 5;

    /// <summary>The most elements the pre-parse guard accepts (T105).</summary>
    public int ExtractMaxElements { get; set; } = 50_000;

    /// <summary>The deepest element nesting the pre-parse guard accepts (T105).</summary>
    public int ExtractMaxDepth { get; set; } = 512;

    /// <summary>The <c>Accept-Language</c> sent when the request carries no episode language.</summary>
    public string AcceptLanguage { get; set; } = "en";

    /// <summary>How long a lease lasts without a heartbeat, in seconds (02 D10: 90 s).</summary>
    public int LeaseSeconds { get; set; } = 90;

    /// <summary>How far a heartbeat moves the lease end from now, in seconds (02 D10: 60 s).</summary>
    public int HeartbeatSeconds { get; set; } = 60;

    /// <summary>
    /// The longest a lease may live in total, in seconds, counted from <c>leased_at</c> (SEC-1: 300). A heartbeat never
    /// extends <c>lease_until</c> past it, and once it has passed the heartbeat answers <c>fetch_lease_lost</c> so the
    /// sweeper reclaims a task whose fetcher keeps heartbeating while the fetch is stuck.
    /// </summary>
    public int MaxLeaseTotalSeconds { get; set; } = 300;

    /// <summary>The most tasks leased platform-wide at once (02 D10, FR-02-8: 4).</summary>
    public int MaxLeasesPlatformWide { get; set; } = 4;

    /// <summary>
    /// The most tasks leased per registrable domain at once (02 D10: 1). The lease statement's per-domain partition is
    /// fixed at one, so the validator accepts no other value; the key documents the contract.
    /// </summary>
    public int MaxLeasesPerDomain { get; set; } = 1;
}
