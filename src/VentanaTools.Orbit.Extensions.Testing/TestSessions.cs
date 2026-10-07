// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Testing;

/// <summary>
/// Creates <see cref="RecordingSession"/>s for unit tests of a handler: no host, no pipe and no
/// connection, just a <see cref="Session"/> whose publications are recorded.
/// </summary>
/// <example>
/// <code>
/// using var recording = TestSessions.FromManifest(manifest, "example.clock/time");
/// var run = recording.RunAsync(new TimeWidget());
/// var face = await recording.WaitForFaceAsync(TimeSpan.FromSeconds(5));
/// // … assert on face …
/// recording.Stop();
/// await run;
/// </code>
/// </example>
public static class TestSessions
{
    /// <summary>
    /// A session for a contribution of <paramref name="manifest"/>: its <see cref="Session.Provides"/>
    /// comes from the manifest, missing settings take their defaults, and the result is checked
    /// as a companion checks a <c>startSession</c>.
    /// </summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="contributionId">The contribution id.</param>
    /// <param name="settings">Setting values; null or missing keys take the defaults.</param>
    /// <param name="time">The session's clock (<see cref="Session.Time"/>); null for <see cref="TimeProvider.System"/>.</param>
    /// <returns>The recording session; dispose it.</returns>
    /// <exception cref="ArgumentException">The contribution is not in the manifest, or the settings do not match it.</exception>
    public static RecordingSession FromManifest(ExtensionManifest manifest, string contributionId,
        IReadOnlyDictionary<string, string>? settings = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(contributionId);
        var contribution = (manifest.Contributions ?? []).FirstOrDefault(item => string.Equals(item?.Id, contributionId, StringComparison.Ordinal))
            ?? throw new ArgumentException("The contribution is not in the manifest.", nameof(contributionId));
        var complete = TestSettings.Complete(contribution, settings);
        if (SessionSettings.Check(manifest, contributionId, complete) is { } code)
        {
            throw new ArgumentException("The settings do not match the manifest (" + code.Value + ").", nameof(settings));
        }

        return new RecordingSession(contributionId, contribution.Provides, complete, "en-US", time ?? TimeProvider.System);
    }

    /// <summary>A session with an explicit <see cref="Session.Provides"/> and settings, without a manifest.</summary>
    /// <param name="contributionId">The contribution id.</param>
    /// <param name="provides">What the contribution provides.</param>
    /// <param name="settings">The setting values, used as given; null for none.</param>
    /// <param name="uiLanguage">The host UI language (<see cref="Session.UiLanguage"/>).</param>
    /// <param name="time">The session's clock; null for <see cref="TimeProvider.System"/>.</param>
    /// <returns>The recording session; dispose it.</returns>
    public static RecordingSession Create(string contributionId, Provides provides, IReadOnlyDictionary<string, string>? settings = null,
        string uiLanguage = "en-US", TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(contributionId);
        ArgumentNullException.ThrowIfNull(uiLanguage);
        return new RecordingSession(contributionId, provides, settings ?? new Dictionary<string, string>(), uiLanguage, time ?? TimeProvider.System);
    }
}

/// <summary>
/// A <see cref="Session"/> that records what its handler publishes, as cleaned for sending, and
/// that a test stops as the host's <c>stopSession</c> would. Publications after
/// <see cref="Stop"/> are recorded as <see cref="PublicationKind.AfterStop"/> and never count as
/// accepted. Renewal (<see cref="Face.Renew"/>) is the client's job and is not simulated here.
/// </summary>
public sealed class RecordingSession : IDisposable
{
    private readonly object _gate = new();
    private readonly List<RecordedPublication> _publications = [];
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private readonly TimeProvider _time;
    private readonly long _start;
    private Face? _lastFace;
    private int _faceCursor;
    private TaskCompletionSource? _published;

    internal RecordingSession(string contributionId, Provides provides, IReadOnlyDictionary<string, string> settings, string uiLanguage,
        TimeProvider time)
    {
        _time = time;
        _start = time.GetTimestamp();
        _token = _cancellation.Token;
        Session = new Session(Guid.NewGuid().ToString("N"), contributionId, provides, settings, uiLanguage, [], time, new Sink(this));
    }

    /// <summary>The session to pass to the handler.</summary>
    public Session Session { get; }

