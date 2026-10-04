using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Storage;
using NightTales.Domain.Research;
using NightTales.Fetcher;
using NightTales.Fetcher.Core.Research.Http;
using NightTales.Fetcher.InternalApi;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Fetching;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Time;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>One result POST the fetcher's API client made, as the real internal API answered it.</summary>
/// <param name="TaskId">The task in the route.</param>
/// <param name="Status">The HTTP status.</param>
/// <param name="Code">The problem <c>code</c> of an error answer, empty otherwise.</param>
internal sealed record ResultPostRecord(Guid TaskId, int Status, string Code);

/// <summary>
/// Sits in front of the real in-process internal API: records every result POST and its answer, and (when
/// <see cref="FailResults"/> is set) makes result POSTs fail in transport, the way an Api that is down would.
/// </summary>
internal sealed class ApiTrafficRecorder : DelegatingHandler
{
    private readonly ConcurrentQueue<ResultPostRecord> _results = new();
    private int _simulatedFailures;

    /// <summary>When true every result POST throws <see cref="HttpRequestException"/> before it reaches the Api.</summary>
    public volatile bool FailResults;

    /// <summary>The result POSTs the Api answered, in order.</summary>
    public IReadOnlyList<ResultPostRecord> Results => [.. _results];

    /// <summary>How many result POSTs were failed on purpose.</summary>
    public int SimulatedFailures => Volatile.Read(ref _simulatedFailures);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var isResult = segments.Length >= 2 && segments[^1] == "result" && Guid.TryParse(segments[^2], out _);
        if (isResult && FailResults)
        {
            Interlocked.Increment(ref _simulatedFailures);

            throw new HttpRequestException("simulated api outage");
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (isResult)
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            _results.Enqueue(new ResultPostRecord(Guid.Parse(segments[^2]), (int)response.StatusCode, CodeOf(text)));
        }

        return response;
    }

    private static string CodeOf(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                    ? code.GetString()!
                    : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}

/// <summary>Collects every log line (level, category, formatted message and exception) so a test can grep what was logged (D19).</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>The lines, in arrival order.</summary>
    public IReadOnlyList<string> Lines => [.. _lines];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(_lines, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<string> lines, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " | " + exception)}");
    }
}

/// <summary>
/// The composition of the fetcher end-to-end tests (02.5.2-T3): the real internal API (02.5.2-T2) behind
/// <see cref="ApiFactory.WithFetcherInternalApi"/>, the real <c>FetcherLoop</c> + <c>FetcherApiClient</c> +
/// <c>SafeHttpSourceFetcher</c> hosted in-process through <c>AddFetcherHost</c>, and a loopback page server. Only the test
/// seams differ from production (02 D20): the API client's primary handler is the factory's TestServer handler on the
/// internal port, DNS is a <see cref="FakeDnsResolver"/> answering every name with loopback, the address validator is the
/// <see cref="LoopbackAllowingAddressValidator"/>, the connector dials the page server's port, and the host rate limiter
/// never waits.
/// </summary>
internal sealed class FetcherE2EHarness : IAsyncDisposable
{
    /// <summary>The worker id the fetcher host runs with.</summary>
    public const string WorkerId = "e2e-fetcher";

    private readonly Action<IServiceCollection>? _configureFetcher;
    private IHost? _host;

