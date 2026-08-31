namespace Zashboard.App.Logic.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
    private long _timestamp;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ManualTimer timer = new(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.ChangeCore(dueTime, period);
        }

        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_gate)
        {
            _timestamp = checked(_timestamp + amount.Ticks);
            _utcNow += amount;
            foreach (ManualTimer timer in _timers)
            {
                timer.CollectCallbacks(_timestamp, callbacks);
            }
        }

        foreach ((TimerCallback callback, object? state) in callbacks)
        {
            callback(state);
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private long _nextDueTimestamp = long.MaxValue;
        private long _periodTicks = Timeout.Infinite;
        private bool _isDisposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_isDisposed)
                {
                    return false;
                }

                ChangeCore(dueTime, period);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _isDisposed = true;
                _nextDueTimestamp = long.MaxValue;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void ChangeCore(TimeSpan dueTime, TimeSpan period)
        {
            ValidateTimeout(dueTime, nameof(dueTime));
            ValidateTimeout(period, nameof(period));
            _periodTicks = period == Timeout.InfiniteTimeSpan
                ? Timeout.Infinite
                : period.Ticks;
            _nextDueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                ? long.MaxValue
                : checked(owner._timestamp + dueTime.Ticks);
        }

        public void CollectCallbacks(
            long timestamp,
            List<(TimerCallback Callback, object? State)> callbacks)
        {
            while (!_isDisposed && _nextDueTimestamp <= timestamp)
            {
                callbacks.Add((callback, state));
                if (_periodTicks == Timeout.Infinite)
                {
                    _nextDueTimestamp = long.MaxValue;
                }
                else
                {
                    _nextDueTimestamp = checked(_nextDueTimestamp + _periodTicks);
                }
            }
        }

        private static void ValidateTimeout(TimeSpan value, string parameterName)
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }
}
