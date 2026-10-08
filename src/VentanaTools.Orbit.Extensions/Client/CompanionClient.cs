// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Runtime.Versioning;
using VentanaTools.Orbit.Extensions.Wire;

namespace VentanaTools.Orbit.Extensions;

/// <summary>Where a <see cref="CompanionClient"/> is.</summary>
public enum ConnectionState
{
    /// <summary><see cref="CompanionClient.RunAsync"/> has not started.</summary>
    NotStarted = 1,

    /// <summary>Connecting to the host and running the handshake.</summary>
    Connecting = 2,

    /// <summary>Authenticated: the host can start sessions.</summary>
    Connected = 3,

    /// <summary>Waiting before the next attempt; <see cref="StatusChangedEventArgs.RetryIn"/> says how long.</summary>
    Waiting = 4,

    /// <summary>Stopped: cancelled, or retrying cannot help until the pairing, the manifest or a version changes.</summary>
    Stopped = 5,
}

/// <summary>Options for a <see cref="CompanionClient"/>. The defaults are the contract's values (contract §9.2).</summary>
public sealed class CompanionClientOptions
{
    /// <summary>The first retry delay of the normal backoff, which doubles per failure.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest normal retry delay, and the most any server that is not verified can make the client wait.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest delay after <c>host.not-running</c> or <c>host.turned-off</c>; probing a missing pipe costs the host nothing.</summary>
    public TimeSpan HostAbsentMaxRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Each retry delay is multiplied by uniform(1 − j, 1 + j), and never exceeds its cap; 0 to just under 1.</summary>
    public double RetryJitter { get; init; } = 0.2;

    /// <summary>How long a connection must stay up for the backoff to start again from <see cref="InitialRetryDelay"/>.</summary>
    public TimeSpan StableConnection { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a handler may keep running after its token was cancelled before <see cref="HandlerFault.IgnoredCancellation"/>.</summary>
    public TimeSpan HandlerStopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The clock for every delay, deadline and timer, and <see cref="Session.Time"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        if (FindProblem() is not { } problem)
        {
            return;
        }

        throw problem.Option == nameof(TimeProvider)
            ? new ArgumentException(problem.Message, problem.Option)
            : new ArgumentOutOfRangeException(problem.Option, problem.Message);
    }

    /// <summary>The first option that is out of range and the sentence that says why; null when every option is valid.</summary>
    internal (string Option, string Message)? FindProblem()
    {
        const string Delay = "The delay must be greater than zero and at most one day.";
        static bool InRange(TimeSpan value) => value > TimeSpan.Zero && value <= TimeSpan.FromDays(1);
        if (!InRange(InitialRetryDelay))
        {
            return (nameof(InitialRetryDelay), Delay);
        }

        if (!InRange(MaxRetryDelay))
        {
            return (nameof(MaxRetryDelay), Delay);
        }

        if (!InRange(HostAbsentMaxRetryDelay))
        {
            return (nameof(HostAbsentMaxRetryDelay), Delay);
        }

        if (!InRange(StableConnection))
        {
            return (nameof(StableConnection), Delay);
        }

        if (!InRange(HandlerStopTimeout))
        {
            return (nameof(HandlerStopTimeout), Delay);
        }

        if (MaxRetryDelay < InitialRetryDelay)
        {
            return (nameof(MaxRetryDelay), "MaxRetryDelay must be at least InitialRetryDelay.");
        }

        if (!(RetryJitter >= 0 && RetryJitter < 1))
        {
            return (nameof(RetryJitter), "RetryJitter must be at least 0 and less than 1.");
        }

        return TimeProvider is null ? (nameof(TimeProvider), "TimeProvider must not be null.") : null;
    }
}

/// <summary>A state change of a <see cref="CompanionClient"/>, with the reason code that explains it.</summary>
public sealed class StatusChangedEventArgs : EventArgs
{
    internal StatusChangedEventArgs(ConnectionState state, ReasonCode? reason, TimeSpan? retryIn, int attempt, bool serverVerified,
        HostIdentity? host, int? negotiatedVersion, string? peerMessage)
    {
        State = state;
        Reason = reason;
        RetryIn = retryIn;
        Attempt = attempt;
        ServerVerified = serverVerified;
        Host = host;
        ProtocolVersion = negotiatedVersion;
        PeerMessage = peerMessage;
    }

    /// <summary>The new state.</summary>
    public ConnectionState State { get; }

    /// <summary>The reason code (contract §8); null when there is none, for example a connection that simply closed.</summary>
    public ReasonCode? Reason { get; }

    /// <summary>For <see cref="ConnectionState.Waiting"/>, the delay before the next attempt.</summary>
    public TimeSpan? RetryIn { get; }

