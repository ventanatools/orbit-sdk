// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Reflection;
using System.Threading.Channels;
using VentanaTools.Orbit.Extensions.Wire;

namespace VentanaTools.Orbit.Extensions;

/// <summary>
/// A bounded recency window of ended ids (contract §7.6.3): the last <c>capacity</c> ids, so a
/// reused id is caught without a set that grows with uptime.
/// </summary>
internal sealed class ReplayWindow
{
    private readonly int _capacity;
    private readonly Queue<string> _order = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public ReplayWindow(int capacity) => _capacity = capacity;

    public int Count => _ids.Count;

    public bool Contains(string id) => _ids.Contains(id);

    public void Add(string id)
    {
        if (!_ids.Add(id))
        {
            return;
        }

        _order.Enqueue(id);
        while (_order.Count > _capacity)
        {
            _ids.Remove(_order.Dequeue());
        }
    }
}

/// <summary>
/// Raises events on the thread pool, one at a time and in order, never on the pipe reader. A
/// subscriber that throws is caught and reported, and never stops the queue (contract §9.2).
/// </summary>
internal sealed class EventQueue
{
    private readonly Channel<Action> _channel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<Exception> _subscriberFaulted;
    private readonly object _gate = new();
    private Task? _pump;
    private int _pending;
    private TaskCompletionSource? _drained;

    public EventQueue(Action<Exception> subscriberFaulted) => _subscriberFaulted = subscriberFaulted;

    public void Post(Action action)
    {
        lock (_gate)
        {
            _pending++;
            _pump ??= Task.Run(PumpAsync);
        }

        _channel.Writer.TryWrite(action);
    }

    /// <summary>Completes when every posted event has been raised.</summary>
    public Task DrainAsync()
    {
        lock (_gate)
        {
            if (_pending == 0)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    private async Task PumpAsync()
    {
        await foreach (var action in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                Report(error);
            }

            TaskCompletionSource? drained = null;
            lock (_gate)
            {
                if (--_pending == 0)
                {
                    drained = _drained;
                    _drained = null;
                }
            }

            drained?.TrySetResult();
        }
    }

    private void Report(Exception error)
    {
        try
        {
            _subscriberFaulted(error);
        }
        catch (Exception)
        {
            // A reporter that throws must not stop the queue either.
        }
    }
}

/// <summary>An auto-reset signal a single waiter awaits.</summary>
internal sealed class AsyncSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _set;

    public void Set()
    {
        lock (_gate)
        {
            _set = true;
            _source.TrySetResult();
        }
    }

    /// <summary>Waits for the signal, at most <paramref name="timeout"/> (infinite when null), on <paramref name="time"/>.</summary>
    /// <returns>True when signalled, false on timeout.</returns>
    public async Task<bool> WaitAsync(TimeSpan? timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        Task wait;
        lock (_gate)
        {
            if (_set)
            {
                _set = false;
                _source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return true;
            }

            wait = _source.Task;
        }

        try
        {
            if (timeout is { } bound)
            {
                await wait.WaitAsync(bound < TimeSpan.Zero ? TimeSpan.Zero : bound, time, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            return false;
        }

        lock (_gate)
        {
            if (_set)
            {
                _set = false;
                _source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return true;
    }
}

/// <summary>
/// Running author handlers, counted across reconnects (contract §9.2): a handler that ignores
/// cancellation keeps its slot until it ends, so reconnecting never accumulates unbounded work.
/// </summary>
internal sealed class HandlerTracker
{
    public const int MaxRunningSessions = 64;
    public const int MaxRunningInvocations = 32;
    private int _sessions;
    private int _invocations;

    public int RunningSessions => Volatile.Read(ref _sessions);

    public int RunningInvocations => Volatile.Read(ref _invocations);

    public bool TryEnterSession() => TryEnter(ref _sessions, MaxRunningSessions);

    public bool TryEnterInvocation() => TryEnter(ref _invocations, MaxRunningInvocations);

    public void ExitSession() => Interlocked.Decrement(ref _sessions);

    public void ExitInvocation() => Interlocked.Decrement(ref _invocations);

    private static bool TryEnter(ref int count, int limit)
    {
        if (Interlocked.Increment(ref count) <= limit)
        {
            return true;
        }

        Interlocked.Decrement(ref count);
        return false;
    }
}

/// <summary>The <c>hello.client</c> value: the author package's assembly name and informational version, read at run time.</summary>
internal static class ClientIdentity
{
    private const int MaxNameLength = 64;
    private const int MaxVersionLength = 32;

    public static ClientInfo Info { get; } = Create();

    private static ClientInfo Create()
    {
        var assembly = typeof(CompanionClient).Assembly;
        var name = assembly.GetName().Name ?? string.Empty;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? string.Empty;
        return new ClientInfo
        {
            Name = Fit(name, MaxNameLength, static c => char.IsAsciiLetterOrDigit(c) || c is '@' or '.' or '_' or '/' or '+' or '-', "client"),
            Version = FitVersion(version),
        };
    }

    /// <summary>
    /// The version within <c>[0-9A-Za-z.+-]{1,32}</c>: the informational version as it is when it
    /// fits, else without its build metadata (the source revision a build appends after <c>+</c>).
    /// </summary>
    internal static string FitVersion(string version)
    {
        static bool Allowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-';
        if (version.Length is > 0 and <= MaxVersionLength && version.All(Allowed))
        {
            return version;
        }

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return Fit(plus >= 0 ? version[..plus] : version, MaxVersionLength, Allowed, "0.0.0");
    }

    private static string Fit(string value, int maxLength, Func<char, bool> allowed, string fallback)
    {
        var kept = new string(value.Where(allowed).Take(maxLength).ToArray());
        return kept.Length == 0 ? fallback : kept;
    }
}
