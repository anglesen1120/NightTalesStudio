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
    /// <summary>The quiet period a burst of file events must reach before one refresh runs.</summary>
    internal TimeSpan ChangeDebounce { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A stream of events that never goes quiet still refreshes after this many quiet periods.</summary>
    private const int MaxDebounceRounds = 10;

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

        using var watchers = new WatcherSet(sampler.WatchedPaths);
        await SampleOnceAsync(sampler, gate, stoppingToken).ConfigureAwait(false);
        var changes = watchers.Enabled ? WatchChangesAsync(sampler, gate, watchers, stoppingToken) : Task.CompletedTask;

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
                // A directory that did not exist (or was replaced) since the last arming is picked up here at the latest.
                watchers.Arm();
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
                // Debounce: wait until a whole quiet period passes without a new event (bounded).
                for (var round = 0; round < MaxDebounceRounds; round++)
                {
                    await Task.Delay(ChangeDebounce, clock, stoppingToken).ConfigureAwait(false);
                    var more = false;
                    while (watchers.Changes.TryRead(out _))
                    {
                        more = true;
                    }

                    if (!more)
                    {
                        break;
                    }
                }

                // Re-arm before sampling: a directory created or replaced by this change gets its watcher, and
                // anything that lands between the arming and the sample is covered by that sample.
                watchers.Arm();
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
    /// <see cref="FileSystemWatcher"/>s over the watched paths, all feeding one coalescing channel. For each path the
    /// directory that holds it is watched together with that directory's parent (so a deleted and recreated
    /// directory is seen); when the directory does not exist yet, the nearest existing ancestor is watched instead,
    /// and <see cref="Arm"/> moves the watchers down as the missing levels appear. Events are filtered to the ones
    /// that concern a watched path, so a busy sibling does not cause refreshes.
    /// </summary>
    private sealed class WatcherSet : IDisposable
    {
        private readonly string[] _paths;
        private readonly object _gate = new();
        private readonly Channel<bool> _changes =
            Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private List<FileSystemWatcher> _watchers = [];
        private bool _disposed;

        public WatcherSet(IReadOnlyList<string> paths)
        {
            _paths = [.. paths.Select(Path.GetFullPath)];
            Arm();
        }

        /// <summary>True when the sampler names paths to watch at all.</summary>
        public bool Enabled => _paths.Length > 0;

        public ChannelReader<bool> Changes => _changes.Reader;

        /// <summary>Rebuilds every watcher from what exists now (stale ones, on a replaced directory, are dropped).</summary>
        public void Arm()
        {
            if (_paths.Length == 0)
            {
                return;
            }

            var fresh = new List<FileSystemWatcher>();
            foreach (var directory in DesiredDirectories())
            {
                if (TryWatch(directory) is { } watcher)
                {
                    fresh.Add(watcher);
                }
            }

            List<FileSystemWatcher> old;
            lock (_gate)
            {
                if (_disposed)
                {
                    old = fresh;
                }
                else
                {
                    old = _watchers;
                    _watchers = fresh;
                }
            }

            foreach (var watcher in old)
            {
                watcher.Dispose();
            }
        }

        public void Dispose()
        {
            List<FileSystemWatcher> old;
            lock (_gate)
            {
                _disposed = true;
                old = _watchers;
                _watchers = [];
            }

            foreach (var watcher in old)
            {
                watcher.Dispose();
            }

            _changes.Writer.TryComplete();
        }

        private HashSet<string> DesiredDirectories()
        {
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in _paths)
            {
                var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                while (directory is not null && !Directory.Exists(directory))
                {
                    directory = Path.GetDirectoryName(directory);
                }

                if (directory is null)
                {
                    continue;
                }

                directories.Add(directory);
                if (Path.GetDirectoryName(directory) is { } parent && Directory.Exists(parent)
                    && (Directory.Exists(path) || Path.GetDirectoryName(path) == directory))
                {
                    directories.Add(parent);
                }
            }

            return directories;
        }

        private FileSystemWatcher? TryWatch(string directory)
        {
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                        | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                watcher.Changed += OnEvent;
                watcher.Created += OnEvent;
                watcher.Deleted += OnEvent;
                watcher.Renamed += OnRenamed;
                watcher.Error += (_, _) => _changes.Writer.TryWrite(true);
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or PlatformNotSupportedException)
            {
                // The periodic tick still covers a directory that cannot be watched.
                return null;
            }
        }

        private void OnEvent(object? sender, FileSystemEventArgs args) => Notify(args.FullPath);

        private void OnRenamed(object? sender, RenamedEventArgs args)
        {
            Notify(args.OldFullPath);
            Notify(args.FullPath);
        }

        private void Notify(string fullPath)
        {
            if (Concerns(fullPath))
            {
                _changes.Writer.TryWrite(true);
            }
        }

        /// <summary>The event's path is a watched path, inside one (a directory), or one of its ancestors.</summary>
        private bool Concerns(string fullPath)
        {
            var separator = Path.DirectorySeparatorChar;
            foreach (var path in _paths)
            {
                if (path == fullPath
                    || path.StartsWith(fullPath + separator, StringComparison.Ordinal)
                    || fullPath.StartsWith(path + separator, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
