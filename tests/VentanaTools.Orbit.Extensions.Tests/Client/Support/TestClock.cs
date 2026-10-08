// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Tests.Client;

/// <summary>A manual clock with real timers: due timers fire in order during <see cref="Advance"/>, on the caller's thread.</summary>
internal sealed class TestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<Timer> _timers = [];
    private long _ticks;
    private long _order;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero) + Elapsed;

    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        long target;
        lock (_gate)
        {
            target = _ticks + delta.Ticks;
        }

        while (true)
        {
            Timer? due = null;
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

                Interlocked.Exchange(ref _ticks, Math.Max(_ticks, due.Due));
                if (due.Period > 0)
                {
                    due.Due += due.Period;
                }
                else
                {
                    _timers.Remove(due);
                }
            }

            due.Callback(due.State);
        }
    }

    /// <summary>Advances in steps, yielding real time between them so asynchronous work keeps up.</summary>
    public async Task AdvanceAsync(TimeSpan delta, TimeSpan? step = null)
    {
        var size = step ?? TimeSpan.FromMilliseconds(100);
        var left = delta;
        while (left > TimeSpan.Zero)
        {
            var next = left < size ? left : size;
            Advance(next);
            left -= next;
            await Task.Delay(1);
        }
    }

    private sealed class Timer : ITimer
    {
        private readonly TestClock _owner;

        public Timer(TestClock owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            Callback = callback;
            State = state;
        }

        public TimerCallback Callback { get; }

        public object? State { get; }

        public long Due { get; set; }

        public long Period { get; set; }

        public long Order { get; set; }

        public bool Disposed { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                if (Disposed)
                {
                    return false;
                }

                _owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return true;
                }

                Due = _owner._ticks + Math.Max(0, dueTime.Ticks);
                Period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
                Order = ++_owner._order;
                _owner._timers.Add(this);
                return true;
            }
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                Disposed = true;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
