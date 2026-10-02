using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace NightTales.MptAdapter.Health;

/// <summary>
/// The only driver of health probes (D7, D19 Q1): every sampler runs once at start, then a periodic one repeats at
/// its own interval. A sampler that names <see cref="IHealthSampler.WatchedPaths"/> (the assets source: fonts,
/// music, catalog) is also re-run, debounced, when one of them changes (D7: "on change"). <c>GET /v1/health</c>
/// never reaches a probe, so its cost does not depend on how often the Worker polls it. A sampler turns its own
/// failures into a failed sample; anything else is logged by the host and the loop carries on.
/// </summary>
internal sealed class HealthSamplerService(IEnumerable<IHealthSampler> samplers, TimeProvider clock)
    : BackgroundService
{
    /// <summary>A burst of file events (a copy writes several) is one refresh after this quiet period.</summary>
    internal TimeSpan ChangeDebounce { get; init; } = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var all = samplers.ToList();
        await Task.WhenAll(all.Select(sampler => RunAsync(sampler, stoppingToken))).ConfigureAwait(false);
    }

    private async Task RunAsync(IHealthSampler sampler, CancellationToken stoppingToken)
    {
        // The tick and the watcher both go through this gate, so a change that lands while a refresh is running
        // queues one more refresh instead of being dropped, and the sampler never runs twice at once.
        using var gate = new SemaphoreSlim(1, 1);

        await SampleOnceAsync(sampler, gate, stoppingToken).ConfigureAwait(false);
        using var watchers = new WatcherSet(sampler.WatchedPaths);
        var changes = watchers.Count > 0 ? WatchChangesAsync(sampler, gate, watchers, stoppingToken) : Task.CompletedTask;

        if (sampler.Interval is not { } interval)
        {
            await changes.ConfigureAwait(false);
            return;
        }

        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SampleOnceAsync(sampler, gate, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }

        await changes.ConfigureAwait(false);
    }

    private async Task WatchChangesAsync(
        IHealthSampler sampler, SemaphoreSlim gate, WatcherSet watchers, CancellationToken stoppingToken)
    {
        try
        {
            while (await watchers.Changes.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(ChangeDebounce, clock, stoppingToken).ConfigureAwait(false);
                while (watchers.Changes.TryRead(out _))
                {
                    // Events that arrived during the quiet period belong to this refresh.
                }

                await SampleOnceAsync(sampler, gate, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    private static async Task SampleOnceAsync(IHealthSampler sampler, SemaphoreSlim gate, CancellationToken stoppingToken)
    {
        try
        {
            await gate.WaitAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await sampler.SampleAsync(stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception)
        {
            // A sampler that throws past its own guard leaves its cache untouched: it goes stale after
            // 3 x its interval and the node reports the source's reason, so the failure is never silent.
        }
    }

    /// <summary>
    /// One <see cref="FileSystemWatcher"/> per existing directory of the watched paths (a file is watched through
    /// its directory), all feeding one coalescing channel. A directory that does not exist yet is skipped: the
    /// periodic tick still covers it.
    /// </summary>
    private sealed class WatcherSet : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers = [];
        private readonly Channel<bool> _changes =
            Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        public WatcherSet(IReadOnlyList<string> paths)
        {
            foreach (var directory in paths.Select(DirectoryOf).Where(Directory.Exists).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var watcher = new FileSystemWatcher(directory)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        IncludeSubdirectories = false,
                    };
                    watcher.Changed += OnEvent;
                    watcher.Created += OnEvent;
                    watcher.Deleted += OnEvent;
                    watcher.Renamed += OnEvent;
                    watcher.Error += (_, _) => _changes.Writer.TryWrite(true);
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or ArgumentException or PlatformNotSupportedException)
                {
                    // The periodic tick still covers a directory that cannot be watched.
                }
            }
        }

        public int Count => _watchers.Count;

        public ChannelReader<bool> Changes => _changes.Reader;

        public void Dispose()
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _changes.Writer.TryComplete();
        }

        private static string DirectoryOf(string path) =>
            Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;

        private void OnEvent(object? sender, FileSystemEventArgs args) => _changes.Writer.TryWrite(true);
    }
}
