// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Wire;

namespace VentanaTools.Orbit.Extensions.Testing;

/// <summary>Options for a <see cref="CompanionTestHost"/>.</summary>
public sealed class CompanionTestHostOptions
{
    /// <summary>
    /// The host id the test host claims; null for the first active registry host in the manifest's
    /// <c>hosts</c>, else its first host.
    /// </summary>
    public string? HostId { get; init; }

    /// <summary>The limits the test host sends in <c>ready</c>.</summary>
    public HostLimits Limits { get; init; } = HostLimits.Protocol3Defaults;

    /// <summary>The capabilities the test host advertises; the effective ones are those the SDK also supports.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>The UI language the test host sends in <c>ready</c>.</summary>
    public string UiLanguage { get; init; } = "en-US";

    /// <summary>The clock of the test host and of the companion client it runs.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// An in-process host for tests: it runs a real <see cref="CompanionClient"/> over an in-memory
/// duplex stream that carries real frames, authenticates it with a generated pairing, and lets a
/// test start and stop sessions, invoke and cancel, disconnect, and inspect the faces, results,
/// statuses, faults and a transcript of every frame.
/// </summary>
/// <remarks>
/// It behaves as a protocol-3 host: it fills in missing settings with their defaults, answers
/// pings, cancels an invocation at <see cref="HostLimits.InvokeTimeoutMs"/>, and paces its own
/// frames within the host budget. It is not a host implementation for production use.
/// </remarks>
/// <example>
/// <code>
/// await using var host = await CompanionTestHost.StartAsync(manifest, new TimeWidget());
/// var session = await host.StartSessionAsync("example.clock/time");
/// var face = await host.WaitForFaceAsync(session, TimeSpan.FromSeconds(5));
/// await host.StopSessionAsync(session);
/// </code>
/// </example>
public sealed class CompanionTestHost : IAsyncDisposable
{
    private const string HostVersion = "1.0.0";
    private readonly ExtensionManifest _manifest;
    private readonly CompanionTestHostOptions _options;
    private readonly TimeProvider _time;
    private readonly long _start;
    private readonly byte[] _secret;
    private readonly string _registrationId;
    private readonly string _manifestHash;
    private readonly Pairing _pairing;
    private readonly CompanionClient _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<StatusChangedEventArgs> _statuses = [];
    private readonly List<HandlerFaultedEventArgs> _faults = [];
    private readonly List<HostConnection> _connections = [];
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _run = Task.CompletedTask;
    private HostConnection? _connection;
    private int _disposed;

    private CompanionTestHost(ExtensionManifest manifest, IContributionHandler handler, CompanionTestHostOptions options)
    {
        _manifest = manifest;
        _options = options;
        _time = options.TimeProvider ?? throw new ArgumentException("TimeProvider must not be null.", nameof(options));
        _start = _time.GetTimestamp();
        Transcript = new CompanionTranscript();
        Observer = new TestObserver();
        HostId = options.HostId ?? DefaultHostId(manifest);
        _registrationId = Guid.NewGuid().ToString("N");
        _secret = RandomNumberGenerator.GetBytes(32);
        _manifestHash = ManifestWriter.ComputeHash(manifest);
        var pipeName = PipeNames.Create(HostId, "test", "0000000000000000", _registrationId);
        _pairing = new Pairing(HostId, pipeName, _registrationId, manifest.Id, (byte[])_secret.Clone());
        _client = new CompanionClient(_pairing, manifest, handler, new CompanionClientOptions { TimeProvider = _time }, new ClientInternals
        {
            Transport = new InMemoryTransport(AcceptAsync, serverVerified: true),
            Observer = Observer,
        });
        _client.StatusChanged += (_, e) =>
        {
            lock (_gate)
            {
                _statuses.Add(e);
            }

            if (e.State == ConnectionState.Connected)
            {
                _connected.TrySetResult();
            }
            else if (e.State is ConnectionState.Waiting or ConnectionState.Stopped)
            {
                _connected.TrySetException(new InvalidOperationException(
                    "The companion did not connect (" + (e.Reason?.Value ?? "closed") + ")."));
            }
        };
        _client.HandlerFaulted += (_, e) =>
        {
            lock (_gate)
            {
                _faults.Add(e);
            }
        };
    }

    /// <summary>A transcript of every frame both ways: times, directions, types and ids, never contents.</summary>
    public CompanionTranscript Transcript { get; }

