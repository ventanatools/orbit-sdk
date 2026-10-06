// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Wire;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>How a simulated invocation ended.</summary>
internal enum SimulatedOutcomeKind
{
    Done = 1,
    Refused = 2,
    Failed = 3,
    Unsupported = 4,
    Cancelled = 5,
    TimedOut = 6,
    Disconnected = 7,
}

/// <summary>A simulated invocation's outcome.</summary>
internal sealed class SimulatedOutcome
{
    public required SimulatedOutcomeKind Kind { get; init; }

    public Failure? Failure { get; init; }

    public override string ToString() => Tokens.Outcome(Kind) + (Failure is { } failure ? " (" + Tokens.Failure(failure) + ")" : string.Empty);
}

/// <summary>Where a simulated session stands.</summary>
internal enum SimulatedSessionStatus
{
    Running = 1,
    Refused = 2,
    Stopped = 3,
    Ended = 4,
}

/// <summary>A session the simulated host started.</summary>
internal sealed class SimulatedSession
{
    public required string Name { get; init; }

    public required string Id { get; init; }

    public required Contribution Contribution { get; init; }

    public required IReadOnlyDictionary<string, string> Settings { get; init; }

    public SimulatedSessionStatus Status { get; set; } = SimulatedSessionStatus.Running;

    public ReasonCode? RefusedCode { get; set; }

    /// <summary>The current face, cleaned as a host would hold it; null when cleared or never set.</summary>
    public WireFace? Face { get; set; }

    /// <summary>The failure the companion reported with <c>fail</c>, until the next face.</summary>
    public Failure? Failure { get; set; }

    /// <summary>How many faces arrived for the session.</summary>
    public int FaceCount { get; set; }
}

/// <summary>An invocation the simulated host sent.</summary>
internal sealed class SimulatedRequest
{
    public required string Name { get; init; }

    public required string Id { get; init; }

    public required SimulatedSession Session { get; init; }

    public TaskCompletionSource<SimulatedOutcome> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ITimer? Deadline { get; set; }
}

/// <summary>The explicit token maps the transcript uses (contract §7.13).</summary>
internal static class Tokens
{
    public static string Outcome(SimulatedOutcomeKind kind) => kind switch
    {
        SimulatedOutcomeKind.Done => "Done",
        SimulatedOutcomeKind.Refused => "Refused",
        SimulatedOutcomeKind.Failed => "Failed",
        SimulatedOutcomeKind.Unsupported => "Unsupported",
        SimulatedOutcomeKind.Cancelled => "Cancelled",
        SimulatedOutcomeKind.TimedOut => "TimedOut",
        _ => "Disconnected",
    };

    public static string Failure(Failure failure) => failure switch
    {
        Extensions.Failure.UnsupportedInput => "UnsupportedInput",
        Extensions.Failure.NeedsSetup => "NeedsSetup",
        Extensions.Failure.Network => "Network",
        Extensions.Failure.NoResult => "NoResult",
        _ => "AppUnavailable",
    };

    public static string State(FaceState state) => state switch
    {
        FaceState.Playing => "Playing",
        FaceState.Paused => "Paused",
        FaceState.On => "On",
        FaceState.Off => "Off",
        _ => "None",
    };
}

