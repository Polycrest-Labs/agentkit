namespace AgentKit.Tests;

/// <summary>A hand-cranked <see cref="TimeProvider"/> for deterministic heartbeat tests:
/// <see cref="Advance"/> moves the clock and fires every due timer synchronously.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => GetUtcNow().Ticks;

    /// <summary>Timers currently scheduled to fire — poll this before advancing so a racing
    /// producer has actually parked on its delay.</summary>
    public int ActiveTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.IsScheduled);
            }
        }
    }

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

    public void Advance(TimeSpan by)
    {
        ManualTimer[] snapshot;
        lock (_gate)
        {
            _now += by;
            snapshot = [.. _timers];
        }
        foreach (var timer in snapshot)
        {
            timer.FireIfDue(GetUtcNow());
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private readonly object _gate = new();
        private DateTimeOffset? _due;
        private TimeSpan _period;

        public bool IsScheduled
        {
            get
            {
                lock (_gate)
                {
                    return _due is not null;
                }
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
                _period = period;
            }
            return true;
        }

        public void FireIfDue(DateTimeOffset now)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_due is not { } due || due > now)
                    {
                        return;
                    }
                    _due = _period > TimeSpan.Zero ? due + _period : null;
                }
                callback(state);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _due = null;
            }
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
