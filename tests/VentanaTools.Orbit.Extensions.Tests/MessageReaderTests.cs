// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests;

public sealed class MessageReaderTests
{
    public static TheoryData<string> ValidNames() => Names("messages-valid");

    public static TheoryData<string> InvalidNames() => Names("messages-invalid");

    public static TheoryData<string> IgnoredNames() => Names("messages-ignored");

    [Theory]
    [MemberData(nameof(ValidNames))]
    public void EveryValidFrameReadsAndCanonicalFramesRoundTrip(string name)
    {
        var item = Case("messages-valid", name);
        var frame = Frame(item);
        var result = Read(item, frame);
        Assert.Null(result.Violation);
        Assert.Null(result.Ignored);
        var message = Assert.IsAssignableFrom<WireMessage>(result.Message);
        Assert.Equal(item.GetProperty("type").GetString(), message.Type);
        var written = MessageWriter.Write(message);
        if (item.GetProperty("canonical").GetBoolean())
        {
            Assert.Equal(Encoding.UTF8.GetString(frame), Encoding.UTF8.GetString(written));
        }

        Assert.All(written, b => Assert.InRange(b, (byte)0x20, (byte)0x7E));
        var again = MessageReader.Read(written, Sender(item), Phase(item));
        Assert.Equal(written, MessageWriter.Write(again.Message!));
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void EveryInvalidFrameIsRefusedWithItsCode(string name)
    {
        var item = Case("messages-invalid", name);
        var result = Read(item, Frame(item));
        Assert.Null(result.Message);
        Assert.Null(result.Ignored);
        Assert.Equal(item.GetProperty("expect").GetString(), result.Violation?.Value);
    }

    [Theory]
    [MemberData(nameof(IgnoredNames))]
    public void UnknownMembersTypesAndCodesAreAcceptedOrIgnored(string name)
    {
        var item = Case("messages-ignored", name);
        var result = Read(item, Frame(item));
        Assert.Null(result.Violation);
        if (item.GetProperty("expect").GetString() == "ignored")
        {
            Assert.Null(result.Message);
            Assert.Equal(item.GetProperty("code").GetString(), result.Ignored?.Value);
            return;
        }

        Assert.Equal(item.GetProperty("type").GetString(), result.Message?.Type);
        if (item.TryGetProperty("unknownCode", out var code))
        {
            var error = Assert.IsType<ErrorMessage>(result.Message);
            Assert.Equal(code.GetString(), error.Code.Value);
            Assert.False(error.Code.IsKnown);
        }
    }

    [Fact]
    public void TheContractSetFaceExampleIsCanonical()
    {
        var frame = """{"type":"setFace","sessionId":"0f0e0d0c0b0a09080706050403020100","face":{"picture":{"$type":"glyph","glyph":"\uE916"},"line1":{"$type":"text","text":"4:59"},"line2":{"$type":"text","text":"Focus"},"state":"Playing","detail":"Four minutes and fifty-nine seconds left.","goodForSeconds":5}}""";
        var result = MessageReader.Read(Encoding.ASCII.GetBytes(frame), Wire.Sender.Companion, ConnectionPhase.Authenticated);
        var message = Assert.IsType<SetFaceMessage>(result.Message);
        Assert.Equal(FacePicture.Glyph("\uE916"), message.Face.Picture);
        Assert.Equal(new TextLine { Text = "4:59" }, message.Face.Line1);
        Assert.Equal(FaceState.Playing, message.Face.State);
        Assert.Equal(5, message.Face.GoodForSeconds);
        Assert.Equal(frame, Encoding.ASCII.GetString(MessageWriter.Write(message)));
    }

    /// <summary>Unlike files, a frame with an unpaired surrogate anywhere is refused (contract §7.2).</summary>
    [Theory]
    [InlineData("{\"type\":\"ping\",\"id\":1,\"\\ud800\":1}")]
    [InlineData("{\"type\":\"ping\",\"id\":1,\"x\":1,\"x\":\"\\udc00\"}")]
    [InlineData("{\"type\":\"ping\",\"id\":1,\"x\":[\"a\\udbffb\"]}")]
    public void UnpairedSurrogatesAnywhereInAFrameAreRefused(string frame)
    {
        var result = MessageReader.Read(Encoding.ASCII.GetBytes(frame), Wire.Sender.Host, ConnectionPhase.Authenticated);
        Assert.Null(result.Message);
        Assert.Equal(ReasonCode.FrameJsonInvalid.Value, result.Violation?.Value);
    }

    [Fact]
    public void MessagesMirrorTheirFrames()
    {
        var item = Case("messages-valid", "ready");
        var ready = Assert.IsType<ReadyMessage>(Read(item, Frame(item)).Message);
        Assert.Equal(3, ready.Version);
        Assert.Equal(TestHosts.Id, ready.Host.Id);
        Assert.Equal("en-US", ready.UiLanguage);
        Assert.True(ready.Limits.IsWithinRanges());
        Assert.Equal(HostLimits.Protocol3Defaults.MessageBurst, ready.Limits.MessageBurst);
        var startItem = Case("messages-valid", "startSession");
        var start = Assert.IsType<StartSessionMessage>(Read(startItem, Frame(startItem)).Message);
        Assert.Equal("start", start.Settings["mode"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)start.Settings).Clear());
        var errorItem = Case("messages-valid", "error with every optional member but supported");
        var error = Assert.IsType<ErrorMessage>(Read(errorItem, Frame(errorItem)).Message);
        Assert.Equal(ReasonCode.RateThrottled, error.Code);
        Assert.Equal(300_000, error.RetryAfterMs);
        Assert.Equal(Disposition.Advisory, error.Code.Info.Disposition);
        var resultItem = Case("messages-valid", "result Failed with a failure");
        var result = Assert.IsType<ResultMessage>(Read(resultItem, Frame(resultItem)).Message);
        Assert.Equal((Outcome.Failed, Failure.Network), (result.Outcome, result.Failure));
    }

