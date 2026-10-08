// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Wire;

/// <summary>
/// One message of protocol 3 (contract §7.3, §7.5). The hierarchy is closed: every message is
/// one of the sealed classes of this namespace, with one property per JSON member.
/// </summary>
public abstract class WireMessage
{
    private protected WireMessage()
    {
    }

    /// <summary>The message's <c>type</c> member.</summary>
    public abstract string Type { get; }
}

/// <summary><c>hello</c> (companion to host): the version offer, the registration and the companion's nonce (contract §7.3.1).</summary>
public sealed class HelloMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "hello";

    /// <summary>The lowest protocol version offered, 1 to 65535.</summary>
    public required int MinVersion { get; init; }

    /// <summary>The highest protocol version offered, <see cref="MinVersion"/> to 65535.</summary>
    public required int MaxVersion { get; init; }

    /// <summary>The pairing file's registration id.</summary>
    public required string RegistrationId { get; init; }

    /// <summary>32 fresh random bytes in canonical Base64.</summary>
    public required string ClientNonce { get; init; }

    /// <summary>The 0 to 32 unique capability ids the companion supports.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>The companion's informational name and version.</summary>
    public required ClientInfo Client { get; init; }

    /// <summary>The manifest hash of the manifest the companion loaded.</summary>
    public required string ManifestHash { get; init; }
}

/// <summary><c>challenge</c> (host to companion): the negotiated version and the host's proof (contract §7.3.2).</summary>
public sealed class ChallengeMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "challenge";

    /// <summary>32 fresh random bytes in canonical Base64.</summary>
    public required string ServerNonce { get; init; }

    /// <summary>The negotiated version.</summary>
    public required int Version { get; init; }

    /// <summary>The 0 to 32 unique capability ids the host supports.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>The host's id and version.</summary>
    public required HostIdentity Host { get; init; }

    /// <summary>The server proof (contract §7.3.4).</summary>
    public required string Proof { get; init; }
}

/// <summary><c>authenticate</c> (companion to host): the companion's proof (contract §7.3.3).</summary>
public sealed class AuthenticateMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "authenticate";

    /// <summary>The client proof (contract §7.3.4).</summary>
    public required string Proof { get; init; }
}

/// <summary><c>ready</c> (host to companion): the connection is authenticated (contract §7.3.5).</summary>
public sealed class ReadyMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "ready";

    /// <summary>The negotiated version, equal to the challenge's.</summary>
    public required int Version { get; init; }

    /// <summary>The host's capability list, equal to the challenge's.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>The host's id and version, equal to the challenge's.</summary>
    public required HostIdentity Host { get; init; }

    /// <summary>The language tag of the host's current UI language.</summary>
    public required string UiLanguage { get; init; }

    /// <summary>The host's limits (contract §7.10).</summary>
    public required HostLimits Limits { get; init; }
}

/// <summary><c>startSession</c> (host to companion): starts a session with a complete set of setting values (contract §7.6.1).</summary>
public sealed class StartSessionMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "startSession";

    /// <summary>A new session id.</summary>
    public required string SessionId { get; init; }

    /// <summary>The contribution id.</summary>
    public required string ContributionId { get; init; }

    /// <summary>One value per declared setting: at most 16 setting ids mapped to choice values.</summary>
    public required IReadOnlyDictionary<string, string> Settings { get; init; }
}

/// <summary><c>stopSession</c> (host to companion): ends a session (contract §7.6.3).</summary>
public sealed class StopSessionMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "stopSession";

    /// <summary>The session id.</summary>
    public required string SessionId { get; init; }
}

/// <summary><c>invoke</c> (host to companion): runs an action for a current session (contract §7.8).</summary>
public sealed class InvokeMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "invoke";

    /// <summary>A new request id.</summary>
    public required string RequestId { get; init; }

    /// <summary>The session id.</summary>
    public required string SessionId { get; init; }
}

/// <summary><c>cancel</c> (host to companion): cancels an invocation (contract §7.8).</summary>
public sealed class CancelMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "cancel";

    /// <summary>The request id.</summary>
    public required string RequestId { get; init; }
}

/// <summary><c>setFace</c> (companion to host): replaces a session's face (contract §7.7).</summary>
public sealed class SetFaceMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "setFace";

    /// <summary>The session id.</summary>
    public required string SessionId { get; init; }

    /// <summary>The face.</summary>
    public required WireFace Face { get; init; }
}

/// <summary><c>clearFace</c> (companion to host): removes a session's face.</summary>
public sealed class ClearFaceMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "clearFace";

    /// <summary>The session id.</summary>
    public required string SessionId { get; init; }
}