    /// <summary>Every status the client raised, in order.</summary>
    public IReadOnlyList<StatusChangedEventArgs> Statuses
    {
        get
        {
            lock (_gate)
            {
                return _statuses.ToArray();
            }
        }
    }

    /// <summary>Every fault the client raised, in order.</summary>
    public IReadOnlyList<HandlerFaultedEventArgs> Faults
    {
        get
        {
            lock (_gate)
            {
                return _faults.ToArray();
            }
        }
    }

    internal string HostId { get; }

    internal TestObserver Observer { get; }

    internal CompanionClient Client => _client;

    internal TimeProvider Time => _time;

    internal HostLimits Limits => _options.Limits;

    /// <summary>Starts the client and waits until it is connected.</summary>
    /// <param name="manifest">The companion's manifest; the test host admits exactly this manifest.</param>
    /// <param name="handler">The handler under test.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The connected test host; dispose it.</returns>
    /// <exception cref="ArgumentException">The manifest or an option is invalid.</exception>
    /// <exception cref="InvalidOperationException">The companion did not connect.</exception>
    public static async Task<CompanionTestHost> StartAsync(ExtensionManifest manifest, IContributionHandler handler,
        CompanionTestHostOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new CompanionTestHostOptions();
        Validate(options);
        var host = new CompanionTestHost(manifest, handler, options);
        host._run = Task.Run(() => host._client.RunCoreAsync(host._stop.Token), CancellationToken.None);
        try
        {
            await host._connected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return host;
    }

    /// <summary>
    /// Starts a session, filling missing settings with their defaults, and waits until the
    /// companion has accepted or refused it (<see cref="TestHostSession.RefusedCode"/>).
    /// </summary>
    /// <param name="contributionId">A contribution of the manifest.</param>
    /// <param name="settings">Setting values; missing keys take their defaults, extra keys are sent as given.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The session.</returns>
    /// <exception cref="ArgumentException">The contribution is not in the manifest.</exception>
    /// <exception cref="InvalidOperationException">The companion is not connected.</exception>
    public async Task<TestHostSession> StartSessionAsync(string contributionId, IReadOnlyDictionary<string, string>? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contributionId);
        var contribution = _manifest.Contributions.FirstOrDefault(item => string.Equals(item.Id, contributionId, StringComparison.Ordinal))
            ?? throw new ArgumentException("The contribution is not in the manifest.", nameof(contributionId));
        var connection = Current();
        var session = new TestHostSession(Guid.NewGuid().ToString("N"), contribution.Id, connection);
        var started = Observer.ExpectStart(session.SessionId);
        connection.Register(session);
        await connection.SendAsync(new StartSessionMessage
        {
            SessionId = session.SessionId,
            ContributionId = contribution.Id,
            Settings = TestSettings.Complete(contribution, settings),
        }, cancellationToken).ConfigureAwait(false);
        session.RefusedCode = await started.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (session.RefusedCode is not null)
        {
            await session.Refusal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    /// <summary>Sends <c>invoke</c> for a session and returns at once; <see cref="TestInvocation.Result"/> completes with the outcome.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The invocation.</returns>
    public TestInvocation StartInvoke(TestHostSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ThrowIfDisposed();
        var invocation = new TestInvocation(Guid.NewGuid().ToString("N"));
        session.Connection.StartInvoke(session, invocation);
        return invocation;
    }

    /// <summary>Sends <c>cancel</c> for an invocation; its result completes as <see cref="TestInvokeOutcomeKind.Cancelled"/> unless it already completed.</summary>
    /// <param name="invocation">The invocation.</param>
    /// <returns>A task that completes when <c>cancel</c> is sent.</returns>
    public Task CancelAsync(TestInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ThrowIfDisposed();
        return invocation.Connection?.CancelAsync(invocation) ?? Task.CompletedTask;
    }

    /// <summary>Invokes and waits for the outcome; cancelling <paramref name="cancellationToken"/> sends <c>cancel</c>.</summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The outcome.</returns>
    public async Task<TestInvokeOutcome> InvokeAsync(TestHostSession session, CancellationToken cancellationToken = default)
    {
        var invocation = StartInvoke(session);
        using var registration = cancellationToken.Register(() => _ = CancelAsync(invocation));
        return await invocation.Result.ConfigureAwait(false);
    }

    /// <summary>Sends <c>stopSession</c>.</summary>
    /// <param name="session">The session.</param>
    /// <returns>A task that completes when the frame is sent.</returns>
    public Task StopSessionAsync(TestHostSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ThrowIfDisposed();
        session.Stopped = true;
        return session.Connection.SendAsync(new StopSessionMessage { SessionId = session.SessionId }, CancellationToken.None);
    }

    /// <summary>Closes the connection, after an <c>error</c> frame with <paramref name="code"/> when one is given. The client then reconnects with its backoff.</summary>
    /// <param name="code">The reason code to close with, or null to just close.</param>
    /// <returns>A task that completes when the connection is closed.</returns>
    public async Task DisconnectAsync(ReasonCode? code = null)
    {
        HostConnection? connection;
        lock (_gate)
        {
            connection = _connection;
        }

        if (connection is not null)
        {
            await connection.CloseAsync(code).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for the next face the session publishes after the previous call (faces are returned
    /// in arrival order), as a host would hold it after cleaning.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="timeout">How long to wait, on the test host's clock.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The face and when it arrived.</returns>
    /// <exception cref="TimeoutException">No face arrived within <paramref name="timeout"/>.</exception>
    public Task<ReceivedFace> WaitForFaceAsync(TestHostSession session, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.NextFaceAsync(timeout, _time, cancellationToken);
    }

    /// <summary>Stops the client and closes the connection.</summary>
    /// <returns>A task that completes when the client has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        HostConnection? connection;
        lock (_gate)
        {
            connection = _connection;
        }

        if (connection is not null)
        {
            await connection.CloseAsync(null).ConfigureAwait(false);
        }

        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The client is left to finish on its own.
        }

        HostConnection[] connections;
        lock (_gate)
        {
            connections = [.. _connections];
        }

        foreach (var item in connections)
        {
            await item.CloseAsync(null).ConfigureAwait(false);
            item.Dispose();
        }

        _pairing.Dispose();
        CryptographicOperations.ZeroMemory(_secret);
        _stop.Dispose();
    }

    internal TimeSpan Now => _time.GetElapsedTime(_start);

    internal void Record(Sender from, WireMessage message)
    {
        var (sessionId, requestId) = message switch
        {
            StartSessionMessage m => (m.SessionId, (string?)null),
            StopSessionMessage m => (m.SessionId, null),
            InvokeMessage m => (m.SessionId, m.RequestId),
            CancelMessage m => (null, m.RequestId),
            SetFaceMessage m => (m.SessionId, null),
            ClearFaceMessage m => (m.SessionId, null),
            FailMessage m => (m.SessionId, null),
            ResultMessage m => (null, m.RequestId),
            SessionRefusedMessage m => (m.SessionId, null),
            ErrorMessage m => (m.SessionId, m.RequestId),
            _ => ((string?)null, (string?)null),
        };
        Transcript.Add(new TranscriptEntry { At = Now, From = from, Type = message.Type, SessionId = sessionId, RequestId = requestId });
    }

    private static string DefaultHostId(ExtensionManifest manifest)
    {
        var hosts = manifest.Hosts ?? [];
        foreach (var id in hosts)
        {
            if (id is not null && HostRegistry.Find(id) is { Status: HostStatus.Active })
            {
                return id;
            }
        }

        return hosts.Count > 0 && TextRules.IsHostId(hosts[0])
            ? hosts[0]
            : throw new ArgumentException("The manifest lists no host.", nameof(manifest));
    }

    private static void Validate(CompanionTestHostOptions options)
    {
        if (options.HostId is { } id && !TextRules.IsHostId(id))
        {
            throw new ArgumentException("HostId does not match the host-id grammar.", nameof(options));
        }

        if (options.Limits is null || !options.Limits.IsWithinRanges())
        {
            throw new ArgumentException("Limits must be within the ranges of the contract.", nameof(options));
        }

        var capabilities = options.Capabilities ?? [];
        if (capabilities.Count > 32 || capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Count
            || capabilities.Any(capability => !ReasonCode.TryParse(capability, out _)))
        {
            throw new ArgumentException("Capabilities must be at most 32 unique capability ids.", nameof(options));
        }

        if (!TextRules.IsLanguageTag(options.UiLanguage))
        {
            throw new ArgumentException("UiLanguage must be a language tag.", nameof(options));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private HostConnection Current()
    {
        lock (_gate)
        {
            return _connection is { IsOpen: true } connection
                ? connection
                : throw new InvalidOperationException("The companion is not connected.");
        }
    }

    private ValueTask<bool> AcceptAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ValueTask.FromResult(false);
        }

        var connection = new HostConnection(this, stream, _secret, _registrationId, _manifestHash, _options);
        lock (_gate)
        {
            _connection = connection;
            _connections.Add(connection);
        }

        connection.Start();
        return ValueTask.FromResult(true);
    }
}

/// <summary>A session the test host started.</summary>
public sealed class TestHostSession
{
    private readonly object _gate = new();
    private readonly Queue<ReceivedFace> _faces = new();
    private TaskCompletionSource? _faceWaiter;

    internal TestHostSession(string sessionId, string contributionId, HostConnection connection)
    {
        SessionId = sessionId;
        ContributionId = contributionId;
        Connection = connection;
    }

    /// <summary>The session id the test host chose.</summary>
    public string SessionId { get; }

    /// <summary>The contribution the session runs.</summary>
    public string ContributionId { get; }

    /// <summary>The code the companion refused the session with (<c>sessionRefused</c>), or null when it accepted it.</summary>
    public ReasonCode? RefusedCode { get; internal set; }

    internal HostConnection Connection { get; }

    internal TaskCompletionSource Refusal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool Stopped { get; set; }

    internal List<ReceivedFace> AllFaces { get; } = [];

    internal void AddFace(ReceivedFace face)
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            _faces.Enqueue(face);
            AllFaces.Add(face);
            waiter = _faceWaiter;
            _faceWaiter = null;
        }

        waiter?.TrySetResult();
    }

    internal int FaceCount
    {
        get
        {
            lock (_gate)
            {
                return AllFaces.Count;
            }
        }
    }

    internal async Task<ReceivedFace> NextFaceAsync(TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        Task wait;
        lock (_gate)
        {
            if (_faces.Count > 0)
            {
                return _faces.Dequeue();
            }

            _faceWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = _faceWaiter.Task;
        }

        await wait.WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _faces.Dequeue();
        }
    }
}

/// <summary>An invocation the test host sent.</summary>
public sealed class TestInvocation
{
    private readonly TaskCompletionSource<TestInvokeOutcome> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TestInvocation(string requestId) => RequestId = requestId;

