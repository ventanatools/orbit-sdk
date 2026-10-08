// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;
using System.Text;

namespace VentanaTools.Orbit.Extensions.Wire;

/// <summary>
/// Writes messages in canonical form: compact JSON, <c>type</c> first and the other members in the
/// order of contract §7.3 and §7.5, optional members left out when null, enumerations through
/// explicit token tables, and every character outside printable ASCII written as a
/// <c>\uXXXX</c> escape, so frames are ASCII.
/// </summary>
/// <remarks>
/// The writer checks shapes, not values: it refuses null required members and undefined
/// enumeration values, and leaves the rules of the message table to the caller (an SDK
/// validates before sending). The class is safe to call from any thread.
/// </remarks>
public static class MessageWriter
{
    /// <summary>Writes one message as a frame body.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentException">A required member is null or an enumeration value is undefined.</exception>
    public static byte[] Write(WireMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var json = new JsonText();
        json.Start();
        json.Member("type").String(message.Type);
        switch (message)
        {
            case HelloMessage hello:
                json.Member("minVersion").Number(hello.MinVersion);
                json.Member("maxVersion").Number(hello.MaxVersion);
                json.Member("registrationId").String(hello.RegistrationId);
                json.Member("clientNonce").String(hello.ClientNonce);
                json.Member("capabilities").Strings(hello.Capabilities);
                json.Member("client");
                var client = Require(hello.Client);
                json.Start().Member("name").String(client.Name).Member("version").String(client.Version).End();
                json.Member("manifestHash").String(hello.ManifestHash);
                break;
            case ChallengeMessage challenge:
                json.Member("serverNonce").String(challenge.ServerNonce);
                json.Member("version").Number(challenge.Version);
                json.Member("capabilities").Strings(challenge.Capabilities);
                json.Member("host");
                Host(json, challenge.Host);
                json.Member("proof").String(challenge.Proof);
                break;
            case AuthenticateMessage authenticate:
                json.Member("proof").String(authenticate.Proof);
                break;
            case ReadyMessage ready:
                json.Member("version").Number(ready.Version);
                json.Member("capabilities").Strings(ready.Capabilities);
                json.Member("host");
                Host(json, ready.Host);
                json.Member("uiLanguage").String(ready.UiLanguage);
                json.Member("limits");
                Limits(json, Require(ready.Limits));
                break;
            case StartSessionMessage start:
                json.Member("sessionId").String(start.SessionId);
                json.Member("contributionId").String(start.ContributionId);
                json.Member("settings").Start();
                foreach (var (key, value) in Require(start.Settings).OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    json.Member(key).String(value);
                }

                json.End();
                break;
            case StopSessionMessage stop:
                json.Member("sessionId").String(stop.SessionId);
                break;
            case InvokeMessage invoke:
                json.Member("requestId").String(invoke.RequestId);
                json.Member("sessionId").String(invoke.SessionId);
                break;
            case CancelMessage cancel:
                json.Member("requestId").String(cancel.RequestId);
                break;
            case SetFaceMessage setFace:
                json.Member("sessionId").String(setFace.SessionId);
                json.Member("face");
                Face(json, Require(setFace.Face));
                break;
            case ClearFaceMessage clearFace:
                json.Member("sessionId").String(clearFace.SessionId);
                break;
            case FailMessage fail:
                json.Member("sessionId").String(fail.SessionId);
                json.Member("failure").String(Token(WireTokens.FailureToken(fail.Failure)));
                break;
            case ResultMessage result:
                json.Member("requestId").String(result.RequestId);
                json.Member("outcome").String(Token(WireTokens.OutcomeToken(result.Outcome)));
                if (result.Failure is { } failure)
                {
                    json.Member("failure").String(Token(WireTokens.FailureToken(failure)));
                }

                break;
            case SessionRefusedMessage refused:
                json.Member("sessionId").String(refused.SessionId);
                json.Member("code").String(Code(refused.Code));
                break;
            case PingMessage ping:
                json.Member("id").Number(ping.Id);
                break;
            case PongMessage pong:
                json.Member("id").Number(pong.Id);
                break;
            case ErrorMessage error:
                json.Member("code").String(Code(error.Code));
                if (error.Message is not null)
                {
                    json.Member("message").String(error.Message);
                }

                if (error.SessionId is not null)
                {
                    json.Member("sessionId").String(error.SessionId);
                }

                if (error.RequestId is not null)
                {
                    json.Member("requestId").String(error.RequestId);
                }

                if (error.RetryAfterMs is { } retry)
                {
                    json.Member("retryAfterMs").Number(retry);
                }

                if (error.Supported is { } supported)
                {
                    json.Member("supported").Start()
                        .Member("minVersion").Number(supported.MinVersion)
                        .Member("maxVersion").Number(supported.MaxVersion)
                        .End();
                }

                break;
            default:
                throw new ArgumentException("The message type is not part of protocol 3.", nameof(message));
        }

        json.End();
        return json.ToBytes();
    }

