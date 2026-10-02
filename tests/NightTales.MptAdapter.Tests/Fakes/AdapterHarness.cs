using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;

namespace NightTales.MptAdapter.Tests.Fakes;

/// <summary>
/// The adapter's own wiring in one object: the durable store over a throwaway directory, the real
/// <c>MptClient</c> over a real <c>HttpClient</c> (the production handler, D6), the poller, the real
/// <c>TaskDirCleaner</c>, the completed-task feed and — on request — the render-node core in a real Kestrel,
/// so the acceptances can be asserted on HTTP as well as on the store.
/// </summary>
internal sealed class AdapterHarness : IAsyncDisposable
{
    /// <summary>A 32-byte key: the server core's validator rejects anything shorter.</summary>
    internal const string AdapterApiKey = "0123456789abcdef0123456789abcdef";

    /// <summary>The ids the D18 status fixtures carry, so a polled row compares against them field for field.</summary>
    internal static readonly Guid FixtureTaskId = Guid.Parse("0192f3c1-8a4b-7c2d-9e10-3f5a6b7c8d90");

    internal static readonly Guid FixtureRenderJobId = Guid.Parse("0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8e9f");

    private WebApplication? CoreHost { get; init; }

    private HttpClient? CoreHttp { get; init; }

    private AdapterHarness()
    {
    }

    internal string Root { get; private init; } = null!;

    internal AdapterOptions Config { get; private init; } = null!;

    internal SqliteTaskStore Store { get; private init; } = null!;

    internal MptClient Client { get; private init; } = null!;

    internal MptTaskPoller Poller { get; private init; } = null!;

    internal ChannelCompletedTaskFeed Feed { get; private init; } = null!;

    internal TaskDirCleaner Cleaner { get; private init; } = null!;

    internal FakeProcessStartReader FakeStartReader { get; private init; } = null!;

    internal FakeMptServer Mpt { get; private init; } = null!;

    internal ListLoggerProvider Logs { get; private init; } = new();

    internal ILoggerFactory LogFactory { get; private init; } = null!;

    internal FakeImagePlanSink PlanSink { get; private init; } = null!;

