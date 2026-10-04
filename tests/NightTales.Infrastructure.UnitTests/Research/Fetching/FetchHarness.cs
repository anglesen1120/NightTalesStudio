using System.Net;
using ErrorOr;
using NightTales.Application.Abstractions.Fetching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Research;
using NightTales.Infrastructure.Research.Extraction;
using NightTales.Infrastructure.Research.Http;
using NightTales.TestKit.Fetching;
using NightTales.TestKit.Research;

namespace NightTales.Infrastructure.UnitTests.Research.Fetching;

/// <summary>Collects every log line (level, category, formatted message) so a test can grep what was logged (D19).</summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(ListLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._lines)
            {
                owner._lines.Add($"{logLevel} {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " | " + exception)}");
            }
        }
    }
}

/// <summary>A scripted <see cref="IArticleExtractor"/>.</summary>
internal sealed class StubExtractor(Func<string, ArticleExtraction> extract) : IArticleExtractor
{
    public int Calls { get; private set; }

    public ArticleExtraction Extract(string html, Uri url, string? contentType, CancellationToken ct)
    {
        Calls++;

        return extract(html);
    }
}

/// <summary>
/// The whole fetch stack over a real loopback socket (02 D20): <see cref="HttpTestServer"/>, <see cref="FakeDnsResolver"/>
/// (every name resolves to <c>127.0.0.1</c> unless mapped), the loopback-allowing validator and the real
/// <see cref="SafeHttpClientFactory"/> client, so redirects, robots, headers and byte caps run the production code.
/// </summary>
internal sealed class FetchHarness : IAsyncDisposable
{
    public const string Page = "http://www.example.com/a/secret-path?q=1";

    private FetchHarness(HttpTestServer server, FakeDnsResolver dns, FetcherOptions options, ListLoggerProvider logs, HttpClient http, IArticleExtractor extractor, IRobotsPolicy robots)
    {
        Server = server;
        Dns = dns;
        Options = options;
        Logs = logs;
        Http = http;
        Limiter = new FakeHostRateLimiter();
        var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var psl = new FakePublicSuffixList();
        Fetcher = new SafeHttpSourceFetcher(
            http,
            extractor,
            robots,
            Limiter,
            new DomainBlocklist(options.BlockedDomains.ToHashSet(StringComparer.Ordinal)),
            psl,
            Options2(options),
            factory.CreateLogger<SafeHttpSourceFetcher>());
    }

    public HttpTestServer Server { get; }

    public FakeDnsResolver Dns { get; }

    public FetcherOptions Options { get; }

    public ListLoggerProvider Logs { get; }

    public HttpClient Http { get; }

    public FakeHostRateLimiter Limiter { get; }

    public SafeHttpSourceFetcher Fetcher { get; }

    /// <summary>The answer for <c>/robots.txt</c>; a 404 (allow all) by default.</summary>
    public Func<TestRequest, TestResponse> Robots { get; set; } = _ => new TestResponse(404);

    public static IOptions<FetcherOptions> Options2(FetcherOptions options) => Microsoft.Extensions.Options.Options.Create(options);

    public static FetcherOptions DefaultOptions() => new()
    {
        BlockedDomains = ["youtube.com"],
        MinTextChars = 20,
        TotalTimeoutSeconds = 10,
        ExtractTimeoutSeconds = 5,
    };

    /// <summary>
    /// Builds the stack. <paramref name="script"/> answers every request except <c>/robots.txt</c>, which
    /// <see cref="Robots"/> answers. <paramref name="validator"/> defaults to the loopback-allowing test validator;
    /// <paramref name="extractor"/> to the real SmartReader adapter.
    /// </summary>
    public static FetchHarness Create(
        Func<TestRequest, TestResponse> script,
        Action<FetcherOptions>? configure = null,
        IAddressValidator? validator = null,
        IArticleExtractor? extractor = null,
        bool realRobots = true)
    {
        var options = DefaultOptions();
        configure?.Invoke(options);

        var dns = new FakeDnsResolver { DefaultAnswer = [IPAddress.Loopback] };
        var logs = new ListLoggerProvider();
        HarnessBox box = new();
        var server = new HttpTestServer(request => request.Target.StartsWith("/robots.txt", StringComparison.Ordinal)
            ? box.Harness!.Robots(request)
            : script(request));
        var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var connector = new SafeHttpConnector(
            dns,
            validator ?? new LoopbackAllowingAddressValidator(),
            factory.CreateLogger<SafeHttpConnector>(),
            server.EndpointFor(HttpTestServer.ClosedPort()));
        var http = SafeHttpClientFactory.Create(connector);
        var psl = new FakePublicSuffixList();
        IRobotsPolicy robots = realRobots
            ? new RobotsTxtPolicy(
                http,
                psl,
                new FakeHostRateLimiter(),
                new DomainBlocklist(options.BlockedDomains.ToHashSet(StringComparer.Ordinal)),
                Options2(options),
                TimeProvider.System,
                factory.CreateLogger<RobotsTxtPolicy>())
            : new FakeRobotsPolicy();

        var harness = new FetchHarness(
            server,
            dns,
            options,
            logs,
            http,
            extractor ?? new SmartReaderArticleExtractor(Options2(options)),
            robots);
        box.Harness = harness;

        return harness;
    }

    public ValueTask<ErrorOr<FetchedSource>> FetchAsync(
        string url = Page,
        string? acceptLanguage = null,
        CancellationToken ct = default)
    {
        var uri = new Uri(url);

        return Fetcher.FetchAsync(
            new SourceFetchRequest(uri, RegistrableOf(uri.Host), 1, acceptLanguage),
            ct);
    }

    public static string RegistrableOf(string host) => new FakePublicSuffixList().GetRegistrableDomain(host) ?? host;

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Server.DisposeAsync();
    }

    private sealed class HarnessBox
    {
        public FetchHarness? Harness { get; set; }
    }
}
