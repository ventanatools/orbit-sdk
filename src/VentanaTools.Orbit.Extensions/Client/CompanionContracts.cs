// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions;

/// <summary>Callbacks run outside the pipe reader. Honor cancellation before doing work.</summary>
public interface IContributionHandler
{
    /// <summary>Runs for one opaque instance's lifetime. Stop all renewal work when cancelled.</summary>
    Task RunSessionAsync(CompanionSession session, CancellationToken cancellationToken);

    /// <summary>Runs once per accepted request. Requests are never replayed after a disconnect.</summary>
    Task<CompanionOutcome> InvokeAsync(CompanionInvocation invocation, CancellationToken cancellationToken);
}

public enum CompanionOutcome { Done, Refused, Failed, Unsupported }
public enum CompanionFailure { NeedsSetup, NoData, Network, Unsupported }
public enum CompanionFaceState { None, Playing, Paused, On, Off }

/// <summary>A finite text/glyph face. The host still validates and cleans every received face.</summary>
public sealed record CompanionFace(string Line1, int GoodForSeconds)
{
    public string? Line2 { get; init; }
    public string? Detail { get; init; }
    public string? Glyph { get; init; }
    public CompanionFaceState State { get; init; }
}

/// <summary>An opaque connection-scoped instance. It contains no ring or placement identity.</summary>
public sealed class CompanionSession
{
    private readonly Func<CompanionSession, byte[], bool> _publish;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _hasFace;
    private int _active = 1;

    internal CompanionSession(string sessionId, string actionId, IReadOnlyDictionary<string, string> settings,
        bool hasFace, Func<CompanionSession, byte[], bool> publish, CancellationToken cancellationToken)
    {
        SessionId = sessionId;
        ActionId = actionId;
        Settings = settings;
        _hasFace = hasFace;
        _cancellationToken = cancellationToken;
        _publish = publish;
    }

    public string SessionId { get; }
    public string ActionId { get; }
    public IReadOnlyDictionary<string, string> Settings { get; }
    public bool IsActive => Volatile.Read(ref _active) != 0 && !_cancellationToken.IsCancellationRequested;

    /// <summary>False means retired, command-only, or the bounded outbound queue is full.</summary>
    public bool SetFace(CompanionFace face) =>
        IsActive && _hasFace && _publish(this, CompanionMessages.Face(SessionId, face));

    public bool ClearFace() =>
        IsActive && _hasFace && _publish(this, CompanionMessages.Clear(SessionId));

    public bool Fail(CompanionFailure failure) =>
        IsActive && _hasFace && _publish(this, CompanionMessages.Fail(SessionId, failure));

    internal void Retire() => Interlocked.Exchange(ref _active, 0);
    public override string ToString() => "CompanionSession";
}

/// <summary>The session is the immutable configuration captured for this request.</summary>
public sealed class CompanionInvocation
{
    internal CompanionInvocation(string requestId, CompanionSession session)
    {
        RequestId = requestId;
        Session = session;
    }

    public string RequestId { get; }
    public CompanionSession Session { get; }
    public override string ToString() => "CompanionInvocation";
}
