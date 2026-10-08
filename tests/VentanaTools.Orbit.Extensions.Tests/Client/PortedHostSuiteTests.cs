// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests.Client;

/// <summary>
/// SDK cases that existed only in the host's test suite before it moved to the submodule, ported
/// by intent to contract generation 3 (scratchpad list <c>orbit-unique-sdk-tests.md</c>, section 2).
/// </summary>
public sealed class PortedHostSuiteTests
{
    private const string PipeName = "Ventana.Extensions.v3.example-host.store.a8c06b3027d3fc4a.00112233445566778899aabbccddeeff";
    private const string SecretText = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private static readonly PairingReadOptions Expect = new() { ExpectedExtensionId = "example.countdown", ManifestHosts = [TestHosts.Id] };

    [Fact]
    public void APairingComputesTheFixedKnownAnswerProofs()
    {
        using var read = PairingReader.Read(Encoding.UTF8.GetBytes(PairingJson()), Expect).Value!;
        var transcript = WireTests.VectorTranscript([], []);
        Assert.Equal("eKdrI1jrc3Bicq7QMMaNRq3Ag1bURFuIqMPBb6r/Pfo=", read.ComputeProof(transcript, ProofRole.Server));
        Assert.Equal("/UEufRCAUwJZWK0QCrk6/tMoSGLasVQxQEMWxnLWE4w=", read.ComputeProof(transcript, ProofRole.Client));
        Assert.True(read.VerifyProof("/UEufRCAUwJZWK0QCrk6/tMoSGLasVQxQEMWxnLWE4w=", transcript, ProofRole.Client));
    }

