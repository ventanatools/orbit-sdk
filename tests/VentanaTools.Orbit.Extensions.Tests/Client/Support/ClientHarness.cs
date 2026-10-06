// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests.Client;

/// <summary>Manifests the client tests use; every one names only the test host.</summary>
internal static class TestManifests
{
    public const string Timer = "example.countdown/timer";
    public const string Status = "example.countdown/status";

    /// <summary>The contract's §3.9 example: a widget-and-action with two settings, and a face-only widget.</summary>
    public static ExtensionManifest Countdown => ManifestReader.Read(Fixtures.Text("manifests/valid/countdown.json"), TestHosts.Options).Value!;

    /// <summary>A manifest with one contribution of each kind and no settings.</summary>
    public static ExtensionManifest Kinds { get; } = new()
    {
        Id = "example.kinds",
        Name = "Kinds",
        Description = "One contribution of each kind.",
        Version = "1.0.0",
        Hosts = [TestHosts.Id],
        Contributions =
        [
            Contribution("example.kinds/action", Provides.Invoke),
            Contribution("example.kinds/widget", Provides.Face),
            Contribution("example.kinds/both", Provides.Invoke | Provides.Face),
        ],
    };

    public static Contribution Contribution(string id, Provides provides) => new()
    {
        Id = id,
        Name = "Contribution",
        Description = "A test contribution.",
        Glyph = "",
        Provides = provides,
    };
}

/// <summary>What the scripted host sends in its handshake.</summary>
internal sealed class HostScript
{
    public int? Version { get; init; }

    public string HostId { get; init; } = TestHosts.Id;

    public string HostVersion { get; init; } = "2026.10.1";

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public HostLimits Limits { get; init; } = HostLimits.Protocol3Defaults;

    public string UiLanguage { get; init; } = "en-US";

    public bool BadProof { get; init; }
}

/// <summary>A handler whose behaviour each test sets.</summary>
internal sealed class TestHandler : IContributionHandler
{
    public ConcurrentQueue<Session> Sessions { get; } = new();

    public ConcurrentQueue<Invocation> Invocations { get; } = new();

    public Func<Session, CancellationToken, Task>? OnSession { get; init; }

    public Func<Invocation, CancellationToken, Task<InvokeResult>>? OnInvoke { get; init; }

    public async Task RunSessionAsync(Session session, CancellationToken cancellationToken)
    {
        Sessions.Enqueue(session);
        if (OnSession is { } run)
        {
            await run(session, cancellationToken);
        }
        else
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    public Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken)
    {
        Invocations.Enqueue(invocation);
        return OnInvoke?.Invoke(invocation, cancellationToken) ?? Task.FromResult(InvokeResult.Done);
    }
}

/// <summary>
/// A real <see cref="CompanionClient"/> over the in-memory transport seam, with a scripted host on
/// the other end of each connection. The pairing uses the test host id and the fixture secret.
/// </summary>
internal sealed class ClientHarness : IAsyncDisposable
{
    public const string RegistrationId = "00112233445566778899aabbccddeeff";
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Stream> _accepted = Channel.CreateUnbounded<Stream>();
    private readonly List<StatusChangedEventArgs> _statuses = [];
    private readonly List<HandlerFaultedEventArgs> _faults = [];
    private readonly List<Exception> _subscriberErrors = [];
    private Task _run = Task.CompletedTask;
    private int _refusals;

    public ClientHarness(IContributionHandler handler, ExtensionManifest? manifest = null, CompanionClientOptions? options = null,
        bool serverVerified = true, IReadOnlyList<string>? offered = null, string hostId = TestHosts.Id, Func<double>? random = null)
    {
        Manifest = manifest ?? TestManifests.Countdown;
        Pairing = new Pairing(hostId, PipeNames.Create(hostId, "test", "a8c06b3027d3fc4a", RegistrationId), RegistrationId, Manifest.Id,
            (byte[])Secret.Clone());
        Client = new CompanionClient(Pairing, Manifest, handler, options, new ClientInternals
        {
            Transport = new InMemoryTransport(AcceptAsync, serverVerified),
            Observer = Observer,
            OfferedCapabilities = offered ?? [Capabilities.TestEcho],
            Random = random ?? (() => 0.5),
        });
        Client.StatusChanged += (_, e) =>
        {
            lock (_statuses)
            {
                _statuses.Add(e);
            }
        };
        Client.HandlerFaulted += (_, e) =>
        {
            lock (_faults)
            {
                _faults.Add(e);
            }
        };
        Client.SubscriberFaulted += error =>
        {
            lock (_subscriberErrors)
            {
                _subscriberErrors.Add(error);
            }
        };
    }

    /// <summary>The fixture secret: bytes 0 to 31.</summary>
    public static byte[] Secret { get; } = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();

    public ExtensionManifest Manifest { get; }

    public Pairing Pairing { get; }

    public CompanionClient Client { get; }

    public RecordingObserver Observer { get; } = new();

