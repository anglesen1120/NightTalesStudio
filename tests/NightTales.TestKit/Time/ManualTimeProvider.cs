namespace NightTales.TestKit.Time;

/// <summary>
/// A <see cref="TimeProvider"/> the test moves by hand: <see cref="Advance"/> fires the timers that fall due, so
/// <c>Task.Delay(delay, provider)</c> completes without anyone sleeping.
/// </summary>
/// <param name="start">The initial UTC time.</param>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;
    private long _timestamp;

    /// <summary>Creates a provider at <see cref="FakeClock.DefaultStart"/>.</summary>
    public ManualTimeProvider()
        : this(FakeClock.DefaultStart)
    {
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
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

    /// <summary>The number of timers that are armed.</summary>
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
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Moves time forward and fires every timer that falls due, in due order.</summary>
    /// <param name="by">The span to advance.</param>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            _timestamp += by.Ticks;
            due = [.. _timers.Where(timer => timer.IsDue(_timestamp)).OrderBy(timer => timer.DueAt)];
        }

        foreach (var timer in due)
        {
            timer.Fire(GetTimestamp());
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
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
