// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace VentanaTools.Orbit.Extensions.Wire;

/// <summary>Which side sent a frame.</summary>
public enum Sender
{
    /// <summary>The host.</summary>
    Host = 1,

    /// <summary>The companion.</summary>
    Companion = 2,
}

/// <summary>Where a connection is, as the receiver of a frame sees it (contract §7.3).</summary>
public enum ConnectionPhase
{
    /// <summary>Before the receiver verified its peer's proof.</summary>
    Handshake = 1,

    /// <summary>After the receiver verified its peer's proof, until <c>ready</c>.</summary>
    PeerVerified = 2,

    /// <summary>After <c>ready</c>.</summary>
    Authenticated = 3,
}

/// <summary>The outcome of <see cref="MessageReader.Read"/>: a message, a violation, or an ignored frame.</summary>
public sealed class MessageReadResult
{
    internal MessageReadResult(WireMessage? message, ReasonCode? violation, ReasonCode? ignored)
    {
        Message = message;
        Violation = violation;
        Ignored = ignored;
    }

    /// <summary>The message, when the frame is valid.</summary>
    public WireMessage? Message { get; }

    /// <summary>The code to close with, when the frame is refused.</summary>
    public ReasonCode? Violation { get; }

    /// <summary>Why the frame was discarded (for example <c>protocol.type-unknown</c>), when it is ignored.</summary>
    public ReasonCode? Ignored { get; }
}

