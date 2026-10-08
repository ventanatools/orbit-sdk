// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using VentanaTools.Orbit.Extensions.Wire;

namespace VentanaTools.Orbit.Extensions.Testing;

/// <summary>
/// The host side of one in-memory connection of a <see cref="CompanionTestHost"/>: the host's
/// half of the handshake (contract §7.3), then a reader that records faces and results and answers
/// pings. Frames are validated with the same <see cref="MessageReader"/> a host uses.
/// </summary>
internal sealed class HostConnection : IDisposable
{
    private const int HostSendRate = 256;
    private const int HostSendBurst = 512;
    private readonly CompanionTestHost _owner;
    private readonly Stream _stream;
    private readonly byte[] _secret;
    private readonly string _registrationId;
    private readonly string _manifestHash;
    private readonly CompanionTestHostOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TokenBucket _sendBudget;
    private readonly CancellationTokenSource _closed = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, TestHostSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TestInvocation> _invocations = new(StringComparer.Ordinal);
    private Task _run = Task.CompletedTask;
    private volatile bool _open;

    public HostConnection(CompanionTestHost owner, Stream stream, byte[] secret, string registrationId, string manifestHash,
        CompanionTestHostOptions options)
    {
        _owner = owner;
        _stream = stream;
        _secret = secret;
        _registrationId = registrationId;
        _manifestHash = manifestHash;
        _options = options;
        _time = owner.Time;
        _sendBudget = new TokenBucket(HostSendRate, HostSendBurst, _time);
    }

    public bool IsOpen => _open;

    public void Dispose()
    {
        _closed.Dispose();
        _writeLock.Dispose();
    }

    public void Start() => _run = Task.Run(RunAsync, CancellationToken.None);

    public void Register(TestHostSession session)
    {
        lock (_gate)
        {
            _sessions[session.SessionId] = session;
        }
    }

    public void StartInvoke(TestHostSession session, TestInvocation invocation)
    {
        invocation.Connection = this;
        lock (_gate)
        {
            _invocations[invocation.RequestId] = invocation;
        }

        invocation.Deadline = _time.CreateTimer(static state =>
        {
            var (connection, call) = ((HostConnection, TestInvocation))state!;
            _ = connection.TimeOutAsync(call);
        }, (this, invocation), TimeSpan.FromMilliseconds(_options.Limits.InvokeTimeoutMs), Timeout.InfiniteTimeSpan);
        _ = SendInvokeAsync(session, invocation);
    }