    /// <summary>The request id the test host chose.</summary>
    public string RequestId { get; }

    /// <summary>Completes with the outcome: the companion's result, <see cref="TestInvokeOutcomeKind.Cancelled"/> or <see cref="TestInvokeOutcomeKind.TimedOut"/>.</summary>
    public Task<TestInvokeOutcome> Result => _result.Task;

    internal HostConnection? Connection { get; set; }

    internal ITimer? Deadline { get; set; }

    internal bool Complete(TestInvokeOutcome outcome)
    {
        Deadline?.Dispose();
        return _result.TrySetResult(outcome);
    }

    internal void Fail(Exception error)
    {
        Deadline?.Dispose();
        _result.TrySetException(error);
    }
}

/// <summary>How a test invocation ended.</summary>
public enum TestInvokeOutcomeKind
{
    /// <summary>The companion answered <c>Done</c>.</summary>
    Done = 1,

    /// <summary>The companion answered <c>Refused</c>.</summary>
    Refused = 2,

    /// <summary>The companion answered <c>Failed</c>.</summary>
    Failed = 3,

    /// <summary>The companion answered <c>Unsupported</c>.</summary>
    Unsupported = 4,

    /// <summary>The test sent <c>cancel</c>; no result is counted.</summary>
    Cancelled = 5,

