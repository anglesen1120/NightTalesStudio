using System.Collections.Concurrent;
using NightTales.Domain.Research;

namespace NightTales.Fetcher.Core.Research.Http;

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

        // A request that is already cancelled never creates an entry.
        ct.ThrowIfCancellationRequested();

        SweepIfDue();

        Entry entry;
        while (true)
        {
            entry = _entries.GetOrAdd(registrableDomain, static _ => new Entry());
            if (entry.TryBorrow())
            {
                break;
            }

            // The entry was retired by a sweep between the lookup and the borrow: unmap it (the sweep may not have yet) and retry.
            _entries.TryRemove(new KeyValuePair<string, Entry>(registrableDomain, entry));
        }

        try
        {
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
        finally
        {
            // Whatever the outcome (including a cancelled first acquire), the borrow ends here, so the entry can be swept.
            entry.Return();
        }
    }

    /// <summary>
    /// Drops every domain idle for <see cref="IdleTtl"/> (and every domain that was never used, such as one whose first
    /// wait was cancelled); when the map is still at <see cref="MaxKeys"/>, also the least recently used idle one. An
    /// entry is retired only while nobody holds or waits for it, and a retired entry can no longer be borrowed, so a
    /// request never runs on an entry that is no longer in the map.
    /// </summary>
    public void Sweep()
    {
        _lastSweep = time.GetTimestamp();

        foreach (var (key, entry) in _entries)
        {
            if (entry.TryRetire(time, IdleTtl))
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }

        while (_entries.Count >= MaxKeys)
        {
            string? oldestKey = null;
            Entry? oldestEntry = null;
            var oldest = long.MaxValue;
            foreach (var (key, entry) in _entries)
            {
                if (entry.TryGetIdleSince(out var idleSince) && idleSince < oldest)
                {
                    oldest = idleSince;
                    oldestKey = key;
                    oldestEntry = entry;
                }
            }

            if (oldestKey is null || oldestEntry is null)
            {
                return;
            }

            // The candidate may have been borrowed since the scan; then it is skipped and the next scan excludes it.
            if (oldestEntry.TryRetire(time, TimeSpan.Zero))
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(oldestKey, oldestEntry));
            }
        }
    }

    private void SweepIfDue()
    {
        if (_entries.Count >= MaxKeys || time.GetElapsedTime(_lastSweep) >= SweepEvery)
        {
            Sweep();
        }
    }

    /// <summary>
    /// One domain. <c>_users</c> counts the requests that hold or wait for <see cref="Gate"/>; <c>_retired</c> is set
    /// once, only while <c>_users</c> is zero, both under <c>_sync</c>, so retiring and borrowing cannot interleave.
    /// <see cref="HasLast"/> and <see cref="LastTimestamp"/> are written under <see cref="Gate"/> by a borrower and read
    /// under <c>_sync</c> only while nobody is borrowing.
    /// </summary>
    private sealed class Entry
    {
        private readonly object _sync = new();
        private int _users;
        private bool _retired;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool HasLast { get; set; }

        public long LastTimestamp { get; set; }

        /// <summary>Registers a user; <see langword="false"/> when the entry is already retired.</summary>
        public bool TryBorrow()
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return false;
                }

                _users++;

                return true;
            }
        }

        public void Return()
        {
            lock (_sync)
            {
                _users--;
            }
        }

        /// <summary>
        /// Retires the entry when nobody uses it and it was last used at least <paramref name="minIdle"/> ago (or never).
        /// </summary>
        public bool TryRetire(TimeProvider clock, TimeSpan minIdle)
        {
            lock (_sync)
            {
                if (_retired || _users > 0 || (HasLast && clock.GetElapsedTime(LastTimestamp) < minIdle))
                {
                    return false;
                }

                _retired = true;

                return true;
            }
        }

        /// <summary>Whether nobody uses the entry, and when it was last used (<see cref="long.MinValue"/> if never).</summary>
        public bool TryGetIdleSince(out long idleSince)
        {
            lock (_sync)
            {
                idleSince = HasLast ? LastTimestamp : long.MinValue;

                return !_retired && _users == 0;
            }
        }
    }
}
