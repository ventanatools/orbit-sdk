// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests;

public sealed class WireTests
{
    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();

    internal static HandshakeTranscript VectorTranscript(IReadOnlyCollection<string> client, IReadOnlyCollection<string> host, int maxVersion = 3) => new()
    {
        HostId = TestHosts.Id,
        HostVersion = "2026.10.1",
        RegistrationId = "00112233445566778899aabbccddeeff",
        ClientNonce = "QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUE=",
        ServerNonce = "QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=",
        Version = 3,
        MinVersion = 3,
        MaxVersion = maxVersion,
        ClientCapabilities = client,
        HostCapabilities = host,
        ManifestHash = "1f7c38cf0bc0408b9b62e5d90fdc796c479fe791e7b54fdb701c82cedccb2e8f",
    };

    [Fact]
    public void TheAppendixAProofsAreReproduced()
    {
        var plain = VectorTranscript([], []);
        Assert.Equal("eKdrI1jrc3Bicq7QMMaNRq3Ag1bURFuIqMPBb6r/Pfo=", Handshake.ComputeProof(Secret, plain, ProofRole.Server));
        Assert.Equal("/UEufRCAUwJZWK0QCrk6/tMoSGLasVQxQEMWxnLWE4w=", Handshake.ComputeProof(Secret, plain, ProofRole.Client));
        var echo = VectorTranscript([Capabilities.TestEcho], [Capabilities.TestEcho]);
        Assert.Equal("BCmR8OP6wx2emYIPUh7AxfXBZQpW8DLrexmsJI6BMWk=", Handshake.ComputeProof(Secret, echo, ProofRole.Server));
        Assert.Equal("oqgAzzTIYecUU29y9FrmbyUXpQ7iqESyQxE3b4OPvRc=", Handshake.ComputeProof(Secret, echo, ProofRole.Client));
        var tampered = VectorTranscript([], [], maxVersion: 4);
        Assert.Equal("yewRo8g86EGY3ltzMQV7gUKZ2pRG+YqzviCgMXOSiPw=", Handshake.ComputeProof(Secret, tampered, ProofRole.Server));
        Assert.False(Handshake.VerifyProof(Secret, "yewRo8g86EGY3ltzMQV7gUKZ2pRG+YqzviCgMXOSiPw=", plain, ProofRole.Server));
        Assert.Equal("a8c06b3027d3fc4a", PipeNames.UserHash("S-1-5-21-1111111111-2222222222-3333333333-1001"));
    }

    [Fact]
    public void TheTranscriptFixtureMatchesTheImplementation()
    {
        using var document = Fixtures.Json("wire/v3/transcript.json");
        var root = document.RootElement;
        var secret = Convert.FromBase64String(root.GetProperty("secret").GetString()!);
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var t = item.GetProperty("transcript");
            var transcript = new HandshakeTranscript
            {
                HostId = t.GetProperty("hostId").GetString()!,
                HostVersion = t.GetProperty("hostVersion").GetString()!,
                RegistrationId = t.GetProperty("registrationId").GetString()!,
                ClientNonce = t.GetProperty("clientNonce").GetString()!,
                ServerNonce = t.GetProperty("serverNonce").GetString()!,
                Version = t.GetProperty("version").GetInt32(),
                MinVersion = t.GetProperty("minVersion").GetInt32(),
                MaxVersion = t.GetProperty("maxVersion").GetInt32(),
                ClientCapabilities = t.GetProperty("clientCapabilities").EnumerateArray().Select(c => c.GetString()!).ToList(),
                HostCapabilities = t.GetProperty("hostCapabilities").EnumerateArray().Select(c => c.GetString()!).ToList(),
                ManifestHash = t.GetProperty("manifestHash").GetString()!,
            };
            Assert.Equal(item.GetProperty("serverTranscript").GetString(), Encoding.UTF8.GetString(transcript.ToBytes(ProofRole.Server)));
            Assert.Equal(item.GetProperty("serverProof").GetString(), Handshake.ComputeProof(secret, transcript, ProofRole.Server));
            if (item.GetProperty("clientProof").ValueKind == JsonValueKind.String)
            {
                Assert.Equal(item.GetProperty("clientProof").GetString(), Handshake.ComputeProof(secret, transcript, ProofRole.Client));
                Assert.True(Handshake.VerifyProof(secret, item.GetProperty("clientProof").GetString(), transcript, ProofRole.Client));
            }
        }