    [Fact]
    public void TheWriterRefusesUndefinedValuesAndForeignTypes()
    {
        Assert.Throws<ArgumentException>(() => MessageWriter.Write(new FailMessage { SessionId = "0f0e0d0c0b0a09080706050403020100", Failure = (Failure)6 }));
        Assert.Throws<ArgumentException>(() => MessageWriter.Write(new ResultMessage { RequestId = "a0a1a2a3a4a5a6a7a8a9aaabacadaeaf", Outcome = 0 }));
        Assert.Throws<ArgumentException>(() => MessageWriter.Write(new SessionRefusedMessage { SessionId = "0f0e0d0c0b0a09080706050403020100", Code = default }));
        Assert.Throws<ArgumentException>(() => MessageWriter.Write(new StopSessionMessage { SessionId = null! }));
        Assert.Throws<ArgumentNullException>(() => MessageWriter.Write(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => MessageReader.Read("{}"u8, 0, ConnectionPhase.Handshake));
        Assert.Throws<ArgumentOutOfRangeException>(() => MessageReader.Read("{}"u8, Wire.Sender.Host, 0));
    }

    public static TheoryData<string> SessionCases() => Names("sessions");

    [Theory]
    [MemberData(nameof(SessionCases))]
    public void SessionSettingsAreCheckedAgainstTheManifest(string name)
    {
        var item = Case("sessions", name);
        using var document = Fixtures.Json("wire/v3/sessions.json");
        var manifest = ManifestReader.Read(Fixtures.Bytes(document.RootElement.GetProperty("manifest").GetString()!), TestHosts.Options).Value!;
        var settings = item.GetProperty("settings").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var code = SessionSettings.Check(manifest, item.GetProperty("contributionId").GetString()!, settings);
        Assert.Equal(item.GetProperty("code").GetString(), code?.Value);
        var frame = MessageWriter.Write(new StartSessionMessage
        {
            SessionId = "0f0e0d0c0b0a09080706050403020100",
            ContributionId = item.GetProperty("contributionId").GetString()!,
            Settings = settings,
        });
        Assert.NotNull(MessageReader.Read(frame, Wire.Sender.Host, ConnectionPhase.Authenticated).Message);
    }

    public static TheoryData<string> HandshakeCases() => Names("handshake");

    [Theory]
    [MemberData(nameof(HandshakeCases))]
    public void ScriptedHandshakeFramesReadAsDeclaredAndTheirProofsCheck(string name)
    {
        var item = Case("handshake", name);
        using var document = Fixtures.Json("wire/v3/handshake.json");
        var secret = Convert.FromBase64String(document.RootElement.GetProperty("secret").GetString()!);
        HelloMessage? hello = null;
        foreach (var step in item.GetProperty("steps").EnumerateArray())
        {
            var frame = Encoding.UTF8.GetBytes(step.GetProperty("frame").GetString()!);
            var result = MessageReader.Read(frame, Enum.Parse<Wire.Sender>(step.GetProperty("from").GetString()!),
                Enum.Parse<ConnectionPhase>(step.GetProperty("phase").GetString()!));
            var read = step.GetProperty("read");
            if (read.TryGetProperty("violation", out var violation))
            {
                Assert.Equal(violation.GetString(), result.Violation?.Value);
                continue;
            }

            Assert.Equal(read.GetProperty("message").GetString(), result.Message?.Type);
            hello = result.Message as HelloMessage ?? hello;
            if (result.Message is ChallengeMessage challenge)
            {
                var transcript = new HandshakeTranscript
                {
                    HostId = challenge.Host.Id,
                    HostVersion = challenge.Host.Version,
                    RegistrationId = hello!.RegistrationId,
                    ClientNonce = hello.ClientNonce,
                    ServerNonce = challenge.ServerNonce,
                    Version = challenge.Version,
                    MinVersion = hello.MinVersion,
                    MaxVersion = hello.MaxVersion,
                    ClientCapabilities = hello.Capabilities.ToList(),
                    HostCapabilities = challenge.Capabilities.ToList(),
                    ManifestHash = hello.ManifestHash,
                };
                Assert.Equal(item.GetProperty("proofs").GetProperty("challengeValid").GetBoolean(),
                    Handshake.VerifyProof(secret, challenge.Proof, transcript, ProofRole.Server));
            }

            if (result.Message is AuthenticateMessage authenticate && hello is not null)
            {
                Assert.True(Grammars.IsKey32(authenticate.Proof));
            }
        }

        foreach (var side in new[] { "verified", "unverified" })
        {
            if (item.GetProperty("sdk").ValueKind == JsonValueKind.Object)
            {
                var outcome = item.GetProperty("sdk").GetProperty(side);
                Assert.Contains(outcome.GetProperty("state").GetString(), new[] { "Connected", "Waiting", "Stopped" });
                if (outcome.GetProperty("reason").ValueKind == JsonValueKind.String)
                {
                    Assert.True(ReasonCode.TryParse(outcome.GetProperty("reason").GetString(), out var reason) && reason.IsKnown);
                }
            }
        }
    }

    public static TheoryData<string> HashCases() => Names("manifest-hash");

    [Theory]
    [MemberData(nameof(HashCases))]
    public void ManifestHashesCoverOnlyTheContractProjection(string name)
    {
        var item = Case("manifest-hash", name);
        var bytes = Encoding.UTF8.GetBytes(item.GetProperty("manifest").GetRawText());
        var result = ManifestReader.Read(bytes, TestHosts.Options);
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        Assert.Equal(item.GetProperty("canonical").GetString(), ManifestWriter.CanonicalProjection(result.Value));
        Assert.Equal(item.GetProperty("hash").GetString(), ManifestWriter.ComputeHash(result.Value));
    }

    [Fact]
    public void TenThousandMutationsNeverThrow()
    {
        var random = new Random(20261005);
        var corpus = new List<(byte[] Frame, Wire.Sender Sender, ConnectionPhase Phase)>();
        using (var document = Fixtures.Json("wire/v3/messages-valid.json"))
        {
            foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                corpus.Add((Frame(item), Sender(item), Phase(item)));
            }
        }

        string[] names = ["type", "sessionId", "id", "face", "$type", "glyph", "settings", "code", "limits", "Type", "sessionid", "x", "a_b", "$z"];
        string[] values = ["null", "0", "-1", "1.5", "1e9", "\"\"", "[]", "{}", "true", "\"\\ud800\"", "\"" + new string('a', 300) + "\"", "[[[[[[[[[]]]]]]]]]"];
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 10_000; i++)
        {
            var (frame, sender, phase) = corpus[random.Next(corpus.Count)];
            var mutated = (byte[])frame.Clone();
            switch (random.Next(4))
            {
                case 0:
                    for (var n = random.Next(1, 4); n > 0; n--)
                    {
                        mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
                    }

                    break;
                case 1:
                    mutated = mutated.AsSpan(0, random.Next(mutated.Length)).ToArray();
                    break;
                case 2:
                {
                    var text = Encoding.UTF8.GetString(frame);
                    var at = text.IndexOf(',', StringComparison.Ordinal);
                    var insert = "\"" + names[random.Next(names.Length)] + "\":" + values[random.Next(values.Length)] + ",";
                    mutated = Encoding.UTF8.GetBytes(at < 0 ? text : text.Insert(at + 1, insert));
                    break;
                }

                default:
                {
                    var text = Encoding.UTF8.GetString(frame);
                    var colon = text.IndexOf(':', random.Next(text.Length));
                    if (colon > 0)
                    {
                        var end = text.IndexOfAny([',', '}'], colon);
                        text = text[..(colon + 1)] + values[random.Next(values.Length)] + (end < 0 ? string.Empty : text[end..]);
                    }

                    mutated = Encoding.UTF8.GetBytes(text);
                    break;
                }
            }

            var result = MessageReader.Read(mutated, sender, (ConnectionPhase)random.Next(1, 4));
            var kinds = (result.Message is null ? 0 : 1) + (result.Violation is null ? 0 : 1) + (result.Ignored is null ? 0 : 1);
            Assert.Equal(1, kinds);
            var key = result.Violation?.Value ?? result.Ignored?.Value ?? "message";
            outcomes[key] = outcomes.GetValueOrDefault(key) + 1;
            if (result.Violation is { } violation)
            {
                Assert.True(violation.IsKnown);
            }
        }