    [Fact]
    public void SerializingAPairingNeverLeaksTheSecret()
    {
        using var pairing = PairingReader.Read(Encoding.UTF8.GetBytes(PairingJson()), Expect).Value!;
        var json = JsonSerializer.Serialize(pairing);
        Assert.DoesNotContain(SecretText, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretText, pairing.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> PairingEdgeCases() => new()
    {
        { PairingJson().Replace("\"pairingVersion\": 3", "\"pairingVersion\": \"3\"", StringComparison.Ordinal), "json.type-mismatch" },
        { PairingJson().Replace("\"mode\"", "\"\\u0070airingVersion\": 3,\n  \"mode\"", StringComparison.Ordinal), "json.duplicate-member" },
        { PairingJson(pipeName: "Ventana.Extensions.v3.example-host..a8c06b3027d3fc4a.00112233445566778899aabbccddeeff"), "pairing.pipe-name-invalid" },
        { PairingJson(pipeName: "Ventana.Extensions.v3.example-host.st/re.a8c06b3027d3fc4a.00112233445566778899aabbccddeeff"), "pairing.pipe-name-invalid" },
        { PairingJson(pipeName: "Ventana.Extensions.v3.example-host.store.a8c06b3027d3fc4A.00112233445566778899aabbccddeeff"), "pairing.pipe-name-invalid" },
        { PairingJson().Replace("\"secret\"", "\"credential\"", StringComparison.Ordinal), "json.unknown-member" },
        { PairingJson().Replace("\"secret\"", "\"secret\": \"" + SecretText + "\",\n  \"secret\"", StringComparison.Ordinal), "json.duplicate-member" },
        { PairingJson(secret: " " + SecretText), "pairing.secret-invalid" },
        { PairingJson(pipeName: "Ventana.Extensions.v3.example-host.store.a8c06b3027d3fc4a.ffeeddccbbaa99887766554433221100"), "pairing.pipe-name-invalid" },
    };

    [Theory]
    [MemberData(nameof(PairingEdgeCases))]
    public void PairingEdgeCasesAreRefused(string json, string code)
    {
        var read = PairingReader.Read(Encoding.UTF8.GetBytes(json), Expect);
        Assert.Null(read.Value);
        Assert.Contains(read.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.All(read.Diagnostics, diagnostic => Assert.DoesNotContain(SecretText, diagnostic.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void AStrayInvalidByteInAPairingIsRefused()
    {
        var bytes = Encoding.UTF8.GetBytes(PairingJson()).ToList();
        bytes.Insert(bytes.IndexOf((byte)'m'), 0xFF);
        var read = PairingReader.Read(bytes.ToArray(), Expect);
        Assert.Null(read.Value);
        Assert.NotEmpty(read.Diagnostics);
    }

    [Fact]
    public void TranscriptValuesThatCouldInjectALineAreRefused()
    {
        using var pairing = PairingReader.Read(Encoding.UTF8.GetBytes(PairingJson()), Expect).Value!;
        var good = WireTests.VectorTranscript([], []);
        HandshakeTranscript[] injected =
        [
            Copy(good, clientNonce: good.ClientNonce + "\n"),
            Copy(good, hostVersion: "1.0\nserver"),
            Copy(good, capabilities: ["x.test-echo\nother"]),
            Copy(good, capabilities: ["a,b"]),
        ];
        Assert.All(injected, transcript => Assert.Throws<ArgumentException>(() => pairing.ComputeProof(transcript, ProofRole.Client)));
        Assert.Throws<ArgumentException>(() => good.ToBytes((ProofRole)7));
    }

    [Fact]
    public void ReservedPublishersAreReaderPolicyEndToEnd()
    {
        var manifest = new ExtensionManifest
        {
            Id = "ventana.demo",
            Name = "Demo",
            Description = "A reserved publisher's manifest.",
            Version = "1.0.0",
            Hosts = [TestHosts.Id],
            Contributions = [TestManifests.Contribution("ventana.demo/run", Provides.Invoke)],
        };
        var bytes = ManifestWriter.Write(manifest);
        Assert.Contains(ManifestReader.Read(bytes, TestHosts.Options).Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.IdRootReserved);
        var open = new ManifestReadOptions { KnownHosts = TestHosts.Known, ReservedPublishers = [] };
        Assert.True(ManifestReader.Read(bytes, open).Succeeded);
        Assert.Null(ExtensionIds.Classify("ventana.demo/run", IdOrigin.ThirdParty, []));
        Assert.Equal(DiagnosticCodes.IdRootReserved, ExtensionIds.Classify("ventana.demo/run", IdOrigin.ThirdParty));
    }

    [Fact]
    public void TheCanonicalManifestFormIsPinnedAndReadManifestsAreReadOnly()
    {
        var manifest = new ExtensionManifest
        {
            Id = "example.pin",
            Name = "Pin",
            Description = "Pinned.",
            Version = "1.0.0",
            Hosts = [TestHosts.Id],
            Contributions = [TestManifests.Contribution("example.pin/run", Provides.Invoke)],
        };
        Assert.Equal(
            "{\"schemaVersion\":3,\"id\":\"example.pin\",\"name\":\"Pin\",\"description\":\"Pinned.\",\"version\":\"1.0.0\",\"hosts\":[\"example-host\"],"
            + "\"contributions\":[{\"id\":\"example.pin/run\",\"name\":\"Contribution\",\"description\":\"A test contribution.\",\"glyph\":\"\\uE916\","
            + "\"provides\":[\"invoke\"]}]}",
            Encoding.UTF8.GetString(ManifestWriter.Write(manifest, indented: false)));
        var read = ManifestReader.Read(ManifestWriter.Write(manifest), TestHosts.Options).Value!;
        Assert.Throws<NotSupportedException>(() => ((IList<string>)read.Hosts).Add("other"));
        Assert.Throws<NotSupportedException>(() => ((IList<Contribution>)read.Contributions).Clear());
    }

    public static TheoryData<string, string> ProgrammaticFaults() => new()
    {
        { "outside", DiagnosticCodes.IdOutsideNamespace },
        { "glyph", DiagnosticCodes.ChromeGlyphInvalid },
        { "description", DiagnosticCodes.StringTooLong },
    };

    [Theory]
    [MemberData(nameof(ProgrammaticFaults))]
    public void ProgrammaticDeclarationsAreValidatedLikeReadOnes(string fault, string code)
    {
        var contribution = fault switch
        {
            "outside" => TestManifests.Contribution("other.root/run", Provides.Invoke),
            "glyph" => new Contribution { Id = "example.code/run", Name = "Run", Description = "Runs.", Glyph = "A", Provides = Provides.Invoke },
            _ => new Contribution { Id = "example.code/run", Name = "Run", Description = new string('d', 513), Glyph = "\uE916", Provides = Provides.Invoke },
        };
        var manifest = new ExtensionManifest
        {
            Id = "example.code",
            Name = "Code",
            Description = "Built in code.",
            Version = "1.0.0",
            Hosts = [TestHosts.Id],
            Contributions = [contribution],
        };
        Assert.Contains(ManifestReader.Validate(manifest, TestHosts.Options).Diagnostics, diagnostic => diagnostic.Code == code);
        using var pairing = new Pairing(TestHosts.Id, PipeName, ClientHarness.RegistrationId, "example.code", (byte[])ClientHarness.Secret.Clone());
        Assert.Throws<ArgumentException>(() => new CompanionClient(pairing, manifest, new TestHandler()));
    }

    [Fact]
    public async Task SettingsResolveFromTheClientsCopyOfTheManifest()
    {
        var choices = new List<SettingChoice> { new() { Value = "on", Name = "On" }, new() { Value = "off", Name = "Off" } };
        var manifest = new ExtensionManifest
        {
            Id = "example.frozen",
            Name = "Frozen",
            Description = "Frozen settings.",
            Version = "1.0.0",
            Hosts = [TestHosts.Id],
            Contributions =
            [
                new Contribution
                {
                    Id = "example.frozen/toggle",
                    Name = "Toggle",
                    Description = "Toggles.",
                    Glyph = "\uE916",
                    Provides = Provides.Invoke,
                    Settings = [new Setting { Id = "mode", Name = "Mode", Default = "on", Choices = choices }],
                },
            ],
        };
        var handler = new TestHandler();
        await using var harness = new ClientHarness(handler, manifest: manifest);
        choices.Clear();
        harness.Start();
        var peer = await harness.ConnectAsync();
        await peer.SendAsync(new StartSessionMessage
        {
            SessionId = ScriptedPeer.NewId(),
            ContributionId = "example.frozen/toggle",
            Settings = new Dictionary<string, string> { ["mode"] = "off" },
        });
        await ClientHarness.WaitForAsync(() => !handler.Sessions.IsEmpty, "session");
        Assert.Equal("off", handler.Sessions.Single().Settings["mode"]);
    }

    [Fact]
    public void TheMessageReaderRefusesAnEscapedDuplicateTypeAndAnEmptyObject()
    {
        Assert.Equal(ReasonCode.FrameJsonDuplicate,
            MessageReader.Read("{\"type\":\"ping\",\"\\u0074ype\":\"pong\",\"id\":1}"u8, Sender.Host, ConnectionPhase.Authenticated).Violation);
        Assert.Equal(ReasonCode.ProtocolMessageInvalid, MessageReader.Read("{}"u8, Sender.Host, ConnectionPhase.Authenticated).Violation);
    }

    [Fact]
    public void BoundedJsonReadsRefuseInvalidUtf8OversizeAndDeepNesting()
    {
        Assert.Equal(ReasonCode.FrameUtf8Invalid, MessageReader.Read(new byte[] { 0x7B, 0xFF, 0x7D }, Sender.Host, ConnectionPhase.Authenticated).Violation);
        Assert.Equal(ReasonCode.FrameTooLarge, MessageReader.Read(new byte[Framing.MaxFrameBytes + 1], Sender.Host, ConnectionPhase.Authenticated).Violation);
        Assert.Equal(ReasonCode.FrameTooLarge, MessageReader.Read(new byte[Framing.MaxHandshakeFrameBytes + 1], Sender.Host, ConnectionPhase.Handshake).Violation);
        var deep = "{\"type\":\"ping\",\"id\":1,\"x\":" + new string('[', 8) + new string(']', 8) + "}";
        Assert.Equal(ReasonCode.FrameJsonInvalid, MessageReader.Read(Encoding.UTF8.GetBytes(deep), Sender.Host, ConnectionPhase.Authenticated).Violation);
        var shallow = "{\"type\":\"ping\",\"id\":1,\"x\":" + new string('[', 6) + new string(']', 6) + "}";
        Assert.IsType<PingMessage>(MessageReader.Read(Encoding.UTF8.GetBytes(shallow), Sender.Host, ConnectionPhase.Authenticated).Message);
        Assert.Contains(ManifestReader.Read(new byte[ManifestReader.MaxBytes + 1]).Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.JsonTooLarge);
    }

    [Fact]
    public void FaceTextCleaningKeepsWholeElementsAndGlyphsAreOnePrivateUseCharacter()
    {
        var input = " A\u202E\tB\uD800\uFDD0" + char.ConvertFromUtf32(0x1FFFE) + "e\u0301 ";
        Assert.Equal("A Be\u0301", TextRules.Clean(input, 40, 160));
        Assert.Equal("e\u0301", TextRules.Clean("e\u0301x", 1, 160));
        Assert.Null(TextRules.Clean("e\u0301x", 40, 1));
        Assert.Equal("\uFFFDb", TextRules.Clean("a" + new string('\u0301', 20) + "b", 40, 160));
        Assert.True(TextRules.IsGlyph("\uE916"));
        Assert.False(TextRules.IsGlyph("A"));
    }

    [Fact]
    public void AnInvokeOnlyManifestReadsAsInvokeOnlyAndAnOldSchemaReportsItsExactCode()
    {
        var json = Encoding.UTF8.GetString(Fixtures.Text("manifests/valid/minimal.json"));
        var minimal = ManifestReader.Read(Encoding.UTF8.GetBytes(json), TestHosts.Options).Value!;
        var invokeOnly = minimal.Contributions.FirstOrDefault(contribution => contribution.Provides == Provides.Invoke);
        if (invokeOnly is null)
        {
            var manifest = new ExtensionManifest
            {
                Id = "example.invoke",
                Name = "Invoke",
                Description = "Invoke only.",
                Version = "1.0.0",
                Hosts = [TestHosts.Id],
                Contributions = [TestManifests.Contribution("example.invoke/run", Provides.Invoke)],
            };
            invokeOnly = ManifestReader.Read(ManifestWriter.Write(manifest), TestHosts.Options).Value!.Contributions.Single();
        }

        Assert.Equal(Provides.Invoke, invokeOnly.Provides);
        Assert.Empty(invokeOnly.Settings);
        var old = json.Replace("\"schemaVersion\": 3", "\"schemaVersion\": 1", StringComparison.Ordinal);
        Assert.NotEqual(json, old);
        Assert.Equal([DiagnosticCodes.SchemaVersionUnsupported], ManifestReader.Read(Encoding.UTF8.GetBytes(old), TestHosts.Options).Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public void TheIdFixtureCoversExactlyTheSharedGrammarsOutcomesForBothOrigins()
    {
        using var document = Fixtures.Json("ids.json");
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToList();
        var codes = cases.Select(item => item.GetProperty("code").ValueKind == JsonValueKind.Null ? null : item.GetProperty("code").GetString())
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new string?[] { null, "id.grammar", "id.required", "id.root-dotted", "id.root-not-dotted", "id.root-reserved", "id.too-long" }, codes);
        foreach (var origin in new[] { "first-party", "third-party" })
        {
            var mine = cases.Where(item => item.GetProperty("origin").GetString() == origin).ToList();
            Assert.Contains(mine, item => item.GetProperty("code").ValueKind == JsonValueKind.Null);
            Assert.Contains(mine, item => item.GetProperty("code").ValueKind != JsonValueKind.Null);
        }
    }

    [Fact]
    public void ReservedPublishersReserveMultiPartRootsAndFirstPartyIdsCannotBeDotted()
    {
        foreach (var publisher in ExtensionIds.ReservedPublishers)
        {
            Assert.Equal(DiagnosticCodes.IdRootReserved, ExtensionIds.Classify(publisher + ".tools.clock/now", IdOrigin.ThirdParty));
            Assert.Equal(DiagnosticCodes.IdRootDotted, ExtensionIds.Classify(publisher + ".clock", IdOrigin.FirstParty));
        }
    }

    private static HandshakeTranscript Copy(HandshakeTranscript source, string? clientNonce = null, string? hostVersion = null,
        IReadOnlyCollection<string>? capabilities = null) => new()
    {
        HostId = source.HostId,
        HostVersion = hostVersion ?? source.HostVersion,
        RegistrationId = source.RegistrationId,
        ClientNonce = clientNonce ?? source.ClientNonce,
        ServerNonce = source.ServerNonce,
        Version = source.Version,
        MinVersion = source.MinVersion,
        MaxVersion = source.MaxVersion,
        ClientCapabilities = capabilities ?? source.ClientCapabilities,
        HostCapabilities = source.HostCapabilities,
        ManifestHash = source.ManifestHash,
    };

    private static string PairingJson(string pipeName = PipeName, string secret = SecretText) =>
        "{\n  \"pairingVersion\": 3,\n  \"mode\": \"Persistent\",\n  \"hostId\": \"example-host\",\n  \"pipeName\": \"" + pipeName
        + "\",\n  \"registrationId\": \"00112233445566778899aabbccddeeff\",\n  \"extensionId\": \"example.countdown\",\n  \"secret\": \"" + secret + "\"\n}\n";
}
