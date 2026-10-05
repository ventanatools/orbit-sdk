// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Seth Cottle

using System.Threading.Channels;
using Orbit.Extensions.Protocol;

namespace Orbit.Extensions.Sdk;

internal sealed class CompanionConnection
{
    internal const int SessionLimit = 64;
    internal const int InvokeLimit = 32;
    private const int OutboundLimit = 256;
    private const int SeenLimit = 4096;
    private readonly object _gate = new();
    private readonly Stream _stream;
    private readonly IContributionHandler _handler;
    private readonly CompanionClient _owner;
    private readonly CancellationTokenSource _stop;
    private readonly Dictionary<string, ExternalExtensionAction> _actions;
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CallEntry> _calls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenCalls = new(StringComparer.Ordinal);
    private readonly Channel<Outbound> _outbound = Channel.CreateBounded<Outbound>(
        new BoundedChannelOptions(OutboundLimit) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private bool _closed;

    internal CompanionConnection(Stream stream, ExternalExtensionManifest manifest, IContributionHandler handler,
        CompanionClient owner, CancellationToken cancellationToken)
    {
        _stream = stream;
        _handler = handler;
        _owner = owner;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _actions = manifest.Actions.ToDictionary(action => action.Id, StringComparer.Ordinal);
    }

    internal async Task RunAsync()
    {
        var read = ReadAsync();
        var write = WriteAsync();
        try
        {
            await Task.WhenAny(read, write).ConfigureAwait(false);
            Stop();
            await Task.WhenAll(read, write).ConfigureAwait(false);
        }
        finally
        {
            Stop();
            await CancelSafelyAsync(_stop).ConfigureAwait(false);
            _stop.Dispose();
        }
    }

    private async Task ReadAsync()
    {
        var window = Environment.TickCount64;
        var count = 0;
        while (await ExtensionWire.ReadAsync(_stream, _stop.Token).ConfigureAwait(false) is { } bytes)
        {
            var now = Environment.TickCount64;
            if (now - window >= 1000) { window = now; count = 0; }
            // The host can legitimately send all starts, replacements and cancels in one burst.
            if (++count > 512) throw new InvalidDataException("Extension message rate exceeded.");
            var message = CompanionMessages.Read(bytes, _actions);
            switch (message.Type)
            {
                case "startSession": Start(message); break;
                case "stopSession": StopSession(message.SessionId!); break;
                case "invoke": Invoke(message); break;
                case "cancel": Cancel(message.RequestId!); break;
            }
        }
    }

    private void Start(HostMessage message)
    {
        SessionEntry entry;
        lock (_gate)
        {
            if (_closed) return;
            if (_sessions.Count >= SessionLimit || _seenSessions.Count >= SeenLimit
                || !_seenSessions.Add(message.SessionId!) || !_owner.TryEnterSession())
                throw new InvalidDataException("Extension session limit exceeded.");
            // Author tokens are deliberately not linked to the reader token: linked token
            // cancellation runs registrations synchronously on the cancelling thread.
            var cancellation = new CancellationTokenSource();
            var session = new CompanionSession(message.SessionId!, message.Action!.Id, message.Settings!,
                message.Action.HasFace, Publish, cancellation.Token);
            entry = new SessionEntry(session, cancellation, _owner);
            _sessions.Add(session.SessionId, entry);
        }
        // An author's synchronous prefix must not block the authenticated pipe reader.
        _ = Task.Run(async () =>
        {
            try
            {
                entry.Token.ThrowIfCancellationRequested();
                await _handler.RunSessionAsync(entry.Session, entry.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (entry.Token.IsCancellationRequested) { }
            catch (Exception) { entry.Session.Fail(CompanionFailure.NoData); }
            finally { entry.HandlerEnded(); }
        }, CancellationToken.None);
    }

    private void StopSession(string sessionId)
    {
        SessionEntry? entry;
        CallEntry[] calls;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out entry)) return; // Late stops cannot affect a new handle.
            entry.Session.Retire();
            calls = _calls.Values.Where(call => ReferenceEquals(call.Invocation.Session, entry.Session)).ToArray();
        }
        entry.Stop();
        foreach (var call in calls) call.Cancel();
    }

    private void Invoke(HostMessage message)
    {
        CallEntry? call = null;
        var refused = false;
        lock (_gate)
        {
            if (_closed) return;
            if (_seenCalls.Count >= SeenLimit || !_seenCalls.Add(message.RequestId!))
                throw new InvalidDataException("Extension request limit exceeded.");
            if (!message.Action!.CanInvoke || !_sessions.TryGetValue(message.SessionId!, out var session)
                || !session.Session.IsActive || session.Session.ActionId != message.Action.Id
                || !CompanionMessages.SameSettings(session.Session.Settings, message.Settings!)
                || _calls.Count >= InvokeLimit || !_owner.TryEnterInvoke())
                refused = true;
            else
            {
                call = new CallEntry(new CompanionInvocation(message.RequestId!, session.Session), _owner);
                _calls.Add(message.RequestId!, call);
            }
        }
        if (refused)
        {
            if (!Send(new Outbound(CompanionMessages.Result(message.RequestId!, CompanionOutcome.Refused)))) Stop();
            return;
        }
        call!.StartDeadline();
        _ = Task.Run(() => RunInvokeAsync(call), CancellationToken.None);
    }