    public Task Run => _run;

    public IReadOnlyList<StatusChangedEventArgs> Statuses
    {
        get
        {
            lock (_statuses)
            {
                return [.. _statuses];
            }
        }
    }

    public IReadOnlyList<HandlerFaultedEventArgs> Faults
    {
        get
        {
            lock (_faults)
            {
                return [.. _faults];
            }
        }
    }

    public IReadOnlyList<Exception> SubscriberErrors
    {
        get
        {
            lock (_subscriberErrors)
            {
                return [.. _subscriberErrors];
            }
        }
    }

    /// <summary>The next <paramref name="count"/> connection attempts fail with <c>host.not-running</c>.</summary>
    public void RefuseConnections(int count) => Interlocked.Exchange(ref _refusals, count);

    public void Start() => _run = Task.Run(() => Client.RunCoreAsync(_stop.Token));

    public async Task<ScriptedPeer> NextPeerAsync()
    {
        var stream = await _accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return new ScriptedPeer(stream);
    }

    /// <summary>Accepts the next connection and completes the handshake as a host.</summary>
    public async Task<ScriptedPeer> ConnectAsync(HostScript? script = null)
    {
        var peer = await NextPeerAsync();
        await peer.HandshakeAsync(script ?? new HostScript());
        await WaitForAsync(() => Client.State == ConnectionState.Connected, "connected");
        return peer;
    }

    public async Task<StatusChangedEventArgs> WaitForStatusAsync(Func<StatusChangedEventArgs, bool> match, string what, int skip = 0)
    {
        StatusChangedEventArgs? found = null;
        await WaitForAsync(() => (found = Statuses.Skip(skip).FirstOrDefault(match)) is not null, what);
        return found!;
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        await _run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            Pairing.Dispose();
            _stop.Dispose();
        }
    }

    public static async Task WaitForAsync(Func<bool> condition, string what, int seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds))
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(5);
        }
    }

    private ValueTask<bool> AcceptAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref _refusals) >= 0)
        {
            return ValueTask.FromResult(false);
        }

        Interlocked.Exchange(ref _refusals, 0);
        _accepted.Writer.TryWrite(stream);
        return ValueTask.FromResult(true);
    }
}

/// <summary>Records the client's internal notes.</summary>
internal sealed class RecordingObserver : IClientObserver
{
    public ConcurrentQueue<string> FramesProcessed { get; } = new();

    public ConcurrentDictionary<string, ReasonCode?> Started { get; } = new();

    public ConcurrentBag<string> SessionsEnded { get; } = [];

    public ConcurrentBag<string> InvocationsEnded { get; } = [];

    public ConcurrentBag<string> Deadlines { get; } = [];

    public ConcurrentBag<string> AfterEnd { get; } = [];

    public ConcurrentBag<string> WithoutFace { get; } = [];

    public void FrameProcessed(WireMessage message) => FramesProcessed.Enqueue(message.Type);

    public void SessionStarted(string sessionId, ReasonCode? refusal) => Started[sessionId] = refusal;

    public void SessionHandlerEnded(string sessionId) => SessionsEnded.Add(sessionId);

    public void InvocationHandlerEnded(string requestId) => InvocationsEnded.Add(requestId);

    public void InvocationDeadlineReached(string requestId) => Deadlines.Add(requestId);

    public void PublishedAfterEnd(string sessionId) => AfterEnd.Add(sessionId);

    public void FaceWithoutProvides(string sessionId) => WithoutFace.Add(sessionId);
}

