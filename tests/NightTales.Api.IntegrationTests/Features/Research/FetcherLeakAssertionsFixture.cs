using System.Collections.Concurrent;
using System.Net;
using ErrorOr;
using Microsoft.Extensions.Logging;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Fetching;
using NightTales.Fetcher.Core.Research;
using NightTales.Fetcher.Core.Research.Extraction;
using NightTales.Fetcher.Core.Research.Http;
using NightTales.TestKit.Fetching;
using NightTales.TestKit.Research;
using Shouldly;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>Which address validator a <see cref="FetchStack"/> runs with (02 D20).</summary>
internal enum ValidatorMode
{
    /// <summary>The test validator: loopback is reachable, everything else goes through the production rules.</summary>
    LoopbackAllowing,

    /// <summary>The production validator: nothing on this machine is reachable, so the test server must see nothing.</summary>
    GlobalUnicast,
}

/// <summary>A <see cref="TimeProvider"/> a test moves forward by hand (the robots cache lifetime, case 28).</summary>
/// <param name="start">The first instant.</param>
internal sealed class AdjustableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">The amount.</param>
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>
/// The fetch stack of the SSRF suite (02.5.2-T4): the real <see cref="SafeHttpSourceFetcher"/> over the real
/// <see cref="SafeHttpClientFactory"/> client, <see cref="SafeHttpConnector"/>, robots policy, content sniff and article
/// extractor, with only the 02 D20 seams swapped - a <see cref="FakeDnsResolver"/> (every name resolves to loopback unless
/// mapped), the chosen <see cref="ValidatorMode"/>, and a connector that dials the <see cref="HttpTestServer"/> instead of
/// port 80/443. Nothing leaves the machine. Disposing the stack runs the D19 leak scan over every log line it captured.
/// </summary>
internal sealed class FetchStack : IAsyncDisposable
{
    /// <summary>A valid page URL whose path and query are the markers of the leak scan.</summary>
    public const string Page = "http://page-e2e.com/secret-path/article?q=secret-q";

    /// <summary>The strings that must never reach a log line (02 D19): the URL path, its query, the title and the text.</summary>
    public static readonly string[] Secrets =
        ["secret-path", "secret-q", HttpTestRoutes.SecretTitle, HttpTestRoutes.SecretText];

    /// <summary>The D11 initial blocklist plus a shortener, which the initial list does not name (see the suite's open items).</summary>
    public static readonly string[] BlockedDomains =
    [
        "youtube.com", "youtu.be", "youtube-nocookie.com", "tiktok.com", "facebook.com", "fb.watch", "fb.com", "instagram.com",
        "threads.net", "x.com", "twitter.com", "t.co", "reddit.com", "vimeo.com", "dailymotion.com", "twitch.tv", "bilibili.com",
        "zalo.me", "zingmp3.vn", "bit.ly",
    ];

    private static readonly ConcurrentQueue<string> ObservedQueue = new();

    private readonly HttpClient _http;

    private FetchStack(
        ValidatorMode mode,
        HttpTestServer server,
        FakeDnsResolver dns,
        FetcherOptions options,
        CapturingLoggerProvider logs,
        HttpClient http,
        IHostRateLimiter limiter,
        SafeHttpSourceFetcher fetcher)
    {
        Mode = mode;
        Server = server;
        Dns = dns;
        Options = options;
        Logs = logs;
        _http = http;
        Limiter = limiter;
        Fetcher = fetcher;
    }

    /// <summary>Every D8 code any stack of the process has observed (the code-set reflection check reads it).</summary>
    public static IReadOnlyCollection<string> ObservedCodes => [.. ObservedQueue];

    /// <summary>The validator this stack ran with; the suite prints it.</summary>
    public ValidatorMode Mode { get; }

    /// <summary>The loopback page server and its request log.</summary>
    public HttpTestServer Server { get; }

    /// <summary>The fake resolver and its resolution log.</summary>
    public FakeDnsResolver Dns { get; }

    /// <summary>The fetcher options of this stack.</summary>
    public FetcherOptions Options { get; }

    /// <summary>Every log line the fetch stack wrote.</summary>
    public CapturingLoggerProvider Logs { get; }

    /// <summary>The limiter the fetcher waits on.</summary>
    public IHostRateLimiter Limiter { get; }

