// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using VentanaTools.Orbit.Extensions.Wire;

namespace VentanaTools.Orbit.Extensions;

/// <summary>
/// One authenticated connection (contract §7): the companion side of the handshake, then the
/// pipe reader, the paced writer, sessions, invocations, liveness and rate limits. Author code
/// never runs on the reader, and nothing an author does can make the SDK send a frame the host
/// would close on.
/// </summary>
internal sealed class CompanionConnection : ISessionSink, IDisposable
{
    private const int ReplayCapacity = 1_024;
    private const int HostFrameRate = 512;
    private const int HostFrameBurst = 1_024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorFlushTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRenewal = TimeSpan.FromDays(1);

    private readonly CompanionClient _client;
    private readonly Stream _stream;
    private readonly bool _osVerified;
    private readonly Pairing _pairing;
    private readonly ExtensionManifest _manifest;
    private readonly string _manifestHash;
    private readonly int _attempt;
    private readonly TimeProvider _time;
    private readonly IClientObserver? _observer;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _abort = new();
    private readonly TaskCompletionSource _writerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TokenBucket _hostFrames;
    private readonly AsyncSignal _writeSignal = new();
    private readonly Queue<OutboundFrame> _control = new();
    private readonly LinkedList<SessionEntry> _facePending = new();
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InvokeEntry> _invokes = new(StringComparer.Ordinal);
    private readonly ReplayWindow _endedSessions = new(ReplayCapacity);
    private readonly ReplayWindow _completedRequests = new(ReplayCapacity);

    private bool _serverVerified;
    private HostIdentity? _host;
    private int? _version;
    private HostLimits _limits = HostLimits.Protocol3Defaults;
    private int _readyPingIntervalMs = HostLimits.Protocol3Defaults.PingIntervalMs;
    private IReadOnlyCollection<string> _effective = [];
    private string _uiLanguage = "en-US";
    private TokenBucket? _facePacing;
    private TimeSpan _faceSpacing = TimeSpan.FromMilliseconds(500);
    private long _faceHoldUntil;
    private ITimer? _pingTimer;
    private ITimer? _pongTimer;
    private int _nextPingId;
    private int _outstandingPing;
    private bool _pongOwed;
    private long _lastHostPing;
    private bool _hostPinged;
    private bool _closing;
    private bool _writerRunning;
    private ReasonCode? _reason;
    private int? _retryAfterMs;
    private string? _peerMessage;
    private long _connectedAt = -1;

    public CompanionConnection(CompanionClient client, Stream stream, bool osVerified, Pairing pairing, ExtensionManifest manifest,
        string manifestHash, int attempt)
    {
        _client = client;
        _stream = stream;
        _osVerified = osVerified;
        _pairing = pairing;
        _manifest = manifest;
        _manifestHash = manifestHash;
        _attempt = attempt;
        _time = client.Time;
        _observer = client.Internals.Observer;
        _hostFrames = new TokenBucket(HostFrameRate, HostFrameBurst, _time);
    }