    private static void Host(JsonText json, HostIdentity? host)
    {
        var value = Require(host);
        json.Start().Member("id").String(value.Id).Member("version").String(value.Version).End();
    }

    private static void Limits(JsonText json, HostLimits limits) =>
        json.Start()
            .Member("maxFrameBytes").Number(limits.MaxFrameBytes)
            .Member("maxSessions").Number(limits.MaxSessions)
            .Member("maxPendingInvokes").Number(limits.MaxPendingInvokes)
            .Member("invokeTimeoutMs").Number(limits.InvokeTimeoutMs)
            .Member("faceChangesPerSecond").Number(limits.FaceChangesPerSecond)
            .Member("faceBurst").Number(limits.FaceBurst)
            .Member("messageRate").Number(limits.MessageRate)
            .Member("messageBurst").Number(limits.MessageBurst)
            .Member("hardMessageRate").Number(limits.HardMessageRate)
            .Member("hardMessageBurst").Number(limits.HardMessageBurst)
            .Member("pingIntervalMs").Number(limits.PingIntervalMs)
            .Member("pongTimeoutMs").Number(limits.PongTimeoutMs)
            .End();

    private static void Face(JsonText json, WireFace face)
    {
        json.Start();
        switch (Require(face.Picture))
        {
            case NoPicture:
                json.Member("picture").Start().Member("$type").String("none").End();
                break;
            case GlyphPicture glyph:
                json.Member("picture").Start().Member("$type").String("glyph").Member("glyph").String(glyph.Glyph).End();
                break;
            default:
                throw new ArgumentException("The picture variant is not part of protocol 3.", nameof(face));
        }

        Line(json, "line1", face.Line1);
        Line(json, "line2", face.Line2);
        json.Member("state").String(Token(WireTokens.FaceStateToken(face.State)));
        if (face.Detail is not null)
        {
            json.Member("detail").String(face.Detail);
        }

        json.Member("goodForSeconds").Number(face.GoodForSeconds);
        json.End();
    }

    private static void Line(JsonText json, string name, FaceLine? line)
    {
        switch (line)
        {
            case null:
                return;
            case TextLine text:
                json.Member(name).Start().Member("$type").String("text").Member("text").String(text.Text).End();
                return;
            default:
                throw new ArgumentException("The line variant is not part of protocol 3.", nameof(line));
        }
    }

    private static string Code(ReasonCode code) =>
        code.Value.Length == 0 ? throw new ArgumentException("The reason code is the default value.", nameof(code)) : code.Value;

    private static string Token(string? token) =>
        token ?? throw new ArgumentException("An enumeration value is undefined.", nameof(token));

    private static T Require<T>(T? value)
        where T : class =>
        value ?? throw new ArgumentException("A required member is null.", nameof(value));

    /// <summary>A minimal canonical JSON writer.</summary>
    private sealed class JsonText
    {
        private readonly StringBuilder _text = new();
        private readonly Stack<bool> _first = new();

        public JsonText Start()
        {
            _text.Append('{');
            _first.Push(true);
            return this;
        }