    public async Task CancelAsync(TestInvocation invocation)
    {
        lock (_gate)
        {
            _invocations.Remove(invocation.RequestId);
        }

        if (invocation.Complete(new TestInvokeOutcome { Kind = TestInvokeOutcomeKind.Cancelled }))
        {
            await SendAsync(new CancelMessage { RequestId = invocation.RequestId }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task SendAsync(WireMessage message, CancellationToken cancellationToken)
    {
        var bytes = MessageWriter.Write(message);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (!_sendBudget.TryTake(TokenBucket.CostOf(bytes.Length)))
            {
                await Task.Delay(_sendBudget.RetryAfter, _time, cancellationToken).ConfigureAwait(false);
            }

            _owner.Record(Sender.Host, message);
            try
            {
                await Framing.WriteFrameAsync(_stream, bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                throw new InvalidOperationException("The companion is not connected.", error);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CloseAsync(ReasonCode? code)
    {
        if (code is { } reason && _open)
        {
            try
            {
                await SendAsync(new ErrorMessage { Code = reason }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Already closed.
            }
        }

        Shutdown();
        try
        {
            await _run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Closed.
        }
    }

    private async Task SendInvokeAsync(TestHostSession session, TestInvocation invocation)
    {
        try
        {
            await SendAsync(new InvokeMessage { RequestId = invocation.RequestId, SessionId = session.SessionId }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            invocation.Fail(error);
        }
    }

    private async Task TimeOutAsync(TestInvocation invocation)
    {
        lock (_gate)
        {
            _invocations.Remove(invocation.RequestId);
        }

        if (invocation.Complete(new TestInvokeOutcome { Kind = TestInvokeOutcomeKind.TimedOut }))
        {
            try
            {
                await SendAsync(new CancelMessage { RequestId = invocation.RequestId }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The connection is gone; nothing to cancel.
            }
        }
    }

    private void Shutdown()
    {
        _open = false;
        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already shut down.
        }

        _stream.Dispose();
        List<TestInvocation> pending;
        lock (_gate)
        {
            pending = [.. _invocations.Values];
            _invocations.Clear();
        }

        foreach (var invocation in pending)
        {
            invocation.Fail(new InvalidOperationException("The companion disconnected before answering."));
        }
    }

    private async Task RunAsync()
    {
        try
        {
            if (await HandshakeAsync().ConfigureAwait(false))
            {
                _open = true;
                await ReadLoopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection ended.
        }
        finally
        {
            Shutdown();
        }
    }

    private async Task<bool> HandshakeAsync()
    {
        var token = _closed.Token;
        if (await ReadAsync(ConnectionPhase.Handshake, Framing.MaxHandshakeFrameBytes, token).ConfigureAwait(false) is not HelloMessage hello)
        {
            return false;
        }

        if (!string.Equals(hello.RegistrationId, _registrationId, StringComparison.Ordinal))
        {
            await RefuseAsync(ReasonCode.AuthRegistrationMismatch, null).ConfigureAwait(false);
            return false;
        }

        if (Handshake.Negotiate(hello.MinVersion, hello.MaxVersion, ProtocolVersions.Min, ProtocolVersions.Max) is not { } version)
        {
            await RefuseAsync(ReasonCode.ProtocolVersionUnsupported,
                new VersionRange { MinVersion = ProtocolVersions.Min, MaxVersion = ProtocolVersions.Max }).ConfigureAwait(false);
            return false;
        }

        var host = new HostIdentity { Id = _owner.HostId, Version = "1.0.0" };
        var capabilities = (_options.Capabilities ?? []).ToArray();
        var transcript = new HandshakeTranscript
        {
            HostId = host.Id,
            HostVersion = host.Version,
            RegistrationId = hello.RegistrationId,
            ClientNonce = hello.ClientNonce,
            ServerNonce = Handshake.NewNonce(),
            Version = version,
            MinVersion = hello.MinVersion,
            MaxVersion = hello.MaxVersion,
            ClientCapabilities = hello.Capabilities,
            HostCapabilities = capabilities,
            ManifestHash = hello.ManifestHash,
        };
        await SendAsync(new ChallengeMessage
        {
            ServerNonce = transcript.ServerNonce,
            Version = version,
            Capabilities = capabilities,
            Host = host,
            Proof = Handshake.ComputeProof(_secret, transcript, ProofRole.Server),
        }, token).ConfigureAwait(false);

        if (await ReadAsync(ConnectionPhase.Handshake, Framing.MaxHandshakeFrameBytes, token).ConfigureAwait(false) is not AuthenticateMessage authenticate)
        {
            return false;
        }

        if (!Handshake.VerifyProof(_secret, authenticate.Proof, transcript, ProofRole.Client))
        {
            await RefuseAsync(ReasonCode.AuthProofInvalid, null).ConfigureAwait(false);
            return false;
        }

        if (!string.Equals(hello.ManifestHash, _manifestHash, StringComparison.Ordinal))
        {
            await RefuseAsync(ReasonCode.ManifestMismatch, null).ConfigureAwait(false);
            return false;
        }

        await SendAsync(new ReadyMessage
        {
            Version = version,
            Capabilities = capabilities,
            Host = host,
            UiLanguage = _options.UiLanguage,
            Limits = _options.Limits,
        }, token).ConfigureAwait(false);
        return true;
    }

    private async Task RefuseAsync(ReasonCode code, VersionRange? supported)
    {
        try
        {
            await SendAsync(new ErrorMessage { Code = code, Supported = supported }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Already closed.
        }
    }

    private async Task<WireMessage?> ReadAsync(ConnectionPhase phase, int maxFrameBytes, CancellationToken token)
    {
        while (true)
        {
            byte[]? frame;
            try
            {
                frame = await Framing.ReadFrameAsync(_stream, maxFrameBytes, _time, token).ConfigureAwait(false);
            }
            catch (FrameException error)
            {
                await RefuseAsync(error.Code, null).ConfigureAwait(false);
                return null;
            }

            if (frame is null)
            {
                return null;
            }

            var read = MessageReader.Read(frame, Sender.Companion, phase);
            if (read.Violation is { } violation)
            {
                await RefuseAsync(violation, null).ConfigureAwait(false);
                return null;
            }

            if (read.Message is { } message)
            {
                _owner.Record(Sender.Companion, message);
                return message;
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        var token = _closed.Token;
        while (await ReadAsync(ConnectionPhase.Authenticated, _options.Limits.MaxFrameBytes, token).ConfigureAwait(false) is { } message)
        {
            switch (message)
            {
                case SetFaceMessage face:
                    SessionFor(face.SessionId)?.AddFace(new ReceivedFace { Face = FaceRules.ToFace(face.Face, renew: false), ArrivedAt = _owner.Now });
                    break;
                case SessionRefusedMessage refused:
                    if (SessionFor(refused.SessionId) is { } session)
                    {
                        session.RefusedCode = refused.Code;
                        session.Refusal.TrySetResult();
                    }

                    break;
                case ResultMessage result:
                    Answer(result);
                    break;
                case PingMessage ping:
                    await SendAsync(new PongMessage { Id = ping.Id }, token).ConfigureAwait(false);
                    break;
                case ErrorMessage:
                    return;
            }
        }
    }

    private TestHostSession? SessionFor(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.GetValueOrDefault(sessionId);
        }
    }

    private void Answer(ResultMessage result)
    {
        TestInvocation? invocation;
        lock (_gate)
        {
            _invocations.Remove(result.RequestId, out invocation);
        }

        invocation?.Complete(new TestInvokeOutcome
        {
            Kind = result.Outcome switch
            {
                Outcome.Done => TestInvokeOutcomeKind.Done,
                Outcome.Refused => TestInvokeOutcomeKind.Refused,
                Outcome.Unsupported => TestInvokeOutcomeKind.Unsupported,
                _ => TestInvokeOutcomeKind.Failed,
            },
            Failure = result.Failure,
        });
    }
}