/// <summary>The host end of one in-memory connection, driven by a test.</summary>
internal sealed class ScriptedPeer : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public ScriptedPeer(Stream stream) => Stream = stream;

    public Stream Stream { get; }

    public HelloMessage? Hello { get; private set; }

    public ChallengeMessage? Challenge { get; private set; }

    public HandshakeTranscript? Transcript { get; private set; }

    /// <summary>Whether SDK pings are answered automatically while reading.</summary>
    public bool AnswerPings { get; set; } = true;

    public ConcurrentQueue<int> PingsReceived { get; } = new();

    public static string NewId() => Guid.NewGuid().ToString("N");

    public async Task<HelloMessage> ReadHelloAsync()
    {
        Hello = Assert.IsType<HelloMessage>(await ReadAsync(ConnectionPhase.Handshake));
        return Hello;
    }

    public ChallengeMessage BuildChallenge(HostScript script)
    {
        var hello = Hello!;
        var version = script.Version ?? 3;
        Transcript = new HandshakeTranscript
        {
            HostId = script.HostId,
            HostVersion = script.HostVersion,
            RegistrationId = hello.RegistrationId,
            ClientNonce = hello.ClientNonce,
            ServerNonce = Handshake.NewNonce(),
            Version = version,
            MinVersion = hello.MinVersion,
            MaxVersion = hello.MaxVersion,
            ClientCapabilities = hello.Capabilities,
            HostCapabilities = script.Capabilities,
            ManifestHash = hello.ManifestHash,
        };
        return new ChallengeMessage
        {
            ServerNonce = Transcript.ServerNonce,
            Version = version,
            Capabilities = script.Capabilities,
            Host = new HostIdentity { Id = script.HostId, Version = script.HostVersion },
            Proof = script.BadProof ? Convert.ToBase64String(new byte[32]) : Handshake.ComputeProof(ClientHarness.Secret, Transcript, ProofRole.Server),
        };
    }

    public static ReadyMessage BuildReady(HostScript script) => new()
    {
        Version = script.Version ?? 3,
        Capabilities = script.Capabilities,
        Host = new HostIdentity { Id = script.HostId, Version = script.HostVersion },
        UiLanguage = script.UiLanguage,
        Limits = script.Limits,
    };

    /// <summary>Runs the host's half of the handshake, checking the companion's proof independently.</summary>
    public async Task HandshakeAsync(HostScript script)
    {
        await ReadHelloAsync();
        Challenge = BuildChallenge(script);
        await SendAsync(Challenge);
        var authenticate = Assert.IsType<AuthenticateMessage>(await ReadAsync(ConnectionPhase.Handshake));
        Assert.True(Handshake.VerifyProof(ClientHarness.Secret, authenticate.Proof, Transcript!, ProofRole.Client));
        await SendAsync(BuildReady(script));
    }

    public Task SendAsync(WireMessage message) => SendBodyAsync(MessageWriter.Write(message));

    public Task SendJsonAsync(string json) => SendBodyAsync(Encoding.UTF8.GetBytes(json));

    public async Task SendBodyAsync(byte[] body)
    {
        var frame = new byte[4 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await Stream.WriteAsync(frame);
    }

    public async Task SendRawAsync(byte[] bytes) => await Stream.WriteAsync(bytes);

    /// <summary>Reads the companion's next message (answering SDK pings when <see cref="AnswerPings"/>); null at the end of the stream.</summary>
    public async Task<WireMessage?> ReadAsync(ConnectionPhase phase = ConnectionPhase.Authenticated, TimeSpan? timeout = null)
    {
        while (true)
        {
            var frame = await ReadFrameAsync(timeout ?? DefaultTimeout);
            if (frame is null)
            {
                return null;
            }

            var read = MessageReader.Read(frame, Sender.Companion, phase);
            Assert.True(read.Violation is null, "The companion sent an invalid frame: " + read.Violation + " " + Encoding.UTF8.GetString(frame));
            if (read.Message is PingMessage ping && AnswerPings)
            {
                PingsReceived.Enqueue(ping.Id);
                await SendAsync(new PongMessage { Id = ping.Id });
                continue;
            }

            if (read.Message is { } message)
            {
                return message;
            }
        }
    }

    public async Task<T> ReadAsync<T>(TimeSpan? timeout = null)
        where T : WireMessage => Assert.IsType<T>(await ReadAsync(ConnectionPhase.Authenticated, timeout));

    /// <summary>
    /// Reads the companion's next message, moving <paramref name="clock"/> on by <paramref name="nudge"/>
    /// whenever nothing arrives for 100 ms of real time (at most 50 times). The SDK's writer measures
    /// a wait and then starts its timer; a clock moved between the two starts that timer late.
    /// </summary>
    public async Task<T> ReadNudgingAsync<T>(TestClock clock, TimeSpan nudge)
        where T : WireMessage
    {
        for (var nudges = 0; ; nudges++)
        {
            try
            {
                return await ReadAsync<T>(TimeSpan.FromMilliseconds(100));
            }
            catch (OperationCanceledException) when (nudges < 50)
            {
                clock.Advance(nudge);
            }
        }
    }

    /// <summary>Whether the companion sends nothing (but SDK pings) for <paramref name="window"/> of real time.</summary>
    public async Task<bool> SilentForAsync(TimeSpan window)
    {
        try
        {
            _ = await ReadAsync(ConnectionPhase.Authenticated, window);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>Reads until the end of the stream; returns the code of the last <c>error</c> frame, if any.</summary>
    public async Task<ReasonCode?> ReadCloseAsync(TimeSpan? timeout = null)
    {
        ReasonCode? code = null;
        while (true)
        {
            var frame = await ReadFrameAsync(timeout ?? DefaultTimeout);
            if (frame is null)
            {
                return code;
            }

            var read = MessageReader.Read(frame, Sender.Companion, ConnectionPhase.Authenticated);
            if (read.Message is ErrorMessage error)
            {
                code = error.Code;
            }
        }
    }

    public async Task<byte[]?> ReadFrameAsync(TimeSpan timeout)
    {
        using var bound = new CancellationTokenSource(timeout);
        try
        {
            return await Framing.ReadFrameAsync(Stream, Framing.MaxFrameBytes, TimeProvider.System, bound.Token);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync() => await Stream.DisposeAsync();
}