    /// <summary>The connection attempt this status belongs to, counting from 1 since the last stable connection.</summary>
    public int Attempt { get; }

    /// <summary>
    /// Whether the server of the attempt that produced this status was verified: the operating
    /// system confirmed its user and integrity level, or it proved knowledge of the secret.
    /// </summary>
    public bool ServerVerified { get; }

    /// <summary>The host's id and version, once its challenge verified.</summary>
    public HostIdentity? Host { get; }

    /// <summary>The negotiated protocol version, once the host's challenge verified.</summary>
    public int? ProtocolVersion { get; }

    /// <summary>The peer's <c>error.message</c>: fixed English from the peer, never shown as host chrome; printed only in verbose mode.</summary>
    internal string? PeerMessage { get; }
}

/// <summary>How an author's handler misbehaved.</summary>
public enum HandlerFault
{
    /// <summary>The handler threw (other than an <see cref="OperationCanceledException"/> after cancellation).</summary>
    Exception = 1,

    /// <summary>The handler was still running <see cref="CompanionClientOptions.HandlerStopTimeout"/> after its token was cancelled.</summary>
    IgnoredCancellation = 2,

    /// <summary>A session was refused (<c>session.capacity</c>) because 64 session handlers were still running.</summary>
    SessionCapacity = 3,

    /// <summary>An invocation returned the default <see cref="InvokeResult"/>; it was answered <see cref="Outcome.Failed"/>.</summary>
    InvalidResult = 4,
}

/// <summary>An author's handler misbehaved. The exception is the author's own, for the author only.</summary>
public sealed class HandlerFaultedEventArgs : EventArgs
{
    internal HandlerFaultedEventArgs(HandlerFault kind, string contributionId, string? sessionId, string? requestId, Exception? exception)
    {
        Kind = kind;
        ContributionId = contributionId;
        SessionId = sessionId;
        RequestId = requestId;
        Exception = exception;
    }

    /// <summary>What happened.</summary>
    public HandlerFault Kind { get; }

    /// <summary>The contribution whose handler misbehaved.</summary>
    public string ContributionId { get; }

    /// <summary>The session, when a session handler or one of its invocations misbehaved.</summary>
    public string? SessionId { get; }

    /// <summary>The request, when an invocation handler misbehaved.</summary>
    public string? RequestId { get; }

    /// <summary>The author's exception, for <see cref="HandlerFault.Exception"/>.</summary>
    public Exception? Exception { get; }
}

/// <summary>Observes a client's internals: used by the Testing package's contract checks.</summary>
internal interface IClientObserver
{
    void FrameProcessed(WireMessage message);

    /// <summary>A <c>startSession</c> was accepted (<paramref name="refusal"/> null) or refused.</summary>
    void SessionStarted(string sessionId, ReasonCode? refusal);

    void SessionHandlerEnded(string sessionId);

    void InvocationHandlerEnded(string requestId);

    void InvocationDeadlineReached(string requestId);

    void PublishedAfterEnd(string sessionId);

    void FaceWithoutProvides(string sessionId);
}

/// <summary>The client's internal seams.</summary>
internal sealed class ClientInternals
{
    /// <summary>The transport; null for the named pipe of the pairing.</summary>
    public ICompanionTransport? Transport { get; init; }

    public IClientObserver? Observer { get; init; }

    /// <summary>The capability ids offered in <c>hello</c>.</summary>
    public IReadOnlyList<string> OfferedCapabilities { get; init; } = [Capabilities.TestEcho];

    /// <summary>Called before a retry that follows <c>manifest.mismatch</c> or <c>auth.identity-changed</c>; returns a changed manifest, or null.</summary>
    public Func<CancellationToken, Task<ExtensionManifest?>>? ReloadManifest { get; init; }

    /// <summary>Replaces the uniform random source of the backoff jitter.</summary>
    public Func<double>? Random { get; init; }
}

/// <summary>
/// A companion's connection to one host registration (contract §7, §9.2): it connects over the
/// pairing's named pipe, authenticates, runs the host's sessions and invocations on the
/// handler, and reconnects with backoff. It never starts the host or loads anything into it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StatusChanged"/> and <see cref="HandlerFaulted"/> are raised on the thread pool,
/// one at a time and in order, never on the pipe reader; a subscriber that throws is caught and
/// never stops the client. A subscriber that touches UI must marshal to its own thread.
/// </para>
/// <para>
/// Most companions use <see cref="CompanionApp.RunAsync"/>, which finds the files, prints status
/// and handles Ctrl+C. The caller keeps ownership of the <see cref="Pairing"/> and must keep it
/// undisposed until <see cref="RunAsync"/> completes.
/// </para>
/// </remarks>
public sealed class CompanionClient
{
    private readonly Pairing _pairing;
    private readonly IContributionHandler _handler;
    private readonly CompanionClientOptions _options;
    private readonly ClientInternals _internals;
    private readonly EventQueue _events;
    private readonly AsyncSignal _wake = new();
    private ExtensionManifest _manifest;
    private string _manifestHash;
    private int _state = (int)ConnectionState.NotStarted;
    private int _running;

