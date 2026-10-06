// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using VentanaTools.Orbit.Extensions;

namespace DotnetExtensionSample;

/// <summary>
/// One on/off value kept only in this process's memory, shared by every placement. A change
/// completes <see cref="Read"/>'s task, which wakes every session; nothing runs on a timer.
/// </summary>
internal sealed class SampleState
{
    private readonly Lock _gate = new();
    private bool _on;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The value, and a task that completes at its next change.</summary>
    public (bool On, Task Changed) Read()
    {
        lock (_gate)
        {
            return (_on, _changed.Task);
        }
    }

    /// <summary>Sets the value; returns whether it changed.</summary>
    public bool Set(bool on)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (_on == on)
            {
                return false;
            }

            _on = on;
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
        return true;
    }
}

/// <summary>
/// The two contributions: <c>set-state</c> (an action that sets the value, with a face) and
/// <c>status</c> (a passive face). Each session publishes when the value changes and marks the
/// face <see cref="Face.Renew"/>, so the SDK keeps it alive while the session runs.
/// </summary>
internal sealed class StateHandler : ContributionHandler
{
    internal const string SetStateId = "example.dotnet-state/set-state";
    internal const string StatusId = "example.dotnet-state/status";

    private static readonly TimeSpan FaceLifetime = TimeSpan.FromMinutes(10);

    internal SampleState State { get; } = new();

    public override async Task RunSessionAsync(Session session, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (on, changed) = State.Read();
            if (session.SetFace(CreateFace(session.ContributionId, session.Settings, on)) == PublishResult.SessionEnded)
            {
                return;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken)
    {
        var session = invocation.Session;
        if (session.ContributionId != SetStateId)
        {
            return Task.FromResult(InvokeResult.Unsupported);
        }

        if (!session.Settings.TryGetValue("mode", out var mode) || mode is not ("on" or "off"))
        {
            return Task.FromResult(InvokeResult.Refused);
        }

        // Check again just before the change: the host may have cancelled, or stopped the session.
        // A real adapter makes the same checks just before its own side effect.
        cancellationToken.ThrowIfCancellationRequested();
        if (!session.IsActive)
        {
            return Task.FromResult(InvokeResult.Refused);
        }

        State.Set(mode == "on");
        return Task.FromResult(InvokeResult.Done);
    }

    internal static Face CreateFace(string contributionId, IReadOnlyDictionary<string, string> settings, bool on)
    {
        var number = contributionId == StatusId && settings.TryGetValue("display", out var display) && display == "number";
        string? line2 = contributionId == SetStateId
            ? settings.TryGetValue("mode", out var mode) && mode == "on" ? "Sets on" : "Sets off"
            : "Memory only";
        return new Face
        {
            Picture = FacePicture.Glyph(""),
            Line1 = number ? (on ? "1" : "0") : (on ? "On" : "Off"),
            Line2 = line2,
            Detail = on ? "The sample's in-memory state is on." : "The sample's in-memory state is off.",
            State = on ? FaceState.On : FaceState.Off,
            GoodFor = FaceLifetime,
            Renew = true,
        };
    }
}