    /// <summary>The fetcher under test.</summary>
    public SafeHttpSourceFetcher Fetcher { get; }

    /// <summary>The answer for <c>/robots.txt</c>; a 404 (allow all) by default.</summary>
    public Func<TestRequest, TestResponse> Robots { get; set; } = _ => HttpTestRoutes.RobotsNotFound();

    /// <summary>The page requests the server received (every request except <c>/robots.txt</c>).</summary>
    public IReadOnlyList<TestRequest> PageRequests =>
        [.. Server.Requests.Where(request => !request.Target.StartsWith("/robots.txt", StringComparison.Ordinal))];

    /// <summary>The robots.txt requests the server received.</summary>
    public IReadOnlyList<TestRequest> RobotsRequests =>
        [.. Server.Requests.Where(request => request.Target.StartsWith("/robots.txt", StringComparison.Ordinal))];

    /// <summary>
    /// Builds the stack. <paramref name="script"/> answers every request except <c>/robots.txt</c>, which <see cref="Robots"/>
    /// answers.
    /// </summary>
    /// <param name="script">The page routes, normally <see cref="HttpTestRoutes.Standard"/>.</param>
    /// <param name="mode">The address validator.</param>
    /// <param name="configure">Adjusts the options before the stack is built.</param>
    /// <param name="validator">Replaces the validator of <paramref name="mode"/> (the scripted-validator defence-in-depth case).</param>
    /// <param name="extractor">Replaces the real article extractor.</param>
    /// <param name="realRateLimiter">Uses the production <see cref="HostRateLimiter"/> instead of a limiter that never waits.</param>
    /// <param name="robotsTime">The clock of the robots cache.</param>
    /// <param name="dns">The resolver; every name answers loopback by default.</param>
    public static FetchStack Create(
        Func<TestRequest, TestResponse> script,
        ValidatorMode mode = ValidatorMode.LoopbackAllowing,
        Action<FetcherOptions>? configure = null,
        IAddressValidator? validator = null,
        IArticleExtractor? extractor = null,
        bool realRateLimiter = false,
        TimeProvider? robotsTime = null,
        FakeDnsResolver? dns = null)
    {
        var options = new FetcherOptions
        {
            BlockedDomains = [.. BlockedDomains],
            MinTextChars = 200,
            TotalTimeoutSeconds = 10,
            RobotsTimeoutSeconds = 5,
            ExtractTimeoutSeconds = 5,
        };
        configure?.Invoke(options);

        dns ??= new FakeDnsResolver { DefaultAnswer = [IPAddress.Loopback] };
        var logs = new CapturingLoggerProvider();
        var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var box = new RobotsBox();
        var server = new HttpTestServer(request => request.Target.StartsWith("/robots.txt", StringComparison.Ordinal)
            ? box.Stack!.Robots(request)
            : script(request));
        var connector = new SafeHttpConnector(
            dns,
            validator ?? (mode == ValidatorMode.GlobalUnicast ? new GlobalUnicastAddressValidator() : new LoopbackAllowingAddressValidator()),
            factory.CreateLogger<SafeHttpConnector>(),
            server.EndpointFor(HttpTestServer.ClosedPort()));
        var http = SafeHttpClientFactory.Create(connector);
        var psl = new FakePublicSuffixList();
        var blocklist = new DomainBlocklist(options.BlockedDomains.ToHashSet(StringComparer.Ordinal));
        var accessor = Microsoft.Extensions.Options.Options.Create(options);
        IHostRateLimiter limiter = realRateLimiter ? new HostRateLimiter(TimeProvider.System) : new FakeHostRateLimiter();
        var robots = new RobotsTxtPolicy(
            http,
            psl,
            new FakeHostRateLimiter(),
            blocklist,
            accessor,
            robotsTime ?? TimeProvider.System,
            factory.CreateLogger<RobotsTxtPolicy>());
        var fetcher = new SafeHttpSourceFetcher(
            http,
            extractor ?? new SmartReaderArticleExtractor(accessor),
            robots,
            limiter,
            blocklist,
            psl,
            accessor,
            factory.CreateLogger<SafeHttpSourceFetcher>());
        var stack = new FetchStack(mode, server, dns, options, logs, http, limiter, fetcher);
        box.Stack = stack;

        return stack;
    }