        public JsonText End()
        {
            _text.Append('}');
            _first.Pop();
            return this;
        }

        public JsonText Member(string name)
        {
            if (!_first.Pop())
            {
                _text.Append(',');
            }

            _first.Push(false);
            Escape(name);
            _text.Append(':');
            return this;
        }

        public JsonText String(string? value)
        {
            Escape(Require(value));
            return this;
        }

        public JsonText Number(long value)
        {
            _text.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonText Strings(IReadOnlyList<string>? values)
        {
            _text.Append('[');
            var first = true;
            foreach (var value in Require(values))
            {
                if (!first)
                {
                    _text.Append(',');
                }

                first = false;
                Escape(Require(value));
            }

            _text.Append(']');
            return this;
        }

        public byte[] ToBytes() => Encoding.ASCII.GetBytes(_text.ToString());

        private void Escape(string value)
        {
            _text.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        _text.Append("\\\"");
                        break;
                    case '\\':
                        _text.Append("\\\\");
                        break;
                    case '\b':
                        _text.Append("\\b");
                        break;
                    case '\f':
                        _text.Append("\\f");
                        break;
                    case '\n':
                        _text.Append("\\n");
                        break;
                    case '\r':
                        _text.Append("\\r");
                        break;
                    case '\t':
                        _text.Append("\\t");
                        break;
                    default:
                        if (c is < ' ' or > '~')
                        {
                            _text.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _text.Append(c);
                        }

                        break;
                }
            }

            _text.Append('"');
        }
    }
}

/// <summary>
/// A token bucket (contract §B.3): <c>burst</c> tokens at first, refilled at <c>ratePerSecond</c>,
/// on a monotonic clock. A frame costs one token per started 1,024 bytes (contract §7.10).
/// </summary>
/// <remarks>Instances are safe to use from several threads.</remarks>
public sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly double _rate;
    private readonly int _burst;
    private readonly TimeProvider _time;
    private double _tokens;
    private long _last;
    private TimeSpan _retryAfter;

    /// <summary>Creates a full bucket.</summary>
    /// <param name="ratePerSecond">The refill rate, in tokens per second; greater than zero.</param>
    /// <param name="burst">The capacity, in tokens; at least 1.</param>
    /// <param name="time">The monotonic clock.</param>
    public TokenBucket(double ratePerSecond, int burst, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (!(ratePerSecond > 0) || double.IsInfinity(ratePerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(ratePerSecond));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        _rate = ratePerSecond;
        _burst = burst;
        _time = time;
        _tokens = burst;
        _last = time.GetTimestamp();
    }

    /// <summary>The wait the last refused <see cref="TryTake"/> computed; zero after an accepted one.</summary>
    public TimeSpan RetryAfter
    {
        get
        {
            lock (_gate)
            {
                return _retryAfter;
            }
        }
    }

    /// <summary>The cost of a frame: one token per started 1,024 bytes of its body.</summary>
    /// <param name="frameBytes">The body length, at least 1.</param>
    /// <returns>The cost in tokens.</returns>
    public static int CostOf(int frameBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameBytes, 1);
        return (int)(((long)frameBytes + 1_023) / 1_024);
    }

    /// <summary>Refills, then takes <paramref name="tokens"/> when the bucket holds them.</summary>
    /// <param name="tokens">The cost, at least 1.</param>
    /// <returns>Whether the tokens were taken; when not, <see cref="RetryAfter"/> says how long to wait.</returns>
    public bool TryTake(int tokens = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tokens, 1);
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var elapsed = (double)(now - _last) / _time.TimestampFrequency;
            _tokens = Math.Min(_burst, _tokens + (_rate * elapsed));
            _last = now;
            if (_tokens >= tokens)
            {
                _tokens -= tokens;
                _retryAfter = TimeSpan.Zero;
                return true;
            }

            _retryAfter = TimeSpan.FromMilliseconds(Math.Ceiling((tokens - _tokens) / _rate * 1000));
            return false;
        }
    }
}
