// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;

namespace VentanaTools.Orbit.Extensions;

/// <summary>Where a session's publications go: a connection, or the Testing package's recorder.</summary>
internal interface ISessionSink
{
    /// <summary>Publishes a checked, cleaned command for an active session.</summary>
    /// <returns><see cref="PublishResult.SessionEnded"/> when the session ended meanwhile.</returns>
    PublishResult Publish(Session session, FaceCommand command);

    /// <summary>The author published after the session ended; nothing is sent.</summary>
    void PublishedAfterEnd(Session session, FaceCommand command);

    /// <summary>The author tried to publish for a contribution that does not provide <c>face</c>.</summary>
    void FaceWithoutProvides(Session session);
}

/// <summary>
/// A host-started instance of one contribution with a fixed, complete set of setting values
/// (contract §7.6). A session belongs to one connection and ends with it; settings never change
/// for a session (a settings change is a new session).
/// </summary>
/// <remarks>
/// <para>
/// A widget publishes its face with <see cref="SetFace"/>, removes it with <see cref="ClearFace"/>,
/// or shows a failure the host words with <see cref="Fail"/>. The three methods are safe to call
/// from any thread, never block, and never send anything after the session ended: they return
/// <see cref="PublishResult.SessionEnded"/> instead. At most one face command per session is
/// pending; a newer one replaces it, so publishing faster than the host's budget is never a
/// failure.
/// </para>
/// <para>
/// A session contains no placement identity, no host UI contents and no information about when
/// the host shows anything (contract §7.6.4).
/// </para>
/// </remarks>
public sealed class Session
{
    private readonly ISessionSink _sink;
    private readonly HashSet<string> _capabilities;
    private int _active = 1;