    public FetcherE2EHarness(
        TestDatabase database,
        Func<TestRequest, TestResponse> pages,
        Action<IServiceCollection>? configureFetcher = null)
    {
        _configureFetcher = configureFetcher;
        Seed = new FetchTaskSeed(database);
        Seed.ResetTasks();
        Blobs = new InMemoryBlobStore();
        Api = new ApiFactory(database, new FakeClock(DateTimeOffset.UtcNow))
            .WithFetcherInternalApi()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IBlobStore>();
                services.AddSingleton<IBlobStore>(Blobs);
            });
        Pages = new HttpTestServer(request => request.Target.StartsWith("/robots.txt", StringComparison.Ordinal)
            ? TestResponse.Text(200, "text/plain", "User-agent: *\nAllow: /\n")
            : pages(request));
        Dns = new FakeDnsResolver { DefaultAnswer = [IPAddress.Loopback] };
    }

    public FetchTaskSeed Seed { get; }

    public InMemoryBlobStore Blobs { get; }

    public ApiFactory Api { get; }

    public HttpTestServer Pages { get; }

    public FakeDnsResolver Dns { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    public ApiTrafficRecorder Traffic { get; } = new();

    /// <summary>Queues a <c>fetch_source</c> task whose source URL is <c>http://{domain}{path}</c> (port 80, so the loopback seam applies).</summary>
    public FetchTaskSeed.SeededTask QueueHttpTask(FetchTaskSeed.World world, string domain, string path)
    {
        var task = Seed.AddQueued(world, domain, path: path);
        Seed.Execute(
            $"UPDATE story_sources SET url = 'http://' || substr(url, 9), normalized_url = 'http://' || substr(normalized_url, 9) WHERE id = '{task.SourceId}'");
        Seed.Execute($"UPDATE fetch_tasks SET url = 'http://' || substr(url, 9) WHERE id = '{task.TaskId}'");

        return task with { Url = "http://" + task.Url[8..] };
    }

    /// <summary>Starts the fetcher host (its loop begins leasing at once).</summary>
    public async Task StartFetcherAsync(CancellationToken ct)
    {
        var apiBase = Api.Server.BaseAddress.ToString();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fetcher:BlockedDomains:0"] = "youtube.com",
            ["Fetcher:BlockedDomains:1"] = "facebook.com",
            ["Fetcher:IdlePollMilliseconds"] = "25",
            ["Fetcher:BusyPollMilliseconds"] = "25",
            ["Fetcher:WorkerId"] = WorkerId,
            ["Fetcher:Internal:BaseUrl"] = apiBase,
            [FetcherApiOptions.TokenVariable] = ApiFactory.FetcherToken,
        });
        builder.Services.AddLogging();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(Logs);

        // Test seams first: AddFetcherHost registers with TryAdd, so these win (02 D20; production never registers them).
        var validator = new LoopbackAllowingAddressValidator();
        var endpointFor = Pages.EndpointFor(HttpTestServer.ClosedPort());
        builder.Services.AddSingleton<IDnsResolver>(Dns);
        builder.Services.AddSingleton<IAddressValidator>(validator);
        builder.Services.AddSingleton<IHostRateLimiter>(new FakeHostRateLimiter());
        builder.Services.AddSingleton(provider =>
            new SafeHttpConnector(Dns, validator, provider.GetRequiredService<ILogger<SafeHttpConnector>>(), endpointFor));
        _configureFetcher?.Invoke(builder.Services);

        builder.Services.AddFetcherHost(builder.Configuration);

        // The API client talks to the real in-process internal API (port 8081 of the TestServer) through the recorder.
        builder.Services.AddHttpClient<IFetcherApi, FetcherApiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => Api.Server.CreateHandler(context => context.Connection.LocalPort = ApiFactory.FetcherInternalPort))
            .AddHttpMessageHandler(() => Traffic);

        _host = builder.Build();
        await _host.StartAsync(ct);
    }

    /// <summary>Stops the fetcher host, cancelling its in-flight work; safe to call twice.</summary>
    public async Task StopFetcherAsync()
    {
        if (_host is { } host)
        {
            _host = null;
            try
            {
                await host.StopAsync(CancellationToken.None);
            }
            finally
            {
                host.Dispose();
            }
        }
    }

    /// <summary>Polls <paramref name="condition"/> every 25 ms until it holds or <paramref name="timeout"/> passes.</summary>
    public static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(25, ct);
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopFetcherAsync();
        await Pages.DisposeAsync();
        Api.Dispose();
    }
}