/// <summary>
/// A pipe-level fake host for <c>simulate</c> (contract §11.1, §11.2): it creates its pipe exactly
/// as a host must (§7.1: access for the current user only, the first and only instance of its
/// name, one connection at a time, kept across reconnects), performs the host's half of the
/// handshake with the temporary registration, validates every frame with the author package's
/// <see cref="MessageReader"/>, answers and sends pings, enforces the hard rate budget, and
/// records sessions, faces and results for the prompt, the script runner and the transcript.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SimulatedHost : IAsyncDisposable
{
    private const int HostSendRate = 256;
    private const int HostSendBurst = 512;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ThrottleNoticeInterval = TimeSpan.FromSeconds(5);

    private readonly ExtensionManifest _manifest;
    private readonly SimulationRegistration _registration;
    private readonly string _manifestHash;
    private readonly Transcript _transcript;
    private readonly HostLimits _limits = HostLimits.Protocol3Defaults;
    private readonly HostIdentity _identity;
    private readonly string _uiLanguage;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, SimulatedSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SimulatedRequest> _requests = new(StringComparer.Ordinal);
    private NamedPipeServerStream? _pipe;
    private Connection? _connection;
    private TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _accept = Task.CompletedTask;
    private int _sessionNumber;
    private int _requestNumber;

    public SimulatedHost(ExtensionManifest manifest, SimulationRegistration registration, Transcript transcript)
    {
        _manifest = manifest;
        _registration = registration;
        _manifestHash = ManifestWriter.ComputeHash(manifest);
        _transcript = transcript;
        _identity = new HostIdentity { Id = registration.HostId, Version = HostVersion() };
        var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
        _uiLanguage = TextRules.IsLanguageTag(culture) ? culture : "en-US";
    }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _connection is not null;
            }
        }
    }

    /// <summary>Creates the pipe and starts accepting connections.</summary>
    public void Start()
    {
        _pipe = CreatePipe(_registration.PipeName);
        _accept = Task.Run(AcceptLoopAsync);
        _transcript.Note("listening", "host " + _registration.HostId + ", edition " + SimulationRegistration.Edition
            + ", registration " + _registration.RegistrationId);
    }

    /// <summary>Waits until a companion has completed the handshake; false after <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task connected;
        lock (_gate)
        {
            connected = _connected.Task;
        }

        try
        {
            await connected.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public IReadOnlyList<SimulatedSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions.Values];
            }
        }
    }

    /// <summary>Starts a session with the contribution's defaults and <paramref name="settings"/>; returns an error text when it cannot.</summary>
    public async Task<(SimulatedSession? Session, string? Error)> StartSessionAsync(string contributionId, IReadOnlyDictionary<string, string> settings,
        string? name)
    {
        var contribution = _manifest.Contributions.FirstOrDefault(item => item.Id == contributionId);
        if (contribution is null)
        {
            return (null, "the manifest has no contribution " + contributionId + ".");
        }

        var values = contribution.Settings.ToDictionary(setting => setting.Id, setting => setting.Default, StringComparer.Ordinal);
        foreach (var (key, value) in settings)
        {
            var setting = contribution.Settings.FirstOrDefault(item => item.Id == key);
            if (setting is null)
            {
                return (null, contributionId + " has no setting " + key + ".");
            }

            if (!setting.Choices.Any(choice => choice.Value == value))
            {
                return (null, value + " is not a choice of " + key + ".");
            }

            values[key] = value;
        }

        Connection connection;
        SimulatedSession session;
        lock (_gate)
        {
            if (_connection is null)
            {
                return (null, "no companion is connected.");
            }

            name ??= NextName("s", ref _sessionNumber);
            if (_sessions.TryGetValue(name, out var existing) && existing.Status == SimulatedSessionStatus.Running)
            {
                return (null, "the session " + name + " is running.");
            }

            connection = _connection;
            session = new SimulatedSession { Name = name, Id = NewId(), Contribution = contribution, Settings = values };
            _sessions[name] = session;
        }

        await connection.SendAsync(new StartSessionMessage { SessionId = session.Id, ContributionId = contributionId, Settings = values })
            .ConfigureAwait(false);
        return (session, null);
    }

    public async Task<string?> StopSessionAsync(string name)
    {
        Connection? connection;
        SimulatedSession? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(name, out session) || session.Status != SimulatedSessionStatus.Running)
            {
                return "no running session is named " + name + ".";
            }

            session.Status = SimulatedSessionStatus.Stopped;
            connection = _connection;
        }

        if (connection is not null)
        {
            await connection.SendAsync(new StopSessionMessage { SessionId = session.Id }).ConfigureAwait(false);
        }

        Signal();
        return null;
    }

    public async Task<(SimulatedRequest? Request, string? Error)> InvokeAsync(string sessionName, string? name)
    {
        Connection? connection;
        SimulatedRequest request;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionName, out var session) || session.Status != SimulatedSessionStatus.Running)
            {
                return (null, "no running session is named " + sessionName + ".");
            }

            if ((session.Contribution.Provides & Provides.Invoke) == 0)
            {
                return (null, session.Contribution.Id + " does not provide invoke.");
            }

            if (_connection is null)
            {
                return (null, "no companion is connected.");
            }

            name ??= NextName("r", ref _requestNumber);
            if (_requests.TryGetValue(name, out var existing) && !existing.Result.Task.IsCompleted)
            {
                return (null, "the request " + name + " is pending.");
            }

            connection = _connection;
            request = new SimulatedRequest { Name = name, Id = NewId(), Session = session };
            _requests[name] = request;
            request.Deadline = TimeProvider.System.CreateTimer(_ => _ = TimeOutAsync(request), null,
                TimeSpan.FromMilliseconds(_limits.InvokeTimeoutMs), Timeout.InfiniteTimeSpan);
        }

        await connection.SendAsync(new InvokeMessage { RequestId = request.Id, SessionId = request.Session.Id }).ConfigureAwait(false);
        return (request, null);
    }

    public async Task<string?> CancelAsync(string name)
    {
        SimulatedRequest? request;
        Connection? connection;
        lock (_gate)
        {
            if (!_requests.TryGetValue(name, out request) || request.Result.Task.IsCompleted)
            {
                return "no pending request is named " + name + ".";
            }

            connection = _connection;
        }

        if (Complete(request, new SimulatedOutcome { Kind = SimulatedOutcomeKind.Cancelled }) && connection is not null)
        {
            await connection.SendAsync(new CancelMessage { RequestId = request.Id }).ConfigureAwait(false);
        }

        return null;
    }

    public async Task<string?> PingAsync()
    {
        Connection? connection;
        lock (_gate)
        {
            connection = _connection;
        }

        return connection is null ? "no companion is connected." : await connection.PingAsync().ConfigureAwait(false);
    }

    /// <summary>Closes the connection with <paramref name="code"/>, as a host does; the companion reconnects.</summary>
    public async Task<string?> DisconnectAsync(ReasonCode code)
    {
        Connection? connection;
        lock (_gate)
        {
            connection = _connection;
        }

        if (connection is null)
        {
            return "no companion is connected.";
        }

        await connection.CloseAsync(code).ConfigureAwait(false);

        // Return once the host has ended the connection, so the next step waits for the companion to reconnect.
        try
        {
            await connection.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "the connection did not close.";
        }

        return null;
    }

    /// <summary>Waits until <paramref name="match"/> holds for the named session, re-checking on every change.</summary>
    public async Task<bool> WaitUntilAsync(Func<bool> match, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (match())
                {
                    return true;
                }

                changed = _changed.Task;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                await changed.WaitAsync(left, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lock (_gate)
                {
                    return match();
                }
            }
        }
    }

    /// <summary>The named session, for the script runner's checks (call under <see cref="WaitUntilAsync"/>).</summary>
    public SimulatedSession? Session(string name) => _sessions.GetValueOrDefault(name);

    public async ValueTask DisposeAsync()
    {
        Connection? connection;
        lock (_gate)
        {
            connection = _connection;
        }

        if (connection is not null)
        {
            await connection.CloseAsync(ReasonCode.HostShuttingDown).ConfigureAwait(false);
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _accept.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException)
        {
            // Stopping.
        }

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    /// <summary>
    /// The host's pipe options (contract §7.1): asynchronous, the first instance of its name, one
    /// instance, and an access-control list with exactly one entry, for the current user's SID.
    /// </summary>
    public static NamedPipeServerStream CreatePipe(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("The current user has no SID.");
        var options = PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance;
        if (identity.Owner == user)
        {
            // The token's owner is the user, so CurrentUserOnly builds exactly the one-entry list.
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, options | PipeOptions.CurrentUserOnly,
                4_096, 4_096);
        }

        // An elevated token's default owner is Administrators, which CurrentUserOnly would grant; list the user instead.
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetOwner(user);
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, options, 4_096, 4_096, security);
    }

    private async Task AcceptLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _pipe!.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // The instance is broken: replace it with a new first instance of the same name.
                await _pipe!.DisposeAsync().ConfigureAwait(false);
                _pipe = CreatePipe(_registration.PipeName);
                continue;
            }

            _transcript.Note("accepted", "a companion opened the pipe");
            using var connection = new Connection(this, _pipe!);
            await connection.RunAsync(token).ConfigureAwait(false);
            EndConnection(connection);
            connection.Ended.TrySetResult();
            try
            {
                _pipe!.Disconnect();
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or ObjectDisposedException)
            {
                // Already disconnected.
            }
        }
    }

    private void Connected(Connection connection)
    {
        lock (_gate)
        {
            _connection = connection;
            _connected.TrySetResult();
        }

        Signal();
    }

    private void EndConnection(Connection connection)
    {
        List<SimulatedRequest> pending;
        lock (_gate)
        {
            if (_connection == connection)
            {
                _connection = null;
                _connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            // A connection loss ends every session; nothing is replayed (contract §7.6.3).
            foreach (var session in _sessions.Values.Where(session => session.Status == SimulatedSessionStatus.Running))
            {
                session.Status = SimulatedSessionStatus.Ended;
            }

            pending = _requests.Values.Where(request => !request.Result.Task.IsCompleted).ToList();
        }

        foreach (var request in pending)
        {
            Complete(request, new SimulatedOutcome { Kind = SimulatedOutcomeKind.Disconnected });
        }

        _transcript.Note("disconnected");
        Signal();
    }

    private async Task TimeOutAsync(SimulatedRequest request)
    {
        Connection? connection;
        lock (_gate)
        {
            connection = _connection;
        }

        if (Complete(request, new SimulatedOutcome { Kind = SimulatedOutcomeKind.TimedOut }))
        {
            _transcript.Note(ReasonCode.InvokeTimeout.Value, request.Name + ": no result within the host's deadline; sending cancel");
            if (connection is not null)
            {
                await connection.SendAsync(new CancelMessage { RequestId = request.Id }).ConfigureAwait(false);
            }
        }
    }

    private bool Complete(SimulatedRequest request, SimulatedOutcome outcome)
    {
        request.Deadline?.Dispose();
        var completed = request.Result.TrySetResult(outcome);
        Signal();
        return completed;
    }

    private void Signal()
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }

    private static string NextName(string prefix, ref int counter) => prefix + (++counter).ToString(CultureInfo.InvariantCulture);

    private static string NewId()
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (id.All(c => c == '0'));

        return id;
    }

    /// <summary>The tool version in the host-version grammar <c>[0-9A-Za-z.+-]{1,32}</c>.</summary>
    private static string HostVersion()
    {
        var version = new string(ToolIdentity.Version.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-').Take(32).ToArray());
        return version.Length > 0 ? version : "0";
    }

    /// <summary>A face's text as a host would hold it after cleaning (contract §7.7.1, §7.7.3).</summary>
    private static string? Clean(FaceLine? line, int elements, int units) => line is TextLine text ? TextRules.Clean(text.Text, elements, units) : null;

    private static WireFace CleanFace(WireFace face) => new()
    {
        Picture = face.Picture,
        Line1 = Clean(face.Line1, 40, 160),
        Line2 = Clean(face.Line2, 60, 240),
        State = face.State,
        Detail = face.Detail is { } detail ? TextRules.Clean(detail, 160, 640) : null,
        GoodForSeconds = face.GoodForSeconds,
    };

    private static string Describe(WireFace face)
    {
        var parts = new List<string>();
        if (face.Line1 is TextLine line1)
        {
            parts.Add("\"" + line1.Text + "\"" + (face.Line2 is TextLine l2 ? " / \"" + l2.Text + "\"" : string.Empty));
        }
        else if (face.Line2 is TextLine line2)
        {
            parts.Add("/ \"" + line2.Text + "\"");
        }

        if (face.Picture is GlyphPicture glyph)
        {
            parts.Add("glyph U+" + ((int)glyph.Glyph[0]).ToString("X4", CultureInfo.InvariantCulture));
        }

        if (face.State != FaceState.None)
        {
            parts.Add(Tokens.State(face.State));
        }

        parts.Add("good for " + face.GoodForSeconds.ToString(CultureInfo.InvariantCulture) + " s");
        if (face.Detail is { } detail)
        {
            parts.Add("detail \"" + detail + "\"");
        }

        return string.Join("  ", parts);
    }

    private static void WriteFace(Utf8JsonWriter writer, WireFace face)
    {
        writer.WriteStartObject("face");
        if (face.Picture is GlyphPicture glyph)
        {
            writer.WriteString("glyph", glyph.Glyph);
        }

        if (face.Line1 is TextLine line1)
        {
            writer.WriteString("line1", line1.Text);
        }

        if (face.Line2 is TextLine line2)
        {
            writer.WriteString("line2", line2.Text);
        }

        writer.WriteString("state", Tokens.State(face.State));
        if (face.Detail is { } detail)
        {
            writer.WriteString("detail", detail);
        }

        writer.WriteNumber("goodForSeconds", face.GoodForSeconds);
        writer.WriteEndObject();
    }

    /// <summary>One accepted connection: the host's half of the handshake, then the read loop and the ping timer.</summary>
    private sealed class Connection(SimulatedHost host, NamedPipeServerStream stream) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly object _sync = new();
        private readonly TokenBucket _sendBudget = new(HostSendRate, HostSendBurst, TimeProvider.System);
        private readonly TokenBucket _hard = new(host._limits.HardMessageRate, host._limits.HardMessageBurst, TimeProvider.System);
        private readonly TokenBucket _soft = new(host._limits.MessageRate, host._limits.MessageBurst, TimeProvider.System);
        private readonly CancellationTokenSource _closed = new();
        private long _lastReceived = Environment.TickCount64;
        private long _lastCompanionPing = long.MinValue;
        private long _lastThrottleNotice = long.MinValue;
        private long _pingSent;
        private int? _outstandingPing;
        private int _nextPing;
        private bool _closing;

        /// <summary>Completed once the host has ended this connection and its sessions.</summary>
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(CancellationToken stop)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, _closed.Token);
            try
            {
                if (!await HandshakeAsync(linked.Token).ConfigureAwait(false))
                {
                    return;
                }

                host.Connected(this);
                var pinger = PingLoopAsync(linked.Token);
                await ReadLoopAsync(linked.Token).ConfigureAwait(false);
                await _closed.CancelAsync().ConfigureAwait(false);
                await pinger.ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The connection ended.
            }
            finally
            {
                Close();
            }
        }

        public void Dispose()
        {
            _closed.Dispose();
            _writeLock.Dispose();
        }

        public async Task SendAsync(WireMessage message)
        {
            var bytes = MessageWriter.Write(message);
            try
            {
                await _writeLock.WaitAsync(_closed.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                while (!_sendBudget.TryTake(TokenBucket.CostOf(bytes.Length)))
                {
                    await Task.Delay(_sendBudget.RetryAfter, _closed.Token).ConfigureAwait(false);
                }

                host.Record(TranscriptSource.Host, message);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
                timeout.CancelAfter(WriteTimeout);
                await Framing.WriteFrameAsync(stream, bytes, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
                Close();
            }
            finally
            {
                try
                {
                    _writeLock.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Closed.
                }
            }
        }

        public async Task<string?> PingAsync()
        {
            int id;
            lock (_sync)
            {
                if (_outstandingPing is not null)
                {
                    return "a ping is already outstanding.";
                }

                id = ++_nextPing;
                _outstandingPing = id;
                _pingSent = Environment.TickCount64;
            }

            await SendAsync(new PingMessage { Id = id }).ConfigureAwait(false);
            return null;
        }

        /// <summary>Sends an <c>error</c> with <paramref name="code"/> when given, then closes (contract §7.5).</summary>
        public async Task CloseAsync(ReasonCode? code)
        {
            lock (_sync)
            {
                if (_closing)
                {
                    return;
                }

                _closing = true;
            }

            if (code is { } reason)
            {
                await SendAsync(new ErrorMessage { Code = reason }).ConfigureAwait(false);
            }

            Close();
        }

        private void Close()
        {
            try
            {
                _closed.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already ended.
            }
        }

        private async Task<bool> HandshakeAsync(CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(HandshakeTimeout);
            try
            {
                if (await ReadAsync(ConnectionPhase.Handshake, Framing.MaxHandshakeFrameBytes, deadline.Token).ConfigureAwait(false) is not HelloMessage hello)
                {
                    return false;
                }

                if (!string.Equals(hello.RegistrationId, host._registration.RegistrationId, StringComparison.Ordinal))
                {
                    await CloseAsync(ReasonCode.AuthRegistrationMismatch).ConfigureAwait(false);
                    return false;
                }

                if (Handshake.Negotiate(hello.MinVersion, hello.MaxVersion, ProtocolVersions.Min, ProtocolVersions.Max) is not { } version)
                {
                    await SendAsync(new ErrorMessage
                    {
                        Code = ReasonCode.ProtocolVersionUnsupported,
                        Supported = new VersionRange { MinVersion = ProtocolVersions.Min, MaxVersion = ProtocolVersions.Max },
                    }).ConfigureAwait(false);
                    Close();
                    return false;
                }

                string[] capabilities = [];
                var transcript = new HandshakeTranscript
                {
                    HostId = host._identity.Id,
                    HostVersion = host._identity.Version,
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
                    Host = host._identity,
                    Proof = Handshake.ComputeProof(host._registration.Secret, transcript, ProofRole.Server),
                }).ConfigureAwait(false);

                if (await ReadAsync(ConnectionPhase.Handshake, Framing.MaxHandshakeFrameBytes, deadline.Token).ConfigureAwait(false)
                    is not AuthenticateMessage authenticate)
                {
                    return false;
                }

                if (!Handshake.VerifyProof(host._registration.Secret, authenticate.Proof, transcript, ProofRole.Client))
                {
                    await CloseAsync(ReasonCode.AuthProofInvalid).ConfigureAwait(false);
                    return false;
                }

                if (!string.Equals(hello.ManifestHash, host._manifestHash, StringComparison.Ordinal))
                {
                    await CloseAsync(ReasonCode.ManifestMismatch).ConfigureAwait(false);
                    return false;
                }

                await SendAsync(new ReadyMessage
                {
                    Version = version,
                    Capabilities = capabilities,
                    Host = host._identity,
                    UiLanguage = host._uiLanguage,
                    Limits = host._limits,
                }).ConfigureAwait(false);
                return !_closed.IsCancellationRequested;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
            {
                await CloseAsync(ReasonCode.AuthTimeout).ConfigureAwait(false);
                return false;
            }
        }

        private async Task<WireMessage?> ReadAsync(ConnectionPhase phase, int maxFrameBytes, CancellationToken token)
        {
            while (true)
            {
                byte[]? frame;
                try
                {
                    frame = await Framing.ReadFrameAsync(stream, maxFrameBytes, TimeProvider.System, token).ConfigureAwait(false);
                }
                catch (FrameException error)
                {
                    await CloseAsync(error.Code).ConfigureAwait(false);
                    return null;
                }
                catch (EndOfStreamException)
                {
                    return null;
                }

                if (frame is null)
                {
                    return null;
                }

                Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
                var cost = TokenBucket.CostOf(frame.Length);
                if (!_hard.TryTake(cost))
                {
                    await CloseAsync(ReasonCode.RateExceeded).ConfigureAwait(false);
                    return null;
                }

                var softPaid = _soft.TryTake(cost);
                var read = MessageReader.Read(frame, Sender.Companion, phase);
                if (read.Violation is { } violation)
                {
                    host._transcript.Write(TranscriptSource.Companion, "refused", detail: violation.Value);
                    await CloseAsync(violation).ConfigureAwait(false);
                    return null;
                }

                if (read.Ignored is { } ignored)
                {
                    host._transcript.Write(TranscriptSource.Companion, "ignored", detail: ignored.Value);
                    continue;
                }

                if (read.Message is not { } message)
                {
                    continue;
                }

                host.Record(TranscriptSource.Companion, message);
                if (!softPaid && message is SetFaceMessage or ClearFaceMessage or FailMessage)
                {
                    await ThrottleAsync().ConfigureAwait(false);
                }

                return message;
            }
        }

        private async Task ThrottleAsync()
        {
            var now = Environment.TickCount64;
            if (_lastThrottleNotice != long.MinValue && now - _lastThrottleNotice < (long)ThrottleNoticeInterval.TotalMilliseconds)
            {
                return;
            }

            _lastThrottleNotice = now;
            await SendAsync(new ErrorMessage
            {
                Code = ReasonCode.RateThrottled,
                RetryAfterMs = (int)Math.Min(300_000, Math.Ceiling(_soft.RetryAfter.TotalMilliseconds)),
            }).ConfigureAwait(false);
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            while (await ReadAsync(ConnectionPhase.Authenticated, host._limits.MaxFrameBytes, token).ConfigureAwait(false) is { } message)
            {
                switch (message)
                {
                    case SetFaceMessage face:
                        if (!await FaceAsync(face.SessionId, session =>
                            {
                                session.Face = CleanFace(face.Face);
                                session.Failure = null;
                                session.FaceCount++;
                            }).ConfigureAwait(false))
                        {
                            return;
                        }

                        break;
                    case ClearFaceMessage clear:
                        if (!await FaceAsync(clear.SessionId, session => session.Face = null).ConfigureAwait(false))
                        {
                            return;
                        }

                        break;
                    case FailMessage fail:
                        if (!await FaceAsync(fail.SessionId, session => session.Failure = fail.Failure).ConfigureAwait(false))
                        {
                            return;
                        }

                        break;
                    case ResultMessage result:
                        Answer(result);
                        break;
                    case SessionRefusedMessage refused:
                        Refused(refused);
                        break;
                    case PingMessage ping:
                        if (!await AnswerPingAsync(ping).ConfigureAwait(false))
                        {
                            return;
                        }

                        break;
                    case PongMessage pong:
                        lock (_sync)
                        {
                            if (_outstandingPing == pong.Id)
                            {
                                _outstandingPing = null;
                                break;
                            }
                        }

                        host._transcript.Write(TranscriptSource.Companion, "ignored", detail: ReasonCode.ProtocolPongUnknown.Value);
                        break;
                    case ErrorMessage:
                        return;
                }
            }
        }

        /// <summary>Applies a face command; false when it broke a rule that closes the connection.</summary>
        private async Task<bool> FaceAsync(string sessionId, Action<SimulatedSession> apply)
        {
            SimulatedSession? session;
            lock (host._gate)
            {
                session = host._sessions.Values.FirstOrDefault(item => item.Id == sessionId);
                if (session is { Status: SimulatedSessionStatus.Running } && (session.Contribution.Provides & Provides.Face) != 0)
                {
                    apply(session);
                }
            }

            if (session is null || session.Status != SimulatedSessionStatus.Running)
            {
                host._transcript.Write(TranscriptSource.Tool, "ignored", detail: ReasonCode.FaceSessionUnknown.Value);
                return true;
            }

            if ((session.Contribution.Provides & Provides.Face) == 0)
            {
                await CloseAsync(ReasonCode.FaceNotProvided).ConfigureAwait(false);
                return false;
            }

            host.Signal();
            return true;
        }

        private void Answer(ResultMessage result)
        {
            SimulatedRequest? request;
            lock (host._gate)
            {
                request = host._requests.Values.FirstOrDefault(item => item.Id == result.RequestId && !item.Result.Task.IsCompleted);
            }

            if (request is null)
            {
                host._transcript.Write(TranscriptSource.Tool, "ignored", detail: ReasonCode.InvokeRequestUnknown.Value);
                return;
            }

            host.Complete(request, new SimulatedOutcome
            {
                Kind = result.Outcome switch
                {
                    Outcome.Done => SimulatedOutcomeKind.Done,
                    Outcome.Refused => SimulatedOutcomeKind.Refused,
                    Outcome.Unsupported => SimulatedOutcomeKind.Unsupported,
                    _ => SimulatedOutcomeKind.Failed,
                },
                Failure = result.Failure,
            });
        }

        private void Refused(SessionRefusedMessage refused)
        {
            lock (host._gate)
            {
                if (host._sessions.Values.FirstOrDefault(item => item.Id == refused.SessionId) is { } session)
                {
                    session.Status = SimulatedSessionStatus.Refused;
                    session.RefusedCode = refused.Code;
                }
            }

            host.Signal();
        }

        /// <summary>Answers a ping, enforcing the one-outstanding rule of contract §7.9.</summary>
        private async Task<bool> AnswerPingAsync(PingMessage ping)
        {
            var now = Environment.TickCount64;
            if (_lastCompanionPing != long.MinValue && now - _lastCompanionPing < host._limits.PingIntervalMs / 2)
            {
                await CloseAsync(ReasonCode.ProtocolUnexpected).ConfigureAwait(false);
                return false;
            }

            _lastCompanionPing = now;
            await SendAsync(new PongMessage { Id = ping.Id }).ConfigureAwait(false);
            return true;
        }

        private async Task PingLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    var now = Environment.TickCount64;
                    bool timedOut;
                    bool due;
                    lock (_sync)
                    {
                        timedOut = _outstandingPing is not null && now - _pingSent >= host._limits.PongTimeoutMs;
                        due = _outstandingPing is null && now - Interlocked.Read(ref _lastReceived) >= host._limits.PingIntervalMs;
                    }

                    if (timedOut)
                    {
                        await CloseAsync(ReasonCode.ProtocolPingTimeout).ConfigureAwait(false);
                        return;
                    }

                    if (due)
                    {
                        await PingAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The connection ended.
            }
        }
    }

    /// <summary>Writes one message to the transcript, with session and request names and no nonce, proof or secret.</summary>
    private void Record(TranscriptSource from, WireMessage message)
    {
        string? sessionId = null, requestId = null, detail = null;
        Action<Utf8JsonWriter>? extra = null;
        switch (message)
        {
            case HelloMessage hello:
                detail = "client " + hello.Client.Name + " " + hello.Client.Version + ", versions "
                    + hello.MinVersion.ToString(CultureInfo.InvariantCulture) + "-" + hello.MaxVersion.ToString(CultureInfo.InvariantCulture);
                break;
            case ChallengeMessage challenge:
                detail = "version " + challenge.Version.ToString(CultureInfo.InvariantCulture) + ", host " + challenge.Host.Id + " " + challenge.Host.Version;
                break;
            case ReadyMessage ready:
                detail = "ui language " + ready.UiLanguage;
                break;
            case StartSessionMessage start:
                sessionId = start.SessionId;
                detail = start.ContributionId + (start.Settings.Count > 0
                    ? "  " + string.Join(' ', start.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value))
                    : string.Empty);
                break;
            case StopSessionMessage stop:
                sessionId = stop.SessionId;
                break;
            case InvokeMessage invoke:
                sessionId = invoke.SessionId;
                requestId = invoke.RequestId;
                break;
            case CancelMessage cancel:
                requestId = cancel.RequestId;
                break;
            case SetFaceMessage face:
                sessionId = face.SessionId;
                var cleaned = CleanFace(face.Face);
                detail = Describe(cleaned);
                extra = writer => WriteFace(writer, cleaned);
                break;
            case ClearFaceMessage clear:
                sessionId = clear.SessionId;
                break;
            case FailMessage fail:
                sessionId = fail.SessionId;
                detail = Tokens.Failure(fail.Failure);
                break;
            case ResultMessage result:
                requestId = result.RequestId;
                detail = result.Outcome switch
                {
                    Outcome.Done => "Done",
                    Outcome.Refused => "Refused",
                    Outcome.Unsupported => "Unsupported",
                    _ => "Failed",
                } + (result.Failure is { } failure ? " (" + Tokens.Failure(failure) + ")" : string.Empty);
                break;
            case SessionRefusedMessage refused:
                sessionId = refused.SessionId;
                detail = refused.Code.Value;
                break;
            case PingMessage ping:
                detail = "id " + ping.Id.ToString(CultureInfo.InvariantCulture);
                break;
            case PongMessage pong:
                detail = "id " + pong.Id.ToString(CultureInfo.InvariantCulture);
                break;
            case ErrorMessage error:
                detail = error.Code.Value + (error.RetryAfterMs is { } retry ? ", retry after " + retry.ToString(CultureInfo.InvariantCulture) + " ms" : string.Empty);
                break;
        }

        string? session = null, request = null;
        lock (_gate)
        {
            if (sessionId is not null)
            {
                session = _sessions.Values.FirstOrDefault(item => item.Id == sessionId)?.Name;
            }

            if (requestId is not null)
            {
                var named = _requests.Values.FirstOrDefault(item => item.Id == requestId);
                request = named?.Name;
                session ??= named?.Session.Name;
            }
        }

        _transcript.Write(from, message.Type, session, sessionId, request, requestId, detail, extra);
    }
}