    internal Session(string id, string contributionId, Provides provides, IReadOnlyDictionary<string, string> settings,
        string uiLanguage, IReadOnlyCollection<string> hostCapabilities, TimeProvider time, ISessionSink sink)
    {
        Id = id;
        ContributionId = contributionId;
        Provides = provides;
        Settings = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(settings, StringComparer.Ordinal));
        UiLanguage = uiLanguage;
        UiCulture = CultureFor(uiLanguage);
        _capabilities = new HashSet<string>(hostCapabilities, StringComparer.Ordinal);
        HostCapabilities = _capabilities.Order(StringComparer.Ordinal).ToArray().AsReadOnly();
        Time = time;
        _sink = sink;
    }

    /// <summary>The opaque session id. It does not identify a placement.</summary>
    public string Id { get; }

    /// <summary>The contribution this session runs, as declared in the manifest.</summary>
    public string ContributionId { get; }

    /// <summary>What the contribution provides, from the manifest.</summary>
    public Provides Provides { get; }

    /// <summary>One value per declared setting: the complete, validated set the host sent. Read-only.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; }

    /// <summary>The language tag of the host's UI language, as the host sent it. Write face text in it.</summary>
    public string UiLanguage { get; }

    /// <summary>The culture for <see cref="UiLanguage"/>; <see cref="CultureInfo.InvariantCulture"/> when the runtime cannot create it.</summary>
    public CultureInfo UiCulture { get; }

    /// <summary>The connection's effective capabilities: those both the host and this SDK listed (contract §7.3.5).</summary>
    public IReadOnlyCollection<string> HostCapabilities { get; }

    /// <summary>The client's clock. Handlers that measure or wait for time should use it, so tests can drive it.</summary>
    public TimeProvider Time { get; }

    /// <summary>Whether the session is still running: false after the host stopped it or the connection ended.</summary>
    public bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>Whether <paramref name="capabilityId"/> is effective on this session's connection.</summary>
    /// <param name="capabilityId">A capability id, for example <c>face.image</c>.</param>
    /// <returns>True when both the host and this SDK support it.</returns>
    public bool Supports(string capabilityId) => capabilityId is not null && _capabilities.Contains(capabilityId);

    /// <summary>
    /// Replaces the session's face. Only <see cref="Face.GoodFor"/>, the picture and
    /// <see cref="Face.State"/> are validated; the text is cleaned and cut to the display limits
    /// exactly as a host would, so text from data never makes this method throw.
    /// </summary>
    /// <param name="face">The face.</param>
    /// <returns><see cref="PublishResult.Accepted"/>, or <see cref="PublishResult.SessionEnded"/> after the session ended.</returns>
    /// <exception cref="ArgumentException"><paramref name="face"/> is null, its lifetime is not 1 second to 1 day, its glyph breaks the glyph rule, or its state is undefined.</exception>
    /// <exception cref="NotSupportedException">The face uses a picture or line variant whose capability is not effective on this connection.</exception>
    /// <exception cref="InvalidOperationException">The contribution does not provide <c>face</c>.</exception>
    public PublishResult SetFace(Face face)
    {
        FaceRules.CheckArguments(face);
        FaceRules.CheckVariants(face, this);
        RequireFace();
        return Publish(new FaceCommand { Kind = FaceCommandKind.Set, Face = FaceRules.Clean(face), Renew = face.Renew });
    }

    /// <summary>Removes the session's face.</summary>
    /// <returns><see cref="PublishResult.Accepted"/>, or <see cref="PublishResult.SessionEnded"/> after the session ended.</returns>
    /// <exception cref="InvalidOperationException">The contribution does not provide <c>face</c>.</exception>
    public PublishResult ClearFace()
    {
        RequireFace();
        return Publish(FaceCommand.Clear);
    }

    /// <summary>Replaces the session's face with a failure the host words (contract §7.8).</summary>
    /// <param name="failure">Why the contribution cannot show its state.</param>
    /// <returns><see cref="PublishResult.Accepted"/>, or <see cref="PublishResult.SessionEnded"/> after the session ended.</returns>
    /// <exception cref="ArgumentException"><paramref name="failure"/> is undefined, or a token whose capability is not effective on this connection.</exception>
    /// <exception cref="InvalidOperationException">The contribution does not provide <c>face</c>.</exception>
    public PublishResult Fail(Failure failure)
    {
        if (!FaceRules.IsBaselineFailure(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure), "The failure token is undefined or its capability is not effective.");
        }

        RequireFace();
        return Publish(new FaceCommand { Kind = FaceCommandKind.Fail, Failure = failure });
    }

    /// <summary>Ends the session: no further publication is sent.</summary>
    internal void End() => Volatile.Write(ref _active, 0);

    private void RequireFace()
    {
        if ((Provides & Provides.Face) == 0)
        {
            _sink.FaceWithoutProvides(this);
            throw new InvalidOperationException("This contribution does not provide face; add face to provides, or do not publish.");
        }
    }

    private PublishResult Publish(FaceCommand command)
    {
        if (!IsActive)
        {
            _sink.PublishedAfterEnd(this, command);
            return PublishResult.SessionEnded;
        }

        return _sink.Publish(this, command);
    }

    private static CultureInfo CultureFor(string tag)
    {
        try
        {
            return CultureInfo.GetCultureInfo(tag);
        }
        catch (ArgumentException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}

/// <summary>One request to run an action, sent for a current session and answered with one <see cref="InvokeResult"/>.</summary>
public sealed class Invocation
{
    internal Invocation(string requestId, Session session)
    {
        RequestId = requestId;
        Session = session;
    }

    /// <summary>The opaque request id. Requests are never replayed after a disconnect.</summary>
    public string RequestId { get; }

    /// <summary>The session the request was sent for, with its settings.</summary>
    public Session Session { get; }
}

/// <summary>
/// How an invocation ended (contract §7.8). Use <see cref="Done"/>, <see cref="Refused"/>,
/// <see cref="Unsupported"/> or <see cref="Failed"/>; the default value is invalid, and the SDK
/// answers it as <c>Failed</c> and raises <see cref="HandlerFault.InvalidResult"/>.
/// </summary>
public readonly struct InvokeResult : IEquatable<InvokeResult>
{
    private InvokeResult(Outcome outcome, Failure? failure)
    {
        Outcome = outcome;
        Failure = failure;
    }

    /// <summary>The outcome; 0 for the invalid default value.</summary>
    public Outcome Outcome { get; }

    /// <summary>Why it failed; only with <see cref="Outcome.Failed"/>.</summary>
    public Failure? Failure { get; }

    /// <summary>It worked.</summary>
    public static InvokeResult Done { get; } = new(Outcome.Done, null);

    /// <summary>The companion declined, for example for a stale session; the host says it didn't run.</summary>
    public static InvokeResult Refused { get; } = new(Outcome.Refused, null);

    /// <summary>The companion does not implement this action.</summary>
    public static InvokeResult Unsupported { get; } = new(Outcome.Unsupported, null);

    /// <summary>Whether this is one of the four valid results.</summary>
    internal bool IsValid => Outcome is Outcome.Done or Outcome.Refused or Outcome.Unsupported
        ? Failure is null
        : Outcome == Outcome.Failed && (Failure is null || FaceRules.IsBaselineFailure(Failure.Value));

    /// <summary>Compares two results.</summary>
    /// <param name="left">The first result.</param>
    /// <param name="right">The second result.</param>
    /// <returns>True when outcome and failure are equal.</returns>
    public static bool operator ==(InvokeResult left, InvokeResult right) => left.Equals(right);

    /// <summary>Compares two results.</summary>
    /// <param name="left">The first result.</param>
    /// <param name="right">The second result.</param>
    /// <returns>True when outcome or failure differ.</returns>
    public static bool operator !=(InvokeResult left, InvokeResult right) => !left.Equals(right);

    /// <summary>It ran and failed.</summary>
    /// <param name="failure">Why, when the companion knows; null lets the host say only that it didn't work.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="failure"/> is undefined or capability-gated.</exception>
    public static InvokeResult Failed(Failure? failure = null)
    {
        if (failure is { } value && !FaceRules.IsBaselineFailure(value))
        {
            throw new ArgumentOutOfRangeException(nameof(failure), "The failure token is undefined or capability-gated.");
        }

        return new InvokeResult(Outcome.Failed, failure);
    }

    /// <inheritdoc/>
    public bool Equals(InvokeResult other) => Outcome == other.Outcome && Failure == other.Failure;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is InvokeResult other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Outcome, Failure);

    /// <summary>The outcome, and the failure when there is one.</summary>
    /// <returns>For example <c>Failed (Network)</c>.</returns>
    public override string ToString() => Failure is { } failure ? Outcome + " (" + failure + ")" : Outcome.ToString();
}