/// <summary><c>fail</c> (companion to host): replaces a session's face with a failure the host words.</summary>
public sealed class FailMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "fail";

    /// <summary>The session id.</summary>
    public required string SessionId { get; init; }

    /// <summary>The failure.</summary>
    public required Failure Failure { get; init; }
}

/// <summary><c>result</c> (companion to host): answers one invocation (contract §7.8).</summary>
public sealed class ResultMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "result";

    /// <summary>The request id.</summary>
    public required string RequestId { get; init; }

    /// <summary>The outcome.</summary>
    public required Outcome Outcome { get; init; }

    /// <summary>Why it failed; only with <see cref="Outcome.Failed"/>.</summary>
    public Failure? Failure { get; init; }
}

/// <summary><c>sessionRefused</c> (companion to host): the companion cannot run a session (contract §7.6.2).</summary>
public sealed class SessionRefusedMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "sessionRefused";

    /// <summary>The session id.</summary>
    public required string SessionId { get; init; }

    /// <summary><c>session.capacity</c>, <c>session.unknown-contribution</c> or <c>session.settings-invalid</c>.</summary>
    public required ReasonCode Code { get; init; }
}

/// <summary><c>ping</c> (either side): asks for a <c>pong</c> with the same id (contract §7.9).</summary>
public sealed class PingMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "ping";

    /// <summary>The ping id, 1 to 2147483647.</summary>
    public required int Id { get; init; }
}

/// <summary><c>pong</c> (either side): answers a <c>ping</c> (contract §7.9).</summary>
public sealed class PongMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "pong";

    /// <summary>The ping's id.</summary>
    public required int Id { get; init; }
}

/// <summary>
/// <c>error</c> (either side): a reason code, after which the sender closes unless the code is
/// advisory (contract §7.5).
/// </summary>
public sealed class ErrorMessage : WireMessage
{
    /// <inheritdoc/>
    public override string Type => "error";

    /// <summary>The reason code. <c>error.code</c> is an open registry: any grammar-valid code is accepted.</summary>
    public required ReasonCode Code { get; init; }

    /// <summary>Optional fixed English text from the sender, 1 to 256 printable ASCII characters. Never display it as host chrome.</summary>
    public string? Message { get; init; }

    /// <summary>The session the error concerns.</summary>
    public string? SessionId { get; init; }

    /// <summary>The request the error concerns.</summary>
    public string? RequestId { get; init; }

    /// <summary>How long to wait before retrying, 0 to 300,000 milliseconds.</summary>
    public int? RetryAfterMs { get; init; }

    /// <summary>The sender's supported version range; only with <c>protocol.version-unsupported</c>.</summary>
    public VersionRange? Supported { get; init; }
}

/// <summary>The <c>face</c> object of <c>setFace</c> (contract §7.7.1).</summary>
public sealed class WireFace
{
    /// <summary>The picture; <see cref="FacePicture.None"/> by default.</summary>
    public FacePicture Picture { get; init; } = FacePicture.None;

    /// <summary>The first line.</summary>
    public FaceLine? Line1 { get; init; }

    /// <summary>The second line.</summary>
    public FaceLine? Line2 { get; init; }

    /// <summary>The state; <see cref="FaceState.None"/> by default.</summary>
    public FaceState State { get; init; } = FaceState.None;

    /// <summary>The sentence assistive technology reads; null for the first line's text.</summary>
    public string? Detail { get; init; }

    /// <summary>How long the face stays current after it arrives, 1 to 86,400 seconds.</summary>
    public required int GoodForSeconds { get; init; }
}

/// <summary>The <c>client</c> object of <c>hello</c>: informational.</summary>
public sealed class ClientInfo
{
    /// <summary>The client name: <c>[A-Za-z0-9@._/+-]{1,64}</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The client version: <c>[0-9A-Za-z.+-]{1,32}</c>.</summary>
    public required string Version { get; init; }
}

/// <summary>A protocol version range.</summary>
public sealed class VersionRange
{
    /// <summary>The lowest version, 1 to 65535.</summary>
    public required int MinVersion { get; init; }

    /// <summary>The highest version, <see cref="MinVersion"/> to 65535.</summary>
    public required int MaxVersion { get; init; }
}

/// <summary>The <c>ready.limits</c> object (contract §7.10).</summary>
/// <remarks>
/// A companion uses, for each capacity and rate member, the smaller of the host's value and the
/// protocol-3 value, and for the liveness timers the larger (<see cref="ForCompanion"/>).
/// </remarks>
public sealed class HostLimits
{
    /// <summary>The largest frame body, 8,192 to 65,536.</summary>
    public required int MaxFrameBytes { get; init; }

    /// <summary>Open sessions per connection, 1 to 64.</summary>
    public required int MaxSessions { get; init; }