    public async Task<ConnectionOutcome> RunAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state => ((CompanionConnection)state!).Close(null, false), this);
        Task? writer = null;
        try
        {
            if (await HandshakeAsync().ConfigureAwait(false))
            {
                bool running;
                lock (_gate)
                {
                    running = _writerRunning = !_closing;
                    if (running)
                    {
                        _pingTimer = _time.CreateTimer(static state => ((CompanionConnection)state!).OnPingDue(), this,
                            TimeSpan.FromMilliseconds(_limits.PingIntervalMs), Timeout.InfiniteTimeSpan);
                    }
                }

                if (running)
                {
                    _client.ReportConnected(_attempt, _serverVerified, _host!, _version!.Value);
                    writer = Task.Run(WriteLoopAsync, CancellationToken.None);
                    await ReadLoopAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Close(null, false);
            if (writer is not null)
            {
                await writer.ConfigureAwait(false);
            }
        }

        return new ConnectionOutcome
        {
            Reason = _reason,
            RetryAfterMs = _retryAfterMs,
            ServerVerified = _serverVerified,
            ConnectedFor = _connectedAt >= 0 ? _time.GetElapsedTime(_connectedAt) : TimeSpan.Zero,
            Host = _host,
            Version = _version,
            PeerMessage = _peerMessage,
        };
    }

    public void Dispose() => _abort.Dispose();

    // ---------------------------------------------------------------- handshake (contract §7.3)

    private async Task<bool> HandshakeAsync()
    {
        _serverVerified = _osVerified;
        using var deadline = new CancellationTokenSource(HandshakeTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token, deadline.Token);
        var token = linked.Token;
        try
        {
            var offered = _client.Internals.OfferedCapabilities;
            var hello = new HelloMessage
            {
                MinVersion = ProtocolVersions.Min,
                MaxVersion = ProtocolVersions.Max,
                RegistrationId = _pairing.RegistrationId,
                ClientNonce = Handshake.NewNonce(),
                Capabilities = offered,
                Client = ClientIdentity.Info,
                ManifestHash = _manifestHash,
            };
            if (!await WriteDirectAsync(MessageWriter.Write(hello), token).ConfigureAwait(false))
            {
                return false;
            }

            var first = await ReadAsync(ConnectionPhase.Handshake, Framing.MaxHandshakeFrameBytes, token).ConfigureAwait(false);
            if (await RefusedAsync(first, token).ConfigureAwait(false))
            {
                return false;
            }

            if (first.Message is not ChallengeMessage challenge)
            {
                await FailHandshakeAsync(ReasonCode.ProtocolUnexpected, token).ConfigureAwait(false);
                return false;
            }

            if (challenge.Version < hello.MinVersion || challenge.Version > hello.MaxVersion
                || challenge.Version < ProtocolVersions.Min || challenge.Version > ProtocolVersions.Max)
            {
                await FailHandshakeAsync(ReasonCode.ProtocolVersionUnsupported, token,
                    new VersionRange { MinVersion = ProtocolVersions.Min, MaxVersion = ProtocolVersions.Max }).ConfigureAwait(false);
                return false;
            }

            if (!string.Equals(challenge.Host.Id, _pairing.HostId, StringComparison.Ordinal))
            {
                await FailHandshakeAsync(ReasonCode.AuthHostMismatch, token).ConfigureAwait(false);
                return false;
            }

            var transcript = new HandshakeTranscript
            {
                HostId = challenge.Host.Id,
                HostVersion = challenge.Host.Version,
                RegistrationId = hello.RegistrationId,
                ClientNonce = hello.ClientNonce,
                ServerNonce = challenge.ServerNonce,
                Version = challenge.Version,
                MinVersion = hello.MinVersion,
                MaxVersion = hello.MaxVersion,
                ClientCapabilities = hello.Capabilities,
                HostCapabilities = challenge.Capabilities,
                ManifestHash = hello.ManifestHash,
            };
            if (!_pairing.VerifyProof(challenge.Proof, transcript, ProofRole.Server))
            {
                await FailHandshakeAsync(ReasonCode.AuthServerProofInvalid, token).ConfigureAwait(false);
                return false;
            }

            lock (_gate)
            {
                _serverVerified = true;
                _host = challenge.Host;
                _version = challenge.Version;
            }

            var authenticate = new AuthenticateMessage { Proof = _pairing.ComputeProof(transcript, ProofRole.Client) };
            if (!await WriteDirectAsync(MessageWriter.Write(authenticate), token).ConfigureAwait(false))
            {
                return false;
            }

            var second = await ReadAsync(ConnectionPhase.PeerVerified, Framing.MaxHandshakeFrameBytes, token).ConfigureAwait(false);
            if (await RefusedAsync(second, token).ConfigureAwait(false))
            {
                return false;
            }

            if (second.Message is not ReadyMessage ready)
            {
                await FailHandshakeAsync(ReasonCode.ProtocolUnexpected, token).ConfigureAwait(false);
                return false;
            }

            if (ready.Version != challenge.Version
                || !ready.Capabilities.SequenceEqual(challenge.Capabilities, StringComparer.Ordinal)
                || !string.Equals(ready.Host.Id, challenge.Host.Id, StringComparison.Ordinal)
                || !string.Equals(ready.Host.Version, challenge.Host.Version, StringComparison.Ordinal)
                || !ready.Limits.IsWithinRanges())
            {
                await FailHandshakeAsync(ReasonCode.ProtocolMessageInvalid, token).ConfigureAwait(false);
                return false;
            }

            lock (_gate)
            {
                var hostSide = new HashSet<string>(challenge.Capabilities, StringComparer.Ordinal);
                _effective = offered.Where(hostSide.Contains).Distinct(StringComparer.Ordinal).ToArray();
                _limits = ready.Limits.ForCompanion();
                _readyPingIntervalMs = ready.Limits.PingIntervalMs;
                _uiLanguage = ready.UiLanguage;
                _facePacing = new TokenBucket(Math.Max(0.5, _limits.MessageRate / 2.0), Math.Max(8, _limits.MessageBurst / 2), _time);
                _faceSpacing = TimeSpan.FromSeconds(1.0 / _limits.FaceChangesPerSecond);
                _connectedAt = _time.GetTimestamp();
            }

            return true;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !_abort.IsCancellationRequested)
        {
            SetClosed(ReasonCode.AuthTimeout);
            return false;
        }
        catch (OperationCanceledException)
        {
            SetClosed(null);
            return false;
        }
    }

    /// <summary>Handles a handshake read that is not the expected message. Returns true when the attempt is over.</summary>
    private async Task<bool> RefusedAsync(ReadOutcome read, CancellationToken token)
    {
        switch (read)
        {
            case { Ended: true }:
                SetClosed(null);
                return true;
            case { Violation: { } violation }:
                await FailHandshakeAsync(violation, token).ConfigureAwait(false);
                return true;
            case { Message: ErrorMessage error }:
                RecordPeerError(error);
                SetClosed(error.Code);
                return true;
            case { Message: null }:
                await FailHandshakeAsync(ReasonCode.ProtocolUnexpected, token).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Ends the handshake: writes an error frame for a wire code (best effort, at most one second), never for a local code.</summary>
    private async Task FailHandshakeAsync(ReasonCode code, CancellationToken token, VersionRange? supported = null)
    {
        SetClosed(code);
        if (code.Info.Disposition == Disposition.Local)
        {
            return;
        }

        using var bound = new CancellationTokenSource(ErrorFlushTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, bound.Token);
        try
        {
            await Framing.WriteFrameAsync(_stream, MessageWriter.Write(new ErrorMessage { Code = code, Supported = supported }), linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The connection is closing anyway.
        }
    }

    private async Task<bool> WriteDirectAsync(byte[] frame, CancellationToken token)
    {
        using var bound = new CancellationTokenSource(WriteTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, bound.Token);
        try
        {
            await Framing.WriteFrameAsync(_stream, frame, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested && !token.IsCancellationRequested)
        {
            SetClosed(ReasonCode.FrameWriteTimeout);
            return false;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            SetClosed(null);
            return false;
        }
    }

    // ---------------------------------------------------------------- reading

    private async Task<ReadOutcome> ReadAsync(ConnectionPhase phase, int maxFrameBytes, CancellationToken token)
    {
        byte[]? frame;
        try
        {
            frame = await Framing.ReadFrameAsync(_stream, maxFrameBytes, _time, token).ConfigureAwait(false);
        }
        catch (FrameException error)
        {
            return new ReadOutcome { Violation = error.Code };
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            return new ReadOutcome { Ended = true };
        }

        if (frame is null)
        {
            return new ReadOutcome { Ended = true };
        }

        if (!_hostFrames.TryTake(TokenBucket.CostOf(frame.Length)))
        {
            return new ReadOutcome { Violation = ReasonCode.RateExceeded };
        }

        _pingTimer?.Change(TimeSpan.FromMilliseconds(_limits.PingIntervalMs), Timeout.InfiniteTimeSpan);
        var result = MessageReader.Read(frame, Sender.Host, phase);
        return new ReadOutcome { Message = result.Message, Violation = result.Violation, Ignored = result.Ignored is not null };
    }

    private async Task ReadLoopAsync()
    {
        while (true)
        {
            ReadOutcome read;
            try
            {
                read = await ReadAsync(ConnectionPhase.Authenticated, _limits.MaxFrameBytes, _abort.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (read.Ended)
            {
                Close(null, false);
                return;
            }

            if (read.Violation is { } violation)
            {
                Close(violation, true);
                return;
            }

            if (read.Message is { } message)
            {
                Dispatch(message);
                _observer?.FrameProcessed(message);
            }

            lock (_gate)
            {
                if (_closing)
                {
                    return;
                }
            }
        }
    }

    private void Dispatch(WireMessage message)
    {
        switch (message)
        {
            case StartSessionMessage start:
                StartSession(start);
                break;
            case StopSessionMessage stop:
                StopSession(stop.SessionId);
                break;
            case InvokeMessage invoke:
                Invoke(invoke);
                break;
            case CancelMessage cancel:
                Cancel(cancel.RequestId);
                break;
            case PingMessage ping:
                HostPing(ping.Id);
                break;
            case PongMessage pong:
                HostPong(pong.Id);
                break;
            case ErrorMessage error:
                HostError(error);
                break;
        }
    }

    // ---------------------------------------------------------------- sessions (contract §7.6)

    private void StartSession(StartSessionMessage message)
    {
        SessionEntry? entry = null;
        ReasonCode? refused = null;
        var replay = false;
        var capacityFault = false;
        lock (_gate)
        {
            if (_closing)
            {
                return;
            }

            if (_sessions.ContainsKey(message.SessionId) || _endedSessions.Contains(message.SessionId))
            {
                replay = true;
            }
            else
            {
                var code = SessionSettings.Check(_manifest, message.ContributionId, message.Settings);
                if (code is null && _sessions.Count >= _limits.MaxSessions)
                {
                    code = ReasonCode.SessionCapacity;
                }

                if (code is null && !_client.Tracker.TryEnterSession())
                {
                    code = ReasonCode.SessionCapacity;
                    capacityFault = true;
                }

                if (code is { } refusal)
                {
                    refused = refusal;
                    _endedSessions.Add(message.SessionId);
                    _control.Enqueue(new OutboundFrame(MessageWriter.Write(new SessionRefusedMessage { SessionId = message.SessionId, Code = refusal })));
                }
                else
                {
                    var contribution = _manifest.Contributions.First(item => string.Equals(item.Id, message.ContributionId, StringComparison.Ordinal));
                    var session = new Session(message.SessionId, contribution.Id, contribution.Provides, message.Settings, _uiLanguage, _effective,
                        _time, this);
                    entry = new SessionEntry(session);
                    _sessions.Add(session.Id, entry);
                }
            }
        }

        if (replay)
        {
            Close(ReasonCode.SessionReplay, true);
            return;
        }

        _writeSignal.Set();
        _observer?.SessionStarted(message.SessionId, refused);
        if (capacityFault)
        {
            _client.RaiseHandlerFaulted(HandlerFault.SessionCapacity, message.ContributionId, message.SessionId, null, null);
        }

        if (entry is not null)
        {
            _ = Task.Run(() => RunSessionHandlerAsync(entry), CancellationToken.None);
        }
    }

    private async Task RunSessionHandlerAsync(SessionEntry entry)
    {
        Exception? fault = null;
        try
        {
            await _client.Handler.RunSessionAsync(entry.Session, entry.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (entry.Token.IsCancellationRequested)
        {
            // A normal completion after the session ended.
        }
        catch (Exception error)
        {
            fault = error;
        }
        finally
        {
            lock (_gate)
            {
                entry.HandlerRunning = false;
            }

            entry.Ended.TrySetResult();
            _client.Tracker.ExitSession();
            _observer?.SessionHandlerEnded(entry.Session.Id);
        }

        if (fault is null)
        {
            return;
        }

        _client.RaiseHandlerFaulted(HandlerFault.Exception, entry.Session.ContributionId, entry.Session.Id, null, fault);
        if ((entry.Session.Provides & Provides.Face) != 0)
        {
            lock (_gate)
            {
                if (!Current(entry))
                {
                    return;
                }

                StopRenewal(entry);
                Enqueue(entry, FaceCommand.Clear);
            }

            _writeSignal.Set();
        }
    }

    private void StopSession(string sessionId)
    {
        SessionEntry? entry;
        List<InvokeEntry> calls;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out entry))
            {
                return;
            }

            Retire(entry);
            _endedSessions.Add(sessionId);
            calls = _invokes.Values.Where(call => ReferenceEquals(call.Session, entry)).ToList();
            foreach (var call in calls)
            {
                call.SessionStopped = true;
            }
        }

        CancelAndWatch(entry.Cancellation, entry.Ended.Task, entry.Session.ContributionId, entry.Session.Id, null);
        foreach (var call in calls)
        {
            CancelAndWatch(call.Cancellation, call.Ended.Task, call.Session.Session.ContributionId, call.Session.Session.Id, call.RequestId);
        }
    }

    /// <summary>Ends a session's publishing. Called under the gate.</summary>
    private void Retire(SessionEntry entry)
    {
        entry.Stopped = true;
        entry.Session.End();
        StopRenewal(entry);
        if (entry.Node is not null)
        {
            _facePending.Remove(entry.Node);
            entry.Node = null;
        }

        entry.Pending = null;
    }

    /// <summary>
    /// Cancels a handler's token off the reader and outside the gate, then raises
    /// <see cref="HandlerFault.IgnoredCancellation"/> when it is still running after the stop timeout.
    /// </summary>
    private void CancelAndWatch(CancellationTokenSource cancellation, Task ended, string contributionId, string sessionId, string? requestId)
    {
        if (ended.IsCompleted)
        {
            return;
        }

        var watch = ended.WaitAsync(_client.Options.HandlerStopTimeout, _time);
        _ = Task.Run(async () =>
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (AggregateException)
            {
                // An author's cancellation callback threw; the handler still has to end.
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }, CancellationToken.None);
        _ = Task.Run(async () =>
        {
            try
            {
                await watch.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _client.RaiseHandlerFaulted(HandlerFault.IgnoredCancellation, contributionId, sessionId, requestId, null);
            }
        }, CancellationToken.None);
    }

    // ---------------------------------------------------------------- invocations (contract §7.8)

    private void Invoke(InvokeMessage message)
    {
        InvokeEntry? entry = null;
        var replay = false;
        lock (_gate)
        {
            if (_closing)
            {
                return;
            }

            if (_invokes.ContainsKey(message.RequestId) || _completedRequests.Contains(message.RequestId))
            {
                replay = true;
            }
            else if (!_sessions.TryGetValue(message.SessionId, out var session) || (session.Session.Provides & Provides.Invoke) == 0
                || _invokes.Count >= _limits.MaxPendingInvokes || !_client.Tracker.TryEnterInvocation())
            {
                _completedRequests.Add(message.RequestId);
                _control.Enqueue(new OutboundFrame(MessageWriter.Write(new ResultMessage { RequestId = message.RequestId, Outcome = Outcome.Refused })));
            }
            else
            {
                entry = new InvokeEntry(message.RequestId, session);
                _invokes.Add(message.RequestId, entry);
            }
        }

        if (replay)
        {
            Close(ReasonCode.InvokeReplay, true);
            return;
        }

        _writeSignal.Set();
        if (entry is null)
        {
            return;
        }

        var deadline = TimeSpan.FromMilliseconds(Math.Max(1_000, _limits.InvokeTimeoutMs - 3_000));
        entry.Deadline = _time.CreateTimer(static state =>
        {
            var (connection, call) = ((CompanionConnection, InvokeEntry))state!;
            connection.OnDeadline(call);
        }, (this, entry), deadline, Timeout.InfiniteTimeSpan);
        _ = Task.Run(() => RunInvocationAsync(entry), CancellationToken.None);
    }

    private async Task RunInvocationAsync(InvokeEntry entry)
    {
        var result = default(InvokeResult);
        Exception? fault = null;
        var cancelled = false;
        try
        {
            result = await _client.Handler.InvokeAsync(entry.Invocation, entry.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (entry.Token.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception error)
        {
            fault = error;
        }
        finally
        {
            entry.Deadline?.Dispose();
            entry.Ended.TrySetResult();
            _client.Tracker.ExitInvocation();
            _observer?.InvocationHandlerEnded(entry.RequestId);
        }

        var kind = fault is not null ? HandlerFault.Exception : !cancelled && !result.IsValid ? HandlerFault.InvalidResult : (HandlerFault?)null;
        var sent = false;
        lock (_gate)
        {
            if (!entry.Settled)
            {
                Settle(entry);
                if (!_closing && !entry.HostCancelled)
                {
                    ResultMessage? answer = kind is not null
                        ? new ResultMessage { RequestId = entry.RequestId, Outcome = Outcome.Failed }
                        : cancelled
                            ? entry.SessionStopped ? new ResultMessage { RequestId = entry.RequestId, Outcome = Outcome.Refused } : null
                            : new ResultMessage
                            {
                                RequestId = entry.RequestId,
                                Outcome = result.Outcome,
                                Failure = result.Outcome == Outcome.Failed ? result.Failure : null,
                            };
                    if (answer is not null)
                    {
                        _control.Enqueue(new OutboundFrame(MessageWriter.Write(answer)));
                        sent = true;
                    }
                }
            }
        }

        if (sent)
        {
            _writeSignal.Set();
        }

        if (kind is { } faultKind)
        {
            _client.RaiseHandlerFaulted(faultKind, entry.Session.Session.ContributionId, entry.Session.Session.Id, entry.RequestId, fault);
        }
    }

    private void OnDeadline(InvokeEntry entry)
    {
        lock (_gate)
        {
            if (entry.Settled)
            {
                return;
            }

            Settle(entry);
            if (!_closing)
            {
                _control.Enqueue(new OutboundFrame(MessageWriter.Write(new ResultMessage { RequestId = entry.RequestId, Outcome = Outcome.Failed })));
            }
        }

        _writeSignal.Set();
        _observer?.InvocationDeadlineReached(entry.RequestId);
        CancelAndWatch(entry.Cancellation, entry.Ended.Task, entry.Session.Session.ContributionId, entry.Session.Session.Id, entry.RequestId);
    }

    private void Cancel(string requestId)
    {
        InvokeEntry? entry;
        lock (_gate)
        {
            if (!_invokes.TryGetValue(requestId, out entry) || entry.Settled)
            {
                return;
            }

            entry.HostCancelled = true;
            Settle(entry);
        }

        CancelAndWatch(entry.Cancellation, entry.Ended.Task, entry.Session.Session.ContributionId, entry.Session.Session.Id, requestId);
    }

    /// <summary>The request is answered or abandoned: it leaves the pending set for the replay window. Called under the gate.</summary>
    private void Settle(InvokeEntry entry)
    {
        entry.Settled = true;
        _invokes.Remove(entry.RequestId);
        _completedRequests.Add(entry.RequestId);
    }

    // ---------------------------------------------------------------- faces (contract §7.7, §9.2)

    public PublishResult Publish(Session session, FaceCommand command)
    {
        lock (_gate)
        {
            if (_closing || !_sessions.TryGetValue(session.Id, out var entry) || !ReferenceEquals(entry.Session, session) || entry.Stopped)
            {
                _observer?.PublishedAfterEnd(session.Id);
                return PublishResult.SessionEnded;
            }

            StopRenewal(entry);
            Enqueue(entry, command);
            if (command is { Kind: FaceCommandKind.Set, Renew: true, Face: { } face })
            {
                StartRenewal(entry, command, face.GoodForSeconds);
            }
        }

        _writeSignal.Set();
        return PublishResult.Accepted;
    }

    public void PublishedAfterEnd(Session session, FaceCommand command) => _observer?.PublishedAfterEnd(session.Id);

    public void FaceWithoutProvides(Session session) => _observer?.FaceWithoutProvides(session.Id);

    /// <summary>Makes <paramref name="command"/> the session's one pending face command. Called under the gate.</summary>
    private void Enqueue(SessionEntry entry, FaceCommand command)
    {
        WireMessage message = command.Kind switch
        {
            FaceCommandKind.Set => new SetFaceMessage { SessionId = entry.Session.Id, Face = command.Face! },
            FaceCommandKind.Clear => new ClearFaceMessage { SessionId = entry.Session.Id },
            _ => new FailMessage { SessionId = entry.Session.Id, Failure = command.Failure!.Value },
        };
        entry.Pending = MessageWriter.Write(message);
        entry.Node ??= _facePending.AddLast(entry);
    }

    private bool Current(SessionEntry entry) =>
        !_closing && !entry.Stopped && _sessions.TryGetValue(entry.Session.Id, out var current) && ReferenceEquals(current, entry);

    private void StartRenewal(SessionEntry entry, FaceCommand command, int goodForSeconds)
    {
        var generation = entry.Generation;
        var interval = TimeSpan.FromTicks(goodForSeconds * TimeSpan.TicksPerSecond * 4 / 5);
        var publishedAt = _time.GetTimestamp();
        entry.Renewal = _time.CreateTimer(static state =>
        {
            var (connection, session, face, version, at) = ((CompanionConnection, SessionEntry, FaceCommand, int, long))state!;
            connection.Renew(session, face, version, at);
        }, (this, entry, command, generation, publishedAt), interval, interval);
    }

    /// <summary>Ends any renewal and makes every older renewal timer a no-op. Called under the gate.</summary>
    private static void StopRenewal(SessionEntry entry)
    {
        entry.Generation++;
        entry.Renewal?.Dispose();
        entry.Renewal = null;
    }

    private void Renew(SessionEntry entry, FaceCommand command, int generation, long publishedAt)
    {
        lock (_gate)
        {
            if (entry.Generation != generation)
            {
                return;
            }

            if (!Current(entry) || !entry.HandlerRunning || entry.Token.IsCancellationRequested
                || _time.GetElapsedTime(publishedAt) > MaxRenewal)
            {
                StopRenewal(entry);
                return;
            }

            Enqueue(entry, command);
        }

        _writeSignal.Set();
    }

    // ---------------------------------------------------------------- liveness (contract §7.9)

    private void HostPing(int id)
    {
        var violation = false;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_pongOwed || (_hostPinged && _time.GetElapsedTime(_lastHostPing, now) < TimeSpan.FromMilliseconds(_readyPingIntervalMs / 2.0)))
            {
                violation = true;
            }
            else
            {
                _hostPinged = true;
                _lastHostPing = now;
                _pongOwed = true;
                _control.Enqueue(new OutboundFrame(MessageWriter.Write(new PongMessage { Id = id }), PongWritten));
            }
        }

        if (violation)
        {
            Close(ReasonCode.ProtocolUnexpected, true);
            return;
        }

        _writeSignal.Set();
    }

    private void PongWritten()
    {
        lock (_gate)
        {
            _pongOwed = false;
        }
    }

    private void HostPong(int id)
    {
        lock (_gate)
        {
            if (_outstandingPing != 0 && id == _outstandingPing)
            {
                _outstandingPing = 0;
                _pongTimer?.Dispose();
                _pongTimer = null;
            }

            // Otherwise protocol.pong-unknown: ignored.
        }
    }

    private void OnPingDue()
    {
        lock (_gate)
        {
            if (_closing || _outstandingPing != 0)
            {
                return;
            }

            _nextPingId = _nextPingId == int.MaxValue ? 1 : _nextPingId + 1;
            var id = _nextPingId;
            _outstandingPing = id;
            _control.Enqueue(new OutboundFrame(MessageWriter.Write(new PingMessage { Id = id })));
            _pongTimer = _time.CreateTimer(static state =>
            {
                var (connection, ping) = ((CompanionConnection, int))state!;
                connection.OnPongTimeout(ping);
            }, (this, id), TimeSpan.FromMilliseconds(_limits.PongTimeoutMs), Timeout.InfiniteTimeSpan);
        }

        _writeSignal.Set();
    }

    private void OnPongTimeout(int id)
    {
        lock (_gate)
        {
            if (_outstandingPing != id)
            {
                return;
            }
        }

        Close(ReasonCode.ProtocolPingTimeout, true);
    }

    private void HostError(ErrorMessage error)
    {
        if (error.Code.Info.Disposition == Disposition.Advisory)
        {
            if (error.RetryAfterMs is { } wait)
            {
                lock (_gate)
                {
                    _faceHoldUntil = _time.GetTimestamp() + (long)(wait / 1000.0 * _time.TimestampFrequency);
                }
            }

            return;
        }

        RecordPeerError(error);
        Close(error.Code, false);
    }

    private void RecordPeerError(ErrorMessage error)
    {
        lock (_gate)
        {
            _retryAfterMs = error.RetryAfterMs;
            _peerMessage = error.Message;
        }
    }

    // ---------------------------------------------------------------- writing

    private async Task WriteLoopAsync()
    {
        try
        {
            while (true)
            {
                OutboundFrame? frame = null;
                TimeSpan? wait = null;
                lock (_gate)
                {
                    if (_control.Count > 0)
                    {
                        frame = _control.Dequeue();
                    }
                    else if (_closing)
                    {
                        return;
                    }
                    else
                    {
                        frame = TakeFace(out wait);
                    }
                }

                if (frame is { } next)
                {
                    if (!await WriteFrameAsync(next.Bytes).ConfigureAwait(false))
                    {
                        return;
                    }

                    next.Written?.Invoke();
                    continue;
                }

                await _writeSignal.WaitAsync(wait, _time, _abort.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Aborted.
        }
        catch (ObjectDisposedException)
        {
            // Aborted.
        }
        finally
        {
            _writerDone.TrySetResult();
        }
    }

    /// <summary>The next face frame the pacing allows, or the time until one may go. Called under the gate.</summary>
    private OutboundFrame? TakeFace(out TimeSpan? wait)
    {
        wait = null;
        if (_facePending.Count == 0 || _facePacing is null)
        {
            return null;
        }

        var now = _time.GetTimestamp();
        if (_faceHoldUntil > now)
        {
            wait = _time.GetElapsedTime(now, _faceHoldUntil);
            return null;
        }

        for (var node = _facePending.First; node is not null; node = node.Next)
        {
            var entry = node.Value;
            if (entry.NextFaceAt > now)
            {
                var until = _time.GetElapsedTime(now, entry.NextFaceAt);
                wait = wait is { } earlier && earlier < until ? earlier : until;
                continue;
            }

            var bytes = entry.Pending!;
            if (!_facePacing.TryTake(TokenBucket.CostOf(bytes.Length)))
            {
                wait = _facePacing.RetryAfter;
                return null;
            }

            _facePending.Remove(node);
            entry.Node = null;
            entry.Pending = null;
            entry.NextFaceAt = now + (long)(_faceSpacing.TotalSeconds * _time.TimestampFrequency);
            return new OutboundFrame(bytes);
        }

        return null;
    }

    private async Task<bool> WriteFrameAsync(byte[] frame)
    {
        using var bound = new CancellationTokenSource(WriteTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token, bound.Token);
        try
        {
            await Framing.WriteFrameAsync(_stream, frame, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested && !_abort.IsCancellationRequested)
        {
            Close(ReasonCode.FrameWriteTimeout, false);
            return false;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            Close(null, false);
            return false;
        }
    }

    // ---------------------------------------------------------------- closing

    /// <summary>Records why the handshake ended; the connection then closes.</summary>
    private void SetClosed(ReasonCode? reason)
    {
        lock (_gate)
        {
            if (!_closing)
            {
                _closing = true;
                _reason = reason;
            }
        }
    }

    /// <summary>
    /// Closes the connection with <paramref name="reason"/>: retires every session and
    /// invocation, and, when <paramref name="sendError"/> and the code goes on the wire, writes an
    /// <c>error</c> frame first (at most one second). Only the first call counts.
    /// </summary>
    private void Close(ReasonCode? reason, bool sendError)
    {
        List<SessionEntry> sessions;
        List<InvokeEntry> calls;
        var flush = false;
        lock (_gate)
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            _reason = reason;
            if (sendError && _writerRunning && reason is { } code && code.Info.Disposition != Disposition.Local)
            {
                _control.Enqueue(new OutboundFrame(MessageWriter.Write(new ErrorMessage { Code = code })));
                flush = true;
            }

            sessions = _sessions.Values.ToList();
            _sessions.Clear();
            foreach (var entry in sessions)
            {
                Retire(entry);
            }

            calls = _invokes.Values.ToList();
            _invokes.Clear();
            foreach (var call in calls)
            {
                call.Settled = true;
            }

            _facePending.Clear();
            _pingTimer?.Dispose();
            _pingTimer = null;
            _pongTimer?.Dispose();
            _pongTimer = null;
        }

        foreach (var entry in sessions)
        {
            CancelAndWatch(entry.Cancellation, entry.Ended.Task, entry.Session.ContributionId, entry.Session.Id, null);
        }

        foreach (var call in calls)
        {
            CancelAndWatch(call.Cancellation, call.Ended.Task, call.Session.Session.ContributionId, call.Session.Session.Id, call.RequestId);
        }

        _writeSignal.Set();
        if (flush)
        {
            _ = AbortAfterFlushAsync();
        }
        else
        {
            Abort();
        }
    }

    private async Task AbortAfterFlushAsync()
    {
        try
        {
            await _writerDone.Task.WaitAsync(ErrorFlushTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The peer stopped reading; close anyway.
        }

        Abort();
    }

    private void Abort()
    {
        try
        {
            _abort.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
        catch (AggregateException)
        {
            // A registration threw; the connection is closed regardless.
        }
    }

    // ---------------------------------------------------------------- types

    private readonly record struct OutboundFrame(byte[] Bytes, Action? Written = null);

    private readonly record struct ReadOutcome
    {
        public WireMessage? Message { get; init; }

        public ReasonCode? Violation { get; init; }

        public bool Ignored { get; init; }

        public bool Ended { get; init; }
    }

    private sealed class SessionEntry
    {
        public SessionEntry(Session session)
        {
            Session = session;
            Token = Cancellation.Token;
        }

        public Session Session { get; }

        // Author tokens are not linked to the reader's: a linked token's callbacks would run on the cancelling thread.
        public CancellationTokenSource Cancellation { get; } = new();

        public CancellationToken Token { get; }

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HandlerRunning { get; set; } = true;

        public bool Stopped { get; set; }

        public byte[]? Pending { get; set; }

        public LinkedListNode<SessionEntry>? Node { get; set; }

        public long NextFaceAt { get; set; }

        public int Generation { get; set; }

        public ITimer? Renewal { get; set; }
    }

    private sealed class InvokeEntry
    {
        public InvokeEntry(string requestId, SessionEntry session)
        {
            RequestId = requestId;
            Session = session;
            Invocation = new Invocation(requestId, session.Session);
            Token = Cancellation.Token;
        }

        public string RequestId { get; }

        public SessionEntry Session { get; }

        public Invocation Invocation { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public CancellationToken Token { get; }

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ITimer? Deadline { get; set; }

        public bool Settled { get; set; }

        public bool HostCancelled { get; set; }

        public bool SessionStopped { get; set; }
    }
}
