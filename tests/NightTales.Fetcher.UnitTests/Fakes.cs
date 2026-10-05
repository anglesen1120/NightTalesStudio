using System.Collections.Concurrent;
using ErrorOr;
using Microsoft.Extensions.Logging;
using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Fetching;
using NightTales.Fetcher.InternalApi;

namespace NightTales.Fetcher.UnitTests;

/// <summary>
/// A <see cref="TimeProvider"/> the tests move by hand. Timers whose due time satisfies <see cref="FireImmediately"/> fire
/// at once (the loop's poll and back-off delays), every other timer waits for <see cref="Advance"/>. Every one-shot due
/// time is recorded in <see cref="Delays"/>.
/// </summary>
internal sealed class FakeTime : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private readonly List<TimeSpan> _delays = [];
    private long _timestamp;

    /// <summary>Which one-shot delays complete without waiting.</summary>
    public Func<TimeSpan, bool> FireImmediately { get; set; } = static _ => false;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>The due times of every timer created so far.</summary>
    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_gate)
            {
                return [.. _delays];
            }
        }
    }

    /// <summary>The armed timers waiting for <see cref="Advance"/>.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(timer => timer.IsArmed);
            }
        }
    }

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        var oneShot = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero;
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_gate)
            {
                _delays.Add(dueTime);
            }
        }

        if (dueTime != Timeout.InfiniteTimeSpan && oneShot && FireImmediately(dueTime))
        {
            _ = Task.Run(() => callback(state));
            return timer;
        }

        timer.Change(dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Moves the clock and fires every timer that came due.</summary>
    /// <param name="by">How far to move.</param>
    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due;
        long now;
        lock (_gate)
        {
            _timestamp += by.Ticks;
            now = _timestamp;
            due = [.. _timers.Where(timer => timer.IsDue(now)).OrderBy(timer => timer.DueAt)];
        }

        foreach (var timer in due)
        {
            timer.Fire(now);
        }
    }

    private sealed class FakeTimer(FakeTime owner, TimerCallback callback, object? state) : ITimer
    {
        private long _dueAt = -1;
        private long _period;

        public bool IsArmed => Volatile.Read(ref _dueAt) >= 0;

        public long DueAt => Volatile.Read(ref _dueAt);

        public bool IsDue(long now) => IsArmed && DueAt <= now;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? -1 : owner._timestamp + dueTime.Ticks;
                _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            }

            return true;
        }

        public void Fire(long now)
        {
            lock (owner._gate)
            {
                if (!IsDue(now))
                {
                    return;
                }

                _dueAt = _period > 0 ? now + _period : -1;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _dueAt = -1;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A scripted <see cref="IFetcherApi"/> that records every call.</summary>
internal sealed class FakeApi : IFetcherApi
{
    private readonly ConcurrentQueue<int> _leaseMax = new();
    private readonly ConcurrentQueue<(Guid TaskId, FetchResultBody Body)> _posts = new();
    private int _leaseCalls;
    private int _heartbeats;

    /// <summary>Answers a lease call; the argument is the 1-based call number.</summary>
    public Func<int, ErrorOr<IReadOnlyList<LeasedTask>>> OnLease { get; set; } = static _ => Array.Empty<LeasedTask>();

    /// <summary>Answers a heartbeat.</summary>
    public Func<Guid, ErrorOr<Success>> OnHeartbeat { get; set; } = static _ => Result.Success;

    /// <summary>Answers a result POST; may throw.</summary>
    public Func<Guid, FetchResultBody, ErrorOr<Success>> OnPost { get; set; } = static (_, _) => Result.Success;

    /// <summary>The body a successful POST reports as actually posted (the real client substitutes an over-cap done body); null echoes the sent one.</summary>
    public Func<FetchResultBody, FetchResultBody>? PostedAs { get; set; }

    public int LeaseCalls => Volatile.Read(ref _leaseCalls);

    public int Heartbeats => Volatile.Read(ref _heartbeats);

    public IReadOnlyList<int> LeaseMax => [.. _leaseMax];

    public IReadOnlyList<(Guid TaskId, FetchResultBody Body)> Posts => [.. _posts];

    public ValueTask<ErrorOr<IReadOnlyList<LeasedTask>>> LeaseAsync(string workerId, int max, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _leaseMax.Enqueue(max);
        var call = Interlocked.Increment(ref _leaseCalls);

        return ValueTask.FromResult(OnLease(call));
    }

    public ValueTask<ErrorOr<Success>> HeartbeatAsync(Guid taskId, string workerId, CancellationToken ct)
    {
        Interlocked.Increment(ref _heartbeats);

        return ValueTask.FromResult(OnHeartbeat(taskId));
    }

    public ValueTask<ErrorOr<FetchResultBody>> PostResultAsync(Guid taskId, FetchResultBody body, CancellationToken ct)
    {
        _posts.Enqueue((taskId, body));
        var answer = OnPost(taskId, body);
        if (answer.IsError)
        {
            return ValueTask.FromResult<ErrorOr<FetchResultBody>>(answer.Errors);
        }

        return ValueTask.FromResult<ErrorOr<FetchResultBody>>(PostedAs?.Invoke(body) ?? body);
    }
}

/// <summary>A scripted <see cref="ISourceFetcher"/>.</summary>
internal sealed class FakeSourceFetcher(Func<SourceFetchRequest, CancellationToken, ValueTask<ErrorOr<FetchedSource>>> fetch) : ISourceFetcher
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public ValueTask<ErrorOr<FetchedSource>> FetchAsync(SourceFetchRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);

        return fetch(request, ct);
    }

    public static FetchedSource Page(string url = "https://news.example.com/a") =>
        new(url, "Title", "text/html", new string('a', 64), "Một đoạn văn bản.", 17, 200);
}

/// <summary>Records the exit requests of the loop.</summary>
internal sealed class FakeExit : IFetcherExit
{
    private readonly ConcurrentQueue<(int Code, string Reason)> _calls = new();

    public IReadOnlyList<(int Code, string Reason)> Calls => [.. _calls];

    public void RequestExit(int exitCode, string reason) => _calls.Enqueue((exitCode, reason));
}

/// <summary>The registrable domain is the last two labels; enough for the <c>*.example.com</c> URLs of the tests.</summary>
internal sealed class FakePublicSuffixList : IPublicSuffixList
{
    public string? GetRegistrableDomain(string host)
    {
        var labels = host.Split('.');

        return labels.Length < 2 ? null : string.Join('.', labels[^2..]);
    }
}

/// <summary>Keeps every formatted message so a test can search them for a secret.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => [.. _messages];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _messages.Enqueue(formatter(state, exception));
        if (exception is not null)
        {
            _messages.Enqueue(exception.ToString());
        }
    }
}

internal static class Wait
{
    /// <summary>Polls <paramref name="condition"/> on the real clock until it holds; fails after ten seconds.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for: {what}");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Lets pending work run, to assert that something did <em>not</em> happen.</summary>
    public static Task SettleAsync() => Task.Delay(150);
}