    /// <summary>Creates a client. Nothing connects until <see cref="RunAsync"/>.</summary>
    /// <param name="pairing">The pairing; the caller keeps ownership and disposes it after <see cref="RunAsync"/> completes.</param>
    /// <param name="manifest">The companion's manifest, the same one the host admitted. The client keeps a copy.</param>
    /// <param name="handler">The contribution handler.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="manifest"/> is invalid, or an option is out of range.</exception>
    public CompanionClient(Pairing pairing, ExtensionManifest manifest, IContributionHandler handler,
        CompanionClientOptions? options = null)
        : this(pairing, manifest, handler, options, new ClientInternals())
    {
    }

    internal CompanionClient(Pairing pairing, ExtensionManifest manifest, IContributionHandler handler,
        CompanionClientOptions? options, ClientInternals internals)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(internals);
        _options = options ?? new CompanionClientOptions();
        _options.Validate();
        _pairing = pairing;
        _handler = handler;
        _internals = internals;
        _manifest = Snapshot(manifest, nameof(manifest));
        _manifestHash = ManifestWriter.ComputeHash(_manifest);
        _events = new EventQueue(error => SubscriberFaulted?.Invoke(error));
    }

    /// <summary>Raised on every state change, with the reason code.</summary>
    public event EventHandler<StatusChangedEventArgs>? StatusChanged;

    /// <summary>Raised when an author's handler throws, ignores cancellation, hits the session cap or returns an invalid result.</summary>
    public event EventHandler<HandlerFaultedEventArgs>? HandlerFaulted;

    /// <summary>A <see cref="StatusChanged"/> or <see cref="HandlerFaulted"/> subscriber threw.</summary>
    internal event Action<Exception>? SubscriberFaulted;

    /// <summary>The current state.</summary>
    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

    internal CompanionClientOptions Options => _options;

    internal ClientInternals Internals => _internals;

    internal IContributionHandler Handler => _handler;

    internal HandlerTracker Tracker { get; } = new();

    internal TimeProvider Time => _options.TimeProvider;

    /// <summary>
    /// Connects and serves the host until <paramref name="cancellationToken"/> is cancelled or the
    /// client reaches <see cref="ConnectionState.Stopped"/>.
    /// </summary>
    /// <param name="cancellationToken">Stops the client.</param>
    /// <returns>A task that completes when the client stops.</returns>
    /// <exception cref="InvalidOperationException">The client is already running.</exception>
    [SupportedOSPlatform("windows")]
    public Task RunAsync(CancellationToken cancellationToken) => RunCoreAsync(cancellationToken);

    /// <summary>Cuts a <see cref="ConnectionState.Waiting"/> delay short.</summary>
    internal void RetryNow() => _wake.Set();

    /// <summary>Completes when every event raised so far has been delivered.</summary>
    internal Task FlushEventsAsync() => _events.DrainAsync();

    internal async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("This companion client is already running.");
        }

        try
        {
            await RunLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _events.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A subscriber that never returns must not keep RunAsync from completing.
            }

            Volatile.Write(ref _running, 0);
        }
    }

    internal void RaiseHandlerFaulted(HandlerFault kind, string contributionId, string? sessionId, string? requestId, Exception? exception)
    {
        var args = new HandlerFaultedEventArgs(kind, contributionId, sessionId, requestId, exception);
        _events.Post(() => Raise(HandlerFaulted, args));
    }

    internal void ReportConnected(int attempt, bool serverVerified, HostIdentity host, int version) =>
        SetState(ConnectionState.Connected, null, null, attempt, serverVerified, host, version, null);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_pairing.ExtensionId, _manifest.Id, StringComparison.Ordinal))
        {
            SetState(ConnectionState.Stopped, ReasonCode.PairingExtensionMismatch, null, 1, false, null, null, null);
            return;
        }

        if (!_manifest.Hosts.Contains(_pairing.HostId, StringComparer.Ordinal))
        {
            SetState(ConnectionState.Stopped, ReasonCode.PairingHostNotListed, null, 1, false, null, null, null);
            return;
        }

        var policy = new RetryPolicy(_options, _internals.Random);
        var transport = _internals.Transport ?? (OperatingSystem.IsWindows()
            ? new PipeTransport(_pairing.PipeName)
            : throw new PlatformNotSupportedException("The companion transport is a Windows named pipe."));
        var attempt = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(ConnectionState.Connecting, null, null, attempt, false, null, null, null);
            ConnectionOutcome outcome;
            try
            {
                outcome = await ConnectOnceAsync(transport, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (outcome.ConnectedFor >= _options.StableConnection)
            {
                policy.Reset();
                attempt = 1;
            }

            var decision = policy.Next(outcome.Reason, outcome.RetryAfterMs, outcome.ServerVerified);
            if (decision.Kind == RetryKind.Stop)
            {
                SetState(ConnectionState.Stopped, outcome.Reason, null, attempt, outcome.ServerVerified, outcome.Host, outcome.Version,
                    outcome.PeerMessage);
                return;
            }

            if (decision.Kind == RetryKind.Wait)
            {
                // The delay starts before Waiting is raised, so RetryIn counts from a timer that already runs.
                var wait = _wake.WaitAsync(decision.Delay, _options.TimeProvider, cancellationToken);
                SetState(ConnectionState.Waiting, outcome.Reason, decision.Delay, attempt, outcome.ServerVerified, outcome.Host,
                    outcome.Version, outcome.PeerMessage);
                try
                {
                    await wait.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if ((outcome.Reason == ReasonCode.ManifestMismatch || outcome.Reason == ReasonCode.AuthIdentityChanged)
                    && _internals.ReloadManifest is { } reload)
                {
                    ExtensionManifest? changed;
                    try
                    {
                        changed = await reload(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // A stop or a restart that lands while extension.json is re-read.
                        break;
                    }
                    catch (Exception)
                    {
                        // A manifest that cannot be re-read is no change; the next attempt uses the current one.
                        changed = null;
                    }

                    if (changed is not null)
                    {
                        ReplaceManifest(changed);
                    }
                }
            }

            attempt++;
        }

        SetState(ConnectionState.Stopped, null, null, attempt, false, null, null, null);
    }

    private async Task<ConnectionOutcome> ConnectOnceAsync(ICompanionTransport transport, int attempt, CancellationToken cancellationToken)
    {
        var connected = await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (connected.Stream is not { } stream)
        {
            return new ConnectionOutcome { Reason = connected.Failure };
        }

        await using (stream.ConfigureAwait(false))
        {
            using var connection = new CompanionConnection(this, stream, connected.ServerVerified, _pairing, _manifest, _manifestHash, attempt);
            return await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ReplaceManifest(ExtensionManifest manifest)
    {
        try
        {
            var snapshot = Snapshot(manifest, nameof(manifest));
            if (string.Equals(snapshot.Id, _manifest.Id, StringComparison.Ordinal))
            {
                _manifest = snapshot;
                _manifestHash = ManifestWriter.ComputeHash(snapshot);
            }
        }
        catch (ArgumentException)
        {
            // An invalid replacement leaves the current manifest in place.
        }
    }

    private void SetState(ConnectionState state, ReasonCode? reason, TimeSpan? retryIn, int attempt, bool serverVerified,
        HostIdentity? host, int? version, string? peerMessage)
    {
        Volatile.Write(ref _state, (int)state);
        var args = new StatusChangedEventArgs(state, reason, retryIn, attempt, serverVerified, host, version, peerMessage);
        _events.Post(() => Raise(StatusChanged, args));
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
        {
            return;
        }

        List<Exception>? errors = null;
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)subscriber)(this, args);
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }

        foreach (var error in errors ?? [])
        {
            SubscriberFaulted?.Invoke(error);
        }
    }

    /// <summary>A validated, independent copy of a manifest, so the caller's later changes never reach the running client.</summary>
    private static ExtensionManifest Snapshot(ExtensionManifest manifest, string parameterName)
    {
        var validated = ManifestReader.Validate(manifest);
        if (!validated.Succeeded)
        {
            var codes = string.Join(", ", validated.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.Code)
                .Distinct(StringComparer.Ordinal));
            throw new ArgumentException("The manifest is invalid: " + codes + ".", parameterName);
        }

        var copy = ManifestReader.Read(ManifestWriter.Write(manifest, indented: false));
        return copy.Value ?? throw new ArgumentException("The manifest is invalid.", parameterName);
    }
}

/// <summary>How one connection attempt ended.</summary>
internal sealed class ConnectionOutcome
{
    public ReasonCode? Reason { get; init; }

    public int? RetryAfterMs { get; init; }

    public bool ServerVerified { get; init; }

    public TimeSpan ConnectedFor { get; init; }

    public HostIdentity? Host { get; init; }

    public int? Version { get; init; }

    public string? PeerMessage { get; init; }
}