    internal static async Task<AdapterHarness> StartAsync(
        Action<AdapterOptions>? configure = null,
        bool withHost = false,
        bool realStartReader = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "nts-mpt-adapter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var mpt = await FakeMptServer.StartAsync().ConfigureAwait(false);

        var options = new AdapterOptions
        {
            DataDir = Path.Combine(root, "adapter"),
            MptRoot = Path.Combine(root, "mpt"),
            RelayDir = Path.Combine(root, "relay"),
            MptBaseUrl = mpt.BaseAddress.ToString(),
            PollSeconds = 1,
        };
        configure?.Invoke(options);

        var logs = new ListLoggerProvider();
        var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));

        var store = new SqliteTaskStore(
            new SqliteStoreOptions(options.TasksDbPath),
            factory.CreateLogger<SqliteTaskStore>());
        await store.MigrateAsync(CancellationToken.None).ConfigureAwait(false);

        var fakeStartReader = new FakeProcessStartReader();
        IMptProcessStartReader startReader = realStartReader
            ? new MptProcessStartReader(Options.Create(options), factory.CreateLogger<MptProcessStartReader>())
            : fakeStartReader;

        var cleaner = new TaskDirCleaner(Options.Create(options), factory.CreateLogger<TaskDirCleaner>());
        var serverOptions = new RenderNodeServerOptions { ApiKey = AdapterApiKey, MaxConcurrentTasks = 1 };
        var client = new MptClient(
            MptHttpClient.Create(options),
            Options.Create(options),
            Options.Create(serverOptions),
            startReader,
            cleaner,
            factory.CreateLogger<MptClient>(),
            new FileOutputSource(store, Options.Create(options), factory.CreateLogger<FileOutputSource>()));

        var feed = new ChannelCompletedTaskFeed();
        var poller = new MptTaskPoller(
            store,
            client,
            startReader,
            cleaner,
            feed,
            Options.Create(options),
            factory.CreateLogger<MptTaskPoller>());

        var sink = new FakeImagePlanSink();
        WebApplication? host = null;
        HttpClient? http = null;
        if (withHost)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.None);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddSingleton<ITaskStore>(store);
            builder.Services.AddSingleton<IRenderBackend>(client);
            builder.Services.AddSingleton<IImagePlanSink>(sink);
            builder.Services.AddSingleton<INodeHealthSource, FakeNodeHealthSource>();
            builder.Services.AddRenderNodeServer(builder.Configuration.GetSection(RenderNodeServerOptions.SectionName));

            // Last registration wins for IOptions<T>, so the harness's instance is what every endpoint reads.
            builder.Services.AddSingleton<IOptions<RenderNodeServerOptions>>(Options.Create(serverOptions));

            host = builder.Build();
            host.UseRenderNodeServer();
            host.MapRenderNodeV1();
            await host.StartAsync().ConfigureAwait(false);

            var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var address = addresses?.Addresses.FirstOrDefault()
                ?? throw new InvalidOperationException("The adapter core host did not bind an address.");
            http = new HttpClient { BaseAddress = new Uri(address) };
        }

        return new AdapterHarness
        {
            Root = root,
            Config = options,
            Store = store,
            Client = client,
            Poller = poller,
            Feed = feed,
            Cleaner = cleaner,
            FakeStartReader = fakeStartReader,
            Mpt = mpt,
            Logs = logs,
            LogFactory = factory,
            PlanSink = sink,
            CoreHost = host,
            CoreHttp = http,
        };
    }

    /// <summary>A row in the shape <c>POST /v1/renders</c> inserts, with only what a test wants to vary.</summary>
    internal static RenderTaskRecord NewRecord(
        Guid taskId,
        Guid renderJobId,
        string? backendTaskId = null,
        DateTimeOffset? backendStartedAt = null,
        string state = RenderStates.Queued,
        string stage = RenderStages.Queued,
        int percent = 0,
        bool cancelRequested = false,
        RenderOutcomeV1? outcome = null,
        RenderOutputV1? output = null,
        MptStateV1? snapshot = null) =>
        new(
            TaskId: taskId,
            RenderJobId: renderJobId,
            BodySha256: "sha256-of-the-submit-body",
            BackendTaskId: backendTaskId,
            SubmittedAt: DateTimeOffset.UtcNow.AddMinutes(-2),
            BackendStartedAt: backendStartedAt,
            State: state,
            Stage: stage,
            Percent: percent,
            CancelRequested: cancelRequested,
            Outcome: outcome,
            Output: output,
            Check: null,
            Images: null,
            FinishedAt: null,
            CleanedAt: null,
            MptLastSnapshot: snapshot);

    internal async Task SeedAsync(RenderTaskRecord record) =>
        (await Store.InsertAsync(record, CancellationToken.None).ConfigureAwait(false)).ShouldBeTrue();

    internal async Task<RenderTaskRecord> RowAsync(Guid taskId)
    {
        var record = await Store.TryGetAsync(taskId, CancellationToken.None).ConfigureAwait(false);
        record.ShouldNotBeNull();
        return record!;
    }

    /// <summary>One poll cycle, exactly as <c>ExecuteAsync</c> runs it.</summary>
    internal Task PollOnceAsync() => Poller.PollOnceAsync(CancellationToken.None);

    /// <summary>The status body the core serves, which is the fixture comparison's input.</summary>
    internal async Task<string> StatusJsonAsync(Guid taskId)
    {
        var response = await SendAsync(HttpMethod.Get, $"/v1/renders/{taskId}").ConfigureAwait(false);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>Reads up to <paramref name="count"/> tasks the poller handed over, giving up after the timeout.</summary>
    internal async Task<IReadOnlyList<RenderTaskRecord>> ReadPublishedAsync(int count, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var published = new List<RenderTaskRecord>();
        try
        {
            await foreach (var record in Feed.ReadAllAsync(cancellation.Token).ConfigureAwait(false))
            {
                published.Add(record);
                if (published.Count >= count)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The timeout is the assertion's "and nothing else arrived".
        }

        return published;
    }

    internal async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null,
        string? apiKey = AdapterApiKey)
    {
        var client = CoreHttp ?? throw new InvalidOperationException("This harness has no core host.");
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    internal Task<HttpResponseMessage> PostAsync(string path, string body) =>
        SendAsync(HttpMethod.Post, path, new StringContent(body, Encoding.UTF8, "application/json"));

    internal static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString() ?? string.Empty;
    }

    /// <summary>
    /// Writes the four paths a task owns on disk: MPT's task dir (with a produced file), the adapter's report
    /// dir, and the relay's two per-task files (plan §3.3).
    /// </summary>
    internal void WriteTaskFiles(Guid taskId, string mptTaskId)
    {
        var mptTaskDir = Path.Combine(Config.MptTasksDir, mptTaskId);
        Directory.CreateDirectory(mptTaskDir);
        File.WriteAllText(Path.Combine(mptTaskDir, "final-1.mp4"), "video");
        File.WriteAllText(Path.Combine(mptTaskDir, "final-1.norm.mp4"), "normalised");

        var reportDir = Path.Combine(Config.AdapterTasksDir, taskId.ToString("D"));
        Directory.CreateDirectory(reportDir);
        File.WriteAllText(Path.Combine(reportDir, "render-check.json"), "{}");

        Directory.CreateDirectory(Path.Combine(Config.RelayDir, "plans"));
        File.WriteAllText(Path.Combine(Config.RelayDir, $"{taskId:D}.jsonl"), "{}");
        File.WriteAllText(Path.Combine(Config.RelayDir, "plans", $"{taskId:D}.json"), "{}");
    }

    internal bool TaskFilesExist(Guid taskId, string mptTaskId) =>
        Directory.Exists(Path.Combine(Config.MptTasksDir, mptTaskId))
        || File.Exists(Path.Combine(Config.RelayDir, $"{taskId:D}.jsonl"))
        || File.Exists(Path.Combine(Config.RelayDir, "plans", $"{taskId:D}.json"))
        || Directory.Exists(Path.Combine(Config.AdapterTasksDir, taskId.ToString("D")));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (CoreHttp is not null)
        {
            CoreHttp.Dispose();
        }

        if (CoreHost is not null)
        {
            await CoreHost.DisposeAsync().ConfigureAwait(false);
        }

        Poller.Dispose();
        await Store.DisposeAsync().ConfigureAwait(false);
        await Mpt.DisposeAsync().ConfigureAwait(false);
        LogFactory.Dispose();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp dir is not a test failure.
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>The image-plan seam of the core, recording the two calls the lifecycle routes make.</summary>
internal sealed class FakeImagePlanSink : IImagePlanSink
{
    internal List<Guid> Registered { get; } = [];

    internal List<Guid> Released { get; } = [];

    /// <inheritdoc />
    public void Register(Guid taskId, ImagePlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Registered.Add(taskId);
    }

    /// <inheritdoc />
    public void Release(Guid taskId) => Released.Add(taskId);

    /// <inheritdoc />
    public ImageReportV1? BuildReport(Guid taskId) => null;
}

/// <summary>
/// The health seam: the frozen <c>ok</c> fixture, so the submit path sees a healthy node without any health
/// source being built here (that is 11.3.5-T4).
/// </summary>
internal sealed class FakeNodeHealthSource : INodeHealthSource
{
    private readonly RenderNodeHealthV1 _health = JsonSerializer.Deserialize<RenderNodeHealthV1>(
        FixtureFiles.Read("render-node-health.v1.ok.json"),
        RenderNodeJson.Tolerant)!;

    /// <inheritdoc />
    public ValueTask<RenderNodeHealthV1> GetAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(_health);
}
