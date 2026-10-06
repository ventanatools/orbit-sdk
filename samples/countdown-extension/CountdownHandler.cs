// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;
using VentanaTools.Orbit.Extensions;

namespace CountdownExtensionSample;

/// <summary>
/// The countdown's two contributions: the timer action (start, pause or reset, with a face) and
/// the passive status. Faces are published when what they show changes: every second while the
/// countdown runs, and once per change otherwise, renewed by the SDK while the session runs.
/// </summary>
internal sealed class CountdownHandler(TimeProvider? time = null) : ContributionHandler
{
    private static readonly TimeSpan RunningFaceLifetime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StillFaceLifetime = TimeSpan.FromMinutes(10);

    internal Countdown Countdown { get; } = new(time ?? TimeProvider.System);

    public override async Task RunSessionAsync(Session session, CancellationToken cancellationToken)
    {
        CountdownChoice? choice = session.ContributionId == CountdownChoices.TimerId && CountdownChoices.TryRead(session.Settings, out var configured)
            ? configured
            : null;
        while (true)
        {
            var changed = Countdown.Changed;
            var snapshot = Countdown.Read();
            session.SetFace(CreateFace(snapshot, choice, session.UiCulture));
            if (snapshot.Phase == CountdownPhase.Running)
            {
                // Wake when the displayed second changes, or sooner when a pick changes the countdown.
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await Task.WhenAny(changed, Task.Delay(UntilNextSecond(snapshot.Remaining), session.Time, tick.Token)).ConfigureAwait(false);
                await tick.CancelAsync().ConfigureAwait(false);
            }
            else
            {
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public override Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Session.ContributionId != CountdownChoices.TimerId)
        {
            return Task.FromResult(InvokeResult.Unsupported);
        }

        if (!CountdownChoices.TryRead(invocation.Session.Settings, out var choice))
        {
            return Task.FromResult(InvokeResult.Refused);
        }

        // Check again just before the change: the host may have cancelled, or stopped the session.
        cancellationToken.ThrowIfCancellationRequested();
        if (!invocation.Session.IsActive)
        {
            return Task.FromResult(InvokeResult.Refused);
        }

        Countdown.Apply(choice);
        return Task.FromResult(InvokeResult.Done);
    }

    internal static Face CreateFace(CountdownSnapshot snapshot, CountdownChoice? choice, CultureInfo culture)
    {
        var seconds = Math.Max(0, (long)Math.Ceiling(snapshot.Remaining.TotalSeconds));
        var text = (seconds / 60).ToString(culture) + ":" + (seconds % 60).ToString("00", culture);
        var phase = snapshot.Phase switch
        {
            CountdownPhase.Running => "Running",
            CountdownPhase.Paused => "Paused",
            CountdownPhase.Finished => "Finished",
            _ => "Ready",
        };
        var operation = choice?.Mode switch
        {
            CountdownMode.Start => "Start",
            CountdownMode.Pause => "Pause",
            CountdownMode.Reset => "Reset",
            _ => null,
        };
        var running = snapshot.Phase == CountdownPhase.Running;
        return new Face
        {
            Picture = FacePicture.Glyph(""),
            Line1 = text,
            Line2 = operation is null ? phase : operation + " · " + phase,
            Detail = "Countdown " + phase.ToLowerInvariant() + ". " + text + " remaining.",
            State = snapshot.Phase switch
            {
                CountdownPhase.Running => FaceState.Playing,
                CountdownPhase.Paused => FaceState.Paused,
                CountdownPhase.Finished => FaceState.Off,
                _ => FaceState.None,
            },
            GoodFor = running ? RunningFaceLifetime : StillFaceLifetime,
            Renew = !running,
        };
    }

    /// <summary>How long until the displayed whole second changes (the display rounds up).</summary>
    internal static TimeSpan UntilNextSecond(TimeSpan remaining)
    {
        var fraction = remaining.Ticks % TimeSpan.TicksPerSecond;
        return fraction == 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromTicks(Math.Max(fraction, TimeSpan.TicksPerMillisecond));
    }
}
