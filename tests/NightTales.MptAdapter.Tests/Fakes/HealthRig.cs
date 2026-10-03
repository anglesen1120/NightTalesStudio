using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.MptAdapter.Health;
using NightTales.RenderNode.Server;

namespace NightTales.MptAdapter.Tests.Fakes;

/// <summary>A clock the test moves by hand; samplers and caches read only this.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    internal void Advance(TimeSpan by) => _now += by;
}

/// <summary>Scripts <see cref="IProcessRunner"/> per file name and counts every call (Q1: health runs none).</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(string FileName, IReadOnlyList<string> Arguments)> _calls = [];

    internal const string MusicJson =
        "{\"ok\": true, \"catalog_revision\": 1, \"music_missing\": [], \"unlicensed\": [], \"catalog_errors\": 0}";

    /// <summary>What <c>check-fonts.sh</c> prints for the one registered subtitle font (font_glyph_check.py: the
    /// register hash line names the file, the glyph line is a count).</summary>
    internal const string FontOutput = "hash ok BeVietnamPro-Bold.ttf\nglyphs ok 64/64\n";

    internal Func<string, IReadOnlyList<string>, ProcessResult> Script { get; set; } = Default;

    internal IReadOnlyList<(string FileName, IReadOnlyList<string> Arguments)> Calls => _calls;

    internal int CallsTo(string fileNameEnd) => _calls.Count(c => c.FileName.EndsWith(fileNameEnd, StringComparison.Ordinal));

    internal TimeSpan Delay { get; set; }

    public async Task<ProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (_calls)
        {
            _calls.Add((fileName, arguments));
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
        }

        return Script(fileName, arguments);
    }

    private static ProcessResult Default(string fileName, IReadOnlyList<string> arguments)
    {
        if (fileName.EndsWith("nvidia-smi", StringComparison.Ordinal))
        {
            return new ProcessResult(0, "37, 1024, 24576, 55\n");
        }

        if (fileName.EndsWith("ffmpeg", StringComparison.Ordinal))
        {
            return new ProcessResult(0, " V....D libx264              H.264 / AVC\n V....D libx265              H.265\n");
        }

        return fileName.EndsWith("check-music.sh", StringComparison.Ordinal)
            ? new ProcessResult(0, MusicJson + "\n")
            : new ProcessResult(0, FontOutput);
    }
}

/// <summary>A proxy stand-in: answers a scripted status and body, counts requests, never follows anything.</summary>
internal sealed class StubProxyHandler : HttpMessageHandler
{
    private int _count;

    internal const string OkBody = "{\"status\":\"ok\",\"reasons\":[]}";

    internal string Body { get; set; } = OkBody;

    internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    internal bool Throw { get; set; }