    private async Task RunInvokeAsync(CallEntry call)
    {
        var outcome = CompanionOutcome.Failed;
        try
        {
            call.Token.ThrowIfCancellationRequested();
            if (call.Invocation.Session.IsActive)
                outcome = await _handler.InvokeAsync(call.Invocation, call.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.Token.IsCancellationRequested) { outcome = CompanionOutcome.Refused; }
        catch (Exception) { outcome = CompanionOutcome.Failed; }
        finally
        {
            var send = false;
            lock (_gate)
            {
                _calls.Remove(call.Invocation.RequestId);
                send = !_closed && !call.Token.IsCancellationRequested && Current(call.Invocation.Session);
            }
            if (send && !Send(new Outbound(CompanionMessages.Result(call.Invocation.RequestId, outcome),
                call.Invocation.Session, call.Token))) Stop();
            call.HandlerEnded();
        }
    }

    private void Cancel(string requestId)
    {
        CallEntry? call;
        lock (_gate) _calls.TryGetValue(requestId, out call);
        call?.Cancel();
    }

    private bool Publish(CompanionSession session, byte[] message)
    {
        lock (_gate)
            return !_closed && Current(session) && _outbound.Writer.TryWrite(new Outbound(message, session));
    }

    private bool Current(CompanionSession session) =>
        session.IsActive && _sessions.TryGetValue(session.SessionId, out var entry)
            && ReferenceEquals(entry.Session, session);

    private bool Send(Outbound message)
    {
        lock (_gate) return !_closed && _outbound.Writer.TryWrite(message);
    }

    private async Task WriteAsync()
    {
        // A rolling bound leaves room below the host's 128 messages/second limit.
        // The bounded queue admits a burst without allowing an author loop to flood the host.
        var recent = new Queue<long>();
        await foreach (var message in _outbound.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
        {
            while (recent.Count >= 64)
            {
                var wait = 1001 - (Environment.TickCount64 - recent.Peek());
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), _stop.Token).ConfigureAwait(false);
                while (recent.TryPeek(out var sent) && Environment.TickCount64 - sent >= 1000) recent.Dequeue();
            }
            lock (_gate)
                if (_closed || message.CancellationToken.IsCancellationRequested
                    || message.Session is { } session && !Current(session)) continue;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            deadline.CancelAfter(ExtensionWire.FrameTimeout);
            await ExtensionWire.WriteAsync(_stream, message.Bytes, deadline.Token).ConfigureAwait(false);
            recent.Enqueue(Environment.TickCount64);
        }
    }

    private void Stop()
    {
        SessionEntry[] sessions;
        CallEntry[] calls;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            sessions = _sessions.Values.ToArray();
            calls = _calls.Values.ToArray();
            foreach (var entry in sessions) entry.Session.Retire();
            _sessions.Clear();
            _calls.Clear();
            _outbound.Writer.TryComplete();
        }
        // Author cancellation registrations can run here; never call them under _gate.
        _ = CancelSafelyAsync(_stop);
        foreach (var entry in sessions) entry.Stop();
        foreach (var call in calls) call.Cancel();
    }

    private static async Task CancelSafelyAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException) { /* An author callback must not prevent the other contexts retiring. */ }
        catch (ObjectDisposedException) { /* Its worker already finished. */ }
    }

    private sealed record Outbound(byte[] Bytes, CompanionSession? Session = null, CancellationToken CancellationToken = default);

    private sealed class SessionEntry(CompanionSession session, CancellationTokenSource cancellation, CompanionClient owner)
    {
        private readonly TaskCompletionSource _handlerEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stopped;
        internal CompanionSession Session { get; } = session;
        internal CancellationToken Token { get; } = cancellation.Token;
        internal void HandlerEnded() => _handlerEnded.TrySetResult();
        internal void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0) _ = FinishAsync();
        }

        private async Task FinishAsync()
        {
            // Keep the global reservation while either author work or cancellation callbacks
            // remain alive. Reconnecting cannot accumulate abandoned work without a bound.
            await CancelSafelyAsync(cancellation).ConfigureAwait(false);
            await _handlerEnded.Task.ConfigureAwait(false);
            cancellation.Dispose();
            owner.ExitSession();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "HandlerEnded owns async disposal after author work and cancellation callbacks complete.")]
    private sealed class CallEntry(CompanionInvocation invocation, CompanionClient owner)
    {
        private readonly object _lifecycle = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly CancellationTokenSource _deadlineStop = new();
        private Task? _cancellationTask;
        private bool _ended;
        internal CompanionInvocation Invocation { get; } = invocation;
        internal CancellationToken Token => _cancellation.Token;

        internal void StartDeadline() => _ = DeadlineAsync();

        private async Task DeadlineAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(12), _deadlineStop.Token).ConfigureAwait(false);
                Cancel();
            }
            catch (OperationCanceledException) { }
        }

        internal void Cancel()
        {
            lock (_lifecycle)
                if (!_ended) _cancellationTask ??= CancelSafelyAsync(_cancellation);
        }

        internal void HandlerEnded() => _ = FinishAsync();

        private async Task FinishAsync()
        {
            Task? cancellationTask;
            lock (_lifecycle)
            {
                if (_ended) return;
                _ended = true;
                cancellationTask = _cancellationTask;
            }
            _deadlineStop.Cancel();
            if (cancellationTask is not null) await cancellationTask.ConfigureAwait(false);
            _deadlineStop.Dispose();
            _cancellation.Dispose();
            owner.ExitInvoke();
        }
    }
}