/// <summary>
/// Reads and validates one frame body against every rule of contract §7.2, §7.3, §7.5 and
/// §7.11: framing limits, UTF-8, strict JSON, member rules, message direction and phase, and each
/// message's members. It never throws for any frame content.
/// </summary>
/// <remarks>
/// <para>
/// Member names, <c>type</c> values and <c>$type</c> values compare ordinally after unescaping.
/// An unknown member whose name matches <c>[a-z][A-Za-z0-9]{0,31}</c> and is not a case variant of
/// a known member is ignored; any other unknown name is <c>protocol.member-invalid</c>. An unknown
/// message type after authentication is ignored. Before the receiver has verified its peer, an
/// <c>error</c> with a known code that lacks the Pre flag is <c>protocol.unexpected</c>.
/// </para>
/// <para>
/// The reader is stateless: replay windows, ping pacing, session and request bookkeeping and the
/// equality of <c>ready</c> with <c>challenge</c> belong to the caller. It is safe to call from
/// any thread.
/// </para>
/// </remarks>
public static class MessageReader
{
    private static readonly FrozenDictionary<string, (Sender[] Senders, ConnectionPhase[] Phases)> Types =
        new Dictionary<string, (Sender[], ConnectionPhase[])>(StringComparer.Ordinal)
        {
            ["hello"] = ([Sender.Companion], [ConnectionPhase.Handshake]),
            ["authenticate"] = ([Sender.Companion], [ConnectionPhase.Handshake]),
            ["challenge"] = ([Sender.Host], [ConnectionPhase.Handshake]),
            ["ready"] = ([Sender.Host], [ConnectionPhase.PeerVerified]),
            ["error"] = ([Sender.Host, Sender.Companion], [ConnectionPhase.Handshake, ConnectionPhase.PeerVerified, ConnectionPhase.Authenticated]),
            ["startSession"] = ([Sender.Host], [ConnectionPhase.Authenticated]),
            ["stopSession"] = ([Sender.Host], [ConnectionPhase.Authenticated]),
            ["invoke"] = ([Sender.Host], [ConnectionPhase.Authenticated]),
            ["cancel"] = ([Sender.Host], [ConnectionPhase.Authenticated]),
            ["setFace"] = ([Sender.Companion], [ConnectionPhase.Authenticated]),
            ["clearFace"] = ([Sender.Companion], [ConnectionPhase.Authenticated]),
            ["fail"] = ([Sender.Companion], [ConnectionPhase.Authenticated]),
            ["result"] = ([Sender.Companion], [ConnectionPhase.Authenticated]),
            ["sessionRefused"] = ([Sender.Companion], [ConnectionPhase.Authenticated]),
            ["ping"] = ([Sender.Host, Sender.Companion], [ConnectionPhase.Authenticated]),
            ["pong"] = ([Sender.Host, Sender.Companion], [ConnectionPhase.Authenticated]),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly ReasonCode[] RefusalCodes =
        [ReasonCode.SessionCapacity, ReasonCode.SessionUnknownContribution, ReasonCode.SessionSettingsInvalid];

    /// <summary>Reads one frame body.</summary>
    /// <param name="frame">The frame body, without its length prefix.</param>
    /// <param name="sender">Who sent the frame.</param>
    /// <param name="phase">The receiver's phase.</param>
    /// <returns>The message, the violation to close with, or the reason the frame was ignored.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sender"/> or <paramref name="phase"/> is undefined.</exception>
    public static MessageReadResult Read(ReadOnlySpan<byte> frame, Sender sender, ConnectionPhase phase)
    {
        if (sender is not (Sender.Host or Sender.Companion))
        {
            throw new ArgumentOutOfRangeException(nameof(sender));
        }

        if (phase is not (ConnectionPhase.Handshake or ConnectionPhase.PeerVerified or ConnectionPhase.Authenticated))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (frame.Length is 0 or > Framing.MaxFrameBytes
            || (phase != ConnectionPhase.Authenticated && frame.Length > Framing.MaxHandshakeFrameBytes))
        {
            return Refuse(ReasonCode.FrameTooLarge);
        }

        var parse = JsonTree.Parse(frame);
        if (parse.Failure == JsonFailure.Utf8)
        {
            return Refuse(ReasonCode.FrameUtf8Invalid);
        }

        // §7.2: strings must not contain unpaired surrogates after unescaping.
        if (parse.Root is not { Kind: JsonKind.Object } root || parse.UnpairedSurrogate)
        {
            return Refuse(ReasonCode.FrameJsonInvalid);
        }

        if (parse.Duplicates.Count > 0)
        {
            return Refuse(ReasonCode.FrameJsonDuplicate);
        }

        if (HasNull(root))
        {
            return Refuse(IsHello(root) ? ReasonCode.ProtocolHelloInvalid : ReasonCode.ProtocolMessageInvalid);
        }

        var typeMember = root.Members!.FirstOrDefault(member => member.Name == "type");
        if (typeMember is not { Value: { Kind: JsonKind.String, Text: { } type } })
        {
            return Refuse(ReasonCode.ProtocolMessageInvalid);
        }

        if (!Types.TryGetValue(type, out var rule))
        {
            if (phase != ConnectionPhase.Authenticated)
            {
                return Refuse(ReasonCode.ProtocolUnexpected);
            }

            return Grammars.IsMemberName(type)
                ? new MessageReadResult(null, null, ReasonCode.ProtocolTypeUnknown)
                : Refuse(ReasonCode.ProtocolMessageInvalid);
        }

        if (!rule.Senders.Contains(sender) || !rule.Phases.Contains(phase))
        {
            return Refuse(ReasonCode.ProtocolUnexpected);
        }

        var parser = new Parser(type == "hello" ? ReasonCode.ProtocolHelloInvalid : ReasonCode.ProtocolMessageInvalid);
        var message = parser.Message(type, root, phase);
        if (parser.Failure is { } failure)
        {
            return Refuse(failure);
        }

        return new MessageReadResult(message, null, null);
    }

    private static MessageReadResult Refuse(ReasonCode code) => new(null, code, null);

    private static bool IsHello(JsonNode root) =>
        root.Members!.Any(member => member.Name == "type" && member.Value.Kind == JsonKind.String && member.Value.Text == "hello");

    private static bool HasNull(JsonNode node) => node.Kind switch
    {
        JsonKind.Null => true,
        JsonKind.Object => node.Members!.Any(member => HasNull(member.Value)),
        JsonKind.Array => node.Items!.Any(HasNull),
        _ => false,
    };

    /// <summary>Validates members, remembering the first failure.</summary>
    private sealed class Parser(ReasonCode invalid)
    {
        public ReasonCode? Failure { get; private set; }

        public WireMessage? Message(string type, JsonNode root, ConnectionPhase phase) => type switch
        {
            "hello" => Hello(root),
            "challenge" => Challenge(root),
            "authenticate" => Authenticate(root),
            "ready" => Ready(root),
            "startSession" => StartSession(root),
            "stopSession" => StopSession(root),
            "invoke" => Invoke(root),
            "cancel" => Cancel(root),
            "setFace" => SetFace(root),
            "clearFace" => ClearFace(root),
            "fail" => FailBody(root),
            "result" => Result(root),
            "sessionRefused" => SessionRefused(root),
            "ping" => Ping(root),
            "pong" => Pong(root),
            _ => Error(root, phase),
        };

        private HelloMessage? Hello(JsonNode root)
        {
            var m = Members(root, "type", "minVersion", "maxVersion", "registrationId", "clientNonce", "capabilities", "client", "manifestHash");
            var min = Int(m, "minVersion", 1, 65_535);
            var max = Int(m, "maxVersion", 1, 65_535);
            var registration = Guid(m, "registrationId");
            var nonce = Key(m, "clientNonce");
            var capabilities = CapabilityList(m);
            var client = Client(m);
            var hash = Hash(m, "manifestHash");
            if (Failure is null && min > max)
            {
                Fail(invalid);
            }

            return Failure is null
                ? new HelloMessage
                {
                    MinVersion = min,
                    MaxVersion = max,
                    RegistrationId = registration!,
                    ClientNonce = nonce!,
                    Capabilities = capabilities!,
                    Client = client!,
                    ManifestHash = hash!,
                }
                : null;
        }

        private ChallengeMessage? Challenge(JsonNode root)
        {
            var m = Members(root, "type", "serverNonce", "version", "capabilities", "host", "proof");
            var nonce = Key(m, "serverNonce");
            var version = Int(m, "version", 1, 65_535);
            var capabilities = CapabilityList(m);
            var host = Host(m);
            var proof = Key(m, "proof");
            return Failure is null
                ? new ChallengeMessage { ServerNonce = nonce!, Version = version, Capabilities = capabilities!, Host = host!, Proof = proof! }
                : null;
        }

        private AuthenticateMessage? Authenticate(JsonNode root)
        {
            var m = Members(root, "type", "proof");
            var proof = Key(m, "proof");
            return Failure is null ? new AuthenticateMessage { Proof = proof! } : null;
        }

        private ReadyMessage? Ready(JsonNode root)
        {
            var m = Members(root, "type", "version", "capabilities", "host", "uiLanguage", "limits");
            var version = Int(m, "version", 1, 65_535);
            var capabilities = CapabilityList(m);
            var host = Host(m);
            var language = Str(m, "uiLanguage");
            if (language is not null && !TextRules.IsLanguageTag(language))
            {
                Fail(invalid);
            }

            var limits = Limits(m);
            return Failure is null
                ? new ReadyMessage { Version = version, Capabilities = capabilities!, Host = host!, UiLanguage = language!, Limits = limits! }
                : null;
        }

        private StartSessionMessage? StartSession(JsonNode root)
        {
            var m = Members(root, "type", "sessionId", "contributionId", "settings");
            var session = Guid(m, "sessionId");
            var contribution = Str(m, "contributionId");
            if (contribution is not null && ExtensionIds.Classify(contribution, IdOrigin.ThirdParty, []) is not null)
            {
                Fail(invalid);
            }

            IReadOnlyDictionary<string, string>? settings = null;
            if (Required(m, "settings") is { } node)
            {
                if (node.Kind != JsonKind.Object || node.Members!.Count > 16)
                {
                    Fail(invalid);
                }
                else
                {
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var member in node.Members!)
                    {
                        if (!TextRules.IsSettingId(member.Name) || member.Value.Kind != JsonKind.String
                            || !TextRules.IsChoiceValue(member.Value.Text))
                        {
                            Fail(invalid);
                            break;
                        }

                        map[member.Name] = member.Value.Text;
                    }

                    settings = new ReadOnlyDictionary<string, string>(map);
                }
            }

            return Failure is null
                ? new StartSessionMessage { SessionId = session!, ContributionId = contribution!, Settings = settings! }
                : null;
        }

        private StopSessionMessage? StopSession(JsonNode root)
        {
            var m = Members(root, "type", "sessionId");
            var session = Guid(m, "sessionId");
            return Failure is null ? new StopSessionMessage { SessionId = session! } : null;
        }

        private InvokeMessage? Invoke(JsonNode root)
        {
            var m = Members(root, "type", "requestId", "sessionId");
            var request = Guid(m, "requestId");
            var session = Guid(m, "sessionId");
            return Failure is null ? new InvokeMessage { RequestId = request!, SessionId = session! } : null;
        }

        private CancelMessage? Cancel(JsonNode root)
        {
            var m = Members(root, "type", "requestId");
            var request = Guid(m, "requestId");
            return Failure is null ? new CancelMessage { RequestId = request! } : null;
        }

        private SetFaceMessage? SetFace(JsonNode root)
        {
            var m = Members(root, "type", "sessionId", "face");
            var session = Guid(m, "sessionId");
            WireFace? face = null;
            if (Required(m, "face") is { } node)
            {
                face = Face(node);
            }

            return Failure is null ? new SetFaceMessage { SessionId = session!, Face = face! } : null;
        }

        private ClearFaceMessage? ClearFace(JsonNode root)
        {
            var m = Members(root, "type", "sessionId");
            var session = Guid(m, "sessionId");
            return Failure is null ? new ClearFaceMessage { SessionId = session! } : null;
        }

        private FailMessage? FailBody(JsonNode root)
        {
            var m = Members(root, "type", "sessionId", "failure");
            var session = Guid(m, "sessionId");
            var failure = FailureToken(m);
            return Failure is null ? new FailMessage { SessionId = session!, Failure = failure!.Value } : null;
        }

        private ResultMessage? Result(JsonNode root)
        {
            var m = Members(root, "type", "requestId", "outcome", "failure");
            var request = Guid(m, "requestId");
            Outcome? outcome = null;
            if (Str(m, "outcome") is { } token)
            {
                outcome = WireTokens.ParseOutcome(token);
                if (outcome is null)
                {
                    Fail(ReasonCode.InvokeOutcomeInvalid);
                }
            }

            Failure? failure = null;
            if (m.ContainsKey("failure"))
            {
                failure = FailureToken(m);
                if (Failure is null && outcome is not Extensions.Outcome.Failed)
                {
                    Fail(ReasonCode.InvokeFailureUnexpected);
                }
            }

            return Failure is null ? new ResultMessage { RequestId = request!, Outcome = outcome!.Value, Failure = failure } : null;
        }

        private SessionRefusedMessage? SessionRefused(JsonNode root)
        {
            var m = Members(root, "type", "sessionId", "code");
            var session = Guid(m, "sessionId");
            ReasonCode code = default;
            if (Str(m, "code") is { } text)
            {
                var match = RefusalCodes.FirstOrDefault(refusal => refusal.Value == text);
                if (match.Value.Length == 0)
                {
                    Fail(invalid);
                }

                code = match;
            }

            return Failure is null ? new SessionRefusedMessage { SessionId = session!, Code = code } : null;
        }

        private PingMessage? Ping(JsonNode root)
        {
            var m = Members(root, "type", "id");
            var id = Int(m, "id", 1, int.MaxValue);
            return Failure is null ? new PingMessage { Id = id } : null;
        }

        private PongMessage? Pong(JsonNode root)
        {
            var m = Members(root, "type", "id");
            var id = Int(m, "id", 1, int.MaxValue);
            return Failure is null ? new PongMessage { Id = id } : null;
        }

        private ErrorMessage? Error(JsonNode root, ConnectionPhase phase)
        {
            var m = Members(root, "type", "code", "message", "sessionId", "requestId", "retryAfterMs", "supported");
            ReasonCode code = default;
            if (Str(m, "code") is { } text && !ReasonCode.TryParse(text, out code))
            {
                Fail(invalid);
            }

            var message = Optional(m, "message") is not null ? Str(m, "message") : null;
            if (message is not null && !Grammars.IsPrintableAscii(message, 256))
            {
                Fail(invalid);
            }

            var session = Optional(m, "sessionId") is not null ? Guid(m, "sessionId") : null;
            var request = Optional(m, "requestId") is not null ? Guid(m, "requestId") : null;
            int? retry = Optional(m, "retryAfterMs") is not null ? Int(m, "retryAfterMs", 0, 300_000) : null;
            VersionRange? supported = null;
            if (Optional(m, "supported") is { } node)
            {
                if (node.Kind != JsonKind.Object)
                {
                    Fail(invalid);
                }
                else
                {
                    var sm = Members(node, "minVersion", "maxVersion");
                    var min = Int(sm, "minVersion", 1, 65_535);
                    var max = Int(sm, "maxVersion", 1, 65_535);
                    if (Failure is null && min > max)
                    {
                        Fail(invalid);
                    }

                    supported = new VersionRange { MinVersion = min, MaxVersion = max };
                }

                if (Failure is null && code != ReasonCode.ProtocolVersionUnsupported)
                {
                    Fail(invalid);
                }
            }

            if (Failure is null && phase == ConnectionPhase.Handshake && code.IsKnown && !code.Info.PreAuthentication)
            {
                Fail(ReasonCode.ProtocolUnexpected);
            }

            return Failure is null
                ? new ErrorMessage
                {
                    Code = code,
                    Message = message,
                    SessionId = session,
                    RequestId = request,
                    RetryAfterMs = retry,
                    Supported = supported,
                }
                : null;
        }

        private WireFace? Face(JsonNode node)
        {
            if (node.Kind != JsonKind.Object)
            {
                Fail(invalid);
                return null;
            }

            var m = Members(node, "picture", "line1", "line2", "state", "detail", "goodForSeconds");
            FacePicture picture = FacePicture.None;
            if (Optional(m, "picture") is { } pictureNode)
            {
                picture = Picture(pictureNode) ?? FacePicture.None;
            }

            var line1 = Optional(m, "line1") is { } first ? Line(first) : null;
            var line2 = Optional(m, "line2") is { } second ? Line(second) : null;
            var state = FaceState.None;
            if (Optional(m, "state") is not null && Str(m, "state") is { } token)
            {
                if (WireTokens.ParseFaceState(token) is { } parsed)
                {
                    state = parsed;
                }
                else
                {
                    Fail(ReasonCode.FaceStateInvalid);
                }
            }

            var detail = Optional(m, "detail") is not null ? Str(m, "detail") : null;
            var goodFor = 0;
            if (Required(m, "goodForSeconds") is { } lifetime)
            {
                if (!lifetime.TryGetInteger(out var seconds))
                {
                    Fail(invalid);
                }
                else if (seconds is < 1 or > 86_400)
                {
                    Fail(ReasonCode.FaceLifetimeInvalid);
                }
                else
                {
                    goodFor = (int)seconds;
                }
            }

            return Failure is null
                ? new WireFace { Picture = picture, Line1 = line1, Line2 = line2, State = state, Detail = detail, GoodForSeconds = goodFor }
                : null;
        }

        private FacePicture? Picture(JsonNode node)
        {
            if (node.Kind != JsonKind.Object)
            {
                Fail(invalid);
                return null;
            }

            var m = Members(node, "$type", "glyph");
            switch (Variant(m))
            {
                case "none":
                    return Failure is null ? FacePicture.None : null;
                case "glyph":
                {
                    var glyph = Str(m, "glyph");
                    if (glyph is not null && !TextRules.IsGlyph(glyph))
                    {
                        Fail(ReasonCode.FaceGlyphInvalid);
                    }

                    return Failure is null ? FacePicture.Glyph(glyph!) : null;
                }

                case null:
                    return null;
                default:
                    Fail(ReasonCode.ProtocolVariantUnknown);
                    return null;
            }
        }

        private TextLine? Line(JsonNode node)
        {
            if (node.Kind != JsonKind.Object)
            {
                Fail(invalid);
                return null;
            }

            var m = Members(node, "$type", "text");
            switch (Variant(m))
            {
                case "text":
                {
                    var text = Str(m, "text");
                    return Failure is null ? new TextLine { Text = text! } : null;
                }

                case null:
                    return null;
                default:
                    Fail(ReasonCode.ProtocolVariantUnknown);
                    return null;
            }
        }

        private string? Variant(Dictionary<string, JsonNode> m) => Failure is null ? Str(m, "$type") : null;

        private HostLimits? Limits(Dictionary<string, JsonNode> m)
        {
            if (Required(m, "limits") is not { } node)
            {
                return null;
            }

            if (node.Kind != JsonKind.Object)
            {
                Fail(invalid);
                return null;
            }

            var l = Members(node, "maxFrameBytes", "maxSessions", "maxPendingInvokes", "invokeTimeoutMs", "faceChangesPerSecond",
                "faceBurst", "messageRate", "messageBurst", "hardMessageRate", "hardMessageBurst", "pingIntervalMs", "pongTimeoutMs");
            var limits = new HostLimits
            {
                MaxFrameBytes = Int(l, "maxFrameBytes", int.MinValue, int.MaxValue),
                MaxSessions = Int(l, "maxSessions", int.MinValue, int.MaxValue),
                MaxPendingInvokes = Int(l, "maxPendingInvokes", int.MinValue, int.MaxValue),
                InvokeTimeoutMs = Int(l, "invokeTimeoutMs", int.MinValue, int.MaxValue),
                FaceChangesPerSecond = Int(l, "faceChangesPerSecond", int.MinValue, int.MaxValue),
                FaceBurst = Int(l, "faceBurst", int.MinValue, int.MaxValue),
                MessageRate = Int(l, "messageRate", int.MinValue, int.MaxValue),
                MessageBurst = Int(l, "messageBurst", int.MinValue, int.MaxValue),
                HardMessageRate = Int(l, "hardMessageRate", int.MinValue, int.MaxValue),
                HardMessageBurst = Int(l, "hardMessageBurst", int.MinValue, int.MaxValue),
                PingIntervalMs = Int(l, "pingIntervalMs", int.MinValue, int.MaxValue),
                PongTimeoutMs = Int(l, "pongTimeoutMs", int.MinValue, int.MaxValue),
            };
            if (Failure is null && !limits.IsWithinRanges())
            {
                Fail(invalid);
            }

            return Failure is null ? limits : null;
        }

        private ClientInfo? Client(Dictionary<string, JsonNode> m)
        {
            if (Required(m, "client") is not { } node)
            {
                return null;
            }

            if (node.Kind != JsonKind.Object)
            {
                Fail(invalid);
                return null;
            }

            var c = Members(node, "name", "version");
            var name = Str(c, "name");
            var version = Str(c, "version");
            if ((name is not null && !Grammars.IsClientName(name)) || (version is not null && !Grammars.IsPeerVersion(version)))
            {
                Fail(invalid);
            }

            return Failure is null ? new ClientInfo { Name = name!, Version = version! } : null;
        }

        private HostIdentity? Host(Dictionary<string, JsonNode> m)
        {
            if (Required(m, "host") is not { } node)
            {
                return null;
            }

            if (node.Kind != JsonKind.Object)
            {
                Fail(invalid);
                return null;
            }

            var h = Members(node, "id", "version");
            var id = Str(h, "id");
            var version = Str(h, "version");
            if ((id is not null && !TextRules.IsHostId(id)) || (version is not null && !Grammars.IsPeerVersion(version)))
            {
                Fail(invalid);
            }

            return Failure is null ? new HostIdentity { Id = id!, Version = version! } : null;
        }

        private ReadOnlyCollection<string>? CapabilityList(Dictionary<string, JsonNode> m)
        {
            if (Required(m, "capabilities") is not { } node)
            {
                return null;
            }

            if (node.Kind != JsonKind.Array || node.Items!.Count > 32)
            {
                Fail(invalid);
                return null;
            }

            var ids = new List<string>();
            foreach (var item in node.Items!)
            {
                if (item.Kind != JsonKind.String || !Grammars.IsDottedCode(item.Text) || ids.Contains(item.Text, StringComparer.Ordinal))
                {
                    Fail(invalid);
                    return null;
                }

                ids.Add(item.Text);
            }

            return ids.AsReadOnly();
        }

        private Extensions.Failure? FailureToken(Dictionary<string, JsonNode> m)
        {
            if (Str(m, "failure") is not { } token)
            {
                return null;
            }

            var failure = WireTokens.ParseFailure(token);
            if (failure is null)
            {
                Fail(ReasonCode.FaceFailureInvalid);
            }

            return failure;
        }

        /// <summary>Applies rule 2 of contract §7.11 to one object's members.</summary>
        private Dictionary<string, JsonNode> Members(JsonNode node, params string[] known)
        {
            var members = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            foreach (var member in node.Members!)
            {
                if (known.Contains(member.Name, StringComparer.Ordinal))
                {
                    members.TryAdd(member.Name, member.Value);
                }
                else if (!Grammars.IsMemberName(member.Name) || known.Contains(member.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Fail(ReasonCode.ProtocolMemberInvalid);
                }
            }

            return members;
        }

        private JsonNode? Required(Dictionary<string, JsonNode> m, string name)
        {
            if (m.TryGetValue(name, out var node))
            {
                return node;
            }

            Fail(invalid);
            return null;
        }

        private static JsonNode? Optional(Dictionary<string, JsonNode> m, string name) => m.GetValueOrDefault(name);

        private string? Str(Dictionary<string, JsonNode> m, string name)
        {
            if (Required(m, name) is not { } node)
            {
                return null;
            }

            if (node.Kind != JsonKind.String)
            {
                Fail(invalid);
                return null;
            }

            return node.Text;
        }

        private int Int(Dictionary<string, JsonNode> m, string name, int min, int max)
        {
            if (Required(m, name) is not { } node)
            {
                return 0;
            }

            if (!node.TryGetInteger(out var value) || value < min || value > max)
            {
                Fail(invalid);
                return 0;
            }

            return (int)value;
        }

        private string? Guid(Dictionary<string, JsonNode> m, string name)
        {
            var value = Str(m, name);
            if (value is not null && !Grammars.IsGuid(value))
            {
                Fail(invalid);
            }

            return value;
        }

        private string? Key(Dictionary<string, JsonNode> m, string name)
        {
            var value = Str(m, name);
            if (value is not null && !Grammars.IsKey32(value))
            {
                Fail(invalid);
            }

            return value;
        }

        private string? Hash(Dictionary<string, JsonNode> m, string name)
        {
            var value = Str(m, name);
            if (value is not null && !Grammars.IsSha256(value))
            {
                Fail(invalid);
            }

            return value;
        }

        private void Fail(ReasonCode code) => Failure ??= code;
    }
}