    /// <summary>Every publication so far, in order.</summary>
    public IReadOnlyList<RecordedPublication> Publications
    {
        get
        {
            lock (_gate)
            {
                return _publications.ToArray();
            }
        }
    }

    /// <summary>The face currently shown, as cleaned for sending: the last <c>SetFace</c>, or null after <c>ClearFace</c> or <c>Fail</c>.</summary>
    public Face? LastFace
    {
        get
        {
            lock (_gate)
            {
                return _lastFace;
            }
        }
    }

    /// <summary>The session's token, cancelled by <see cref="Stop"/>.</summary>
    public CancellationToken Cancellation => _token;

    /// <summary>Ends the session as <c>stopSession</c> would: it becomes inactive and its token is cancelled.</summary>
    public void Stop()
    {
        Session.End();
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    /// <summary>
    /// Runs the handler's <see cref="IContributionHandler.RunSessionAsync"/> and waits for it. The
    /// handler runs up to its first <c>await</c> before this method returns, outside any
    /// synchronization context, so a face it publishes before awaiting is already recorded; the
    /// rest runs on the thread pool, as with the client. Use <see cref="WaitForFaceAsync"/> or
    /// <see cref="WaitForPublicationsAsync"/> before asserting on later faces. An
    /// <see cref="OperationCanceledException"/> after <see cref="Stop"/> is a normal completion;
    /// any other exception is rethrown.
    /// </summary>
    /// <param name="handler">The handler.</param>
    /// <param name="timeout">How long to wait on the session's clock; null to wait until it ends.</param>
    /// <returns>A task that completes when the handler ends.</returns>
    /// <exception cref="TimeoutException">The handler did not end within <paramref name="timeout"/>.</exception>
    public async Task RunAsync(IContributionHandler handler, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var run = Start(handler);
        try
        {
            if (timeout is { } bound)
            {
                await run.WaitAsync(bound, _time).ConfigureAwait(false);
            }
            else
            {
                await run.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            // A normal completion after Stop.
        }
    }

    /// <summary>
    /// Waits for the next face the handler shows (<see cref="PublicationKind.SetFace"/>), in
    /// publication order: the first call returns the first face, and each later call the face after
    /// the one the previous call returned, at once when it is already recorded.
    /// </summary>
    /// <param name="timeout">How long to wait, in real time whatever the session's clock, so a test that drives a manual clock cannot hang here.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The face, as cleaned for sending.</returns>
    /// <exception cref="TimeoutException">No further face was published within <paramref name="timeout"/>.</exception>
    public async Task<Face> WaitForFaceAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            Task published;
            lock (_gate)
            {
                for (; _faceCursor < _publications.Count; _faceCursor++)
                {
                    if (_publications[_faceCursor] is { Kind: PublicationKind.SetFace, Face: { } face })
                    {
                        _faceCursor++;
                        return face;
                    }
                }

                published = (_published ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            await WaitAsync(published, timeout, started, "No further face was published within the timeout.", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until at least <paramref name="count"/> publications of any kind are recorded (<see cref="Publications"/>).</summary>
    /// <param name="count">The number of publications to wait for.</param>
    /// <param name="timeout">How long to wait, in real time whatever the session's clock.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the publications are recorded.</returns>
    /// <exception cref="TimeoutException">Fewer publications were recorded within <paramref name="timeout"/>.</exception>
    public async Task WaitForPublicationsAsync(int count, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            Task published;
            lock (_gate)
            {
                if (_publications.Count >= count)
                {
                    return;
                }

                published = (_published ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            await WaitAsync(published, timeout, started, "Fewer publications than expected were recorded within the timeout.", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Stops the session and releases its token.</summary>
    public void Dispose()
    {
        Stop();
        _cancellation.Dispose();
    }

    private static async Task WaitAsync(Task published, TimeSpan timeout, long started, string message, CancellationToken cancellationToken)
    {
        var remaining = timeout - TimeProvider.System.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(message);
        }

        try
        {
            await published.WaitAsync(remaining, TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(message);
        }
    }

    /// <summary>Calls the handler up to its first await with no synchronization context, so its continuations run on the thread pool.</summary>
    private Task Start(IContributionHandler handler)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return handler.RunSessionAsync(Session, _token) ?? Task.CompletedTask;
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private void Record(PublicationKind kind, FaceCommand command)
    {
        var face = command.Face is { } wire ? FaceRules.ToFace(wire, command.Renew) : null;
        TaskCompletionSource? published;
        lock (_gate)
        {
            _publications.Add(new RecordedPublication
            {
                Kind = kind,
                Face = face,
                Failure = command.Failure,
                At = _time.GetElapsedTime(_start),
            });
            if (kind != PublicationKind.AfterStop)
            {
                _lastFace = face;
            }

            published = _published;
            _published = null;
        }

        published?.TrySetResult();
    }

    private sealed class Sink : ISessionSink
    {
        private readonly RecordingSession _owner;

        public Sink(RecordingSession owner) => _owner = owner;

        public PublishResult Publish(Session session, FaceCommand command)
        {
            _owner.Record(command.Kind switch
            {
                FaceCommandKind.Set => PublicationKind.SetFace,
                FaceCommandKind.Clear => PublicationKind.ClearFace,
                _ => PublicationKind.Fail,
            }, command);
            return PublishResult.Accepted;
        }

        public void PublishedAfterEnd(Session session, FaceCommand command) => _owner.Record(PublicationKind.AfterStop, command);

        public void FaceWithoutProvides(Session session)
        {
            // The session throws InvalidOperationException to the handler.
        }
    }
}

/// <summary>What a recorded publication was.</summary>
public enum PublicationKind
{
    /// <summary><see cref="Session.SetFace"/>.</summary>
    SetFace = 1,

    /// <summary><see cref="Session.ClearFace"/>.</summary>
    ClearFace = 2,

    /// <summary><see cref="Session.Fail"/>.</summary>
    Fail = 3,

    /// <summary>Any publication after the session ended; nothing would be sent.</summary>
    AfterStop = 4,
}

/// <summary>One publication a <see cref="RecordingSession"/> recorded.</summary>
public sealed class RecordedPublication
{
    /// <summary>What it was.</summary>
    public required PublicationKind Kind { get; init; }

    /// <summary>The face, cleaned for sending; <see cref="Face"/> is a record, so it compares by value.</summary>
    public Face? Face { get; init; }

    /// <summary>The failure, for <see cref="PublicationKind.Fail"/>.</summary>
    public Failure? Failure { get; init; }

    /// <summary>When it happened, on the session's clock, since the session was created.</summary>
    public required TimeSpan At { get; init; }
}

/// <summary>Creates invocations for unit tests of an action's <see cref="IContributionHandler.InvokeAsync"/>.</summary>
public static class TestInvocations
{
    /// <summary>An invocation of <paramref name="session"/>'s contribution.</summary>
    /// <param name="session">The session the invocation belongs to.</param>
    /// <param name="requestId">The request id; null for a new one.</param>
    /// <returns>The invocation.</returns>
    public static Invocation Create(RecordingSession session, string? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new Invocation(requestId ?? Guid.NewGuid().ToString("N"), session.Session);
    }
}

/// <summary>Setting helpers shared by the test kit.</summary>
internal static class TestSettings
{
    /// <summary>The contribution's defaults, overridden by <paramref name="settings"/> (extra keys kept, so a companion can refuse them).</summary>
    public static Dictionary<string, string> Complete(Contribution contribution, IReadOnlyDictionary<string, string>? settings)
    {
        var complete = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in contribution.Settings ?? [])
        {
            complete[setting.Id] = setting.Default;
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            complete[key] = value;
        }

        return complete;
    }

    /// <summary>Setting combinations, defaults first, at most <paramref name="max"/>.</summary>
    public static IEnumerable<IReadOnlyDictionary<string, string>> Combinations(Contribution contribution, int max)
    {
        var settings = contribution.Settings ?? [];
        var values = settings
            .Select(setting => new[] { setting.Default }.Concat(setting.Choices.Select(choice => choice.Value)
                .Where(value => !string.Equals(value, setting.Default, StringComparison.Ordinal))).ToArray())
            .ToArray();
        var index = new int[values.Length];
        for (var count = 0; count < max; count++)
        {
            var combination = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < values.Length; i++)
            {
                combination[settings[i].Id] = values[i][index[i]];
            }

            yield return combination;
            var position = values.Length - 1;
            while (position >= 0 && ++index[position] == values[position].Length)
            {
                index[position] = 0;
                position--;
            }

            if (position < 0)
            {
                yield break;
            }
        }
    }
}
