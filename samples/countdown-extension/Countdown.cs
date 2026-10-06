// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace CountdownExtensionSample;

internal enum CountdownPhase
{
    Ready = 1,
    Running = 2,
    Paused = 3,
    Finished = 4,
}

internal enum CountdownMode
{
    Start = 1,
    Pause = 2,
    Reset = 3,
}

internal readonly record struct CountdownSnapshot(TimeSpan Remaining, CountdownPhase Phase);

internal readonly record struct CountdownChoice(CountdownMode Mode, TimeSpan Duration);

/// <summary>
/// One countdown shared by every placement. Elapsed time comes from a monotonic clock, never from
/// how often a face was published, and nothing runs in the background: a reader computes the time
/// left when it asks.
/// </summary>
internal sealed class Countdown(TimeProvider time)
{
    private readonly Lock _gate = new();
    private TimeSpan _remaining = TimeSpan.FromMinutes(5);
    private CountdownPhase _phase = CountdownPhase.Ready;
    private long _startedAt;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes at the next change a pick makes (start, pause or reset).</summary>
    public Task Changed
    {
        get
        {
            lock (_gate)
            {
                return _changed.Task;
            }
        }
    }

    public CountdownSnapshot Read()
    {
        lock (_gate)
        {
            return ReadCore();
        }
    }

    /// <summary>Applies a pick. Start while running and pause while not running change nothing.</summary>
    public void Apply(CountdownChoice choice)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            var current = ReadCore();
            switch (choice.Mode)
            {
                case CountdownMode.Start:
                    if (current.Phase == CountdownPhase.Running)
                    {
                        return;
                    }

                    _remaining = current.Phase == CountdownPhase.Paused ? current.Remaining : choice.Duration;
                    _startedAt = time.GetTimestamp();
                    _phase = CountdownPhase.Running;
                    break;
                case CountdownMode.Pause:
                    if (current.Phase != CountdownPhase.Running)
                    {
                        return;
                    }

                    _remaining = current.Remaining;
                    _phase = CountdownPhase.Paused;
                    break;
                case CountdownMode.Reset:
                    _remaining = choice.Duration;
                    _phase = CountdownPhase.Ready;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(choice));
            }

            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }

    private CountdownSnapshot ReadCore()
    {
        if (_phase != CountdownPhase.Running)
        {
            return new CountdownSnapshot(_remaining, _phase);
        }

        var remaining = _remaining - time.GetElapsedTime(_startedAt);
        if (remaining > TimeSpan.Zero)
        {
            return new CountdownSnapshot(remaining, _phase);
        }

        _remaining = TimeSpan.Zero;
        _phase = CountdownPhase.Finished;
        return new CountdownSnapshot(_remaining, _phase);
    }
}

/// <summary>The contribution ids and the choices of extension.json.</summary>
internal static class CountdownChoices
{
    public const string TimerId = "example.countdown/timer";
    public const string StatusId = "example.countdown/status";

    public static bool TryRead(IReadOnlyDictionary<string, string> settings, out CountdownChoice choice)
    {
        choice = default;
        if (settings.Count != 2 || !settings.TryGetValue("mode", out var mode) || !settings.TryGetValue("duration", out var duration))
        {
            return false;
        }

        CountdownMode? parsedMode = mode switch
        {
            "start" => CountdownMode.Start,
            "pause" => CountdownMode.Pause,
            "reset" => CountdownMode.Reset,
            _ => null,
        };
        TimeSpan? parsedDuration = duration switch
        {
            "one-minute" => TimeSpan.FromMinutes(1),
            "five-minutes" => TimeSpan.FromMinutes(5),
            "twenty-five-minutes" => TimeSpan.FromMinutes(25),
            _ => null,
        };
        if (parsedMode is null || parsedDuration is null)
        {
            return false;
        }

        choice = new CountdownChoice(parsedMode.Value, parsedDuration.Value);
        return true;
    }
}