    /// <summary>No result within the test host's <see cref="HostLimits.InvokeTimeoutMs"/>; <c>cancel</c> was sent.</summary>
    TimedOut = 6,
}

/// <summary>The outcome of a test invocation.</summary>
public sealed class TestInvokeOutcome
{
    /// <summary>How it ended.</summary>
    public required TestInvokeOutcomeKind Kind { get; init; }

    /// <summary>The failure the companion gave with <see cref="TestInvokeOutcomeKind.Failed"/>, if any.</summary>
    public Failure? Failure { get; init; }
}

/// <summary>A face the test host received.</summary>
public sealed class ReceivedFace
{
    /// <summary>The face, as a host would hold it after cleaning (contract §7.7.3).</summary>
    public required Face Face { get; init; }

    /// <summary>When it arrived, on the test host's clock, since the test host started.</summary>
    public required TimeSpan ArrivedAt { get; init; }
}

/// <summary>A record of every frame between the test host and the companion.</summary>
public sealed class CompanionTranscript
{
    private readonly object _gate = new();
    private readonly List<TranscriptEntry> _entries = [];

    internal CompanionTranscript()
    {
    }

    /// <summary>Every entry so far, in order.</summary>
    public IReadOnlyList<TranscriptEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>The entries as JSON Lines: one object per line with <c>at</c> (milliseconds), <c>from</c>, <c>type</c>, and the ids.</summary>
    /// <returns>The transcript text.</returns>
    public string ToJsonLines()
    {
        var text = new StringBuilder();
        foreach (var entry in Entries)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("at", (long)entry.At.TotalMilliseconds);
                writer.WriteString("from", entry.From == Sender.Host ? "Host" : "Companion");
                writer.WriteString("type", entry.Type);
                if (entry.SessionId is { } sessionId)
                {
                    writer.WriteString("sessionId", sessionId);
                }

                if (entry.RequestId is { } requestId)
                {
                    writer.WriteString("requestId", requestId);
                }

                writer.WriteEndObject();
            }