    /// <summary>Unanswered invocations per connection, 1 to 32.</summary>
    public required int MaxPendingInvokes { get; init; }

    /// <summary>The host's deadline per invocation, 4,000 to 60,000 milliseconds.</summary>
    public required int InvokeTimeoutMs { get; init; }

    /// <summary>The per-session face budget's refill rate, 1 to 2 per second.</summary>
    public required int FaceChangesPerSecond { get; init; }

    /// <summary>The per-session face budget's burst, 1 to 4.</summary>
    public required int FaceBurst { get; init; }

    /// <summary>The soft bucket's refill, 1 to 128 tokens per second.</summary>
    public required int MessageRate { get; init; }

    /// <summary>The soft bucket's capacity: one token per started KiB of the largest frame, to 256.</summary>
    public required int MessageBurst { get; init; }

    /// <summary>The hard bucket's refill, <see cref="MessageRate"/> to 512 tokens per second.</summary>
    public required int HardMessageRate { get; init; }

    /// <summary>The hard bucket's capacity, <see cref="MessageBurst"/> to 1,024 tokens.</summary>
    public required int HardMessageBurst { get; init; }

    /// <summary>The idle time before a ping, 5,000 to 300,000 milliseconds and greater than <see cref="PongTimeoutMs"/>.</summary>
    public required int PingIntervalMs { get; init; }

    /// <summary>How long a pong may take, 1,000 to 60,000 milliseconds.</summary>
    public required int PongTimeoutMs { get; init; }

    /// <summary>The values protocol-3 hosts send.</summary>
    public static HostLimits Protocol3Defaults { get; } = new()
    {
        MaxFrameBytes = 65_536,
        MaxSessions = 64,
        MaxPendingInvokes = 32,
        InvokeTimeoutMs = 15_000,
        FaceChangesPerSecond = 2,
        FaceBurst = 4,
        MessageRate = 128,
        MessageBurst = 256,
        HardMessageRate = 512,
        HardMessageBurst = 1_024,
        PingIntervalMs = 30_000,
        PongTimeoutMs = 10_000,
    };

    /// <summary>Whether every member is within its range and every cross-member rule of contract §7.10 holds.</summary>
    /// <returns>True when the limits are acceptable in a <c>ready</c>.</returns>
    public bool IsWithinRanges() =>
        MaxFrameBytes is >= 8_192 and <= 65_536
        && MaxSessions is >= 1 and <= 64
        && MaxPendingInvokes is >= 1 and <= 32
        && InvokeTimeoutMs is >= 4_000 and <= 60_000
        && FaceChangesPerSecond is >= 1 and <= 2
        && FaceBurst is >= 1 and <= 4
        && MessageRate is >= 1 and <= 128
        && MessageBurst >= (MaxFrameBytes + 1_023) / 1_024 && MessageBurst <= 256
        && HardMessageRate >= MessageRate && HardMessageRate <= 512
        && HardMessageBurst >= MessageBurst && HardMessageBurst <= 1_024
        && PingIntervalMs is >= 5_000 and <= 300_000
        && PongTimeoutMs is >= 1_000 and <= 60_000
        && PingIntervalMs > PongTimeoutMs;

    /// <summary>
    /// The values a companion uses: for capacity and rate members the smaller of each value and
    /// the protocol-3 value, for <see cref="PingIntervalMs"/> and <see cref="PongTimeoutMs"/> the larger.
    /// </summary>
    /// <returns>The companion's limits.</returns>
    public HostLimits ForCompanion()
    {
        var d = Protocol3Defaults;
        return new HostLimits
        {
            MaxFrameBytes = Math.Min(MaxFrameBytes, d.MaxFrameBytes),
            MaxSessions = Math.Min(MaxSessions, d.MaxSessions),
            MaxPendingInvokes = Math.Min(MaxPendingInvokes, d.MaxPendingInvokes),
            InvokeTimeoutMs = Math.Min(InvokeTimeoutMs, d.InvokeTimeoutMs),
            FaceChangesPerSecond = Math.Min(FaceChangesPerSecond, d.FaceChangesPerSecond),
            FaceBurst = Math.Min(FaceBurst, d.FaceBurst),
            MessageRate = Math.Min(MessageRate, d.MessageRate),
            MessageBurst = Math.Min(MessageBurst, d.MessageBurst),
            HardMessageRate = Math.Min(HardMessageRate, d.HardMessageRate),
            HardMessageBurst = Math.Min(HardMessageBurst, d.HardMessageBurst),
            PingIntervalMs = Math.Max(PingIntervalMs, d.PingIntervalMs),
            PongTimeoutMs = Math.Max(PongTimeoutMs, d.PongTimeoutMs),
        };
    }
}