/// <summary>
/// The bounded live state a widget publishes for one session (contract §7.7): a picture, up to two
/// lines of text, a state, a sentence for assistive technology, and a lifetime. The host renders
/// every part of it.
/// </summary>
/// <remarks>
/// <para>
/// A string converts to a line, so <c>Line1 = "4:59"</c> works. Text is cleaned and cut to the
/// display limits (40 text elements for <see cref="Line1"/>, 60 for <see cref="Line2"/>, 160 for
/// <see cref="Detail"/>) before sending; it never makes <see cref="Session.SetFace"/> throw.
/// </para>
/// <para>
/// The host shows a face as current until <see cref="GoodFor"/> after it arrives, then as stale.
/// With <see cref="Renew"/>, the SDK republishes the face at 80% of its lifetime while the
/// session's <c>RunSessionAsync</c> is still running and nothing newer was published, for at most
/// one day; a handler that stops or hangs lets the face go stale as it should.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// session.SetFace(new Face
/// {
///     Picture = FacePicture.Glyph("\uE916"),
///     Line1 = "4:59",
///     Line2 = "Focus",
///     State = FaceState.Playing,
///     Detail = "Four minutes and fifty-nine seconds left.",
///     GoodFor = TimeSpan.FromSeconds(5),
/// });
/// </code>
/// </example>
public sealed record Face
{
    /// <summary>The picture; <see cref="FacePicture.None"/> by default.</summary>
    public FacePicture Picture { get; init; } = FacePicture.None;

    /// <summary>The first line, shown in compact places; a string converts.</summary>
    public FaceLine? Line1 { get; init; }

    /// <summary>The second line, shown in larger places; a string converts.</summary>
    public FaceLine? Line2 { get; init; }

    /// <summary>The sentence assistive technology reads; null for the first line's text.</summary>
    public string? Detail { get; init; }

    /// <summary>The state; <see cref="FaceState.None"/> by default.</summary>
    public FaceState State { get; init; } = FaceState.None;

    /// <summary>How long the face stays current after it arrives: 1 second to 1 day, rounded up to whole seconds.</summary>
    public required TimeSpan GoodFor { get; init; }

    /// <summary>Whether the SDK keeps the face current by republishing it (see the remarks).</summary>
    public bool Renew { get; init; }
}

/// <summary>What happened to a publication.</summary>
public enum PublishResult
{
    /// <summary>The command is queued; a newer one for the same session replaces it.</summary>
    Accepted = 1,

    /// <summary>The session ended; nothing was sent.</summary>
    SessionEnded = 2,
}