            text.Append(Encoding.UTF8.GetString(buffer.ToArray())).Append('\n');
        }

        return text.ToString();
    }

    internal void Add(TranscriptEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }
}

/// <summary>One frame of a <see cref="CompanionTranscript"/>.</summary>
public sealed class TranscriptEntry
{
    /// <summary>When it was sent or received, on the test host's clock.</summary>
    public required TimeSpan At { get; init; }

    /// <summary>Who sent it.</summary>
    public required Sender From { get; init; }

    /// <summary>The message type.</summary>
    public required string Type { get; init; }

    /// <summary>The session the frame names, if any.</summary>
    public string? SessionId { get; init; }

    /// <summary>The request the frame names, if any.</summary>
    public string? RequestId { get; init; }
}

/// <summary>Records what the client tells the test kit about its handlers.</summary>
internal sealed class TestObserver : IClientObserver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<ReasonCode?>> _starts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sessionsEnded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invocationsEnded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deadlines = new(StringComparer.Ordinal);
    private readonly HashSet<string> _afterEnd = new(StringComparer.Ordinal);
    private readonly HashSet<string> _withoutFace = new(StringComparer.Ordinal);

    public Task<ReasonCode?> ExpectStart(string sessionId)
    {
        lock (_gate)
        {
            var source = new TaskCompletionSource<ReasonCode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _starts[sessionId] = source;
            return source.Task;
        }
    }

    public bool SessionEnded(string sessionId) => Contains(_sessionsEnded, sessionId);

    public bool InvocationEnded(string requestId) => Contains(_invocationsEnded, requestId);

    public bool DeadlineReached(string requestId) => Contains(_deadlines, requestId);

    public bool PublishedAfterEnd(string sessionId) => Contains(_afterEnd, sessionId);

    public bool PublishedWithoutFace(string sessionId) => Contains(_withoutFace, sessionId);

    void IClientObserver.FrameProcessed(WireMessage message)
    {
    }

    void IClientObserver.SessionStarted(string sessionId, ReasonCode? refusal)
    {
        TaskCompletionSource<ReasonCode?>? source;
        lock (_gate)
        {
            _starts.Remove(sessionId, out source);
        }

        source?.TrySetResult(refusal);
    }

    void IClientObserver.SessionHandlerEnded(string sessionId) => Add(_sessionsEnded, sessionId);

    void IClientObserver.InvocationHandlerEnded(string requestId) => Add(_invocationsEnded, requestId);

    void IClientObserver.InvocationDeadlineReached(string requestId) => Add(_deadlines, requestId);

    void IClientObserver.PublishedAfterEnd(string sessionId) => Add(_afterEnd, sessionId);

    void IClientObserver.FaceWithoutProvides(string sessionId) => Add(_withoutFace, sessionId);

    private bool Contains(HashSet<string> set, string id)
    {
        lock (_gate)
        {
            return set.Contains(id);
        }
    }

    private void Add(HashSet<string> set, string id)
    {
        lock (_gate)
        {
            set.Add(id);
        }
    }
}