        var user = root.GetProperty("userHash");
        Assert.Equal(user.GetProperty("hash").GetString(), PipeNames.UserHash(user.GetProperty("sid").GetString()!));
        var pipe = root.GetProperty("pipeName");
        Assert.Equal(pipe.GetProperty("name").GetString(), PipeNames.Create(pipe.GetProperty("hostId").GetString()!,
            pipe.GetProperty("edition").GetString()!, user.GetProperty("hash").GetString()!, pipe.GetProperty("registrationId").GetString()!));
    }

    [Fact]
    public void TranscriptsSortCapabilitiesOrdinallyAndRefuseSeparators()
    {
        var transcript = VectorTranscript(["b.z", "a.y", "B.x"], []);
        var text = Encoding.UTF8.GetString(transcript.ToBytes(ProofRole.Client));
        Assert.Contains("\nB.x,a.y,b.z\n\n", text, StringComparison.Ordinal);
        Assert.StartsWith("Ventana.Extensions.v3\nclient\nexample-host\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text[^64..], StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => VectorTranscript(["a,b"], []).ToBytes(ProofRole.Server));
        Assert.Throws<ArgumentException>(() => (new HandshakeTranscript
        {
            HostId = "a\nb", HostVersion = "1", RegistrationId = "r", ClientNonce = "c", ServerNonce = "s", Version = 3, MinVersion = 3,
            MaxVersion = 3, ClientCapabilities = [], HostCapabilities = [], ManifestHash = "h",
        }).ToBytes(ProofRole.Server));
        Assert.Throws<ArgumentException>(() => transcript.ToBytes((ProofRole)0));
        Assert.Throws<ArgumentException>(() => Handshake.ComputeProof(new byte[31], transcript, ProofRole.Server));
        Assert.False(Handshake.VerifyProof(new byte[31], "x", transcript, ProofRole.Server));
    }

    [Fact]
    public void ProofsAndNoncesAreCanonical()
    {
        var nonce = Handshake.NewNonce();
        Assert.True(Grammars.IsKey32(nonce));
        Assert.NotEqual(nonce, Handshake.NewNonce());
        var valid = Convert.ToBase64String(new byte[32]);
        Assert.True(Grammars.IsKey32(valid));
        Assert.False(Grammars.IsKey32(valid.TrimEnd('=')));
        Assert.False(Grammars.IsKey32(Convert.ToBase64String(new byte[31])));
        Assert.False(Grammars.IsKey32("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB="));
        Assert.True(Grammars.IsKey32("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE="));
        Assert.False(Grammars.IsKey32(" AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));
        var transcript = VectorTranscript([], []);
        var proof = Handshake.ComputeProof(Secret, transcript, ProofRole.Client);
        Assert.True(Handshake.VerifyProof(Secret, proof, transcript, ProofRole.Client));
        Assert.False(Handshake.VerifyProof(Secret, proof, transcript, ProofRole.Server));
        Assert.False(Handshake.VerifyProof(Secret, proof.TrimEnd('='), transcript, ProofRole.Client));
        Assert.False(Handshake.VerifyProof(Secret, null, transcript, ProofRole.Client));
    }

    [Theory]
    [InlineData(3, 3, 3, 3, 3)]
    [InlineData(1, 5, 3, 4, 4)]
    [InlineData(3, 4, 3, 3, 3)]
    [InlineData(4, 5, 3, 3, null)]
    [InlineData(1, 2, 3, 3, null)]
    [InlineData(5, 4, 3, 5, null)]
    public void NegotiationPicksTheHighestCommonVersion(int clientMin, int clientMax, int hostMin, int hostMax, int? expected) =>
        Assert.Equal(expected, Handshake.Negotiate(clientMin, clientMax, hostMin, hostMax));

    [Fact]
    public void PipeNamesFollowTheGrammarAndNameNoProduct()
    {
        var name = PipeNames.Create(TestHosts.Id, "store", "a8c06b3027d3fc4a", "00112233445566778899aabbccddeeff");
        Assert.Equal("Ventana.Extensions.v3.example-host.store.a8c06b3027d3fc4a.00112233445566778899aabbccddeeff", name);
        Assert.True(PipeNames.TryParse(name, out var parts));
        Assert.Equal((TestHosts.Id, "store", "a8c06b3027d3fc4a", "00112233445566778899aabbccddeeff"),
            (parts.HostId, parts.Edition, parts.UserHash, parts.RegistrationId));
        foreach (var bad in new[]
        {
            null, "", "Ventana.Extensions.v3.example-host.store.a8c06b3027d3fc4a", "Ventana.Extensions.v2.example-host.store.a8c06b3027d3fc4a.00112233445566778899aabbccddeeff",
            "Ventana.Extensions.v3.Example.store.a8c06b3027d3fc4a.00112233445566778899aabbccddeeff",
            "Ventana.Extensions.v3.example-host.st_ore.a8c06b3027d3fc4a.00112233445566778899aabbccddeeff",
            "Ventana.Extensions.v3.example-host.store.A8C06B3027D3FC4A.00112233445566778899aabbccddeeff",
            "Ventana.Extensions.v3.example-host.store.a8c06b3027d3fc4a.00000000000000000000000000000000",
            "Ventana.Extensions.v3.example-host.store.a8c06b3027d3fc4a.00112233445566778899aabbccddeeff.x",
        })
        {
            Assert.False(PipeNames.TryParse(bad, out _));
        }

        Assert.Throws<ArgumentException>(() => PipeNames.Create("Example", "store", "a8c06b3027d3fc4a", "00112233445566778899aabbccddeeff"));
        Assert.Throws<ArgumentException>(() => PipeNames.Create(TestHosts.Id, new string('e', 33), "a8c06b3027d3fc4a", "00112233445566778899aabbccddeeff"));
        Assert.Throws<ArgumentException>(() => PipeNames.Create(TestHosts.Id, "store", "short", "00112233445566778899aabbccddeeff"));
        Assert.Throws<ArgumentException>(() => PipeNames.Create(TestHosts.Id, "store", "a8c06b3027d3fc4a", "nope"));
    }

    [Fact]
    public async Task FragmentedLittleEndianFramesRoundTripAndCleanEndOfStreamIsDistinct()
    {
        var content = Encoding.UTF8.GetBytes("""{"type":"ping","id":1}""");
        using var output = new MemoryStream();
        await Framing.WriteFrameAsync(output, content, CancellationToken.None);
        Assert.Equal(content.Length, BinaryPrimitives.ReadInt32LittleEndian(output.ToArray()));
        using var fragmented = new FragmentedStream(output.ToArray());
        Assert.Equal(content, await Framing.ReadFrameAsync(fragmented, Framing.MaxFrameBytes, TimeProvider.System, CancellationToken.None));
        Assert.Null(await Framing.ReadFrameAsync(fragmented, Framing.MaxFrameBytes, TimeProvider.System, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, Framing.MaxFrameBytes)]
    [InlineData(65537, Framing.MaxFrameBytes)]
    [InlineData(-1, Framing.MaxFrameBytes)]
    [InlineData(8193, Framing.MaxHandshakeFrameBytes)]
    public async Task InvalidDeclaredLengthsAreRefusedBeforeTheBodyIsAllocated(int length, int limit)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var input = new MemoryStream(header);
        var error = await Assert.ThrowsAsync<FrameException>(() => Framing.ReadFrameAsync(input, limit, TimeProvider.System, CancellationToken.None).AsTask());
        Assert.Equal(ReasonCode.FrameTooLarge, error.Code);
    }

    [Fact]
    public async Task TruncatedHeadersAndBodiesEndTheStream()
    {
        using var header = new MemoryStream([1, 0]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadFrameAsync(header, Framing.MaxFrameBytes, TimeProvider.System, CancellationToken.None).AsTask());
        using var body = new MemoryStream([2, 0, 0, 0, 65]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadFrameAsync(body, Framing.MaxFrameBytes, TimeProvider.System, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task AStartedFrameMustFinishWithinFiveSecondsOfTheClock()
    {
        var time = new ManualTime();
        using var stream = new StallingStream([3, 0]);
        var read = Framing.ReadFrameAsync(stream, Framing.MaxFrameBytes, time, CancellationToken.None).AsTask();
        await stream.Stalled.Task;
        time.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<FrameException>(() => read);
        Assert.Equal(ReasonCode.FrameTimeout, error.Code);
        using var cancel = new CancellationTokenSource();
        using var idle = new StallingStream([]);
        var waiting = Framing.ReadFrameAsync(idle, Framing.MaxFrameBytes, time, cancel.Token).AsTask();
        await idle.Stalled.Task;
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(waiting.IsCompleted);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65537)]
    public async Task InvalidOutgoingLengthsNeverWriteAHeader(int length)
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Framing.WriteFrameAsync(output, new byte[length], CancellationToken.None).AsTask());
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void CapabilitiesFixtureRegeneratesFromTheRegistry()
    {
        Fixtures.AssertGenerated("wire/v3/capabilities.json", Fixtures.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("about", "The capability registry of contract section 7.4, generated from the C# registry. Every baseline feature of protocol 3 is implied by the negotiated version; these ids name optional features. Receivers ignore ids they do not know, and an id is never reused. Ids that start with x. are experimental and work only in a host's developer mode.");
            writer.WriteString("experimentalPrefix", "x.");
            writer.WriteStartArray("capabilities");
            foreach (var (id, status, meaning) in CapabilityRegistry.All)
            {
                writer.WriteStartObject();
                writer.WriteString("id", id);
                writer.WriteString("status", status);
                writer.WriteString("meaning", meaning);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }));
        Assert.True(Capabilities.IsExperimental(Capabilities.TestEcho));
        Assert.True(Capabilities.IsExperimental("x.future.thing"));
        Assert.False(Capabilities.IsExperimental("x."));
        Assert.False(Capabilities.IsExperimental("face.image"));
        Assert.False(Capabilities.IsExperimental("X.test"));
        Assert.All(CapabilityRegistry.All, row => Assert.True(Grammars.IsDottedCode(row.Id)));
    }

    [Fact]
    public void EnumsFixtureRegeneratesFromTheTokenTables()
    {
        Fixtures.AssertGenerated("wire/v3/enums.json", Fixtures.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("about", "Every enumeration token and its C# value (contract section 7.13), generated from the explicit token tables. The wire carries tokens, never numbers. Non-flags C# enumerations start at 1. reserved lists tokens a capability or a later schema will add, with their reserved value when one is assigned.");
            Table(writer, "FaceState", WireTokens.FaceStates, []);
            Table(writer, "Failure", WireTokens.Failures, [("LlmUnavailable", 6)]);
            Table(writer, "Outcome", WireTokens.Outcomes, []);
            Table(writer, "NetworkUse", WireTokens.NetworkUses, []);
            Table(writer, "SettingKind", WireTokens.SettingKinds, [("Toggle", null), ("Text", null), ("Number", null)]);
            Table(writer, "Provides", WireTokens.ProvidesValues, []);
            Table(writer, "PairingMode", WireTokens.PairingModes, []);
            Table(writer, "DiagnosticSeverity", WireTokens.Severities, []);
            Table(writer, "HostStatus", WireTokens.HostStatuses, []);
            Table(writer, "Disposition", WireTokens.Dispositions, []);
            Table(writer, "ProofRole", WireTokens.ProofRoles, []);
            writer.WriteEndObject();
        }));
        Assert.Equal(5, Enum.GetValues<Failure>().Length);
        Assert.Equal(Enum.GetValues<FaceState>().Length, WireTokens.FaceStates.Count);
        Assert.Equal(Enum.GetValues<Outcome>().Length, WireTokens.Outcomes.Count);
        Assert.Equal(Enum.GetValues<NetworkUse>().Length, WireTokens.NetworkUses.Count);
        Assert.All(new[] { typeof(FaceState), typeof(Failure), typeof(Outcome), typeof(NetworkUse), typeof(SettingKind), typeof(PairingMode),
            typeof(DiagnosticSeverity), typeof(HostStatus), typeof(Disposition), typeof(IdOrigin), typeof(ProofRole), typeof(Sender), typeof(ConnectionPhase) },
            type => Assert.DoesNotContain(0, Enum.GetValues(type).Cast<object>().Select(Convert.ToInt32)));
    }

    public static TheoryData<string> TokenBucketCases()
    {
        var data = new TheoryData<string>();
        using var document = Fixtures.Json("wire/v3/token-bucket.json");
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TokenBucketCases))]
    public void TokenBucketsTakeAndRefuseAsTheVectorsSay(string name)
    {
        using var document = Fixtures.Json("wire/v3/token-bucket.json");
        var item = document.RootElement.GetProperty("cases").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        var time = new ManualTime();
        var bucket = new TokenBucket(item.GetProperty("rate").GetDouble(), item.GetProperty("burst").GetInt32(), time);
        var now = 0L;
        foreach (var step in item.GetProperty("steps").EnumerateArray())
        {
            var at = step.GetProperty("atMs").GetInt64();
            time.Advance(TimeSpan.FromMilliseconds(at - now));
            now = at;
            var cost = TokenBucket.CostOf(step.GetProperty("bytes").GetInt32());
            Assert.Equal(step.GetProperty("cost").GetInt32(), cost);
            Assert.Equal(step.GetProperty("take").GetBoolean(), bucket.TryTake(cost));
            Assert.Equal(step.GetProperty("retryAfterMs").GetInt64(), (long)bucket.RetryAfter.TotalMilliseconds);
        }
    }

    [Fact]
    public void FrameCostsArePerStartedKibibyte()
    {
        Assert.Equal(1, TokenBucket.CostOf(1));
        Assert.Equal(1, TokenBucket.CostOf(1024));
        Assert.Equal(2, TokenBucket.CostOf(1025));
        Assert.Equal(64, TokenBucket.CostOf(65_536));
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenBucket.CostOf(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(0, 1, TimeProvider.System));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(1, 0, TimeProvider.System));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(1, 1, TimeProvider.System).TryTake(0));
    }

    [Fact]
    public void LimitsHaveRangesCrossRulesAndCompanionValues()
    {
        var defaults = HostLimits.Protocol3Defaults;
        Assert.True(defaults.IsWithinRanges());
        var companion = defaults.ForCompanion();
        Assert.True(companion.IsWithinRanges());
        Assert.Equal(15_000, companion.InvokeTimeoutMs);
        var host = new HostLimits
        {
            MaxFrameBytes = 8_192, MaxSessions = 4, MaxPendingInvokes = 2, InvokeTimeoutMs = 60_000, FaceChangesPerSecond = 1, FaceBurst = 1,
            MessageRate = 8, MessageBurst = 8, HardMessageRate = 8, HardMessageBurst = 8, PingIntervalMs = 5_000, PongTimeoutMs = 4_999,
        };
        Assert.True(host.IsWithinRanges());
        var used = host.ForCompanion();
        Assert.Equal((8_192, 4, 15_000, 30_000, 10_000), (used.MaxFrameBytes, used.MaxSessions, used.InvokeTimeoutMs, used.PingIntervalMs, used.PongTimeoutMs));
        Assert.False(With(host, messageBurst: 7).IsWithinRanges());
        Assert.False(With(host, pingIntervalMs: 4_999).IsWithinRanges());
        Assert.False(With(host, hardMessageRate: 7).IsWithinRanges());
    }

    private static HostLimits With(HostLimits limits, int? messageBurst = null, int? pingIntervalMs = null, int? hardMessageRate = null) => new()
    {
        MaxFrameBytes = limits.MaxFrameBytes,
        MaxSessions = limits.MaxSessions,
        MaxPendingInvokes = limits.MaxPendingInvokes,
        InvokeTimeoutMs = limits.InvokeTimeoutMs,
        FaceChangesPerSecond = limits.FaceChangesPerSecond,
        FaceBurst = limits.FaceBurst,
        MessageRate = limits.MessageRate,
        MessageBurst = messageBurst ?? limits.MessageBurst,
        HardMessageRate = hardMessageRate ?? limits.HardMessageRate,
        HardMessageBurst = limits.HardMessageBurst,
        PingIntervalMs = pingIntervalMs ?? limits.PingIntervalMs,
        PongTimeoutMs = limits.PongTimeoutMs,
    };

    private static void Table<T>(Utf8JsonWriter writer, string name, IReadOnlyList<(string Token, T Value)> table, (string Token, int? Value)[] reserved)
        where T : struct, Enum
    {
        writer.WriteStartObject(name);
        writer.WriteBoolean("flags", typeof(T).IsDefined(typeof(FlagsAttribute), false));
        writer.WriteStartArray("values");
        foreach (var (token, value) in table)
        {
            writer.WriteStartObject();
            writer.WriteString("token", token);
            writer.WriteNumber("value", Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("reserved");
        foreach (var (token, value) in reserved)
        {
            writer.WriteStartObject();
            writer.WriteString("token", token);
            if (value is { } number)
            {
                writer.WriteNumber("value", number);
            }
            else
            {
                writer.WriteNull("value");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    /// <summary>Returns its bytes, then waits until cancelled.</summary>
    private sealed class StallingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
            if (read > 0)
            {
                return read;
            }

            Stalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