    internal int Count => Volatile.Read(ref _count);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _count);
        if (Throw)
        {
            throw new HttpRequestException("proxy down");
        }

        return Task.FromResult(new HttpResponseMessage(Status)
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed class FakeDiskStats : IDiskStats
{
    internal DiskFigures Figures { get; set; } = new(100, 50);

    internal int Reads { get; private set; }

    public DiskFigures Read(string path)
    {
        Reads++;
        return Figures;
    }
}

/// <summary>Every health source with real samplers over fakes; the node health source reads only their caches.</summary>
internal sealed class HealthRig : IAsyncDisposable
{
    internal const string ConfigToml = "[whisper]\ndevice = \"cuda\"\ncompute_type = \"float16\"\n";

    private HealthRig()
    {
    }

    internal ManualTimeProvider Clock { get; private init; } = null!;

    internal FakeMptServer Mpt { get; private init; } = null!;

    internal FakeProcessRunner Runner { get; private init; } = null!;

    internal StubProxyHandler Proxy { get; private init; } = null!;

    internal FakeDiskStats Disk { get; private init; } = null!;

    internal AdapterOptions Options { get; private init; } = null!;

    internal string Root { get; private init; } = null!;

    internal MptSampler MptSampler { get; private init; } = null!;

    internal ProxySampler ProxySampler { get; private init; } = null!;

    internal DiskSampler DiskSampler { get; private init; } = null!;

    internal GpuSampler GpuSampler { get; private init; } = null!;

    internal AssetsSampler AssetsSampler { get; private init; } = null!;

    internal CodecProbe CodecProbe { get; private init; } = null!;

    internal ConfigProbe ConfigProbe { get; private init; } = null!;

    internal NodeHealthSource Source { get; private init; } = null!;

    internal IReadOnlyList<IHealthSampler> All { get; private init; } = null!;

    internal FakeProcessStartReader StartReader { get; private init; } = null!;

    internal static async Task<HealthRig> StartAsync(
        Action<AdapterOptions>? configure = null,
        IImageRelayHealthProvider? relay = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "nts-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var mpt = await FakeMptServer.StartAsync().ConfigureAwait(false);

        var options = new AdapterOptions
        {
            DataDir = Path.Combine(root, "adapter"),
            MptRoot = Path.Combine(root, "bundle", "mpt"),
            RelayDir = Path.Combine(root, "relay"),
            MptBaseUrl = mpt.BaseAddress.ToString(),
            MptConfigPath = Path.Combine(root, "config.toml"),
            VideoCodec = "libx264",
        };
        configure?.Invoke(options);
        Directory.CreateDirectory(options.DataDir);
        if (!File.Exists(options.MptConfigPath))
        {
            await File.WriteAllTextAsync(options.MptConfigPath, ConfigToml).ConfigureAwait(false);
        }

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var serverOptions = new RenderNodeServerOptions { ApiKey = "adapter-key", MaxConcurrentTasks = 2 };
        var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace));
        var store = new SqliteTaskStore(new SqliteStoreOptions(options.TasksDbPath), factory.CreateLogger<SqliteTaskStore>());
        await store.MigrateAsync(CancellationToken.None).ConfigureAwait(false);

        var startReader = new FakeProcessStartReader { Available = true, Value = clock.GetUtcNow().AddHours(-2) };
        var client = new MptClient(
            MptHttpClient.Create(options),
            wrapped,
            Microsoft.Extensions.Options.Options.Create(serverOptions),
            startReader,
            new DiskRuleEnforcer(
                new TaskDirCleaner(wrapped, factory.CreateLogger<TaskDirCleaner>()),
                factory.CreateLogger<DiskRuleEnforcer>()),
            new FileOutputSource(store, wrapped, factory.CreateLogger<FileOutputSource>()),
            factory.CreateLogger<MptClient>());

        var runner = new FakeProcessRunner();
        var proxy = new StubProxyHandler();
        var disk = new FakeDiskStats();
        var bundle = new BundleInfo(Path.Combine(root, "bundle"), "3d5f4e4");

        var mptSampler = new MptSampler(client, startReader, clock, factory.CreateLogger<MptSampler>());
        var proxySampler = new ProxySampler(new HttpClient(proxy), wrapped, clock, factory.CreateLogger<ProxySampler>());
        var diskSampler = new DiskSampler(disk, wrapped, clock, factory.CreateLogger<DiskSampler>());
        var gpuSampler = new GpuSampler(runner, wrapped, clock, factory.CreateLogger<GpuSampler>());
        var assets = new AssetsSampler(runner, bundle, wrapped, clock, factory.CreateLogger<AssetsSampler>());
        var codec = new CodecProbe(runner, wrapped, clock, factory.CreateLogger<CodecProbe>());
        var config = new ConfigProbe(wrapped, clock, factory.CreateLogger<ConfigProbe>());

        return new HealthRig
        {
            Clock = clock,
            Mpt = mpt,
            Runner = runner,
            Proxy = proxy,
            Disk = disk,
            Options = options,
            Root = root,
            StartReader = startReader,
            MptSampler = mptSampler,
            ProxySampler = proxySampler,
            DiskSampler = diskSampler,
            GpuSampler = gpuSampler,
            AssetsSampler = assets,
            CodecProbe = codec,
            ConfigProbe = config,
            All = [mptSampler, proxySampler, diskSampler, gpuSampler, assets, codec, config],
            Source = new NodeHealthSource(
                mptSampler,
                proxySampler,
                diskSampler,
                gpuSampler,
                assets,
                codec,
                config,
                relay ?? new DisabledImageRelayHealth(),
                store,
                wrapped,
                Microsoft.Extensions.Options.Options.Create(serverOptions),
                bundle,
                clock),
        };
    }

    internal async Task SampleAllAsync()
    {
        foreach (var sampler in All)
        {
            await sampler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Mpt.DisposeAsync().ConfigureAwait(false);
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
