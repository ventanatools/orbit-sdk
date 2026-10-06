// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace CountdownExtensionSample;

internal enum CountdownPhase { Ready, Running, Paused, Finished }
internal enum CountdownMode { Start, Pause, Reset }
internal readonly record struct CountdownSnapshot(TimeSpan Remaining, CountdownPhase Phase);
internal readonly record struct CountdownChoice(CountdownMode Mode, TimeSpan Duration);

// Elapsed time is derived from a monotonic clock, never from publication count.
// No background work is needed while Orbit has no sessions for this sample.
internal sealed class Countdown(TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private TimeSpan _remaining = TimeSpan.FromMinutes(5);
    private CountdownPhase _phase = CountdownPhase.Ready;
    private long _startedAt;

    public CountdownSnapshot Read()
    {
        lock (_gate) return ReadCore();
    }

    public bool Apply(CountdownChoice choice, Func<bool> isActive, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isActive()) return false;
            var current = ReadCore();
            switch (choice.Mode)
            {
                case CountdownMode.Start:
                    if (current.Phase == CountdownPhase.Running) return true;
                    _remaining = current.Phase == CountdownPhase.Paused ? current.Remaining : choice.Duration;
                    _startedAt = timeProvider.GetTimestamp();
                    _phase = CountdownPhase.Running;
                    break;
                case CountdownMode.Pause:
                    if (current.Phase != CountdownPhase.Running) return true;
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
            return true;
        }
    }

    private CountdownSnapshot ReadCore()
    {
        if (_phase != CountdownPhase.Running) return new(_remaining, _phase);
        var remaining = _remaining - timeProvider.GetElapsedTime(_startedAt);
        if (remaining > TimeSpan.Zero) return new(remaining, _phase);
        _remaining = TimeSpan.Zero;
        _phase = CountdownPhase.Finished;
        return new(_remaining, _phase);
    }
}

internal static class CountdownChoices
{
    public const string ExtensionId = "example.countdown";
    public const string TimerAction = ExtensionId + "/timer";
    public const string StatusAction = ExtensionId + "/status";

    public static bool TryRead(IReadOnlyDictionary<string, string> settings, out CountdownChoice choice)
    {
        choice = default;
        if (settings.Count != 2 || !settings.TryGetValue("mode", out var modeText)
            || !settings.TryGetValue("duration", out var durationText)) return false;
        CountdownMode? mode = modeText switch
        {
            "start" => CountdownMode.Start,
            "pause" => CountdownMode.Pause,
            "reset" => CountdownMode.Reset,
            _ => null,
        };
        TimeSpan? duration = durationText switch
        {
            "one-minute" => TimeSpan.FromMinutes(1),
            "five-minutes" => TimeSpan.FromMinutes(5),
            "twenty-five-minutes" => TimeSpan.FromMinutes(25),
            _ => null,
        };
        if (mode is null || duration is null) return false;
        choice = new(mode.Value, duration.Value);
        return true;
    }
}
