using System.Globalization;
using Orbit.Extensions.Sdk;

namespace CountdownExtensionSample;

internal sealed class CountdownHandler : IContributionHandler
{
    private readonly Countdown _countdown;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private int _activeFaceLoops;

    public CountdownHandler(Countdown countdown, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _countdown = countdown;
        _delay = delay ?? ((interval, token) => Task.Delay(interval, token));
    }

    public Task RunSessionAsync(CompanionSession session, CancellationToken cancellationToken) =>
        RunSessionCoreAsync(session.ActionId, session.Settings, () => session.IsActive,
            session.SetFace, failure => session.Fail(failure), cancellationToken);

    public Task<CompanionOutcome> InvokeAsync(CompanionInvocation invocation, CancellationToken cancellationToken) =>
        Task.FromResult(InvokeCore(invocation.Session.ActionId, invocation.Session.Settings,
            () => invocation.Session.IsActive, cancellationToken));

    // Narrow behavior seams exercise cancellation and publication without copying transport.
    internal async Task RunSessionCoreAsync(string actionId, IReadOnlyDictionary<string, string> settings,
        Func<bool> isActive, Func<CompanionFace, bool> publish, Action<CompanionFailure> fail,
        CancellationToken cancellationToken)
    {
        CountdownChoice? choice = null;
        if (actionId == CountdownChoices.TimerAction && CountdownChoices.TryRead(settings, out var configured))
            choice = configured;
        else if (actionId != CountdownChoices.StatusAction || settings.Count != 0)
        {
            if (isActive() && !cancellationToken.IsCancellationRequested) fail(CompanionFailure.NeedsSetup);
            return;
        }

        Interlocked.Increment(ref _activeFaceLoops);
        try
        {
            while (isActive())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!publish(CreateFace(_countdown.Read(), choice))) return;
                var milliseconds = Math.Max(1000, Volatile.Read(ref _activeFaceLoops) * 1000 / 32);
                await _delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
            }
        }
        finally { Interlocked.Decrement(ref _activeFaceLoops); }
    }

    internal CompanionOutcome InvokeCore(string actionId, IReadOnlyDictionary<string, string> settings,
        Func<bool> isActive, CancellationToken cancellationToken)
    {
        if (actionId != CountdownChoices.TimerAction) return CompanionOutcome.Unsupported;
        if (!CountdownChoices.TryRead(settings, out var choice)) return CompanionOutcome.Refused;
        return _countdown.Apply(choice, isActive, cancellationToken) ? CompanionOutcome.Done : CompanionOutcome.Refused;
    }

    internal static CompanionFace CreateFace(CountdownSnapshot snapshot, CountdownChoice? choice = null)
    {
        var seconds = Math.Max(0, (long)Math.Ceiling(snapshot.Remaining.TotalSeconds));
        var text = string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
        var phase = snapshot.Phase switch
        {
            CountdownPhase.Ready => "Ready",
            CountdownPhase.Running => "Running",
            CountdownPhase.Paused => "Paused",
            _ => "Finished",
        };
        var operation = choice?.Mode switch
        {
            CountdownMode.Start => "Start",
            CountdownMode.Pause => "Pause",
            CountdownMode.Reset => "Reset",
            _ => null,
        };
        return new CompanionFace(text, 5)
        {
            Line2 = operation is null ? phase : $"{operation} · {phase}",
            Detail = $"Countdown {phase.ToLowerInvariant()}. {text} remaining.",
            State = snapshot.Phase switch
            {
                CountdownPhase.Running => CompanionFaceState.Playing,
                CountdownPhase.Paused => CompanionFaceState.Paused,
                CountdownPhase.Finished => CompanionFaceState.Off,
                _ => CompanionFaceState.None,
            },
        };
    }
}
