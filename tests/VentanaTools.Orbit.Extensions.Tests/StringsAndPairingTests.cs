// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests;

public sealed class StringsAndPairingTests
{
    private static readonly PairingReadOptions PairingOptions = new() { ExpectedExtensionId = "example.countdown", ManifestHosts = [TestHosts.Id] };

    public static TheoryData<string> StringsFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Fixtures.PathOf("strings"), "*.json", SearchOption.AllDirectories)
                     .Select(path => Path.GetRelativePath(Fixtures.PathOf("strings"), path).Replace(Path.DirectorySeparatorChar, '/'))
                     .Where(path => path.Contains('/', StringComparison.Ordinal) && !path.EndsWith(".expected.json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            data.Add(path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StringsFiles))]
    public void EveryStringsFixtureGivesItsExpectedDiagnostics(string file)
    {
        var manifest = ManifestReaderTests.Countdown();
        var tag = Path.GetFileNameWithoutExtension(file);
        var result = StringsReader.Read(Fixtures.Bytes("strings/" + file), tag,
            new StringsReadOptions { Manifest = manifest, KnownHosts = TestHosts.Known });
        if (file.StartsWith("valid/", StringComparison.Ordinal))
        {
            Assert.Empty(result.Diagnostics);
            Assert.True(result.Succeeded);
            Assert.Equal(tag, result.Value.Language, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Single(result.Diagnostics);
            Assert.Null(result.Value);
        }

        Expected.Match("strings/" + Path.ChangeExtension(file, null) + ".expected.json", result.Diagnostics);
    }

    [Fact]
    public void TheGermanStringsMirrorTheirFile()
    {
        var result = StringsReader.Read(Fixtures.Bytes("strings/valid/de-DE.json"), "de-DE",
            new StringsReadOptions { Manifest = ManifestReaderTests.Countdown() });
        var strings = result.Value!;
        Assert.Equal("Countdown-Beispiel", strings.Name);
        Assert.Equal("Beispiel GmbH", strings.Publisher!.Name);
        var timer = strings.Contributions["example.countdown/timer"];
        Assert.Equal("Beim Auswählen", timer.Settings["mode"].Name);
        Assert.Equal("Anhalten", timer.Settings["mode"].Choices["pause"]);
        Assert.Null(timer.Description);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, ContributionStrings>)strings.Contributions).Clear());
    }

    [Fact]
    public void UnpairedSurrogatesInStringsAreTextFaultsAndCollectAllContinues()
    {
        var text = "{\"schemaVersion\": 3, \"language\": \"de-DE\", \"name\": \"Count\\ud800down\", "
            + "\"description\": \"Ein \\udfff Beispiel\", \"extra\": 1, \"\\ud800\": 2}";
        var result = StringsReader.Read(Encoding.UTF8.GetBytes(text), "de-DE",
            new StringsReadOptions { Manifest = ManifestReaderTests.Countdown() });
        Assert.Null(result.Value);
        (string Code, string Path)[] expected =
        [
            (DiagnosticCodes.JsonUnknownMember, "/extra"),
            (DiagnosticCodes.JsonUnknownMember, string.Empty),
            (DiagnosticCodes.TextInvalidCharacter, "/name"),
            (DiagnosticCodes.TextInvalidCharacter, "/description"),
        ];
        Assert.Equal(expected.Order(), Expected.Pairs(result.Diagnostics).Order());
    }

    [Fact]
    public async Task StringsFilesTakeTheirTagFromTheFileName()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ventana-s2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "de-DE.json");
            await File.WriteAllBytesAsync(path, Fixtures.Bytes("strings/valid/de-DE.json"));
            var options = new StringsReadOptions { Manifest = ManifestReaderTests.Countdown() };
            Assert.True((await StringsReader.ReadFileAsync(path, options)).Succeeded);
            var renamed = Path.Combine(folder, "fr-FR.json");
            File.Move(path, renamed);
            Assert.Equal(DiagnosticCodes.StringsLanguageMismatch, Assert.Single((await StringsReader.ReadFileAsync(renamed, options)).Diagnostics).Code);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    public static TheoryData<string> PairingFiles()
    {
        var data = new TheoryData<string>();
        foreach (var name in Directory.EnumerateFiles(Fixtures.PathOf("pairing"), "*.json")
                     .Select(Path.GetFileName)
                     .Where(name => !name!.EndsWith(".expected.json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            data.Add(name![..^5]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PairingFiles))]
    public void EveryPairingFixtureGivesItsExpectedDiagnostics(string name)
    {
        var result = PairingReader.Read(Fixtures.Bytes("pairing/" + name + ".json"), PairingOptions);
        using var pairing = result.Value;
        if (name.StartsWith("valid-", StringComparison.Ordinal))
        {
            Assert.Empty(result.Diagnostics);
            Assert.NotNull(pairing);
            Assert.Equal(TestHosts.Id, pairing.HostId);
            Assert.Equal("00112233445566778899aabbccddeeff", pairing.RegistrationId);
            Assert.Equal("example.countdown", pairing.ExtensionId);
            Assert.Equal(3, pairing.PairingVersion);
            Assert.Equal(PairingMode.Persistent, pairing.Mode);
            Assert.True(PipeNames.TryParse(pairing.PipeName, out var parts));
            Assert.Equal("store", parts.Edition);
        }
        else
        {
            Assert.NotEmpty(result.Diagnostics);
            Assert.Null(pairing);
            var code = result.Diagnostics[0].Code;
            Assert.True(code.StartsWith("pairing.", StringComparison.Ordinal) || code.StartsWith("json.", StringComparison.Ordinal));
        }

        Expected.Match("pairing/" + name + ".expected.json", result.Diagnostics);
    }

    [Fact]
    public void ProofsMatchTheTranscriptVectorAndNeverExposeTheSecret()
    {
        var result = PairingReader.Read(Fixtures.Bytes("pairing/valid-lf.json"), PairingOptions);
        using var pairing = result.Value!;
        var transcript = WireTests.VectorTranscript([], []);
        Assert.Equal("eKdrI1jrc3Bicq7QMMaNRq3Ag1bURFuIqMPBb6r/Pfo=", pairing.ComputeProof(transcript, ProofRole.Server));
        Assert.Equal("/UEufRCAUwJZWK0QCrk6/tMoSGLasVQxQEMWxnLWE4w=", pairing.ComputeProof(transcript, ProofRole.Client));
        Assert.True(pairing.VerifyProof("eKdrI1jrc3Bicq7QMMaNRq3Ag1bURFuIqMPBb6r/Pfo=", transcript, ProofRole.Server));
        Assert.False(pairing.VerifyProof("eKdrI1jrc3Bicq7QMMaNRq3Ag1bURFuIqMPBb6r/Pfo=", transcript, ProofRole.Client));
        Assert.False(pairing.VerifyProof(null, transcript, ProofRole.Server));
        Assert.False(pairing.VerifyProof("not base64", transcript, ProofRole.Server));
        Assert.DoesNotContain("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", pairing.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("Pairing", pairing.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADisposedPairingCannotProve()
    {
        var pairing = PairingReader.Read(Fixtures.Bytes("pairing/valid-lf.json"), PairingOptions).Value!;
        var transcript = WireTests.VectorTranscript([], []);
        pairing.Dispose();
        pairing.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pairing.ComputeProof(transcript, ProofRole.Client));
        Assert.Throws<ObjectDisposedException>(() => pairing.VerifyProof(null, transcript, ProofRole.Client));
    }

    [Theory]
    [InlineData("\"pairingVersion\": 3", "\"pairingVersion\": 1", "pairing.version-unsupported")]
    [InlineData("\"pairingVersion\": 3", "\"pairingVersion\": 4", "pairing.version-unsupported")]
    [InlineData("\"pairingVersion\": 3", "\"pairingVersion\": 3, \"pairingVersion\": 3", DiagnosticCodes.JsonDuplicateMember)]
    [InlineData("\"pairingVersion\": 3", "\"pairingVersion\": 3, \"entrypoint\": \"run.exe\"", DiagnosticCodes.JsonUnknownMember)]
    [InlineData("Ventana.Extensions.v3.example-host", "Ventana.Extensions.v2.example-host", "pairing.pipe-name-invalid")]
    [InlineData("Ventana.Extensions.v3.example-host", "Other.Extensions.v3.example-host", "pairing.pipe-name-invalid")]
    [InlineData(".store.", ".STORE.", "pairing.pipe-name-invalid")]
    [InlineData(".store.", ".store.extra.", "pairing.pipe-name-invalid")]
    [InlineData("\"extensionId\": \"example.countdown\"", "\"extensionId\": \"other.sdk\"", "pairing.extension-mismatch")]
    [InlineData("\"registrationId\": \"00112233445566778899aabbccddeeff\"", "\"registrationId\": \"00112233445566778899AABBCCDDEEFF\"", "pairing.registration-invalid")]
    [InlineData(".a8c06b3027d3fc4a.", ".a8c06b3027d3fc4g.", "pairing.pipe-name-invalid")]
    public void InvalidIdentityVersionsPipesAndAmbiguousMembersGiveFixedCodes(string before, string after, string code)
    {
        var text = Fixtures.Utf8(Fixtures.Text("pairing/valid-lf.json"));
        Assert.Contains(before, text, StringComparison.Ordinal);
        var result = PairingReader.Read(Encoding.UTF8.GetBytes(text.Replace(before, after, StringComparison.Ordinal)), PairingOptions);
        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.DoesNotContain("AAECAw", diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("run.exe", diagnostic.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void ASecretMustHoldExactly32Bytes(int length)
    {
        var text = Fixtures.Utf8(Fixtures.Text("pairing/valid-lf.json"))
            .Replace("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", Convert.ToBase64String(new byte[length]), StringComparison.Ordinal);
        Assert.Equal("pairing.secret-invalid", Assert.Single(PairingReader.Read(Encoding.UTF8.GetBytes(text), PairingOptions).Diagnostics).Code);
    }

    [Fact]
    public void TheCallerCannotChangeTheExpectedIdentityAndInputIsBounded()
    {
        var bytes = Fixtures.Bytes("pairing/valid-lf.json");
        Assert.Equal("pairing.extension-mismatch", Assert.Single(PairingReader.Read(bytes, new PairingReadOptions { ExpectedExtensionId = "other.sdk" }).Diagnostics).Code);
        Assert.Equal("pairing.host-not-listed", Assert.Single(PairingReader.Read(bytes, new PairingReadOptions { ExpectedExtensionId = "example.countdown", ManifestHosts = ["other-host"] }).Diagnostics).Code);
        Assert.True(PairingReader.Read(bytes, new PairingReadOptions { ExpectedExtensionId = "example.countdown" }).Succeeded);
        Assert.Throws<ArgumentException>(() => PairingReader.Read(bytes, new PairingReadOptions { ExpectedExtensionId = "clock" }));
        Assert.Throws<ArgumentException>(() => PairingReader.Read(bytes, new PairingReadOptions { ExpectedExtensionId = "example.countdown/timer" }));
        Assert.Equal(DiagnosticCodes.JsonSyntax, Assert.Single(PairingReader.Read([], PairingOptions).Diagnostics).Code);
        Assert.Equal("pairing.too-large", Assert.Single(PairingReader.Read(new byte[PairingReader.MaxBytes + 1], PairingOptions).Diagnostics).Code);
    }

    [Fact]
    public async Task ReadFileAsyncIsBoundedAndTheDefaultPathIsPerUser()
    {
        var path = Path.Combine(Path.GetTempPath(), "ventana-s2-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllBytesAsync(path, Fixtures.Bytes("pairing/valid-bom.json"));
            using (var read = (await PairingReader.ReadFileAsync(path, PairingOptions)).Value)
            {
                Assert.NotNull(read);
            }

            await File.WriteAllBytesAsync(path, new byte[1024 * 1024]);
            Assert.Equal("pairing.too-large", Assert.Single((await PairingReader.ReadFileAsync(path, PairingOptions)).Diagnostics).Code);
        }
        finally
        {
            File.Delete(path);
        }

        await Assert.ThrowsAsync<FileNotFoundException>(() => PairingReader.ReadFileAsync(path, PairingOptions));
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ventana", "pairings", TestHosts.Id, "example.countdown.pairing.json");
        Assert.Equal(expected, PairingReader.DefaultPath(TestHosts.Id, "example.countdown"));
        Assert.Throws<ArgumentException>(() => PairingReader.DefaultPath("Example", "example.countdown"));
        Assert.Throws<ArgumentException>(() => PairingReader.DefaultPath(TestHosts.Id, "..\\escape"));
    }
}
