// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Testing;

/// <summary>
/// A clock that moves only when <see cref="Advance"/> is called. Due timers fire in order, on the
/// caller's thread, outside the clock's lock.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly DateTimeOffset _origin;
    private long _ticks;
    private long _sequence;

    public ManualTimeProvider(DateTimeOffset origin) => _origin = origin;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public override DateTimeOffset GetUtcNow() => _origin + Elapsed;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward, firing every timer that falls due on the way.</summary>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        long target;
        lock (_gate)
        {
            target = _ticks + delta.Ticks;
        }

        while (true)
        {
            ManualTimer? due = null;
            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.Due <= target && (due is null || timer.Due < due.Due || (timer.Due == due.Due && timer.Order < due.Order)))
                    {
                        due = timer;
                    }
                }

                if (due is null)
                {
                    Interlocked.Exchange(ref _ticks, target);
                    return;
                }

                if (due.Due > _ticks)
                {
                    Interlocked.Exchange(ref _ticks, due.Due);
                }

                if (due.Period > 0)
                {
                    due.Due += due.Period;
                }
                else
                {
                    _timers.Remove(due);
                }
            }

            due.Fire();
        }
    }

    private bool Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            if (timer.Disposed)
            {
                return false;
            }

            _timers.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return true;
            }

            timer.Due = _ticks + Math.Max(0, dueTime.Ticks);
            timer.Period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
            timer.Order = ++_sequence;
            _timers.Add(timer);
            return true;
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            timer.Disposed = true;
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public long Due { get; set; }

        public long Period { get; set; }

        public long Order { get; set; }

        public bool Disposed { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => _owner.Schedule(this, dueTime, period);

        public void Fire() => _callback(_state);

        public void Dispose() => _owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
