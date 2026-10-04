using System.Collections.Concurrent;
using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Research.Http;

/// <summary>
/// The in-process <see cref="IHostRateLimiter"/> (02 D11, 02.5.2-T1 §rate limiter): per registrable domain, one
/// <see cref="SemaphoreSlim"/> and the timestamp of the last request. A request waits until
/// <c>lastRequestAt + minInterval</c>, then records <c>now</c>. The map is bounded to <see cref="MaxKeys"/> domains and
/// swept of domains idle for <see cref="IdleTtl"/>.
/// </summary>
/// <remarks>
/// [DECISION] One instance per process: the server-side one-lease-per-domain rule (02.5.2-T2) is the cross-process
/// guarantee, so this limiter only has to be correct within one fetcher.
/// </remarks>
/// <param name="time">The clock; <see cref="TimeProvider.System"/> in production.</param>
public sealed class HostRateLimiter(TimeProvider time) : IHostRateLimiter
{
    /// <summary>The most domains tracked at once.</summary>
    public const int MaxKeys = 10_000;

    /// <summary>A domain with no request for this long is dropped.</summary>
    public static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _lastSweep = time.GetTimestamp();

    /// <summary>The number of domains currently tracked (for tests and metrics).</summary>
    public int TrackedHosts => _entries.Count;

    /// <inheritdoc />
    public async ValueTask WaitAsync(string registrableDomain, TimeSpan minInterval, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(registrableDomain);

        SweepIfDue();

        var entry = _entries.GetOrAdd(registrableDomain, static _ => new Entry());
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.HasLast)
            {
                var remaining = minInterval - time.GetElapsedTime(entry.LastTimestamp);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, time, ct).ConfigureAwait(false);
                }
            }

            entry.LastTimestamp = time.GetTimestamp();
            entry.HasLast = true;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// Drops every domain idle for <see cref="IdleTtl"/>; when the map is still at <see cref="MaxKeys"/>, also the least
    /// recently used idle one. A domain with a waiter is never dropped.
    /// </summary>
    public void Sweep()
    {
        _lastSweep = time.GetTimestamp();

        foreach (var (key, entry) in _entries)
        {
            if (IsFree(entry) && time.GetElapsedTime(entry.LastTimestamp) >= IdleTtl)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }

        while (_entries.Count >= MaxKeys)
        {
            string? oldestKey = null;
            var oldest = long.MaxValue;
            foreach (var (key, entry) in _entries)
            {
                if (IsFree(entry) && entry.LastTimestamp < oldest)
                {
                    oldest = entry.LastTimestamp;
                    oldestKey = key;
                }
            }

            if (oldestKey is null || !_entries.TryRemove(oldestKey, out _))
            {
                return;
            }
        }
    }

    private static bool IsFree(Entry entry) => entry.Gate.CurrentCount == 1 && entry.HasLast;

    private void SweepIfDue()
    {
        if (_entries.Count >= MaxKeys || time.GetElapsedTime(_lastSweep) >= SweepEvery)
        {
            Sweep();
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool HasLast { get; set; }

        public long LastTimestamp { get; set; }
    }
}