    /// <summary>The registrable domain of <paramref name="host"/> under the test public-suffix list.</summary>
    /// <param name="host">The host name.</param>
    public static string RegistrableOf(string host) => new FakePublicSuffixList().GetRegistrableDomain(host) ?? host;

    /// <summary>Fetches <paramref name="url"/> the way the loop does: a request built from the pasted URL.</summary>
    /// <param name="url">The URL; <see cref="Page"/> by default.</param>
    /// <param name="ct">Cancels the fetch.</param>
    public ValueTask<ErrorOr<FetchedSource>> FetchAsync(string url = Page, CancellationToken ct = default)
    {
        var uri = new Uri(url);

        return Fetcher.FetchAsync(new SourceFetchRequest(uri, RegistrableOf(uri.IdnHost), 1), ct);
    }

    /// <summary>Asserts the fetch failed and returns its D8 code, recording it for the catalogue check.</summary>
    /// <param name="result">The fetch result.</param>
    public static string Code(ErrorOr<FetchedSource> result)
    {
        result.IsError.ShouldBeTrue("the fetch was expected to fail with a D8 code");
        ObservedQueue.Enqueue(result.FirstError.Code);

        return result.FirstError.Code;
    }

    /// <summary>"The request never happened": no connection and no request reached the page server.</summary>
    /// <param name="because">What was blocked.</param>
    public void ShouldNeverHaveReachedTheServer(string because)
    {
        Server.Connections.ShouldBe(0, $"{because}: the server accepted a connection");
        Server.Requests.ShouldBeEmpty($"{because}: the server saw a request");
    }

    /// <summary>The D19 scan: no captured log line carries a URL path, query, title or extracted text.</summary>
    public void AssertNoLeak() => FetcherSuiteAsserts.NoSecrets(Logs.Lines, Secrets, "fetcher log");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await Server.DisposeAsync();
        AssertNoLeak();
    }

    private sealed class RobotsBox
    {
        public FetchStack? Stack { get; set; }
    }
}