        Assert.True(outcomes.Count > 5);
    }

    private static MessageReadResult Read(JsonElement item, byte[] frame) => MessageReader.Read(frame, Sender(item), Phase(item));

    private static Wire.Sender Sender(JsonElement item) => Enum.Parse<Wire.Sender>(item.GetProperty("sender").GetString()!);

    private static ConnectionPhase Phase(JsonElement item) => Enum.Parse<ConnectionPhase>(item.GetProperty("phase").GetString()!);

    internal static byte[] Frame(JsonElement item)
    {
        if (item.TryGetProperty("frameBase64", out var encoded))
        {
            return Convert.FromBase64String(encoded.GetString()!);
        }

        if (item.TryGetProperty("frameRepeat", out var repeat))
        {
            var prefix = repeat.GetProperty("prefix").GetString()!;
            var suffix = repeat.GetProperty("suffix").GetString()!;
            var length = repeat.GetProperty("length").GetInt32();
            var fill = repeat.GetProperty("fill").GetString()![0];
            return Encoding.UTF8.GetBytes(prefix + new string(fill, length - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix)) + suffix);
        }

        return Encoding.UTF8.GetBytes(LooseJson.String(item.GetProperty("frame"))!);
    }

    private static JsonElement Case(string file, string name)
    {
        var document = Fixtures.Json("wire/v3/" + file + ".json");
        return document.RootElement.GetProperty("cases").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name).Clone();
    }

    private static TheoryData<string> Names(string file)
    {
        var data = new TheoryData<string>();
        using var document = Fixtures.Json("wire/v3/" + file + ".json");
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }
}