/// <summary>Assertions shared by both suite classes.</summary>
internal static class FetcherSuiteAsserts
{
    /// <summary>
    /// The D8 job-outcome codes of <c>story_sources.error_code</c> (02 README D8), plus <c>source_url_invalid</c>: the fetcher
    /// reports it for a rejected redirect target and the catalogue lists it as a 400 code only (flagged to the TL, see the
    /// suite's open items).
    /// </summary>
    public static readonly IReadOnlySet<string> JobCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "source_unreachable",
        "source_too_large",
        "source_blocked_by_robots",
        "source_unsupported_type",
        "source_no_text",
        "source_domain_not_allowed",
        "source_url_invalid",
    };

    /// <summary>Fails when any of <paramref name="lines"/> contains one of <paramref name="secrets"/>.</summary>
    /// <param name="lines">The captured text.</param>
    /// <param name="secrets">The forbidden fragments.</param>
    /// <param name="surface">What was scanned, for the failure message.</param>
    public static void NoSecrets(IEnumerable<string> lines, IEnumerable<string> secrets, string surface)
    {
        var fragments = secrets.ToList();
        foreach (var line in lines)
        {
            foreach (var secret in fragments)
            {
                line.ShouldNotContain(secret, Case.Insensitive, $"a {surface} line leaks a URL path, query, title or text (02 D19)");
            }
        }
    }

    /// <summary>Every public <c>*Code</c> constant of <see cref="SourceErrors"/> and its catalogue twin in <see cref="ErrorCodes"/>.</summary>
    public static IEnumerable<string> SourceErrorCodes() =>
        typeof(SourceErrors).GetFields()
            .Where(field => field is { IsLiteral: true, Name: var name } && name.EndsWith("Code", StringComparison.Ordinal) && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    /// <summary>Every <c>const string</c> of the error catalogue.</summary>
    public static IReadOnlySet<string> Catalogue() =>
        typeof(ErrorCodes).GetFields()
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// The case map of the suite: the 37 cases of the task file (34 + 23b + 23c + 23d) and the test member of each. The count
/// test reflects over it, so a deleted test or a deleted row shows up as a failure.
/// </summary>
internal static class SuiteCases
{
    /// <summary>The number of cases the task file demands.</summary>
    public const int Expected = 37;

    private static readonly (string Case, bool Ssrf, string Member)[] Rows =
    [
        ("1", true, "Ssrf_LoopbackAddress_Blocked_AndServerSeesNoRequest"),
        ("2", true, "Ssrf_PrivateAddresses_Blocked"),
        ("3", true, "Ssrf_MetadataAddress_Blocked"),
        ("4", true, "Ssrf_CgnatAndZeroAddress_Blocked"),
        ("5", true, "Ssrf_IpLiteralDecimalOctalHex_RejectedBeforeDns"),
        ("6", true, "Ssrf_Ipv4MappedAndCompatibleV6_Blocked"),
        ("7", true, "Ssrf_SixToFourAndTeredo_Blocked"),
        ("8", true, "Ssrf_UlaLinkLocalSiteLocalMulticastV6_Blocked"),
        ("9", true, "Ssrf_Nat64AndDocumentationV6_Blocked"),
        ("10", true, "Ssrf_DnsRebinding_PublicAnswerWhileServerOnLoopback_NoConnectionAndUnreachable"),
        ("11", true, "Url_IdnAndTrailingDot_NormalisedNotBlocked"),
        ("12", true, "Url_SingleLabelAndBareTld_Rejected"),
        ("13", true, "Url_PortUserInfoSchemeLength_Rejected"),
        ("14", true, "InternalApi_LeaseRouteAbsentOnPublicPort"),
        ("15", true, "Redirect_ToLoopback_Blocked"),
        ("16", true, "Redirect_ToMetadata_Blocked"),
        ("17", true, "Redirect_ToBlockedDomain_Blocked"),
        ("18", true, "Redirect_FourHops_Unreachable"),
        ("19", true, "Redirect_RelativeLocation_FollowedAndRevalidated"),
        ("20", true, "Redirect_WithCredentialsOrBadScheme_Rejected"),
        ("21", false, "Payload_GzipBomb_TooLarge"),
        ("22", false, "Payload_WrongOrDisguisedContentType_Unsupported"),
        ("23", false, "Payload_SpaAndShortText_NoText"),
        ("23b", false, "Payload_MinTextCharsBoundary"),
        ("23c", false, "Payload_DeepNestedHtml_DepthGuard_TooLarge_NoCrash"),
        ("23d", false, "Payload_ManyElements_ElementGuard_TooLarge"),
        ("24", false, "Payload_OverMaxChars_TooLarge"),
        ("25", false, "Robots_Disallow_BlockedByRobots"),
        ("26", false, "Robots_ServerErrorOrTimeout_BlockedByRobots"),
        ("27", false, "Robots_NotFound_AllowsAll"),
        ("28", false, "Robots_Cached24h_OneFetchPerOrigin"),
        ("29", false, "Cookies_SetByServer_NotReplayed"),
        ("30", false, "Timeout_SharedBudget_Unreachable"),
        ("31", false, "RateLimit_OnePerSecondPerDomain_NotAcrossDomains"),
        ("32", false, "Caps_ThirtyFirstUrlSource_429_AndEleventhSourceOnEpisode_409"),
        ("33", false, "Lease_ConcurrentCalls_PlatformAndDomainCapsHold"),
        ("34", false, "Lease_AbandonedTask_SweeperRequeuesThenFails"),
    ];

    private static readonly (bool Ssrf, string Member)[] Extras =
    [
        (true, "Ssrf_DnsResolverAnswerLoopback_ExactlyOneRequestReachesServer"),
        (true, "Connect_PeerAddressDeniedByScriptedValidator_Throws"),
        (false, "Leak_NoUrlTitleOrTextInLogsOrTraces"),
        (false, "ErrorCodes_AreInCatalogue"),
        (false, "Persistence_ExtractedTextObjectHasNoHtml_FailedCaseWritesNone_ApiLogsDoNotLeak"),
    ];

    /// <summary>The non-case members the task names (10 converse, defence in depth, the two cross-cutting tests).</summary>
    public static IEnumerable<(Type Owner, string Member)> ExtraMembers() =>
        Extras.Select(row => (row.Ssrf ? typeof(FetcherSsrfSuiteTests) : typeof(FetcherAbuseAndCapsTests), row.Member));

    /// <summary>The case id, the class that holds its test, and the member name.</summary>
    public static IEnumerable<(string Case, Type Owner, string Member)> All() =>
        Rows.Select(row => (row.Case, row.Ssrf ? typeof(FetcherSsrfSuiteTests) : typeof(FetcherAbuseAndCapsTests), row.Member));
}
